using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Puckmite.View
{
    /// <summary>
    /// Shared shape of the framing screens (title / game over / game clear — 사용자 지정): a dark empty
    /// scene with a heading a little above centre and a single button under it. Subclasses say the words
    /// and where the button leads. Placeholder IMGUI like every other screen, restyled later.
    /// </summary>
    public abstract class SimpleScreenController : MonoBehaviour
    {
        protected abstract string Heading { get; }
        protected abstract string ButtonLabel { get; }

        /// <summary>What the one button does.</summary>
        protected abstract void OnButton();

        private void Awake()
        {
            Camera screenCamera = Camera.main;
            if (screenCamera == null)
            {
                GameObject camObject = new GameObject("Main Camera") { tag = "MainCamera" };
                screenCamera = camObject.AddComponent<Camera>();
            }

            // URP renders through a per-camera data component (same pattern as the arenas).
            if (!screenCamera.TryGetComponent(out UniversalAdditionalCameraData _))
            {
                screenCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            }

            screenCamera.orthographic = true;
            screenCamera.clearFlags = CameraClearFlags.SolidColor;
            screenCamera.backgroundColor = new Color(0.07f, 0.08f, 0.10f); // the arenas' dark backdrop
        }

        private void OnGUI()
        {
            float midX = Screen.width * 0.5f;
            float headingY = Screen.height * 0.5f - 40f; // heading dead centre (사용자 지정), button below

            GUIStyle heading = new GUIStyle(GUI.skin.label)
            {
                fontSize = 48,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            GUI.Label(new Rect(midX - 300f, headingY, 600f, 80f), Heading, heading);

            GUIStyle button = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            if (GUI.Button(new Rect(midX - 110f, headingY + 110f, 220f, 48f), ButtonLabel, button))
            {
                OnButton();
            }
        }
    }
}
