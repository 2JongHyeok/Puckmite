using System;
using TMPro;
using UnityEngine;

namespace Puckmite.View
{
    /// <summary>
    /// The merchant's buying panel (user mock 2026-08-09): a wooden panel parked over the board with the
    /// close X top-right, the reroll bar under it (its cost written in the bar's empty right half), the
    /// gold readout just left of the X, and three offer cards — cell-sheet faces with the price under
    /// each. Pure view like VictoryPanel: the controller owns the shop rules and feeds state in through
    /// the setters every frame; clicks come back through the callbacks.
    /// </summary>
    public sealed class MerchantPanel
    {
        // Panel geometry: 360x200px art at the backdrop's pixel density (0.1 unit/px) = 36x20 world,
        // parked over the board (top edge on the board's top wall, one cell row left visible below).
        private static readonly Vector2 PanelCenter = new Vector2(0f, 2.5f);
        private static readonly Vector2 PanelSize = new Vector2(36f, 20f);

        private static readonly Color PanelTint = new Color(0.14f, 0.17f, 0.24f, 0.97f);   // art-less fallback
        private static readonly Color ButtonTint = new Color(0.28f, 0.33f, 0.44f, 1f);
        private static readonly Color OutlineTint = new Color(1f, 0.9f, 0.25f, 0.9f);      // ring convention
        private static readonly Color GoldTextColor = new Color(1f, 0.823f, 0.29f);        // #ffd24a, GoldTag
        private static readonly Color DeniedTextColor = new Color(1f, 0.353f, 0.29f);      // #ff5a4a
        private static readonly Color FaceDimmed = new Color(1f, 1f, 1f, 0.45f);
        private static readonly Color BarDimmed = new Color(1f, 1f, 1f, 0.55f);

        public Action CloseClicked = delegate { };
        public Action RerollClicked = delegate { };
        public Action<int> SlotClicked = delegate { };

        private sealed class Slot
        {
            public SpriteRenderer Outline;
            public SpriteRenderer Face;
            public TextMeshPro Overlay;
            public TextMeshPro Price;
            public bool Sold;
            public bool Affordable;
            public Sprite FittedSprite;
        }

        private readonly GameObject _root;
        private readonly SpriteRenderer _closeOutline;
        private readonly SpriteRenderer _closeBg;
        private readonly SpriteRenderer _rerollOutline;
        private readonly SpriteRenderer _rerollBg;
        private readonly TextMeshPro _rerollCost;
        private readonly TextMeshPro _goldText;
        private readonly Slot[] _slots;
        private readonly bool _rerollUsesArt;

        private bool _rerollAffordable;
        private int _hoveredSlot = -1;

        /// <summary>The offer slot under the cursor (sold ones excluded), -1 for none — the controller's
        /// tooltip reads this.</summary>
        public int HoveredSlot => _hoveredSlot;

        public MerchantPanel(Transform parent, int slotCount, Sprite panelArt, Sprite closeArt, Sprite rerollArt, Sprite goldPanelArt)
        {
            _root = new GameObject("MerchantPanel");
            _root.transform.SetParent(parent, false);
            _root.transform.localPosition = PanelCenter;

            MakeRect(_root.transform, "PanelBg", panelArt, Vector2.zero, PanelSize,
                panelArt != null ? Color.white : PanelTint, 20);

            // Close X in the frame's top-right corner (mock position).
            _closeOutline = MakeRect(_root.transform, "CloseOutline", null, new Vector2(16.1f, 7.9f), new Vector2(3.0f, 3.0f), OutlineTint, 21);
            _closeOutline.enabled = false;
            _closeBg = MakeRect(_root.transform, "CloseBg", closeArt, new Vector2(16.1f, 7.9f), new Vector2(2.6f, 2.6f),
                closeArt != null ? Color.white : new Color(0.62f, 0.18f, 0.22f), 22);

            // Gold readout, just left of the X with a small gap (사용자 지정).
            MakeRect(_root.transform, "GoldPanelBg", goldPanelArt, new Vector2(11.65f, 7.9f), new Vector2(5.6f, 2.6f),
                goldPanelArt != null ? Color.white : ButtonTint, 22);
            _goldText = MakeText(_root.transform, "GoldText", "0G", new Vector2(11.65f, 7.85f), new Vector2(4.9f, 1.9f),
                TextAlignmentOptions.Center, 5f, 23);
            _goldText.color = GoldTextColor;

            // Reroll bar under the X: icon on the art's left, the cost written in its empty right half.
            _rerollUsesArt = rerollArt != null;
            _rerollOutline = MakeRect(_root.transform, "RerollOutline", null, new Vector2(13.3f, 4.7f), new Vector2(6.0f, 3.0f), OutlineTint, 21);
            _rerollOutline.enabled = false;
            _rerollBg = MakeRect(_root.transform, "RerollBg", rerollArt, new Vector2(13.3f, 4.7f), new Vector2(5.6f, 2.6f),
                _rerollUsesArt ? Color.white : ButtonTint, 22);
            _rerollCost = MakeText(_root.transform, "RerollCost", "0G", new Vector2(14.0f, 4.65f), new Vector2(3.6f, 1.9f),
                TextAlignmentOptions.Center, 5f, 23);

            // The three offer cards: cell-sheet faces the controller feeds in, price under each.
            _slots = new Slot[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                GameObject group = new GameObject($"Slot{i}");
                group.transform.SetParent(_root.transform, false);
                group.transform.localPosition = new Vector3(-8.5f + 8.5f * i, -1.2f, 0f);

                Slot slot = new Slot();
                slot.Outline = MakeRect(group.transform, "Outline", null, Vector2.zero, new Vector2(5.5f, 5.5f), OutlineTint, 21);
                slot.Outline.enabled = false;
                slot.Face = MakeRect(group.transform, "Face", null, Vector2.zero, new Vector2(5f, 5f), Color.white, 22);
                slot.Face.enabled = false;
                slot.Overlay = MakeText(group.transform, "Overlay", "", Vector2.zero, new Vector2(4.6f, 4.6f),
                    TextAlignmentOptions.Center, 5f, 23);
                slot.Overlay.gameObject.SetActive(false);
                slot.Price = MakeText(group.transform, "Price", "", new Vector2(0f, -3.4f), new Vector2(4.6f, 1.3f),
                    TextAlignmentOptions.Center, 5f, 23);
                _slots[i] = slot;
            }

            _root.SetActive(false);
        }

