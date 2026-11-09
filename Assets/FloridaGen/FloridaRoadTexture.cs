using UnityEngine;

namespace DeadEverywhere.FloridaGeneration
{
    /// <summary>Deterministic, periodic aggregate textures; independent of Unity's gameplay random state.</summary>
    public static class FloridaRoadTexture
    {
        public const int Resolution = 512;

        public static Texture2D CreateAsphalt(bool normalMap = false)
        {
            var texture = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, true, normalMap)
            {
                name = normalMap ? "Florida_Asphalt_Normal" : "Florida_Asphalt_Albedo",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8
            };
            var pixels = new Color[Resolution * Resolution];
            for (int y = 0; y < Resolution; y++)
            for (int x = 0; x < Resolution; x++)
            {
                float height = Aggregate(x, y);
                if (normalMap)
                {
                    float dx = Aggregate(x - 1, y) - Aggregate(x + 1, y);
                    float dy = Aggregate(x, y - 1) - Aggregate(x, y + 1);
                    Vector3 n = new Vector3(dx * 0.65f, dy * 0.65f, 1f).normalized;
                    // RG-or-AG decoding multiplies red by alpha, so leave alpha at one.
                    pixels[y * Resolution + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f,
                                                           n.z * 0.5f + 0.5f, 1f);
                }
                else
                {
                    float value = 0.88f + 0.12f * height;
                    pixels[y * Resolution + x] = new Color(value, value, value, 1f);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            return texture;
        }

        /// <summary>Opaque, lightly worn thermoplastic; UV X spans the stripe, Y repeats every two metres.</summary>
        public static Texture2D CreatePaint()
        {
            const int width = 64, height = 256;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
            {
                name = "Florida_RoadPaint",
                wrapModeU = TextureWrapMode.Clamp,
                wrapModeV = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8
            };
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float edge = Mathf.Min(x, width - 1 - x) / 4f;
                float wear = Hash(x, y) > 0.975f ? 0.14f : 0f;
                float value = 0.92f + 0.08f * Hash(x / 2, y / 2) - wear;
                value *= Mathf.Lerp(0.82f, 1f, Mathf.Clamp01(edge));
                pixels[y * width + x] = new Color(value, value, value, 1f);
            }
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            return texture;
        }

        private static float Aggregate(int x, int y) =>
            0.45f * PeriodicNoise(x / 2f, y / 2f, Resolution / 2) +
            0.20f * PeriodicNoise(x / 16f, y / 16f, Resolution / 16) +
            0.35f * Hash((x % Resolution + Resolution) % Resolution,
                         (y % Resolution + Resolution) % Resolution);

        private static float PeriodicNoise(float x, float y, int period)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float tx = x - ix, ty = y - iy;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            int x0 = (ix % period + period) % period, x1 = (x0 + 1) % period;
            int y0 = (iy % period + period) % period, y1 = (y0 + 1) % period;
            return Mathf.Lerp(Mathf.Lerp(Hash(x0, y0), Hash(x1, y0), tx),
                              Mathf.Lerp(Hash(x0, y1), Hash(x1, y1), tx), ty);
        }

        private static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)y * 668265263u + 1274126177u;
                h = (h ^ (h >> 13)) * 1274126177u;
                return (h ^ (h >> 16)) / (float)uint.MaxValue;
            }
        }
    }
}
