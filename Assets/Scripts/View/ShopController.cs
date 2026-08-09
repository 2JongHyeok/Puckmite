using System.Collections.Generic;
using Puckmite.Game;
using Puckmite.Sim;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Puckmite.View
{
    /// <summary>
    /// The shop scene (design doc 5): the persistent upgrade board with the merchant beside it. Buy cells
    /// from the merchant's screen, place them, then roll this visit's stones onto them; leaving settles
    /// whatever the stones are standing on into the campaign's stats and moves on to the next run. The
    /// board itself and the stats bought on it live in the campaign; the offers and stones are per visit.
    /// </summary>
    public sealed class ShopController : ArenaControllerBase
    {
        private const int ShopOfferSlots = 3;
        private static readonly Color EmptyShopCellColor = new Color(0.19f, 0.20f, 0.24f);
        private const float MerchantX = -17.5f;
        private const float MerchantRadius = 2.2f;
        // Standing on the backdrop's grass line, by the campfire (user mock 2026-08-09).
        private const float MerchantY = CharFeetY + MerchantRadius;

        // The upgrade board: no damage cells and no fixed layout — every cell starts blank and is coloured by
        // whatever the player has bought onto it (design doc 5.1). Cell quads are kept so UpdateShopCells can
        // recolour them as purchases land, plus a merchant on the left that opens the buying screen.
        private SpriteRenderer[] _shopCellViews;
        private SpriteRenderer _merchantView;
        private SpriteRenderer _pendingCellGhost;
        private bool _shopUsesArt; // cell-sheet frames wired: cells wear frames, flat quads otherwise

        // One merchant slot: an upgrade cell, or (rarely) a battle stone in the same slot (design doc 5.3).
        private enum OfferType
        {
            Cell,
            BattleStone,
        }

        private struct ShopOffer
        {
            public OfferType Type;
            public UpgradeKind Kind; // meaningful only while Type == Cell
        }

        private bool _merchantOpen;
        private bool _shopThrowing;          // the roll button was pressed: no more buying, stones may fly
        private int _shopStonesLeft;
        private int _shopStonesTotal;
        private readonly List<ShopOffer?> _shopOffers = new List<ShopOffer?>(); // null = sold until a reroll
        private int _rerollCount;            // rerolls taken this visit; the price climbs with it (design doc 5.3)
        private bool _hasPendingCell;        // a bought cell is riding the cursor, waiting to be placed
        private UpgradeKind _pendingCell;
        private int _pendingSlot = -1;
        private string _shopLog = "";

        // Replace warning (design doc 5.1): a different kind wipes the cell's levels, so the placement
        // waits here for OK/cancel instead of landing straight away.
        private bool _confirmReplaceOpen;
        private int _confirmCol;
        private int _confirmRow;

        // The view arrays are sized once per Build and never grow (see ArenaControllerBase), so the roster
        // is pre-sized for every stone this visit could possibly field: the granted ones plus as many as
        // the entry gold could buy. Gold cannot rise inside a shop, so this is a hard upper bound.
        private int _stoneCapacity;

        private static CampaignState Campaign => GameFlow.Campaign;

        // --- Scene wiring -------------------------------------------------------------------------

        protected override void Awake()
        {
            // Opens straight after a cleared run and cannot be skipped (design doc 5.1). The board and the
            // stats bought on it persist in the campaign; the offers and this visit's stones are fresh.
            if (_tuning != null)
            {
                _shopStonesTotal = Mathf.Max(1, _tuning.ShopStonesPerVisit);
                _shopStonesLeft = _shopStonesTotal;
                _stoneCapacity = _shopStonesTotal + Campaign.Gold / Mathf.Max(1, _tuning.ShopStonePrice);
                _shopLog = "";
                RerollOffers();
            }

            base.Awake();
        }

        protected override void BuildMode()
        {
            BuildShopBoard();
            BuildPuckViews();
            BuildGhost();
            BuildPreviewLine();
            BuildPreviewMarker();
            ResetShopHands();
            UpdatePuckTransforms();
        }

        // On the upgrade board there is only the player, throwing this visit's shop stones (design doc 5.4).
        // Sized to capacity, not to the granted count: stones bought mid-visit must slot into view arrays
        // that were allocated at Build, since rebuilding (a scene reload) would wipe the thrown stones.
        protected override List<Puck> InitialRoster()
        {
            List<Puck> roster = new List<Puck>();
            for (int i = 0; i < Mathf.Max(1, _stoneCapacity); i++)
            {
                roster.Add(new Puck(roster.Count, Vector2.zero, _tuning.PuckRadius, 1f, PuckOwner.Player) { Health = _tuning.StoneHealth });
            }

            return roster;
        }

        // The throw always comes off the bottom wall, so the cursor's x is what slides along it (design doc
        // 5.2; the entry edge differs from battle because the merchant stands on the left).
        protected override Vector2 EntryPoint(int actor, float along)
        {
            float inset = _tuning.PuckRadius * RingRadiusScale;
            float minX = _sim.BoardMin.x + inset;
            float maxX = _sim.BoardMax.x - inset;
            return new Vector2(Mathf.Clamp(along, minX, maxX), _sim.BoardMin.y + inset);
        }

        protected override float EntryAlong(Vector2 world)
        {
            return world.x;
        }

        // The shop is the player alone (design doc 5.4).
        protected override int DeclaredActorCount()
        {
            return 1;
        }

        protected override void EntryAxisBounds(out float min, out float max)
        {
            min = _sim.BoardMin.x;
            max = _sim.BoardMax.x;
        }

        protected override void Update()
        {
            base.Update();

            // The upgrade board has no turns, no enemies and no attacks — just throwing stones onto cells.
            HandleShopInput();
            DriveSimulation();
            UpdatePuckTransforms();
            UpdateShopCells();
            UpdateGhost();
        }

        // --- Shop flow ----------------------------------------------------------------------------

        // Leaving settles the board as it stands: whatever the stones are sitting on is what gets bought
        // into the player's stats for good (design doc 5.2/5.5). Then on to the next run.
        private void LeaveShop()
        {
            UpgradeTotals gained = Campaign.ShopBoard.SumUpgrades(_sim, OccupancyThreshold);
            Campaign.BonusAttack += gained.Attack * _tuning.GainAttack;
            Campaign.BonusShield += gained.Shield * _tuning.GainShield;
            Campaign.BonusRunHeal += gained.RunHeal * _tuning.GainRunHeal;
            Campaign.BonusMaxHealth += gained.MaxHealth * _tuning.GainMaxHealth;

            Campaign.AdvanceRun();
            GameFlow.LoadBattle();
        }

        // Fills every slot afresh, bought-out ones included (design doc 5.3). The draw is view-level
        // UnityEngine.Random — the sim's determinism is untouched.
        private void RerollOffers()
        {
            _shopOffers.Clear();
            for (int i = 0; i < ShopOfferSlots; i++)
            {
                if (Random.value < _tuning.BattleStoneChance)
                {
                    _shopOffers.Add(new ShopOffer { Type = OfferType.BattleStone });
                }
                else
                {
                    _shopOffers.Add(new ShopOffer { Type = OfferType.Cell, Kind = (UpgradeKind)Random.Range(0, 4) });
                }
            }
        }

        // What the next reroll costs: the price climbs with every reroll taken this visit (design doc 5.3).
        private int RerollPrice()
        {
            return _tuning.RerollBasePrice + _tuning.RerollPriceStep * _rerollCount;
        }

        private int PriceOf(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.Attack: return _tuning.PriceAttack;
                case UpgradeKind.Shield: return _tuning.PriceShield;
                case UpgradeKind.RunHeal: return _tuning.PriceRunHeal;
                default: return _tuning.PriceMaxHealth;
            }
        }

        private int GainOf(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.Attack: return _tuning.GainAttack;
                case UpgradeKind.Shield: return _tuning.GainShield;
                case UpgradeKind.RunHeal: return _tuning.GainRunHeal;
                default: return _tuning.GainMaxHealth;
            }
        }

        private static string UpgradeName(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.Attack: return "Attack";
                case UpgradeKind.Shield: return "Shield";
                case UpgradeKind.RunHeal: return "Run heal";
                default: return "Max health";
            }
        }

        private static Color UpgradeColor(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.Attack: return new Color(0.70f, 0.30f, 0.25f);
                case UpgradeKind.Shield: return new Color(0.25f, 0.45f, 0.70f);
                case UpgradeKind.RunHeal: return new Color(0.30f, 0.62f, 0.35f);
                default: return new Color(0.62f, 0.50f, 0.25f);
            }
        }

        // The sheet frame a bought cell wears — sparkles from level 2, the battle centre's "sparkles mean
        // stronger" rule. RunHeal/MaxHealth have no sheet art yet: null keeps the colour quad.
        private Sprite UpgradeSprite(UpgradeKind kind, int level)
        {
            switch (kind)
            {
                case UpgradeKind.Attack: return level >= 2 ? _cellAttackStrongSprite : _cellAttackSprite;
                case UpgradeKind.Shield: return level >= 2 ? _cellShieldStrongSprite : _cellShieldSprite;
                default: return null;
            }
        }

        // Picking an affordable cell closes the merchant and hands it to the cursor to place on the board.
        private void BuyCell(int slot)
        {
            if (!_shopOffers[slot].HasValue || _shopOffers[slot].Value.Type != OfferType.Cell)
            {
                return;
            }

            UpgradeKind kind = _shopOffers[slot].Value.Kind;
            if (Campaign.Gold < PriceOf(kind) || _shopThrowing)
            {
                return;
            }

            _pendingCell = kind;
            _hasPendingCell = true;
            _pendingSlot = slot;
            _merchantOpen = false;
        }

        // A battle stone lands in the campaign, not on this board: one more roster stone from the next run
        // until defeat (design doc 5.6). Nothing to place, so the merchant screen stays open.
        private void BuyBattleStone(int slot)
        {
            if (Campaign.Gold < _tuning.BattleStonePrice || _shopThrowing)
            {
                return;
            }

            Campaign.Gold -= _tuning.BattleStonePrice;
            Campaign.ExtraBattleStones++;
            _shopOffers[slot] = null; // that slot is spent until a reroll
            _shopLog = "Battle stone bought — one more stone from the next run.";
        }

        // An extra stone for this visit only (design doc 5.4): paid, straight into the hand, and up onto
        // the entry edge if the throwing phase is already on and no stone is waiting there.
        private void BuyStone()
        {
            // The capacity guard backs the affordability check: ShopStonePrice is live-tunable, so a price
            // lowered mid-visit could otherwise afford more stones than the view arrays were sized for.
            if (Campaign.Gold < _tuning.ShopStonePrice || _shopStonesTotal >= _stoneCapacity)
            {
                return;
            }

            Campaign.Gold -= _tuning.ShopStonePrice;
            int id = _shopStonesTotal; // roster ids run 0..capacity-1; this is the next unused one
            _shopStonesTotal++;
            _shopStonesLeft++;
            _handReady[0].Add(id);
            _shopLog = "Stone bought for this visit.";

            if (_shopThrowing && !_ghostActive)
            {
                SetupGhost(0);
            }
        }

        // Places the carried cell on the board cell under the cursor. Stacking the same kind raises that
        // cell; a different kind wipes its levels, which the player has to confirm first (design doc 5.1),
        // so that case parks the placement in the confirm dialog instead of landing it.
        private void PlacePendingCell(int col, int row)
        {
            if (Campaign.Gold < PriceOf(_pendingCell))
            {
                return;
            }

            if (Campaign.ShopBoard.Preview(col, row, _pendingCell) == ShopPlacement.Replaced)
            {
                _confirmReplaceOpen = true;
                _confirmCol = col;
                _confirmRow = row;
                return;
            }

            CommitPendingCell(col, row);
        }

        // The charge and the placement proper — reached directly for empty/same-kind cells, or through the
        // confirm dialog's OK for a replacement.
        private void CommitPendingCell(int col, int row)
        {
            int price = PriceOf(_pendingCell);
            if (Campaign.Gold < price)
            {
                return;
            }

            ShopPlacement result = Campaign.ShopBoard.Place(col, row, _pendingCell);
            Campaign.Gold -= price;
            if (_pendingSlot >= 0 && _pendingSlot < _shopOffers.Count)
            {
                _shopOffers[_pendingSlot] = null; // that slot is spent until a reroll
            }

            _hasPendingCell = false;
            _pendingSlot = -1;
            _shopLog = result == ShopPlacement.Upgraded
                ? $"{UpgradeName(_pendingCell)} cell upgraded to level {Campaign.ShopBoard.CellAt(col, row).Level}."
                : result == ShopPlacement.Replaced
                    ? $"Cell replaced with {UpgradeName(_pendingCell)} (previous levels lost)."
                    : $"{UpgradeName(_pendingCell)} cell placed.";
        }

        // Committing to the throwing phase: the merchant leaves, so no more cells can be bought
        // (design doc 5.2 — buy and place first, then throw).
        private void BeginShopThrowing()
        {
            _shopThrowing = true;
            _merchantOpen = false;
            _hasPendingCell = false;

            // Not a turn start: that is the battle sequence, damage-cell settlement and all, and the upgrade
            // board has no damage cells. Only the ghost needs to come up.
            ClearGhost();
            SetupGhost(0);
        }

        // --- Shop view ----------------------------------------------------------------------------

        private void BuildShopBoard()
        {
            Transform board = new GameObject("ShopBoard").transform;
            board.SetParent(transform, false);

            float full = BoardHalf * 2f;
            MakeQuad("Background", board, Vector2.zero, new Vector2(full, full), new Color(0.16f, 0.17f, 0.20f), 0);

            Vector2 boardMin = new Vector2(-BoardHalf, -BoardHalf);
            Vector2 boardMax = new Vector2(BoardHalf, BoardHalf);
            Vector2 cellSize = BoardCells.CellSize(boardMin, boardMax);

            _shopUsesArt = _cellEmptySprite != null && _cellAttackSprite != null && _cellAttackStrongSprite != null
                && _cellShieldSprite != null && _cellShieldStrongSprite != null;

            _shopCellViews = new SpriteRenderer[BoardCells.Size * BoardCells.Size];
            for (int row = 0; row < BoardCells.Size; row++)
            {
                for (int col = 0; col < BoardCells.Size; col++)
                {
                    Vector2 center = BoardCells.CellCenter(boardMin, boardMax, col, row);
                    // Art frames carry their own borders, so they sit edge to edge at full cell size;
                    // the flat quads keep their 0.96 inset as the visual grid.
                    _shopCellViews[col + row * BoardCells.Size] = _shopUsesArt
                        ? MakeCellSprite("ShopCell", board, center, cellSize, _cellEmptySprite, 1)
                        : MakeQuad("ShopCell", board, center, cellSize * 0.96f, EmptyShopCellColor, 1);
                }
            }

            if (!_shopUsesArt)
            {
                Color gridColor = new Color(1f, 1f, 1f, 0.13f);
                float[] gridLines = { -7.5f, -2.5f, 2.5f, 7.5f };
                foreach (float g in gridLines)
                {
                    MakeQuad("GridV", board, new Vector2(g, 0f), new Vector2(0.08f, full), gridColor, 2);
                    MakeQuad("GridH", board, new Vector2(0f, g), new Vector2(full, 0.08f), gridColor, 2);
                }
            }

            Color wallColor = new Color(0.85f, 0.86f, 0.92f);
            const float wallThickness = 0.4f;
            MakeQuad("WallTop", board, new Vector2(0f, BoardHalf), new Vector2(full + wallThickness, wallThickness), wallColor, 3);
            MakeQuad("WallBottom", board, new Vector2(0f, -BoardHalf), new Vector2(full + wallThickness, wallThickness), wallColor, 3);
            MakeQuad("WallLeft", board, new Vector2(-BoardHalf, 0f), new Vector2(wallThickness, full + wallThickness), wallColor, 3);
            MakeQuad("WallRight", board, new Vector2(BoardHalf, 0f), new Vector2(wallThickness, full + wallThickness), wallColor, 3);

            // The merchant stands by the campfire, left of the board; clicking opens the buying screen.
            GameObject merchantGo = new GameObject("Merchant");
            merchantGo.transform.SetParent(transform, false);
            float d = MerchantRadius * 2f;
            merchantGo.transform.localPosition = new Vector3(MerchantX, MerchantY, 0f);
            merchantGo.transform.localScale = new Vector3(d, d, 1f);
            _merchantView = merchantGo.AddComponent<SpriteRenderer>();
            _merchantView.sprite = ProceduralSprites.Circle();
            _merchantView.color = new Color(0.85f, 0.75f, 0.45f);
            _merchantView.sortingOrder = 10;

            // Ghost of the cell being carried from the shop, snapped to whichever board cell is hovered.
            GameObject pending = new GameObject("PendingCellGhost");
            pending.transform.SetParent(transform, false);
            _pendingCellGhost = pending.AddComponent<SpriteRenderer>();
            _pendingCellGhost.sprite = ProceduralSprites.Unit();
            _pendingCellGhost.sortingOrder = 4;
            _pendingCellGhost.enabled = false;

            MakeInfoBox("ShopBox", new Vector2(-19f, 9f), new Vector2(9f, 3.5f)).text = "상점";
        }

        // Every shop stone starts in hand and comes in off the bottom wall.
        private void ResetShopHands()
        {
            _handReady = new List<int>[_actorCount];
            _handPending = new List<int>[_actorCount];
            for (int actor = 0; actor < _actorCount; actor++)
            {
                _handReady[actor] = new List<int>();
                _handPending[actor] = new List<int>();
            }

            List<Puck> roster = InitialRoster();
            for (int i = 0; i < roster.Count && i < _shopStonesLeft; i++)
            {
                _handReady[0].Add(roster[i].Id);
            }

            _hasRolledThisTurn = false;
            _currentActor = 0;
            ClearGhost();
            if (_shopThrowing)
            {
                SetupGhost(0);
            }
        }

        // Dresses each board cell for what has been bought onto it — sheet frames when the art is wired
        // (sparkles from level 2), colour quads otherwise and for kinds without art — and parks the
        // carried-cell ghost on whichever cell the cursor is over.
        private void UpdateShopCells()
        {
            if (_shopCellViews == null)
            {
                return;
            }

            Vector2 boardMin = _sim.BoardMin;
            Vector2 boardMax = _sim.BoardMax;
            Vector2 cellSize = BoardCells.CellSize(boardMin, boardMax);
            for (int row = 0; row < BoardCells.Size; row++)
            {
                for (int col = 0; col < BoardCells.Size; col++)
                {
                    ShopCell cell = Campaign.ShopBoard.CellAt(col, row);
                    SpriteRenderer view = _shopCellViews[col + row * BoardCells.Size];
                    Vector2 center = BoardCells.CellCenter(boardMin, boardMax, col, row);
                    Sprite art = _shopUsesArt
                        ? (cell.IsEmpty ? _cellEmptySprite : UpgradeSprite(cell.Kind, cell.Level))
                        : null;
                    if (art != null)
                    {
                        FitCellSprite(view, center, cellSize, art);
                        view.color = Color.white;
                    }
                    else
                    {
                        // Flat board, or a RunHeal/MaxHealth cell (no sheet art yet): the colour language.
                        view.sprite = ProceduralSprites.Unit();
                        view.transform.localScale = new Vector3(cellSize.x * 0.96f, cellSize.y * 0.96f, 1f);
                        view.transform.localPosition = new Vector3(center.x, center.y, 0f);
                        view.color = cell.IsEmpty
                            ? EmptyShopCellColor
                            : Color.Lerp(UpgradeColor(cell.Kind), Color.white, Mathf.Min(0.12f * (cell.Level - 1), 0.5f));
                    }
                }
            }

            _pendingCellGhost.enabled = false;
            if (!_hasPendingCell || _merchantOpen || _confirmReplaceOpen)
            {
                return;
            }

            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            Vector2 screen = mouse.position.ReadValue();
            if (PointerOverShopGui(screen))
            {
                return;
            }

            if (ShopCellAt(ScreenToWorld(screen), out int hoverCol, out int hoverRow))
            {
                Vector2 center = BoardCells.CellCenter(boardMin, boardMax, hoverCol, hoverRow);
                Sprite ghostArt = _shopUsesArt ? UpgradeSprite(_pendingCell, 1) : null;
                if (ghostArt != null)
                {
                    FitCellSprite(_pendingCellGhost, center, cellSize, ghostArt);
                    _pendingCellGhost.color = new Color(1f, 1f, 1f, 0.55f);
                }
                else
                {
                    Color ghost = UpgradeColor(_pendingCell);
                    ghost.a = 0.55f;
                    _pendingCellGhost.sprite = ProceduralSprites.Unit();
                    _pendingCellGhost.transform.localPosition = new Vector3(center.x, center.y, 0f);
                    _pendingCellGhost.transform.localScale = new Vector3(cellSize.x * 0.96f, cellSize.y * 0.96f, 1f);
                    _pendingCellGhost.color = ghost;
                }

                _pendingCellGhost.enabled = true;
            }
        }

        // --- Shop input ---------------------------------------------------------------------------

        // Upgrade-board input: click the merchant to buy, place a carried cell, right-click to drop it, and
        // once the throwing phase starts, aim the ghost exactly as a battle stone is aimed.
        private void HandleShopInput()
        {
            _launchReady = false;

            Mouse mouse = Mouse.current;
            if (mouse == null || _merchantOpen || _confirmReplaceOpen)
            {
                return; // both screens are modal: the board must not take clicks through them
            }

            Vector2 screen = mouse.position.ReadValue();
            Vector2 world = ScreenToWorld(screen);

            // Only presses are withheld over the shop's own controls. Releases must always be seen, or a
            // shot let go over a panel would leave the aim armed and fire on the next click.
            bool overGui = PointerOverShopGui(screen);

            if (_hasPendingCell)
            {
                if (mouse.rightButton.wasPressedThisFrame)
                {
                    _hasPendingCell = false; // purchase dropped; the board screen stays put
                    _pendingSlot = -1;
                    return;
                }

                if (mouse.leftButton.wasPressedThisFrame && !overGui && ShopCellAt(world, out int col, out int row))
                {
                    PlacePendingCell(col, row);
                }

                return;
            }

            if (mouse.leftButton.wasPressedThisFrame && !overGui && !_shopThrowing
                && (world - new Vector2(MerchantX, MerchantY)).magnitude <= MerchantRadius)
            {
                _merchantOpen = true;
                return;
            }

            if (!_shopThrowing)
            {
                return;
            }

            UpdateGhostAim(world);

            if (mouse.leftButton.wasPressedThisFrame && !overGui && GhostVisible() && !_ghostBlocked
                && (world - _ghost.Position).magnitude <= GrabRadius())
            {
                _aiming = true;
                _aimingPuckId = _ghost.Id;
            }

            if (_aiming && mouse.rightButton.wasPressedThisFrame)
            {
                _aiming = false;
                _aimingPuckId = -1;
                HidePreview();
            }

            if (_aiming && TryGetAimedPosition(out Vector2 aimPosition))
            {
                Vector2 drag = PullbackDrag(aimPosition, world);
                _launchReady = drag.magnitude >= MinDrag && !_ghostBlocked;
                if (_launchReady)
                {
                    _currentPowerFraction = DragToPowerFraction(drag.magnitude);
                    ComputePreview(_aimingPuckId, drag.normalized * (_tuning.MaxPower * _currentPowerFraction));
                }
                else
                {
                    _currentPowerFraction = 0f;
                    HidePreview();
                }
            }

            if (mouse.leftButton.wasReleasedThisFrame && _aiming)
            {
                bool blocked = _ghostBlocked;
                bool hasPosition = TryGetAimedPosition(out Vector2 releasePosition);
                _aiming = false;
                HidePreview();

                if (hasPosition && !blocked)
                {
                    Vector2 drag = PullbackDrag(releasePosition, world);
                    if (drag.magnitude >= MinDrag)
                    {
                        LaunchGhost(drag.normalized * (_tuning.MaxPower * DragToPowerFraction(drag.magnitude)));
                        _shopStonesLeft = Mathf.Max(0, _shopStonesLeft - 1);
                        ResetAccumulator();
                        if (_handReady[0].Count > 0)
                        {
                            SetupGhost(0); // the next stone steps up to the edge
                        }
                    }
                }

                _aimingPuckId = -1;
            }
        }

        // The board cell under a point, or false when the point is off the board.
        private bool ShopCellAt(Vector2 world, out int col, out int row)
        {
            col = 0;
            row = 0;
            if (!CursorInsideBoard(world))
            {
                return false;
            }

            Vector2 size = BoardCells.CellSize(_sim.BoardMin, _sim.BoardMax);
            col = Mathf.Clamp(Mathf.FloorToInt((world.x - _sim.BoardMin.x) / size.x), 0, BoardCells.Size - 1);
            row = Mathf.Clamp(Mathf.FloorToInt((world.y - _sim.BoardMin.y) / size.y), 0, BoardCells.Size - 1);
            return true;
        }

        // The rects DrawShopGui actually occupies, plus the debug panel while open. The battle HUD panel is
        // not drawn in shop mode, so board clicks must not be blocked by its rect — doing that left an
        // invisible dead strip down the left, right where the merchant stands.
        private bool PointerOverShopGui(Vector2 screen)
        {
            Vector2 gui = new Vector2(screen.x, Screen.height - screen.y); // Mouse.position is y-up; GUI rects are y-down
            if (DebugPanel.Covers(gui))
            {
                return true;
            }

            if (gui.y <= 70f)
            {
                return true; // gold, stone count and the log along the top
            }

            return gui.x >= Screen.width - 200f && gui.y <= 300f; // right-hand control column
        }

        // --- Shop GUI -----------------------------------------------------------------------------

        // Gold is shown red wherever the player cannot afford the thing next to it.
        private string GoldTag(int price)
        {
            string colour = Campaign.Gold >= price ? "#ffd24a" : "#ff5a4a";
            return $"<color={colour}>{price}G</color>";
        }

        private void OnGUI()
        {
            if (_sim == null)
            {
                return;
            }

            GUIStyle rich = new GUIStyle(GUI.skin.label) { richText = true };

            // Modal screens, and IMGUI has no z-order: a control drawn earlier still takes the click even
            // when something is painted over it. So while one is up, nothing else is drawn at all —
            // otherwise a miss near its buttons would hit "Roll stones" or "Leave shop" underneath, both
            // of which end the visit for good. The replace warning outranks the merchant: it can only be
            // open while the merchant is closed, but check it first all the same.
            if (_confirmReplaceOpen)
            {
                DrawReplaceConfirm(rich);
                return;
            }

            if (_merchantOpen)
            {
                DrawMerchantScreen(rich);
                return;
            }

            // Gold, top right (design doc 5.6: it carries between shops).
            GUI.Label(new Rect(Screen.width - 170f, 12f, 160f, 24f), $"<b>Gold {Campaign.Gold}</b>", rich);

            // Stones left this visit, above the board.
            GUI.Label(new Rect(Screen.width * 0.5f - 80f, 12f, 200f, 24f),
                $"Stones {_shopStonesLeft} / {_shopStonesTotal}", rich);

            if (_shopLog.Length > 0)
            {
                GUI.Label(new Rect(Screen.width * 0.5f - 220f, 38f, 520f, 24f), _shopLog, rich);
            }

            // Right-hand controls: roll (once, then greyed out but still shown), buy a stone, and leave.
            float x = Screen.width - 190f;
            GUI.enabled = !_shopThrowing;
            if (GUI.Button(new Rect(x, 90f, 170f, 34f), _shopThrowing ? "Rolling…" : "Roll stones"))
            {
                BeginShopThrowing();
            }
            GUI.enabled = true;

            // An extra stone for this visit (design doc 5.4): always shown, roll phase or not, greyed out
            // only while it cannot be paid for.
            GUI.enabled = Campaign.Gold >= _tuning.ShopStonePrice && _shopStonesTotal < _stoneCapacity;
            if (GUI.Button(new Rect(x, 150f, 170f, 34f), "Add stone"))
            {
                BuyStone();
            }
            GUI.enabled = true;
            GUI.Label(new Rect(x + 55f, 186f, 120f, 22f), GoldTag(_tuning.ShopStonePrice), rich);

            // Settlement reads where the stones ARE, so leaving mid-flight would freeze them wherever they
            // happened to be that frame. The way out only opens once the board has settled.
            bool settled = _sim.AllAtRest();
            GUI.enabled = settled;
            if (GUI.Button(new Rect(x, 210f, 170f, 34f), settled ? "Leave shop" : "Stones rolling…"))
            {
                LeaveShop();
                return;
            }
            GUI.enabled = true;

            if (_hasPendingCell)
            {
                GUI.Label(new Rect(x, 254f, 190f, 40f),
                    $"Placing {UpgradeName(_pendingCell)}\nright-click to cancel", rich);
            }
        }

        // The merchant's screen: three cells on the table, a reroll button, and a close button. It covers
        // the board while open (design doc 5.3 — the offer is what you may buy).
        private void DrawMerchantScreen(GUIStyle rich)
        {
            GUI.Box(new Rect(0f, 0f, Screen.width, Screen.height), GUIContent.none);

            float midX = Screen.width * 0.5f;
            GUI.Label(new Rect(midX - 60f, Screen.height * 0.18f, 200f, 30f), "<b>Merchant</b>", rich);
            GUI.Label(new Rect(Screen.width - 170f, 12f, 160f, 24f), $"<b>Gold {Campaign.Gold}</b>", rich);

            // Reroll sits above the table with its price beside it: every slot is redrawn, sold ones
            // included, and each reroll makes the next one dearer (design doc 5.3).
            int rerollPrice = RerollPrice();
            GUI.enabled = Campaign.Gold >= rerollPrice;
            if (GUI.Button(new Rect(midX + 150f, Screen.height * 0.42f - 40f, 90f, 30f), "Reroll"))
            {
                Campaign.Gold -= rerollPrice;
                _rerollCount++;
                RerollOffers();
            }
            GUI.enabled = true;
            GUI.Label(new Rect(midX + 250f, Screen.height * 0.42f - 36f, 120f, 24f), GoldTag(rerollPrice), rich);

            // The table: three slots, emptied as they are bought.
            float tableY = Screen.height * 0.42f;
            GUI.Box(new Rect(midX - 330f, tableY, 660f, 190f), GUIContent.none);

            for (int slot = 0; slot < _shopOffers.Count; slot++)
            {
                Rect card = new Rect(midX - 310f + slot * 215f, tableY + 20f, 190f, 150f);
                if (!_shopOffers[slot].HasValue)
                {
                    GUI.Label(new Rect(card.x + 60f, card.y + 60f, 120f, 24f), "sold", rich);
                    continue;
                }

                ShopOffer offer = _shopOffers[slot].Value;

                // The rare battle-stone offer shares the slot (design doc 5.3); buying it is immediate.
                if (offer.Type == OfferType.BattleStone)
                {
                    int stonePrice = _tuning.BattleStonePrice;
                    GUI.enabled = Campaign.Gold >= stonePrice && !_shopThrowing;
                    if (GUI.Button(card, "Battle stone\n\n+1 stone in battle"))
                    {
                        BuyBattleStone(slot);
                    }
                    GUI.enabled = true;

                    GUI.Label(new Rect(card.x + 70f, card.yMax + 2f, 120f, 24f), GoldTag(stonePrice), rich);

                    if (card.Contains(Event.current.mousePosition))
                    {
                        Vector2 m = Event.current.mousePosition;
                        GUI.Box(new Rect(m.x + 16f, m.y + 16f, 260f, 46f), GUIContent.none);
                        GUI.Label(new Rect(m.x + 24f, m.y + 20f, 250f, 40f),
                            "Battle stone\nOne more roster stone from the next run, until defeat.", rich);
                    }

                    continue;
                }

                UpgradeKind kind = offer.Kind;
                int price = PriceOf(kind);
                bool affordable = Campaign.Gold >= price && !_shopThrowing;

                GUI.enabled = affordable;
                if (GUI.Button(card, $"{UpgradeName(kind)}\n\n+{GainOf(kind)} per point"))
                {
                    BuyCell(slot);
                }
                GUI.enabled = true;

                GUI.Label(new Rect(card.x + 70f, card.yMax + 2f, 120f, 24f), GoldTag(price), rich);

                // Tooltip beside the cursor while the card is hovered.
                if (card.Contains(Event.current.mousePosition))
                {
                    Vector2 m = Event.current.mousePosition;
                    GUI.Box(new Rect(m.x + 16f, m.y + 16f, 260f, 46f), GUIContent.none);
                    GUI.Label(new Rect(m.x + 24f, m.y + 20f, 250f, 40f),
                        $"{UpgradeName(kind)}\nStone on this cell: +{GainOf(kind)} x cell level x stone level", rich);
                }
            }

            if (GUI.Button(new Rect(Screen.width - 110f, 50f, 90f, 30f), "Close"))
            {
                _merchantOpen = false;
            }
        }

        // The replace warning (design doc 5.1): OK replaces the cell and its levels are gone, cancel keeps
        // the board as it was — the bought cell stays on the cursor for another spot or a right-click drop.
        private void DrawReplaceConfirm(GUIStyle rich)
        {
            GUI.Box(new Rect(0f, 0f, Screen.width, Screen.height), GUIContent.none);

            float midX = Screen.width * 0.5f;
            float midY = Screen.height * 0.5f;
            GUI.Box(new Rect(midX - 220f, midY - 70f, 440f, 140f), GUIContent.none);

            ShopCell cell = Campaign.ShopBoard.CellAt(_confirmCol, _confirmRow);
            GUI.Label(new Rect(midX - 200f, midY - 52f, 400f, 48f),
                $"Replace <b>{UpgradeName(cell.Kind)}</b> (level {cell.Level}) with <b>{UpgradeName(_pendingCell)}</b>?\nAll of its levels will be lost.", rich);

            if (GUI.Button(new Rect(midX - 110f, midY + 12f, 100f, 32f), "Replace"))
            {
                _confirmReplaceOpen = false;
                CommitPendingCell(_confirmCol, _confirmRow);
            }

            if (GUI.Button(new Rect(midX + 10f, midY + 12f, 100f, 32f), "Cancel"))
            {
                _confirmReplaceOpen = false;
            }
        }
    }
}
