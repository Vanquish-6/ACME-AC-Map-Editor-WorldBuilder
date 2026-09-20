using System.Runtime.CompilerServices;

namespace WorldBuilder.Shared.Lib {
    /// <summary>
    /// Client-faithful terrain palette codes from CLandBlockStruct::GetCellRotation / PalShift::GetBeginRotIx.
    /// </summary>
    public static class TerrainPalCode {
        public const uint InvalidPalCode = 0xFFFFFFFF;

        /// <summary>TexMerge path: 4 for full 8×8 landblocks, 1 when pal-shifted or 1×1 cells.</summary>
        public static uint GetTextureSizeBits(bool isPalShifted, int sideCellCount = 8) {
            uint texSize = isPalShifted || sideCellCount == 1 ? 1u : 4u;
            return texSize << 28;
        }

        /// <summary>
        /// Four rotated palette codes (pal_code[0..3]) for one cell, matching CLandBlockStruct::GetCellRotation.
        /// For PalShift / client LandSurf only — do not pass to LandSurfaceManager TexMerge (use <see cref="GetPalCode"/>).
        /// Corners: c0=(x,y), c1=(x+1,y), c2=(x+1,y+1), c3=(x,y+1) → (r1,t1)..(r4,t4).
        /// </summary>
        public static void GetPalCodesForCell(
            int r1, int t1, int r2, int t2, int r3, int t3, int r4, int t4,
            uint textureSizeBits,
            Span<uint> palCodes) {
            if (palCodes.Length < 4)
                throw new ArgumentException("palCodes span must hold at least 4 entries.", nameof(palCodes));

            // CLandBlockStruct::GetCellRotation — each pal_code[i] permutes val0..val3 corners.
            palCodes[0] = EncodeRotatedPalCode(r4, t3, t2, t1, textureSizeBits);
            palCodes[1] = EncodeRotatedPalCode(r1, t4, t3, t2, textureSizeBits);
            palCodes[2] = EncodeRotatedPalCode(r2, t1, t4, t3, textureSizeBits);
            palCodes[3] = EncodeRotatedPalCode(r3, t4, t2, t1, textureSizeBits);
        }

        /// <summary>One pal_code[i]: (road &amp; 3) + size + 32*tHi + 1024*tMid + 32768*tLo per client.</summary>
        static uint EncodeRotatedPalCode(int road, int tHi, int tMid, int tLo, uint textureSizeBits) {
            unchecked {
                return (uint)(road & 3)
                    + textureSizeBits
                    + 32u * (uint)(tHi & 0x1F)
                    + 32u * 32u * (uint)(tMid & 0x1F)
                    + 32u * 32u * 32u * (uint)(tLo & 0x1F);
            }
        }

        /// <summary>
        /// Primary palette code for corners c0..c3 = (r1,t1)..(r4,t4); matches ClientReference.GetPalCode(r0,t0,…).
        /// </summary>
        public static uint GetPalCode(int r1, int r2, int r3, int r4, int t1, int t2, int t3, int t4, uint textureSizeBits) {
            return PackPalCode(r1, t1, r2, t2, r3, t3, r4, t4, textureSizeBits);
        }

        /// <summary>Legacy helper: tex_size = 1 in bits 28–31.</summary>
        public static uint GetPalCode(int r1, int r2, int r3, int r4, int t1, int t2, int t3, int t4) {
            return GetPalCode(r1, r2, r3, r4, t1, t2, t3, t4, 1u << 28);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackPalCode(
            int r0, int t0, int r1, int t1, int r2, int t2, int r3, int t3,
            uint textureSizeBits) {
            unchecked {
                return textureSizeBits
                    + (uint)(t3
                    + 32 * (t2 + 32 * (t1 + 32 * (t0 + 32 * (r3 + 4 * (r2 + 4 * (r1 + 4 * r0)))))));
            }
        }

        /// <summary>Extract tex_size from palette code (bits 28–31).</summary>
        public static uint GetTextureSizeFromPalCode(uint paletteCode) {
            return (paletteCode >> 28) & 0xFu;
        }

        /// <summary>
        /// Port of PalShift__GetBeginRotIx — picks which pal_code[0..3] drives surface build / UV rotation.
        /// </summary>
        public static int GetBeginRotIndex(int globalCellX, int globalCellY, ReadOnlySpan<uint> palCodes, bool minimizePal) {
            if (palCodes.Length < 4)
                throw new ArgumentException("palCodes span must hold at least 4 entries.", nameof(palCodes));

            if (minimizePal) {
                uint best = palCodes[0];
                int index = 0;
                if (palCodes[1] < best) { best = palCodes[1]; index = 1; }
                if (palCodes[2] < best) { best = palCodes[2]; index = 2; }
                if (palCodes[3] < best) index = 3;
                return index;
            }

            unchecked {
                int v = 1813693831 * globalCellY
                      - globalCellX * (501661475 * globalCellY + 1109124029)
                      + 1225298869;
                return (int)((double)(uint)v * 2.3283064e-10 * 4.0);
            }
        }
    }
}
