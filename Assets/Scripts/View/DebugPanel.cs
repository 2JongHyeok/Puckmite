using UnityEngine;
using UnityEngine.InputSystem;

namespace Puckmite.View
{
    /// <summary>
    /// The old playtest panel behind F1: diagnostics, the tuning sliders (writing into the shared
    /// GameTuning asset), playback speed, and the battle-only AI controls. Lives in both scenes; its
    /// visibility and the speed are static so they survive scene loads within a session.
    /// </summary>
    public sealed class DebugPanel : MonoBehaviour
    {
        [SerializeField] private GameTuning _tuning;
        [SerializeField] private ArenaControllerBase _arena;

        private static bool _visible;
        private static float _speed = 1f;

        /// <summary>Sim steps consumed per real second, x1/x2/x4 — read by the shared sim driver.</summary>
        public static float SpeedMultiplier => _speed;

        // A fresh play session opens with the panel closed at x1 (also with domain reload off).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay()
        {
            _visible = false;
            _speed = 1f;
        }

        private Vector2 _scroll;

        // Below the battle's game HUD (which ends at y=210) so the two never overlap.
        private static Rect PanelRect => new Rect(10f, 220f, 260f, Mathf.Max(120f, Screen.height - 230f));

        /// <summary>Whether the open panel covers this GUI-space point. Consulted by the scenes' pointer
        /// blocking — only what is actually drawn may block, so this is false while the panel is closed.</summary>
        public static bool Covers(Vector2 guiPoint)
        {
            return _visible && PanelRect.Contains(guiPoint);
        }

        private void Awake()
        {
            if (_tuning == null || _arena == null)
            {
                Debug.LogError("[Puckmite] DebugPanel is missing its references — run Tools/Puckmite/Setup Game Scenes.");
                enabled = false;
            }
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f1Key.wasPressedThisFrame)
            {
                _visible = !_visible;
            }
        }

        private void OnGUI()
        {
            if (!_visible)
            {
                return;
            }

            BattleController battle = _arena as BattleController;

            GUILayout.BeginArea(PanelRect, GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("Debug (F1 to close)");
            GUILayout.Label($"Pucks: {_arena.SimPuckCount}    At rest: {_arena.SimAllAtRest}");
            GUILayout.Label($"Wall bounces: {_arena.WallBounceTotal}    Collisions: {_arena.CollisionTotal}");
            GUILayout.Label($"Destroyed: {_arena.DestroyedTotal}");
            GUILayout.Label($"Preview: {_arena.PreviewMs:F3} ms");
            if (battle != null)
            {
                GUILayout.Label($"Enemy AI: {battle.AiPlanMs:F1} ms, {battle.AiPlanCandidates} shots");
            }

            GUILayout.Space(6f);
            GUILayout.Label($"Friction: {_tuning.Friction:F1}");
            _tuning.Friction = GUILayout.HorizontalSlider(_tuning.Friction, 0f, 40f);
            GUILayout.Label($"Restitution: {_tuning.Restitution:F2}");
            _tuning.Restitution = GUILayout.HorizontalSlider(_tuning.Restitution, 0f, 1f);
            GUILayout.Label($"Impact damping: {_tuning.CollisionSpeedKept:F2}");
            _tuning.CollisionSpeedKept = GUILayout.HorizontalSlider(_tuning.CollisionSpeedKept, 0.3f, 1f);
            GUILayout.Label($"Rest threshold: {_tuning.RestThreshold:F2}");
            _tuning.RestThreshold = GUILayout.HorizontalSlider(_tuning.RestThreshold, 0f, 1f);
            GUILayout.Label($"Wall damping: {_tuning.WallRestitution:F2}");
            _tuning.WallRestitution = GUILayout.HorizontalSlider(_tuning.WallRestitution, 0.3f, 1f);
            GUILayout.Label($"Power scale: {_tuning.PowerScale:F1}");
            _tuning.PowerScale = GUILayout.HorizontalSlider(_tuning.PowerScale, 0.5f, 10f);
            GUILayout.Label($"Max power: {_tuning.MaxPower:F0}");
            _tuning.MaxPower = GUILayout.HorizontalSlider(_tuning.MaxPower, 5f, 120f);
            GUILayout.Label($"Power curve: {_tuning.PowerCurve:F2}");
            _tuning.PowerCurve = GUILayout.HorizontalSlider(_tuning.PowerCurve, 0.3f, 3f);
            GUILayout.Label($"Max health: {_tuning.StoneHealth}");
            _tuning.StoneHealth = Mathf.RoundToInt(GUILayout.HorizontalSlider(_tuning.StoneHealth, 1f, 8f));
            GUILayout.Label($"Cell damage: {_tuning.CellDamage}");
            _tuning.CellDamage = Mathf.RoundToInt(GUILayout.HorizontalSlider(_tuning.CellDamage, 1f, 5f));

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Speed x{_speed:F0}", GUILayout.Width(70f));
            if (GUILayout.Button("x1"))
            {
                _speed = 1f;
            }
            if (GUILayout.Button("x2"))
            {
                _speed = 2f;
            }
            if (GUILayout.Button("x4"))
            {
                _speed = 4f;
            }
            GUILayout.EndHorizontal();

            if (battle != null)
            {
                if (GUILayout.Button(_tuning.EnemyAiEnabled ? "Enemy AI: ON" : "Enemy AI: OFF (hot-seat)"))
                {
                    battle.SetEnemyAiEnabled(!_tuning.EnemyAiEnabled);
                }

                // Difficulty row: the pressed-looking toggle is the current tier.
                GUILayout.BeginHorizontal();
                for (int i = 0; i < BattleController.AiDifficultyNames.Length; i++)
                {
                    bool selected = i == Mathf.Clamp(_tuning.AiDifficulty, 0, BattleController.AiDifficultyNames.Length - 1);
                    if (GUILayout.Toggle(selected, BattleController.AiDifficultyNames[i], GUI.skin.button) && !selected)
                    {
                        _tuning.AiDifficulty = i;
                    }
                }
                GUILayout.EndHorizontal();

                // Hidden once the run is decided: from there the game HUD's buttons are the only way on, so
                // this cannot wipe a win or hand out the run-end heal twice.
                if (battle.CanRestartRun && GUILayout.Button("Restart this run"))
                {
                    battle.RestartRun();
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
