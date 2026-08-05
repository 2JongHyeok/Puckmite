using System.Collections.Generic;
using Puckmite.Sim;
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

        [Header("Playback")]
        [SerializeField] private float _speedMultiplier = 1f;  // sim steps consumed per real second, x1/x2/x4

        // Board: 5 cells of side 5 => 25 units across, centred on the origin.
        private const float BoardHalf = 12.5f;
        private const float InnerHalf = 7.5f; // inner 3x3 buff zone spans cells 1..3
        private const float MinDrag = 0.3f;        // ignore tiny drags so a click does not fling
        private const float MaxAccumulated = 0.1f; // clamp so a hitch cannot trigger a step burst
        private const int MaxStepsPerFrame = 4000;

        // Trajectory preview: safety cap on how many steps to roll the cue forward when tracing its path.
        private const int PreviewMaxSteps = 2000;

        // Health display: max arc segments drawn around a puck (matches the health slider's max).
        private const int MaxHealthArcs = 8;

        // Cell-occupancy highlights: pool cap (6 pucks * up to 4 cells, with margin).
        private const int MaxCellHighlights = 30;

        private static readonly Rect HudRect = new Rect(10f, 10f, 260f, 640f);

        private PuckSim _sim;
        private Camera _camera;
        private SpriteRenderer[] _puckViews; // indexed by puck Id (layout uses contiguous Ids from 0)
        private LineRenderer[][] _healthArcs; // [puckId][arc]: health shown as arc segments around the rim
        private Material _arcMaterial;        // shared by every health arc
        private SpriteRenderer[] _cellHighlights;                    // pool of occupied-cell overlays
        private readonly List<int> _occupiedCells = new List<int>(); // reused per puck each frame
        private SpriteRenderer _aimLine;
        private LineRenderer _previewLine;
        private SpriteRenderer _previewMarker; // ghost circle at the cue's predicted final position
        private readonly List<Vector3> _previewPoints = new List<Vector3>();
        private float _previewMs; // last preview compute time, shown in the HUD

        private float _accumulator;
        private bool _aiming;
        private int _aimingPuckId = -1;
        private float _currentPowerFraction;

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
            UpdatePuckTransforms();
            UpdateCellHighlights();
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
            BuildCamera();
            BuildBoard();
            BuildCellHighlights();
            BuildPuckViews();
            BuildAimLine();
            BuildPreviewLine();
            BuildPreviewMarker();
            UpdatePuckTransforms();
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
                    else
                    {
                        _destroyedTotal++;
                    }
                }

                _accumulator -= PuckSim.Dt;
                steps++;
            }
        }

        private void ResetPucks()
        {
            BuildSimFrom(InitialLayout());
            _accumulator = 0f;
            _wallBounceTotal = 0;
            _collisionTotal = 0;
            _destroyedTotal = 0;
            UpdatePuckTransforms();
        }

        // --- Input --------------------------------------------------------------------------------

        private void HandleInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            Vector2 screen = mouse.position.ReadValue();
            Vector2 world = ScreenToWorld(screen);

            if (mouse.leftButton.wasPressedThisFrame && !PointerOverHud(screen))
            {
                int id = NearestPuckId(world);
                if (id >= 0)
                {
                    _aiming = true;
                    _aimingPuckId = id;
                }
            }

            if (_aiming && _sim.TryGetPuck(_aimingPuckId, out Puck aimPuck))
            {
                UpdateAimLine(aimPuck.Position, world);

                Vector2 drag = world - aimPuck.Position;
                if (drag.magnitude >= MinDrag)
                {
                    float power = _maxPower * DragToPowerFraction(drag.magnitude);
                    ComputePreview(_aimingPuckId, drag.normalized * power);
                }
                else
                {
                    HidePreview();
                }
            }

            if (mouse.leftButton.wasReleasedThisFrame && _aiming)
            {
                _aiming = false;
                _aimLine.gameObject.SetActive(false);
                HidePreview();

                if (_sim.TryGetPuck(_aimingPuckId, out Puck p))
                {
                    Vector2 drag = world - p.Position;
                    if (drag.magnitude >= MinDrag)
                    {
                        float power = _maxPower * DragToPowerFraction(drag.magnitude);
                        _sim.SetVelocity(_aimingPuckId, drag.normalized * power);
                        _accumulator = 0f;
                    }
                }

                _aimingPuckId = -1;
            }
        }

        // Closest puck to the point, if within a forgiving grab radius; -1 if the click is in open space.
        private int NearestPuckId(Vector2 world)
        {
            float grab = Mathf.Max(_puckRadius * 2.5f, 3f);
            float bestSqr = grab * grab;
            int best = -1;

            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
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
            _camera.transform.position = new Vector3(0f, 0f, -10f);
            _camera.transform.rotation = Quaternion.identity;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.07f, 0.08f, 0.10f);

            // Frame the whole board with a margin, and make sure it fits horizontally too.
            float margin = 1.12f;
            float aspect = Mathf.Max(0.0001f, _camera.aspect);
            _camera.orthographicSize = Mathf.Max(BoardHalf * margin, BoardHalf * margin / aspect);
        }

        private void BuildBoard()
        {
            Transform board = new GameObject("Board").transform;
            board.SetParent(transform, false);

            float full = BoardHalf * 2f;

            MakeQuad("Background", board, Vector2.zero, new Vector2(full, full), new Color(0.16f, 0.17f, 0.20f), 0);
            MakeQuad("InnerZone", board, Vector2.zero, new Vector2(InnerHalf * 2f, InnerHalf * 2f), new Color(0.22f, 0.27f, 0.36f), 1);

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

        private void BuildPuckViews()
        {
            _arcMaterial = new Material(Shader.Find("Sprites/Default"));

            IReadOnlyList<Puck> pucks = _sim.Pucks;
            _puckViews = new SpriteRenderer[pucks.Count];
            _healthArcs = new LineRenderer[pucks.Count][];
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

        private void BuildAimLine()
        {
            GameObject go = new GameObject("AimLine");
            go.transform.SetParent(transform, false);

            _aimLine = go.AddComponent<SpriteRenderer>();
            _aimLine.sprite = ProceduralSprites.Unit();
            _aimLine.sortingOrder = 11;
            go.SetActive(false);
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
            }
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

        private void UpdateAimLine(Vector2 from, Vector2 to)
        {
            Vector2 direction = to - from;
            float distance = direction.magnitude;
            if (distance < 1e-4f)
            {
                _aimLine.gameObject.SetActive(false);
                _currentPowerFraction = 0f;
                return;
            }

            float dragForMax = _maxPower / Mathf.Max(0.0001f, _powerScale);
            float length = Mathf.Min(distance, dragForMax); // line follows the cursor up to the max-power drag
            _currentPowerFraction = DragToPowerFraction(distance);

            Vector2 unit = direction / distance;
            Vector2 mid = from + unit * (length * 0.5f);
            float angle = Mathf.Atan2(unit.y, unit.x) * Mathf.Rad2Deg;

            _aimLine.gameObject.SetActive(true);
            _aimLine.transform.localPosition = new Vector3(mid.x, mid.y, 0f);
            _aimLine.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
            _aimLine.transform.localScale = new Vector3(length, 0.18f, 1f);
            _aimLine.color = Color.Lerp(new Color(0.5f, 1f, 0.4f, 0.9f), new Color(1f, 0.35f, 0.25f, 0.95f), _currentPowerFraction);
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
    }
}
