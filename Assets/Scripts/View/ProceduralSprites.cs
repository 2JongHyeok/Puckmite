using UnityEngine;

namespace Puckmite.View
{
    /// <summary>
    /// Builds the sprites the playtest needs at runtime, so no art is imported (design doc 7.9). One
    /// 1x1 white sprite is scaled by transforms into every rectangle (board, grid, walls, aim line);
    /// a soft-edged white circle is the puck. Both are cached after first use.
    /// </summary>
    public static class ProceduralSprites
    {
        private static Sprite _unit;
        private static Sprite _circle;

        /// <summary>A 1x1 white sprite whose size is exactly one world unit (pixelsPerUnit = 1), so a
        /// transform localScale of (w, h) draws a w-by-h rectangle.</summary>
        public static Sprite Unit()
        {
            if (_unit != null)
            {
                return _unit;
            }

            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();

            _unit = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            return _unit;
        }

        /// <summary>A white filled circle, one world unit in diameter, with a one-pixel soft edge so it
        /// is not jagged. Scale the transform by the desired diameter to size it.</summary>
        public static Sprite Circle(int resolution = 128)
        {
            if (_circle != null)
            {
                return _circle;
            }

            Texture2D tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            float center = (resolution - 1) * 0.5f;
            float radius = resolution * 0.5f - 1f;
            Color[] pixels = new Color[resolution * resolution];
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(radius - distance); // 1 inside, ramps to 0 across the last pixel
                    pixels[y * resolution + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            // pixelsPerUnit = circle diameter in pixels => the drawn circle is exactly one world unit.
            _circle = Sprite.Create(tex, new Rect(0f, 0f, resolution, resolution), new Vector2(0.5f, 0.5f), 2f * radius);
            return _circle;
        }
    }
}
