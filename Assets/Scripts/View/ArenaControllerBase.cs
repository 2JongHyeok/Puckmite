using System.Collections.Generic;
using Puckmite.Sim;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Puckmite.View
{
    /// <summary>
    /// What the battle and shop scenes share: the fixed-timestep sim driver, the procedurally built camera
    /// and puck views, the hand of stones waiting to enter, the entry-edge ghost, pull-back aiming with the
    /// trajectory preview, and the highlight rings. Each scene's controller derives from this and adds its
    /// own board, rules and GUI. Rendering only reads the sim; the one write is the launch velocity.
    ///
    /// The view arrays here are indexed by Puck.Id (contiguous from 0), which is why a destroyed stone's Id
    /// must be reused when it returns to a hand — a brand new Id would run off the end of every array.
    /// </summary>
    public abstract class ArenaControllerBase : MonoBehaviour
    {
        // Every tunable number lives in one shared asset (wired by Tools/PuckHero/Setup Game Scenes) so the
        // battle and shop scenes can never drift apart. Serialized, so it survives a domain reload and is
        // readable in a subclass's Awake before base.Awake runs.
        [SerializeField] protected GameTuning _tuning;

        // Board: 5 cells of side 5 => 25 units across, centred on the origin.
        protected const float BoardHalf = 12.5f;
        protected const float InnerHalf = 7.5f; // inner 3x3 buff zone spans cells 1..3
        // How far a stone must cross a cell boundary to occupy it (design doc 3.2, temp 0.1). A constant, not
        // a tunable: anything at or above the puck radius makes "radius - distance >= threshold" unsatisfiable,
        // which silently switches off cell occupancy entirely — no highlights, no buffs, no damage-cell settling.
        protected const float OccupancyThreshold = 0.1f;

        protected const float MinDrag = 0.3f;        // ignore tiny drags so a click does not fling
        private const float MaxAccumulated = 0.1f;   // clamp so a hitch cannot trigger a step burst
        private const int MaxStepsPerFrame = 4000;

        // The shared feet line the backdrop's grass row is drawn on: battle characters and the shop
        // merchant all stand here, slightly above the board's top wall.
        protected const float CharFeetY = BoardHalf + 0.8f;

        // The camera frames content up to here (the battle's character row: bodies on the feet line plus
        // the stat columns over their heads). The shop keeps the same framing even without a row, so the
        // board sits identically in both scenes.
        private const float CameraContentTop = 25f;

        // Trajectory preview: safety cap on how many steps to roll the cue forward when tracing its path,
        // and how many impacts along it get a ghost-and-arrow readout (extras are simply not shown).
        private const int PreviewMaxSteps = 2000;
        private const int PreviewMaxHits = 4;

        // Health display: max arc segments drawn around a puck (matches the health slider's max).
        private const int MaxHealthArcs = 8;

        // Highlight rings. Yellow is the default "this is yours to act on"; the faint version marks a
        // target that is merely available. Red marks a stone drawn back far enough to launch.
        protected static readonly Color RingStrong = new Color(1f, 0.9f, 0.25f, 0.9f);
        protected static readonly Color RingFaint = new Color(1f, 0.9f, 0.25f, 0.25f);
        protected static readonly Color RingLaunchReady = new Color(1f, 0.35f, 0.25f, 0.95f);
        protected static readonly Color RingBlocked = new Color(0.55f, 0.55f, 0.60f, 0.55f); // entry spot occupied

        // Highlight rings reach this far out from a stone's centre, in radii. The entry spot is inset by it
        // so a waiting stone's ring clears the wall instead of being sliced by it.
        protected const float RingRadiusScale = 1.25f;

        protected PuckSim _sim;
        protected Camera _camera;
        private SpriteRenderer[] _puckViews; // indexed by puck Id (layout uses contiguous Ids from 0)
        private LineRenderer[][] _healthArcs; // [puckId][arc]: health shown as arc segments around the rim
        private Material _arcMaterial;        // shared by every health arc
        private TextMeshPro[] _levelTexts;      // [puckId]: level number (world-space TMP; a placeholder to restyle later)
        private MeshRenderer[] _levelRenderers; // [puckId]: the level text's renderer, toggled for show/hide
        private MeshRenderer[] _xpFillRenderers; // [puckId]: XP fill renderer (circle-segment mesh)
        private Mesh[] _xpFillMeshes;            // [puckId]: the fill mesh, regenerated when fraction changes
        protected float[] _xpFillFraction;       // [puckId]: last drawn fraction (-1 = none yet)

        // Reused buffers for building the XP fill mesh (one puck at a time, no per-frame allocation).
        private static readonly List<Vector3> _fillVerts = new List<Vector3>();
        private static readonly List<int> _fillTris = new List<int>();
        private static readonly List<Color> _fillColors = new List<Color>();
        private LineRenderer _previewLine;
        private SpriteRenderer _previewMarker; // dashed ring at the cue's predicted final position
        private SpriteRenderer[] _previewHitGhosts; // dashed ring where the cue strikes a stone (per hit)
        private SpriteRenderer[] _previewHitArrows; // the struck stone's flight direction (per hit)
        private readonly List<Vector3> _previewPoints = new List<Vector3>();
        private float _previewMs; // last preview compute time, shown in the debug panel

        // Optional preview art, wired by Setup Game Scenes when the files exist (promised paths
        // Assets/Art/Sprites/UI/PreviewDash and /PreviewHitGhost). Procedural stand-ins until then.
        [SerializeField] private Sprite _previewDashSprite;
        [SerializeField] private Sprite _previewHitGhostSprite;

        // Each scene's full-view backdrop, wired by Setup Game Scenes (battle_background /
        // shop_background — procedurally generated to the camera's 16:9 view, 720x405 at 10ppu =
        // 72x40.5 world, grass row on the feet line, the board rect left featureless). Nothing draws
        // until it is wired; wider aspects see the camera colour past its edges.
        [SerializeField] private Sprite _backgroundSprite;

        // Optional board-cell art from Assets/Art/Sprites/UI/cell_sheet.aseprite, wired by Setup Game
        // Scenes one frame per face (frame order is the mapping — see WireCellSprites). Scenes fall back
        // to their flat placeholder cells while frames are missing. All frames share one rect and pivot,
        // so a renderer fitted once can swap between them freely.
        [SerializeField] protected Sprite _cellAttackSprite;       // Frame_0: sword
        [SerializeField] protected Sprite _cellAttackStrongSprite; // Frame_1: sword + sparkles (stronger)
        [SerializeField] protected Sprite _cellShieldSprite;       // Frame_2: shield
        [SerializeField] protected Sprite _cellShieldStrongSprite; // Frame_3: shield + sparkles (stronger)
        [SerializeField] protected Sprite _cellMaxHealthSprite;    // Frame_4: MAX heart (shop upgrade)
        [SerializeField] protected Sprite _cellRunHealSprite;      // Frame_5: green heal heart (shop upgrade)
        [SerializeField] protected Sprite _cellDamageSprite;       // Frame_6: hazard stripes
        [SerializeField] protected Sprite _cellEmptySprite;        // Frame_7: plain

        private void BuildBackground()
        {
            if (_backgroundSprite == null)
            {
                return;
            }

            GameObject go = new GameObject("Background");
            go.transform.SetParent(transform, false);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _backgroundSprite;
            sr.sortingOrder = -20;

            // Centre on the camera view (content spans -12.5..25 → centre y 6.25), by bounds as usual.
            go.transform.position += new Vector3(0f, 6.25f, 0f) - sr.bounds.center;
        }

        // A labelled info box for the corner UI (dark placeholder rect + Korean-capable TMP), shared by
        // the battle's stage/turn boxes and the shop's label.
        protected TextMeshPro MakeInfoBox(string name, Vector2 center, Vector2 size)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = center;

            GameObject bgGo = new GameObject("Bg");
            bgGo.transform.SetParent(go.transform, false);
            bgGo.transform.localScale = new Vector3(size.x, size.y, 1f);
            SpriteRenderer bg = bgGo.AddComponent<SpriteRenderer>();
            bg.sprite = ProceduralSprites.Unit();
            bg.color = new Color(0.14f, 0.17f, 0.24f, 0.9f);
            bg.sortingOrder = 15;

            GameObject textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            TextMeshPro tmp = textGo.AddComponent<TextMeshPro>();
            if (KoreanFont.Asset() != null)
            {
                tmp.font = KoreanFont.Asset();
            }

            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 1f;
            tmp.fontSizeMax = 8f;
            tmp.rectTransform.sizeDelta = new Vector2(size.x - 0.8f, size.y - 0.6f);
            tmp.color = Color.white;
            tmp.GetComponent<MeshRenderer>().sortingOrder = 16;
            return tmp;
        }

        // --- Silhouette outline highlight, shared by the battle's characters and the shop's merchant ---

        // Created and wired by Setup Game Scenes; as a material asset it also survives shader stripping
        // in builds, which a runtime Shader.Find would not.
        [SerializeField] protected Material _silhouetteMaterial;

        // The outline is built from eight silhouette copies of the body, each pushed out one thickness
        // step. Overlapping ghosts stack alpha, so a translucent state needs a per-ghost sliver of alpha
        // (OutlineFaint) rather than RingFaint's 0.25 — stacked, it reads at about the same strength.
        protected static readonly Vector2[] OutlineDirections =
        {
            new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f),
            new Vector2(0.7071f, 0.7071f), new Vector2(-0.7071f, 0.7071f),
            new Vector2(0.7071f, -0.7071f), new Vector2(-0.7071f, -0.7071f),
        };
        protected const float OutlineThickness = 0.12f;      // world units the silhouettes are pushed out
        protected const float HoverOutlineThickness = 0.2f;  // the hover state needs more presence: a
                                                             // hairline vanished against the body art
        protected static readonly Color OutlineFaint = new Color(1f, 0.9f, 0.25f, 0.07f);

        // Eight silhouette ghosts parented under the body, so they inherit its transform (character art
        // is scaled and re-centred) and, on show, its current sprite — the outline follows whatever the
        // Animator is playing with no per-frame outline art.
        protected SpriteRenderer[] BuildCharacterOutline(SpriteRenderer body)
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

        // Shows or hides one body's outline, keeping the ghost sprites in step with the animating body.
        // Thickness is per-state, so the offsets are recomputed against the body's scale on show.
        protected static void SetSpriteOutline(SpriteRenderer[] ghosts, SpriteRenderer body, Color color, bool on, float thickness = OutlineThickness)
        {
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

        private float _accumulator;
        protected bool _aiming;
        protected int _aimingPuckId = -1;
        protected bool _launchReady; // the current drag is long enough to actually fling the aimed stone
        protected float _currentPowerFraction;

        // Turn structure (view-only orchestration; the sim stays pure physics/combat). The shop is a single
        // actor (the player) with no turns, but shares the hand and ghost machinery through the same fields.
        protected int[] _actorOf;              // puck id -> actor (0 = player, 1.. = each enemy)
        protected int _actorCount;
        protected int _currentActor;
        protected bool _hasRolledThisTurn;

        // New stone entry (design doc 3.3/3.4). The hand holds the ids of destroyed stones waiting to come
        // back; an id reused this way keeps every id-indexed view array and _actorOf valid. Pending becomes
        // ready at the owner's next turn start, so a stone lost this turn cannot be played this turn.
        protected List<int>[] _handReady;
        protected List<int>[] _handPending;

        // The stone waiting on the entry edge. It lives here rather than in the sim until it is launched.
        protected bool _ghostActive;
        protected Puck _ghost;
        protected bool _ghostBlocked; // its spot overlaps a stone already on the board, so it cannot launch
        protected bool _ghostShown;   // the cursor is somewhere the ghost should be drawn and track
        private SpriteRenderer _ghostView;
        private SpriteRenderer _ghostRing;

        protected readonly List<Vector2> _entryScanScratch = new List<Vector2>(); // reused by the edge scan

        private int _wallBounceTotal; // session tallies, straight from Step()'s events
        private int _collisionTotal;
        private int _destroyedTotal;

        // Physics values currently baked into _sim; a mismatch with the tuning asset triggers a rebuild.
        private float _appliedFriction;
        private float _appliedRestitution;
        private float _appliedRestThreshold;
        private float _appliedWallRestitution;
        private float _appliedCollisionSpeedKept;
        private int _appliedHealth;

        // Diagnostics read by the debug panel.
        public int SimPuckCount => _sim != null ? _sim.Pucks.Count : 0;
        public bool SimAllAtRest => _sim == null || _sim.AllAtRest();
        public int WallBounceTotal => _wallBounceTotal;
        public int CollisionTotal => _collisionTotal;
        public int DestroyedTotal => _destroyedTotal;
        public float PreviewMs => _previewMs;

        protected virtual void Awake()
        {
            if (_tuning == null)
            {
                Debug.LogError("[PuckHero] GameTuning asset is not assigned — run Tools/PuckHero/Setup Game Scenes.");
                enabled = false;
                return;
            }

            Build();
        }

        protected virtual void Update()
        {
            // A script recompile during Play triggers a domain reload that clears runtime fields (_sim,
            // views) without calling Awake again. Rebuild when that happens so the scene self-heals
            // instead of throwing a NullReferenceException every frame.
            if (_sim == null)
            {
                Build();
            }

            RebuildIfPhysicsChanged();
            ApplyHealthChangeIfNeeded();
        }

        // Builds (or rebuilds) everything this component owns. Destroys any children a previous build left
        // so rebuilding after a domain reload does not stack duplicate boards and pucks.
        protected void Build()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Destroy(transform.GetChild(i).gameObject);
            }

            BuildSimFrom(new List<Puck>()); // the board starts empty; every stone enters from a hand
            AssignActors();
            BuildCamera();
            BuildBackground();
            BuildMode();
        }

        /// <summary>Everything scene-specific that Build() must construct, in the scene's own order.</summary>
        protected abstract void BuildMode();

        /// <summary>Every stone this scene can ever field, by Id and team. Ids are contiguous from 0
        /// because the view arrays are indexed by Id. The roster carries no board position — the board
        /// starts empty and stones enter from a hand.</summary>
        protected abstract List<Puck> InitialRoster();

        protected static int RosterMaxId(List<Puck> roster)
        {
            int maxId = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i].Id > maxId)
                {
                    maxId = roster[i].Id;
                }
            }

            return maxId;
        }

        private void BuildSimFrom(List<Puck> pucks)
        {
            _sim = new PuckSim(
                new Vector2(-BoardHalf, -BoardHalf),
                new Vector2(BoardHalf, BoardHalf),
                new PuckSimConfig(_tuning.Friction, _tuning.Restitution, _tuning.RestThreshold,
                    _tuning.WallRestitution, _tuning.CollisionSpeedKept));

            for (int i = 0; i < pucks.Count; i++)
            {
                _sim.AddPuck(pucks[i]);
            }

            _appliedFriction = _tuning.Friction;
            _appliedRestitution = _tuning.Restitution;
            _appliedRestThreshold = _tuning.RestThreshold;
            _appliedWallRestitution = _tuning.WallRestitution;
            _appliedCollisionSpeedKept = _tuning.CollisionSpeedKept;
            _appliedHealth = _tuning.StoneHealth;
        }

        private void RebuildIfPhysicsChanged()
        {
            bool changed =
                _tuning.Friction != _appliedFriction ||
                _tuning.Restitution != _appliedRestitution ||
                _tuning.RestThreshold != _appliedRestThreshold ||
                _tuning.WallRestitution != _appliedWallRestitution ||
                _tuning.CollisionSpeedKept != _appliedCollisionSpeedKept;
            if (!changed)
            {
                return;
            }

            // Rebuild in place, preserving every puck exactly as it is now (position, velocity, bounces),
            // so a slider tweak is felt immediately even mid-roll. The boss's open hole is sim state too —
            // BuildSimFrom starts fresh, so carry it across or a slider nudge would quietly cancel the ability.
            int hole = _sim.HoleCell;
            BuildSimFrom(SnapshotPucks());
            if (hole >= 0)
            {
                _sim.SetHole(hole % BoardCells.Size, hole / BoardCells.Size);
            }
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

        // Refills every puck to its own maximum when the health slider changes (positions kept). Per
        // stone, not the flat slider value: enemy kinds carry different maxima (강석 +2, 반석 2), and a
        // flat refill would hand the anchor invisible health beyond its two drawn arcs.
        private void ApplyHealthChangeIfNeeded()
        {
            if (_tuning.StoneHealth == _appliedHealth)
            {
                return;
            }

            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                _sim.SetHealth(pucks[i].Id, MaxStoneHealth(pucks[i]));
            }

            _appliedHealth = _tuning.StoneHealth;
        }

        protected void DriveSimulation()
        {
            if (_sim.AllAtRest())
            {
                _accumulator = 0f; // start each fling from a clean clock
                return;
            }

            _accumulator += Time.deltaTime * Mathf.Max(0f, DebugPanel.SpeedMultiplier);
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

        protected void ResetAccumulator()
        {
            _accumulator = 0f;
        }

        // Assigns each puck to a turn actor from the fixed roster: the player is actor 0 (owns all its
        // stones); each enemy is its own actor (1, 2, 3, ...) — one stone per enemy here, which is why the
        // battle scene (where an enemy type can field two stones, or none) overrides this with its own
        // roster-order mapping. The actor COUNT is declared by the scene, not derived from the stones — a
        // stoneless actor (the stage-1 boss) would otherwise not exist at all and be won-over on arrival.
        protected virtual void AssignActors()
        {
            List<Puck> roster = InitialRoster();
            _actorOf = new int[RosterMaxId(roster) + 1];
            int nextEnemy = 1;
            for (int i = 0; i < roster.Count; i++)
            {
                Puck p = roster[i];
                _actorOf[p.Id] = p.Owner == PuckOwner.Player ? 0 : nextEnemy++;
            }

            _actorCount = DeclaredActorCount();
            _currentActor = 0;
            _hasRolledThisTurn = false;
        }

        /// <summary>How many turn actors this scene fields (player included), stones or not. Stone-to-actor
        /// mapping still assumes one stone per enemy — revisit both together if that ever changes.</summary>
        protected abstract int DeclaredActorCount();

        // --- Hand and new stone entry (design doc 3.3/3.4) ------------------------------------------

        // A destroyed stone becomes a fresh stone in its owner's hand. Its Id is reused, which is what keeps
        // every Id-indexed view array and _actorOf valid — a brand new Id would run off the end of them.
        protected virtual void ReturnStoneToHand(int puckId)
        {
            if (_handPending == null || puckId < 0 || puckId >= _actorOf.Length)
            {
                return;
            }

            int actor = _actorOf[puckId];
            if (!_handPending[actor].Contains(puckId) && !_handReady[actor].Contains(puckId))
            {
                _handPending[actor].Add(puckId);
            }
        }

        // Turn start: stones that came back earlier become playable now — "다음 턴부터 사용 가능" (design doc
        // 3.3). Anything lost later this turn lands in pending and so waits for the turn after.
        protected void PromoteHand(int actor)
        {
            for (int i = 0; i < _handPending[actor].Count; i++)
            {
                _handReady[actor].Add(_handPending[actor][i]);
            }

            _handPending[actor].Clear();
        }

        // Puts the next playable stone on the actor's entry edge, ready to be aimed. No ready stone, no ghost.
        protected void SetupGhost(int actor)
        {
            if (_handReady[actor].Count == 0)
            {
                ClearGhost();
                return;
            }

            _ghost = CreateHandStone(actor, _handReady[actor][0]);
            _ghost.Position = EntryPoint(actor, 0f);
            _ghostActive = true;
            _ghostBlocked = false;
            _ghostShown = false; // stays hidden until the cursor comes onto the board
        }

        /// <summary>A fresh stone for this actor's hand — full health, and whatever health/trait the
        /// actor's kind carries (enemy types, design doc 4.3). Everything that turns a hand id into a
        /// live stone (ghost, launch, AI template) must go through here or the kinds drift apart.</summary>
        protected virtual Puck CreateHandStone(int actor, int id)
        {
            PuckOwner owner = actor == 0 ? PuckOwner.Player : PuckOwner.Enemy;
            return new Puck(id, Vector2.zero, _tuning.PuckRadius, 1f, owner) { Health = _tuning.StoneHealth };
        }

        protected void ClearGhost()
        {
            _ghostActive = false;
            _ghostBlocked = false;
            _ghostShown = false;
        }

        /// <summary>Where a new stone waits on this actor's entry edge, with the free axis clamped to the
        /// board. Battle enters on the left/right walls (by actor), the shop off the bottom wall.</summary>
        protected abstract Vector2 EntryPoint(int actor, float along);

        /// <summary>The cursor coordinate that slides the waiting stone along its entry edge.</summary>
        protected abstract float EntryAlong(Vector2 world);

        /// <summary>The board-space range the entry edge spans (before the ring inset is applied).</summary>
        protected abstract void EntryAxisBounds(out float min, out float max);

        // Slides the waiting stone along its edge to track the cursor, and checks whether that spot is
        // free. It shows only while the cursor is over the board and not over one of this actor's own
        // stones — there the stone itself is what a click is for, so the ghost would only be in the way.
        protected void UpdateGhostAim(Vector2 world)
        {
            if (!_ghostActive || _hasRolledThisTurn)
            {
                _ghostShown = false;
                return;
            }

            if (IsAimingGhost())
            {
                // Held in place for the pull-back, which drags the cursor back off the board.
                _ghostBlocked = EntrySpotBlocked(_ghost.Position);
                return;
            }

            _ghostShown = !_aiming && CursorInsideBoard(world) && NearestPuckId(world) < 0;
            if (!_ghostShown)
            {
                return;
            }

            _ghost.Position = EntryPoint(_currentActor, EntryAlong(world));
            _ghostBlocked = EntrySpotBlocked(_ghost.Position);
        }

        protected bool CursorInsideBoard(Vector2 world)
        {
            return world.x >= _sim.BoardMin.x && world.x <= _sim.BoardMax.x
                && world.y >= _sim.BoardMin.y && world.y <= _sim.BoardMax.y;
        }

        // Drawn and grabbable only while it is tracking the cursor, or while it is the stone being aimed.
        protected bool GhostVisible()
        {
            return _ghostActive && !_hasRolledThisTurn && (_ghostShown || IsAimingGhost());
        }

        // Launching from inside another stone would shove it aside for free, so such a spot is refused.
        // A spot inside the boss's hole is refused too: the stone would be swallowed on its first step,
        // which the preview cannot draw — better the grey "spot blocked" ring than a silent wasted roll.
        protected bool EntrySpotBlocked(Vector2 position)
        {
            if (_sim.IsInsideHole(position))
            {
                return true;
            }

            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                if ((pucks[i].Position - position).magnitude < pucks[i].Radius + _tuning.PuckRadius)
                {
                    return true;
                }
            }

            return false;
        }

        // Every clear spot along this actor's edge, scanned at a fixed fine step. This one scan backs both
        // the turn-start "can a stone come in at all?" gate and the AI's entry candidates — if they sampled
        // differently, the gate could promise an entry the AI then fails to find, forfeiting a legal roll.
        protected void CollectFreeEntrySpots(int actor, List<Vector2> outSpots)
        {
            outSpots.Clear();
            float inset = _tuning.PuckRadius * RingRadiusScale;

            EntryAxisBounds(out float axisMin, out float axisMax);
            float min = axisMin + inset;
            float max = axisMax - inset;
            for (float along = min; along <= max; along += _tuning.PuckRadius * 0.5f)
            {
                Vector2 spot = EntryPoint(actor, along);
                if (!EntrySpotBlocked(spot))
                {
                    outSpots.Add(spot);
                }
            }
        }

        // Whether anywhere along this actor's edge is clear enough to bring a stone in.
        protected bool HasFreeEntrySpot(int actor)
        {
            CollectFreeEntrySpots(actor, _entryScanScratch);
            return _entryScanScratch.Count > 0;
        }

        // The new stone enters the board where the ghost stands and rolls in the same motion (design doc 3.5).
        protected void LaunchGhost(Vector2 velocity)
        {
            _handReady[_currentActor].Remove(_ghost.Id);

            // Rebuilt through the factory rather than copied off the ghost, so the entering stone carries
            // its kind's health/trait even if the ghost struct went stale across a tuning change.
            Puck stone = CreateHandStone(_currentActor, _ghost.Id);
            stone.Position = _ghost.Position;
            stone.Velocity = velocity;
            _sim.AddPuck(stone);

            _xpFillFraction[stone.Id] = -1f; // drop the fill cached for whatever last used this Id
            ClearGhost();
        }

        protected void UpdateGhost()
        {
            if (_ghostView == null)
            {
                return;
            }

            if (!GhostVisible())
            {
                _ghostView.enabled = false;
                _ghostRing.enabled = false;
                return;
            }

            // Keep the blocked flag honest even on turns where input is locked out (the AI's), where the
            // aim path that normally refreshes it never runs.
            _ghostBlocked = EntrySpotBlocked(_ghost.Position);

            float diameter = _ghost.Radius * 2f;
            Vector3 position = new Vector3(_ghost.Position.x, _ghost.Position.y, 0f);
            _ghostView.transform.localPosition = position;
            _ghostView.transform.localScale = new Vector3(diameter, diameter, 1f);

            Color body = StoneColor(_ghost);
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

        // --- Aiming -------------------------------------------------------------------------------

        protected bool IsAimingGhost()
        {
            return _ghostActive && _aiming && _aimingPuckId == _ghost.Id;
        }

        // Where the stone being aimed sits — the waiting new stone lives outside the sim, so it is read
        // from the ghost rather than looked up by Id.
        protected bool TryGetAimedPosition(out Vector2 position)
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

        // A stone is grabbed by clicking inside its highlight ring and no further, so what can be picked is
        // exactly what is drawn.
        protected float GrabRadius()
        {
            return _tuning.PuckRadius * RingRadiusScale;
        }

        // Aiming (EXPERIMENT, 2026-08-06): the stone flies where the cursor is dragged — the launch vector
        // points from the stone to the cursor, its length the drag distance. The original pull-back scheme
        // was the reverse (`puckPosition - cursor`); flip this one line to go back. If this sticks, rename
        // the method (the "pull-back" name is now a lie) and update the doc/memory rule.
        protected static Vector2 PullbackDrag(Vector2 puckPosition, Vector2 cursor)
        {
            return cursor - puckPosition;
        }

        // Closest puck to the point, if within a forgiving grab radius; -1 if the click is in open space.
        protected int NearestPuckId(Vector2 world)
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

        protected Vector2 ScreenToWorld(Vector2 screen)
        {
            Vector3 world = _camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -_camera.transform.position.z));
            return new Vector2(world.x, world.y);
        }

        // Maps drag distance to a [0,1] power fraction with an adjustable curve (1 = linear, >1 = more
        // precision at the low end, <1 = reaches high power sooner).
        protected float DragToPowerFraction(float dragDistance)
        {
            float dragForMax = _tuning.MaxPower / Mathf.Max(0.0001f, _tuning.PowerScale);
            float t = Mathf.Clamp01(dragDistance / Mathf.Max(0.0001f, dragForMax));
            return Mathf.Pow(t, Mathf.Max(0.0001f, _tuning.PowerCurve));
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
            const float contentTop = CameraContentTop;
            const float contentBottom = -BoardHalf;
            float centerY = (contentTop + contentBottom) * 0.5f;
            _camera.transform.position = new Vector3(0f, centerY, -10f);

            float margin = 1.08f;
            float aspect = Mathf.Max(0.0001f, _camera.aspect);
            float halfHeight = (contentTop - contentBottom) * 0.5f * margin;
            float halfWidth = BoardHalf * margin;
            _camera.orthographicSize = Mathf.Max(halfHeight, halfWidth / aspect);
        }

        /// <summary>Fits a cell renderer's sprite to the cell rectangle, centring by bounds so the
        /// sheet's bottom pivot needs no special casing. Safe to call again to swap the sprite.</summary>
        protected static void FitCellSprite(SpriteRenderer sr, Vector2 center, Vector2 size, Sprite sprite)
        {
            sr.sprite = sprite;
            Bounds b = sprite.bounds;
            Vector3 scale = new Vector3(size.x / b.size.x, size.y / b.size.y, 1f);
            sr.transform.localScale = scale;
            sr.transform.localPosition = new Vector3(center.x - b.center.x * scale.x, center.y - b.center.y * scale.y, 0f);
        }

        protected static SpriteRenderer MakeCellSprite(string name, Transform parent, Vector2 center, Vector2 size, Sprite sprite, int sortingOrder)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = sortingOrder;
            FitCellSprite(sr, center, size, sprite);
            return sr;
        }

        protected static SpriteRenderer MakeQuad(string name, Transform parent, Vector2 center, Vector2 size, Color color, int sortingOrder)
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

        protected static Color OwnerColor(PuckOwner owner)
        {
            return owner == PuckOwner.Player ? new Color(0.30f, 0.75f, 1f) : new Color(1f, 0.45f, 0.35f);
        }

        /// <summary>The body colour a stone is drawn with. The battle scene tints special enemy stones
        /// (sniper/bomb/anchor, design doc 4.3) so their behaviour is readable before they move.</summary>
        protected virtual Color StoneColor(Puck p)
        {
            return OwnerColor(p.Owner);
        }

        /// <summary>A stone's full health, for the health-arc count. The battle scene answers per enemy
        /// kind (강석형 +2, 반석형 2 — design doc 4.3).</summary>
        protected virtual int MaxStoneHealth(Puck p)
        {
            return _tuning.StoneHealth;
        }

        protected void BuildPuckViews()
        {
            _arcMaterial = new Material(Shader.Find("Sprites/Default"));

            // Sized from the roster, not the board: the board starts empty and stones arrive later, and these
            // arrays are addressed by Puck.Id, so the length has to cover every Id the match can ever use.
            List<Puck> roster = InitialRoster();
            int slots = RosterMaxId(roster) + 1;
            _puckViews = new SpriteRenderer[slots];
            _healthArcs = new LineRenderer[slots][];
            _levelTexts = new TextMeshPro[slots];
            _levelRenderers = new MeshRenderer[slots];
            _xpFillRenderers = new MeshRenderer[slots];
            _xpFillMeshes = new Mesh[slots];
            _xpFillFraction = new float[slots];
            for (int i = 0; i < roster.Count; i++)
            {
                Puck p = roster[i];
                GameObject go = new GameObject($"Puck{p.Id}");
                go.transform.SetParent(transform, false);

                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = ProceduralSprites.Circle();
                sr.color = StoneColor(p);
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

        // The waiting new stone and its ring. Built once like every other renderer here and toggled per frame.
        protected void BuildGhost()
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

        protected void BuildPreviewLine()
        {
            GameObject go = new GameObject("PreviewLine");
            go.transform.SetParent(transform, false);

            _previewLine = go.AddComponent<LineRenderer>();
            _previewLine.material = new Material(Shader.Find("Sprites/Default"));
            // Tiling a dash texture along the line draws it dashed; the user's art replaces the
            // procedural tile when wired.
            _previewLine.material.mainTexture = _previewDashSprite != null ? _previewDashSprite.texture : ProceduralSprites.DashTexture();
            _previewLine.useWorldSpace = true;
            _previewLine.widthMultiplier = 0.15f;
            _previewLine.numCapVertices = 2;
            _previewLine.numCornerVertices = 2;
            _previewLine.textureMode = LineTextureMode.Tile;
            _previewLine.startColor = new Color(0.55f, 0.95f, 1f, 0.9f);
            _previewLine.endColor = new Color(0.55f, 0.95f, 1f, 0.35f);
            _previewLine.sortingOrder = 12;
            _previewLine.positionCount = 0;
            _previewLine.enabled = false;
        }

        protected void BuildPreviewMarker()
        {
            GameObject go = new GameObject("PreviewMarker");
            go.transform.SetParent(transform, false);

            _previewMarker = go.AddComponent<SpriteRenderer>();
            _previewMarker.sprite = PreviewGhostSprite();
            // White, not preview-cyan: the landing ghost must read apart from the impact ghosts when
            // the path doubles back and they overlap (user feedback 2026-08-09).
            _previewMarker.color = new Color(1f, 1f, 1f, 0.9f);
            _previewMarker.sortingOrder = 9; // just under the pucks
            _previewMarker.enabled = false;

            // One ghost-and-arrow pair per possible impact readout along the path.
            _previewHitGhosts = new SpriteRenderer[PreviewMaxHits];
            _previewHitArrows = new SpriteRenderer[PreviewMaxHits];
            for (int i = 0; i < PreviewMaxHits; i++)
            {
                GameObject ghostGo = new GameObject($"PreviewHitGhost{i}");
                ghostGo.transform.SetParent(transform, false);
                SpriteRenderer ghost = ghostGo.AddComponent<SpriteRenderer>();
                ghost.sprite = PreviewGhostSprite();
                ghost.color = new Color(0.55f, 0.95f, 1f, 0.6f);
                ghost.sortingOrder = 9; // like the landing ghost, just under the pucks
                ghost.enabled = false;
                _previewHitGhosts[i] = ghost;

                GameObject arrowGo = new GameObject($"PreviewHitArrow{i}");
                arrowGo.transform.SetParent(transform, false);
                SpriteRenderer arrow = arrowGo.AddComponent<SpriteRenderer>();
                arrow.sprite = ProceduralSprites.Arrow();
                arrow.color = new Color(0.55f, 0.95f, 1f, 0.8f);
                arrow.transform.localScale = new Vector3(1.2f, 0.7f, 1f); // 1.2 long, 0.35 tall
                arrow.sortingOrder = 12; // with the preview line, above the stones so it reads
                arrow.enabled = false;
                _previewHitArrows[i] = arrow;
            }
        }

        private Sprite PreviewGhostSprite()
        {
            return _previewHitGhostSprite != null ? _previewHitGhostSprite : ProceduralSprites.DashedRing();
        }

        // Scales the renderer so its sprite stands worldSize tall — art of any pixel size fits the slot.
        private static void FitSpriteScale(SpriteRenderer sr, float worldSize)
        {
            sr.transform.localScale = Vector3.one * (worldSize / sr.sprite.bounds.size.y);
        }

        // --- Trajectory preview -------------------------------------------------------------------

        // Rolls out a clone of the sim (never the real one — design doc 7.9) to draw the cue puck's path
        // to its final resting spot. Any stone the cue strikes is removed from the clone so a struck stone
        // can never bounce back into the cue's path: the preview is a one-time-bumper prediction, not a
        // full cascade, so it can diverge from reality exactly in that case (by design, 6.2).
        protected void ComputePreview(int cueId, Vector2 launchVelocity)
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
            int hits = 0;

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

                    int struckId = e.PuckA == cueId ? e.PuckB : e.PuckB == cueId ? e.PuckA : -1;
                    if (struckId < 0)
                    {
                        continue;
                    }

                    // The impact readout, captured before the removal below: the cue sits at the contact
                    // spot this step, and the collision impulse is already on the struck stone's velocity.
                    // (A repeated event for the same stone fails the TryGetPuck and is skipped.)
                    if (hits < PreviewMaxHits
                        && clone.TryGetPuck(struckId, out Puck struck)
                        && clone.TryGetPuck(cueId, out Puck cueAtHit))
                    {
                        PlacePreviewHit(hits, cueAtHit, struck);
                        hits++;
                    }

                    clone.RemovePuck(struckId);
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
            _previewMarker.transform.localPosition = new Vector3(landing.x, landing.y, 0f);
            FitSpriteScale(_previewMarker, _tuning.PuckRadius * 2f);
            _previewMarker.enabled = true;

            for (int i = hits; i < PreviewMaxHits; i++)
            {
                _previewHitGhosts[i].enabled = false;
                _previewHitArrows[i].enabled = false;
            }
        }

        // One impact readout: the cue's phantom at the contact spot, plus an arrow off the struck stone's
        // rim showing which way it will fly (direction only — the one-bumper preview never rolls the
        // struck stone itself, design doc 6.2).
        private void PlacePreviewHit(int index, Puck cueAtHit, Puck struck)
        {
            SpriteRenderer ghost = _previewHitGhosts[index];
            ghost.transform.localPosition = new Vector3(cueAtHit.Position.x, cueAtHit.Position.y, 0f);
            FitSpriteScale(ghost, cueAtHit.Radius * 2f);
            ghost.enabled = true;

            SpriteRenderer arrow = _previewHitArrows[index];
            arrow.enabled = false; // stays off for a graze that barely moves the stone
            if (struck.Velocity.sqrMagnitude < 0.0001f)
            {
                return;
            }

            Vector2 direction = struck.Velocity.normalized;
            Vector2 tail = struck.Position + direction * struck.Radius;
            arrow.transform.localPosition = new Vector3(tail.x, tail.y, 0f);
            arrow.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
            arrow.enabled = true;
        }

        protected void HidePreview()
        {
            if (_previewLine != null)
            {
                _previewLine.enabled = false;
            }

            if (_previewMarker != null)
            {
                _previewMarker.enabled = false;
            }

            if (_previewHitGhosts != null)
            {
                for (int i = 0; i < _previewHitGhosts.Length; i++)
                {
                    _previewHitGhosts[i].enabled = false;
                    _previewHitArrows[i].enabled = false;
                }
            }
        }

        // --- Per-frame puck rendering -------------------------------------------------------------

        protected void UpdatePuckTransforms()
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
            int max = Mathf.Clamp(MaxStoneHealth(p), 1, MaxHealthArcs);
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
    }
}
