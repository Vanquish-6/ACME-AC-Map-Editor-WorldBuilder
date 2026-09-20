using System.Numerics;
using System.Runtime.CompilerServices;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Tests {
    /// <summary>
    /// Faithful C# port of the original AC client terrain algorithms,
    /// translated from decompiled C++ in acclient-source-split/CLandBlockStruct.cpp.
    /// Serves as the ground-truth oracle for conformance testing.
    /// All formulas use signed int arithmetic with unchecked wrapping to match x86 behavior.
    /// </summary>
    public static class ClientReference {
        /// <summary>
        /// Port of the split direction logic from CLandBlockStruct::ConstructPolygons.
        /// Source: CLandBlockStruct.cpp, ConstructPolygons at offset 0x00531D10.
        /// <code>
        /// v7 = (v5 + block_y) * (214614067 * v6 + 1813693831) - 1109124029 * v6 - 1369149221;
        /// if ( (double)(unsigned int)v7 * 2.3283064e-10 >= 0.5 )
        ///     SWtoNEcut = 1;  // diagonal from SW to NE
        /// else
        ///     SWtoNEcut = 0;  // diagonal from SE to NW
        /// </code>
        /// Where v6 = globalCellX (row + landblockX*8) and (v5 + block_y) = globalCellY (col + landblockY*8).
        /// Returns true when the triangle split goes from SW to NE (SWtoNEcut=1).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSWtoNECut(int globalCellX, int globalCellY) {
            unchecked {
                int v7 = globalCellY * (214614067 * globalCellX + 1813693831)
                       - 1109124029 * globalCellX - 1369149221;
                return (double)(uint)v7 * 2.3283064e-10 >= 0.5;
            }
        }

        /// <summary>
        /// Port of pal_code[0] from CLandBlockStruct::GetCellRotation.
        /// Source: CLandBlockStruct.cpp, GetCellRotation at offset 0x00532170.
        /// <code>
        /// pal_code[0] = t3 + (tex_size &lt;&lt; 28)
        ///     + 32 * (t2 + 32 * (t1 + 32 * (t0 + 32 * (r3 + 4 * (r2 + 4 * (r1 + 4 * r0))))));
        /// </code>
        /// Corner order: 0=(ix,iy), 1=(ix+1,iy), 2=(ix+1,iy+1), 3=(ix,iy+1)
        /// </summary>
        public static uint GetPalCode(
            int r0, int t0,
            int r1, int t1,
            int r2, int t2,
            int r3, int t3,
            int texSize = 1) {
            return TerrainPalCode.PackPalCode(r0, t0, r1, t1, r2, t2, r3, t3, (uint)(texSize << 28));
        }

        /// <summary>
        /// Port of CLandBlockStruct::ConstructVertices vertex height lookup.
        /// Source: CLandBlockStruct.cpp, ConstructVertices at offset 0x005328D0.
        /// Height = LandDefs::Land_Height_Table[height_byte]
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetVertexHeight(float[] landHeightTable, byte heightByte) {
            return landHeightTable[heightByte];
        }

        /// <summary>
        /// Port of CLandBlockStruct::ConstructVertices vertex position.
        /// Source: CLandBlockStruct.cpp, ConstructVertices at offset 0x005328D0.
        /// Each vertex is at (ix * polySize, iy * polySize, height) where polySize = 192 / side_polygon_count.
        /// For full-resolution (side_polygon_count=8): polySize = 24.
        /// </summary>
        public static Vector3 GetVertexPosition(float[] landHeightTable, byte heightByte, int ix, int iy, float polySize = 24f) {
            return new Vector3(ix * polySize, iy * polySize, landHeightTable[heightByte]);
        }

        /// <summary>
        /// Bit layout constants from LandDefs::get_vars.
        /// Source: LandDefs.cpp at offset 0x005A9980.
        /// </summary>
        public const int MapWidth = 255;
        public const int MapHeight = 255;
        public const float CellSize = 24.0f;
        public const int CellsPerBlock = 8;
        public const float RoadWidth = 5.0f;
        public const float BlockLength = 192.0f; // 8 * 24
    }
}
