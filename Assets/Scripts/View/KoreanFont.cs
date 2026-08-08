using TMPro;
using UnityEngine;

namespace Puckmite.View
{
    /// <summary>
    /// One dynamic Korean-capable TMP font for the whole view, built from the OS font (Malgun Gothic,
    /// on every Windows) because the bundled TMP font has no Korean glyphs. Swap for a bundled font
    /// asset when the design picks one.
    /// </summary>
    public static class KoreanFont
    {
        private static TMP_FontAsset _asset;

        public static TMP_FontAsset Asset()
        {
            if (_asset == null)
            {
                _asset = TMP_FontAsset.CreateFontAsset("Malgun Gothic", "Regular");
                if (_asset == null)
                {
                    Debug.LogError("[PuckHero] Could not build a Korean font from 'Malgun Gothic' — Korean text will show boxes.");
                }
            }

            return _asset;
        }
    }
}
