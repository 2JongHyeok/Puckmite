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
        private TextMeshPro _pendingGuideText; // "우클릭으로 구매 취소", left of the board while carrying
        private bool _shopUsesArt; // cell-sheet frames wired: cells wear frames, flat quads otherwise

        // Per-cell stack gauge and level sparkles (사용자 지정 2026-08-09): each same-kind placement
        // fills one tick along the cell's bottom; a full gauge levels the cell up, shown as one more
        // sparkle in the frame's top-left row. The face art bakes the first sparkles, extras draw here.
        private SpriteRenderer[][] _shopCellXpTicks;   // [cell][XpPerLevel]
        private SpriteRenderer[][] _shopCellSparkles;  // [cell][MaxExtraSparkles]
        private const int MaxExtraSparkles = 4;        // display cap; levels keep counting past it
        private static readonly Color XpTickFilled = new Color(0.45f, 1f, 0.5f, 0.9f); // the stones' XP green
        private static readonly Color XpTickEmpty = new Color(0.08f, 0.10f, 0.14f, 0.9f);
        private static readonly Color XpTickBlink = new Color(0.8f, 1f, 0.85f, 1f); // the stack-hover flash

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

        // The player standing top-centre with battle-style stat rows (사용자 지정 2026-08-10): health
        // carry/max, shield, attack, battle stones — live campaign values, and the target the leave
        // settlement's icons fly to. Art and icons wired by Setup; battle's row geometry, mirrored.
        [SerializeField] private GameObject _heroBodyPrefab;
        [SerializeField] private Sprite _healthIconSprite;
        [SerializeField] private Sprite _shieldIconSprite;
        [SerializeField] private Sprite _attackIconSprite;
        [SerializeField] private Sprite _runHealIconSprite; // stat sheet Frame_5, the green heal heart
        [SerializeField] private Sprite _goldIconSprite;    // UI/gold (사용자 아트 2026-08-10)
        private const float HeroX = 0f;
        private const float HeroBodyHeight = 6.4f;  // battle's CharBodyHeight
        private const float StatRowHeight = 1.1f;
        private const int StatRowCount = 4;         // top to bottom: health, shield, attack, stones
        private const float StatBlockBottom = CharFeetY + HeroBodyHeight + 0.4f;
        private TextMeshPro[] _statRowTexts;
        private Vector2[] _statRowIconPos;          // world centre of each row's icon, the flight targets

        // The leave settlement flight (사용자 지정 2026-08-10): one icon per settled point rises from its
        // board cell to its stat row (base icon-flight utility), the shown number ticking up as each
        // lands; the campaign state is already final when the flight starts (display only), then the
        // next run loads after a beat.
        private const float SettleFlightLead = 0.15f;  // breath between the click and the first launch
        private const float SettleLoadBeat = 0.35f;    // pause after the last landing, before the scene
        private readonly List<int> _settleCells = new List<int>(); // reused per stone, like the sim's
        private bool _leaving;
        private float _leaveLoadTimer;
        private int _shownMaxHealth;
        private int _shownShield;
        private int _shownAttack;

        // Leaving asks first (사용자 지정 2026-08-10): the difficulty picker's dress, an icon row of
        // the buffs this settlement buys (글 말고 아이콘 — 사용자 지정), the unrolled-stones warning
        // when any remain, then [전투로 이동] settles and goes, [뒤로가기]/ESC returns to the board.
        // Built lazily on the first open, like the pause menu.
        private bool _leaveConfirmOpen;
        private GameObject _leaveConfirmRoot;
        private TextMeshPro _leaveConfirmBody;
        private readonly TextMeshPro[] _leaveGainTexts = new TextMeshPro[4]; // 공격·쉴드·최대체력·회복
        private SpriteRenderer _leaveGoOutline;
        private SpriteRenderer _leaveGoBg;
        private SpriteRenderer _leaveBackOutline;
        private SpriteRenderer _leaveBackBg;

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
                // Stones bought with 스톤 추가하기 are permanent (사용자 지정 2026-08-10): every visit
                // starts with the granted ones plus everything bought so far this campaign.
                _shopStonesTotal = Mathf.Max(1, _tuning.ShopStonesPerVisit + Campaign.ExtraShopStones);
                _shopStonesLeft = _shopStonesTotal;
                _stoneCapacity = _shopStonesTotal + MaxStoneBuysPerVisit;
                RerollOffers();
            }

            base.Awake();
        }

        protected override void BuildMode()
        {
            BuildShopBoard();
            BuildShopHero();
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

            // A rolled stone must come to rest before the next steps up (사용자 지정 2026-08-10): the
            // launch no longer stages its successor — it appears here, once the board settles.
            if (_shopThrowing && !_leaving && !_ghostActive && _handReady[0].Count > 0 && _sim.AllAtRest())
            {
                SetupGhost(0);
            }

            UpdatePuckTransforms();
            UpdateShopCells();
            UpdateMerchantHighlight();
            UpdateMerchantPanel();
            UpdateShopSideUi();
            UpdateLeaveConfirm();
            UpdateShopHeroStats();
            TickLeaveFlights();
            UpdateGhost();
        }

        // --- Shop flow ----------------------------------------------------------------------------

        // The shop's own modals eat ESC first (the buying screen closes on it), so the pause menu only
        // toggles while nothing else owns the key.
        protected override bool PauseMenuBlocked()
        {
            return _merchantOpen || _confirmReplaceOpen || _leaveConfirmOpen;
        }

        // The ESC menu's bottom button (사용자 지정 2026-08-10).
        protected override void OnPauseMainMenu()
        {
            GameFlow.LoadTitle();
        }

        // Leaving settles the board as it stands: whatever the stones are sitting on is what gets bought
        // into the player's stats for good (design doc 5.2/5.5). The campaign lands in full right here —
        // the icon flight that follows is display only (사용자 지정 2026-08-10), so a reload mid-flight
        // cannot double-settle. With nothing settled it goes straight on to the next run, as before.
        private void LeaveShop()
        {
            _shownMaxHealth = _tuning.PlayerBaseHealth + Campaign.BonusMaxHealth;
            _shownShield = _tuning.PlayerBaseShield + Campaign.BonusShield;
            _shownAttack = _tuning.PlayerBaseAttack + Campaign.BonusAttack;

            UpgradeTotals gained = Campaign.ShopBoard.SumUpgrades(_sim, OccupancyThreshold);
            Campaign.BonusAttack += gained.Attack * _tuning.GainAttack;
            Campaign.BonusShield += gained.Shield * _tuning.GainShield;
            Campaign.BonusRunHeal += gained.RunHeal * _tuning.GainRunHeal;
            Campaign.BonusMaxHealth += gained.MaxHealth * _tuning.GainMaxHealth;

            Campaign.AdvanceRun();

            if (BuildSettleFlights() == 0)
            {
                GameFlow.LoadBattle();
                return;
            }

            _leaving = true;
            _leaveLoadTimer = 0f;
        }

        // One flight per settled point, launched from the cell that earned it — the same walk as
        // ShopBoard.SumUpgrades, per (stone, cell) pair so the icons rise from where the value sits.
        private int BuildSettleFlights()
        {
            int spawned = 0;
            float start = SettleFlightLead;
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                Puck p = pucks[i];
                BoardCells.GetOccupiedCells(_sim.BoardMin, _sim.BoardMax, p.Position, p.Radius, OccupancyThreshold, _settleCells);
                for (int c = 0; c < _settleCells.Count; c++)
                {
                    int index = _settleCells[c];
                    int col = index % BoardCells.Size;
                    int row = index / BoardCells.Size;
                    ShopCell cell = Campaign.ShopBoard.CellAt(col, row);
                    if (cell.IsEmpty)
                    {
                        continue;
                    }

                    int points = cell.Level * p.Level;
                    Vector2 from = BoardCells.CellCenter(_sim.BoardMin, _sim.BoardMax, col, row);
                    for (int n = 0; n < points; n++)
                    {
                        SpawnSettleFlight(cell.Kind, from, start);
                        start += IconFlightStagger;
                        spawned++;
                    }
                }
            }

            return spawned;
        }

        // Attack and shield fly to their rows; run-heal and max-health both fly to the health row
        // (사용자 지정 2026-08-10 — battle keeps its four rows), run-heal landing without a number change.
        private void SpawnSettleFlight(UpgradeKind kind, Vector2 from, float start)
        {
            int row;
            int landDelta;
            Sprite sprite;
            Color fallbackTint;
            switch (kind)
            {
                case UpgradeKind.Attack:
                    row = 2; landDelta = _tuning.GainAttack; sprite = _attackIconSprite;
                    fallbackTint = new Color(0.95f, 0.60f, 0.20f);
                    break;
                case UpgradeKind.Shield:
                    row = 1; landDelta = _tuning.GainShield; sprite = _shieldIconSprite;
                    fallbackTint = new Color(0.35f, 0.55f, 0.90f);
                    break;
                case UpgradeKind.RunHeal:
                    row = 0; landDelta = 0; sprite = _runHealIconSprite; // green heal heart (사용자 지정 2026-08-10)
                    fallbackTint = new Color(0.45f, 0.95f, 0.5f);
                    break;
                default: // MaxHealth
                    row = 0; landDelta = _tuning.GainMaxHealth; sprite = _healthIconSprite;
                    fallbackTint = new Color(0.90f, 0.30f, 0.30f);
                    break;
            }

            SpawnIconFlight(sprite, fallbackTint, from, _statRowIconPos[row], start, () => ApplySettleLand(row, landDelta));
        }

        private void ApplySettleLand(int row, int delta)
        {
            if (row == 0)
            {
                _shownMaxHealth += delta;
            }
            else if (row == 1)
            {
                _shownShield += delta;
            }
            else
            {
                _shownAttack += delta;
            }
        }

        // Drives the settle icons; once every one has landed the next run loads after a short beat.
        private void TickLeaveFlights()
        {
            if (!_leaving)
            {
                return;
            }

            if (TickIconFlights(Time.deltaTime))
            {
                return;
            }

            _leaveLoadTimer += Time.deltaTime;
            if (_leaveLoadTimer >= SettleLoadBeat)
            {
                _leaving = false; // one shot: the load below tears the scene down
                GameFlow.LoadBattle();
            }
        }

        // Fills every slot afresh, bought-out ones included (design doc 5.3), each slot drawn from the
        // five offer weights (사용자 지정 2026-08-10: 공격 10%·쉴드 30%·최대체력 30%·회복 25%·전투
        // 스톤 5%), normalised by their sum so live tuning cannot break the draw. View-level
        // UnityEngine.Random — the sim's determinism is untouched.
        private void RerollOffers()
        {
            _shopOffers.Clear();
            for (int i = 0; i < ShopOfferSlots; i++)
            {
                float total = _tuning.OfferAttackChance + _tuning.OfferShieldChance
                    + _tuning.OfferMaxHealthChance + _tuning.OfferRunHealChance + _tuning.OfferBattleStoneChance;
                if (total <= 0f)
                {
                    // Every weight zeroed out (a live-tuning corner): fall back to a plain uniform cell.
                    _shopOffers.Add(new ShopOffer { Type = OfferType.Cell, Kind = (UpgradeKind)Random.Range(0, 4) });
                    continue;
                }

                float r = Random.value * total;
                if ((r -= _tuning.OfferBattleStoneChance) < 0f)
                {
                    _shopOffers.Add(new ShopOffer { Type = OfferType.BattleStone });
                }
                else if ((r -= _tuning.OfferAttackChance) < 0f)
                {
                    _shopOffers.Add(new ShopOffer { Type = OfferType.Cell, Kind = UpgradeKind.Attack });
                }
                else if ((r -= _tuning.OfferShieldChance) < 0f)
                {
                    _shopOffers.Add(new ShopOffer { Type = OfferType.Cell, Kind = UpgradeKind.Shield });
                }
                else if ((r -= _tuning.OfferMaxHealthChance) < 0f)
                {
                    _shopOffers.Add(new ShopOffer { Type = OfferType.Cell, Kind = UpgradeKind.MaxHealth });
                }
                else
                {
                    _shopOffers.Add(new ShopOffer { Type = OfferType.Cell, Kind = UpgradeKind.RunHeal });
                }
            }
        }

        // Every reroll costs the same flat price (사용자 지정 2026-08-10: 5G 고정, 가격 상승 없음).
        private int RerollPrice()
        {
            return _tuning.RerollBasePrice;
        }

        // The panel's reroll button: every slot is redrawn, sold ones included (design doc 5.3).
        private void TryReroll()
        {
            int price = RerollPrice();
            if (Campaign.Gold < price)
            {
                return;
            }

            Campaign.Gold -= price;
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

        // 스톤 추가하기 price: the base plus a permanent step per stone already bought (사용자 지정
        // 2026-08-10: 15G 시작, 구매마다 +15G — the climb outlives the visit, until defeat resets it).
        private int StonePrice => _tuning.ShopStonePrice + _tuning.ShopStonePriceStep * Campaign.ExtraShopStones;

        // A paid stone, permanent for the campaign (사용자 지정 2026-08-10): straight into this visit's
        // hand and onto the entry edge if the throwing phase is already on, and counted into every later
        // visit's starting stones. Each buy steps the price up for good.
        private void BuyStone()
        {
            // The capacity guard backs the affordability check: the price is live-tunable, so a price
            // lowered mid-visit could otherwise afford more stones than the view arrays were sized for.
            int price = StonePrice;
            if (Campaign.Gold < price || _shopStonesTotal >= _stoneCapacity)
            {
                return;
            }

            Campaign.Gold -= price;
            Campaign.ExtraShopStones++;
            int id = _shopStonesTotal; // roster ids run 0..capacity-1; this is the next unused one
            _shopStonesTotal++;
            _shopStonesLeft++;
            _handReady[0].Add(id);
            // Mid-throw, the new stone steps up via Update's at-rest gate like any other.
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
            // board has no damage cells. The first stone steps up via Update's at-rest gate.
            ClearGhost();
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
                _shopPanelSprite, _closeButtonSprite, _rerollButtonSprite, _goldIconSprite);
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

            // The carry guide (사용자 지정 2026-08-10): while a bought cell rides the cursor, a big
            // Korean line left of the board — vertically on the board's centre — says right-click
            // cancels the purchase. Replaces the old English IMGUI hint along the top.
            _pendingGuideText = SideText(transform, "PendingGuide", "우클릭으로\n구매 취소",
                new Vector2(-17f, 0f), new Vector2(8.5f, 7f), TextAlignmentOptions.Center, 16f, 15);
            _pendingGuideText.color = new Color(1f, 0.92f, 0.55f); // the merchant label's warm yellow
            _pendingGuideText.gameObject.SetActive(false);

            BuildShopSideUi();
        }

        // One bought cell's stack gauge and level sparkles: the ticks along the bottom fill with each
        // same-kind placement, and the sparkle count equals the cell's level (사용자 지정) — the face
        // art bakes the first ones (one per face; two on the lv2+ attack/shield faces), extras continue
        // the top-left row here, capped at MaxExtraSparkles for display. Hovering a same-kind purchase
        // over the cell blinks the tick that placement would fill — all of them when it would complete
        // the gauge, the level-up tease (사용자 지정 2026-08-10).
        private void UpdateCellProgress(int index, ShopCell cell, Vector2 center, bool stackHover)
        {
            bool blinkOn = stackHover && Mathf.FloorToInt(Time.time * 4f) % 2 == 0;
            bool wouldLevelUp = cell.Xp >= ShopBoard.XpPerLevel - 1;
            SpriteRenderer[] ticks = _shopCellXpTicks[index];
            for (int t = 0; t < ticks.Length; t++)
            {
                ticks[t].enabled = !cell.IsEmpty;
                if (!cell.IsEmpty)
                {
                    Color tickColor = t < cell.Xp ? XpTickFilled : XpTickEmpty;
                    if (blinkOn && (wouldLevelUp || t == cell.Xp))
                    {
                        tickColor = XpTickBlink;
                    }

                    ticks[t].color = tickColor;
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
        // The player standing top-centre (사용자 지정 2026-08-10), battle's hero treatment: scaled to the
        // battle height, feet on the shared line, idle at half speed. Missing art skips the body but the
        // stat rows still build — they are the settlement flight's target.
        private void BuildShopHero()
        {
            if (_heroBodyPrefab != null)
            {
                GameObject go = Instantiate(_heroBodyPrefab, transform, false);
                go.name = "Hero";
                SpriteRenderer sr = go.GetComponentInChildren<SpriteRenderer>();
                if (sr == null || sr.sprite == null)
                {
                    Debug.LogError("[PuckHero] Hero prefab has no usable SpriteRenderer — the shop shows stat rows only.");
                    Destroy(go);
                }
                else
                {
                    sr.sortingOrder = 10;
                    sr.color = Color.white;
                    if (go.TryGetComponent(out Animator animator))
                    {
                        animator.speed = 0.5f; // the battle idle's pace
                    }

                    go.transform.localPosition = new Vector3(HeroX, CharFeetY, 0f);
                    go.transform.localScale *= HeroBodyHeight / sr.bounds.size.y;
                    Vector3 target = transform.TransformPoint(new Vector3(HeroX, CharFeetY + HeroBodyHeight * 0.5f, 0f));
                    go.transform.position += target - sr.bounds.center;
                }
            }

            BuildHeroStatRows();
        }

        // Battle's stat column, mirrored for the lone player: [icon] [number] per row, icons fitted by
        // bounds (import pivots sit below the art), placeholder squares where the sheet is unwired.
        private void BuildHeroStatRows()
        {
            Sprite[] icons = { _healthIconSprite, _shieldIconSprite, _attackIconSprite, _stoneIconSprite };
            Color[] placeholderTints =
            {
                new Color(0.90f, 0.30f, 0.30f),
                new Color(0.35f, 0.55f, 0.90f),
                new Color(0.95f, 0.60f, 0.20f),
                new Color(0.75f, 0.75f, 0.75f),
            };

            _statRowTexts = new TextMeshPro[StatRowCount];
            _statRowIconPos = new Vector2[StatRowCount];
            for (int row = 0; row < StatRowCount; row++)
            {
                float y = StatBlockBottom + (StatRowCount - row - 0.5f) * StatRowHeight;
                Vector2 iconPos = new Vector2(HeroX - 1.4f, y);
                _statRowIconPos[row] = iconPos;

                GameObject iconGo = new GameObject($"HeroStatIcon{row}");
                iconGo.transform.SetParent(transform, false);
                iconGo.transform.localPosition = new Vector3(iconPos.x, iconPos.y, 0f);
                SpriteRenderer icon = iconGo.AddComponent<SpriteRenderer>();
                icon.sortingOrder = 12;
                if (icons[row] != null)
                {
                    icon.sprite = icons[row];
                    iconGo.transform.localScale = Vector3.one * (0.9f / icon.bounds.size.y);
                    iconGo.transform.position += transform.TransformPoint(new Vector3(iconPos.x, iconPos.y, 0f)) - icon.bounds.center;
                }
                else
                {
                    icon.sprite = ProceduralSprites.Unit();
                    icon.color = placeholderTints[row];
                    iconGo.transform.localScale = new Vector3(0.8f, 0.8f, 1f);
                }

                _statRowTexts[row] = SideText(transform, $"HeroStatValue{row}", "",
                    new Vector2(HeroX + 0.85f, y), new Vector2(3.6f, StatRowHeight), TextAlignmentOptions.MidlineLeft, 8f, 12);
            }
        }

        // Live campaign values each frame; during the leave flight the shown numbers are frozen at their
        // pre-settle values and tick up only as icons land.
        private void UpdateShopHeroStats()
        {
            if (_statRowTexts == null)
            {
                return;
            }

            int maxHealth = _leaving ? _shownMaxHealth : _tuning.PlayerBaseHealth + Campaign.BonusMaxHealth;
            int shield = _leaving ? _shownShield : _tuning.PlayerBaseShield + Campaign.BonusShield;
            int attack = _leaving ? _shownAttack : _tuning.PlayerBaseAttack + Campaign.BonusAttack;
            int current = Campaign.NextRunHealth == 0 ? maxHealth : Mathf.Min(Campaign.NextRunHealth, maxHealth);

            // Live settlement preview (사용자 지정 2026-08-10): what leaving right now would buy, from
            // where the stones sit — blue beside the stat, gone during the leave flight (the numbers
            // tick up for real then). RunHeal has no row of its own and stays out.
            string hpSuffix = "";
            string shieldSuffix = "";
            string attackSuffix = "";
            if (!_leaving)
            {
                UpgradeTotals pending = Campaign.ShopBoard.SumUpgrades(_sim, OccupancyThreshold);
                int gainMax = pending.MaxHealth * _tuning.GainMaxHealth;
                int gainShield = pending.Shield * _tuning.GainShield;
                int gainAttack = pending.Attack * _tuning.GainAttack;
                if (gainMax > 0)
                {
                    hpSuffix = $" <color=#5AB4FF>+{gainMax}</color>";
                }

                if (gainShield > 0)
                {
                    shieldSuffix = $" <color=#5AB4FF>+{gainShield}</color>";
                }

                if (gainAttack > 0)
                {
                    attackSuffix = $" <color=#5AB4FF>+{gainAttack}</color>";
                }
            }

            _statRowTexts[0].text = current + "/" + maxHealth + hpSuffix;
            _statRowTexts[1].text = "x " + shield + shieldSuffix;
            _statRowTexts[2].text = "x " + attack + attackSuffix;
            _statRowTexts[3].text = "x " + (_tuning.PlayerStoneCount + Campaign.ExtraBattleStones);
        }

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
        // its base on the field line (사용자 지정), refreshed every frame in UpdateShopSideUi. The coin
        // art replaces the "G" suffix (사용자 지정 2026-08-10).
        private void BuildGoldReadout()
        {
            SideRect(transform, "GoldReadoutBg", _goldPanelSprite, new Vector2(31.3f, 15.35f), new Vector2(8.4f, 3.9f),
                _goldPanelSprite != null ? Color.white : new Color(0.28f, 0.33f, 0.44f), 15);
            SideRect(transform, "GoldReadoutCoin", _goldIconSprite != null ? _goldIconSprite : ProceduralSprites.Circle(),
                new Vector2(28.7f, 15.3f), new Vector2(1.4f, 1.4f),
                _goldIconSprite != null ? Color.white : new Color(1f, 0.823f, 0.29f), 16);
            _goldReadoutText = SideText(transform, "GoldReadoutText", "0", new Vector2(32.45f, 15.3f),
                new Vector2(4.5f, 3.0f), TextAlignmentOptions.MidlineLeft, 10f, 16);
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

            bool clickable = !_shopThrowing && !_merchantOpen && !_confirmReplaceOpen && !_leaveConfirmOpen;
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
            SideRect(root, "BuyPriceCoin", _goldIconSprite != null ? _goldIconSprite : ProceduralSprites.Circle(),
                new Vector2(SideX - 1.5f, -2.4f), new Vector2(1.2f, 1.2f),
                _goldIconSprite != null ? Color.white : new Color(1f, 0.823f, 0.29f), 16);
            _buyPriceText = SideText(root, "BuyPrice", "0", new Vector2(SideX + 1.55f, -2.4f),
                new Vector2(3.1f, 2.6f), TextAlignmentOptions.MidlineLeft, 10f, 16);

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
                _goldReadoutText.text = Campaign.Gold.ToString();
            }

            bool shown = !_merchantOpen && !_confirmReplaceOpen && !_leaveConfirmOpen;
            if (_sideRoot.activeSelf != shown)
            {
                _sideRoot.SetActive(shown);
            }

            if (!shown)
            {
                return;
            }

            _stoneCountText.text = "x " + _shopStonesLeft;

            // The leave flight and the ESC menu freeze the column: no hovers, no clicks.
            if (_leaving || _pauseMenuOpen)
            {
                _rollOutline.enabled = false;
                _buyOutline.enabled = false;
                _leaveOutline.enabled = false;
                return;
            }

            bool canRoll = !_shopThrowing;
            bool canBuy = Campaign.Gold >= StonePrice && _shopStonesTotal < _stoneCapacity;
            // Settlement reads where the stones ARE, so leaving mid-flight would freeze them wherever
            // they happened to be that frame. The way out only opens once the board has settled.
            bool canLeave = _sim.AllAtRest();
            _rollBg.color = canRoll ? Color.white : SideDimmed;
            _buyBg.color = canBuy ? Color.white : SideDimmed;
            _leaveBg.color = canLeave ? Color.white : SideDimmed;
            _buyPriceText.text = StonePrice.ToString();
            _buyPriceText.color = Campaign.Gold >= StonePrice
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
                // Always through the dialog now (사용자 지정 2026-08-10): it reports the settlement's
                // buffs, warns about unrolled stones, and [전투로 이동] is what actually leaves.
                SetLeaveConfirmOpen(true);
            }
        }

        // The leave-confirm dialog (사용자 지정 2026-08-10), in the difficulty picker's dress: thin gold
        // frame, dark box, gold title — parked on the camera centre like the pause menu. Under the title
        // sits the settlement's buff row as icons (사용자 지정: 글 말고 아이콘), then the warning line.
        private void BuildLeaveConfirm()
        {
            _leaveConfirmRoot = new GameObject("LeaveConfirm");
            _leaveConfirmRoot.transform.SetParent(transform, false);
            _leaveConfirmRoot.transform.localPosition = new Vector3(0f, 6.25f, 0f);
            Transform root = _leaveConfirmRoot.transform;

            Vector2 panelSize = new Vector2(24f, 16f);
            SideRect(root, "Frame", null, Vector2.zero, panelSize + new Vector2(0.3f, 0.3f), new Color(0.72f, 0.55f, 0.22f), 40);
            SideRect(root, "PanelBg", null, Vector2.zero, panelSize, new Color(0.08f, 0.10f, 0.15f, 0.97f), 41);

            TextMeshPro title = SideText(root, "Title", "상점을 떠나시겠습니까?", new Vector2(0f, 5.8f),
                new Vector2(20f, 2.2f), TextAlignmentOptions.Center, 8f, 45);
            title.color = new Color(1f, 0.85f, 0.35f);

            // The settlement's buff row: [icon] +N per kind, the hero rows' icons at dialog scale.
            Sprite[] gainIcons = { _attackIconSprite, _shieldIconSprite, _healthIconSprite, _runHealIconSprite };
            Color[] gainTints =
            {
                new Color(0.95f, 0.60f, 0.20f),
                new Color(0.35f, 0.55f, 0.90f),
                new Color(0.90f, 0.30f, 0.30f),
                new Color(0.45f, 0.95f, 0.5f),
            };
            for (int i = 0; i < 4; i++)
            {
                float x = -7.8f + 5.2f * i;
                SideRect(root, $"GainIcon{i}", gainIcons[i] != null ? gainIcons[i] : ProceduralSprites.Unit(),
                    new Vector2(x - 0.9f, 3.4f), new Vector2(1.3f, 1.3f),
                    gainIcons[i] != null ? Color.white : gainTints[i], 45);
                _leaveGainTexts[i] = SideText(root, $"GainValue{i}", "+0", new Vector2(x + 1.7f, 3.4f),
                    new Vector2(2.7f, 1.8f), TextAlignmentOptions.MidlineLeft, 7f, 45);
            }

            _leaveConfirmBody = SideText(root, "Body", "", new Vector2(0f, 1.7f),
                new Vector2(21f, 1.4f), TextAlignmentOptions.Center, 6f, 45);

            Vector2 buttonSize = new Vector2(17.6f, 3.4f);
            _leaveGoOutline = SideRect(root, "GoOutline", null, new Vector2(0f, -1.0f), buttonSize + new Vector2(0.4f, 0.4f), SideOutlineTint, 42);
            _leaveGoOutline.enabled = false;
            _leaveGoBg = SideRect(root, "GoBg", null, new Vector2(0f, -1.0f), buttonSize, new Color(0.28f, 0.33f, 0.44f), 43);
            SideText(root, "GoLabel", "전투로 이동", new Vector2(0f, -1.0f), new Vector2(16f, 2.6f), TextAlignmentOptions.Center, 7f, 45);

            _leaveBackOutline = SideRect(root, "BackOutline", null, new Vector2(0f, -5.2f), buttonSize + new Vector2(0.4f, 0.4f), SideOutlineTint, 42);
            _leaveBackOutline.enabled = false;
            _leaveBackBg = SideRect(root, "BackBg", null, new Vector2(0f, -5.2f), buttonSize, new Color(0.28f, 0.33f, 0.44f), 43);
            SideText(root, "BackLabel", "뒤로가기", new Vector2(0f, -5.2f), new Vector2(16f, 2.6f), TextAlignmentOptions.Center, 7f, 45);

            _leaveConfirmRoot.SetActive(false);
        }

        private void SetLeaveConfirmOpen(bool open)
        {
            if (open && _leaveConfirmRoot == null)
            {
                BuildLeaveConfirm();
            }

            _leaveConfirmOpen = open;
            if (_leaveConfirmRoot != null)
            {
                _leaveConfirmRoot.SetActive(open);
            }
        }

        // Hover, clicks and ESC for the leave-confirm dialog. Only [전투로 이동] leaves; [뒤로가기] and
        // ESC return to the board with everything as it was (사용자 지정).
        private void UpdateLeaveConfirm()
        {
            if (!_leaveConfirmOpen || _leaveConfirmRoot == null)
            {
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                SetLeaveConfirmOpen(false);
                return;
            }

            // What leaving right now buys — the same math as the settlement (사용자 지정: 아이콘 옆 수치).
            UpgradeTotals totals = Campaign.ShopBoard.SumUpgrades(_sim, OccupancyThreshold);
            _leaveGainTexts[0].text = "+" + totals.Attack * _tuning.GainAttack;
            _leaveGainTexts[1].text = "+" + totals.Shield * _tuning.GainShield;
            _leaveGainTexts[2].text = "+" + totals.MaxHealth * _tuning.GainMaxHealth;
            _leaveGainTexts[3].text = "+" + totals.RunHeal * _tuning.GainRunHeal;

            _leaveConfirmBody.text = _shopStonesLeft > 0
                ? $"아직 굴리지 않은 스톤이 {_shopStonesLeft}개 남아 있습니다."
                : "";

            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            Vector2 screen = mouse.position.ReadValue();
            Vector2 world = ScreenToWorld(screen);
            bool blocked = PointerOverShopGui(screen);
            bool hoverGo = !blocked && InBounds(_leaveGoBg, world);
            bool hoverBack = !blocked && InBounds(_leaveBackBg, world);
            _leaveGoOutline.enabled = hoverGo;
            _leaveBackOutline.enabled = hoverBack;

            if (!mouse.leftButton.wasPressedThisFrame)
            {
                return;
            }

            if (hoverGo)
            {
                SetLeaveConfirmOpen(false);
                LeaveShop();
            }
            else if (hoverBack)
            {
                SetLeaveConfirmOpen(false);
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
            // Mid-throw rebuilds (physics sliders) re-stage via Update's at-rest gate.
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

            // Which cell the carried purchase would stack onto (same kind under the cursor): its gauge
            // blinks below as the level-up tease (사용자 지정 2026-08-10).
            int stackHoverIndex = -1;
            if (_hasPendingCell && !_merchantOpen && !_confirmReplaceOpen && !_leaveConfirmOpen && Mouse.current != null)
            {
                Vector2 hoverScreen = Mouse.current.position.ReadValue();
                if (!PointerOverShopGui(hoverScreen)
                    && BoardCellAt(ScreenToWorld(hoverScreen), out int stackCol, out int stackRow)
                    && Campaign.ShopBoard.Preview(stackCol, stackRow, _pendingCell) == ShopPlacement.Upgraded)
                {
                    stackHoverIndex = stackCol + stackRow * BoardCells.Size;
                }
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

                    UpdateCellProgress(index, cell, center, index == stackHoverIndex);
                }
            }

            _pendingCellGhost.enabled = false;
            bool carrying = _hasPendingCell && !_merchantOpen && !_confirmReplaceOpen && !_leaveConfirmOpen;
            if (_pendingGuideText != null && _pendingGuideText.gameObject.activeSelf != carrying)
            {
                _pendingGuideText.gameObject.SetActive(carrying); // the right-click guide rides the carry
            }

            if (!carrying)
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

            if (BoardCellAt(ScreenToWorld(screen), out int hoverCol, out int hoverRow))
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
            if (mouse == null || _merchantOpen || _confirmReplaceOpen || _leaveConfirmOpen || _leaving || _pauseMenuOpen)
            {
                return; // the modals block clicks through them; the leave flight locks the board entirely
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

                if (mouse.leftButton.wasPressedThisFrame && !overGui && BoardCellAt(world, out int col, out int row))
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
                        // The next stone steps up in Update, once the board is at rest (사용자 지정
                        // 2026-08-10) — no more rolling over a still-moving shot.
                    }
                }

                _aimingPuckId = -1;
            }
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

            return gui.y <= 90f; // the reserved strip along the top (the old IMGUI hint's home)
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
                DrawMerchantTooltip();
                return;
            }

            // The leave-confirm panel is world-space sprites; IMGUI draws nothing under it so no board
            // tooltip bleeds over the dialog.
            if (_leaveConfirmOpen)
            {
                return;
            }

            // Gold lives on the world-space signpost readout now, the stone count and the shop controls
            // in the world-space side column, and the carry hint is the world-space guide left of the
            // board (사용자 지정 2026-08-10); IMGUI keeps only the tooltips.
            DrawBoardCellTooltip();
        }

        // Kind, level and per-stone effect of the bought board cell under the cursor (사용자 지정
        // 2026-08-10: 수치 표기 — 칸 레벨 × Gain, 튜닝 연동이라 거짓말하지 않는다) — quiet on empty
        // cells, over the GUI, and mid-drag where it would only chase the aim.
        private void DrawBoardCellTooltip()
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
            if (PointerOverShopGui(screen) || !BoardCellAt(ScreenToWorld(screen), out int col, out int row))
            {
                return;
            }

            ShopCell cell = Campaign.ShopBoard.CellAt(col, row);
            if (cell.IsEmpty)
            {
                return;
            }

            DrawCursorTooltip($"{KoreanUpgradeName(cell.Kind)} 칸 lv{cell.Level}\n해당 칸에 올라간 스톤 수 만큼 {KoreanUpgradeEffect(cell.Kind)} +{cell.Level * GainOf(cell.Kind)}");
        }

        // The cursor-following tooltip for the offer the panel reports as hovered — the one piece of the
        // buying screen still on IMGUI, so it can sit beside the cursor for free.
        private void DrawMerchantTooltip()
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
                : $"{KoreanUpgradeName(offer.Kind)} 칸 lv1\n해당 칸에 올라간 스톤 수 만큼 {KoreanUpgradeEffect(offer.Kind)} +{GainOf(offer.Kind)}";

            DrawCursorTooltip(text);
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
