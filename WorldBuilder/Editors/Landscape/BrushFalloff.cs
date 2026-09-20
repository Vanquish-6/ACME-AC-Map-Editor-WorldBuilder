using System;

namespace WorldBuilder.Editors.Landscape {
    /// <summary>
    /// Maps distance-from-center to a 0–1 brush weight.
    /// Falloff 0 is a hard disk; 1 is a cosine fade from the center to the edge.
    /// </summary>
    public static class BrushFalloff {
        public static float Weight(float distance, float radius, float falloff) {
            if (radius < 0.001f) return 1f;
            float t = distance / radius;
            if (t >= 1f) return 0f;
            falloff = Math.Clamp(falloff, 0f, 1f);
            if (falloff <= 0.001f) return 1f;
            float inner = 1f - falloff;
            if (t <= inner) return 1f;
            float u = (t - inner) / Math.Max(1e-4f, 1f - inner);
            return 0.5f * (1f + MathF.Cos(u * MathF.PI));
        }

        public static float WorldRadius(float brushRadiusSetting) => (brushRadiusSetting * 12f) + 1f;
    }
}
