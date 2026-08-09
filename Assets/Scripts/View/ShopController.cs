using System.Collections.Generic;
using Puckmite.Game;
using Puckmite.Sim;
using TMPro;
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
        // Peddler art at twice the battle characters' pixel scale (사용자 지정 2026-08-09: 2배), the
        // campfire art at four times, standing to his right. The label floats over the head and bobs
        // gently to advertise the click.
        private const float MerchantArtScale = 15.8f;
        private const float CampfireArtScale = 31.6f;
        private const float CampfireX = -10f;
        // The lantern in his right hand widens the sprite bounds, so the bounds centre sits right of the
        // head — this nudge parks the label over the head itself (사용자 지정: 머리 위로).
        private const float MerchantLabelOffsetX = -0.7f;
        private const float LabelBobAmplitude = 0.3f;
        private const float LabelBobSpeed = 2.4f; // radians/sec

        // The upgrade board: no damage cells and no fixed layout — every cell starts blank and is coloured by
        // whatever the player has bought onto it (design doc 5.1). Cell quads are kept so UpdateShopCells can
        // recolour them as purchases land, plus a merchant on the left that opens the buying screen.
        private SpriteRenderer[] _shopCellViews;
        private SpriteRenderer _merchantView;
        private SpriteRenderer _pendingCellGhost;
        private bool _shopUsesArt; // cell-sheet frames wired: cells wear frames, flat quads otherwise

        // Per-cell stack gauge and level sparkles (사용자 지정 2026-08-09): each same-kind placement
        // fills one tick along the cell's bottom; a full gauge levels the cell up, shown as one more
        // sparkle in the frame's top-left row. The face art bakes the first sparkles, extras draw here.
        private SpriteRenderer[][] _shopCellXpTicks;   // [cell][XpPerLevel]
        private SpriteRenderer[][] _shopCellSparkles;  // [cell][MaxExtraSparkles]
        private const int MaxExtraSparkles = 4;        // display cap; levels keep counting past it
        private static readonly Color XpTickFilled = new Color(0.45f, 1f, 0.5f, 0.9f); // the stones' XP green
        private static readonly Color XpTickEmpty = new Color(0.08f, 0.10f, 0.14f, 0.9f);

        // The merchant's click affordances: silhouette outline (faint = clickable, bright = hovered) and
        // the floating "상점 열기" label. The click/hover disc comes from the art bounds, like the battle
        // characters', or from the placeholder circle's constants.
        [SerializeField] private GameObject _merchantBodyPrefab; // Peddler.aseprite prefab, wired by Setup
        [SerializeField] private GameObject _campfirePrefab;     // UI/campfire.aseprite prefab, wired by Setup
        private SpriteRenderer[] _merchantOutline;
        private GameObject _merchantLabelRoot;
        private float _merchantLabelBaseY;
        private Vector2 _merchantCenter;
        private float _merchantGrabRadius;

        // The buying panel's art (user mock 2026-08-09), wired by Setup Game Scenes; the panel renders
        // flat placeholder rects for any piece still missing.
        [SerializeField] private Sprite _shopPanelSprite;
        [SerializeField] private Sprite _rerollButtonSprite;
        [SerializeField] private Sprite _closeButtonSprite;
        [SerializeField] private Sprite _goldPanelSprite;
        private MerchantPanel _merchantPanel;

        // The right-of-board column (사용자 지정 2026-08-09): the remaining-stones panel — the stone icon
        // and count in its space under the title — then the roll / buy-stone / leave buttons, replacing
        // the old IMGUI corner controls. Same hover-outline and dim language as everywhere else.
        [SerializeField] private Sprite _stonePanelSprite;      // UI/panel_stone_count_1
        [SerializeField] private Sprite _rollButtonSprite;      // UI/btn_shop_roll
        [SerializeField] private Sprite _buyStoneButtonSprite;  // UI/btn_shop_buy_stone
        [SerializeField] private Sprite _leaveButtonSprite;     // UI/btn_shop_leave
        [SerializeField] private Sprite _stoneIconSprite;       // stat sheet Frame_3, the stone pebble
        private const float SideX = 22.5f;                      // column centre, right of the board wall
        private static readonly Color SideOutlineTint = new Color(1f, 0.9f, 0.25f, 0.9f);
        private static readonly Color SideDimmed = new Color(1f, 1f, 1f, 0.45f);
        private GameObject _sideRoot;
        private TextMeshPro _stoneCountText;
        private SpriteRenderer _rollOutline;
        private SpriteRenderer _rollBg;
        private SpriteRenderer _buyOutline;
        private SpriteRenderer _buyBg;
        private TextMeshPro _buyPriceText;
        private SpriteRenderer _leaveOutline;
        private SpriteRenderer _leaveBg;

        // The always-visible gold readout on the backdrop's signpost, right of it on the field
        // (사용자 지정 2026-08-09) — the buying panel does not cover it, so it never disappears.
        private TextMeshPro _goldReadoutText;

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

        // Replace warning (design doc 5.1): a different kind wipes the cell's levels, so the placement
        // waits here for OK/cancel instead of landing straight away.
        private bool _confirmReplaceOpen;
        private int _confirmCol;
        private int _confirmRow;

        // The view arrays are sized once per Build and never grow (see ArenaControllerBase), so the roster
        // is pre-sized for every stone this visit could possibly field: the granted ones plus a fixed
        // headroom of buys. A fixed number rather than entry gold (사용자 발견 2026-08-09): capacity
        // derived from entry gold froze the buy button for the whole visit when gold arrived later
        // (debug +10000), because the arrays could not grow to match it.
        private const int MaxStoneBuysPerVisit = 10;
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
                _stoneCapacity = _shopStonesTotal + MaxStoneBuysPerVisit;
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

        // The throw comes off the left wall (사용자 지정 2026-08-09 — the right side now holds the shop
        // controls), so the cursor's y is what slides along it.
        protected override Vector2 EntryPoint(int actor, float along)
        {
            float inset = _tuning.PuckRadius * RingRadiusScale;
            float minY = _sim.BoardMin.y + inset;
            float maxY = _sim.BoardMax.y - inset;
            return new Vector2(_sim.BoardMin.x + inset, Mathf.Clamp(along, minY, maxY));
        }

        protected override float EntryAlong(Vector2 world)
        {
            return world.y;
        }

        // The shop is the player alone (design doc 5.4).
        protected override int DeclaredActorCount()
        {
            return 1;
        }

        protected override void EntryAxisBounds(out float min, out float max)
        {
            min = _sim.BoardMin.y;
            max = _sim.BoardMax.y;
        }

        protected override void Update()
        {
            base.Update();

            // The upgrade board has no turns, no enemies and no attacks — just throwing stones onto cells.
            HandleShopInput();
            DriveSimulation();
            UpdatePuckTransforms();
            UpdateShopCells();
            UpdateMerchantHighlight();
            UpdateMerchantPanel();
            UpdateShopSideUi();
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

        // The panel's reroll button: every slot is redrawn, sold ones included, and each reroll makes
        // the next one dearer (design doc 5.3).
        private void TryReroll()
        {
            int price = RerollPrice();
            if (Campaign.Gold < price)
            {
                return;
            }

            Campaign.Gold -= price;
            _rerollCount++;
            RerollOffers();
        }

        private void OnMerchantSlotClicked(int slot)
        {
            if (slot < 0 || slot >= _shopOffers.Count || !_shopOffers[slot].HasValue)
            {
                return;
            }

            if (_shopOffers[slot].Value.Type == OfferType.BattleStone)
            {
                BuyBattleStone(slot);
            }
            else
            {
                BuyCell(slot);
            }
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

        // Player-facing kind names are Korean (the in-game UI language); UpgradeName stays English for
        // the developer-facing log lines.
        private static string KoreanUpgradeName(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.Attack: return "공격";
                case UpgradeKind.Shield: return "방패";
                case UpgradeKind.RunHeal: return "회복";
                default: return "최대 체력";
            }
        }

        // What a kind raises, in the tooltip's words (사용자 지정 2026-08-09).
        private static string KoreanUpgradeEffect(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.Attack: return "기본 공격력";
                case UpgradeKind.Shield: return "기본 쉴드량";
                case UpgradeKind.RunHeal: return "체력 회복";
                default: return "최대 체력";
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
        // stronger" rule. RunHeal/MaxHealth have one face each (no sparkle variant drawn yet); a missing
        // frame returns null and the cell keeps the colour quad.
        private Sprite UpgradeSprite(UpgradeKind kind, int level)
        {
            switch (kind)
            {
                case UpgradeKind.Attack: return level >= 2 ? _cellAttackStrongSprite : _cellAttackSprite;
                case UpgradeKind.Shield: return level >= 2 ? _cellShieldStrongSprite : _cellShieldSprite;
                case UpgradeKind.RunHeal: return _cellRunHealSprite;
                default: return _cellMaxHealthSprite;
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

            Campaign.ShopBoard.Place(col, row, _pendingCell);
            Campaign.Gold -= price;
            if (_pendingSlot >= 0 && _pendingSlot < _shopOffers.Count)
            {
                _shopOffers[_pendingSlot] = null; // that slot is spent until a reroll
            }

            _hasPendingCell = false;
            _pendingSlot = -1;
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
            _shopCellXpTicks = new SpriteRenderer[BoardCells.Size * BoardCells.Size][];
            _shopCellSparkles = new SpriteRenderer[BoardCells.Size * BoardCells.Size][];
            for (int row = 0; row < BoardCells.Size; row++)
            {
                for (int col = 0; col < BoardCells.Size; col++)
                {
                    int index = col + row * BoardCells.Size;
                    Vector2 center = BoardCells.CellCenter(boardMin, boardMax, col, row);
                    // Art frames carry their own borders, so they sit edge to edge at full cell size;
                    // the flat quads keep their 0.96 inset as the visual grid.
                    _shopCellViews[index] = _shopUsesArt
                        ? MakeCellSprite("ShopCell", board, center, cellSize, _cellEmptySprite, 1)
                        : MakeQuad("ShopCell", board, center, cellSize * 0.96f, EmptyShopCellColor, 1);

                    // The stack gauge (hidden until the cell is bought) and the extra level sparkles.
                    SpriteRenderer[] ticks = new SpriteRenderer[ShopBoard.XpPerLevel];
                    for (int t = 0; t < ticks.Length; t++)
                    {
                        SpriteRenderer tick = MakeQuad("CellXpTick", board,
                            center + new Vector2(-1.45f + 1.45f * t, -1.95f), new Vector2(1.2f, 0.35f), XpTickEmpty, 2);
                        tick.enabled = false;
                        ticks[t] = tick;
                    }

                    _shopCellXpTicks[index] = ticks;

                    SpriteRenderer[] sparkles = new SpriteRenderer[MaxExtraSparkles];
                    for (int s = 0; s < sparkles.Length; s++)
                    {
                        SpriteRenderer sparkle = MakeQuad("CellLvSparkle", board, center, new Vector2(0.34f, 0.34f), Color.white, 2);
                        sparkle.transform.localRotation = Quaternion.Euler(0f, 0f, 45f); // diamond, like the art's
                        sparkle.enabled = false;
                        sparkles[s] = sparkle;
                    }

                    _shopCellSparkles[index] = sparkles;
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
            _merchantView = TryBuildMerchantBody();
            if (_merchantView == null)
            {
                GameObject merchantGo = new GameObject("Merchant");
                merchantGo.transform.SetParent(transform, false);
                float d = MerchantRadius * 2f;
                merchantGo.transform.localPosition = new Vector3(MerchantX, MerchantY, 0f);
                merchantGo.transform.localScale = new Vector3(d, d, 1f);
                _merchantView = merchantGo.AddComponent<SpriteRenderer>();
                _merchantView.sprite = ProceduralSprites.Circle();
                _merchantView.color = new Color(0.85f, 0.75f, 0.45f);
                _merchantView.sortingOrder = 10;
                _merchantCenter = new Vector2(MerchantX, MerchantY);
                _merchantGrabRadius = MerchantRadius;
            }

            // Faint at rest ("this is clickable"), bright under the cursor — the battle characters' language.
            _merchantOutline = BuildCharacterOutline(_merchantView);
            BuildMerchantLabel();
            TryBuildCampfire();
            BuildGoldReadout();

            // The buying panel, hidden until the merchant is clicked (user mock 2026-08-09).
            _merchantPanel = new MerchantPanel(transform, ShopOfferSlots,
                _shopPanelSprite, _closeButtonSprite, _rerollButtonSprite);
            _merchantPanel.CloseClicked = () => _merchantOpen = false;
            _merchantPanel.RerollClicked = TryReroll;
            _merchantPanel.SlotClicked = OnMerchantSlotClicked;

            // Ghost of the cell being carried from the shop, snapped to whichever board cell is hovered.
            GameObject pending = new GameObject("PendingCellGhost");
            pending.transform.SetParent(transform, false);
            _pendingCellGhost = pending.AddComponent<SpriteRenderer>();
            _pendingCellGhost.sprite = ProceduralSprites.Unit();
            _pendingCellGhost.sortingOrder = 4;
            _pendingCellGhost.enabled = false;

            BuildShopSideUi();
        }

        // One bought cell's stack gauge and level sparkles: the ticks along the bottom fill with each
        // same-kind placement, and the sparkle count equals the cell's level (사용자 지정) — the face
        // art bakes the first ones (one per face; two on the lv2+ attack/shield faces), extras continue
        // the top-left row here, capped at MaxExtraSparkles for display.
        private void UpdateCellProgress(int index, ShopCell cell, Vector2 center)
        {
            SpriteRenderer[] ticks = _shopCellXpTicks[index];
            for (int t = 0; t < ticks.Length; t++)
            {
                ticks[t].enabled = !cell.IsEmpty;
                if (!cell.IsEmpty)
                {
                    ticks[t].color = t < cell.Xp ? XpTickFilled : XpTickEmpty;
                }
            }

            SpriteRenderer[] sparkles = _shopCellSparkles[index];
            int baked = !_shopUsesArt || cell.IsEmpty
                ? 0
                : (cell.Level >= 2 && (cell.Kind == UpgradeKind.Attack || cell.Kind == UpgradeKind.Shield) ? 2 : 1);
            int extras = cell.IsEmpty ? 0 : Mathf.Clamp(cell.Level - baked, 0, sparkles.Length);
            for (int s = 0; s < sparkles.Length; s++)
            {
                sparkles[s].enabled = s < extras;
                if (s < extras)
                {
                    // Continue the art's sparkle row rightward from however many it baked.
                    sparkles[s].transform.localPosition = new Vector3(
                        center.x - 2.5f + 0.65f + 0.7f * (baked + s), center.y + 2.5f - 0.65f, 0f);
                    sparkles[s].color = SparkleColor(cell.Kind);
                }
            }
        }

        // The art's sparkle colours by kind, for the extra level sparkles.
        private static Color SparkleColor(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.Attack: return new Color(1f, 0.68f, 0.25f);
                case UpgradeKind.Shield: return new Color(0.40f, 0.85f, 1f);
                case UpgradeKind.RunHeal: return new Color(0.45f, 0.95f, 0.5f);
                default: return new Color(1f, 0.40f, 0.45f);
            }
        }

        // The peddler art (Peddler.aseprite prefab): the battle characters' pixel scale, feet on the
        // grass line, aligned by bounds since the import pivot sits below the feet. Returns null when
        // the art is missing or broken, and the caller falls back to the circle.
        private SpriteRenderer TryBuildMerchantBody()
        {
            if (_merchantBodyPrefab == null)
            {
                return null;
            }

            GameObject go = Instantiate(_merchantBodyPrefab, transform, false);
            go.name = "Merchant";

            SpriteRenderer sr = go.GetComponentInChildren<SpriteRenderer>();
            if (sr == null || sr.sprite == null)
            {
                Debug.LogError("[PuckHero] Peddler prefab has no usable SpriteRenderer — using the placeholder circle.");
                Destroy(go);
                return null;
            }

            sr.sortingOrder = 10;
            sr.color = Color.white;

            if (go.TryGetComponent(out Animator animator))
            {
                animator.speed = 0.5f; // idle plays at half speed, like the battle bodies
            }

            go.transform.localPosition = new Vector3(MerchantX, CharFeetY, 0f);
            go.transform.localScale *= MerchantArtScale;
            float height = sr.bounds.size.y;
            Vector3 target = transform.TransformPoint(new Vector3(MerchantX, CharFeetY + height * 0.5f, 0f));
            go.transform.position += target - sr.bounds.center;

            _merchantCenter = new Vector2(MerchantX, CharFeetY + height * 0.5f);
            _merchantGrabRadius = height * 0.6f;
            return sr;
        }

        // The campfire art beside the peddler (사용자 지정: 그의 오른쪽, 4배 배율), its base sitting on
        // the same grass line, aligned by bounds like every character.
        private void TryBuildCampfire()
        {
            if (_campfirePrefab == null)
            {
                return;
            }

            GameObject go = Instantiate(_campfirePrefab, transform, false);
            go.name = "Campfire";
            SpriteRenderer sr = go.GetComponentInChildren<SpriteRenderer>();
            if (sr == null || sr.sprite == null)
            {
                Destroy(go);
                return;
            }

            sr.sortingOrder = 11;
            sr.color = Color.white;
            go.transform.localPosition = new Vector3(CampfireX, CharFeetY, 0f);
            go.transform.localScale *= CampfireArtScale;
            float height = sr.bounds.size.y;
            Vector3 target = transform.TransformPoint(new Vector3(CampfireX, CharFeetY + height * 0.5f, 0f));
            go.transform.position += target - sr.bounds.center;
        }

        // The always-visible gold readout: the gold panel art parked right of the backdrop's signpost,
        // its base on the field line (사용자 지정), refreshed every frame in UpdateShopSideUi.
        private void BuildGoldReadout()
        {
            SideRect(transform, "GoldReadoutBg", _goldPanelSprite, new Vector2(31.3f, 15.35f), new Vector2(8.4f, 3.9f),
                _goldPanelSprite != null ? Color.white : new Color(0.28f, 0.33f, 0.44f), 15);
            _goldReadoutText = SideText(transform, "GoldReadoutText", "0G", new Vector2(31.3f, 15.3f),
                new Vector2(7.2f, 3.0f), TextAlignmentOptions.Center, 10f, 16);
            _goldReadoutText.color = new Color(1f, 0.823f, 0.29f);
        }

        // "상점 열기" floats over the merchant's head — plain Korean text, no box — telling what the
        // click does. UpdateMerchantHighlight bobs it.
        private void BuildMerchantLabel()
        {
            _merchantLabelRoot = new GameObject("MerchantLabel");
            _merchantLabelRoot.transform.SetParent(transform, false);

            TextMeshPro tmp = _merchantLabelRoot.AddComponent<TextMeshPro>();
            if (KoreanFont.Asset() != null)
            {
                tmp.font = KoreanFont.Asset();
            }

            tmp.text = "상점 열기";
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 12f; // 사용자 지정 2026-08-09: 상점 텍스트 2배
            tmp.rectTransform.sizeDelta = new Vector2(14f, 3.5f);
            tmp.color = new Color(1f, 0.92f, 0.55f); // warm, toward the highlight yellow
            tmp.GetComponent<MeshRenderer>().sortingOrder = 15;

            _merchantLabelBaseY = _merchantView.bounds.max.y + 1.1f;
            _merchantLabelRoot.transform.localPosition = new Vector3(MerchantX + MerchantLabelOffsetX, _merchantLabelBaseY, 0f);
        }

        // Merchant affordances, per frame: the label bobs over the head, the outline sits faint ("this
        // is clickable") and lights up under the cursor. All of it goes down once throwing starts or a
        // modal is up — the merchant no longer opens then, and the promise would be a lie.
        private void UpdateMerchantHighlight()
        {
            if (_merchantOutline == null)
            {
                return;
            }

            bool clickable = !_shopThrowing && !_merchantOpen && !_confirmReplaceOpen;
            _merchantLabelRoot.SetActive(clickable);
            if (!clickable)
            {
                SetSpriteOutline(_merchantOutline, _merchantView, default, false);
                return;
            }

            float bob = Mathf.Sin(Time.time * LabelBobSpeed) * LabelBobAmplitude;
            _merchantLabelRoot.transform.localPosition = new Vector3(MerchantX + MerchantLabelOffsetX, _merchantLabelBaseY + bob, 0f);

            bool hovered = false;
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                Vector2 screen = mouse.position.ReadValue();
                hovered = !PointerOverShopGui(screen)
                    && (ScreenToWorld(screen) - _merchantCenter).magnitude <= _merchantGrabRadius;
            }

            SetSpriteOutline(_merchantOutline, _merchantView, hovered ? RingStrong : OutlineFaint, true,
                hovered ? HoverOutlineThickness : OutlineThickness);
        }

        // Feeds the buying panel and drives its input while the merchant is open: offers, prices, gold
        // and the reroll cost every frame (cheap, and it keeps the panel honest after buys and rerolls),
        // clicks through the panel's callbacks, ESC as a second way out (사용자 지정).
        private void UpdateMerchantPanel()
        {
            if (_merchantPanel == null)
            {
                return;
            }

            if (!_merchantOpen || _confirmReplaceOpen)
            {
                _merchantPanel.Hide();
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                _merchantOpen = false;
                _merchantPanel.Hide();
                return;
            }

            _merchantPanel.Show();
            int rerollPrice = RerollPrice();
            _merchantPanel.SetReroll(rerollPrice, Campaign.Gold >= rerollPrice);

            for (int slot = 0; slot < _shopOffers.Count; slot++)
            {
                if (!_shopOffers[slot].HasValue)
                {
                    _merchantPanel.SetSlotSold(slot);
                    continue;
                }

                ShopOffer offer = _shopOffers[slot].Value;
                if (offer.Type == OfferType.BattleStone)
                {
                    // The stone deal has no card art yet: the plain cell frame carries the line.
                    _merchantPanel.SetSlot(slot, _cellEmptySprite, "전투 스톤 +1", _tuning.BattleStonePrice,
                        Campaign.Gold >= _tuning.BattleStonePrice && !_shopThrowing);
                }
                else
                {
                    _merchantPanel.SetSlot(slot, UpgradeSprite(offer.Kind, 1), null, PriceOf(offer.Kind),
                        Campaign.Gold >= PriceOf(offer.Kind) && !_shopThrowing);
                }
            }

            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            Vector2 screen = mouse.position.ReadValue();
            _merchantPanel.Tick(ScreenToWorld(screen), mouse.leftButton.wasPressedThisFrame, PointerOverShopGui(screen));
        }

        // The right-of-board column: the remaining-stones panel with the stone icon and count in the
        // space under its baked title, then the roll / buy-stone / leave buttons (사용자 지정).
        private void BuildShopSideUi()
        {
            _sideRoot = new GameObject("ShopSideUi");
            _sideRoot.transform.SetParent(transform, false);
            Transform root = _sideRoot.transform;

            SideRect(root, "StonePanel", _stonePanelSprite, new Vector2(SideX, 9.4f), new Vector2(15f, 4.6f),
                _stonePanelSprite != null ? Color.white : new Color(0.14f, 0.17f, 0.24f, 0.9f), 15);
            SideRect(root, "StoneIcon", _stoneIconSprite != null ? _stoneIconSprite : ProceduralSprites.Circle(),
                new Vector2(SideX - 2.6f, 8.65f), new Vector2(1.9f, 1.9f),
                _stoneIconSprite != null ? Color.white : new Color(0.92f, 0.92f, 0.95f), 16);
            _stoneCountText = SideText(root, "StoneCount", "x 0", new Vector2(SideX + 2.2f, 8.6f),
                new Vector2(7f, 2.7f), TextAlignmentOptions.MidlineLeft, 14f, 16);

            _rollOutline = SideRect(root, "RollOutline", null, new Vector2(SideX, 4.6f), new Vector2(17.5f, 4.3f), SideOutlineTint, 15);
            _rollOutline.enabled = false;
            _rollBg = SideRect(root, "RollButton", _rollButtonSprite, new Vector2(SideX, 4.6f), new Vector2(17f, 3.8f),
                _rollButtonSprite != null ? Color.white : new Color(0.28f, 0.33f, 0.44f), 16);

            _buyOutline = SideRect(root, "BuyOutline", null, new Vector2(SideX, 0.2f), new Vector2(17.5f, 4.3f), SideOutlineTint, 15);
            _buyOutline.enabled = false;
            _buyBg = SideRect(root, "BuyButton", _buyStoneButtonSprite, new Vector2(SideX, 0.2f), new Vector2(17f, 3.8f),
                _buyStoneButtonSprite != null ? Color.white : new Color(0.28f, 0.33f, 0.44f), 16);
            _buyPriceText = SideText(root, "BuyPrice", "0G", new Vector2(SideX, -2.4f),
                new Vector2(8f, 2.6f), TextAlignmentOptions.Center, 10f, 16);

            _leaveOutline = SideRect(root, "LeaveOutline", null, new Vector2(SideX, -6.3f), new Vector2(17.5f, 4.3f), SideOutlineTint, 15);
            _leaveOutline.enabled = false;
            _leaveBg = SideRect(root, "LeaveButton", _leaveButtonSprite, new Vector2(SideX, -6.3f), new Vector2(17f, 3.8f),
                _leaveButtonSprite != null ? Color.white : new Color(0.28f, 0.33f, 0.44f), 16);
        }

        // States, hover and clicks for the side column, every frame. Hidden while a modal screen is up
        // (the old IMGUI column was simply not drawn then).
        private void UpdateShopSideUi()
        {
            if (_sideRoot == null)
            {
                return;
            }

            // The signpost gold readout stays on through the modals (사용자 지정) — they never cover it.
            if (_goldReadoutText != null)
            {
                _goldReadoutText.text = Campaign.Gold + "G";
            }

            bool shown = !_merchantOpen && !_confirmReplaceOpen;
            if (_sideRoot.activeSelf != shown)
            {
                _sideRoot.SetActive(shown);
            }

            if (!shown)
            {
                return;
            }

            _stoneCountText.text = "x " + _shopStonesLeft;

            bool canRoll = !_shopThrowing;
            bool canBuy = Campaign.Gold >= _tuning.ShopStonePrice && _shopStonesTotal < _stoneCapacity;
            // Settlement reads where the stones ARE, so leaving mid-flight would freeze them wherever
            // they happened to be that frame. The way out only opens once the board has settled.
            bool canLeave = _sim.AllAtRest();
            _rollBg.color = canRoll ? Color.white : SideDimmed;
            _buyBg.color = canBuy ? Color.white : SideDimmed;
            _leaveBg.color = canLeave ? Color.white : SideDimmed;
            _buyPriceText.text = _tuning.ShopStonePrice + "G";
            _buyPriceText.color = Campaign.Gold >= _tuning.ShopStonePrice
                ? new Color(1f, 0.823f, 0.29f)
                : new Color(1f, 0.353f, 0.29f);

            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            Vector2 screen = mouse.position.ReadValue();
            bool blocked = PointerOverShopGui(screen);
            Vector2 world = ScreenToWorld(screen);

            bool hoverRoll = !blocked && canRoll && InBounds(_rollBg, world);
            bool hoverBuy = !blocked && canBuy && InBounds(_buyBg, world);
            bool hoverLeave = !blocked && canLeave && InBounds(_leaveBg, world);
            _rollOutline.enabled = hoverRoll;
            _buyOutline.enabled = hoverBuy;
            _leaveOutline.enabled = hoverLeave;

            if (!mouse.leftButton.wasPressedThisFrame)
            {
                return;
            }

            if (hoverRoll)
            {
                BeginShopThrowing();
            }
            else if (hoverBuy)
            {
                BuyStone();
            }
            else if (hoverLeave)
            {
                LeaveShop();
            }
        }

        private static bool InBounds(SpriteRenderer sr, Vector2 world)
        {
            Bounds b = sr.bounds;
            return world.x >= b.min.x && world.x <= b.max.x && world.y >= b.min.y && world.y <= b.max.y;
        }

        private static SpriteRenderer SideRect(Transform parent, string name, Sprite art, Vector2 pos, Vector2 size, Color tint, int order)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = order;
            sr.color = tint;
            sr.sprite = art != null ? art : ProceduralSprites.Unit();
            Bounds b = sr.sprite.bounds;
            Vector3 scale = new Vector3(size.x / b.size.x, size.y / b.size.y, 1f);
            go.transform.localScale = scale;
            go.transform.localPosition = new Vector3(pos.x - b.center.x * scale.x, pos.y - b.center.y * scale.y, 0f);
            return sr;
        }

        private static TextMeshPro SideText(Transform parent, string name, string text, Vector2 pos, Vector2 box,
            TextAlignmentOptions alignment, float maxSize, int order)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            if (KoreanFont.Asset() != null)
            {
                tmp.font = KoreanFont.Asset();
            }

            tmp.text = text;
            tmp.alignment = alignment;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 1f;
            tmp.fontSizeMax = maxSize;
            tmp.rectTransform.sizeDelta = box;
            tmp.color = Color.white;
            tmp.GetComponent<MeshRenderer>().sortingOrder = order;
            return tmp;
        }

        // Every shop stone starts in hand and comes in off the left wall.
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
                    int index = col + row * BoardCells.Size;
                    ShopCell cell = Campaign.ShopBoard.CellAt(col, row);
                    SpriteRenderer view = _shopCellViews[index];
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
                        // Flat board, or a kind whose sheet frame is missing: the colour language.
                        view.sprite = ProceduralSprites.Unit();
                        view.transform.localScale = new Vector3(cellSize.x * 0.96f, cellSize.y * 0.96f, 1f);
                        view.transform.localPosition = new Vector3(center.x, center.y, 0f);
                        view.color = cell.IsEmpty
                            ? EmptyShopCellColor
                            : Color.Lerp(UpgradeColor(cell.Kind), Color.white, Mathf.Min(0.12f * (cell.Level - 1), 0.5f));
                    }

                    UpdateCellProgress(index, cell, center);
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
                && (world - _merchantCenter).magnitude <= _merchantGrabRadius)
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
                Vector2 drag = LaunchDrag(aimPosition, world);
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
                    Vector2 drag = LaunchDrag(releasePosition, world);
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

            return gui.y <= 90f; // the pending-cell hint along the top
        }

        // --- Shop GUI -----------------------------------------------------------------------------

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

            // The buying panel itself is world-space sprites (MerchantPanel); IMGUI only follows the
            // cursor with the hovered offer's tooltip. Everything else stays undrawn so no hidden
            // control can take a click through the panel.
            if (_merchantOpen)
            {
                DrawMerchantTooltip(rich);
                return;
            }

            // Gold lives on the world-space signpost readout now, the stone count and the shop controls
            // in the world-space side column, and the placement log is gone (사용자 지정); IMGUI keeps
            // only the pending-cell hint and the tooltips.
            if (_hasPendingCell)
            {
                GUI.Label(new Rect(Screen.width * 0.5f - 220f, 38f, 520f, 24f),
                    $"Placing {UpgradeName(_pendingCell)} — right-click to cancel", rich);
            }

            DrawBoardCellTooltip(rich);
        }

        // Kind and level of the bought board cell under the cursor (사용자 지정 2026-08-09) — quiet on
        // empty cells, over the GUI, and mid-drag where it would only chase the aim.
        private void DrawBoardCellTooltip(GUIStyle rich)
        {
            if (_aiming)
            {
                return;
            }

            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            Vector2 screen = mouse.position.ReadValue();
            if (PointerOverShopGui(screen) || !ShopCellAt(ScreenToWorld(screen), out int col, out int row))
            {
                return;
            }

            ShopCell cell = Campaign.ShopBoard.CellAt(col, row);
            if (cell.IsEmpty)
            {
                return;
            }

            string text = $"{KoreanUpgradeName(cell.Kind)} 칸 · 레벨 {cell.Level}";
            Vector2 m = Event.current.mousePosition;
            GUI.Box(new Rect(m.x + 16f, m.y + 16f, 190f, 28f), GUIContent.none);
            GUI.Label(new Rect(m.x + 24f, m.y + 20f, 180f, 22f), text, rich);
        }

        // The cursor-following tooltip for the offer the panel reports as hovered — the one piece of the
        // buying screen still on IMGUI, so it can sit beside the cursor for free.
        private void DrawMerchantTooltip(GUIStyle rich)
        {
            int slot = _merchantPanel != null ? _merchantPanel.HoveredSlot : -1;
            if (slot < 0 || slot >= _shopOffers.Count || !_shopOffers[slot].HasValue)
            {
                return;
            }

            // Tooltip copy is 사용자 지정 (2026-08-09); the number tracks the tuning so it never lies.
            ShopOffer offer = _shopOffers[slot].Value;
            string text = offer.Type == OfferType.BattleStone
                ? "전투 스톤\n전투에서 사용할 수 있는 스톤 수 +1"
                : $"{KoreanUpgradeName(offer.Kind)} 칸\n해당 칸에 올라간 스톤 수 만큼 {KoreanUpgradeEffect(offer.Kind)} +{GainOf(offer.Kind)}";

            Vector2 m = Event.current.mousePosition;
            GUI.Box(new Rect(m.x + 16f, m.y + 16f, 260f, 46f), GUIContent.none);
            GUI.Label(new Rect(m.x + 24f, m.y + 20f, 250f, 40f), text, rich);
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
