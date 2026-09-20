using System;
using System.Runtime.CompilerServices;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Shared.Lib {
    /// <summary>
    /// AC-accurate terrain height sampling extracted from TerrainDataManager
    /// so it can be used from both the editor and the export pipeline.
    /// </summary>
    public static class TerrainHeightSampler {
        public const uint LandblockLength = 192;
        public const uint LandblockEdgeCellCount = 8;
        public const float CellSize = 24.0f;

        /// <summary>
        /// Gets the interpolated terrain height at a landblock-local position.
        /// localX/localY are in [0, 192] within the landblock.
        /// </summary>
        public static float SampleHeightTriangle(TerrainEntry[] data, float[] heightTable,
            float localX, float localY, uint landblockX, uint landblockY) {

            float cellX = localX / CellSize;
            float cellY = localY / CellSize;

            uint cellIndexX = Math.Min((uint)Math.Floor(cellX), LandblockEdgeCellCount - 1);
            uint cellIndexY = Math.Min((uint)Math.Floor(cellY), LandblockEdgeCellCount - 1);

            float fracX = cellX - cellIndexX;
            float fracY = cellY - cellIndexY;

            float hSW = GetHeightFromData(data, heightTable, cellIndexX, cellIndexY);
            float hSE = GetHeightFromData(data, heightTable, cellIndexX + 1, cellIndexY);
            float hNW = GetHeightFromData(data, heightTable, cellIndexX, cellIndexY + 1);
            float hNE = GetHeightFromData(data, heightTable, cellIndexX + 1, cellIndexY + 1);

            uint globalCellX = landblockX * LandblockEdgeCellCount + cellIndexX;
            uint globalCellY = landblockY * LandblockEdgeCellCount + cellIndexY;
            bool isSWtoNE = IsSWtoNEcut(globalCellX, globalCellY);

            if (isSWtoNE) {
                if (fracX > fracY) {
                    return hSW + fracX * (hSE - hSW) + fracY * (hNE - hSE);
                }
                else {
                    return hSW + fracX * (hNE - hNW) + fracY * (hNW - hSW);
                }
            }
            else {
                if (fracX + fracY <= 1.0f) {
                    return hSW + fracX * (hSE - hSW) + fracY * (hNW - hSW);
                }
                else {
                    return hNE + (1.0f - fracX) * (hNW - hNE) + (1.0f - fracY) * (hSE - hNE);
                }
            }
        }

        /// <summary>
        /// Determines the triangle split direction for a terrain cell, matching the
        /// AC client's pseudo-random algorithm from LandblockStruct.ConstructPolygons.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSWtoNEcut(uint globalCellX, uint globalCellY) {
            uint magicA = (uint)unchecked((int)globalCellX * 214614067 + 1813693831);
            uint magicB = (uint)unchecked((int)globalCellX * 1109124029);
            uint splitDir = unchecked((uint)((int)globalCellY * (int)magicA - (int)magicB - 1369149221));
            return splitDir * 2.3283064e-10 >= 0.5;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetHeightFromData(TerrainEntry[] data, float[] heightTable, uint vx, uint vy) {
            vx = Math.Min(vx, 8);
            vy = Math.Min(vy, 8);
            var idx = (int)(vx * 9 + vy);
            return idx < data.Length ? heightTable[data[idx].Height] : 0f;
        }
    }
}
