using System.Collections.Generic;
using Puckmite.Sim;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace Puckmite.View
{
    /// <summary>
    /// Multi-puck sandbox that makes the deterministic <see cref="PuckSim"/> visible and touchable. It
    /// builds the camera, board, walls and pucks procedurally at runtime (design doc 7.9 — no scene
    /// file, no imported art), drives the sim with a fixed-timestep accumulator, and lets the player
    /// grab any puck (click near it, drag, release) to fling it into the others. Rendering only reads
    /// the sim; the one write is the launch velocity. Physics fields are placeholders (the real numbers
    /// are 미정 in the design doc) exposed as on-screen sliders so the feel can be tuned live.
    /// </summary>
    public sealed class PuckmitePlaytest : MonoBehaviour
    {
        [Header("Physics — temporary placeholders, tune by feel (values are 미정 in the design doc)")]
        [SerializeField] private float _friction = 10f;             // constant deceleration, units/s^2
        [SerializeField] private float _restitution = 1f;           // puck-to-puck bounciness
        [SerializeField] private float _collisionSpeedKept = 0.7f;  // speed kept after a puck-puck impact; 1 = no loss
        [SerializeField] private float _restThreshold = 0.4f;
        [SerializeField] private float _wallRestitution = 0.6f;     // reflected speed kept after a wall bounce
        [SerializeField] private float _maxPower = 50f;         // speed cap on a launch
        [SerializeField] private float _powerScale = 6f;        // drag distance (world units) -> launch speed
        [SerializeField] private float _powerCurve = 1f;        // drag->power exponent; 1 = linear
        [SerializeField] private float _puckRadius = 1.5f;      // design doc: diameter 3 on a 5-wide cell
        [SerializeField] private int _health = 5;               // stone health (design doc 3~5, 미정); 1..8 via HUD
        [SerializeField] private float _occupancyThreshold = 0.1f; // cross a cell boundary by this to occupy it (문서 3.2, 임시)
        [SerializeField] private int _cellDamage = 1;              // damage-cell settlement amount (문서 미정, 임시)

        [Header("Character stats — temporary placeholders (values are 미정 in the design doc)")]
        [SerializeField] private int _playerBaseHealth = 20;
        [SerializeField] private int _playerBaseAttack = 2;
        [SerializeField] private int _playerBaseShield = 0;
        [SerializeField] private int _enemyBaseHealth = 10;
        [SerializeField] private int _enemyBaseAttack = 1;
        [SerializeField] private int _enemyBaseShield = 0;

        [Header("New stone entry")]
        [SerializeField] private float _noStoneTurnDelay = 0.8f; // pause so a stoneless turn is readable

        [Header("Playback")]
        [SerializeField] private float _speedMultiplier = 1f;  // sim steps consumed per real second, x1/x2/x4

        // Board: 5 cells of side 5 => 25 units across, centred on the origin.
        private const float BoardHalf = 12.5f;
        private const float InnerHalf = 7.5f; // inner 3x3 buff zone spans cells 1..3
        private const float MinDrag = 0.3f;        // ignore tiny drags so a click does not fling
        private const float MaxAccumulated = 0.1f; // clamp so a hitch cannot trigger a step burst
        private const int MaxStepsPerFrame = 4000;

        // Character row (design doc 2.2: player + enemy characters across the top, board below).
        private const float CharBodyY = 18.5f;       // body centre height, above the board top (12.5)
        private const float CharBodyRadius = 1.6f;
        private const float CharStatOffset = -4.1f;  // name + stat block, centred below the body
        private const float CharStatHeight = 5f;     // its box height (name + HP + ATK + SHD + STONES lines)
        private const float CharSpread = 9f;         // x of the leftmost/rightmost character
        private const float CharRowTop = 20.4f;      // the camera frames up to here

        // Trajectory preview: safety cap on how many steps to roll the cue forward when tracing its path.
        private const int PreviewMaxSteps = 2000;

        // Health display: max arc segments drawn around a puck (matches the health slider's max).
        private const int MaxHealthArcs = 8;

        // Cell-occupancy highlights: pool cap (6 pucks * up to 4 cells, with margin).
        private const int MaxCellHighlights = 30;

        private static readonly Rect HudRect = new Rect(10f, 10f, 260f, 680f);

        // Highlight rings. Yellow is the default "this is yours to act on", used for the current actor's
        // stones and, during the attack phase, for the attacker and the target under the cursor; the faint
        // version marks a target that is merely available. Red marks a stone drawn back far enough to launch.
        private static readonly Color RingStrong = new Color(1f, 0.9f, 0.25f, 0.9f);
        private static readonly Color RingFaint = new Color(1f, 0.9f, 0.25f, 0.25f);
        private static readonly Color RingLaunchReady = new Color(1f, 0.35f, 0.25f, 0.95f);
        private static readonly Color RingBlocked = new Color(0.55f, 0.55f, 0.60f, 0.55f); // entry spot occupied

        // How close to its entry edge the cursor must be before the waiting new stone follows it.
        private const float GhostFollowRange = 6f;

        // Highlight rings reach this far out from a stone's centre, in radii. The entry spot is inset by it
        // so a waiting stone's ring clears the wall instead of being sliced by it.
        private const float RingRadiusScale = 1.25f;

        private PuckSim _sim;
        private Camera _camera;
        private SpriteRenderer[] _puckViews; // indexed by puck Id (layout uses contiguous Ids from 0)
        private LineRenderer[][] _healthArcs; // [puckId][arc]: health shown as arc segments around the rim
        private Material _arcMaterial;        // shared by every health arc
        private TextMeshPro[] _levelTexts;      // [puckId]: level number (world-space TMP; a placeholder to restyle later)
        private MeshRenderer[] _levelRenderers; // [puckId]: the level text's renderer, toggled for show/hide
        private MeshRenderer[] _xpFillRenderers; // [puckId]: XP fill renderer (circle-segment mesh)
        private Mesh[] _xpFillMeshes;            // [puckId]: the fill mesh, regenerated when fraction changes
        private float[] _xpFillFraction;         // [puckId]: last drawn fraction (-1 = none yet)

        // Reused buffers for building the XP fill mesh (one puck at a time, no per-frame allocation).
        private static readonly List<Vector3> _fillVerts = new List<Vector3>();
        private static readonly List<int> _fillTris = new List<int>();
        private static readonly List<Color> _fillColors = new List<Color>();
        private SpriteRenderer[] _cellHighlights;                    // pool of occupied-cell overlays
        private readonly List<int> _occupiedCells = new List<int>(); // reused per puck each frame
        private LineRenderer _previewLine;
        private SpriteRenderer _previewMarker; // ghost circle at the cue's predicted final position
        private readonly List<Vector3> _previewPoints = new List<Vector3>();
        private float _previewMs; // last preview compute time, shown in the HUD

        private float _accumulator;
        private bool _aiming;
        private int _aimingPuckId = -1;
        private bool _launchReady; // the current drag is long enough to actually fling the aimed stone
        private float _currentPowerFraction;

        // Turn structure (view-only orchestration; the sim stays pure physics/combat).
        private int[] _actorOf;              // puck id -> actor (0 = player, 1.. = each enemy)
        private int _actorCount;
        private int _currentActor;
        private bool _hasRolledThisTurn;
        private SpriteRenderer[] _turnRings; // highlight behind the current actor's stones
        private readonly List<int> _settleIds = new List<int>(); // reused: current actor's stone ids to settle

        // Top character row (design doc 2.2): one stat text per actor. Each actor's buff is a snapshot taken
        // when its own turn ends (Σ cellValue*stoneLevel), held until its next turn (design doc 3.6/3.7).
        private TextMeshPro[] _characterStatTexts; // indexed by actor
        private SpriteRenderer[] _characterBodies;      // indexed by actor, greyed out when the character is down
        private SpriteRenderer[] _characterTargetRings; // ring behind a character that can be attacked right now
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

        // New stone entry (design doc 3.3/3.4). The hand holds the ids of destroyed stones waiting to come
        // back; an id reused this way keeps every id-indexed view array and _actorOf valid. Pending becomes
        // ready at the owner's next turn start, so a stone lost this turn cannot be played this turn.
        private List<int>[] _handReady;
        private List<int>[] _handPending;

        // The stone waiting on the entry edge. It lives here rather than in the sim until it is launched.
        private bool _ghostActive;
        private Puck _ghost;
        private bool _ghostBlocked; // its spot overlaps a stone already on the board, so it cannot launch
        private SpriteRenderer _ghostView;
        private SpriteRenderer _ghostRing;

        // A turn with nothing to roll: show that, then attack with base damage and end it (design doc 3.5).
        private bool _noStoneTurn;
        private float _noStoneTimer;

        private int _wallBounceTotal; // session tallies, straight from Step()'s events
        private int _collisionTotal;
        private int _destroyedTotal;

        // Physics values currently baked into _sim; a mismatch with the fields triggers a rebuild.
        private float _appliedFriction;
        private float _appliedRestitution;
        private float _appliedRestThreshold;
        private float _appliedWallRestitution;
        private float _appliedCollisionSpeedKept;
        private int _appliedHealth;

        private void Awake()
        {
            Build();
        }

        private void Update()
        {
            // A script recompile during Play triggers a domain reload that clears runtime fields (_sim,
            // views) without calling Awake again. Rebuild when that happens so the sandbox self-heals
            // instead of throwing a NullReferenceException every frame.
            if (_sim == null)
            {
                Build();
            }

            RebuildIfPhysicsChanged();
            ApplyHealthChangeIfNeeded();
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
                    BeginAttackPhase();
                }
            }

            // Rolled and the board has settled: lock in the buff (step 3), then attack (step 4).
            if (!_gameOver && !_awaitingAttack && _hasRolledThisTurn && _sim.AllAtRest())
            {
                CaptureActorBuff(_currentActor);
                BeginAttackPhase();
            }

            UpdatePuckTransforms();
            UpdateCellHighlights();
            UpdateTurnHighlights();
            UpdateGhost();
            UpdateCharacterStats();
        }

        // Builds (or rebuilds) everything this component owns. Destroys any children a previous build left
        // so rebuilding after a domain reload does not stack duplicate boards and pucks.
        private void Build()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Destroy(transform.GetChild(i).gameObject);
            }

            BuildSimFrom(InitialLayout());
            AssignActors();
            BuildCamera();
            BuildBoard();
            BuildCellHighlights();
            BuildPuckViews();
            BuildTurnRings();
            BuildCharacters();
            BuildGhost();
            BuildPreviewLine();
            BuildPreviewMarker();
            StartTurn();
            UpdatePuckTransforms();
            UpdateCharacterStats();
        }

        // --- Simulation ---------------------------------------------------------------------------

        private List<Puck> InitialLayout()
        {
            // Player pucks down the left ("hand" side), an enemy cluster in the middle, spaced so none
            // start overlapping. Ids are contiguous from 0 so the view array can be indexed by Id.
            return new List<Puck>
            {
                new Puck(0, new Vector2(-9f, -9f), _puckRadius, 1f, PuckOwner.Player) { Health = _health },
                new Puck(1, new Vector2(-9f, 0f), _puckRadius, 1f, PuckOwner.Player) { Health = _health },
                new Puck(2, new Vector2(-9f, 9f), _puckRadius, 1f, PuckOwner.Player) { Health = _health },
                new Puck(3, new Vector2(0f, 0f), _puckRadius, 1f, PuckOwner.Enemy) { Health = _health },
                new Puck(4, new Vector2(4f, 4f), _puckRadius, 1f, PuckOwner.Enemy) { Health = _health },
                new Puck(5, new Vector2(4f, -4f), _puckRadius, 1f, PuckOwner.Enemy) { Health = _health },
            };
        }

        private void BuildSimFrom(List<Puck> pucks)
        {
            _sim = new PuckSim(
                new Vector2(-BoardHalf, -BoardHalf),
                new Vector2(BoardHalf, BoardHalf),
                new PuckSimConfig(_friction, _restitution, _restThreshold, _wallRestitution, _collisionSpeedKept));

            for (int i = 0; i < pucks.Count; i++)
            {
                _sim.AddPuck(pucks[i]);
            }

            _appliedFriction = _friction;
            _appliedRestitution = _restitution;
            _appliedRestThreshold = _restThreshold;
            _appliedWallRestitution = _wallRestitution;
            _appliedCollisionSpeedKept = _collisionSpeedKept;
            _appliedHealth = _health;
        }

        private void RebuildIfPhysicsChanged()
        {
            bool changed =
                _friction != _appliedFriction ||
                _restitution != _appliedRestitution ||
                _restThreshold != _appliedRestThreshold ||
                _wallRestitution != _appliedWallRestitution ||
                _collisionSpeedKept != _appliedCollisionSpeedKept;
            if (!changed)
            {
                return;
            }

            // Rebuild in place, preserving every puck exactly as it is now (position, velocity, bounces),
            // so a slider tweak is felt immediately even mid-roll.
            BuildSimFrom(SnapshotPucks());
        }

        private List<Puck> SnapshotPucks()
        {
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            List<Puck> copy = new List<Puck>(pucks.Count);
            for (int i = 0; i < pucks.Count; i++)
            {
                copy.Add(pucks[i]);
            }

            return copy;
        }

        private void DriveSimulation()
        {
            if (_sim.AllAtRest())
            {
                _accumulator = 0f; // start each fling from a clean clock
                return;
            }

            _accumulator += Time.deltaTime * Mathf.Max(0f, _speedMultiplier);
            if (_accumulator > MaxAccumulated)
            {
                _accumulator = MaxAccumulated;
            }

            int steps = 0;
            while (_accumulator >= PuckSim.Dt && steps < MaxStepsPerFrame)
            {
                IReadOnlyList<PuckSimEvent> events = _sim.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Type == PuckSimEventType.WallBounce)
                    {
                        _wallBounceTotal++;
                    }
                    else if (events[i].Type == PuckSimEventType.PuckCollision)
                    {
                        _collisionTotal++;
                    }
                    else if (events[i].Type == PuckSimEventType.PuckDestroyed)
                    {
                        _destroyedTotal++;
                        ReturnStoneToHand(events[i].PuckA); // comes back as a fresh stone (design doc 3.3)
                    }
                }

                _accumulator -= PuckSim.Dt;
                steps++;
            }
        }

        private void ResetPucks()
        {
            BuildSimFrom(InitialLayout());
            AssignActors();
            ResetCombatState();
            _accumulator = 0f;
            _wallBounceTotal = 0;
            _collisionTotal = 0;
            _destroyedTotal = 0;
            StartTurn();
            UpdatePuckTransforms();
        }

        // Assigns each puck to a turn actor from the fixed roster: the player is actor 0 (owns all its
        // stones); each enemy is its own actor (1, 2, 3, ...). Combat teams (Owner) are unchanged.
        private void AssignActors()
        {
            List<Puck> roster = InitialLayout();
            int maxId = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i].Id > maxId)
                {
                    maxId = roster[i].Id;
                }
            }

            _actorOf = new int[maxId + 1];
            int nextEnemy = 1;
            for (int i = 0; i < roster.Count; i++)
            {
                Puck p = roster[i];
                _actorOf[p.Id] = p.Owner == PuckOwner.Player ? 0 : nextEnemy++;
            }

            _actorCount = nextEnemy; // player (0) + enemies (1 .. nextEnemy-1)
            _currentActor = 0;
            _hasRolledThisTurn = false;
        }

        // Begins _currentActor's turn: hand stones lost earlier become playable, then its own stones settle
        // on the damage cells (design doc 3.5 step 1). Only a character that is down is skipped. An actor
        // with nothing to roll still takes its turn — it just goes straight to its attack.
        private void StartTurn()
        {
            _hasRolledThisTurn = false;
            _awaitingAttack = false;
            _noStoneTurn = false;
            ClearGhost();

            for (int step = 0; step < _actorCount; step++)
            {
                if (!_actorDead[_currentActor])
                {
                    PromoteHand(_currentActor);    // playable from this turn on (design doc 3.3)
                    ClearActorBuff(_currentActor); // turn start: back to base only (design doc 3.6)
                    SettleCurrentActor();          // stones lost here go back to the hand as pending

                    if (ActorHasLiveStones(_currentActor) || _handReady[_currentActor].Count > 0)
                    {
                        SetupGhost(_currentActor);
                        return;
                    }

                    // Nothing on the board and nothing playable in hand: no roll is possible. Hold a beat so
                    // that is visible, then attack with base damage and end the turn (design doc 3.5).
                    _noStoneTurn = true;
                    _noStoneTimer = _noStoneTurnDelay;
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
            _currentActor = (_currentActor + 1) % _actorCount;
            StartTurn();
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

            _sim.SettleDamageCells(_settleIds, _cellDamage, _occupancyThreshold);

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

        private static string ActorName(int actor)
        {
            return actor == 0 ? "Player" : $"Enemy {actor}";
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

            UpdateGhostAim(world);

            if (mouse.leftButton.wasPressedThisFrame && !PointerOverHud(screen) && !_hasRolledThisTurn)
            {
                // The waiting new stone is grabbed like any other; a board stone is the cue-shot choice
                // (design doc 3.5 — the actor picks one or the other).
                if (_ghostActive && (world - _ghost.Position).magnitude <= GrabRadius())
                {
                    _aiming = true;
                    _aimingPuckId = _ghost.Id;
                }
                else
                {
                    int id = NearestPuckId(world);
                    if (id >= 0)
                    {
                        _aiming = true;
                        _aimingPuckId = id;
                    }
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
                    float power = _maxPower * _currentPowerFraction;
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
                        float power = _maxPower * DragToPowerFraction(drag.magnitude);
                        Vector2 velocity = drag.normalized * power;
                        if (wasGhost)
                        {
                            LaunchGhost(velocity);
                        }
                        else
                        {
                            _sim.SetVelocity(_aimingPuckId, velocity);
                        }

                        _accumulator = 0f;
                        _hasRolledThisTurn = true; // one forced roll per turn (design doc 3.5)
                    }
                }

                _aimingPuckId = -1;
            }
        }

        private bool IsAimingGhost()
        {
            return _ghostActive && _aiming && _aimingPuckId == _ghost.Id;
        }

        // Where the stone being aimed sits — the waiting new stone lives outside the sim, so it is read
        // from the ghost rather than looked up by Id.
        private bool TryGetAimedPosition(out Vector2 position)
        {
            if (IsAimingGhost())
            {
                position = _ghost.Position;
                return true;
            }

            if (_sim.TryGetPuck(_aimingPuckId, out Puck p))
            {
                position = p.Position;
                return true;
            }

            position = Vector2.zero;
            return false;
        }

        private float GrabRadius()
        {
            return Mathf.Max(_puckRadius * 2.5f, 3f);
        }

        // Cue-shot aiming: the cursor is pulled back behind the stone and the stone flies the opposite way,
        // so the launch vector points from the cursor to the stone. Its length is the drag distance.
        private static Vector2 PullbackDrag(Vector2 puckPosition, Vector2 cursor)
        {
            return puckPosition - cursor;
        }

        // Closest puck to the point, if within a forgiving grab radius; -1 if the click is in open space.
        private int NearestPuckId(Vector2 world)
        {
            float grab = GrabRadius();
            float bestSqr = grab * grab;
            int best = -1;

            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                if (_actorOf[pucks[i].Id] != _currentActor) // only the current actor's own stones
                {
                    continue;
                }

                float sqr = (pucks[i].Position - world).sqrMagnitude;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    best = pucks[i].Id;
                }
            }

            return best;
        }

        private Vector2 ScreenToWorld(Vector2 screen)
        {
            Vector3 world = _camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -_camera.transform.position.z));
            return new Vector2(world.x, world.y);
        }

        private bool PointerOverHud(Vector2 screen)
        {
            // Mouse.position is y-up from the bottom; GUI/HudRect is y-down from the top.
            return HudRect.Contains(new Vector2(screen.x, Screen.height - screen.y));
        }

        // --- View construction --------------------------------------------------------------------

        private void BuildCamera()
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                GameObject camObject = new GameObject("Main Camera") { tag = "MainCamera" };
                _camera = camObject.AddComponent<Camera>();
            }

            // URP renders through a per-camera data component; the scene's Main Camera already has one.
            if (!_camera.TryGetComponent(out UniversalAdditionalCameraData _))
            {
                _camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            }

            _camera.orthographic = true;
            _camera.transform.rotation = Quaternion.identity;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.07f, 0.08f, 0.10f);

            // Frame the board plus the character row above it (design doc 2.2: characters on top, board below),
            // making sure the board still fits horizontally. The board ends up below screen centre as a result.
            const float contentTop = CharRowTop;
            const float contentBottom = -BoardHalf;
            float centerY = (contentTop + contentBottom) * 0.5f;
            _camera.transform.position = new Vector3(0f, centerY, -10f);

            float margin = 1.08f;
            float aspect = Mathf.Max(0.0001f, _camera.aspect);
            float halfHeight = (contentTop - contentBottom) * 0.5f * margin;
            float halfWidth = BoardHalf * margin;
            _camera.orthographicSize = Mathf.Max(halfHeight, halfWidth / aspect);
        }

        private void BuildBoard()
        {
            Transform board = new GameObject("Board").transform;
            board.SetParent(transform, false);

            float full = BoardHalf * 2f;

            MakeQuad("Background", board, Vector2.zero, new Vector2(full, full), new Color(0.16f, 0.17f, 0.20f), 0);
            // Inner 3x3 buff cells, coloured by kind (attack/shield) and brighter toward the stronger centre.
            Vector2 boardMin = new Vector2(-BoardHalf, -BoardHalf);
            Vector2 boardMax = new Vector2(BoardHalf, BoardHalf);
            Vector2 buffCellSize = BoardCells.CellSize(boardMin, boardMax);
            for (int row = 1; row <= 3; row++)
            {
                for (int col = 1; col <= 3; col++)
                {
                    Vector2 center = BoardCells.CellCenter(boardMin, boardMax, col, row);
                    MakeQuad("BuffCell", board, center, buffCellSize, BuffCellColor(col, row), 1);
                }
            }

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

        private static SpriteRenderer MakeQuad(string name, Transform parent, Vector2 center, Vector2 size, Color color, int sortingOrder)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(center.x, center.y, 0f);
            go.transform.localScale = new Vector3(size.x, size.y, 1f);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = ProceduralSprites.Unit();
            sr.color = color;
            sr.sortingOrder = sortingOrder;
            return sr;
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

        private void BuildPuckViews()
        {
            _arcMaterial = new Material(Shader.Find("Sprites/Default"));

            IReadOnlyList<Puck> pucks = _sim.Pucks;
            _puckViews = new SpriteRenderer[pucks.Count];
            _healthArcs = new LineRenderer[pucks.Count][];
            _levelTexts = new TextMeshPro[pucks.Count];
            _levelRenderers = new MeshRenderer[pucks.Count];
            _xpFillRenderers = new MeshRenderer[pucks.Count];
            _xpFillMeshes = new Mesh[pucks.Count];
            _xpFillFraction = new float[pucks.Count];
            for (int i = 0; i < pucks.Count; i++)
            {
                Puck p = pucks[i];
                GameObject go = new GameObject($"Puck{p.Id}");
                go.transform.SetParent(transform, false);

                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = ProceduralSprites.Circle();
                sr.color = OwnerColor(p.Owner);
                sr.sortingOrder = 10;
                _puckViews[p.Id] = sr;

                // Level number: world-space TMP that auto-sizes into a puck-sized box (placeholder to restyle later).
                GameObject levelGo = new GameObject($"Puck{p.Id}_Level");
                levelGo.transform.SetParent(transform, false);
                TextMeshPro tmp = levelGo.AddComponent<TextMeshPro>();
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.enableAutoSizing = true;
                tmp.fontSizeMin = 1f;
                tmp.fontSizeMax = 300f;
                tmp.rectTransform.sizeDelta = new Vector2(p.Radius * 1.3f, p.Radius * 1.5f);
                tmp.color = Color.white;
                MeshRenderer levelMr = levelGo.GetComponent<MeshRenderer>();
                levelMr.sortingOrder = 12; // above the puck circle and arcs
                levelMr.enabled = false;
                _levelTexts[p.Id] = tmp;
                _levelRenderers[p.Id] = levelMr;

                // XP fill: circle-segment mesh that fills like liquid, clipped to the circle. Regenerated
                // only when the fraction changes; the object just follows the puck.
                GameObject fillGo = new GameObject($"Puck{p.Id}_XpFill");
                fillGo.transform.SetParent(transform, false);
                MeshRenderer fillMr = fillGo.AddComponent<MeshRenderer>();
                fillMr.sharedMaterial = _arcMaterial; // Sprites/Default; the mesh carries the green vertex colours
                fillMr.sortingOrder = 11;             // above the puck circle, below the level number
                fillMr.enabled = false;
                Mesh fillMesh = new Mesh { name = $"XpFill{p.Id}" };
                fillGo.AddComponent<MeshFilter>().sharedMesh = fillMesh;
                _xpFillRenderers[p.Id] = fillMr;
                _xpFillMeshes[p.Id] = fillMesh;
                _xpFillFraction[p.Id] = -1f;

                LineRenderer[] arcs = new LineRenderer[MaxHealthArcs];
                for (int a = 0; a < MaxHealthArcs; a++)
                {
                    GameObject arcGo = new GameObject($"Puck{p.Id}_HealthArc{a}");
                    arcGo.transform.SetParent(transform, false);

                    LineRenderer lr = arcGo.AddComponent<LineRenderer>();
                    lr.sharedMaterial = _arcMaterial;
                    lr.useWorldSpace = true;
                    lr.widthMultiplier = 0.16f;
                    lr.numCapVertices = 1;
                    lr.numCornerVertices = 1;
                    lr.startColor = new Color(1f, 1f, 1f, 0.95f);
                    lr.endColor = new Color(1f, 1f, 1f, 0.95f);
                    lr.sortingOrder = 11; // above the puck circle
                    lr.enabled = false;
                    arcs[a] = lr;
                }

                _healthArcs[p.Id] = arcs;
            }
        }

        private static Color OwnerColor(PuckOwner owner)
        {
            return owner == PuckOwner.Player ? new Color(0.30f, 0.75f, 1f) : new Color(1f, 0.45f, 0.35f);
        }

        // The waiting new stone and its ring. Built once like every other renderer here and toggled per frame.
        private void BuildGhost()
        {
            GameObject ringGo = new GameObject("EntryGhostRing");
            ringGo.transform.SetParent(transform, false);
            _ghostRing = ringGo.AddComponent<SpriteRenderer>();
            _ghostRing.sprite = ProceduralSprites.Circle();
            _ghostRing.sortingOrder = 8; // same layer as the stone turn rings
            _ghostRing.enabled = false;

            GameObject go = new GameObject("EntryGhost");
            go.transform.SetParent(transform, false);
            _ghostView = go.AddComponent<SpriteRenderer>();
            _ghostView.sprite = ProceduralSprites.Circle();
            _ghostView.sortingOrder = 9; // just under the stones on the board
            _ghostView.enabled = false;
        }

        private void BuildPreviewLine()
        {
            GameObject go = new GameObject("PreviewLine");
            go.transform.SetParent(transform, false);

            _previewLine = go.AddComponent<LineRenderer>();
            _previewLine.material = new Material(Shader.Find("Sprites/Default"));
            _previewLine.useWorldSpace = true;
            _previewLine.widthMultiplier = 0.15f;
            _previewLine.numCapVertices = 2;
            _previewLine.numCornerVertices = 2;
            _previewLine.textureMode = LineTextureMode.Stretch;
            _previewLine.startColor = new Color(0.55f, 0.95f, 1f, 0.9f);
            _previewLine.endColor = new Color(0.55f, 0.95f, 1f, 0.35f);
            _previewLine.sortingOrder = 12;
            _previewLine.positionCount = 0;
            _previewLine.enabled = false;
        }

        private void BuildPreviewMarker()
        {
            GameObject go = new GameObject("PreviewMarker");
            go.transform.SetParent(transform, false);

            _previewMarker = go.AddComponent<SpriteRenderer>();
            _previewMarker.sprite = ProceduralSprites.Circle();
            _previewMarker.color = new Color(0.55f, 0.95f, 1f, 0.35f); // translucent cyan "landing" ghost
            _previewMarker.sortingOrder = 9; // just under the pucks
            _previewMarker.enabled = false;
        }

        private void BuildCellHighlights()
        {
            _cellHighlights = new SpriteRenderer[MaxCellHighlights];
            for (int i = 0; i < MaxCellHighlights; i++)
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
            int count = _sim.Pucks.Count;
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

        // Builds the top character row: one placeholder widget per actor (design doc 2.2) — a body circle in
        // the team colour with a name + stat block below it that UpdateCharacterStats refreshes.
        private void BuildCharacters()
        {
            _characterStatTexts = new TextMeshPro[_actorCount];
            _characterBodies = new SpriteRenderer[_actorCount];
            _characterTargetRings = new SpriteRenderer[_actorCount];
            _actorBuffAttack = new int[_actorCount];
            _actorHealth = new int[_actorCount];
            _actorBaseShield = new int[_actorCount];
            _actorEffectShield = new int[_actorCount];
            _actorDead = new bool[_actorCount];
            _handReady = new List<int>[_actorCount];
            _handPending = new List<int>[_actorCount];
            for (int actor = 0; actor < _actorCount; actor++)
            {
                _handReady[actor] = new List<int>();
                _handPending[actor] = new List<int>();
            }

            ResetCombatState();

            for (int actor = 0; actor < _actorCount; actor++)
            {
                float x = CharacterX(actor);

                GameObject root = new GameObject($"Character{actor}");
                root.transform.SetParent(transform, false);

                // Target ring: same treatment the current actor's stones get, so "clickable" reads the same way.
                GameObject ringGo = new GameObject("TargetRing");
                ringGo.transform.SetParent(root.transform, false);
                float ringDiameter = CharBodyRadius * 2.5f;
                ringGo.transform.localPosition = new Vector3(x, CharBodyY, 0f);
                ringGo.transform.localScale = new Vector3(ringDiameter, ringDiameter, 1f);
                SpriteRenderer ring = ringGo.AddComponent<SpriteRenderer>();
                ring.sprite = ProceduralSprites.Circle();
                ring.color = RingStrong;
                ring.sortingOrder = 9; // behind the body (10), above the board
                ring.enabled = false;
                _characterTargetRings[actor] = ring;

                GameObject bodyGo = new GameObject("Body");
                bodyGo.transform.SetParent(root.transform, false);
                float diameter = CharBodyRadius * 2f;
                bodyGo.transform.localPosition = new Vector3(x, CharBodyY, 0f);
                bodyGo.transform.localScale = new Vector3(diameter, diameter, 1f);
                SpriteRenderer body = bodyGo.AddComponent<SpriteRenderer>();
                body.sprite = ProceduralSprites.Circle();
                body.color = ActorColor(actor);
                body.sortingOrder = 10;
                _characterBodies[actor] = body;

                _characterStatTexts[actor] = MakeCharacterText(root.transform, "Stats", x, CharBodyY + CharStatOffset, CharBodyRadius * 3.6f, CharStatHeight);
            }
        }

        // A world-space TMP that auto-sizes into the given box (placeholder to restyle later, like the level text).
        private static TextMeshPro MakeCharacterText(Transform parent, string name, float x, float y, float width, float height)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(x, y, 0f);

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.alignment = TextAlignmentOptions.Center;
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

        // Writes each actor's character row: bold name, current/max health, and attack/shield including the
        // buff snapshot locked in at that actor's last turn end (design doc 3.6 — held until its next turn).
        // The body is tinted grey when the character is down, and brightened while it is a legal target.
        private void UpdateCharacterStats()
        {
            if (_characterStatTexts == null)
            {
                return;
            }

            for (int actor = 0; actor < _actorCount; actor++)
            {
                if (_actorDead[actor])
                {
                    _characterStatTexts[actor].text = $"<b>{ActorName(actor)}</b>\nDOWN";
                    _characterBodies[actor].color = new Color(0.30f, 0.30f, 0.34f, 0.55f);
                    _characterTargetRings[actor].enabled = false;
                    continue;
                }

                string attack = FormatStat(BaseAttack(actor), _actorBuffAttack[actor]);
                string shield = FormatStat(_actorBaseShield[actor], _actorEffectShield[actor]);
                string stones = FormatStat(_handReady[actor].Count, _handPending[actor].Count);
                _characterStatTexts[actor].text =
                    $"<b>{ActorName(actor)}</b>\nHP {_actorHealth[actor]}/{BaseHealth(actor)}\nATK {attack}\nSHD {shield}\nSTONES {stones}";

                _characterBodies[actor].color = ActorColor(actor);
                UpdateCharacterRing(actor);
            }
        }

        // Ring states while the player is picking a target (design doc 3.5 step 4): the attacker is ringed
        // so it is clear who is acting, every enemy it may hit gets a faint ring, and the one under the
        // cursor lights up fully. Outside the attack phase no character is ringed.
        private void UpdateCharacterRing(int actor)
        {
            SpriteRenderer ring = _characterTargetRings[actor];
            if (!_awaitingAttack)
            {
                ring.enabled = false;
                return;
            }

            bool strong = actor == _currentActor || actor == _hoveredCharacter;
            ring.color = strong ? RingStrong : RingFaint;
            ring.enabled = true;
        }

        // Base value, plus the bonus in parentheses when buffed, so the turn-end gain is visible (e.g. "6 (+4)").
        private static string FormatStat(int baseValue, int buff)
        {
            return buff > 0 ? $"{baseValue + buff} (+{buff})" : baseValue.ToString();
        }

        // Turn end (design doc 3.5 step 3): lock in the actor's buff from the cells its stones occupy, each
        // cell's value multiplied by that stone's level (design doc 3.7 growth). Held until its next turn.
        private void CaptureActorBuff(int actor)
        {
            if (_actorBuffAttack == null)
            {
                return;
            }

            int attack = 0;
            int shield = 0;
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

                BoardCells.SumBuffs(boardMin, boardMax, p.Position, p.Radius, _occupancyThreshold, out int a, out int s);
                attack += a * p.Level;
                shield += s * p.Level;
            }

            _actorBuffAttack[actor] = attack;
            _actorEffectShield[actor] = shield; // effect shield refills to the buff amount (design doc 3.6)
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

        // --- Hand and new stone entry (design doc 3.3/3.4) ------------------------------------------

        // A destroyed stone becomes a fresh stone in its owner's hand. Its Id is reused, which is what keeps
        // every Id-indexed view array and _actorOf valid — a brand new Id would run off the end of them.
        private void ReturnStoneToHand(int puckId)
        {
            if (_handPending == null || puckId < 0 || puckId >= _actorOf.Length)
            {
                return;
            }

            int actor = _actorOf[puckId];
            if (_actorDead[actor])
            {
                return; // a character that is out does not get its stones back
            }

            if (!_handPending[actor].Contains(puckId) && !_handReady[actor].Contains(puckId))
            {
                _handPending[actor].Add(puckId);
            }
        }

        // Turn start: stones that came back earlier become playable now — "다음 턴부터 사용 가능" (design doc
        // 3.3). Anything lost later this turn lands in pending and so waits for the turn after.
        private void PromoteHand(int actor)
        {
            for (int i = 0; i < _handPending[actor].Count; i++)
            {
                _handReady[actor].Add(_handPending[actor][i]);
            }

            _handPending[actor].Clear();
        }

        // Puts the next playable stone on the actor's entry edge, ready to be aimed. No ready stone, no ghost.
        private void SetupGhost(int actor)
        {
            if (_handReady[actor].Count == 0)
            {
                ClearGhost();
                return;
            }

            PuckOwner owner = actor == 0 ? PuckOwner.Player : PuckOwner.Enemy;
            _ghost = new Puck(_handReady[actor][0], EntryPoint(actor, 0f), _puckRadius, 1f, owner) { Health = _health };
            _ghostActive = true;
            _ghostBlocked = false;
        }

        private void ClearGhost()
        {
            _ghostActive = false;
            _ghostBlocked = false;
        }

        // The entry edge: the player's new stones come in on the left, an enemy's on the right, hugging that
        // wall (design doc 3.4). The inset is the highlight ring's reach rather than the stone's radius, so
        // the ring is not cut off by the wall; it stays well inside PuckSim's wall clamp, so the stone does
        // not jump on its first step, and still sits squarely in the entry damage column. Only y varies — a
        // new stone slides along its own edge and no further.
        private Vector2 EntryPoint(int actor, float y)
        {
            float inset = _puckRadius * RingRadiusScale;
            float x = actor == 0 ? _sim.BoardMin.x + inset : _sim.BoardMax.x - inset;
            float minY = _sim.BoardMin.y + inset;
            float maxY = _sim.BoardMax.y - inset;
            return new Vector2(x, Mathf.Clamp(y, minY, maxY));
        }

        // Slides the waiting stone along its edge to follow the cursor, and checks whether that spot is free.
        private void UpdateGhostAim(Vector2 world)
        {
            if (!_ghostActive || _hasRolledThisTurn)
            {
                return;
            }

            if (!_aiming && Mathf.Abs(world.x - _ghost.Position.x) <= GhostFollowRange)
            {
                _ghost.Position = EntryPoint(_currentActor, world.y);
            }

            // Launching from inside another stone would shove it aside for free, so that spot is refused.
            _ghostBlocked = false;
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                if ((pucks[i].Position - _ghost.Position).magnitude < pucks[i].Radius + _ghost.Radius)
                {
                    _ghostBlocked = true;
                    return;
                }
            }
        }

        // The new stone enters the board where the ghost stands and rolls in the same motion (design doc 3.5).
        private void LaunchGhost(Vector2 velocity)
        {
            _handReady[_currentActor].Remove(_ghost.Id);

            Puck stone = _ghost;
            stone.Health = _health;
            stone.Velocity = velocity;
            _sim.AddPuck(stone);

            _xpFillFraction[stone.Id] = -1f; // drop the fill cached for whatever last used this Id
            ClearGhost();
        }

        private void UpdateGhost()
        {
            if (_ghostView == null)
            {
                return;
            }

            if (!_ghostActive || _hasRolledThisTurn)
            {
                _ghostView.enabled = false;
                _ghostRing.enabled = false;
                return;
            }

            float diameter = _ghost.Radius * 2f;
            Vector3 position = new Vector3(_ghost.Position.x, _ghost.Position.y, 0f);
            _ghostView.transform.localPosition = position;
            _ghostView.transform.localScale = new Vector3(diameter, diameter, 1f);

            Color body = OwnerColor(_ghost.Owner);
            body.a = _ghostBlocked ? 0.2f : 0.5f; // translucent: it is not on the board yet
            _ghostView.color = body;
            _ghostView.enabled = true;

            float ringDiameter = _ghost.Radius * RingRadiusScale * 2f;
            _ghostRing.transform.localPosition = position;
            _ghostRing.transform.localScale = new Vector3(ringDiameter, ringDiameter, 1f);
            _ghostRing.color = GhostRingColor();
            _ghostRing.enabled = true;
        }

        private Color GhostRingColor()
        {
            if (_ghostBlocked)
            {
                return RingBlocked;
            }

            if (!IsAimingGhost())
            {
                return RingFaint; // available, but not the stone being aimed
            }

            return _launchReady ? RingLaunchReady : RingStrong;
        }

        // --- Character combat (design doc 3.5 step 4, 3.6, 3.8) --------------------------------------

        // Restores every character to full: health, base shield pool, no buffs, nobody down, game on.
        private void ResetCombatState()
        {
            for (int actor = 0; actor < _actorCount; actor++)
            {
                _actorHealth[actor] = BaseHealth(actor);
                _actorBaseShield[actor] = BaseShield(actor);
                _actorEffectShield[actor] = 0;
                _actorBuffAttack[actor] = 0;
                _actorDead[actor] = false;
                _handReady[actor].Clear();
                _handPending[actor].Clear();
            }

            _awaitingAttack = false;
            _gameOver = false;
            _gameOverText = "";
            _attackLog = "";
            _noStoneTurn = false;
            ClearGhost();
        }

        // Turn step 4: the attack is forced, one target. The player picks by clicking an enemy character;
        // an enemy has only the player to hit, so it fires at once (no AI yet — that is the next step).
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

        // Design doc 3.8: every enemy down wins the match, the player going down ends it.
        private void CheckGameOver()
        {
            if (_actorDead[0])
            {
                _gameOver = true;
                _gameOverText = "Defeat — the player is down.";
                return;
            }

            for (int actor = 1; actor < _actorCount; actor++)
            {
                if (!_actorDead[actor])
                {
                    return;
                }
            }

            _gameOver = true;
            _gameOverText = "Victory — every enemy is down.";
        }

        // The character whose body circle covers the point, or -1. Used to pick an attack target.
        private int CharacterAt(Vector2 world)
        {
            float grab = CharBodyRadius * 1.4f; // forgiving, the bodies are far apart
            for (int actor = 0; actor < _actorCount; actor++)
            {
                Vector2 center = new Vector2(CharacterX(actor), CharBodyY);
                if ((world - center).sqrMagnitude <= grab * grab)
                {
                    return actor;
                }
            }

            return -1;
        }

        private int BaseHealth(int actor)
        {
            return actor == 0 ? _playerBaseHealth : _enemyBaseHealth;
        }

        private int BaseAttack(int actor)
        {
            return actor == 0 ? _playerBaseAttack : _enemyBaseAttack;
        }

        private int BaseShield(int actor)
        {
            return actor == 0 ? _playerBaseShield : _enemyBaseShield;
        }

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
                BoardCells.GetOccupiedCells(boardMin, boardMax, p.Position, p.Radius, _occupancyThreshold, _occupiedCells);

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

        private void UpdatePuckTransforms()
        {
            // Hide every puck and its arcs, then show only those still alive in the sim. Destroyed pucks
            // (removed from the sim) simply stay hidden, and a reset restores them, with no extra tracking.
            for (int id = 0; id < _puckViews.Length; id++)
            {
                _puckViews[id].enabled = false;
                _levelRenderers[id].enabled = false;
                _xpFillRenderers[id].enabled = false;
                LineRenderer[] arcs = _healthArcs[id];
                for (int a = 0; a < arcs.Length; a++)
                {
                    arcs[a].enabled = false;
                }
            }

            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                Puck p = pucks[i];
                SpriteRenderer sr = _puckViews[p.Id];
                sr.enabled = true;
                float diameter = p.Radius * 2f;
                sr.transform.localPosition = new Vector3(p.Position.x, p.Position.y, 0f);
                sr.transform.localScale = new Vector3(diameter, diameter, 1f);

                DrawHealthArcs(p);
                DrawLevelAndXp(p);
            }
        }

        // Level number at the puck centre, and the XP progress as a fill rising from the bottom (design doc 6.3).
        private void DrawLevelAndXp(Puck p)
        {
            _levelTexts[p.Id].text = p.Level.ToString();
            _levelTexts[p.Id].transform.localPosition = new Vector3(p.Position.x, p.Position.y, 0f);
            _levelRenderers[p.Id].enabled = true;

            MeshRenderer fill = _xpFillRenderers[p.Id];
            fill.transform.localPosition = new Vector3(p.Position.x, p.Position.y, 0f);

            float fraction = (float)p.Xp / PuckSim.XpPerLevel;
            if (fraction <= 0f)
            {
                fill.enabled = false;
                return;
            }

            // Fill radius stays inside the health-arc ring (0.9 * radius) so the arcs are not covered.
            if (_xpFillFraction[p.Id] != fraction)
            {
                BuildXpFillMesh(_xpFillMeshes[p.Id], p.Radius * 0.8f, fraction);
                _xpFillFraction[p.Id] = fraction;
            }

            fill.enabled = true;
        }

        // Builds the "liquid in a circle" fill: the part of a circle (radius r) below a water line set by
        // fraction, in local space (centred on origin). The segment is convex, so a triangle fan works;
        // Sprites/Default renders both faces, so winding does not matter.
        private static void BuildXpFillMesh(Mesh mesh, float r, float fraction)
        {
            _fillVerts.Clear();
            _fillTris.Clear();
            _fillColors.Clear();

            fraction = Mathf.Clamp01(fraction);
            float waterY = -r + 2f * r * fraction;
            float half = Mathf.Sqrt(Mathf.Max(0f, r * r - waterY * waterY));

            float start = Mathf.Atan2(waterY, half);   // right intersection
            float end = Mathf.Atan2(waterY, -half);    // left intersection
            if (end > start)
            {
                end -= 2f * Mathf.PI; // sweep clockwise through the bottom so end sits below start
            }

            int segments = Mathf.Clamp(Mathf.CeilToInt((start - end) / (12f * Mathf.Deg2Rad)), 1, 48);

            Color color = new Color(0.45f, 1f, 0.5f, 0.85f);
            for (int i = 0; i <= segments; i++)
            {
                float a = Mathf.Lerp(start, end, (float)i / segments);
                _fillVerts.Add(new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
                _fillColors.Add(color);
            }

            for (int i = 1; i < _fillVerts.Count - 1; i++)
            {
                _fillTris.Add(0);
                _fillTris.Add(i);
                _fillTris.Add(i + 1);
            }

            mesh.Clear();
            mesh.SetVertices(_fillVerts);
            mesh.SetColors(_fillColors);
            mesh.SetTriangles(_fillTris, 0);
        }

        // Draws the puck's health as arc segments around its rim: the rim is split into (max health) slices
        // and the current health's worth are shown, so losing a point removes one arc (design doc 6.3).
        private void DrawHealthArcs(Puck p)
        {
            LineRenderer[] arcs = _healthArcs[p.Id];
            int max = Mathf.Clamp(_health, 1, MaxHealthArcs);
            int current = Mathf.Clamp(p.Health, 0, max);

            float radius = p.Radius * 0.9f;
            float stepDeg = 360f / max;
            float gapDeg = Mathf.Clamp(stepDeg * 0.2f, 4f, 14f);

            for (int i = 0; i < arcs.Length; i++)
            {
                if (i >= current)
                {
                    arcs[i].enabled = false;
                    continue;
                }

                // Slot i runs clockwise from the top, inset by half the gap on each end.
                float a0 = 90f - i * stepDeg - gapDeg * 0.5f;
                float a1 = 90f - (i + 1) * stepDeg + gapDeg * 0.5f;
                float spanDeg = Mathf.Abs(a0 - a1);
                int points = Mathf.Clamp(Mathf.CeilToInt(spanDeg / 12f) + 1, 2, 24);

                arcs[i].positionCount = points;
                for (int k = 0; k < points; k++)
                {
                    float t = (float)k / (points - 1);
                    float angleRad = Mathf.Lerp(a0, a1, t) * Mathf.Deg2Rad;
                    float x = p.Position.x + Mathf.Cos(angleRad) * radius;
                    float y = p.Position.y + Mathf.Sin(angleRad) * radius;
                    arcs[i].SetPosition(k, new Vector3(x, y, 0f));
                }

                arcs[i].enabled = true;
            }
        }

        // Refills every puck to the current max health when the health slider changes (positions kept).
        private void ApplyHealthChangeIfNeeded()
        {
            if (_health == _appliedHealth)
            {
                return;
            }

            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                _sim.SetHealth(pucks[i].Id, _health);
            }

            _appliedHealth = _health;
        }

        // Maps drag distance to a [0,1] power fraction with an adjustable curve (1 = linear, >1 = more
        // precision at the low end, <1 = reaches high power sooner).
        private float DragToPowerFraction(float dragDistance)
        {
            float dragForMax = _maxPower / Mathf.Max(0.0001f, _powerScale);
            float t = Mathf.Clamp01(dragDistance / Mathf.Max(0.0001f, dragForMax));
            return Mathf.Pow(t, Mathf.Max(0.0001f, _powerCurve));
        }

        // Rolls out a clone of the sim (never the real one — design doc 7.9) to draw the cue puck's path
        // to its final resting spot. Any stone the cue strikes is removed from the clone so a struck stone
        // can never bounce back into the cue's path: the preview is a one-time-bumper prediction, not a
        // full cascade, so it can diverge from reality exactly in that case (by design, 6.2). Timed for the HUD.
        private void ComputePreview(int cueId, Vector2 launchVelocity)
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            PuckSim clone = _sim.Clone();
            if (IsAimingGhost())
            {
                clone.AddPuck(_ghost); // the waiting stone is not in the real sim yet, so preview it here
            }

            clone.SetVelocity(cueId, launchVelocity);
            clone.SetHealth(cueId, 1_000_000); // never destroy the cue in the preview (design doc 6.2)

            _previewPoints.Clear();
            if (!clone.TryGetPuck(cueId, out Puck cue))
            {
                HidePreview();
                return;
            }

            _previewPoints.Add(new Vector3(cue.Position.x, cue.Position.y, 0f));

            for (int step = 0; step < PreviewMaxSteps; step++)
            {
                IReadOnlyList<PuckSimEvent> events = clone.Step();

                // Drop any stone the cue struck this step so it cannot cascade back into the cue's path.
                for (int i = 0; i < events.Count; i++)
                {
                    PuckSimEvent e = events[i];
                    if (e.Type != PuckSimEventType.PuckCollision)
                    {
                        continue;
                    }

                    if (e.PuckA == cueId)
                    {
                        clone.RemovePuck(e.PuckB);
                    }
                    else if (e.PuckB == cueId)
                    {
                        clone.RemovePuck(e.PuckA);
                    }
                }

                if (!clone.TryGetPuck(cueId, out cue))
                {
                    break;
                }

                _previewPoints.Add(new Vector3(cue.Position.x, cue.Position.y, 0f));

                if (clone.AllAtRest())
                {
                    break;
                }
            }

            stopwatch.Stop();
            _previewMs = (float)stopwatch.Elapsed.TotalMilliseconds;

            _previewLine.positionCount = _previewPoints.Count;
            for (int i = 0; i < _previewPoints.Count; i++)
            {
                _previewLine.SetPosition(i, _previewPoints[i]);
            }
            _previewLine.enabled = _previewPoints.Count >= 2;

            Vector3 landing = _previewPoints[_previewPoints.Count - 1];
            float diameter = _puckRadius * 2f;
            _previewMarker.transform.localPosition = new Vector3(landing.x, landing.y, 0f);
            _previewMarker.transform.localScale = new Vector3(diameter, diameter, 1f);
            _previewMarker.enabled = true;
        }

        private void HidePreview()
        {
            if (_previewLine != null)
            {
                _previewLine.enabled = false;
            }

            if (_previewMarker != null)
            {
                _previewMarker.enabled = false;
            }
        }

        // --- HUD ----------------------------------------------------------------------------------

        private void OnGUI()
        {
            if (_sim == null)
            {
                return;
            }

            GUILayout.BeginArea(new Rect(HudRect.x, HudRect.y, HudRect.width, HudRect.height), GUI.skin.box);

            GUILayout.Label("Puckmite Playtest");
            GUILayout.Label(_gameOver ? $"** {_gameOverText} Press Reset. **" : $"Turn: {ActorName(_currentActor)}    {TurnPrompt()}");
            GUILayout.Label(_attackLog.Length > 0 ? _attackLog : "No attack yet.");
            GUILayout.Label($"Pucks: {_sim.Pucks.Count}    At rest: {_sim.AllAtRest()}");
            GUILayout.Label($"Wall bounces: {_wallBounceTotal}    Collisions: {_collisionTotal}");
            GUILayout.Label($"Destroyed: {_destroyedTotal}");
            GUILayout.Label(_aiming ? $"Power: {_currentPowerFraction * 100f:F0}%" : "Power: -");
            GUILayout.Label($"Preview: {_previewMs:F3} ms");

            GUILayout.Space(6f);
            GUILayout.Label($"Friction: {_friction:F1}");
            _friction = GUILayout.HorizontalSlider(_friction, 0f, 40f);
            GUILayout.Label($"Restitution: {_restitution:F2}");
            _restitution = GUILayout.HorizontalSlider(_restitution, 0f, 1f);
            GUILayout.Label($"Impact damping: {_collisionSpeedKept:F2}");
            _collisionSpeedKept = GUILayout.HorizontalSlider(_collisionSpeedKept, 0.3f, 1f);
            GUILayout.Label($"Rest threshold: {_restThreshold:F2}");
            _restThreshold = GUILayout.HorizontalSlider(_restThreshold, 0f, 1f);
            GUILayout.Label($"Wall damping: {_wallRestitution:F2}");
            _wallRestitution = GUILayout.HorizontalSlider(_wallRestitution, 0.3f, 1f);
            GUILayout.Label($"Power scale: {_powerScale:F1}");
            _powerScale = GUILayout.HorizontalSlider(_powerScale, 0.5f, 10f);
            GUILayout.Label($"Max power: {_maxPower:F0}");
            _maxPower = GUILayout.HorizontalSlider(_maxPower, 5f, 120f);
            GUILayout.Label($"Power curve: {_powerCurve:F2}");
            _powerCurve = GUILayout.HorizontalSlider(_powerCurve, 0.3f, 3f);
            GUILayout.Label($"Max health: {_health}");
            _health = Mathf.RoundToInt(GUILayout.HorizontalSlider(_health, 1f, 8f));
            GUILayout.Label($"Occupancy threshold: {_occupancyThreshold:F2}");
            _occupancyThreshold = GUILayout.HorizontalSlider(_occupancyThreshold, 0.1f, 2f);
            GUILayout.Label($"Cell damage: {_cellDamage}");
            _cellDamage = Mathf.RoundToInt(GUILayout.HorizontalSlider(_cellDamage, 1f, 5f));

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Speed x{_speedMultiplier:F0}", GUILayout.Width(70f));
            if (GUILayout.Button("x1"))
            {
                _speedMultiplier = 1f;
            }
            if (GUILayout.Button("x2"))
            {
                _speedMultiplier = 2f;
            }
            if (GUILayout.Button("x4"))
            {
                _speedMultiplier = 4f;
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Reset pucks"))
            {
                ResetPucks();
            }

            GUILayout.Label("Click a puck, drag and release to fling it.");
            GUILayout.EndArea();
        }

        // What the current actor is waiting on, for the HUD turn line.
        private string TurnPrompt()
        {
            if (_noStoneTurn)
            {
                return "(no stones to roll)";
            }

            if (_awaitingAttack)
            {
                return "(click an enemy to attack)";
            }

            if (_hasRolledThisTurn)
            {
                return "(rolling…)";
            }

            return _ghostActive ? "roll a stone, or the new one on your edge" : "roll a highlighted stone";
        }
    }
}
