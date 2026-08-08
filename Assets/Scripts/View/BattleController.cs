using System.Collections.Generic;
using Puckmite.Game;
using Puckmite.Sim;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Puckmite.View
{
    /// <summary>
    /// The battle scene: one run of the campaign. Turn order over individual actors, the character row and
    /// its combat, buff snapshots, damage-cell settlement, the enemy AI, and win/lose — all view-only
    /// orchestration on top of the shared arena (the sim stays pure physics/combat). A cleared run leads to
    /// the shop scene; a defeat resets the campaign (design doc 2.1).
    /// </summary>
    public sealed class BattleController : ArenaControllerBase
    {
        // Character row (design doc 2.2: player + enemy characters across the top, board below), laid out
        // like the reference shot: everyone stands on one feet line floating just above the board (a ground
        // strip can be wired under it), and each actor's stat column stacks over its head. The columns
        // start above the tallest body (the hero art) so every column tops out level.
        private const float CharFeetY = BoardHalf + 0.8f; // shared feet line, slightly above the board top
        private const float CharBodyHeight = 6.4f;   // hero art height; also sizes its ring and hit disc
        private const float CharBodyRadius = 1.6f;   // placeholder circle radius (enemies, until their art)
        private const float CharSpread = 9f;         // x of the leftmost/rightmost character
        private const float StatRowHeight = 1.1f;    // one icon-and-number row
        private const int StatRowCount = 4;          // top to bottom: health, shield, attack, stones
        private const float StatBlockBottom = CharFeetY + CharBodyHeight + 0.4f;

        // Cell-occupancy highlights: up to 4 cells per stone (radius 1.5 on 5-wide cells). The pool is
        // sized from the roster at build — the twin kind and bought battle stones outgrew a fixed cap.
        private const int MaxCellsPerStone = 4;

        // The enemy-hover link ring (design doc 4.1), outside the other rings so a stone can wear both at
        // once (its owner's turn ring and the hover link) and still read as two.
        private static readonly Color RingHover = new Color(1f, 0.25f, 0.20f, 0.9f);
        private const float HoverRingScale = 1.55f;

        // The rect the game HUD actually draws. Only this (and the open debug panel) may block board
        // clicks — anything more would leave invisible dead strips (the shop learned this the hard way).
        private static Rect GameHudRect => new Rect(10f, 10f, 320f, 200f);

        // Difficulty presets, indexed by the tuning's AiDifficulty (0 아주쉬움 .. 4 매우어려움). Aim density
        // rises with difficulty; the pick drops from mid-ranking shots to the best; only the top tier gets
        // the exact cascade prediction — everything below sees what the player's preview sees (design doc 8.4).
        public static readonly string[] AiDifficultyNames = { "V.easy", "Easy", "Normal", "Hard", "V.hard" };
        private static readonly int[] AiCueDirections = { 8, 12, 16, 24, 24 };
        private static readonly int[] AiEntryDirections = { 6, 6, 8, 10, 10 };
        private static readonly float[] AiPickRank = { 0.75f, 0.5f, 0.25f, 0f, 0f };
        private static readonly bool[] AiFullRollout = { false, false, false, false, true };
        private static readonly float[][] AiPowerFractions =
        {
            new[] { 0.4f, 0.8f },
            new[] { 0.4f, 0.8f },
            new[] { 0.4f, 0.7f, 1f },
            new[] { 0.3f, 0.55f, 0.8f, 1f },
            new[] { 0.3f, 0.55f, 0.8f, 1f },
        };

        private SpriteRenderer[] _turnRings;  // highlight behind the current actor's stones
        private SpriteRenderer[] _hoverRings; // links a hovered enemy to its stones (design doc 4.1)
        private int _hoveredEnemyActor = -1;  // enemy under the cursor, by character or by stone; -1 = none
        private readonly List<int> _settleIds = new List<int>(); // reused: current actor's stone ids to settle

        // Hero art for the player's slot in the character row: the prefab Unity generates from
        // Hero.aseprite (its Animator loops the idle clip on its own). Wired by Tools/PuckHero/Setup
        // Game Scenes; when it is missing the slot falls back to the placeholder circle.
        [SerializeField] private GameObject _heroBodyPrefab;
        private bool _heroBodyUsesArt; // art keeps a white alive-tint (ActorColor would stain it)

        // Optional art slots, wired by Tools/PuckHero/Setup Game Scenes from promised paths under
        // Assets/Art/Sprites/ (Environment/Ground, UI/StatHealth·StatShield·StatAttack·StatStones; .png
        // or .aseprite). Placeholders render until the files exist.
        [SerializeField] private Sprite _groundSprite;
        [SerializeField] private Sprite _healthIconSprite;
        [SerializeField] private Sprite _shieldIconSprite;
        [SerializeField] private Sprite _attackIconSprite;
        [SerializeField] private Sprite _stoneIconSprite;

        // Flat-colour silhouette material (PuckHero/SpriteSilhouette) behind the character outline.
        // Created and wired by Setup Game Scenes; as a material asset it also survives shader stripping
        // in builds, which a runtime Shader.Find would not.
        [SerializeField] private Material _silhouetteMaterial;

        // Victory panel art (promised paths UI/VictoryPanel·VictoryButton·Gold — 디자인은 사용자가 추후).
        [SerializeField] private Sprite _victoryPanelSprite;
        [SerializeField] private Sprite _victoryButtonSprite;
        [SerializeField] private Sprite _goldIconSprite;

        private VictoryPanel _victoryPanel;
        private int _goldEarnedThisRun; // kill gold this run, for the victory panel's line and its double pick

        // Top character row (design doc 2.2). Each actor's buff is a snapshot taken when its own turn
        // ends (Σ cellValue*stoneLevel), held until its next turn (design doc 3.6/3.7).
        private TextMeshPro[][] _statRowTexts;     // [actor][row]: the number next to each stat icon
        private SpriteRenderer[][] _statRowIcons;  // [actor][row]: wired art, or a tinted placeholder square
        private SpriteRenderer[] _characterBodies;      // indexed by actor, greyed out when the character is down
        private SpriteRenderer[][] _characterOutlines;  // [actor]: silhouette ghosts forming its highlight outline
        private float[] _characterCenterY;    // per-actor body centre — art and placeholder circles differ
        private float[] _characterGrabRadius; // per-actor click/hover disc around that centre
        private int[] _actorBuffAttack;            // actor -> attack buff snapshot (0 = base only)

        // Character combat (design doc 3.6/3.8) — view-only; the sim stays pure physics and stone combat.
        private int[] _actorHealth;       // current character health
        private int[] _actorBaseShield;   // base shield: run-scoped pool, never refills once spent
        private int[] _actorEffectShield; // effect shield: refilled by the turn-end buff, spent when hit
        private bool[] _actorDead;
        private bool _awaitingAttack;     // the player's turn is at step 4: pick a target (design doc 3.5)
        private int _hoveredCharacter = -1; // actor under the cursor while picking a target, -1 for none
        private string _attackLog = "";   // last attack resolved, shown in the HUD
        private bool _gameOver;
        private string _gameOverText = "";
        private readonly List<int> _removeIds = new List<int>(); // reused: stones of an actor that just died

        // A turn with nothing to roll: show that, then attack with base damage and end it (design doc 3.5).
        private bool _noStoneTurn;
        private float _noStoneTimer;

        // Enemy AI: think-pause countdown, then a planner search picks and fires the shot itself.
        private float _enemyThinkTimer;
        private float _aiPlanMs;       // last search time, shown in the debug panel
        private int _aiPlanCandidates; // last search size, shown in the debug panel
        private readonly List<int> _planOwnIds = new List<int>();         // reused per plan
        private readonly List<Vector2> _planEntrySpots = new List<Vector2>();

        // The enemy search runs off the main thread so a long think never freezes a frame (the sim is
        // pure C#). The task works exclusively on private copies — a Clone of the board and copied
        // lists — so nothing it reads can change under it; the result fires back on the main thread.
        private System.Threading.Tasks.Task<EnemyPlanOutcome> _planTask;
        private Puck _planTemplate; // the entering stone the running search was given, used when it fires

        private struct EnemyPlanOutcome
        {
            public bool Planned;
            public EnemyPlan Plan;
            public float Milliseconds;
        }

        private SpriteRenderer[] _cellHighlights;                    // pool of occupied-cell overlays
        private readonly List<int> _occupiedCells = new List<int>(); // reused per puck each frame

        private bool _campaignCleared;

        // Defeat and the campaign clear hand over to their end screens (사용자 지정) after this beat, so
        // the deciding blow is still readable on the board. A cleared RUN never sets the timer — there
        // the shop button is the way on.
        private const float EndScreenDelay = 1.5f;
        private float _endScreenTimer;

        // Stage-1 boss: the board-warping caster. It rolls no stones; each of its turns casts one random
        // ability instead, active until its next turn (StartTurn clears and re-casts). The dice are rolled
        // HERE in the view — the sim only receives the outcome, so it stays deterministic.
        private enum BossAbility
        {
            CorruptCells,
            Hole,
            DamageAll,
        }

        private readonly List<int> _debuffCells = new List<int>(); // corrupted inner-cell indices, this round
        private SpriteRenderer[] _buffCellViews; // the inner 3x3 quads, recoloured while corrupted
        private SpriteRenderer _holeView;        // dark quad over the hole cell while one is open
        private static readonly Color CorruptCellColor = new Color(0.36f, 0.10f, 0.16f);

        // Diagnostics and controls for the debug panel.
        public float AiPlanMs => _aiPlanMs;
        public int AiPlanCandidates => _aiPlanCandidates;
        public bool CanRestartRun => !_gameOver;

        private static CampaignState Campaign => GameFlow.Campaign;

        // Stage-1 boss (run 5 of stage 1). Stages 2-3 keep the ordinary-enemy placeholder until their own
        // bosses are built.
        private static bool IsStage1Boss => Campaign.IsBossRun && Campaign.Stage == 1;

        // The seven enemy kinds plus the plain one (design doc 4.3). Stats and stone specs are the user's
        // draft numbers, finalised in the balance pass (step 16); which kind appears where is rolled per
        // run HERE in the view, so the sim stays deterministic.
        private enum EnemyType
        {
            Basic,
            Striker,   // 강공형: hits hard, folds fast
            Tank,      // 중갑형: bulk over punch
            Twin,      // 쌍석형: fields two stones
            Sniper,    // 저격형: its rolls hunt the weakest player stone
            HardStone, // 강석형: stones carry +2 health
            Bomber,    // 자폭형: a bomb stone, rolled every second turn
            Anchor,    // 반석형: immovable stone that returns the blow in full, 2 health
        }

        private EnemyType[] _actorTypes;   // actor -> kind, rolled once per Build; [0] is the player, never read
        private int[] _actorTurnCounts;    // turns each actor has taken this run (the bomber's cadence)
        private readonly List<int> _rosterActors = new List<int>(); // actor of each roster entry, filled by InitialRoster
        private readonly List<int> _snipePriority = new List<int>(); // reused per sniper plan

        private int TypeBaseHealth(EnemyType type)
        {
            switch (type)
            {
                case EnemyType.Striker: return 5;  // 사용자 지정 HP 5 / ATK 5
                case EnemyType.Tank: return 20;    // 사용자 지정 HP 20 / ATK 2
                default: return _tuning.EnemyBaseHealth;
            }
        }

        private int TypeBaseAttack(EnemyType type)
        {
            switch (type)
            {
                case EnemyType.Striker: return 5;
                case EnemyType.Tank: return 2;
                default: return _tuning.EnemyBaseAttack;
            }
        }

        private static int TypeStoneCount(EnemyType type)
        {
            return type == EnemyType.Twin ? 2 : 1;
        }

        private int TypeStoneHealth(EnemyType type)
        {
            switch (type)
            {
                case EnemyType.HardStone: return _tuning.StoneHealth + 2;
                case EnemyType.Anchor: return Mathf.Max(1, _tuning.StoneHealth - 1); // 2 at the base 3 (사용자 지정)
                default: return _tuning.StoneHealth;
            }
        }

        private static StoneTrait TypeStoneTrait(EnemyType type)
        {
            switch (type)
            {
                case EnemyType.Sniper: return StoneTrait.Sniper;
                case EnemyType.Bomber: return StoneTrait.Bomb;
                case EnemyType.Anchor: return StoneTrait.Anchor;
                default: return StoneTrait.None;
            }
        }

        private static string TypeLabel(EnemyType type)
        {
            switch (type)
            {
                case EnemyType.Striker: return "Striker";
                case EnemyType.Tank: return "Tank";
                case EnemyType.Twin: return "Twin";
                case EnemyType.Sniper: return "Sniper";
                case EnemyType.HardStone: return "Hard";
                case EnemyType.Bomber: return "Bomber";
                case EnemyType.Anchor: return "Anchor";
                default: return "";
            }
        }

        // --- Scene wiring -------------------------------------------------------------------------

        protected override void BuildMode()
        {
            BuildBoard();
            BuildCellHighlights();
            BuildPuckViews();
            BuildTurnRings();
            BuildHoverRings();
            BuildCharacters();
            BuildVictoryPanel();
            BuildGhost();
            BuildPreviewLine();
            BuildPreviewMarker();
            StartTurn();
            UpdatePuckTransforms();
            UpdateCharacterStats();
        }

        // Every stone in the current run, with the actor of each entry recorded in _rosterActors in
        // lock-step (enemy kinds field different counts — the twin two, the stage-1 boss none — so the
        // one-stone-per-enemy mapping of the base class no longer holds). Battle stones bought in shops
        // add to the player's count until the campaign is lost (design doc 5.6).
        protected override List<Puck> InitialRoster()
        {
            List<Puck> roster = new List<Puck>();
            _rosterActors.Clear();

            int playerStones = _tuning.PlayerStoneCount + Campaign.ExtraBattleStones;
            for (int i = 0; i < playerStones; i++)
            {
                roster.Add(new Puck(roster.Count, Vector2.zero, _tuning.PuckRadius, 1f, PuckOwner.Player) { Health = _tuning.StoneHealth });
                _rosterActors.Add(0);
            }

            // The stage-1 boss fields no stones at all — its turns cast board-warping abilities instead.
            if (!IsStage1Boss)
            {
                int enemies = Campaign.EnemyCountForRun;
                for (int actor = 1; actor <= enemies; actor++)
                {
                    EnemyType type = _actorTypes[actor];
                    int stones = TypeStoneCount(type);
                    for (int s = 0; s < stones; s++)
                    {
                        roster.Add(new Puck(roster.Count, Vector2.zero, _tuning.PuckRadius, 1f, PuckOwner.Enemy)
                        {
                            Health = TypeStoneHealth(type),
                            Trait = TypeStoneTrait(type),
                        });
                        _rosterActors.Add(actor);
                    }
                }
            }

            return roster;
        }

        // The base mapping assumes one stone per enemy; here the roster carries its own actor list.
        protected override void AssignActors()
        {
            RollEnemyTypes();

            List<Puck> roster = InitialRoster(); // fills _rosterActors in lock-step
            _actorOf = new int[RosterMaxId(roster) + 1];
            for (int i = 0; i < roster.Count; i++)
            {
                _actorOf[roster[i].Id] = _rosterActors[i];
            }

            _actorCount = DeclaredActorCount();
            _currentActor = 0;
            _hasRolledThisTurn = false;
        }

        // Which kind each enemy actor is this run (design doc 4.3): plain enemies until stage 1 run 3,
        // the full pool from there on (사용자 지정). Boss runs stay out of the pool — stage 1 has its own
        // boss, stages 2-3 keep the plain placeholder. Rolled once per Build; the dice live in the view.
        private void RollEnemyTypes()
        {
            int enemies = Campaign.EnemyCountForRun;
            _actorTypes = new EnemyType[1 + enemies];
            for (int actor = 1; actor <= enemies; actor++)
            {
                _actorTypes[actor] = RollEnemyType(enemies);
            }
        }

        private EnemyType RollEnemyType(int enemiesThisRun)
        {
            if (Campaign.IsBossRun)
            {
                return EnemyType.Basic;
            }

            if (Campaign.Stage == 1 && Campaign.Run < 3)
            {
                return EnemyType.Basic;
            }

            // 자폭형 is never fielded alone (사용자 지정): a single-enemy run draws uniformly from the
            // other seven kinds — a roll landing on Bomber's slot takes the one value the shortened
            // range cannot reach, keeping every kind at 1/7.
            if (enemiesThisRun == 1)
            {
                int roll = Random.Range(0, 7);
                return roll == (int)EnemyType.Bomber ? EnemyType.Anchor : (EnemyType)roll;
            }

            return (EnemyType)Random.Range(0, 8); // uniform over Basic + the seven kinds
        }

        // Player + this run's enemies — declared, not derived from stones, so the stoneless boss still
        // gets its actor slot, its turn and its character widget.
        protected override int DeclaredActorCount()
        {
            return 1 + Campaign.EnemyCountForRun;
        }

        // Enemy hand stones carry their kind's health and trait (design doc 4.3).
        protected override Puck CreateHandStone(int actor, int id)
        {
            if (actor == 0)
            {
                return base.CreateHandStone(actor, id);
            }

            EnemyType type = _actorTypes[actor];
            return new Puck(id, Vector2.zero, _tuning.PuckRadius, 1f, PuckOwner.Enemy)
            {
                Health = TypeStoneHealth(type),
                Trait = TypeStoneTrait(type),
            };
        }

        // Health arcs are drawn against the stone's own maximum (강석형 5, 반석형 2 at the base 3).
        protected override int MaxStoneHealth(Puck p)
        {
            int actor = p.Id >= 0 && p.Id < _actorOf.Length ? _actorOf[p.Id] : 0;
            return actor == 0 || _actorTypes == null ? _tuning.StoneHealth : TypeStoneHealth(_actorTypes[actor]);
        }

        // Special stones wear their behaviour's colour so it reads before they ever move (design doc 4.3).
        protected override Color StoneColor(Puck p)
        {
            switch (p.Trait)
            {
                case StoneTrait.Sniper: return new Color(0.95f, 0.35f, 0.62f);
                case StoneTrait.Bomb: return new Color(1f, 0.72f, 0.20f);
                case StoneTrait.Anchor: return new Color(0.62f, 0.62f, 0.68f);
                default: return OwnerColor(p.Owner);
            }
        }

        // The entry edge: the player's new stones come in on the left, an enemy's on the right, hugging that
        // wall (design doc 3.4). The inset is the highlight ring's reach rather than the stone's radius, so
        // the ring is not cut off by the wall; it stays well inside PuckSim's wall clamp, so the stone does
        // not jump on its first step, and still sits squarely in the entry damage column. Only y varies — a
        // new stone slides along its own edge and no further.
        protected override Vector2 EntryPoint(int actor, float along)
        {
            float inset = _tuning.PuckRadius * RingRadiusScale;
            float x = actor == 0 ? _sim.BoardMin.x + inset : _sim.BoardMax.x - inset;
            float minY = _sim.BoardMin.y + inset;
            float maxY = _sim.BoardMax.y - inset;
            return new Vector2(x, Mathf.Clamp(along, minY, maxY));
        }

        protected override float EntryAlong(Vector2 world)
        {
            return world.y;
        }

        protected override void EntryAxisBounds(out float min, out float max)
        {
            min = _sim.BoardMin.y;
            max = _sim.BoardMax.y;
        }

        // A character that is out does not get its stones back.
        protected override void ReturnStoneToHand(int puckId)
        {
            if (puckId < 0 || puckId >= _actorOf.Length || _actorDead == null || _actorDead[_actorOf[puckId]])
            {
                return;
            }

            base.ReturnStoneToHand(puckId);
        }

        protected override void Update()
        {
            base.Update();

            // A decided campaign moves on to its end screen once the beat has passed.
            if (_endScreenTimer > 0f)
            {
                _endScreenTimer -= Time.deltaTime;
                if (_endScreenTimer <= 0f)
                {
                    if (_campaignCleared)
                    {
                        GameFlow.LoadGameClear();
                    }
                    else
                    {
                        GameFlow.LoadGameOver();
                    }

                    return;
                }
            }

            HandleInput();
            DriveSimulation();
            // Nothing to roll: after the beat that shows it, go straight to the attack (design doc 3.5).
            if (!_gameOver && _noStoneTurn)
            {
                _noStoneTimer -= Time.deltaTime;
                if (_noStoneTimer <= 0f)
                {
                    _noStoneTurn = false;
                    CaptureActorBuff(_currentActor); // no stones, so this is base stats only
                    if (!_gameOver) // corrupted shield cells can end the run inside the capture
                    {
                        BeginAttackPhase();
                    }
                }
            }

            // An enemy turn drives itself: after a short think pause, search for the best shot on a
            // background task (frames — and the idle animations — keep running), then fire the result.
            if (!_gameOver && _tuning.EnemyAiEnabled && _currentActor != 0 && !_actorDead[_currentActor]
                && !_hasRolledThisTurn && !_noStoneTurn && !_awaitingAttack && _sim.AllAtRest())
            {
                if (_planTask == null)
                {
                    // A domain reload during the search drops the task; the already-expired timer restarts it.
                    _enemyThinkTimer -= Time.deltaTime;
                    if (_enemyThinkTimer <= 0f)
                    {
                        StartEnemyPlanSearch();
                    }
                }
                else if (_planTask.IsCompleted)
                {
                    ApplyEnemyPlan();
                }
            }

            // Rolled and the board has settled: lock in the buff (step 3), then attack (step 4).
            if (!_gameOver && !_awaitingAttack && _hasRolledThisTurn && _sim.AllAtRest())
            {
                CaptureActorBuff(_currentActor);
                if (!_gameOver) // corrupted shield cells can end the run inside the capture
                {
                    BeginAttackPhase();
                }
            }

            UpdatePuckTransforms();
            UpdateCellHighlights();
            UpdateBossEffectVisuals();
            UpdateTurnHighlights();
            UpdateGhost();
            UpdateHoverHighlight(); // before the character row, which reads the hovered enemy
            UpdateCharacterStats();

            // The victory panel runs its own pointer work: board input is already dead (_gameOver).
            if (_victoryPanel != null && _victoryPanel.IsShown && Mouse.current != null)
            {
                Vector2 panelScreen = Mouse.current.position.ReadValue();
                _victoryPanel.Tick(Time.deltaTime, ScreenToWorld(panelScreen),
                    Mouse.current.leftButton.wasPressedThisFrame, PointerOverHud(panelScreen));
            }
        }

        // --- Turn structure -----------------------------------------------------------------------

        // Begins _currentActor's turn: hand stones lost earlier become playable, then its own stones settle
        // on the damage cells (design doc 3.5 step 1). Only a character that is down is skipped. An actor
        // with nothing to roll still takes its turn — it just goes straight to its attack.
        private void StartTurn()
        {
            _hasRolledThisTurn = false;
            _awaitingAttack = false;
            _noStoneTurn = false;
            _enemyThinkTimer = _tuning.EnemyThinkDelay;
            ClearGhost();

            for (int step = 0; step < _actorCount; step++)
            {
                if (!_actorDead[_currentActor])
                {
                    _actorTurnCounts[_currentActor]++; // the bomber's cadence counts taken turns only

                    PromoteHand(_currentActor);    // playable from this turn on (design doc 3.3)
                    ClearActorBuff(_currentActor); // turn start: back to base only (design doc 3.6)
                    SettleCurrentActor();          // stones lost here go back to the hand as pending

                    // The stage-1 boss rolls nothing: last round's board warp expires now ("until its next
                    // turn"), a fresh ability is cast, and the no-stone beat carries the turn into its
                    // forced attack.
                    if (_currentActor != 0 && IsStage1Boss)
                    {
                        ClearBossEffects();
                        CastBossAbility();
                        _noStoneTurn = true;
                        _noStoneTimer = _tuning.NoStoneTurnDelay;
                        return;
                    }

                    // 자폭형 (design doc 4.3): its bomb flies only every second turn — on the off turns it
                    // holds and just takes its forced attack (the no-stone beat carries the turn there).
                    if (_currentActor != 0 && _actorTypes[_currentActor] == EnemyType.Bomber
                        && _actorTurnCounts[_currentActor] % 2 == 0)
                    {
                        _noStoneTurn = true;
                        _noStoneTimer = _tuning.NoStoneTurnDelay;
                        _attackLog = $"{ActorName(_currentActor)} holds its bomb — attacking.";
                        return;
                    }

                    // A hand stone only counts as a move if somewhere on the edge is actually free — with no
                    // board stone to cue either, a fully blocked edge would leave no legal roll and the turn
                    // could never end.
                    bool canEnter = _handReady[_currentActor].Count > 0 && HasFreeEntrySpot(_currentActor);
                    if (ActorHasLiveStones(_currentActor) || canEnter)
                    {
                        SetupGhost(_currentActor);
                        return;
                    }

                    // Nothing on the board and nothing playable in hand: no roll is possible. Hold a beat so
                    // that is visible, then attack with base damage and end the turn (design doc 3.5).
                    _noStoneTurn = true;
                    _noStoneTimer = _tuning.NoStoneTurnDelay;
                    _attackLog = $"{ActorName(_currentActor)} has no stones — attacking with base damage.";
                    return;
                }

                _currentActor = (_currentActor + 1) % _actorCount;
            }
            // Every character is down; leave the current actor as is.
        }

        // Moves to the next actor and begins its turn.
        private void AdvanceTurn()
        {
            DisarmSnipers(); // the armed 2-damage hit lives and dies with its owner's roll (design doc 4.3)
            _currentActor = (_currentActor + 1) % _actorCount;
            StartTurn();
        }

        // A sniper stone whose roll ended without a player contact must not carry the bonus into later
        // turns — the player striking it is always an ordinary 1.
        private void DisarmSnipers()
        {
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                if (pucks[i].Trait == StoneTrait.Sniper && pucks[i].SniperArmed)
                {
                    _sim.SetSniperArmed(pucks[i].Id, false);
                }
            }
        }

        // Applies one round of damage-cell settlement to the current actor's own stones (design doc 3.4/3.5).
        private void SettleCurrentActor()
        {
            _settleIds.Clear();
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                if (_actorOf[pucks[i].Id] == _currentActor)
                {
                    _settleIds.Add(pucks[i].Id);
                }
            }

            _sim.SettleDamageCells(_settleIds, _tuning.CellDamage, OccupancyThreshold);

            // SettleDamageCells reports nothing, so the stones it destroyed are the ids it was handed that
            // the sim no longer has. Those go back to their owner's hand (design doc 3.3).
            for (int i = 0; i < _settleIds.Count; i++)
            {
                if (!_sim.TryGetPuck(_settleIds[i], out Puck _))
                {
                    ReturnStoneToHand(_settleIds[i]);
                }
            }
        }

        private bool ActorHasLiveStones(int actor)
        {
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                if (_actorOf[pucks[i].Id] == actor)
                {
                    return true;
                }
            }

            return false;
        }

        private string ActorName(int actor)
        {
            if (actor == 0)
            {
                return "Player";
            }

            if (Campaign.IsBossRun)
            {
                // Stage-1 boss is real; stages 2-3 still wear the label over ordinary stats.
                return "Boss";
            }

            // The kind label doubles as the type indicator until real sprites arrive (사용자 예정).
            EnemyType type = _actorTypes != null && actor < _actorTypes.Length ? _actorTypes[actor] : EnemyType.Basic;
            return type == EnemyType.Basic ? $"Enemy {actor}" : $"Enemy {actor} ({TypeLabel(type)})";
        }

        // --- Input --------------------------------------------------------------------------------

        private void HandleInput()
        {
            _hoveredCharacter = -1; // recomputed below while an attack is pending
            _launchReady = false;   // recomputed below while aiming

            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            Vector2 screen = mouse.position.ReadValue();
            Vector2 world = ScreenToWorld(screen);

            if (_gameOver)
            {
                return;
            }

            // The AI owns the enemies' turns: keep the cursor from grabbing their stones or their ghost.
            // With the AI off this falls through to the existing hot-seat input.
            if (_tuning.EnemyAiEnabled && _currentActor != 0)
            {
                return;
            }

            // A scripted beat (a no-stone turn, the boss's cast, the bomber's hold) takes no input at all:
            // in hot-seat the bomber still has grabbable stones during its hold turn, and rolling one
            // would race the beat's timer into a mid-flight buff capture and a stolen roll.
            if (_noStoneTurn)
            {
                return;
            }

            // Turn step 4 (design doc 3.5): the player's attack is forced and hits one target — hover an
            // enemy character in the top row to highlight it, click to hit it. Rolling is locked out until
            // the attack is spent. Hover and click share one hit test, so only a ringed character is hittable.
            if (_awaitingAttack)
            {
                if (!PointerOverHud(screen))
                {
                    int target = CharacterAt(world);
                    if (target > 0 && !_actorDead[target])
                    {
                        _hoveredCharacter = target;
                        if (mouse.leftButton.wasPressedThisFrame)
                        {
                            ResolveAttack(_currentActor, target);
                            _awaitingAttack = false;
                            if (!_gameOver)
                            {
                                AdvanceTurn();
                            }
                        }
                    }
                }

                return;
            }

            // Skipping the roll (사용자 지정 — the forced roll of design doc 3.5 is now optional): before
            // rolling, clicking an enemy character attacks at once with the board exactly as it stands,
            // and the turn ends. Player only, and only while everything is at rest — a click while stones
            // are still moving must not fire this.
            if (_currentActor == 0 && !_hasRolledThisTurn && !_noStoneTurn && !_aiming
                && mouse.leftButton.wasPressedThisFrame && !PointerOverHud(screen) && _sim.AllAtRest())
            {
                int skipTarget = CharacterAt(world);
                if (skipTarget > 0 && !_actorDead[skipTarget])
                {
                    CaptureActorBuff(_currentActor); // "현재 보드 상태로" — the snapshot is taken right now
                    if (!_gameOver) // corrupted shield cells can end the run inside the capture
                    {
                        ResolveAttack(_currentActor, skipTarget);
                    }

                    if (!_gameOver)
                    {
                        AdvanceTurn();
                    }

                    return;
                }
            }

            UpdateGhostAim(world);

            if (mouse.leftButton.wasPressedThisFrame && !PointerOverHud(screen) && !_hasRolledThisTurn)
            {
                // A stone already on the board wins the click (design doc 3.5 — the actor picks one or the
                // other). The waiting new stone tracks the cursor along its edge, so it is always within
                // reach there; letting it win would make every stone in the entry column unselectable. It is
                // also not grabbable while its spot is blocked, since that click could never fire.
                int id = NearestPuckId(world);
                if (id >= 0)
                {
                    _aiming = true;
                    _aimingPuckId = id;
                }
                else if (GhostVisible() && !_ghostBlocked && (world - _ghost.Position).magnitude <= GrabRadius())
                {
                    _aiming = true;
                    _aimingPuckId = _ghost.Id;
                }
            }

            // Right-click aborts the shot: the stone stays put and the turn's roll is still unspent. Dropping
            // out of aiming here also stops the coming left-release from firing it.
            if (_aiming && mouse.rightButton.wasPressedThisFrame)
            {
                _aiming = false;
                _aimingPuckId = -1;
                HidePreview();
            }

            if (_aiming && TryGetAimedPosition(out Vector2 aimPosition))
            {
                Vector2 drag = PullbackDrag(aimPosition, world);
                _launchReady = drag.magnitude >= MinDrag && !(IsAimingGhost() && _ghostBlocked);
                if (_launchReady)
                {
                    _currentPowerFraction = DragToPowerFraction(drag.magnitude);
                    float power = _tuning.MaxPower * _currentPowerFraction;
                    ComputePreview(_aimingPuckId, drag.normalized * power);
                }
                else
                {
                    _currentPowerFraction = 0f;
                    HidePreview();
                }
            }

            if (mouse.leftButton.wasReleasedThisFrame && _aiming)
            {
                bool wasGhost = IsAimingGhost();
                bool blocked = wasGhost && _ghostBlocked;
                bool hasPosition = TryGetAimedPosition(out Vector2 releasePosition);

                _aiming = false;
                HidePreview();

                if (hasPosition && !blocked)
                {
                    Vector2 drag = PullbackDrag(releasePosition, world);
                    if (drag.magnitude >= MinDrag)
                    {
                        float power = _tuning.MaxPower * DragToPowerFraction(drag.magnitude);
                        Vector2 velocity = drag.normalized * power;
                        if (wasGhost)
                        {
                            LaunchGhost(velocity);
                        }
                        else
                        {
                            _sim.SetVelocity(_aimingPuckId, velocity);
                        }

                        ResetAccumulator();
                        _hasRolledThisTurn = true; // one forced roll per turn (design doc 3.5)
                    }
                }

                _aimingPuckId = -1;
            }
        }

        // The rects the battle actually draws: the compact game HUD, plus the debug panel while open.
        private bool PointerOverHud(Vector2 screen)
        {
            // Mouse.position is y-up from the bottom; GUI rects are y-down from the top.
            Vector2 gui = new Vector2(screen.x, Screen.height - screen.y);
            return GameHudRect.Contains(gui) || DebugPanel.Covers(gui);
        }

        // The character whose body covers the point, or -1. Used to pick an attack target. Per-actor
        // discs, because the hero art and the placeholder circles differ in size and centre.
        private int CharacterAt(Vector2 world)
        {
            for (int actor = 0; actor < _actorCount; actor++)
            {
                Vector2 center = new Vector2(CharacterX(actor), _characterCenterY[actor]);
                float grab = _characterGrabRadius[actor];
                if ((world - center).sqrMagnitude <= grab * grab)
                {
                    return actor;
                }
            }

            return -1;
        }

        // --- Enemy AI ------------------------------------------------------------------------------

        // --- Stage-1 boss abilities -----------------------------------------------------------------

        private void ClearBossEffects()
        {
            _debuffCells.Clear();
            _sim.ClearHole();
        }

        private void CastBossAbility()
        {
            BossAbility ability = (BossAbility)Random.Range(0, 3);
            switch (ability)
            {
                case BossAbility.CorruptCells:
                    CastCorruptCells();
                    break;
                case BossAbility.Hole:
                    CastHole();
                    break;
                default:
                    CastDamageAll();
                    break;
            }
        }

        // 1~2 inner buff cells flip for one round: an attack cell now drains attack, a shield cell now
        // costs health — the sign flip itself happens in CaptureActorBuff. A duplicate pick simply
        // collapses to one cell, which still lands inside the designed 1~2 range.
        private void CastCorruptCells()
        {
            int picks = Random.Range(1, 3); // 1 or 2
            for (int i = 0; i < picks; i++)
            {
                int col = Random.Range(1, 4);
                int row = Random.Range(1, 4);
                int index = col + row * BoardCells.Size;
                if (!_debuffCells.Contains(index))
                {
                    _debuffCells.Add(index);
                }
            }

            _attackLog = $"Boss corrupts {_debuffCells.Count} buff cell(s).";
        }

        // One random cell becomes a hole until the boss's next turn. The hole lives in the SIM (so the
        // trajectory preview sees it through Clone), but a board at rest takes no sim steps — stones
        // already parked on the cell are culled right here, the same re-query pattern settlement uses.
        private void CastHole()
        {
            _sim.SetHole(Random.Range(0, BoardCells.Size), Random.Range(0, BoardCells.Size));

            _removeIds.Clear();
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                if (_sim.IsInsideHole(pucks[i].Position))
                {
                    _removeIds.Add(pucks[i].Id);
                }
            }

            for (int i = 0; i < _removeIds.Count; i++)
            {
                _sim.RemovePuck(_removeIds[i]);
                ReturnStoneToHand(_removeIds[i]); // swallowed stones come back as fresh ones (design doc 3.3)
            }

            _attackLog = "Boss opens a hole in the board.";
        }

        // Every stone on the board loses 1 health; any at 0 is destroyed and returns to its owner's hand.
        private void CastDamageAll()
        {
            _settleIds.Clear();
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                _settleIds.Add(pucks[i].Id);
            }

            for (int i = 0; i < _settleIds.Count; i++)
            {
                if (!_sim.TryGetPuck(_settleIds[i], out Puck p))
                {
                    continue;
                }

                if (p.Health <= 1)
                {
                    _sim.RemovePuck(p.Id);
                    ReturnStoneToHand(p.Id);
                }
                else
                {
                    _sim.SetHealth(p.Id, p.Health - 1);
                }
            }

            _attackLog = "Boss racks the board — every stone loses 1 health.";
        }

        // --- Enemy AI (continued) --------------------------------------------------------------------

        // The debug panel's toggle routes through here so a mid-aim switch cannot leave a stale shot armed.
        public void SetEnemyAiEnabled(bool value)
        {
            _tuning.EnemyAiEnabled = value;
            _aiming = false;
            _aimingPuckId = -1;
            HidePreview();
        }

        // The debug panel's mid-run reset. A scene reload is the single rebuild path — it re-sizes every
        // Id-indexed view array for the roster, which a partial reset would not.
        public void RestartRun()
        {
            GameFlow.LoadBattle();
        }

        // Gathers everything the enemy search needs — own stone ids, entry candidates, weights, config —
        // and starts it on a background task. Everything handed to the task is a private copy (the board
        // is a Clone, the lists are copied, the snipe priority too), so the search reads nothing this
        // thread can mutate; TryPlan itself only ever rolls further clones.
        private void StartEnemyPlanSearch()
        {
            _planOwnIds.Clear();
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                if (_actorOf[pucks[i].Id] == _currentActor)
                {
                    _planOwnIds.Add(pucks[i].Id);
                }
            }

            // A new stone is a candidate from free spots along the entry edge — found with the same scan
            // StartTurn's gate uses, so a promised entry always yields at least one candidate here, then
            // thinned to a handful (evenly spaced across the free ones) to bound the search.
            bool hasNewStone = _handReady[_currentActor].Count > 0;
            Puck template = default;
            _planEntrySpots.Clear();
            if (hasNewStone)
            {
                // Through the hand-stone factory, so the entering stone carries its kind's health/trait.
                template = CreateHandStone(_currentActor, _handReady[_currentActor][0]);

                const int EntrySpotCount = 5;
                CollectFreeEntrySpots(_currentActor, _entryScanScratch);
                if (_entryScanScratch.Count <= EntrySpotCount)
                {
                    _planEntrySpots.AddRange(_entryScanScratch);
                }
                else
                {
                    for (int k = 0; k < EntrySpotCount; k++)
                    {
                        int pick = Mathf.RoundToInt((float)k * (_entryScanScratch.Count - 1) / (EntrySpotCount - 1));
                        _planEntrySpots.Add(_entryScanScratch[pick]);
                    }
                }

                hasNewStone = _planEntrySpots.Count > 0;
            }

            EnemyPlanWeights weights = new EnemyPlanWeights
            {
                BuffAttack = _tuning.AiBuffAttackWeight,
                BuffShield = _tuning.AiBuffShieldWeight,
                DamageDealt = _tuning.AiDamageWeight,
                StoneDestroyed = _tuning.AiDestroyBonus,
                OwnDamage = _tuning.AiOwnDamageWeight,
                OwnOnDamageCell = _tuning.AiDamageCellPenalty * _tuning.CellDamage, // future settlement loss per cell
            };

            int difficulty = Mathf.Clamp(_tuning.AiDifficulty, 0, AiDifficultyNames.Length - 1);
            EnemyPlanConfig config = new EnemyPlanConfig
            {
                CueDirections = AiCueDirections[difficulty],
                EntryDirections = AiEntryDirections[difficulty],
                PowerFractions = AiPowerFractions[difficulty],
                FullRollout = AiFullRollout[difficulty],
                PickRank = AiPickRank[difficulty],
            };

            // 저격형 (design doc 4.3): its roll hunts the player's weakest stone — priority sorted by
            // health, then Id (deterministic tie-break), handed to the planner's snipe tiers. Copied,
            // because BuildSnipePriority returns a reused buffer the task must not share.
            if (_actorTypes[_currentActor] == EnemyType.Sniper)
            {
                config.SnipePriority = new List<int>(BuildSnipePriority());
            }

            PuckSim board = _sim.Clone();
            List<int> ownIds = new List<int>(_planOwnIds);
            List<Vector2> entrySpots = new List<Vector2>(_planEntrySpots);
            bool useNewStone = hasNewStone;
            Puck stoneTemplate = template;
            float maxPower = _tuning.MaxPower;

            _planTemplate = template;
            _planTask = System.Threading.Tasks.Task.Run(() =>
            {
                System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
                bool planned = EnemyPlanner.TryPlan(
                    board, PuckOwner.Enemy, ownIds, useNewStone, stoneTemplate, entrySpots,
                    maxPower, OccupancyThreshold, weights, config, out EnemyPlan plan);
                stopwatch.Stop();
                return new EnemyPlanOutcome
                {
                    Planned = planned,
                    Plan = plan,
                    Milliseconds = (float)stopwatch.Elapsed.TotalMilliseconds,
                };
            });
        }

        // Fires the finished search's best shot on the main thread, exactly as if a hand had rolled it —
        // the buff capture, attack and turn end all run through the normal flow.
        private void ApplyEnemyPlan()
        {
            System.Threading.Tasks.Task<EnemyPlanOutcome> task = _planTask;
            _planTask = null;

            if (task.IsFaulted)
            {
                Debug.LogError($"[PuckHero] Enemy AI search failed for {ActorName(_currentActor)}: {task.Exception?.GetBaseException().Message} — ending its turn.");
                _noStoneTurn = true;
                _noStoneTimer = 0f;
                return;
            }

            EnemyPlanOutcome outcome = task.Result;
            _aiPlanMs = outcome.Milliseconds;
            _aiPlanCandidates = outcome.Plan.CandidatesEvaluated;

            if (!outcome.Planned)
            {
                // StartTurn routes turns with nothing to roll to the no-stone path, so this is unexpected.
                Debug.LogError($"[PuckHero] Enemy AI found no shot for {ActorName(_currentActor)}; ending its turn.");
                _noStoneTurn = true;
                _noStoneTimer = 0f;
                return;
            }

            EnemyPlan plan = outcome.Plan;
            if (plan.UseNewStone)
            {
                _handReady[_currentActor].Remove(plan.StoneId);
                Puck stone = _planTemplate;
                stone.Position = plan.EntryPosition;
                stone.Velocity = plan.Velocity;
                _sim.AddPuck(stone);
                _xpFillFraction[stone.Id] = -1f; // drop the fill cached for whatever last used this Id
                ClearGhost();
            }
            else
            {
                _sim.SetVelocity(plan.StoneId, plan.Velocity);
            }

            // The sniper's rolled stone is armed for its 2-damage first player contact; AdvanceTurn
            // disarms whatever the roll leaves armed (design doc 4.3).
            if (_actorTypes[_currentActor] == EnemyType.Sniper)
            {
                _sim.SetSniperArmed(plan.StoneId, true);
            }

            ResetAccumulator();
            _hasRolledThisTurn = true; // the normal roll-finished flow takes over from here
        }

        // Player stones sorted weakest-first (Id breaks ties) — the snipe order (design doc 4.3).
        private IReadOnlyList<int> BuildSnipePriority()
        {
            _snipePriority.Clear();
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                if (pucks[i].Owner == PuckOwner.Player)
                {
                    _snipePriority.Add(pucks[i].Id);
                }
            }

            _snipePriority.Sort((x, y) =>
            {
                _sim.TryGetPuck(x, out Puck px);
                _sim.TryGetPuck(y, out Puck py);
                return px.Health != py.Health ? px.Health.CompareTo(py.Health) : x.CompareTo(y);
            });

            return _snipePriority;
        }

        // --- Character combat (design doc 3.5 step 4, 3.6, 3.8) --------------------------------------

        // Sets every character up for the run about to start: enemies come in fresh, the player brings the
        // health it left the last run with (design doc 2.1), and the base shield pool refills — it is
        // run-scoped, not match-scoped (design doc 3.6).
        private void ResetCombatState()
        {
            for (int actor = 0; actor < _actorCount; actor++)
            {
                _actorHealth[actor] = actor == 0 && Campaign.RunStartHealth > 0
                    ? Mathf.Min(Campaign.RunStartHealth, BaseHealth(actor))
                    : BaseHealth(actor);
                _actorBaseShield[actor] = BaseShield(actor);
                _actorEffectShield[actor] = 0;
                _actorBuffAttack[actor] = 0;
                _actorDead[actor] = false;
                _handReady[actor].Clear();
                _handPending[actor].Clear();
            }

            // The match opens with an empty board: every stone in the roster starts in its owner's hand,
            // playable from that actor's first turn (design doc 3.3/3.4).
            List<Puck> roster = InitialRoster();
            for (int i = 0; i < roster.Count; i++)
            {
                _handReady[_actorOf[roster[i].Id]].Add(roster[i].Id);
            }

            _awaitingAttack = false;
            _gameOver = false;
            _campaignCleared = false;
            _gameOverText = "";
            _attackLog = "";
            _noStoneTurn = false;
            ClearGhost();
        }

        // Turn step 4: the attack is forced, one target. The player picks by clicking an enemy character;
        // an enemy has only the player to hit, so it fires at once.
        private void BeginAttackPhase()
        {
            if (_currentActor == 0)
            {
                _awaitingAttack = true;
                return;
            }

            ResolveAttack(_currentActor, 0);
            if (!_gameOver)
            {
                AdvanceTurn();
            }
        }

        // Applies one attack: damage = attacker's base attack + its buff snapshot. The target spends its
        // effect shield first (it expires at its next turn anyway), then the base pool, and only what is
        // left cuts health (design doc 3.6). Health at 0 takes the character out (design doc 3.8).
        private void ResolveAttack(int attacker, int target)
        {
            int damage = BaseAttack(attacker) + _actorBuffAttack[attacker];

            // Corrupted attack cells can push the total below zero; a negative attack HEALS the target by
            // that amount instead (사용자 확정), capped at its base maximum. Shields are untouched.
            if (damage < 0)
            {
                int healed = Mathf.Min(-damage, BaseHealth(target) - _actorHealth[target]);
                _actorHealth[target] += healed;
                _attackLog = $"{ActorName(attacker)}'s corrupted attack heals {ActorName(target)} for {healed}.";
                return;
            }

            int fromEffect = Mathf.Min(_actorEffectShield[target], damage);
            _actorEffectShield[target] -= fromEffect;

            int remaining = damage - fromEffect;
            int fromBase = Mathf.Min(_actorBaseShield[target], remaining);
            _actorBaseShield[target] -= fromBase;

            remaining -= fromBase;
            _actorHealth[target] -= remaining;

            int absorbed = fromEffect + fromBase;
            string absorbedText = absorbed > 0 ? $" (shield absorbed {absorbed})" : "";

            if (_actorHealth[target] <= 0)
            {
                _actorHealth[target] = 0;
                KillActor(target);
                _attackLog = $"{ActorName(attacker)} hit {ActorName(target)} for {damage}{absorbedText} — down.";
            }
            else
            {
                _attackLog = $"{ActorName(attacker)} hit {ActorName(target)} for {damage}{absorbedText} — HP {_actorHealth[target]}.";
            }

            CheckGameOver();
        }

        // Takes a character out of the match: its stones leave the board (design doc 4.1) and its turn is
        // skipped from here on.
        private void KillActor(int actor)
        {
            if (actor != 0)
            {
                Campaign.Gold += _tuning.GoldPerKill; // gold comes from taking enemies down (design doc 5.6)
                _goldEarnedThisRun += _tuning.GoldPerKill;
            }

            _actorDead[actor] = true;
            _actorHealth[actor] = 0;
            _actorBaseShield[actor] = 0;
            _actorEffectShield[actor] = 0;
            _actorBuffAttack[actor] = 0;
            _handReady[actor].Clear();   // a character that is out keeps nothing in hand
            _handPending[actor].Clear();
            if (_ghostActive && _actorOf[_ghost.Id] == actor)
            {
                ClearGhost();
            }

            _removeIds.Clear();
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                if (_actorOf[pucks[i].Id] == actor)
                {
                    _removeIds.Add(pucks[i].Id);
                }
            }

            for (int i = 0; i < _removeIds.Count; i++)
            {
                _sim.RemovePuck(_removeIds[i]);
            }
        }

        // Design doc 3.8: every enemy down clears the run, the player going down ends the campaign — and
        // there is no continue, so that means starting over from stage 1 (design doc 2.1).
        private void CheckGameOver()
        {
            if (_actorDead[0])
            {
                _gameOver = true;
                _gameOverText = $"Defeat on stage {Campaign.Stage}-{Campaign.Run}.";
                _endScreenTimer = EndScreenDelay; // the game-over screen takes it from here (사용자 지정)
                return;
            }

            for (int actor = 1; actor < _actorCount; actor++)
            {
                if (!_actorDead[actor])
                {
                    return;
                }
            }

            ClearRun();
        }

        // --- Progression (design doc 2.1) -----------------------------------------------------------

        // Run won: the player heals a set amount and carries that health into the next run. The cleared
        // board stays up until the player enters the shop, which is the only way on (design doc 2.1: it
        // opens straight away and cannot be skipped).
        private void ClearRun()
        {
            _gameOver = true;

            // Only the next run's figure moves; RunStartHealth stays put so restarting this run is still
            // worth what it was. CampaignState.AdvanceRun (leaving the shop) is what promotes it.
            Campaign.NextRunHealth = Mathf.Min(_actorHealth[0] + RunEndHeal(), BaseHealth(0));
            _actorHealth[0] = Campaign.NextRunHealth; // shown healed on the cleared board

            bool lastRun = Campaign.IsBossRun;
            if (lastRun && Campaign.Stage >= CampaignState.StageCount)
            {
                _campaignCleared = true;
                _gameOverText = "All stages cleared.";
                _endScreenTimer = EndScreenDelay; // the game-clear screen takes it from here (사용자 지정)
                return;
            }

            _gameOverText = lastRun
                ? $"Stage {Campaign.Stage} cleared. Healed to {Campaign.NextRunHealth}."
                : $"Run {Campaign.Stage}-{Campaign.Run} cleared. Healed to {Campaign.NextRunHealth}.";

            _victoryPanel.Show(_goldEarnedThisRun); // its 상점으로 button is the way on (design doc 2.1)
        }

        // The victory panel: built hidden with the fixed picks' numbers, shown by ClearRun. The panel is
        // pure view — the effects live here, on the campaign the controller already owns.
        private void BuildVictoryPanel()
        {
            _victoryPanel = new VictoryPanel(transform, _victoryPanelSprite, _victoryButtonSprite, _goldIconSprite, VictoryHealAmount());
            _victoryPanel.HealChosen = ApplyVictoryHeal;
            _victoryPanel.GoldChosen = ApplyVictoryGold;
            _victoryPanel.ShopChosen = GameFlow.LoadShop;
        }

        private int VictoryHealAmount()
        {
            return Mathf.CeilToInt(BaseHealth(0) * 0.2f);
        }

        // Victory pick 1: a fifth of max health onto the carry-over figure (capped at max). Written to
        // the shown health too, so the stat row ticks up the moment it is picked.
        private void ApplyVictoryHeal()
        {
            Campaign.NextRunHealth = Mathf.Min(Campaign.NextRunHealth + VictoryHealAmount(), BaseHealth(0));
            _actorHealth[0] = Campaign.NextRunHealth;
        }

        // Victory pick 2: the run's kill gold once more — double in total; the panel's line shows the sum.
        private void ApplyVictoryGold()
        {
            Campaign.Gold += _goldEarnedThisRun;
            _victoryPanel.SetGoldAmount(_goldEarnedThisRun * 2);
        }

        private string RunLabel()
        {
            return Campaign.IsBossRun ? $"Stage {Campaign.Stage}-{Campaign.Run} (Boss)" : $"Stage {Campaign.Stage}-{Campaign.Run}";
        }

        // The player's base stats carry the upgrades bought on the shop board; they accumulate for the whole
        // campaign (design doc 5.5). Enemies are unaffected.
        private int BaseHealth(int actor)
        {
            if (actor == 0)
            {
                return _tuning.PlayerBaseHealth + Campaign.BonusMaxHealth;
            }

            return IsStage1Boss ? _tuning.BossBaseHealth : TypeBaseHealth(_actorTypes[actor]);
        }

        private int BaseAttack(int actor)
        {
            if (actor == 0)
            {
                return _tuning.PlayerBaseAttack + Campaign.BonusAttack;
            }

            return IsStage1Boss ? _tuning.BossBaseAttack : TypeBaseAttack(_actorTypes[actor]);
        }

        private int BaseShield(int actor)
        {
            if (actor == 0)
            {
                return _tuning.PlayerBaseShield + Campaign.BonusShield;
            }

            return IsStage1Boss ? _tuning.BossBaseShield : _tuning.EnemyBaseShield;
        }

        private int RunEndHeal()
        {
            return _tuning.RunEndHeal + Campaign.BonusRunHeal;
        }

        // --- Buffs (design doc 3.6/3.7) -------------------------------------------------------------

        // Turn end (design doc 3.5 step 3): lock in the actor's buff from the cells its stones occupy, each
        // cell's value multiplied by that stone's level (design doc 3.7 growth). Held until its next turn.
        // Per-cell rather than BoardCells.SumBuffs, because boss-corrupted cells count the other way: an
        // attack cell drains the attack snapshot (it may go negative), and a shield cell costs the
        // character health right here instead of granting shield.
        private void CaptureActorBuff(int actor)
        {
            if (_actorBuffAttack == null)
            {
                return;
            }

            int attack = 0;
            int shield = 0;
            int healthLoss = 0;
            Vector2 boardMin = _sim.BoardMin;
            Vector2 boardMax = _sim.BoardMax;
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                Puck p = pucks[i];
                if (_actorOf[p.Id] != actor)
                {
                    continue;
                }

                BoardCells.GetOccupiedCells(boardMin, boardMax, p.Position, p.Radius, OccupancyThreshold, _occupiedCells);
                for (int c = 0; c < _occupiedCells.Count; c++)
                {
                    int index = _occupiedCells[c];
                    int col = index % BoardCells.Size;
                    int row = index / BoardCells.Size;
                    if (BoardCells.TypeOf(col, row) != CellType.Buff)
                    {
                        continue;
                    }

                    int gain = BoardCells.BuffValue(col, row) * p.Level;
                    bool corrupted = _debuffCells.Contains(index);
                    if (BoardCells.KindOf(col, row) == BuffKind.Attack)
                    {
                        attack += corrupted ? -gain : gain;
                    }
                    else if (corrupted)
                    {
                        healthLoss += gain;
                    }
                    else
                    {
                        shield += gain;
                    }
                }
            }

            _actorBuffAttack[actor] = attack;
            _actorEffectShield[actor] = shield; // effect shield refills to the buff amount (design doc 3.6)

            if (healthLoss > 0)
            {
                _actorHealth[actor] -= healthLoss;
                _attackLog = $"{ActorName(actor)} loses {healthLoss} health to corrupted cells.";
                if (_actorHealth[actor] <= 0)
                {
                    _actorHealth[actor] = 0;
                    KillActor(actor);
                }

                CheckGameOver();
            }
        }

        // Turn start (design doc 3.6): the actor's buff resets, leaving base stats only until it rolls again.
        // The effect shield expires with it; the base shield pool is untouched (it is run-scoped).
        private void ClearActorBuff(int actor)
        {
            if (_actorBuffAttack == null)
            {
                return;
            }

            _actorBuffAttack[actor] = 0;
            _actorEffectShield[actor] = 0;
        }

        // --- Battle-only view construction ----------------------------------------------------------

        private void BuildBoard()
        {
            Transform board = new GameObject("Board").transform;
            board.SetParent(transform, false);

            float full = BoardHalf * 2f;

            MakeQuad("Background", board, Vector2.zero, new Vector2(full, full), new Color(0.16f, 0.17f, 0.20f), 0);
            // Inner 3x3 buff cells, coloured by kind (attack/shield) and brighter toward the stronger centre.
            // The renderers are kept so UpdateBossEffectVisuals can recolour corrupted ones per frame.
            Vector2 boardMin = new Vector2(-BoardHalf, -BoardHalf);
            Vector2 boardMax = new Vector2(BoardHalf, BoardHalf);
            Vector2 buffCellSize = BoardCells.CellSize(boardMin, boardMax);
            _buffCellViews = new SpriteRenderer[9];
            for (int row = 1; row <= 3; row++)
            {
                for (int col = 1; col <= 3; col++)
                {
                    Vector2 center = BoardCells.CellCenter(boardMin, boardMax, col, row);
                    _buffCellViews[(row - 1) * 3 + (col - 1)] = MakeQuad("BuffCell", board, center, buffCellSize, BuffCellColor(col, row), 1);
                }
            }

            // The boss's hole, moved onto whichever cell is open. Above the occupancy highlights (4) so the
            // pit visibly swallows, below the rings (7+) and stones (10).
            _holeView = MakeQuad("Hole", board, Vector2.zero, buffCellSize, new Color(0.02f, 0.02f, 0.04f), 5);
            _holeView.enabled = false;

            // Internal cell boundaries (the outermost boundaries are the walls, drawn below).
            float[] gridLines = { -InnerHalf, -2.5f, 2.5f, InnerHalf };
            Color gridColor = new Color(1f, 1f, 1f, 0.13f);
            const float gridThickness = 0.08f;
            foreach (float g in gridLines)
            {
                MakeQuad("GridV", board, new Vector2(g, 0f), new Vector2(gridThickness, full), gridColor, 2);
                MakeQuad("GridH", board, new Vector2(0f, g), new Vector2(full, gridThickness), gridColor, 2);
            }

            Color wallColor = new Color(0.85f, 0.86f, 0.92f);
            const float wallThickness = 0.4f;
            MakeQuad("WallTop", board, new Vector2(0f, BoardHalf), new Vector2(full + wallThickness, wallThickness), wallColor, 3);
            MakeQuad("WallBottom", board, new Vector2(0f, -BoardHalf), new Vector2(full + wallThickness, wallThickness), wallColor, 3);
            MakeQuad("WallLeft", board, new Vector2(-BoardHalf, 0f), new Vector2(wallThickness, full + wallThickness), wallColor, 3);
            MakeQuad("WallRight", board, new Vector2(BoardHalf, 0f), new Vector2(wallThickness, full + wallThickness), wallColor, 3);
        }

        // Placeholder buff-cell tint: attack cells warm, shield cells cool, brighter at the stronger centre.
        private static Color BuffCellColor(int col, int row)
        {
            bool strong = BoardCells.BuffValue(col, row) >= 2; // centre grants 2, other inner cells 1
            if (BoardCells.KindOf(col, row) == BuffKind.Attack)
            {
                return strong ? new Color(0.46f, 0.30f, 0.16f) : new Color(0.34f, 0.24f, 0.16f);
            }

            return strong ? new Color(0.18f, 0.34f, 0.42f) : new Color(0.16f, 0.26f, 0.32f);
        }

        private void BuildCellHighlights()
        {
            int cap = InitialRoster().Count * MaxCellsPerStone;
            _cellHighlights = new SpriteRenderer[cap];
            for (int i = 0; i < cap; i++)
            {
                GameObject go = new GameObject($"CellHighlight{i}");
                go.transform.SetParent(transform, false);

                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = ProceduralSprites.Unit();
                sr.sortingOrder = 4; // above board/grid/walls (0..3), below pucks (10)
                sr.enabled = false;
                _cellHighlights[i] = sr;
            }
        }

        private void BuildTurnRings()
        {
            int count = InitialRoster().Count; // the board is empty at build time; size for every stone
            _turnRings = new SpriteRenderer[count];
            for (int i = 0; i < count; i++)
            {
                GameObject go = new GameObject($"TurnRing{i}");
                go.transform.SetParent(transform, false);

                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = ProceduralSprites.Circle();
                sr.color = RingStrong; // recoloured per frame in UpdateTurnHighlights
                sr.sortingOrder = 8; // behind the puck (10), above the cell highlights (4)
                sr.enabled = false;
                _turnRings[i] = sr;
            }
        }

        // Pool for the hover link. One per stone, drawn behind the turn rings so both can show at once.
        private void BuildHoverRings()
        {
            int count = InitialRoster().Count;
            _hoverRings = new SpriteRenderer[count];
            for (int i = 0; i < count; i++)
            {
                GameObject go = new GameObject($"HoverRing{i}");
                go.transform.SetParent(transform, false);

                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = ProceduralSprites.Circle();
                sr.color = RingHover;
                sr.sortingOrder = 7; // outside and behind the turn rings (8)
                sr.enabled = false;
                _hoverRings[i] = sr;
            }
        }

        // Builds the top character row: one placeholder widget per actor (design doc 2.2) — a body circle in
        // the team colour with a name + stat block below it that UpdateCharacterStats refreshes.
        private void BuildCharacters()
        {
            _statRowTexts = new TextMeshPro[_actorCount][];
            _statRowIcons = new SpriteRenderer[_actorCount][];
            _characterBodies = new SpriteRenderer[_actorCount];
            _characterOutlines = new SpriteRenderer[_actorCount][];
            _characterCenterY = new float[_actorCount];
            _characterGrabRadius = new float[_actorCount];
            _actorBuffAttack = new int[_actorCount];
            _actorHealth = new int[_actorCount];
            _actorBaseShield = new int[_actorCount];
            _actorEffectShield = new int[_actorCount];
            _actorDead = new bool[_actorCount];
            _actorTurnCounts = new int[_actorCount];
            _handReady = new List<int>[_actorCount];
            _handPending = new List<int>[_actorCount];
            for (int actor = 0; actor < _actorCount; actor++)
            {
                _handReady[actor] = new List<int>();
                _handPending[actor] = new List<int>();
            }

            ResetCombatState();

            BuildGroundStrip();

            for (int actor = 0; actor < _actorCount; actor++)
            {
                float x = CharacterX(actor);

                GameObject root = new GameObject($"Character{actor}");
                root.transform.SetParent(transform, false);

                // Body first: the hero art and the placeholder circle stand on the same feet line but
                // differ in height, and the ring and hit disc follow whichever body was built.
                SpriteRenderer body = actor == 0 ? TryBuildHeroBody(root.transform, x) : null;
                if (body != null)
                {
                    _characterCenterY[actor] = CharFeetY + CharBodyHeight * 0.5f;
                    _characterGrabRadius[actor] = CharBodyHeight * 0.6f;
                }
                else
                {
                    GameObject bodyGo = new GameObject("Body");
                    bodyGo.transform.SetParent(root.transform, false);
                    float diameter = CharBodyRadius * 2f;
                    bodyGo.transform.localPosition = new Vector3(x, CharFeetY + CharBodyRadius, 0f);
                    bodyGo.transform.localScale = new Vector3(diameter, diameter, 1f);
                    body = bodyGo.AddComponent<SpriteRenderer>();
                    body.sprite = ProceduralSprites.Circle();
                    body.color = ActorColor(actor);
                    body.sortingOrder = 10;
                    _characterCenterY[actor] = CharFeetY + CharBodyRadius;
                    _characterGrabRadius[actor] = CharBodyRadius * 1.4f; // forgiving, the bodies are far apart
                }

                _characterBodies[actor] = body;

                // Highlight outline: silhouette ghosts of the body, nudged out in 8 directions, so the
                // "clickable" treatment hugs the art — any frame, any future animation — instead of a circle.
                _characterOutlines[actor] = BuildCharacterOutline(body);

                BuildStatRows(root.transform, actor, x);
            }
        }

        // The player's slot shows the hero art when it is wired: the imported prefab is scaled to
        // CharBodyHeight and stood on the feet line — the import pivot sits below the feet, so the pivot
        // cannot be trusted for placement and the sprite bounds are aligned instead. Returns null when
        // the art is missing or broken, and the caller falls back to the circle.
        private SpriteRenderer TryBuildHeroBody(Transform parent, float x)
        {
            if (_heroBodyPrefab == null)
            {
                return null;
            }

            GameObject go = Instantiate(_heroBodyPrefab, parent, false);
            go.name = "Body";

            SpriteRenderer sr = go.GetComponentInChildren<SpriteRenderer>();
            if (sr == null || sr.sprite == null)
            {
                Debug.LogError("[PuckHero] Hero prefab has no usable SpriteRenderer — using the placeholder circle.");
                Destroy(go);
                return null;
            }

            sr.sortingOrder = 10;
            sr.color = Color.white;

            if (go.TryGetComponent(out Animator animator))
            {
                animator.speed = 0.5f; // idle plays at half speed (user-tuned feel)
            }

            go.transform.localPosition = new Vector3(x, CharFeetY, 0f);
            go.transform.localScale *= CharBodyHeight / sr.bounds.size.y;
            // Scaling grew the art around its pivot; align the sprite centre so the feet land on the line.
            Vector3 target = parent.TransformPoint(new Vector3(x, CharFeetY + CharBodyHeight * 0.5f, 0f));
            go.transform.position += target - sr.bounds.center;

            _heroBodyUsesArt = true;
            return sr;
        }

        // Row tints for the placeholder icon squares, in row order, so the columns read before the real
        // icons exist: health red, shield blue, attack orange, stones grey.
        private static readonly Color[] StatPlaceholderTints =
        {
            new Color(0.90f, 0.30f, 0.30f),
            new Color(0.35f, 0.55f, 0.90f),
            new Color(0.95f, 0.60f, 0.20f),
            new Color(0.75f, 0.75f, 0.75f),
        };

        // The ground strip the characters stand on (reference look, art only — no gameplay). Tiled across
        // the board's full width so the image repeats instead of stretching; nothing is drawn until the
        // sprite is wired.
        private void BuildGroundStrip()
        {
            if (_groundSprite == null)
            {
                return;
            }

            GameObject go = new GameObject("CharacterGround");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, (BoardHalf + CharFeetY) * 0.5f, 0f);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _groundSprite;
            sr.drawMode = SpriteDrawMode.Tiled;
            sr.size = new Vector2(BoardHalf * 2f + 0.8f, CharFeetY - BoardHalf);
            sr.sortingOrder = 5; // above the board walls (3), behind the rings and bodies (9, 10)
        }

        // One actor's stat column over its head: StatRowCount rows of [icon] [number]. Wired icon sprites
        // are fitted to the row height; a missing icon renders as a tinted square so the slot is visible.
        private void BuildStatRows(Transform root, int actor, float x)
        {
            Sprite[] icons = { _healthIconSprite, _shieldIconSprite, _attackIconSprite, _stoneIconSprite };
            _statRowTexts[actor] = new TextMeshPro[StatRowCount];
            _statRowIcons[actor] = new SpriteRenderer[StatRowCount];
            for (int row = 0; row < StatRowCount; row++)
            {
                float y = StatBlockBottom + (StatRowCount - row - 0.5f) * StatRowHeight;

                GameObject iconGo = new GameObject($"StatIcon{row}");
                iconGo.transform.SetParent(root, false);
                iconGo.transform.localPosition = new Vector3(x - 1.4f, y, 0f);
                SpriteRenderer icon = iconGo.AddComponent<SpriteRenderer>();
                icon.sortingOrder = 12;
                if (icons[row] != null)
                {
                    icon.sprite = icons[row];
                    iconGo.transform.localScale = Vector3.one * (0.9f / icon.bounds.size.y);
                    // The import pivots sit below the art; re-centre the visual on the row line.
                    iconGo.transform.position += root.TransformPoint(new Vector3(x - 1.4f, y, 0f)) - icon.bounds.center;
                }
                else
                {
                    icon.sprite = ProceduralSprites.Unit();
                    icon.color = StatPlaceholderTints[row];
                    iconGo.transform.localScale = new Vector3(0.8f, 0.8f, 1f);
                }

                _statRowIcons[actor][row] = icon;
                _statRowTexts[actor][row] = MakeStatText(root, $"StatValue{row}", x + 0.85f, y, 3.6f, StatRowHeight);
            }
        }

        // A world-space TMP that auto-sizes into the given box (placeholder to restyle later, like the level text).
        private static TextMeshPro MakeStatText(Transform parent, string name, float x, float y, float width, float height)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(x, y, 0f);

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 1f;
            tmp.fontSizeMax = 8f;
            tmp.rectTransform.sizeDelta = new Vector2(width, height);
            tmp.color = Color.white;
            tmp.GetComponent<MeshRenderer>().sortingOrder = 12;
            return tmp;
        }

        // Spreads the actors evenly across the top, player leftmost.
        private float CharacterX(int actor)
        {
            if (_actorCount <= 1)
            {
                return 0f;
            }

            float t = (float)actor / (_actorCount - 1);
            return Mathf.Lerp(-CharSpread, CharSpread, t);
        }

        private static Color ActorColor(int actor)
        {
            return actor == 0 ? OwnerColor(PuckOwner.Player) : OwnerColor(PuckOwner.Enemy);
        }

        // --- Battle-only per-frame rendering --------------------------------------------------------

        // Overlays each cell a puck currently occupies with a translucent quad in the owner's colour,
        // reading occupancy straight from BoardCells with the live threshold. Pooled, so it never allocates.
        private void UpdateCellHighlights()
        {
            for (int i = 0; i < _cellHighlights.Length; i++)
            {
                _cellHighlights[i].enabled = false;
            }

            Vector2 boardMin = _sim.BoardMin;
            Vector2 boardMax = _sim.BoardMax;
            Vector2 cellSize = BoardCells.CellSize(boardMin, boardMax);
            float w = cellSize.x * 0.9f;
            float h = cellSize.y * 0.9f;

            int next = 0;
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count && next < _cellHighlights.Length; i++)
            {
                Puck p = pucks[i];
                BoardCells.GetOccupiedCells(boardMin, boardMax, p.Position, p.Radius, OccupancyThreshold, _occupiedCells);

                Color color = OwnerColor(p.Owner);
                color.a = 0.22f;

                for (int c = 0; c < _occupiedCells.Count && next < _cellHighlights.Length; c++)
                {
                    int idx = _occupiedCells[c];
                    int col = idx % BoardCells.Size;
                    int row = idx / BoardCells.Size;
                    Vector2 center = BoardCells.CellCenter(boardMin, boardMax, col, row);

                    SpriteRenderer sr = _cellHighlights[next++];
                    sr.transform.localPosition = new Vector3(center.x, center.y, 0f);
                    sr.transform.localScale = new Vector3(w, h, 1f);
                    sr.color = color;
                    sr.enabled = true;
                }
            }
        }

        // Corrupted cells wear their warning tint, and the hole quad sits on whichever cell is open.
        // Recoloured per frame so expiry (boss's next turn) restores the board with no bookkeeping.
        private void UpdateBossEffectVisuals()
        {
            if (_buffCellViews == null)
            {
                return;
            }

            for (int row = 1; row <= 3; row++)
            {
                for (int col = 1; col <= 3; col++)
                {
                    bool corrupted = _debuffCells.Contains(col + row * BoardCells.Size);
                    _buffCellViews[(row - 1) * 3 + (col - 1)].color = corrupted ? CorruptCellColor : BuffCellColor(col, row);
                }
            }

            int hole = _sim.HoleCell;
            if (hole >= 0)
            {
                Vector2 center = BoardCells.CellCenter(_sim.BoardMin, _sim.BoardMax, hole % BoardCells.Size, hole / BoardCells.Size);
                _holeView.transform.localPosition = new Vector3(center.x, center.y, 0f);
                _holeView.enabled = true;
            }
            else
            {
                _holeView.enabled = false;
            }
        }

        // Shows a ring behind each stone belonging to the actor whose turn it is, so the player knows which
        // stones are rollable (the three enemies are all red, so colour alone is not enough). Once the roll
        // is spent the rings go out — nothing of this actor's is rollable while its stone is still travelling
        // or while it is picking an attack target.
        private void UpdateTurnHighlights()
        {
            for (int i = 0; i < _turnRings.Length; i++)
            {
                _turnRings[i].enabled = false;
            }

            if (_hasRolledThisTurn)
            {
                return;
            }

            int next = 0;
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count && next < _turnRings.Length; i++)
            {
                Puck p = pucks[i];
                if (_actorOf[p.Id] != _currentActor)
                {
                    continue;
                }

                SpriteRenderer ring = _turnRings[next++];
                float d = p.Radius * 2.5f;
                ring.transform.localPosition = new Vector3(p.Position.x, p.Position.y, 0f);
                ring.transform.localScale = new Vector3(d, d, 1f);

                // Red once the pull-back is past the minimum, so it is obvious when releasing will fire.
                ring.color = _launchReady && p.Id == _aimingPuckId ? RingLaunchReady : RingStrong;
                ring.enabled = true;
            }
        }

        // Links an enemy to its stones both ways (design doc 4.1): hover the character and its stones light
        // up, hover any of its stones and the character does. Runs every frame regardless of whose turn it
        // is and whether input is accepted, so it works during the enemy's own turn too.
        private void UpdateHoverHighlight()
        {
            _hoveredEnemyActor = HoveredEnemyActor();

            for (int i = 0; i < _hoverRings.Length; i++)
            {
                _hoverRings[i].enabled = false;
            }

            if (_hoveredEnemyActor < 0)
            {
                return;
            }

            int next = 0;
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count && next < _hoverRings.Length; i++)
            {
                Puck p = pucks[i];
                if (_actorOf[p.Id] != _hoveredEnemyActor)
                {
                    continue;
                }

                SpriteRenderer ring = _hoverRings[next++];
                float d = p.Radius * HoverRingScale * 2f;
                ring.transform.localPosition = new Vector3(p.Position.x, p.Position.y, 0f);
                ring.transform.localScale = new Vector3(d, d, 1f);
                ring.enabled = true;
            }
        }

        // The enemy the cursor is on — its character body, or any stone it owns. -1 when the cursor is on
        // the HUD, on the player's own things, or on nothing.
        private int HoveredEnemyActor()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || _actorDead == null)
            {
                return -1;
            }

            Vector2 screen = mouse.position.ReadValue();
            if (PointerOverHud(screen))
            {
                return -1;
            }

            Vector2 world = ScreenToWorld(screen);

            int character = CharacterAt(world);
            if (character > 0 && !_actorDead[character])
            {
                return character;
            }

            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                Puck p = pucks[i];
                int owner = _actorOf[p.Id];
                if (owner == 0)
                {
                    continue; // the player's own stones are not part of this link
                }

                // The drawn circle exactly, not the ring's reach: the sim separates stones to a full
                // diameter apart, so discs of this radius can never both contain the cursor. That makes the
                // first match the only match — no nearest-wins tie-break needed, and the highlight lands on
                // the stone the cursor is visibly on.
                if ((p.Position - world).magnitude <= p.Radius)
                {
                    return owner;
                }
            }

            return -1;
        }

        // Writes each actor's stat column (rows over the head — health, shield, attack, stones). Attack
        // and shield show the buffed total as one number (the buff snapshot from that actor's last turn
        // end, design doc 3.6); a corrupted-cell debuff simply reads as a lower — possibly negative —
        // total. Health stays current/max, and stones keep the waiting count in parentheses (those are
        // not a buff: they are stones that exist but cannot be thrown yet).
        private void UpdateCharacterStats()
        {
            if (_statRowTexts == null)
            {
                return;
            }

            for (int actor = 0; actor < _actorCount; actor++)
            {
                if (_actorDead[actor])
                {
                    _characterBodies[actor].color = new Color(0.30f, 0.30f, 0.34f, 0.55f);
                    SetOutline(actor, default, false);
                }
                else
                {
                    _characterBodies[actor].color = actor == 0 && _heroBodyUsesArt ? Color.white : ActorColor(actor);
                    UpdateCharacterOutline(actor);
                }

                int pending = _handPending[actor].Count;
                _statRowTexts[actor][0].text = $"{_actorHealth[actor]}/{BaseHealth(actor)}";
                _statRowTexts[actor][1].text = $"x {_actorBaseShield[actor] + _actorEffectShield[actor]}";
                _statRowTexts[actor][2].text = $"x {BaseAttack(actor) + _actorBuffAttack[actor]}";
                _statRowTexts[actor][3].text = pending > 0
                    ? $"x {_handReady[actor].Count} (+{pending})"
                    : $"x {_handReady[actor].Count}";
            }
        }

        // The outline is built from eight silhouette copies of the body, each pushed out one thickness
        // step. Overlapping ghosts stack alpha, so a translucent state needs a per-ghost sliver of alpha
        // (OutlineFaint) rather than RingFaint's 0.25 — stacked, it reads at about the same strength.
        private static readonly Vector2[] OutlineDirections =
        {
            new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f),
            new Vector2(0.7071f, 0.7071f), new Vector2(-0.7071f, 0.7071f),
            new Vector2(0.7071f, -0.7071f), new Vector2(-0.7071f, -0.7071f),
        };
        private const float OutlineThickness = 0.12f;      // world units the silhouettes are pushed out
        private const float HoverOutlineThickness = 0.2f;  // the hover link needs more presence: the red
                                                           // hairline vanished against the enemy circles
        private static readonly Color OutlineFaint = new Color(1f, 0.9f, 0.25f, 0.07f);

        // Eight silhouette ghosts parented under the body, so they inherit its transform (the hero art
        // is scaled and re-centred) and, on show, its current sprite — the outline follows whatever the
        // Animator is playing with no per-frame outline art.
        private SpriteRenderer[] BuildCharacterOutline(SpriteRenderer body)
        {
            if (_silhouetteMaterial == null)
            {
                Debug.LogError("[PuckHero] Silhouette material is not assigned — run Tools/PuckHero/Setup Game Scenes; character highlights are off.");
                return new SpriteRenderer[0];
            }

            SpriteRenderer[] ghosts = new SpriteRenderer[OutlineDirections.Length];
            float parentScale = body.transform.lossyScale.x; // uniform for both body kinds
            for (int i = 0; i < ghosts.Length; i++)
            {
                GameObject go = new GameObject($"Outline{i}");
                go.transform.SetParent(body.transform, false);
                go.transform.localPosition = OutlineDirections[i] * (OutlineThickness / parentScale);

                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sharedMaterial = _silhouetteMaterial;
                sr.sortingOrder = 9; // just behind the body (10)
                sr.enabled = false;
                ghosts[i] = sr;
            }

            return ghosts;
        }

        // Shows or hides one actor's outline, keeping the ghost sprites in step with the animating body.
        // Thickness is per-state, so the offsets are recomputed against the body's scale on show.
        private void SetOutline(int actor, Color color, bool on, float thickness = OutlineThickness)
        {
            SpriteRenderer[] ghosts = _characterOutlines[actor];
            SpriteRenderer body = _characterBodies[actor];
            float parentScale = body.transform.lossyScale.x;
            for (int i = 0; i < ghosts.Length; i++)
            {
                ghosts[i].enabled = on;
                if (on)
                {
                    ghosts[i].sprite = body.sprite;
                    ghosts[i].color = color;
                    ghosts[i].transform.localPosition = OutlineDirections[i] * (thickness / parentScale);
                }
            }
        }

        // Outline states while the player is picking a target (design doc 3.5 step 4): the attacker is
        // outlined so it is clear who is acting, every enemy it may hit gets a faint outline, and the one
        // under the cursor lights up fully. Outside the attack phase the outline still marks the enemy
        // under the cursor, tying it to the stones lit up on the board (design doc 4.1).
        private void UpdateCharacterOutline(int actor)
        {
            if (_awaitingAttack)
            {
                // Hovering an enemy's stone promotes its owner too (design doc 4.1), so the stone→owner
                // link stays readable while picking the target.
                bool strong = actor == _currentActor || actor == _hoveredCharacter || actor == _hoveredEnemyActor;
                SetOutline(actor, strong ? RingStrong : OutlineFaint, true);
                return;
            }

            if (actor == _hoveredEnemyActor)
            {
                SetOutline(actor, RingHover, true, HoverOutlineThickness);
                return;
            }

            SetOutline(actor, default, false);
        }

        // --- Game HUD -----------------------------------------------------------------------------

        private void OnGUI()
        {
            if (_sim == null)
            {
                return;
            }

            GUILayout.BeginArea(GameHudRect, GUI.skin.box);

            GUILayout.Label($"PuckHero — {RunLabel()}");
            GUILayout.Label(_gameOver ? $"** {_gameOverText} **" : $"Turn: {ActorName(_currentActor)}    {TurnPrompt()}");

            // The shop opens straight after a cleared run and is the only way on (design doc 2.1) —
            // the victory panel's 상점으로 button loads it. Defeat and the campaign clear need nothing
            // here either: their end screens load themselves.

            GUILayout.Label(_attackLog.Length > 0 ? _attackLog : "No attack yet.");
            GUILayout.Label(_aiming ? $"Power: {_currentPowerFraction * 100f:F0}%" : "Power: -");
            GUILayout.Label("F1: debug panel");

            GUILayout.EndArea();
        }

        // What the current actor is waiting on, for the HUD turn line.
        private string TurnPrompt()
        {
            if (_noStoneTurn)
            {
                return _currentActor != 0 && IsStage1Boss ? "(boss is warping the board…)" : "(no stones to roll)";
            }

            if (_tuning.EnemyAiEnabled && _currentActor != 0)
            {
                return _hasRolledThisTurn ? "(rolling…)" : "(enemy thinking…)";
            }

            if (_awaitingAttack)
            {
                return "(click an enemy to attack)";
            }

            if (_hasRolledThisTurn)
            {
                return "(rolling…)";
            }

            string roll = _ghostActive ? "roll a stone, or the new one on your edge" : "roll a highlighted stone";
            // The roll-skip is player-only, so only the player's prompt advertises it.
            return _currentActor == 0 ? roll + " — or click an enemy to attack now" : roll;
        }
    }
}
