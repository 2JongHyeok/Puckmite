using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace Puckmite.View
{
    /// <summary>
    /// Shared shape of the framing screens (game over / game clear — 사용자 지정): a full-view mock
    /// backdrop with its lettering baked in (user mock 2026-08-09), optionally the hero standing live on
    /// its grass line, and a single button under it — mock button art with the yellow hover outline once
    /// wired, the old IMGUI button until then. Subclasses say the words and where the button leads.
    /// </summary>
    public abstract class SimpleScreenController : MonoBehaviour
    {
        // The arena scenes' framing, so 720x405 backdrop mocks map 1:1 (world y = 26.5 - row/10).
        private const float ViewWidth = 72f;
        private const float CameraHalfHeight = 20.25f;
        private const float CameraCenterY = 6.25f;
        private const float HeroHeight = 6.5f;

        // The button stands on the mocks' dirt band, dead centre — the title's start-button spot.
        private static readonly Vector2 ButtonPos = new Vector2(0f, -9.4f);
        private static readonly Color OutlineTint = new Color(1f, 0.9f, 0.25f, 0.9f); // ring convention

        // The mock backdrop, the hero for screens that show one, and the button art (wired by Setup
        // Game Scenes; each is quiet until its art exists).
        [SerializeField] private Sprite _backgroundSprite;
        [SerializeField] private GameObject _heroBodyPrefab;
        [SerializeField] private Sprite _buttonSprite;

        private Camera _camera;
        private SpriteRenderer _buttonOutline;
        private SpriteRenderer _buttonBg;

        protected abstract string Heading { get; }
        protected abstract string ButtonLabel { get; }

        /// <summary>Where the hero stands when a prefab is wired (the mocks' grass line).</summary>
        protected virtual Vector2 HeroFeet => new Vector2(-7f, -3.5f);

        /// <summary>What the one button does.</summary>
        protected abstract void OnButton();

        private void Awake()
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                GameObject camObject = new GameObject("Main Camera") { tag = "MainCamera" };
                _camera = camObject.AddComponent<Camera>();
            }

            // URP renders through a per-camera data component (same pattern as the arenas).
            if (!_camera.TryGetComponent(out UniversalAdditionalCameraData _))
            {
                _camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            }

            _camera.orthographic = true;
            _camera.orthographicSize = CameraHalfHeight;
            _camera.transform.position = new Vector3(0f, CameraCenterY, -10f);
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.07f, 0.08f, 0.10f); // the arenas' dark backdrop

            BuildBackdrop();
        }

        private void Update()
        {
            if (_buttonBg == null)
            {
                return; // the IMGUI fallback button handles itself in OnGUI
            }

            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            Vector3 world3 = _camera.ScreenToWorldPoint(new Vector3(
                mouse.position.ReadValue().x, mouse.position.ReadValue().y, -_camera.transform.position.z));
            Vector2 world = new Vector2(world3.x, world3.y);

            Bounds b = _buttonBg.bounds;
            bool hover = world.x >= b.min.x && world.x <= b.max.x && world.y >= b.min.y && world.y <= b.max.y;
            _buttonOutline.enabled = hover;
            if (hover && mouse.leftButton.wasPressedThisFrame)
            {
                OnButton();
            }
        }

        // The mock backdrop scaled to the full view width by bounds (importer PPU-agnostic), the hero on
        // its grass line for screens that wire one, and the mock button on the dirt band.
        private void BuildBackdrop()
        {
            if (_backgroundSprite != null)
            {
                GameObject go = new GameObject("Background");
                go.transform.SetParent(transform, false);
                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = _backgroundSprite;
                sr.sortingOrder = -20;
                float scale = ViewWidth / sr.sprite.bounds.size.x;
                go.transform.localScale = new Vector3(scale, scale, 1f);
                go.transform.position += new Vector3(0f, CameraCenterY, 0f) - sr.bounds.center;
            }

            if (_heroBodyPrefab != null)
            {
                GameObject hero = Instantiate(_heroBodyPrefab, transform, false);
                hero.name = "Hero";
                SpriteRenderer sr = hero.GetComponentInChildren<SpriteRenderer>();
                if (sr == null || sr.sprite == null)
                {
                    Debug.LogError("[PuckHero] Hero prefab has no usable SpriteRenderer — the screen shows the backdrop only.");
                    Destroy(hero);
                }
                else
                {
                    sr.sortingOrder = 10;
                    sr.color = Color.white;
                    if (hero.TryGetComponent(out Animator animator))
                    {
                        animator.speed = 0.5f;
                    }

                    Vector2 feet = HeroFeet;
                    hero.transform.localPosition = new Vector3(feet.x, feet.y, 0f);
                    hero.transform.localScale *= HeroHeight / sr.bounds.size.y;
                    Vector3 target = transform.TransformPoint(new Vector3(feet.x, feet.y + HeroHeight * 0.5f, 0f));
                    hero.transform.position += target - sr.bounds.center;
                }
            }

            if (_buttonSprite != null)
            {
                // Sized from the art's own pixels at the backdrop density (0.1 unit/px).
                Vector2 size = new Vector2(_buttonSprite.rect.width, _buttonSprite.rect.height) * 0.1f;
                _buttonOutline = MakeRect(transform, "ButtonOutline", null, ButtonPos, size + new Vector2(0.5f, 0.5f), OutlineTint, 20);
                _buttonOutline.enabled = false;
                _buttonBg = MakeRect(transform, "Button", _buttonSprite, ButtonPos, size, Color.white, 21);
            }
        }

        private static SpriteRenderer MakeRect(Transform parent, string name, Sprite art, Vector2 pos, Vector2 size, Color tint, int order)
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

        private void OnGUI()
        {
            float midX = Screen.width * 0.5f;

            // The mock backdrop bakes its own lettering; the IMGUI heading is only the art-less fallback.
            float buttonY;
            if (_backgroundSprite == null)
            {
                float headingY = Screen.height * 0.5f - 40f; // heading dead centre (사용자 지정), button below
                GUIStyle heading = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 48,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
                GUI.Label(new Rect(midX - 300f, headingY, 600f, 80f), Heading, heading);
                buttonY = headingY + 110f;
            }
            else
            {
                buttonY = Screen.height * 0.84f;
            }

            if (_buttonSprite != null)
            {
                return; // the mock button in the scene took over
            }

            GUIStyle button = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            if (GUI.Button(new Rect(midX - 110f, buttonY, 220f, 48f), ButtonLabel, button))
            {
                OnButton();
            }
        }
    }
}
