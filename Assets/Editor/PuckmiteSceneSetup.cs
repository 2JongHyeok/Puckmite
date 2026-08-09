using System.Collections.Generic;
using System.IO;
using Puckmite.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Puckmite.EditorTools
{
    /// <summary>
    /// Builds the game's two scenes (Battle, Shop) so the user only has to run the menu and press Play —
    /// no manual GameObject setup, no scene file edited by hand (project rule: object setup goes behind a
    /// Tools/ menu). Idempotent: existing scenes, objects and the tuning asset are reused and re-wired,
    /// never duplicated. Also carries the one-time cleanup of the legacy playtest.
    /// </summary>
    public static class PuckmiteSceneSetup
    {
        private const string TuningPath = "Assets/Settings/GameTuning.asset";
        private const string HeroArtPath = "Assets/Art/Sprites/Characters/Hero.aseprite";
        private const string PeddlerArtPath = "Assets/Art/Sprites/Characters/Peddler.aseprite";
        private const string SilhouetteShaderName = "PuckHero/SpriteSilhouette";
        private const string SilhouetteMaterialPath = "Assets/Art/Materials/SpriteSilhouette.mat";
        private const string TitlePath = "Assets/Scenes/Title.unity";
        private const string BattlePath = "Assets/Scenes/Battle.unity";
        private const string ShopPath = "Assets/Scenes/Shop.unity";
        private const string GameOverPath = "Assets/Scenes/GameOver.unity";
        private const string GameClearPath = "Assets/Scenes/GameClear.unity";

        private const string LegacyScenePath = "Assets/Scenes/TestScene.unity";
        private const string LegacyScriptPath = "Assets/Scripts/View/PuckmitePlaytest.cs";
        private const string LegacyMenuPath = "Assets/Editor/PuckmitePlaytestMenu.cs";

        [MenuItem("Tools/PuckHero/Setup Game Scenes")]
        public static void SetupGameScenes()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return; // the user cancelled rather than save/discard the open scene
            }

            EnsureTuningAsset();
            SetupScene<BattleController>(BattlePath, "Battle");
            SetupScene<ShopController>(ShopPath, "Shop");
            SetupTitleScene();
            SetupSimpleScene<GameOverController>(GameOverPath, "GameOver", "Assets/Art/Sprites/UI/screen_over", false);
            SetupSimpleScene<GameClearController>(GameClearPath, "GameClear", "Assets/Art/Sprites/UI/screen_clear", true);

            // Title first: index 0 is what a build opens with (사용자 지정: 제일 처음 씬).
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(TitlePath, true),
                new EditorBuildSettingsScene(BattlePath, true),
                new EditorBuildSettingsScene(ShopPath, true),
                new EditorBuildSettingsScene(GameOverPath, true),
                new EditorBuildSettingsScene(GameClearPath, true),
            };

            // The name a player sees on the build's window title (the project default was "My project").
            PlayerSettings.productName = "PuckHero";

            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(TitlePath, OpenSceneMode.Single);
            Debug.Log("[PuckHero] All five scenes ready and in Build Settings. Press Play in Title.");
        }

        private static void EnsureTuningAsset()
        {
            if (AssetDatabase.LoadAssetAtPath<GameTuning>(TuningPath) == null)
            {
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<GameTuning>(), TuningPath);
            }
        }

        // Opens (or creates) the scene, ensures a camera and one controller object with a debug panel,
        // wires their serialized references, and saves.
        private static void SetupScene<T>(string path, string rootName) where T : ArenaControllerBase
        {
            Scene scene = File.Exists(path)
                ? EditorSceneManager.OpenScene(path, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // A plain camera object; the controller configures it (ortho, framing, URP data) at runtime.
            Camera camera = Object.FindAnyObjectByType<Camera>();
            if (camera == null)
            {
                GameObject camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                camera = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }

            T controller = Object.FindAnyObjectByType<T>();
            if (controller == null)
            {
                controller = new GameObject(rootName).AddComponent<T>();
            }

            if (!controller.TryGetComponent(out DebugPanel panel))
            {
                panel = controller.gameObject.AddComponent<DebugPanel>();
            }

            // Loaded fresh AFTER the scene switch above: an asset instance held across NewScene/OpenScene
            // (Single) can come out of the switch dead, and wiring a dead reference saves as fileID: 0 —
            // exactly the "GameTuning asset is not assigned" error at Play time.
            GameTuning tuning = AssetDatabase.LoadAssetAtPath<GameTuning>(TuningPath);
            if (tuning == null)
            {
                Debug.LogError($"[PuckHero] {TuningPath} could not be loaded — scene '{rootName}' is left unwired.");
                return;
            }

            SetReference(controller, "_tuning", tuning);
            SetReference(panel, "_tuning", tuning);
            SetReference(panel, "_arena", controller);

            // Both scenes share the aiming preview: optional dash tile and impact-ghost art for it.
            SetOptionalSprite(controller, "_previewDashSprite", "Assets/Art/Sprites/UI/PreviewDash");
            SetOptionalSprite(controller, "_previewHitGhostSprite", "Assets/Art/Sprites/UI/PreviewHitGhost");

            // Each scene's full-view backdrop (procedurally generated PNGs; quiet until they exist).
            SetOptionalSprite(controller, "_backgroundSprite", controller is BattleController
                ? "Assets/Art/Sprites/UI/battle_background"
                : "Assets/Art/Sprites/UI/shop_background");

            // Both boards' cell faces, one frame per face from the cell sheet.
            WireCellSprites(controller);

            // The silhouette material both scenes' highlight outlines draw with (battle characters, shop
            // merchant), created next to its shader on first run. A real asset (not a runtime Material)
            // so the scene reference keeps the shader in builds.
            Material silhouette = EnsureSilhouetteMaterial();
            if (silhouette != null)
            {
                SetReference(controller, "_silhouetteMaterial", silhouette);
            }

            // Battle only: the hero art prefab for the player's character-row slot. Missing art is not
            // fatal — the controller falls back to its placeholder circle — so this only warns.
            if (controller is BattleController)
            {
                GameObject heroArt = AssetDatabase.LoadAssetAtPath<GameObject>(HeroArtPath);
                if (heroArt == null)
                {
                    Debug.LogWarning($"[PuckHero] {HeroArtPath} not found — the player keeps the placeholder circle.");
                }
                else
                {
                    SetReference(controller, "_heroBodyPrefab", heroArt);
                }

                // Enemy art by kind — the file→kind mapping is design doc 4.4 (2026-08-08 확정).
                WireEnemyPrefab(controller, "_basicEnemyPrefab", "slime");
                WireEnemyPrefab(controller, "_strikerPrefab", "thief");
                WireEnemyPrefab(controller, "_tankPrefab", "pig");
                WireEnemyPrefab(controller, "_twinPrefab", "thief2");
                WireEnemyPrefab(controller, "_hardStonePrefab", "oak");
                WireEnemyPrefab(controller, "_bomberPrefab", "oak2");
                WireEnemyPrefab(controller, "_anchorPrefab", "pig2");

                // Optional art slots: quiet when the file does not exist yet (the controller renders
                // placeholders), so no warnings pile up while the art is still being drawn.
                SetOptionalSprite(controller, "_victoryPanelSprite", "Assets/Art/Sprites/UI/VictoryPanel");
                SetOptionalSprite(controller, "_victoryButtonSprite", "Assets/Art/Sprites/UI/VictoryButton");
                SetOptionalSprite(controller, "_goldIconSprite", "Assets/Art/Sprites/UI/Gold");
                WireStatIcons(controller);
            }
            else
            {
                // Shop only: the peddler art for the merchant slot. Missing art is not fatal — the
                // controller falls back to its placeholder circle — so this only warns.
                GameObject peddlerArt = AssetDatabase.LoadAssetAtPath<GameObject>(PeddlerArtPath);
                if (peddlerArt == null)
                {
                    Debug.LogWarning($"[PuckHero] {PeddlerArtPath} not found — the merchant keeps the placeholder circle.");
                }
                else
                {
                    SetReference(controller, "_merchantBodyPrefab", peddlerArt);
                }

                // The buying panel's pieces (user mock 2026-08-09); the panel renders flat placeholder
                // rects for any that are still missing.
                SetOptionalSprite(controller, "_shopPanelSprite", "Assets/Art/Sprites/UI/shop_panel");
                SetOptionalSprite(controller, "_rerollButtonSprite", "Assets/Art/Sprites/UI/btn_reroll");
                SetOptionalSprite(controller, "_closeButtonSprite", "Assets/Art/Sprites/UI/btn_close");
                SetOptionalSprite(controller, "_goldPanelSprite", "Assets/Art/Sprites/UI/Gold_pannel");
            }

            EditorSceneManager.SaveScene(scene, path);

            // Read back what was actually saved, so a wiring regression names itself here instead of
            // surfacing as a null-reference error the next time someone presses Play.
            if (new SerializedObject(controller).FindProperty("_tuning").objectReferenceValue == null)
            {
                Debug.LogError($"[PuckHero] Wiring did not stick in '{rootName}' — the tuning reference saved as null.");
            }
        }

        // The framing screens (game over / game clear): a camera, their controller, and the mock
        // backdrop's promised path — plus the hero prefab for the screens that show one (clear).
        private static void SetupSimpleScene<T>(string path, string rootName, string backgroundPath, bool wireHero) where T : MonoBehaviour
        {
            Scene scene = File.Exists(path)
                ? EditorSceneManager.OpenScene(path, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Camera camera = Object.FindAnyObjectByType<Camera>();
            if (camera == null)
            {
                GameObject camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }

            T controller = Object.FindAnyObjectByType<T>();
            if (controller == null)
            {
                controller = new GameObject(rootName).AddComponent<T>();
            }

            SetOptionalSprite(controller, "_backgroundSprite", backgroundPath);
            SetOptionalSprite(controller, "_buttonSprite", "Assets/Art/Sprites/UI/btn_title");
            if (wireHero)
            {
                GameObject heroArt = AssetDatabase.LoadAssetAtPath<GameObject>(HeroArtPath);
                if (heroArt != null)
                {
                    SetReference(controller, "_heroBodyPrefab", heroArt);
                }
            }

            EditorSceneManager.SaveScene(scene, path);
        }

        // The title (user mock 2026-08-09): its own controller with the backdrop, the live hero, the
        // start button and the difficulty picker's art — plus the tuning the difficulty pick writes.
        private static void SetupTitleScene()
        {
            Scene scene = File.Exists(TitlePath)
                ? EditorSceneManager.OpenScene(TitlePath, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Camera camera = Object.FindAnyObjectByType<Camera>();
            if (camera == null)
            {
                GameObject camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }

            TitleController controller = Object.FindAnyObjectByType<TitleController>();
            if (controller == null)
            {
                controller = new GameObject("Title").AddComponent<TitleController>();
            }

            // Loaded fresh after the scene switch above (asset instances must not cross scene loads).
            GameTuning tuning = AssetDatabase.LoadAssetAtPath<GameTuning>(TuningPath);
            if (tuning == null)
            {
                Debug.LogError($"[PuckHero] {TuningPath} could not be loaded — the title is left unwired.");
                return;
            }

            SetReference(controller, "_tuning", tuning);
            SetOptionalSprite(controller, "_backgroundSprite", "Assets/Art/Sprites/UI/screen_title");
            SetOptionalSprite(controller, "_startButtonSprite", "Assets/Art/Sprites/UI/btn_start");
            SetOptionalSprite(controller, "_closeButtonSprite", "Assets/Art/Sprites/UI/btn_close");
            SetOptionalSprite(controller, "_diffVeryEasySprite", "Assets/Art/Sprites/UI/btn_diff_veryeasy");
            SetOptionalSprite(controller, "_diffEasySprite", "Assets/Art/Sprites/UI/btn_diff_easy");
            SetOptionalSprite(controller, "_diffNormalSprite", "Assets/Art/Sprites/UI/btn_diff_normal");
            SetOptionalSprite(controller, "_diffHardSprite", "Assets/Art/Sprites/UI/btn_diff_hard");
            SetOptionalSprite(controller, "_diffVeryHardSprite", "Assets/Art/Sprites/UI/btn_diff_veryhard");

            GameObject titleHero = AssetDatabase.LoadAssetAtPath<GameObject>(HeroArtPath);
            if (titleHero == null)
            {
                Debug.LogWarning($"[PuckHero] {HeroArtPath} not found — the title shows no hero.");
            }
            else
            {
                SetReference(controller, "_heroBodyPrefab", titleHero);
            }

            EditorSceneManager.SaveScene(scene, TitlePath);
        }

        // One enemy kind's art prefab. The art exists for every kind in the spawn pool, so a missing
        // file is worth a warning — that kind would silently fall back to the placeholder circle.
        private static void WireEnemyPrefab(Object controller, string field, string fileName)
        {
            string path = $"Assets/Art/Sprites/Enemy/{fileName}.aseprite";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"[PuckHero] {path} not found — that enemy kind keeps the placeholder circle.");
                return;
            }

            SetReference(controller, field, prefab);
        }

        // Finds or creates the flat-colour silhouette material the character outline draws with. Created
        // through the API (project rule: no hand-editing Unity YAML) beside its shader.
        private static Material EnsureSilhouetteMaterial()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(SilhouetteMaterialPath);
            if (material != null)
            {
                return material;
            }

            Shader shader = Shader.Find(SilhouetteShaderName);
            if (shader == null)
            {
                Debug.LogError($"[PuckHero] Shader '{SilhouetteShaderName}' not found — character highlights will be off.");
                return null;
            }

            material = new Material(shader);
            AssetDatabase.CreateAsset(material, SilhouetteMaterialPath);
            return material;
        }

        private const string StatIconSheetPath = "Assets/Art/Sprites/UI/stat_icons_sheet.aseprite";

        // The stat icons live in one Aseprite sheet, one icon per frame in draw order: health, shield,
        // attack, stones (Frame_4, the dark stone, is reserved — 2026-08-08 user decision). Reordering
        // frames in the file shifts this mapping. Falls back to the individual promised files when the
        // sheet or a frame is missing.
        private static void WireStatIcons(Object controller)
        {
            string[] fields = { "_healthIconSprite", "_shieldIconSprite", "_attackIconSprite", "_stoneIconSprite" };
            string[] frames = { "Frame_0", "Frame_1", "Frame_2", "Frame_3" };
            string[] fallbacks = { "StatHealth", "StatShield", "StatAttack", "StatStones" };

            Dictionary<string, Sprite> byName = new Dictionary<string, Sprite>();
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(StatIconSheetPath))
            {
                if (asset is Sprite sprite)
                {
                    byName[sprite.name] = sprite;
                }
            }

            for (int i = 0; i < fields.Length; i++)
            {
                if (byName.TryGetValue(frames[i], out Sprite sheetSprite))
                {
                    SetReference(controller, fields[i], sheetSprite);
                }
                else
                {
                    SetOptionalSprite(controller, fields[i], "Assets/Art/Sprites/UI/" + fallbacks[i]);
                }
            }
        }

        private const string CellSheetPath = "Assets/Art/Sprites/UI/cell_sheet.aseprite";

        // The board-cell faces live in one Aseprite sheet, one face per frame in draw order: attack,
        // attack+sparkle (the stronger battle centre; shop level 2+), shield, shield+sparkle, MAX-health
        // heart, green heal heart, damage hazard, plain empty (8 frames since 2026-08-09). Reordering
        // frames in the file shifts this mapping. Missing frames stay null and the scene keeps its flat
        // placeholder cells.
        private static void WireCellSprites(Object controller)
        {
            string[] fields =
            {
                "_cellAttackSprite", "_cellAttackStrongSprite", "_cellShieldSprite", "_cellShieldStrongSprite",
                "_cellMaxHealthSprite", "_cellRunHealSprite", "_cellDamageSprite", "_cellEmptySprite",
            };

            Dictionary<string, Sprite> byName = new Dictionary<string, Sprite>();
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(CellSheetPath))
            {
                if (asset is Sprite sprite)
                {
                    byName[sprite.name] = sprite;
                }
            }

            for (int i = 0; i < fields.Length; i++)
            {
                if (byName.TryGetValue("Frame_" + i, out Sprite sheetSprite))
                {
                    SetReference(controller, fields[i], sheetSprite);
                }
            }
        }

        // An optional art slot: tries the promised path as .png first, then .aseprite. A missing file is
        // normal (the art is drawn over time), so nothing is logged for it.
        private static void SetOptionalSprite(Object target, string field, string pathWithoutExtension)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(pathWithoutExtension + ".png");
            if (sprite == null)
            {
                sprite = AssetDatabase.LoadAssetAtPath<Sprite>(pathWithoutExtension + ".aseprite");
            }

            if (sprite != null)
            {
                SetReference(target, field, sprite);
            }
        }

        private static void SetReference(Object target, string field, Object value)
        {
            SerializedObject so = new SerializedObject(target);
            SerializedProperty property = so.FindProperty(field);
            if (property == null)
            {
                // A silent miss here would resurface later as a "not assigned" error at Play time.
                Debug.LogError($"[PuckHero] {target.GetType().Name} has no serialized field '{field}' — wiring skipped.");
                return;
            }

            property.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // The tuning asset keeps its own values once created — code defaults only seed it. When the user
        // re-baselines the numbers (2026-08: player 50/3, enemy 10/3, stone health 3), the asset has to be
        // updated through the API (project rule: no hand-editing Unity YAML). Touches only these fields;
        // every other slider tweak in the asset survives.
        [MenuItem("Tools/PuckHero/Apply Stat Baseline (2026-08)")]
        public static void ApplyStatBaseline()
        {
            GameTuning tuning = AssetDatabase.LoadAssetAtPath<GameTuning>(TuningPath);
            if (tuning == null)
            {
                Debug.LogError("[PuckHero] GameTuning.asset not found — run Tools/PuckHero/Setup Game Scenes first.");
                return;
            }

            tuning.PlayerBaseHealth = 50;
            tuning.PlayerBaseAttack = 3;
            tuning.EnemyBaseHealth = 10;
            tuning.EnemyBaseAttack = 3;
            tuning.StoneHealth = 3;
            EditorUtility.SetDirty(tuning);
            AssetDatabase.SaveAssets();
            Debug.Log("[PuckHero] Stat baseline applied: player 50/3, enemy 10/3, stone health 3.");
        }

        // One-time cleanup, run only after the new scenes are verified: AssetDatabase handles the .meta
        // files, so nothing is left dangling.
        [MenuItem("Tools/PuckHero/Delete Legacy Playtest")]
        public static void DeleteLegacyPlaytest()
        {
            bool confirmed = EditorUtility.DisplayDialog(
                "Delete legacy playtest",
                "TestScene.unity, PuckmitePlaytest.cs and PuckmitePlaytestMenu.cs will be deleted.\n\n" +
                "Run this only after the Battle and Shop scenes are verified working.",
                "Delete",
                "Cancel");
            if (!confirmed)
            {
                return;
            }

            // Deleting the scene that is open would pull the floor out from under the editor.
            if (SceneManager.GetActiveScene().path == LegacyScenePath)
            {
                if (!File.Exists(BattlePath))
                {
                    Debug.LogError("[PuckHero] Run Tools/PuckHero/Setup Game Scenes first.");
                    return;
                }

                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    return;
                }

                EditorSceneManager.OpenScene(BattlePath, OpenSceneMode.Single);
            }

            DeleteIfPresent(LegacyScenePath);
            DeleteIfPresent(LegacyScriptPath);
            DeleteIfPresent(LegacyMenuPath);
            Debug.Log("[PuckHero] Legacy playtest removed.");
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
            {
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
