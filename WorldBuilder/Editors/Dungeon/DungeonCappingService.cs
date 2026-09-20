using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// Standalone portal capping service extracted from the generator's capping phase.
    /// Caps individual portals or all open portals with dead-end rooms using proven
    /// transforms from the knowledge base, falling back to geometric snap.
    /// </summary>
    public class DungeonCappingService {
        private readonly DungeonDocument _doc;
        private readonly IDatReaderWriter _dats;
        private readonly DungeonKnowledgeBase _kb;
        private readonly PortalCompatibilityIndex _portalIndex;
        private readonly PortalGeometryCache _geoCache;
        private readonly Dictionary<(ushort, ushort, ushort), List<DeadEndOption>> _deadEndLookup;
        private readonly HashSet<(ushort, ushort)> _catalogDeadEnds;

        public DungeonCappingService(
            DungeonDocument doc, IDatReaderWriter dats,
            DungeonKnowledgeBase kb, PortalCompatibilityIndex portalIndex,
            PortalGeometryCache geoCache) {
            _doc = doc;
            _dats = dats;
            _kb = kb;
            _portalIndex = portalIndex;
            _geoCache = geoCache;

            _deadEndLookup = new();
            foreach (var entry in kb.DeadEndIndex) {
                _deadEndLookup[(entry.EnvId, entry.CellStruct, entry.PolyId)] = entry.Options;
            }

            _catalogDeadEnds = new(kb.Catalog
                .Where(c => c.Category == "Dead End")
                .Select(c => (c.EnvId, c.CellStruct)));
        }

        /// <summary>
        /// Try to cap a single open portal with a dead-end room.
        /// Returns the composite command on success, null on failure.
        /// </summary>
        public DungeonCompositeCommand? CapPortal(ushort cellNum, ushort polyId) {
            var existingCell = _doc.GetCell(cellNum);
            if (existingCell == null) return null;

            var candidates = FindCapCandidates(existingCell.EnvironmentId, existingCell.CellStructure, polyId);
            if (candidates.Count == 0) return null;

            var surfaces = FindSurfacesForRoom(candidates[0].EnvId, candidates[0].CellStruct);

            foreach (var cap in candidates.Take(8)) {
                var placement = ComputeCapPlacement(existingCell, cellNum, polyId, cap);
                if (placement == null) continue;

                var (origin, orientation, sourcePolyId) = placement.Value;

                if (HasOverlap(origin, cap.EnvId, cap.CellStruct))
                    continue;

                var capSurfaces = FindSurfacesForRoom(cap.EnvId, cap.CellStruct);
                var composite = new DungeonCompositeCommand("Cap Portal");
                var cmd = new AddCellCommand(
                    cap.EnvId, cap.CellStruct,
                    origin, orientation, capSurfaces,
                    connectToCellNum: cellNum, connectToPolyId: polyId,
                    sourcePolyId: sourcePolyId);
                cmd.Execute(_doc);
                composite.Add(cmd);
                return composite;
            }
            return null;
        }

        /// <summary>
        /// Cap all open portals in the dungeon. Returns the number of portals sealed.
        /// </summary>
        public (int sealed_, int failed, DungeonCompositeCommand? command) CapAllOpenPortals() {
            var frontier = CollectOpenFaces();
            if (frontier.Count == 0) return (0, 0, null);

            var composite = new DungeonCompositeCommand("Cap All Open Portals");
            int sealed_ = 0, failed = 0;

            for (int pass = 0; pass < 4 && frontier.Count > 0; pass++) {
                int passSealed = 0;
                foreach (var (cn, pid, envId, cs) in frontier.ToList()) {
                    var existingCell = _doc.GetCell(cn);
                    if (existingCell == null) { failed++; continue; }

                    var candidates = FindCapCandidates(envId, cs, pid);
                    if (candidates.Count == 0) { failed++; continue; }

                    bool placed = false;
                    foreach (var cap in candidates.Take(8)) {
                        var placement = ComputeCapPlacement(existingCell, cn, pid, cap);
                        if (placement == null) continue;

                        var (origin, orientation, sourcePolyId) = placement.Value;
                        if (HasOverlap(origin, cap.EnvId, cap.CellStruct)) continue;

                        var capSurfaces = FindSurfacesForRoom(cap.EnvId, cap.CellStruct);
                        var cmd = new AddCellCommand(
                            cap.EnvId, cap.CellStruct,
                            origin, orientation, capSurfaces,
                            connectToCellNum: cn, connectToPolyId: pid,
                            sourcePolyId: sourcePolyId);
                        cmd.Execute(_doc);
                        composite.Add(cmd);
                        passSealed++;
                        placed = true;
                        break;
                    }
                    if (!placed) failed++;
                }
                sealed_ += passSealed;
                if (passSealed == 0) break;
                frontier = CollectOpenFaces();
            }

            return (sealed_, failed, sealed_ > 0 ? composite : null);
        }

        private List<CompatibleRoom> FindCapCandidates(ushort envId, ushort cs, ushort polyId) {
            var candidates = new List<CompatibleRoom>();

            if (_deadEndLookup.TryGetValue((envId, cs, polyId), out var deadEnds)) {
                foreach (var opt in deadEnds) {
                    candidates.Add(new CompatibleRoom {
                        EnvId = opt.EnvId, CellStruct = opt.CellStruct, PolyId = opt.PolyId,
                        Count = opt.Count,
                        RelOffset = new Vector3(opt.RelOffsetX, opt.RelOffsetY, opt.RelOffsetZ),
                        RelRot = new Quaternion(opt.RelRotX, opt.RelRotY, opt.RelRotZ, opt.RelRotW)
                    });
                }
            }

            if (candidates.Count == 0) {
                var compat = _portalIndex.GetCompatible(envId, cs, polyId);
                foreach (var cr in compat) {
                    if (_catalogDeadEnds.Contains((cr.EnvId, cr.CellStruct)))
                        candidates.Add(cr);
                }
            }

            if (candidates.Count == 0) {
                var targetGeo = _geoCache.Get(envId, cs, polyId);
                if (targetGeo != null && targetGeo.Area > 0.5f) {
                    foreach (var de in _kb.Catalog.Where(c => c.Category == "Dead End" && c.PortalPolyIds.Count > 0)) {
                        var dePoly = de.PortalPolyIds[0];
                        if (!_geoCache.AreCompatible(envId, cs, polyId, de.EnvId, de.CellStruct, dePoly))
                            continue;
                        candidates.Add(new CompatibleRoom {
                            EnvId = de.EnvId, CellStruct = de.CellStruct, PolyId = dePoly,
                            Count = de.UsageCount, IsGeometryDerived = true
                        });
                    }
                }
            }

            return candidates.OrderByDescending(c => c.Count).ToList();
        }

        private (Vector3 origin, Quaternion orientation, ushort sourcePolyId)? ComputeCapPlacement(
            DungeonCellData existingCell, ushort existingCellNum, ushort existingPolyId,
            CompatibleRoom capRoom) {

            if (!capRoom.IsGeometryDerived && capRoom.RelRot.LengthSquared() > 0.01f) {
                var origin = existingCell.Origin +
                    Vector3.Transform(capRoom.RelOffset, existingCell.Orientation);
                var orientation = Quaternion.Normalize(existingCell.Orientation * capRoom.RelRot);
                return (origin, orientation, capRoom.PolyId);
            }

            uint existingEnvFileId = (uint)(existingCell.EnvironmentId | 0x0D000000);
            if (!_dats.TryGet<Acme.Dat.Environment>(existingEnvFileId, out var existingEnv)) return null;
            if (!existingEnv.Cells.TryGetValue(existingCell.CellStructure, out var existingCS)) return null;

            var targetGeom = PortalSnapper.GetPortalGeometry(existingCS, existingPolyId);
            if (targetGeom == null) return null;

            var (centroid, normal) = PortalSnapper.TransformPortalToWorld(
                targetGeom.Value, existingCell.Origin, existingCell.Orientation);

            uint capEnvFileId = (uint)(capRoom.EnvId | 0x0D000000);
            if (!_dats.TryGet<Acme.Dat.Environment>(capEnvFileId, out var capEnv)) return null;
            if (!capEnv.Cells.TryGetValue(capRoom.CellStruct, out var capCS)) return null;

            var srcPortalId = capRoom.PolyId;
            if (srcPortalId == 0) {
                var picked = PortalSnapper.PickBestSourcePortal(capCS, normal);
                if (picked == null) return null;
                srcPortalId = picked.Value;
            }

            var srcGeom = PortalSnapper.GetPortalGeometry(capCS, srcPortalId);
            if (srcGeom == null) return null;

            var (snapOrigin, snapRot) = PortalSnapper.ComputeSnapTransform(centroid, normal, srcGeom.Value);
            return (snapOrigin, snapRot, srcPortalId);
        }

        private bool HasOverlap(Vector3 newOrigin, ushort envId, ushort cellStruct) {
            var newAABB = _geoCache.GetAABB(envId, cellStruct);
            if (newAABB == null) {
                const float minDist = 3f;
                foreach (var cell in _doc.Cells) {
                    if ((cell.Origin - newOrigin).LengthSquared() < minDist * minDist)
                        return true;
                }
                return false;
            }

            var newWorld = newAABB.Value.ToWorldSpace(newOrigin, Quaternion.Identity);
            foreach (var cell in _doc.Cells) {
                var existAABB = _geoCache.GetAABB(cell.EnvironmentId, cell.CellStructure);
                if (existAABB == null) continue;
                var existWorld = existAABB.Value.ToWorldSpace(cell.Origin, cell.Orientation);
                if (newWorld.Intersects(existWorld, shrink: 1.0f))
                    return true;
            }
            return false;
        }

        private List<ushort> FindSurfacesForRoom(ushort envId, ushort cellStruct) {
            var cr = _kb.Catalog.FirstOrDefault(c => c.EnvId == envId && c.CellStruct == cellStruct);
            if (cr != null && cr.SampleSurfaces.Count > 0)
                return new List<ushort>(cr.SampleSurfaces);

            uint envFileId = (uint)(envId | 0x0D000000);
            if (_dats.TryGet<Acme.Dat.Environment>(envFileId, out var env) &&
                env.Cells.TryGetValue(cellStruct, out var cs)) {
                var portalIds = cs.Portals != null ? new HashSet<ushort>(cs.Portals) : new HashSet<ushort>();
                int maxIdx = -1;
                foreach (var kvp in cs.Polygons) {
                    if (portalIds.Contains(kvp.Key)) continue;
                    if (kvp.Value.PosSurface > maxIdx) maxIdx = kvp.Value.PosSurface;
                }
                if (maxIdx >= 0)
                    return Enumerable.Repeat((ushort)0x032A, maxIdx + 1).ToList();
            }
            return new List<ushort>();
        }

        private List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)> CollectOpenFaces() {
            var frontier = new List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)>();
            foreach (var dc in _doc.Cells) {
                uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
                if (!_dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(dc.CellStructure, out var cs)) continue;

                var allPortals = PortalSnapper.GetPortalPolygonIds(cs);
                var connected = new HashSet<ushort>(dc.CellPortals.Select(cp => cp.PolygonId));
                foreach (var pid in allPortals) {
                    if (!connected.Contains(pid))
                        frontier.Add((dc.CellNumber, pid, dc.EnvironmentId, dc.CellStructure));
                }
            }
            return frontier;
        }
    }
}
