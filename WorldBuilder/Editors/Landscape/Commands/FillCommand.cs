using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.Commands {
    public class FillCommand : TerrainVertexChangeCommand {
        private readonly TerrainRaycast.TerrainRaycastHit _hitResult;
        private readonly byte _newType;

        public FillCommand(TerrainEditingContext context, TerrainRaycast.TerrainRaycastHit hitResult, TerrainTextureType newType) : base(context) {
            _hitResult = hitResult;
            _newType = (byte)newType;
            CollectChanges();
        }

        public override string Description => $"Bucket fill with {Enum.GetName(typeof(TerrainTextureType), _newType)}";
        public override TerrainField Field => TerrainField.Type;

        protected override byte GetEntryValue(TerrainEntry entry) => entry.Type;
        protected override TerrainEntry SetEntryValue(TerrainEntry entry, byte value) => entry with { Type = value };

        private void CollectChanges() {
            var vertices = FloodFillVertices(_context.TerrainSystem, _hitResult, _newType, null);
            foreach (var (lbID, index, oldType) in vertices) {
                if (!_changes.TryGetValue(lbID, out var list)) {
                    list = new List<(int, byte, byte)>();
                    _changes[lbID] = list;
                }
                list.Add((index, oldType, _newType));
            }
        }

        /// <summary>
        /// Performs a read-only flood fill from the hit vertex, collecting all contiguous
        /// vertices with the same texture type. Returns a list of (landblockId, vertexIndex, oldType).
        /// Can be used for both preview highlighting and actual command execution.
        ///
        /// Uses a two-level approach: landblocks where every vertex matches are processed in
        /// bulk (81 vertices at once, propagate to neighbors) while mixed landblocks fall back
        /// to per-vertex BFS. This keeps a full-world fill (256x256 uniform landblocks) fast
        /// by reducing ~5.3M individual vertex operations to ~65K landblock checks.
        /// </summary>
        /// <param name="allowedLandblocks">
        /// Optional set of landblock keys to constrain the fill to (e.g. visible/loaded landblocks).
        /// When null the fill is unbounded. When provided the flood will not cross into landblocks
        /// outside this set, keeping the operation scoped to the camera view.
        /// </param>
        public static List<(ushort LbID, int VertexIndex, byte OldType)> FloodFillVertices(
            TerrainSystem terrainSystem,
            TerrainRaycast.TerrainRaycastHit hitResult,
            byte newType,
            HashSet<ushort>? allowedLandblocks = null) {

            var result = new List<(ushort, int, byte)>();

            uint startLbX = hitResult.LandblockX;
            uint startLbY = hitResult.LandblockY;
            uint startCellX = (uint)hitResult.CellX;
            uint startCellY = (uint)hitResult.CellY;
            ushort startLbID = (ushort)((startLbX << 8) | startLbY);

            if (allowedLandblocks != null && !allowedLandblocks.Contains(startLbID))
                return result;

            var startData = terrainSystem.GetLandblockTerrain(startLbID);
            if (startData == null) return result;

            int startIndex = (int)(startCellX * 9 + startCellY);
            if (startIndex >= startData.Length) return result;

            byte oldType = startData[startIndex].Type;
            if (oldType == newType) return result;

            var landblockDataCache = new Dictionary<ushort, TerrainEntry[]>();
            landblockDataCache[startLbID] = startData;

            // Fully-processed uniform landblocks (all 81 vertices matched and were added)
            var fullyProcessed = new HashSet<ushort>();
            // Per-vertex visited tracking only for mixed landblocks (bool[81] per lb)
            var vertexVisited = new Dictionary<ushort, bool[]>();

            var queue = new Queue<(uint lbX, uint lbY, uint cellX, uint cellY)>();
            queue.Enqueue((startLbX, startLbY, startCellX, startCellY));

            while (queue.Count > 0) {
                var (lbX, lbY, cellX, cellY) = queue.Dequeue();
                var lbID = (ushort)((lbX << 8) | lbY);

                if (fullyProcessed.Contains(lbID)) continue;
                if (allowedLandblocks != null && !allowedLandblocks.Contains(lbID)) continue;

                if (!landblockDataCache.TryGetValue(lbID, out var data)) {
                    data = terrainSystem.GetLandblockTerrain(lbID);
                    if (data == null) continue;
                    landblockDataCache[lbID] = data;
                }

                // First time seeing this landblock: check if every vertex matches oldType
                if (!vertexVisited.ContainsKey(lbID)) {
                    bool uniform = true;
                    for (int i = 0; i < data.Length; i++) {
                        if (data[i].Type != oldType) { uniform = false; break; }
                    }

                    if (uniform) {
                        fullyProcessed.Add(lbID);
                        for (int i = 0; i < data.Length; i++)
                            result.Add((lbID, i, oldType));

                        // Propagate to neighboring landblocks via their shared border edges.
                        // Queue all 9 border vertices per edge so mixed neighbors get
                        // seeded correctly; uniform neighbors fast-path on the first vertex
                        // and skip the rest via the fullyProcessed check.
                        if (lbX > 0 && !fullyProcessed.Contains((ushort)(((lbX - 1) << 8) | lbY)))
                            for (uint cy = 0; cy <= 8; cy++)
                                queue.Enqueue((lbX - 1, lbY, 8, cy));
                        if (lbX < 255 && !fullyProcessed.Contains((ushort)(((lbX + 1) << 8) | lbY)))
                            for (uint cy = 0; cy <= 8; cy++)
                                queue.Enqueue((lbX + 1, lbY, 0, cy));
                        if (lbY > 0 && !fullyProcessed.Contains((ushort)((lbX << 8) | (lbY - 1))))
                            for (uint cx = 0; cx <= 8; cx++)
                                queue.Enqueue((lbX, lbY - 1, cx, 8));
                        if (lbY < 255 && !fullyProcessed.Contains((ushort)((lbX << 8) | (lbY + 1))))
                            for (uint cx = 0; cx <= 8; cx++)
                                queue.Enqueue((lbX, lbY + 1, cx, 0));
                        continue;
                    }

                    vertexVisited[lbID] = new bool[81];
                }

                // Mixed landblock: per-vertex BFS
                var visited = vertexVisited[lbID];
                int index = (int)(cellX * 9 + cellY);
                if (index >= data.Length || visited[index]) continue;
                visited[index] = true;

                if (data[index].Type != oldType) continue;

                result.Add((lbID, index, oldType));

                if (cellX > 0) queue.Enqueue((lbX, lbY, cellX - 1, cellY));
                else if (lbX > 0) queue.Enqueue((lbX - 1, lbY, 8, cellY));

                if (cellX < 8) queue.Enqueue((lbX, lbY, cellX + 1, cellY));
                else if (lbX < 255) queue.Enqueue((lbX + 1, lbY, 0, cellY));

                if (cellY > 0) queue.Enqueue((lbX, lbY, cellX, cellY - 1));
                else if (lbY > 0) queue.Enqueue((lbX, lbY - 1, cellX, 8));

                if (cellY < 8) queue.Enqueue((lbX, lbY, cellX, cellY + 1));
                else if (lbY < 255) queue.Enqueue((lbX, lbY + 1, cellX, 0));
            }

            return result;
        }
    }
}
