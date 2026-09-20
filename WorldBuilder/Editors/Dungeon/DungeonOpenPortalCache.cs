using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// Cached view of all open (unconnected) portal faces in a dungeon document.
    /// Rebuilt lazily after any structural change (cell added/removed, portal
    /// connected/disconnected) to avoid the O(cells × portals) DAT-scan that was
    /// previously duplicated across five separate methods in DungeonEditorViewModel.
    /// </summary>
    public class DungeonOpenPortalCache {

        public readonly record struct OpenPortalInfo(
            DungeonCellData Cell,
            CellStruct CellStruct,
            ushort CellNum,
            ushort PolyId,
            Vector3 WorldCentroid,
            Vector3 WorldNormal,
            Vector3[] WorldVertices
        );

        private List<OpenPortalInfo> _openPortals = new();
        private bool _dirty = true;

        public event EventHandler? Invalidated;

        /// <summary>All currently open (unconnected) portal faces.</summary>
        public IReadOnlyList<OpenPortalInfo> OpenPortals {
            get {
                if (_dirty) throw new InvalidOperationException("Cache is dirty — call Rebuild first.");
                return _openPortals;
            }
        }

        public bool IsDirty => _dirty;

        /// <summary>Mark the cache as needing a rebuild on the next access.</summary>
        public void Invalidate() {
            _dirty = true;
            Invalidated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Rebuild the full cache from the current document state.
        /// Call this once per editing operation before accessing OpenPortals.
        /// </summary>
        public void Rebuild(DungeonDocument document, IDatReaderWriter dats, ushort landblockKey) {
            _openPortals = new List<OpenPortalInfo>();
            _dirty = false;

            uint lbId = landblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);
            const float dungeonZBump = -50f;

            foreach (var dc in document.Cells) {
                uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
                if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(dc.CellStructure, out var cs)) continue;

                var allPortals = PortalSnapper.GetPortalPolygonIds(cs);
                var connected = new HashSet<ushort>(dc.CellPortals.Select(cp => cp.PolygonId));

                var cellOrigin = dc.Origin + lbOffset + new Vector3(0, 0, dungeonZBump);
                var cellRot = dc.Orientation;
                var cellTransform = Matrix4x4.CreateFromQuaternion(cellRot) * Matrix4x4.CreateTranslation(cellOrigin);

                foreach (var pid in allPortals) {
                    if (connected.Contains(pid)) continue;
                    if (!cs.Polygons.TryGetValue(pid, out var poly)) continue;
                    if (poly.VertexIds.Count < 3) continue;

                    var worldVerts = new List<Vector3>();
                    foreach (var vid in poly.VertexIds) {
                        if (cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var vtx))
                            worldVerts.Add(Vector3.Transform(vtx.Origin, cellTransform));
                    }
                    if (worldVerts.Count < 3) continue;

                    var centroid = Vector3.Zero;
                    foreach (var v in worldVerts) centroid += v;
                    centroid /= worldVerts.Count;

                    var geom = PortalSnapper.GetPortalGeometry(cs, pid);
                    var normal = geom != null
                        ? Vector3.Normalize(Vector3.Transform(geom.Value.Normal, cellRot))
                        : Vector3.UnitZ;

                    _openPortals.Add(new OpenPortalInfo(dc, cs, dc.CellNumber, pid, centroid, normal, worldVerts.ToArray()));
                }
            }
        }

        /// <summary>
        /// Rebuild if dirty, then return all open portal infos.
        /// </summary>
        public IReadOnlyList<OpenPortalInfo> GetOrRebuild(DungeonDocument document, IDatReaderWriter dats, ushort landblockKey) {
            if (_dirty) Rebuild(document, dats, landblockKey);
            return _openPortals;
        }

        /// <summary>
        /// Returns a flat list of (envId, cellStruct, polyId) tuples for the open portals,
        /// used by RoomPalette compatibility filtering.
        /// </summary>
        public List<(ushort envId, ushort cs, ushort polyId)> GetOpenPortalKeys(
            DungeonDocument document, IDatReaderWriter dats, ushort landblockKey) {
            var portals = GetOrRebuild(document, dats, landblockKey);
            var result = new List<(ushort, ushort, ushort)>(portals.Count);
            foreach (var p in portals)
                result.Add((p.Cell.EnvironmentId, p.Cell.CellStructure, p.PolyId));
            return result;
        }

        /// <summary>
        /// Returns the number of open portals. Uses cached data if not dirty.
        /// </summary>
        public int Count(DungeonDocument document, IDatReaderWriter dats, ushort landblockKey) {
            return GetOrRebuild(document, dats, landblockKey).Count;
        }

        /// <summary>
        /// Find the nearest open portal face to a given world position (e.g. a raycast hit).
        /// </summary>
        public OpenPortalInfo? FindNearestToPosition(
            Vector3 worldPos, DungeonDocument document, IDatReaderWriter dats, ushort landblockKey) {
            var portals = GetOrRebuild(document, dats, landblockKey);
            if (portals.Count == 0) return null;
            OpenPortalInfo? best = null;
            float bestDist = float.MaxValue;
            foreach (var p in portals) {
                float d = (p.WorldCentroid - worldPos).LengthSquared();
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        /// <summary>
        /// Find the nearest open portal face to a camera ray (when no surface was hit).
        /// </summary>
        public OpenPortalInfo? FindNearestToRay(
            Vector3 rayOrigin, Vector3 rayDir, DungeonDocument document, IDatReaderWriter dats, ushort landblockKey) {
            var portals = GetOrRebuild(document, dats, landblockKey);
            if (portals.Count == 0) return null;
            float bestDist = float.MaxValue;
            OpenPortalInfo? best = null;
            foreach (var p in portals) {
                var toPoint = p.WorldCentroid - rayOrigin;
                var proj = Vector3.Dot(toPoint, rayDir);
                if (proj < 0) continue;
                var closest = rayOrigin + rayDir * proj;
                var dist = (p.WorldCentroid - closest).LengthSquared();
                if (dist < bestDist) { bestDist = dist; best = p; }
            }
            return best;
        }
    }
}
