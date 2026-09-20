using Acme.Render;
using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Landscape {
    /// <summary>
    /// Manages terrain chunk metadata and streaming logic
    /// </summary>
    public class TerrainDataManager {
        public const uint MapSize = 254;
        public const uint LandblockLength = 192;
        public const uint LandblockEdgeCellCount = 8;
        public const float CellSize = 24.0f;

        private readonly TerrainSystem _terrainSystem;
        private readonly uint _chunkSizeInLandblocks;
        private readonly ChunkMetrics _metrics;
        private readonly Dictionary<ulong, TerrainChunk> _chunks = new();

        public TerrainDocument Terrain => _terrainSystem.TerrainDoc;
        private Region _region => _terrainSystem.Region;
        public uint ChunkSize => _chunkSizeInLandblocks;
        public ChunkMetrics Metrics => _metrics;

        public TerrainDataManager(TerrainSystem terrain, uint chunkSizeInLandblocks = 16) {
            _terrainSystem = terrain ?? throw new ArgumentNullException(nameof(terrain));
            _chunkSizeInLandblocks = Math.Max(1, chunkSizeInLandblocks);
            _metrics = ChunkMetrics.Calculate(_chunkSizeInLandblocks);
        }

        /// <summary>
        /// How many chunks to load around the camera in each direction.
        /// With 16 landblocks/chunk at 192 units each, range 3 = ~9,216 units visibility.
        /// </summary>
        public uint LoadRange { get; set; } = 3;

        /// <summary>
        /// Chunks beyond this range (in chunk units) are eligible for unloading.
        /// Should be >= LoadRange + 1 to avoid thrashing.
        /// </summary>
        public uint UnloadRange { get; set; } = 5;

        /// <summary>
        /// Determines which chunks should be loaded based on camera position.
        /// Only loads chunks within LoadRange of the camera, not the entire map.
        /// </summary>
        public List<ulong> GetRequiredChunks(Vector3 cameraPosition) {
            var chunks = new List<ulong>();

            var chunkX = (uint)Math.Max(0, Math.Min(MapSize / _chunkSizeInLandblocks - 1, cameraPosition.X / _metrics.WorldSize));
            var chunkY = (uint)Math.Max(0, Math.Min(MapSize / _chunkSizeInLandblocks - 1, cameraPosition.Y / _metrics.WorldSize));

            var maxChunksX = (MapSize + _chunkSizeInLandblocks - 1) / _chunkSizeInLandblocks;
            var maxChunksY = (MapSize + _chunkSizeInLandblocks - 1) / _chunkSizeInLandblocks;

            var minX = (uint)Math.Max(0, (int)chunkX - LoadRange);
            var maxX = Math.Min(maxChunksX - 1, chunkX + LoadRange);
            var minY = (uint)Math.Max(0, (int)chunkY - LoadRange);
            var maxY = Math.Min(maxChunksY - 1, chunkY + LoadRange);

            for (uint y = minY; y <= maxY; y++) {
                for (uint x = minX; x <= maxX; x++) {
                    chunks.Add(GetChunkId(x, y));
                }
            }

            return chunks;
        }

        /// <summary>
        /// Returns chunk IDs that are loaded but beyond UnloadRange from the camera.
        /// </summary>
        public List<ulong> GetChunksToUnload(Vector3 cameraPosition) {
            var toUnload = new List<ulong>();
            var camChunkX = (int)(cameraPosition.X / _metrics.WorldSize);
            var camChunkY = (int)(cameraPosition.Y / _metrics.WorldSize);

            foreach (var (id, chunk) in _chunks) {
                int dx = Math.Abs((int)chunk.ChunkX - camChunkX);
                int dy = Math.Abs((int)chunk.ChunkY - camChunkY);
                if (dx > UnloadRange || dy > UnloadRange) {
                    toUnload.Add(id);
                }
            }

            return toUnload;
        }

        /// <summary>
        /// Removes a chunk from the data manager.
        /// </summary>
        public bool RemoveChunk(ulong chunkId) {
            return _chunks.Remove(chunkId);
        }

        /// <summary>
        /// Removes all chunks from the data manager so they will be re-created on demand.
        /// </summary>
        public void ClearChunks() {
            _chunks.Clear();
        }

        /// <summary>
        /// Gets or creates chunk metadata
        /// </summary>
        public TerrainChunk GetOrCreateChunk(uint chunkX, uint chunkY) {
            var chunkId = GetChunkId(chunkX, chunkY);

            if (_chunks.TryGetValue(chunkId, out var chunk)) {
                return chunk;
            }

            chunk = CreateChunk(chunkX, chunkY);
            _chunks[chunkId] = chunk;
            return chunk;
        }

        private TerrainChunk CreateChunk(uint chunkX, uint chunkY) {
            var chunk = new TerrainChunk {
                ChunkX = chunkX,
                ChunkY = chunkY,
                LandblockStartX = chunkX * _chunkSizeInLandblocks,
                LandblockStartY = chunkY * _chunkSizeInLandblocks
            };

            // Calculate actual dimensions (handle map edges)
            chunk.ActualLandblockCountX = Math.Min(_chunkSizeInLandblocks, MapSize - chunk.LandblockStartX);
            chunk.ActualLandblockCountY = Math.Min(_chunkSizeInLandblocks, MapSize - chunk.LandblockStartY);

            // Calculate bounding box
            chunk.Bounds = CalculateChunkBounds(chunk);
            chunk.IsGenerated = true;

            return chunk;
        }

        private BoundingBox CalculateChunkBounds(TerrainChunk chunk) {
            // Use current implementation from GenerateChunk
            var minHeight = float.MaxValue;
            var maxHeight = float.MinValue;

            for (uint ly = 0; ly < chunk.ActualLandblockCountY; ly++) {
                for (uint lx = 0; lx < chunk.ActualLandblockCountX; lx++) {
                    var landblockX = chunk.LandblockStartX + lx;
                    var landblockY = chunk.LandblockStartY + ly;

                    if (landblockX >= MapSize || landblockY >= MapSize) continue;

                    var landblockID = landblockX << 8 | landblockY;
                    var landblockData = _terrainSystem.GetLandblockTerrain((ushort)landblockID);

                    if (landblockData != null) {
                        for (int i = 0; i < landblockData.Length; i++) {
                            var height = _region.LandDefs.LandHeightTable[landblockData[i].Height];
                            minHeight = Math.Min(minHeight, height);
                            maxHeight = Math.Max(maxHeight, height);
                        }
                    }
                }
            }

            return new BoundingBox(
                new Vector3(chunk.LandblockStartX * LandblockLength, chunk.LandblockStartY * LandblockLength, minHeight),
                new Vector3((chunk.LandblockStartX + chunk.ActualLandblockCountX) * LandblockLength,
                           (chunk.LandblockStartY + chunk.ActualLandblockCountY) * LandblockLength, maxHeight)
            );
        }

        public TerrainChunk? GetChunk(ulong chunkId) => _chunks.TryGetValue(chunkId, out var chunk) ? chunk : null;
        public TerrainChunk? GetChunkForLandblock(uint landblockX, uint landblockY) {
            var chunkX = landblockX / _chunkSizeInLandblocks;
            var chunkY = landblockY / _chunkSizeInLandblocks;
            return GetChunk(GetChunkId(chunkX, chunkY));
        }

        public IEnumerable<TerrainChunk> GetAllChunks() => _chunks.Values;

        /// <summary>
        /// Marks landblocks as dirty for incremental updates
        /// </summary>
        public void MarkLandblocksDirty(HashSet<ushort> landblockIds) {
            foreach (var lbId in landblockIds) {
                var chunk = GetChunkForLandblock((uint)lbId >> 8, (uint)lbId & 0xFF);
                chunk?.MarkDirty(lbId);
            }
        }

        public static ulong GetChunkId(uint chunkX, uint chunkY) => (ulong)chunkX << 32 | chunkY;

        // Height lookup utilities

        /// <summary>
        /// Gets the terrain height at a world position using AC-accurate triangle-based
        /// interpolation. Each 24x24 terrain cell is split into 2 triangles using a
        /// pseudo-random split direction matching the AC client's algorithm
        /// (see LandblockStruct.ConstructPolygons).
        /// </summary>
        public float GetHeightAtPosition(float worldX, float worldY) {
            uint landblockX = (uint)Math.Floor(worldX / LandblockLength);
            uint landblockY = (uint)Math.Floor(worldY / LandblockLength);

            if (landblockX >= MapSize || landblockY >= MapSize) return 0f;

            var landblockID = landblockX << 8 | landblockY;
            var landblockData = _terrainSystem.GetLandblockTerrain((ushort)landblockID);
            if (landblockData == null) return 0f;

            float localX = worldX - landblockX * LandblockLength;
            float localY = worldY - landblockY * LandblockLength;

            return SampleHeightTriangle(landblockData, _region.LandDefs.LandHeightTable,
                localX, localY, landblockX, landblockY);
        }

        public static float SampleHeightTriangle(TerrainEntry[] data, float[] heightTable,
            float localX, float localY, uint landblockX, uint landblockY)
            => TerrainHeightSampler.SampleHeightTriangle(data, heightTable, localX, localY, landblockX, landblockY);

        public static bool IsSWtoNEcut(uint globalCellX, uint globalCellY)
            => TerrainHeightSampler.IsSWtoNEcut(globalCellX, globalCellY);

        private static float GetHeightFromData(TerrainEntry[] data, float[] heightTable, uint vx, uint vy)
            => TerrainHeightSampler.GetHeightFromData(data, heightTable, vx, vy);
    }
}