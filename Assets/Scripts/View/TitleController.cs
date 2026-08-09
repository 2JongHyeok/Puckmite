using Puckmite.Game;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace Puckmite.View
{
    /// <summary>
    /// The title screen (user mock 2026-08-09): a full-view backdrop with the logo baked in, the hero
    /// standing live on the mock's grass line, the start button on the dirt dead centre below, and a
    /// difficulty picker that opens over it — its X (or ESC) backs out, picking a difficulty writes the
    /// tuning's AiDifficulty and starts a fresh campaign. Buttons are mock art with the yellow hover
    /// outline; everything renders as world sprites on the arena scenes' camera framing.
    /// </summary>
    public sealed class TitleController : MonoBehaviour
    {
        // The arena scenes' framing, so the 720x405 backdrop mocks map 1:1 (world y = 26.5 - row/10).
        private const float ViewWidth = 72f;
        private const float CameraHalfHeight = 20.25f;
        private const float CameraCenterY = 6.25f;

        // Mock geometry: the title's own grass line sits low, the button on the dirt below it.
        private static readonly Vector2 HeroFeet = new Vector2(0f, -3.5f);
        private const float HeroHeight = 7.5f;
        private static readonly Vector2 StartButtonPos = new Vector2(0f, -9.4f);
        private static readonly Vector2 StartButtonSize = new Vector2(16.8f, 4f);

        // The difficulty picker (mock 2): a dark gold-framed box over the centre, five mock buttons,
        // the close X top-right (사용자 지정).
        private static readonly Vector2 PanelCenter = new Vector2(0f, 0.3f);
        private static readonly Vector2 PanelSize = new Vector2(21.5f, 23f);
        private static readonly Vector2 DiffButtonSize = new Vector2(17.6f, 3.4f);
        private const float DiffFirstY = 6.8f;   // top button centre; the rest step down evenly
        private const float DiffStepY = 3.8f;

        private static readonly Color OutlineTint = new Color(1f, 0.9f, 0.25f, 0.9f); // ring convention
        private static readonly Color PanelTint = new Color(0.08f, 0.10f, 0.15f, 0.97f);
        private static readonly Color PanelFrameTint = new Color(0.72f, 0.55f, 0.22f);
        private static readonly Color ButtonTint = new Color(0.28f, 0.33f, 0.44f, 1f); // art-less fallback

        [SerializeField] private GameTuning _tuning;            // the difficulty pick writes AiDifficulty
        [SerializeField] private AudioClip _bgmClip;            // Sound/bgm/BGM_Title.mp3 (사용자 지정 2026-08-10)
        [SerializeField] private Sprite _backgroundSprite;      // promised: UI/title_background (logo baked, 문구 없음)
        [SerializeField] private GameObject _heroBodyPrefab;    // Characters/Hero.aseprite prefab
        [SerializeField] private Sprite _startButtonSprite;     // UI/btn_start
        [SerializeField] private Sprite _closeButtonSprite;     // UI/btn_close (shared with the shop panel)
        [SerializeField] private Sprite _diffVeryEasySprite;    // UI/btn_diff_veryeasy .. in AiDifficulty order
        [SerializeField] private Sprite _diffEasySprite;
        [SerializeField] private Sprite _diffNormalSprite;
        [SerializeField] private Sprite _diffHardSprite;
        [SerializeField] private Sprite _diffVeryHardSprite;

        private Camera _camera;
        private SpriteRenderer _startOutline;
        private SpriteRenderer _startBg;
        private GameObject _difficultyRoot;
        private SpriteRenderer _closeOutline;
        private SpriteRenderer _closeBg;
        private SpriteRenderer[] _diffOutlines;
        private SpriteRenderer[] _diffBgs;
        private bool _pickerOpen;

        private void Awake()
        {
            GameAudio.PlayBgm(_bgmClip);
            BuildCamera();
            BuildBackdrop();
            BuildStartButton();
            BuildDifficultyPanel();
        }

        private void Update()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            Vector2 world = ScreenToWorld(mouse.position.ReadValue());
            bool clickDown = mouse.leftButton.wasPressedThisFrame;

            if (_pickerOpen)
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                {
                    SetPickerOpen(false);
                    return;
                }

                bool hoverClose = Contains(_closeBg, world);
                _closeOutline.enabled = hoverClose;

                int hovered = -1;
                for (int i = 0; i < _diffBgs.Length; i++)
                {
                    bool hover = Contains(_diffBgs[i], world);
                    _diffOutlines[i].enabled = hover;
                    if (hover)
                    {
                        hovered = i;
                    }
                }

                if (!clickDown)
                {
                    return;
                }

                if (hoverClose)
                {
                    SetPickerOpen(false);
                }
                else if (hovered >= 0)
                {
                    StartAtDifficulty(hovered);
                }

                return;
            }

            bool hoverStart = Contains(_startBg, world);
            _startOutline.enabled = hoverStart;
            if (clickDown && hoverStart)
            {
                SetPickerOpen(true);
            }
        }

        // Picking writes the difficulty every campaign start reads (the same value as the debug panel's
        // AI buttons), then begins a fresh campaign (design doc: 시작은 언제나 새 캠페인).
        private void StartAtDifficulty(int difficulty)
        {
            if (_tuning != null)
            {
                _tuning.AiDifficulty = difficulty;
            }
            else
            {
                Debug.LogError("[PuckHero] Title has no tuning wired — run Tools/PuckHero/Setup Game Scenes; starting at the current difficulty.");
            }

            GameFlow.StartNewCampaign();
        }

        private void SetPickerOpen(bool open)
        {
            _pickerOpen = open;
            _difficultyRoot.SetActive(open);
            _startOutline.enabled = false;
        }

        // --- Construction ---------------------------------------------------------------------------

        private void BuildCamera()
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                GameObject camObject = new GameObject("Main Camera") { tag = "MainCamera" };
                _camera = camObject.AddComponent<Camera>();
                camObject.AddComponent<AudioListener>();
            }

            if (!_camera.TryGetComponent(out UniversalAdditionalCameraData _))
            {
                _camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            }

            _camera.orthographic = true;
            _camera.orthographicSize = CameraHalfHeight;
            _camera.transform.position = new Vector3(0f, CameraCenterY, -10f);
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.07f, 0.08f, 0.10f); // the mock sky's dark, past its edges
        }

        // The mock backdrop scaled to the full view width by bounds (importer PPU-agnostic), and the
        // hero standing on its grass line. Both are quiet until their art is wired.
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
                    Debug.LogError("[PuckHero] Hero prefab has no usable SpriteRenderer — the title stays empty.");
                    Destroy(hero);
                    return;
                }

                sr.sortingOrder = 10;
                sr.color = Color.white;
                if (hero.TryGetComponent(out Animator animator))
                {
                    animator.speed = 0.5f; // the battle idle's feel
                }

                hero.transform.localPosition = new Vector3(HeroFeet.x, HeroFeet.y, 0f);
                hero.transform.localScale *= HeroHeight / sr.bounds.size.y;
                Vector3 target = transform.TransformPoint(new Vector3(HeroFeet.x, HeroFeet.y + HeroHeight * 0.5f, 0f));
                hero.transform.position += target - sr.bounds.center;
            }
        }

        private void BuildStartButton()
        {
            _startOutline = MakeRect(transform, "StartOutline", null, StartButtonPos,
                StartButtonSize + new Vector2(0.5f, 0.5f), OutlineTint, 20);
            _startOutline.enabled = false;
            _startBg = MakeRect(transform, "StartButton", _startButtonSprite, StartButtonPos, StartButtonSize,
                _startButtonSprite != null ? Color.white : ButtonTint, 21);
            if (_startButtonSprite == null)
            {
                MakeText(transform, "StartLabel", "게임 시작", StartButtonPos, new Vector2(15f, 3f), 9f, 22);
            }
        }

        private void BuildDifficultyPanel()
        {
            _difficultyRoot = new GameObject("DifficultyPanel");
            _difficultyRoot.transform.SetParent(transform, false);
            _difficultyRoot.transform.localPosition = PanelCenter;

            // The mock's thin gold frame: a frame rect with the dark box inset over it.
            MakeRect(_difficultyRoot.transform, "Frame", null, Vector2.zero,
                PanelSize + new Vector2(0.3f, 0.3f), PanelFrameTint, 30);
            MakeRect(_difficultyRoot.transform, "PanelBg", null, Vector2.zero, PanelSize, PanelTint, 31);

            TextMeshPro heading = MakeText(_difficultyRoot.transform, "Heading", "난이도 선택",
                new Vector2(0f, PanelSize.y * 0.5f - 1.7f), new Vector2(12f, 2.2f), 8f, 33);
            heading.color = new Color(1f, 0.85f, 0.35f); // the mock's gold heading

            Vector2 closePos = new Vector2(PanelSize.x * 0.5f - 1.6f, PanelSize.y * 0.5f - 1.6f);
            _closeOutline = MakeRect(_difficultyRoot.transform, "CloseOutline", null, closePos, new Vector2(3.0f, 3.0f), OutlineTint, 32);
            _closeOutline.enabled = false;
            _closeBg = MakeRect(_difficultyRoot.transform, "CloseBg", _closeButtonSprite, closePos, new Vector2(2.6f, 2.6f),
                _closeButtonSprite != null ? Color.white : new Color(0.62f, 0.18f, 0.22f), 33);

            Sprite[] faces = { _diffVeryEasySprite, _diffEasySprite, _diffNormalSprite, _diffHardSprite, _diffVeryHardSprite };
            _diffBgs = new SpriteRenderer[faces.Length];
            _diffOutlines = new SpriteRenderer[faces.Length];
            for (int i = 0; i < faces.Length; i++)
            {
                Vector2 pos = new Vector2(0f, DiffFirstY - DiffStepY * i - PanelCenter.y);
                _diffOutlines[i] = MakeRect(_difficultyRoot.transform, $"DiffOutline{i}", null, pos,
                    DiffButtonSize + new Vector2(0.4f, 0.4f), OutlineTint, 32);
                _diffOutlines[i].enabled = false;
                _diffBgs[i] = MakeRect(_difficultyRoot.transform, $"DiffButton{i}", faces[i], pos, DiffButtonSize,
                    faces[i] != null ? Color.white : ButtonTint, 33);
                if (faces[i] == null)
                {
                    MakeText(_difficultyRoot.transform, $"DiffLabel{i}", BattleController.AiDifficultyNames[i],
                        pos, new Vector2(16f, 2.6f), 7f, 34);
                }
            }

            _difficultyRoot.SetActive(false);
        }

        // --- Small shared helpers (the VictoryPanel/MerchantPanel placeholder patterns) --------------

        private Vector2 ScreenToWorld(Vector2 screen)
        {
            Vector3 world = _camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -_camera.transform.position.z));
            return new Vector2(world.x, world.y);
        }

        private static bool Contains(SpriteRenderer sr, Vector2 world)
        {
            Bounds b = sr.bounds;
            return world.x >= b.min.x && world.x <= b.max.x && world.y >= b.min.y && world.y <= b.max.y;
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

        private static TextMeshPro MakeText(Transform parent, string name, string text, Vector2 pos, Vector2 box, float maxSize, int order)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            if (KoreanFont.Asset() != null)
            {
                tmp.font = KoreanFont.Asset();
            }

            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 1f;
            tmp.fontSizeMax = maxSize;
            tmp.rectTransform.sizeDelta = box;
            tmp.color = Color.white;
            tmp.GetComponent<MeshRenderer>().sortingOrder = order;
            return tmp;
        }
    }
}
