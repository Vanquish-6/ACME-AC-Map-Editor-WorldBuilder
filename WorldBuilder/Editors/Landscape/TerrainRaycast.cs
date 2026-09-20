using Acme.Render;
using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape {
    public static class TerrainRaycast {
        public struct TerrainRaycastHit {
            public bool Hit;
            public Vector3 HitPosition;
            public float Distance;
            public uint LandcellId;

            public ushort LandblockId => (ushort)(LandcellId >> 16);
            public uint LandblockX => (uint)(LandblockId >> 8);
            public uint LandblockY => (uint)(LandblockId & 0xFF);
            public uint CellX => (uint)Math.Round(HitPosition.X % 192f / 24f);
            public uint CellY => (uint)Math.Round(HitPosition.Y % 192f / 24f);

            public Vector3 NearestVertice {
                get {
                    var vx = VerticeX;
                    var vy = VerticeY;
                    var x = (LandblockId >> 8) * 192 + vx * 24;
                    var y = (LandblockId & 0xFF) * 192 + vy * 24;
                    return new Vector3(x, y, HitPosition.Z);
                }
            }

            public int VerticeIndex {
                get {
                    var vx = (int)Math.Round(HitPosition.X % 192f / 24f);
                    var vy = (int)Math.Round(HitPosition.Y % 192f / 24f);
                    return vx * 9 + vy;
                }
            }

            public int VerticeX => (int)Math.Round(HitPosition.X % 192f / 24f);
            public int VerticeY => (int)Math.Round(HitPosition.Y % 192f / 24f);
        }

        /// <summary>
        /// Performs raycast against terrain system
        /// </summary>
        public static TerrainRaycastHit Raycast(
            float mouseX, float mouseY,
            int viewportWidth, int viewportHeight,
            ICamera camera,
            TerrainSystem terrainSystem) {

            TerrainRaycastHit hit = new TerrainRaycastHit { Hit = false };

            // Convert to NDC
            float ndcX = 2.0f * mouseX / viewportWidth - 1.0f;
            float ndcY = 2.0f * mouseY / viewportHeight - 1.0f;

            // Create ray in world space
            Matrix4x4 projection = camera.GetProjectionMatrix();
            Matrix4x4 view = camera.GetViewMatrix();

            if (!Matrix4x4.Invert(view * projection, out Matrix4x4 viewProjectionInverse)) {
                return hit;
            }

            Vector4 nearPoint = new Vector4(ndcX, ndcY, -1.0f, 1.0f);
            Vector4 farPoint = new Vector4(ndcX, ndcY, 1.0f, 1.0f);

            Vector4 nearWorld = Vector4.Transform(nearPoint, viewProjectionInverse);
            Vector4 farWorld = Vector4.Transform(farPoint, viewProjectionInverse);

            nearWorld /= nearWorld.W;
            farWorld /= farWorld.W;

            Vector3 rayOrigin = new Vector3(nearWorld.X, nearWorld.Y, nearWorld.Z);
            Vector3 rayDirection = Vector3.Normalize(new Vector3(farWorld.X, farWorld.Y, farWorld.Z) - rayOrigin);

            return TraverseLandblocks(rayOrigin, rayDirection, terrainSystem);
        }

        // Reusable vertex buffer to avoid per-cell allocations during raycast
        [ThreadStatic] private static Vector3[]? _vertexBuffer;

        private static TerrainRaycastHit TraverseLandblocks(
            Vector3 rayOrigin,
            Vector3 rayDirection,
            TerrainSystem terrainSystem) {

            TerrainRaycastHit hit = new TerrainRaycastHit { Hit = false };

            const float maxDistance = 80000f;
            const float landblockSize = 192f;
            // Cap traversal to prevent UI thread starvation when raycasting at great distances.
            // 150 landblocks ≈ 28,800 world units — well beyond any practical render distance.
            const int maxStepsCap = 150;

            Vector3 rayEnd = rayOrigin + rayDirection * maxDistance;

            int startLbX = (int)Math.Floor(rayOrigin.X / landblockSize);
            int startLbY = (int)Math.Floor(rayOrigin.Y / landblockSize);
            int endLbX = (int)Math.Floor(rayEnd.X / landblockSize);
            int endLbY = (int)Math.Floor(rayEnd.Y / landblockSize);

            int currentLbX = startLbX;
            int currentLbY = startLbY;

            int stepX = rayDirection.X > 0 ? 1 : -1;
            int stepY = rayDirection.Y > 0 ? 1 : -1;

            float deltaDistX = Math.Abs(1.0f / rayDirection.X);
            float deltaDistY = Math.Abs(1.0f / rayDirection.Y);

            float sideDistX = rayDirection.X < 0
                ? (rayOrigin.X / landblockSize - currentLbX) * deltaDistX
                : (currentLbX + 1.0f - rayOrigin.X / landblockSize) * deltaDistX;

            float sideDistY = rayDirection.Y < 0
                ? (rayOrigin.Y / landblockSize - currentLbY) * deltaDistY
                : (currentLbY + 1.0f - rayOrigin.Y / landblockSize) * deltaDistY;

            float closestDistance = float.MaxValue;

            // Fix: DDA with side-distance tracking needs |dx|+|dy| steps (each step moves
            // in only one axis), not max(|dx|,|dy|). Cap to prevent excessive traversal.
            int maxSteps = Math.Min(
                Math.Abs(endLbX - startLbX) + Math.Abs(endLbY - startLbY) + 1,
                maxStepsCap);

            for (int step = 0; step < maxSteps; step++) {
                if (currentLbX >= 0 && currentLbX < TerrainDataManager.MapSize &&
                    currentLbY >= 0 && currentLbY < TerrainDataManager.MapSize) {

                    uint landblockID = (uint)(currentLbX << 8 | currentLbY);
                    var landblockData = terrainSystem.GetLandblockTerrain((ushort)landblockID);

                    if (landblockData != null) {
                        var landblockHit = TestLandblockIntersection(
                            rayOrigin, rayDirection,
                            (uint)currentLbX, (uint)currentLbY, landblockID,
                            landblockData, terrainSystem);

                        if (landblockHit.Hit && landblockHit.Distance < closestDistance) {
                            hit = landblockHit;
                            closestDistance = landblockHit.Distance;
                        }
                    }
                }

                if (sideDistX < sideDistY) {
                    sideDistX += deltaDistX;
                    currentLbX += stepX;
                }
                else {
                    sideDistY += deltaDistY;
                    currentLbY += stepY;
                }

                if (hit.Hit && (sideDistX * landblockSize > closestDistance || sideDistY * landblockSize > closestDistance)) {
                    break;
                }
            }

            return hit;
        }

        private static TerrainRaycastHit TestLandblockIntersection(
            Vector3 rayOrigin, Vector3 rayDirection,
            uint landblockX, uint landblockY, uint landblockID,
            TerrainEntry[] landblockData, TerrainSystem terrainSystem) {

            TerrainRaycastHit hit = new TerrainRaycastHit { Hit = false };

            float baseLandblockX = landblockX * TerrainDataManager.LandblockLength;
            float baseLandblockY = landblockY * TerrainDataManager.LandblockLength;

            BoundingBox landblockBounds = new BoundingBox(
                new Vector3(baseLandblockX, baseLandblockY, -1000f),
                new Vector3(baseLandblockX + TerrainDataManager.LandblockLength,
                           baseLandblockY + TerrainDataManager.LandblockLength, 1000f)
            );

            if (!RayIntersectsBox(rayOrigin, rayDirection, landblockBounds, out float tMin, out float tMax)) {
                return hit;
            }

            float closestDistance = float.MaxValue;
            uint hitCellX = 0;
            uint hitCellY = 0;
            Vector3 hitPosition = Vector3.Zero;

            // Use ray entry/exit points to determine which cells to test, rather than
            // sorting all 64 cells by distance. This reduces per-landblock work from
            // 64 cells to only the cells the ray actually passes through.
            GetCellRange(rayOrigin, rayDirection, baseLandblockX, baseLandblockY, tMin, tMax,
                out int minCellX, out int maxCellX, out int minCellY, out int maxCellY);

            // Reuse a single vertex buffer to avoid allocating Vector3[4] for every cell
            var vertices = _vertexBuffer ??= new Vector3[4];

            for (int cy = minCellY; cy <= maxCellY; cy++) {
                for (int cx = minCellX; cx <= maxCellX; cx++) {
                    uint cellX = (uint)cx;
                    uint cellY = (uint)cy;

                    GenerateCellVerticesInPlace(
                        baseLandblockX, baseLandblockY, cellX, cellY,
                        landblockData, terrainSystem.Region, vertices);

                    BoundingBox cellBounds = CalculateCellBounds(vertices);
                    if (!RayIntersectsBox(rayOrigin, rayDirection, cellBounds, out float cellTMin, out float cellTMax)) {
                        continue;
                    }

                    if (cellTMin > closestDistance) continue;

                    var splitDiagonal = TerrainGeometryGenerator.CalculateSplitDirection(landblockX, cellX, landblockY, cellY);

                    // Test triangles using individual vertex refs instead of allocating arrays
                    Vector3 t1v0, t1v1, t1v2, t2v0, t2v1, t2v2;
                    if (splitDiagonal == CellSplitDirection.SEtoNW) {
                        t1v0 = vertices[0]; t1v1 = vertices[1]; t1v2 = vertices[2];
                        t2v0 = vertices[0]; t2v1 = vertices[2]; t2v2 = vertices[3];
                    }
                    else {
                        t1v0 = vertices[0]; t1v1 = vertices[1]; t1v2 = vertices[3];
                        t2v0 = vertices[1]; t2v1 = vertices[2]; t2v2 = vertices[3];
                    }

                    if (RayIntersectsTriangle(rayOrigin, rayDirection, t1v0, t1v1, t1v2, out float t1, out Vector3 p1) && t1 < closestDistance) {
                        closestDistance = t1;
                        hitPosition = p1;
                        hitCellX = cellX;
                        hitCellY = cellY;
                        hit.Hit = true;
                    }

                    if (RayIntersectsTriangle(rayOrigin, rayDirection, t2v0, t2v1, t2v2, out float t2, out Vector3 p2) && t2 < closestDistance) {
                        closestDistance = t2;
                        hitPosition = p2;
                        hitCellX = cellX;
                        hitCellY = cellY;
                        hit.Hit = true;
                    }
                }
            }

            if (hit.Hit) {
                hit.HitPosition = hitPosition;
                hit.Distance = closestDistance;
                hit.LandcellId = (landblockID << 16) + hitCellX * 8 + hitCellY;
            }

            return hit;
        }

        /// <summary>
        /// Fills a pre-allocated vertex buffer with cell corner positions (avoids allocation).
        /// </summary>
        private static void GenerateCellVerticesInPlace(
            float baseLandblockX, float baseLandblockY,
            uint cellX, uint cellY,
            TerrainEntry[] landblockData, Region region, Vector3[] vertices) {

            var bottomLeft = GetTerrainEntryForCell(landblockData, cellX, cellY);
            var bottomRight = GetTerrainEntryForCell(landblockData, cellX + 1, cellY);
            var topRight = GetTerrainEntryForCell(landblockData, cellX + 1, cellY + 1);
            var topLeft = GetTerrainEntryForCell(landblockData, cellX, cellY + 1);

            vertices[0] = new Vector3(
                baseLandblockX + cellX * 24f,
                baseLandblockY + cellY * 24f,
                region.LandDefs.LandHeightTable[bottomLeft.Height]
            );

            vertices[1] = new Vector3(
                baseLandblockX + (cellX + 1) * 24f,
                baseLandblockY + cellY * 24f,
                region.LandDefs.LandHeightTable[bottomRight.Height]
            );

            vertices[2] = new Vector3(
                baseLandblockX + (cellX + 1) * 24f,
                baseLandblockY + (cellY + 1) * 24f,
                region.LandDefs.LandHeightTable[topRight.Height]
            );

            vertices[3] = new Vector3(
                baseLandblockX + cellX * 24f,
                baseLandblockY + (cellY + 1) * 24f,
                region.LandDefs.LandHeightTable[topLeft.Height]
            );
        }

        /// <summary>
        /// Computes the cell rectangle that the ray passes through within a landblock,
        /// using the ray's entry/exit points from the landblock bounding box intersection.
        /// This replaces sorting all 64 cells by distance — typically reduces to 5-15 cells.
        /// </summary>
        private static void GetCellRange(
            Vector3 rayOrigin, Vector3 rayDirection,
            float baseLandblockX, float baseLandblockY,
            float tMin, float tMax,
            out int minCellX, out int maxCellX, out int minCellY, out int maxCellY) {

            const float cellSize = 24f;
            const int maxCell = (int)TerrainDataManager.LandblockEdgeCellCount - 1; // 7

            // Compute ray entry and exit points in world space
            Vector3 entryPoint = rayOrigin + rayDirection * Math.Max(tMin, 0f);
            Vector3 exitPoint = rayOrigin + rayDirection * tMax;

            // Convert to cell coordinates within the landblock
            int entryCellX = (int)Math.Floor((entryPoint.X - baseLandblockX) / cellSize);
            int entryCellY = (int)Math.Floor((entryPoint.Y - baseLandblockY) / cellSize);
            int exitCellX = (int)Math.Floor((exitPoint.X - baseLandblockX) / cellSize);
            int exitCellY = (int)Math.Floor((exitPoint.Y - baseLandblockY) / cellSize);

            // Clamp to valid cell range [0, 7]
            entryCellX = Math.Clamp(entryCellX, 0, maxCell);
            entryCellY = Math.Clamp(entryCellY, 0, maxCell);
            exitCellX = Math.Clamp(exitCellX, 0, maxCell);
            exitCellY = Math.Clamp(exitCellY, 0, maxCell);

            // Build the rectangle that covers both entry and exit cells
            minCellX = Math.Min(entryCellX, exitCellX);
            maxCellX = Math.Max(entryCellX, exitCellX);
            minCellY = Math.Min(entryCellY, exitCellY);
            maxCellY = Math.Max(entryCellY, exitCellY);
        }

        private static BoundingBox CalculateCellBounds(Vector3[] vertices) {
            // Use current implementation
            Vector3 min = vertices[0];
            Vector3 max = vertices[0];

            for (int i = 1; i < vertices.Length; i++) {
                if (vertices[i].X < min.X) min.X = vertices[i].X;
                if (vertices[i].Y < min.Y) min.Y = vertices[i].Y;
                if (vertices[i].Z < min.Z) min.Z = vertices[i].Z;
                if (vertices[i].X > max.X) max.X = vertices[i].X;
                if (vertices[i].Y > max.Y) max.Y = vertices[i].Y;
                if (vertices[i].Z > max.Z) max.Z = vertices[i].Z;
            }

            return new BoundingBox(min, max);
        }

        private static bool RayIntersectsBox(Vector3 origin, Vector3 direction, BoundingBox box, out float tMin, out float tMax) {
            tMin = 0.0f;
            tMax = float.MaxValue;
            Vector3 min = box.Min;
            Vector3 max = box.Max;

            for (int i = 0; i < 3; i++) {
                if (Math.Abs(direction[i]) < 1e-6f) {
                    if (origin[i] < min[i] || origin[i] > max[i]) return false;
                }
                else {
                    float invD = 1.0f / direction[i];
                    float t0 = (min[i] - origin[i]) * invD;
                    float t1 = (max[i] - origin[i]) * invD;
                    if (t0 > t1) (t0, t1) = (t1, t0);
                    tMin = Math.Max(tMin, t0);
                    tMax = Math.Min(tMax, t1);
                    if (tMin > tMax) return false;
                }
            }
            return true;
        }

        private static bool RayIntersectsTriangle(Vector3 origin, Vector3 direction, Vector3 v0, Vector3 v1, Vector3 v2, out float t, out Vector3 intersectionPoint) {
            t = 0;
            intersectionPoint = Vector3.Zero;

            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 h = Vector3.Cross(direction, edge2);
            float a = Vector3.Dot(edge1, h);

            if (Math.Abs(a) < 1e-6f) return false;

            float f = 1.0f / a;
            Vector3 s = origin - v0;
            float u = f * Vector3.Dot(s, h);

            if (u < 0.0f || u > 1.0f) return false;

            Vector3 q = Vector3.Cross(s, edge1);
            float v = f * Vector3.Dot(direction, q);

            if (v < 0.0f || u + v > 1.0f) return false;

            t = f * Vector3.Dot(edge2, q);

            if (t > 1e-6f) {
                intersectionPoint = origin + direction * t;
                return true;
            }

            return false;
        }

        private static TerrainEntry GetTerrainEntryForCell(TerrainEntry[] data, uint cellX, uint cellY) {
            var idx = (int)(cellX * 9 + cellY);
            return data != null && idx < data.Length ? data[idx] : new TerrainEntry(0);
        }
    }
}