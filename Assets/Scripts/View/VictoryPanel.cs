using System;
using TMPro;
using UnityEngine;

namespace Puckmite.View
{
    /// <summary>
    /// The run-victory panel in the difficulty picker's dress (사용자 지정 2026-08-10: gold frame, dark
    /// box, gold heading): drops in from above the camera, reports the run's gold, offers one fixed
    /// bonus pick — heal 20% of max health, or double the gold — and the way on to the shop. Picking one
    /// hides the other and keeps the pick visible; the shop button works with or without a pick. Pure
    /// view: the controller owns the effects and feeds numbers in through Show and the callbacks.
    /// </summary>
    public sealed class VictoryPanel
    {
        private const float CenterY = 6.25f;  // camera centre (content spans -12.5..25, ArenaControllerBase)
        private const float DropFrom = 26f;   // starts this far above the target, fully off screen
        private const float DropSeconds = 0.45f;
        private const float PanelScale = 2f;  // whole-panel size (사용자 지정: 가로·세로 2배)

        public Action HealChosen = delegate { };
        public Action GoldChosen = delegate { };
        public Action ShopChosen = delegate { };

        // The difficulty picker's dress (TitleController's picker, reused by the ESC menu too).
        private static readonly Color PanelTint = new Color(0.08f, 0.10f, 0.15f, 0.97f);
        private static readonly Color FrameTint = new Color(0.72f, 0.55f, 0.22f);
        private static readonly Color HeadingTint = new Color(1f, 0.85f, 0.35f);
        private static readonly Color ButtonTint = new Color(0.28f, 0.33f, 0.44f, 1f);
        private static readonly Color ChosenTint = new Color(0.30f, 0.55f, 0.38f, 1f);
        private static readonly Color GoldTint = new Color(0.98f, 0.80f, 0.25f, 1f);
        private static readonly Color OutlineTint = new Color(1f, 0.9f, 0.25f, 0.9f); // ring convention

        private readonly GameObject _root;
        private readonly TextMeshPro _goldLine;
        private readonly GameObject _healButton;
        private readonly SpriteRenderer _healBg;
        private readonly SpriteRenderer _healOutline;
        private readonly GameObject _goldButton;
        private readonly SpriteRenderer _goldBg;
        private readonly SpriteRenderer _goldOutline;
        private readonly TextMeshPro _goldButtonText;
        private readonly SpriteRenderer _shopBg;
        private readonly SpriteRenderer _shopOutline;

        private bool _shown;
        private bool _choiceMade;
        private float _dropT;

        public bool IsShown => _shown;

        public VictoryPanel(Transform parent, Sprite goldArt)
        {
            _root = new GameObject("VictoryPanel");
            _root.transform.SetParent(parent, false);
            _root.transform.localScale = new Vector3(PanelScale, PanelScale, 1f); // scales layout, art and hit bounds together

            // The picker's thin gold frame with the dark box inset over it (+0.3 world at the ×2 scale).
            MakeRect(_root.transform, "Frame", null, Vector2.zero, new Vector2(16.15f, 11.15f), FrameTint, 19);
            MakeRect(_root.transform, "PanelBg", null, Vector2.zero, new Vector2(16f, 11f), PanelTint, 20);
            TextMeshPro title = MakeText(_root.transform, "Title", "승리!", new Vector2(0f, 7.2f), new Vector2(12f, 3f),
                TextAlignmentOptions.Center, 14f, 24);
            title.color = HeadingTint;

            MakeIcon(_root.transform, "GoldIcon", goldArt, new Vector2(-2.4f, 3.6f), 0.9f, 23);
            _goldLine = MakeText(_root.transform, "GoldLine", "x 0 획득!", new Vector2(1.9f, 3.6f),
                new Vector2(7.4f, 1.4f), TextAlignmentOptions.MidlineLeft, 8f, 23);

            MakeText(_root.transform, "ChoiceLabel", "추가 선택", new Vector2(0f, 2.0f), new Vector2(8f, 1.2f),
                TextAlignmentOptions.Center, 6f, 23);

            // Heal pick. The outline behind the body rect doubles as the hover highlight.
            _healButton = new GameObject("HealButton");
            _healButton.transform.SetParent(_root.transform, false);
            _healButton.transform.localPosition = new Vector3(0f, 0.4f, 0f);
            _healOutline = MakeRect(_healButton.transform, "Outline", null, Vector2.zero, new Vector2(10.3f, 2.1f), OutlineTint, 21);
            _healOutline.enabled = false;
            _healBg = MakeRect(_healButton.transform, "Bg", null, Vector2.zero, new Vector2(10f, 1.8f), ButtonTint, 22);
            MakeText(_healButton.transform, "Text", "최대 체력 20% 회복", Vector2.zero, new Vector2(9f, 1.6f),
                TextAlignmentOptions.Center, 8f, 23);

            // Double-gold pick, with the coin inside the label like the mock.
            _goldButton = new GameObject("GoldButton");
            _goldButton.transform.SetParent(_root.transform, false);
            _goldButton.transform.localPosition = new Vector3(0f, -1.9f, 0f);
            _goldOutline = MakeRect(_goldButton.transform, "Outline", null, Vector2.zero, new Vector2(10.3f, 2.1f), OutlineTint, 21);
            _goldOutline.enabled = false;
            _goldBg = MakeRect(_goldButton.transform, "Bg", null, Vector2.zero, new Vector2(10f, 1.8f), ButtonTint, 22);
            MakeIcon(_goldButton.transform, "Icon", goldArt, new Vector2(-3.6f, 0f), 0.8f, 23);
            _goldButtonText = MakeText(_goldButton.transform, "Text", "x 0 추가 획득", new Vector2(0.5f, 0f),
                new Vector2(7.6f, 1.6f), TextAlignmentOptions.Center, 8f, 23);

            GameObject shopButton = new GameObject("ShopButton");
            shopButton.transform.SetParent(_root.transform, false);
            shopButton.transform.localPosition = new Vector3(0f, -4.2f, 0f);
            _shopOutline = MakeRect(shopButton.transform, "Outline", null, Vector2.zero, new Vector2(6.3f, 1.8f), OutlineTint, 21);
            _shopOutline.enabled = false;
            _shopBg = MakeRect(shopButton.transform, "Bg", null, Vector2.zero, new Vector2(6f, 1.5f), ButtonTint, 22);
            MakeText(shopButton.transform, "Text", "상점으로", Vector2.zero, new Vector2(5.4f, 1.3f),
                TextAlignmentOptions.Center, 8f, 23);

            _root.SetActive(false);
        }

