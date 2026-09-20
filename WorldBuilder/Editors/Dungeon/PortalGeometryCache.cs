using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    public class PortalGeometryInfo {
        public float Area { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public int VertexCount { get; set; }
        public Vector3 Centroid { get; set; }
        public Vector3 Normal { get; set; }
    }

    /// <summary>
    /// Axis-aligned bounding box for a room in local space (before cell orientation).
    /// Used for geometric overlap testing that is more accurate than origin-distance.
    /// </summary>
    public struct RoomAABB {
        public Vector3 Min;
        public Vector3 Max;

        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Extents => (Max - Min) * 0.5f;

        /// <summary>
        /// Transform this local-space AABB to world space given cell origin+orientation,
        /// returning a conservative axis-aligned bounding box that encloses the rotated volume.
        /// </summary>
        public RoomAABB ToWorldSpace(Vector3 origin, Quaternion orientation) {
            var localCenter = Center;
            var localExtents = Extents;

            var worldCenter = Vector3.Transform(localCenter, orientation) + origin;

            // Rotate extents: compute the world-axis-aligned extent by summing
            // the absolute contributions of each local axis.
            var m = Matrix4x4.CreateFromQuaternion(orientation);
            float ex = MathF.Abs(m.M11) * localExtents.X + MathF.Abs(m.M21) * localExtents.Y + MathF.Abs(m.M31) * localExtents.Z;
            float ey = MathF.Abs(m.M12) * localExtents.X + MathF.Abs(m.M22) * localExtents.Y + MathF.Abs(m.M32) * localExtents.Z;
            float ez = MathF.Abs(m.M13) * localExtents.X + MathF.Abs(m.M23) * localExtents.Y + MathF.Abs(m.M33) * localExtents.Z;

            var worldExtents = new Vector3(ex, ey, ez);
            return new RoomAABB { Min = worldCenter - worldExtents, Max = worldCenter + worldExtents };
        }

        public bool Intersects(RoomAABB other, float shrink = 0f) {
            return Min.X + shrink < other.Max.X - shrink &&
                   Max.X - shrink > other.Min.X + shrink &&
                   Min.Y + shrink < other.Max.Y - shrink &&
                   Max.Y - shrink > other.Min.Y + shrink &&
                   Min.Z + shrink < other.Max.Z - shrink &&
                   Max.Z - shrink > other.Min.Z + shrink;
        }

        public bool ContainsPoint(Vector3 p, float inflate = 0f) {
            return p.X >= Min.X - inflate && p.X <= Max.X + inflate &&
                   p.Y >= Min.Y - inflate && p.Y <= Max.Y + inflate &&
                   p.Z >= Min.Z - inflate && p.Z <= Max.Z + inflate;
        }
    }

    /// <summary>
    /// Lazily caches portal polygon geometry (area, vertex count) from CellStruct data.
    /// Used for compatibility checks: two portals match if areas are within tolerance.
    /// </summary>
    public class PortalGeometryCache {
        private readonly Dictionary<(ushort envId, ushort cs, ushort polyId), PortalGeometryInfo?> _cache = new();
        private readonly Dictionary<(ushort envId, ushort cs), RoomAABB?> _aabbCache = new();
        private readonly IDatReaderWriter _dats;
        private const float AreaTolerance = 0.15f;

        public PortalGeometryCache(IDatReaderWriter dats) {
            _dats = dats;
        }

        /// <summary>
        /// Get the local-space axis-aligned bounding box for a room type,
        /// computed from the CellStruct vertex array.
        /// </summary>
        public RoomAABB? GetAABB(ushort envId, ushort cellStruct) {
            var key = (envId, cellStruct);
            if (_aabbCache.TryGetValue(key, out var cached)) return cached;

            var aabb = ComputeAABB(envId, cellStruct);
            _aabbCache[key] = aabb;
            return aabb;
        }

        private RoomAABB? ComputeAABB(ushort envId, ushort cellStruct) {
            uint envFileId = (uint)(envId | 0x0D000000);
            if (!_dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return null;
            if (!env.Cells.TryGetValue(cellStruct, out var cs)) return null;
            if (cs.VertexArray?.Vertices == null || cs.VertexArray.Vertices.Count == 0) return null;

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var vtx in cs.VertexArray.Vertices.Values) {
                var p = vtx.Origin;
                if (p.X < min.X) min.X = p.X; if (p.X > max.X) max.X = p.X;
                if (p.Y < min.Y) min.Y = p.Y; if (p.Y > max.Y) max.Y = p.Y;
                if (p.Z < min.Z) min.Z = p.Z; if (p.Z > max.Z) max.Z = p.Z;
            }
            return new RoomAABB { Min = min, Max = max };
        }

        public PortalGeometryInfo? Get(ushort envId, ushort cellStruct, ushort polyId) {
            var key = (envId, cellStruct, polyId);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var info = Compute(envId, cellStruct, polyId);
            _cache[key] = info;
            return info;
        }

        private PortalGeometryInfo? Compute(ushort envId, ushort cellStruct, ushort polyId) {
            uint envFileId = (uint)(envId | 0x0D000000);
            if (!_dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return null;
            if (!env.Cells.TryGetValue(cellStruct, out var cs)) return null;

            var geom = PortalSnapper.GetPortalGeometry(cs, polyId);
            if (geom == null) return null;

            var verts = geom.Value.Vertices;
            float area = ComputePolygonArea(verts);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var v in verts) {
                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
            }
            float w = MathF.Max(maxX - minX, maxY - minY);
            float h = maxZ - minZ;

            return new PortalGeometryInfo {
                Area = area,
                Width = w,
                Height = h,
                VertexCount = verts.Count,
                Centroid = geom.Value.Centroid,
                Normal = geom.Value.Normal
            };
        }

        private static float ComputePolygonArea(List<Vector3> vertices) {
            if (vertices.Count < 3) return 0f;
            var cross = Vector3.Zero;
            for (int i = 1; i < vertices.Count - 1; i++) {
                cross += Vector3.Cross(vertices[i] - vertices[0], vertices[i + 1] - vertices[0]);
            }
            return cross.Length() * 0.5f;
        }

        /// <summary>
        /// Check if two portals are geometrically compatible.
        /// Compares area, width, and height within tolerance.
        /// </summary>
        public bool AreCompatible(ushort envA, ushort csA, ushort polyA,
                                   ushort envB, ushort csB, ushort polyB) {
            var a = Get(envA, csA, polyA);
            var b = Get(envB, csB, polyB);
            if (a == null || b == null) return false;
            if (a.Area < 0.01f || b.Area < 0.01f) return false;

            float areaRatio = MathF.Min(a.Area, b.Area) / MathF.Max(a.Area, b.Area);
            if (areaRatio < (1f - AreaTolerance)) return false;

            if (a.Width > 0.5f && b.Width > 0.5f) {
                float widthRatio = MathF.Min(a.Width, b.Width) / MathF.Max(a.Width, b.Width);
                if (widthRatio < 0.7f) return false;
            }
            if (a.Height > 0.5f && b.Height > 0.5f) {
                float heightRatio = MathF.Min(a.Height, b.Height) / MathF.Max(a.Height, b.Height);
                if (heightRatio < 0.7f) return false;
            }

            return true;
        }

        /// <summary>
        /// True if any open face on the prefab is the same doorway size as any of the given portals.
        /// </summary>
        public bool PrefabFitsAny(
            DungeonPrefab prefab, IReadOnlyList<(ushort envId, ushort cs, ushort polyId)> portals) {
            if (prefab?.OpenFaces == null || prefab.OpenFaces.Count == 0 || portals == null || portals.Count == 0)
                return false;
            foreach (var of in prefab.OpenFaces) {
                foreach (var p in portals) {
                    if (AreCompatible(p.envId, p.cs, p.polyId, of.EnvId, of.CellStruct, of.PolyId))
                        return true;
                }
            }
            return false;
        }
    }
}
