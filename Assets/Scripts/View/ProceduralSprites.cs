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
        private static Texture2D _dash;
        private static Sprite _dashedRing;
        private static Sprite _arrow;

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

        /// <summary>A dash tile for LineRenderer Tile mode: left half opaque, right half transparent, so
        /// each repetition along the line draws one dash and one gap. Wrap mode repeats.</summary>
        public static Texture2D DashTexture()
        {
            if (_dash != null)
            {
                return _dash;
            }

            const int width = 32;
            const int height = 8;
            _dash = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
            };

            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    pixels[y * width + x] = x < width / 2 ? Color.white : Color.clear;
                }
            }

            _dash.SetPixels(pixels);
            _dash.Apply();
            return _dash;
        }

        /// <summary>A dashed circle outline, one world unit across like Circle — the "phantom position"
        /// marker. Soft-edged ring with alternating dashes around the rim.</summary>
        public static Sprite DashedRing(int resolution = 128, int dashes = 12)
        {
            if (_dashedRing != null)
            {
                return _dashedRing;
            }

            Texture2D tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            float center = (resolution - 1) * 0.5f;
            float outer = resolution * 0.5f - 1f;
            float inner = outer - resolution * 0.07f; // ring thickness ≈ 7% of the diameter
            Color[] pixels = new Color[resolution * resolution];
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(outer - distance) * Mathf.Clamp01(distance - inner);

                    // Alternate segments around the rim; even = dash, odd = gap.
                    float turn = (Mathf.Atan2(dy, dx) + Mathf.PI) / (2f * Mathf.PI); // 0..1 around
                    if ((int)(turn * dashes * 2f) % 2 == 1)
                    {
                        alpha = 0f;
                    }

                    pixels[y * resolution + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            _dashedRing = Sprite.Create(tex, new Rect(0f, 0f, resolution, resolution), new Vector2(0.5f, 0.5f), 2f * outer);
            return _dashedRing;
        }

        /// <summary>A right-pointing arrow (shaft + triangular head), one world unit long and half as
        /// tall, pivot at the tail — rotate the transform to aim it.</summary>
        public static Sprite Arrow()
        {
            if (_arrow != null)
            {
                return _arrow;
            }

            const int width = 128;
            const int height = 64;
            const int headStart = 76;      // shaft ends, head begins
            const float shaftHalf = 7f;    // shaft half-thickness in pixels
            float mid = (height - 1) * 0.5f;

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float dy = Mathf.Abs(y - mid);
                    float alpha;
                    if (x < headStart)
                    {
                        alpha = Mathf.Clamp01(shaftHalf - dy); // soft-edged shaft
                    }
                    else
                    {
                        // Head tapers linearly from full half-height at its base to a point at the tip.
                        float headHalf = (width - 1f - x) * (mid - 1f) / (width - 1f - headStart);
                        alpha = Mathf.Clamp01(headHalf - dy);
                    }

                    pixels[y * width + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            _arrow = Sprite.Create(tex, new Rect(0f, 0f, width, height), new Vector2(0f, 0.5f), width);
            return _arrow;
        }
    }
}
