using Puckmite.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Puckmite.EditorTools
{
    /// <summary>
    /// Creates the runtime playtest object in the open scene so the user only has to click the menu
    /// and press Play — no manual GameObject setup, no scene file edited (design doc 7.9, and the
    /// project rule that object setup goes behind a Tools/ menu). Idempotent: reuses the existing
    /// object if one is already there.
    /// </summary>
    public static class PuckmitePlaytestMenu
    {
        [MenuItem("Tools/Puckmite/Create Playtest Object")]
        public static void CreatePlaytestObject()
        {
            PuckmitePlaytest existing = Object.FindAnyObjectByType<PuckmitePlaytest>();
            GameObject go;
            if (existing != null)
            {
                go = existing.gameObject;
            }
            else
            {
                go = new GameObject("Puckmite Playtest");
                go.AddComponent<PuckmitePlaytest>();
                Undo.RegisterCreatedObjectUndo(go, "Create Puckmite Playtest");
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            }

            Selection.activeGameObject = go;
            Debug.Log("[Puckmite] Playtest object ready in the scene. Press Play to fling the puck.");
        }
    }
}