        public void Show()
        {
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
            }
        }

        public void Hide()
        {
            if (_root.activeSelf)
            {
                _root.SetActive(false);
            }

            _hoveredSlot = -1;
        }

        public void SetGold(int gold)
        {
            SetText(_goldText, gold + "G");
        }

        public void SetReroll(int price, bool affordable)
        {
            _rerollAffordable = affordable;
            SetText(_rerollCost, price + "G");
            _rerollCost.color = affordable ? GoldTextColor : DeniedTextColor;
            _rerollBg.color = _rerollUsesArt
                ? (affordable ? Color.white : BarDimmed)
                : (affordable ? ButtonTint : ButtonTint * BarDimmed);
        }

        /// <summary>An offer on display: a cell face (or the plain frame with an overlay line for the
        /// battle-stone deal) and its price, dimmed while it cannot be paid for.</summary>
        public void SetSlot(int index, Sprite face, string overlay, int price, bool affordable)
        {
            Slot slot = _slots[index];
            slot.Sold = false;
            slot.Affordable = affordable;

            if (face != null)
            {
                if (slot.FittedSprite != face)
                {
                    Fit(slot.Face, Vector2.zero, new Vector2(5f, 5f), face);
                    slot.FittedSprite = face;
                }

                slot.Face.enabled = true;
                slot.Face.color = affordable ? Color.white : FaceDimmed;
            }
            else
            {
                slot.Face.enabled = false;
            }

            bool hasOverlay = !string.IsNullOrEmpty(overlay);
            if (slot.Overlay.gameObject.activeSelf != hasOverlay)
            {
                slot.Overlay.gameObject.SetActive(hasOverlay);
            }

            if (hasOverlay)
            {
                SetText(slot.Overlay, overlay);
            }

            if (!slot.Price.gameObject.activeSelf)
            {
                slot.Price.gameObject.SetActive(true);
            }

            SetText(slot.Price, price + "G");
            slot.Price.color = affordable ? GoldTextColor : DeniedTextColor;
        }

        /// <summary>A bought-out slot: nothing on the table until a reroll.</summary>
        public void SetSlotSold(int index)
        {
            Slot slot = _slots[index];
            slot.Sold = true;
            slot.Face.enabled = false;
            slot.Outline.enabled = false;
            if (slot.Overlay.gameObject.activeSelf)
            {
                slot.Overlay.gameObject.SetActive(false);
            }

            if (slot.Price.gameObject.activeSelf)
            {
                slot.Price.gameObject.SetActive(false);
            }
        }

        // Hover and clicks. World is the cursor in world space; pointerBlocked marks it sitting on the
        // debug panel or the HUD strips so the panel neither hovers nor clicks under them.
        public void Tick(Vector2 world, bool clickDown, bool pointerBlocked)
        {
            _hoveredSlot = -1;
            if (!_root.activeSelf)
            {
                return;
            }

            bool interact = !pointerBlocked;
            bool hoverClose = interact && Contains(_closeBg, world);
            bool hoverReroll = interact && _rerollAffordable && Contains(_rerollBg, world);
            _closeOutline.enabled = hoverClose;
            _rerollOutline.enabled = hoverReroll;

            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                bool hover = interact && !slot.Sold && Contains(slot.Outline, world);
                slot.Outline.enabled = hover && slot.Affordable;
                if (hover)
                {
                    _hoveredSlot = i; // tooltip shows for unaffordable offers too — the info still helps
                }
            }

            if (!clickDown || !interact)
            {
                return;
            }

            if (hoverClose)
            {
                CloseClicked();
                return;
            }

            if (hoverReroll)
            {
                RerollClicked();
                return;
            }

            if (_hoveredSlot >= 0)
            {
                SlotClicked(_hoveredSlot); // the controller's buy guards affordability again
            }
        }

        private static void SetText(TextMeshPro tmp, string value)
        {
            if (tmp.text != value)
            {
                tmp.text = value;
            }
        }

        private static bool Contains(SpriteRenderer sr, Vector2 world)
        {
            Bounds b = sr.bounds;
            return world.x >= b.min.x && world.x <= b.max.x && world.y >= b.min.y && world.y <= b.max.y;
        }

        // Fits a sprite to the rectangle, centring by bounds — the Aseprite imports carry bottom pivots,
        // so the pivot cannot be trusted for placement (same treatment as the board cells).
        private static void Fit(SpriteRenderer sr, Vector2 pos, Vector2 size, Sprite sprite)
        {
            sr.sprite = sprite;
            Bounds b = sprite.bounds;
            Vector3 scale = new Vector3(size.x / b.size.x, size.y / b.size.y, 1f);
            sr.transform.localScale = scale;
            sr.transform.localPosition = new Vector3(pos.x - b.center.x * scale.x, pos.y - b.center.y * scale.y, 0f);
        }

        private static SpriteRenderer MakeRect(Transform parent, string name, Sprite art, Vector2 pos, Vector2 size, Color tint, int order)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = order;
            sr.color = tint;
            Fit(sr, pos, size, art != null ? art : ProceduralSprites.Unit());
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
