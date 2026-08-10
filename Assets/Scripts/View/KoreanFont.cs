using TMPro;
using UnityEngine;

namespace Puckmite.View
{
    /// <summary>
    /// The bundled Korean font (Noto Sans KR under Assets/Resources/Fonts, SIL OFL) for the whole view,
    /// because the default fonts have no Korean glyphs and WebGL builds cannot reach OS fonts.
    /// Asset() is the TMP face; LegacyFont() is the raw Font for IMGUI styles, which cannot take TMP.
    /// </summary>
    public static class KoreanFont
    {
        private const string FontResourcePath = "Fonts/NotoSansKR-Regular";

        private static Font _font;
        private static TMP_FontAsset _asset;

        public static Font LegacyFont()
        {
            if (_font == null)
            {
                _font = Resources.Load<Font>(FontResourcePath);
                if (_font == null)
                {
                    Debug.LogError($"[PuckHero] Bundled Korean font missing at 'Resources/{FontResourcePath}' — Korean text will show boxes.");
                }
            }

            return _font;
        }

        public static TMP_FontAsset Asset()
        {
            if (_asset == null)
            {
                Font source = LegacyFont();
                _asset = source != null ? TMP_FontAsset.CreateFontAsset(source) : null;
                if (_asset == null && source != null)
                {
                    Debug.LogError("[PuckHero] Could not build a TMP font from the bundled Korean font — Korean text will show boxes.");
                }
            }

            return _asset;
        }
    }
}
