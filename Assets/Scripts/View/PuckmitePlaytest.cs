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
        [SerializeField] private float _friction = 12f;       // constant deceleration, units/s^2
        [SerializeField] private float _restitution = 0.85f;  // puck-to-puck bounciness
        [SerializeField] private float _restThreshold = 0.05f;
        [SerializeField] private float _maxPower = 45f;        // speed cap on a launch
        [SerializeField] private float _powerScale = 4f;       // drag distance (world units) -> launch speed
        [SerializeField] private float _puckRadius = 1.5f;     // design doc: diameter 3 on a 5-wide cell

        [Header("Playback")]
        [SerializeField] private float _speedMultiplier = 1f;  // sim steps consumed per real second, x1/x2/x4

        // Board: 5 cells of side 5 => 25 units across, centred on the origin.
        private const float BoardHalf = 12.5f;
        private const float InnerHalf = 7.5f; // inner 3x3 buff zone spans cells 1..3
        private const float MinDrag = 0.3f;        // ignore tiny drags so a click does not fling
        private const float MaxAccumulated = 0.1f; // clamp so a hitch cannot trigger a step burst
        private const int MaxStepsPerFrame = 4000;

        private static readonly Rect HudRect = new Rect(10f, 10f, 260f, 360f);

        private PuckSim _sim;
        private Camera _camera;
        private SpriteRenderer[] _puckViews; // indexed by puck Id (layout uses contiguous Ids from 0)
        private SpriteRenderer _aimLine;

        private float _accumulator;
        private bool _aiming;
        private int _aimingPuckId = -1;
        private float _currentPowerFraction;

        private int _wallBounceTotal; // session tallies, straight from Step()'s events
        private int _collisionTotal;

        // Physics values currently baked into _sim; a mismatch with the fields triggers a rebuild.
        private float _appliedFriction;
        private float _appliedRestitution;
        private float _appliedRestThreshold;

        private void Awake()
        {
            BuildSimFrom(InitialLayout());
            BuildCamera();
            BuildBoard();
            BuildPuckViews();
            BuildAimLine();
            UpdatePuckTransforms();
        }

        private void Update()
        {
            RebuildIfPhysicsChanged();
            HandleInput();
            DriveSimulation();
            UpdatePuckTransforms();
        }

        // --- Simulation ---------------------------------------------------------------------------

        private List<Puck> InitialLayout()
        {
            // Player pucks down the left ("hand" side), an enemy cluster in the middle, spaced so none
            // start overlapping. Ids are contiguous from 0 so the view array can be indexed by Id.
            return new List<Puck>
            {
                new Puck(0, new Vector2(-9f, -9f), _puckRadius, 1f, PuckOwner.Player),
                new Puck(1, new Vector2(-9f, 0f), _puckRadius, 1f, PuckOwner.Player),
                new Puck(2, new Vector2(-9f, 9f), _puckRadius, 1f, PuckOwner.Player),
                new Puck(3, new Vector2(0f, 0f), _puckRadius, 1f, PuckOwner.Enemy),
                new Puck(4, new Vector2(4f, 4f), _puckRadius, 1f, PuckOwner.Enemy),
                new Puck(5, new Vector2(4f, -4f), _puckRadius, 1f, PuckOwner.Enemy),
            };
        }

        private void BuildSimFrom(List<Puck> pucks)
        {
            _sim = new PuckSim(
                new Vector2(-BoardHalf, -BoardHalf),
                new Vector2(BoardHalf, BoardHalf),
                _friction, _restitution, _restThreshold);

            for (int i = 0; i < pucks.Count; i++)
            {
                _sim.AddPuck(pucks[i]);
            }

            _appliedFriction = _friction;
            _appliedRestitution = _restitution;
            _appliedRestThreshold = _restThreshold;
        }

        private void RebuildIfPhysicsChanged()
        {
            bool changed =
                _friction != _appliedFriction ||
                _restitution != _appliedRestitution ||
                _restThreshold != _appliedRestThreshold;
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
                    else
                    {
                        _collisionTotal++;
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
            }

            if (mouse.leftButton.wasReleasedThisFrame && _aiming)
            {
                _aiming = false;
                _aimLine.gameObject.SetActive(false);

                if (_sim.TryGetPuck(_aimingPuckId, out Puck p))
                {
                    Vector2 drag = world - p.Position;
                    if (drag.magnitude >= MinDrag)
                    {
                        float power = Mathf.Min(drag.magnitude * _powerScale, _maxPower);
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
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            _puckViews = new SpriteRenderer[pucks.Count];
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

        private void UpdatePuckTransforms()
        {
            IReadOnlyList<Puck> pucks = _sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                Puck p = pucks[i];
                Transform t = _puckViews[p.Id].transform;
                t.localPosition = new Vector3(p.Position.x, p.Position.y, 0f);
                float diameter = p.Radius * 2f;
                t.localScale = new Vector3(diameter, diameter, 1f);
            }
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

            float maxLength = _maxPower / Mathf.Max(0.0001f, _powerScale);
            float length = Mathf.Min(distance, maxLength);
            _currentPowerFraction = length / maxLength;

            Vector2 unit = direction / distance;
            Vector2 mid = from + unit * (length * 0.5f);
            float angle = Mathf.Atan2(unit.y, unit.x) * Mathf.Rad2Deg;

            _aimLine.gameObject.SetActive(true);
            _aimLine.transform.localPosition = new Vector3(mid.x, mid.y, 0f);
            _aimLine.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
            _aimLine.transform.localScale = new Vector3(length, 0.18f, 1f);
            _aimLine.color = Color.Lerp(new Color(0.5f, 1f, 0.4f, 0.9f), new Color(1f, 0.35f, 0.25f, 0.95f), _currentPowerFraction);
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
            GUILayout.Label(_aiming ? $"Power: {_currentPowerFraction * 100f:F0}%" : "Power: -");

            GUILayout.Space(6f);
            GUILayout.Label($"Friction: {_friction:F1}");
            _friction = GUILayout.HorizontalSlider(_friction, 0f, 40f);
            GUILayout.Label($"Restitution: {_restitution:F2}");
            _restitution = GUILayout.HorizontalSlider(_restitution, 0f, 1f);
            GUILayout.Label($"Power scale: {_powerScale:F1}");
            _powerScale = GUILayout.HorizontalSlider(_powerScale, 0.5f, 10f);
            GUILayout.Label($"Max power: {_maxPower:F0}");
            _maxPower = GUILayout.HorizontalSlider(_maxPower, 5f, 120f);

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