        /// <summary>Resets the pick state, fills in the run's gold and starts the drop.</summary>
        public void Show(int goldEarned)
        {
            _goldLine.text = $"x {goldEarned} 획득!";
            _goldButtonText.text = $"x {goldEarned} 추가 획득";
            _shown = true;
            _choiceMade = false;
            _dropT = 0f;
            _root.transform.localPosition = new Vector3(0f, CenterY + DropFrom, 0f);
            _root.SetActive(true);
        }

        /// <summary>Rewrites the earned line — the controller calls this after doubling.</summary>
        public void SetGoldAmount(int total)
        {
            _goldLine.text = $"x {total} 획득!";
        }

        // Drives the drop and the pointer work. World is the cursor in world space; pointerBlocked marks
        // the cursor sitting on the HUD/debug panel so the panel neither hovers nor clicks under it.
        public void Tick(float dt, Vector2 world, bool clickDown, bool pointerBlocked)
        {
            if (!_shown)
            {
                return;
            }

            if (_dropT < 1f)
            {
                _dropT = Mathf.Min(1f, _dropT + dt / DropSeconds);
                float eased = EaseOutBack(_dropT);
                _root.transform.localPosition = new Vector3(0f, CenterY + DropFrom * (1f - eased), 0f);
            }

            bool interactable = _dropT >= 1f && !pointerBlocked;
            bool hoverHeal = interactable && !_choiceMade && _healButton.activeSelf && Contains(_healBg, world);
            bool hoverGold = interactable && !_choiceMade && _goldButton.activeSelf && Contains(_goldBg, world);
            bool hoverShop = interactable && Contains(_shopBg, world);
            _healOutline.enabled = hoverHeal;
            _goldOutline.enabled = hoverGold;
            _shopOutline.enabled = hoverShop;

            if (!clickDown || !interactable)
            {
                return;
            }

            if (hoverHeal)
            {
                _choiceMade = true;
                _goldButton.SetActive(false);        // the pick that was passed over disappears
                _healBg.color = ChosenTint;          // the taken pick stays, marked as settled
                _healOutline.enabled = false;
                HealChosen();
            }
            else if (hoverGold)
            {
                _choiceMade = true;
                _healButton.SetActive(false);
                _goldBg.color = ChosenTint;
                _goldOutline.enabled = false;
                GoldChosen();
            }
            else if (hoverShop)
            {
                ShopChosen();
            }
        }

        // Standard ease-out-back: lands past the target and settles — the requested "툭" drop.
        private static float EaseOutBack(float t)
        {
            const float c1 = 1.2f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
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
            go.transform.localPosition = pos;

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = art != null ? art : ProceduralSprites.Unit();
            Bounds b = sr.sprite.bounds;
            go.transform.localScale = new Vector3(size.x / b.size.x, size.y / b.size.y, 1f);
            sr.color = tint;
            sr.sortingOrder = order;
            return sr;
        }

        private static SpriteRenderer MakeIcon(Transform parent, string name, Sprite art, Vector2 pos, float height, int order)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = art != null ? art : ProceduralSprites.Circle();
            sr.color = art != null ? Color.white : GoldTint;
            go.transform.localScale = Vector3.one * (height / sr.sprite.bounds.size.y);
            sr.sortingOrder = order;
            return sr;
        }

        private static TextMeshPro MakeText(Transform parent, string name, string text, Vector2 pos, Vector2 box,
            TextAlignmentOptions alignment, float maxSize, int order)
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
            tmp.alignment = alignment;
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
