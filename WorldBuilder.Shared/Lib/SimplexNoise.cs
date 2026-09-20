using System;
using System.Runtime.CompilerServices;

namespace WorldBuilder.Shared.Lib {
    /// <summary>
    /// 2D Simplex noise with seeded permutation table and Fractal Brownian Motion.
    /// Based on Stefan Gustavson's simplex noise implementation.
    /// </summary>
    public sealed class SimplexNoise {
        private const double F2 = 0.3660254037844386;  // (sqrt(3) - 1) / 2
        private const double G2 = 0.21132486540518713; // (3 - sqrt(3)) / 6

        private static readonly int[][] Grad3 = {
            new[]{1,1}, new[]{-1,1}, new[]{1,-1}, new[]{-1,-1},
            new[]{1,0}, new[]{-1,0}, new[]{0,1}, new[]{0,-1},
            new[]{1,1}, new[]{-1,1}, new[]{1,-1}, new[]{-1,-1}
        };

        private readonly byte[] _perm = new byte[512];
        private readonly byte[] _permMod12 = new byte[512];

        public SimplexNoise(int seed) {
            var p = new byte[256];
            for (int i = 0; i < 256; i++) p[i] = (byte)i;

            var rng = new Random(seed);
            for (int i = 255; i > 0; i--) {
                int j = rng.Next(i + 1);
                (p[i], p[j]) = (p[j], p[i]);
            }

            for (int i = 0; i < 512; i++) {
                _perm[i] = p[i & 255];
                _permMod12[i] = (byte)(_perm[i] % 12);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Dot(int[] g, double x, double y) => g[0] * x + g[1] * y;

        /// <summary>Single-octave 2D simplex noise. Returns value in [-1, 1].</summary>
        public float Noise2D(float xf, float yf) {
            double x = xf, y = yf;
            double s = (x + y) * F2;
            int i = FastFloor(x + s);
            int j = FastFloor(y + s);

            double t = (i + j) * G2;
            double x0 = x - (i - t);
            double y0 = y - (j - t);

            int i1, j1;
            if (x0 > y0) { i1 = 1; j1 = 0; }
            else { i1 = 0; j1 = 1; }

            double x1 = x0 - i1 + G2;
            double y1 = y0 - j1 + G2;
            double x2 = x0 - 1.0 + 2.0 * G2;
            double y2 = y0 - 1.0 + 2.0 * G2;

            int ii = i & 255;
            int jj = j & 255;

            double n0 = 0, n1 = 0, n2 = 0;

            double t0 = 0.5 - x0 * x0 - y0 * y0;
            if (t0 >= 0) {
                t0 *= t0;
                int gi0 = _permMod12[ii + _perm[jj]];
                n0 = t0 * t0 * Dot(Grad3[gi0], x0, y0);
            }

            double t1 = 0.5 - x1 * x1 - y1 * y1;
            if (t1 >= 0) {
                t1 *= t1;
                int gi1 = _permMod12[ii + i1 + _perm[jj + j1]];
                n1 = t1 * t1 * Dot(Grad3[gi1], x1, y1);
            }

            double t2 = 0.5 - x2 * x2 - y2 * y2;
            if (t2 >= 0) {
                t2 *= t2;
                int gi2 = _permMod12[ii + 1 + _perm[jj + 1]];
                n2 = t2 * t2 * Dot(Grad3[gi2], x2, y2);
            }

            return (float)(70.0 * (n0 + n1 + n2));
        }

        /// <summary>
        /// Fractal Brownian Motion: layers multiple octaves of noise for natural-looking terrain.
        /// Returns value roughly in [-1, 1] (can slightly exceed with many octaves).
        /// </summary>
        public float FBM(float x, float y, int octaves, float persistence = 0.5f, float lacunarity = 2.0f) {
            float total = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float maxAmplitude = 0f;

            for (int i = 0; i < octaves; i++) {
                total += Noise2D(x * frequency, y * frequency) * amplitude;
                maxAmplitude += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            return total / maxAmplitude;
        }

        /// <summary>Ridged noise variant for mountain ranges. Returns [0, 1].</summary>
        public float RidgedNoise(float x, float y, int octaves, float persistence = 0.5f, float lacunarity = 2.0f) {
            float total = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float maxAmplitude = 0f;

            for (int i = 0; i < octaves; i++) {
                float n = 1f - MathF.Abs(Noise2D(x * frequency, y * frequency));
                total += n * n * amplitude;
                maxAmplitude += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            return total / maxAmplitude;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastFloor(double x) {
            int xi = (int)x;
            return x < xi ? xi - 1 : xi;
        }
    }
}
