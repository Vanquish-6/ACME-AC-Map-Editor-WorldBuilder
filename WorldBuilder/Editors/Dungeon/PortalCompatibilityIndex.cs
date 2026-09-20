using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// Pre-built index mapping each portal face to the rooms proven to connect there
    /// in real dungeons. Built from the 31K adjacency edges at startup.
    /// </summary>
    public class PortalCompatibilityIndex {
        private readonly Dictionary<(ushort envId, ushort cs, ushort polyId), List<CompatibleRoom>> _index = new();

        public static PortalCompatibilityIndex Build(DungeonKnowledgeBase kb) {
            var idx = new PortalCompatibilityIndex();
            if (kb.Edges == null) return idx;
            var catalogByRoom = kb.Catalog?
                .GroupBy(c => (c.EnvId, c.CellStruct))
                .ToDictionary(g => g.Key, g => g.First())
                ?? new Dictionary<(ushort, ushort), CatalogRoom>();

            foreach (var edge in kb.Edges) {
                var keyA = (edge.EnvIdA, edge.CellStructA, edge.PolyIdA);
                var keyB = (edge.EnvIdB, edge.CellStructB, edge.PolyIdB);
                var relOffset = new Vector3(edge.RelOffsetX, edge.RelOffsetY, edge.RelOffsetZ);
                var relRot = new Quaternion(edge.RelRotX, edge.RelRotY, edge.RelRotZ, edge.RelRotW);
                catalogByRoom.TryGetValue((edge.EnvIdA, edge.CellStructA), out var roomA);
                catalogByRoom.TryGetValue((edge.EnvIdB, edge.CellStructB), out var roomB);
                float qualityAtoB = ComputeEdgeQuality(edge.Count, edge.ExactMatch, edge.RestrictionB, roomB?.RestrictionRate ?? 0f, roomB?.OutsidePortalRate ?? 0f);
                float qualityBtoA = ComputeEdgeQuality(edge.Count, edge.ExactMatch, edge.RestrictionA, roomA?.RestrictionRate ?? 0f, roomA?.OutsidePortalRate ?? 0f);

                idx.Add(keyA, new CompatibleRoom {
                    EnvId = edge.EnvIdB, CellStruct = edge.CellStructB, PolyId = edge.PolyIdB,
                    Count = edge.Count, RelOffset = relOffset, RelRot = relRot,
                    PortalWidth = edge.WidthB, PortalHeight = edge.HeightB,
                    PortalArea = edge.AreaB, PortalVertexCount = edge.VertexCountB,
                    EdgeQuality = qualityAtoB,
                    ExactMatch = edge.ExactMatch,
                    RestrictionTarget = edge.RestrictionB,
                    TargetVisibleCellCount = edge.VisibleCellCountB,
                    TargetOutsidePortalRate = roomB?.OutsidePortalRate ?? 0f,
                    TargetRestrictionRate = roomB?.RestrictionRate ?? 0f
                });

                var invRot = Quaternion.Inverse(relRot);
                idx.Add(keyB, new CompatibleRoom {
                    EnvId = edge.EnvIdA, CellStruct = edge.CellStructA, PolyId = edge.PolyIdA,
                    Count = edge.Count,
                    RelOffset = Vector3.Transform(-relOffset, invRot),
                    RelRot = invRot,
                    PortalWidth = edge.WidthA, PortalHeight = edge.HeightA,
                    PortalArea = edge.AreaA, PortalVertexCount = edge.VertexCountA,
                    EdgeQuality = qualityBtoA,
                    ExactMatch = edge.ExactMatch,
                    RestrictionTarget = edge.RestrictionA,
                    TargetVisibleCellCount = edge.VisibleCellCountA,
                    TargetOutsidePortalRate = roomA?.OutsidePortalRate ?? 0f,
                    TargetRestrictionRate = roomA?.RestrictionRate ?? 0f
                });
            }

            Console.WriteLine($"[PortalIndex] Built index: {idx._index.Count} unique portal faces, {kb.Edges.Count} edges");
            return idx;
        }

        private static float ComputeEdgeQuality(int count, bool exactMatch, bool restrictionTarget, float targetRestrictionRate, float targetOutsideRate) {
            float score = MathF.Max(1f, count);
            if (exactMatch) score *= 1.08f;
            if (restrictionTarget) score *= 0.85f;
            score *= 1f - MathF.Min(0.45f, targetRestrictionRate * 0.40f);
            score *= 1f - MathF.Min(0.55f, targetOutsideRate * 0.60f);
            return score;
        }

        private void Add((ushort, ushort, ushort) key, CompatibleRoom room) {
            if (!_index.TryGetValue(key, out var list)) {
                list = new List<CompatibleRoom>();
                _index[key] = list;
            }
            list.Add(room);
        }

        /// <summary>Get all rooms proven to connect at this portal face, sorted by usage count.</summary>
        public List<CompatibleRoom> GetCompatible(ushort envId, ushort cellStruct, ushort polyId) {
            return _index.TryGetValue((envId, cellStruct, polyId), out var list)
                ? list.OrderByDescending(r => r.EdgeQuality > 0f ? r.EdgeQuality : r.Count)
                    .ThenByDescending(r => r.Count)
                    .ToList()
                : new List<CompatibleRoom>();
        }

        /// <summary>Best proven retail connection from this portal face to that room type.</summary>
        public CompatibleRoom? FindMatch(ushort portalEnvId, ushort portalCS, ushort portalPolyId,
            ushort roomEnvId, ushort roomCS) {
            return FindProvenMatch(portalEnvId, portalCS, portalPolyId, roomEnvId, roomCS, roomPolyId: 0);
        }

        /// <summary>
        /// Proven DAT wiring: this portal polygon on the existing room really connected
        /// to <paramref name="roomPolyId"/> on that room type. Geometry-guessed entries
        /// are ignored. <paramref name="roomPolyId"/> 0 matches any face on the room.
        /// </summary>
        public CompatibleRoom? FindProvenMatch(
            ushort portalEnvId, ushort portalCS, ushort portalPolyId,
            ushort roomEnvId, ushort roomCS, ushort roomPolyId) {
            var matches = GetProvenMatches(portalEnvId, portalCS, portalPolyId, roomEnvId, roomCS, roomPolyId);
            return matches.Count > 0 ? matches[0] : null;
        }

        /// <summary>Distinct recorded transforms for this door → room face, best first.</summary>
        public List<CompatibleRoom> GetProvenMatches(
            ushort portalEnvId, ushort portalCS, ushort portalPolyId,
            ushort roomEnvId, ushort roomCS, ushort roomPolyId) {
            var result = new List<CompatibleRoom>();
            if (!_index.TryGetValue((portalEnvId, portalCS, portalPolyId), out var list))
                return result;

            var seen = new HashSet<(int, int, int, int)>();
            foreach (var r in list
                .Where(r => !r.IsGeometryDerived && r.EnvId == roomEnvId && r.CellStruct == roomCS
                    && (roomPolyId == 0 || r.PolyId == roomPolyId))
                .OrderByDescending(r => r.ExactMatch)
                .ThenByDescending(r => r.EdgeQuality > 0f ? r.EdgeQuality : r.Count)
                .ThenByDescending(r => r.Count)) {
                var key = (
                    (int)MathF.Round(r.RelOffset.X * 5f),
                    (int)MathF.Round(r.RelOffset.Y * 5f),
                    (int)MathF.Round(r.RelOffset.Z * 5f),
                    (int)MathF.Round(r.RelRot.W * 20f));
                if (!seen.Add(key)) continue;
                result.Add(r);
                if (result.Count >= 4) break;
            }
            return result;
        }

        /// <summary>Get all unique (envId, cellStruct) room types compatible with a portal.</summary>
        public HashSet<(ushort envId, ushort cs)> GetCompatibleRoomTypes(ushort envId, ushort cellStruct, ushort polyId) {
            if (!_index.TryGetValue((envId, cellStruct, polyId), out var list))
                return new HashSet<(ushort, ushort)>();
            return list.Select(r => (r.EnvId, r.CellStruct)).ToHashSet();
        }

        /// <summary>Get all unique room types compatible with ANY of the given portal faces.</summary>
        public HashSet<(ushort envId, ushort cs)> GetCompatibleRoomTypesForAny(
            IEnumerable<(ushort envId, ushort cs, ushort polyId)> portals) {
            var result = new HashSet<(ushort, ushort)>();
            foreach (var p in portals) {
                if (_index.TryGetValue(p, out var list)) {
                    foreach (var r in list)
                        result.Add((r.EnvId, r.CellStruct));
                }
            }
            return result;
        }

        public int PortalFaceCount => _index.Count;

        /// <summary>
        /// Enrich the index with geometry-matched rooms from the catalog.
        /// For each portal face already in the index, find catalog rooms whose portal
        /// dimensions match within tolerance. These entries use geometric snap at placement
        /// time (IsGeometryDerived = true) rather than proven RelOffset/RelRot.
        /// This dramatically expands the candidate pool — proven edges only record pairings
        /// that existed in real dungeons, but many rooms with identical portal geometry
        /// are fully interchangeable.
        /// </summary>
        public int EnrichWithGeometryMatches(List<CatalogRoom> catalog) {
            const float DimTolerance = 0.20f;
            int added = 0;

            var portalDimLookup = new Dictionary<(ushort envId, ushort cs, ushort polyId), (float w, float h)>();
            foreach (var room in catalog) {
                if (room.PortalDimensions == null) continue;
                foreach (var pd in room.PortalDimensions)
                    portalDimLookup[(room.EnvId, room.CellStruct, pd.PolyId)] = (pd.Width, pd.Height);
            }

            var catalogByRoom = catalog
                .Where(c => c.PortalPolyIds != null && c.PortalPolyIds.Count > 0)
                .ToDictionary(c => (c.EnvId, c.CellStruct), c => c);

            foreach (var faceKey in _index.Keys.ToList()) {
                if (!portalDimLookup.TryGetValue(faceKey, out var faceDim))
                    continue;
                if (faceDim.w < 0.5f && faceDim.h < 0.5f) continue;

                var existingRoomTypes = _index[faceKey]
                    .Select(r => (r.EnvId, r.CellStruct)).ToHashSet();

                foreach (var room in catalog) {
                    if (room.PortalPolyIds == null || room.PortalPolyIds.Count < 2)
                        continue;
                    if (existingRoomTypes.Contains((room.EnvId, room.CellStruct)))
                        continue;

                    ushort bestPoly = 0;
                    float bestScore = 0f;
                    foreach (var pid in room.PortalPolyIds) {
                        if (!portalDimLookup.TryGetValue((room.EnvId, room.CellStruct, pid), out var roomDim))
                            continue;
                        if (roomDim.w < 0.5f && roomDim.h < 0.5f) continue;

                        float wRatio = MathF.Min(faceDim.w, roomDim.w) / MathF.Max(faceDim.w, roomDim.w);
                        float hRatio = MathF.Min(faceDim.h, roomDim.h) / MathF.Max(faceDim.h, roomDim.h);
                        if (wRatio < (1f - DimTolerance) || hRatio < (1f - DimTolerance))
                            continue;

                        float score = (wRatio + hRatio) * 0.5f;
                        if (score > bestScore) {
                            bestScore = score;
                            bestPoly = pid;
                        }
                    }

                    if (bestPoly == 0) continue;

                    int vpc = room.VerifiedPortalCount > 0 ? room.VerifiedPortalCount : room.PortalCount;
                    _index[faceKey].Add(new CompatibleRoom {
                        EnvId = room.EnvId,
                        CellStruct = room.CellStruct,
                        PolyId = bestPoly,
                        Count = Math.Max(1, room.UsageCount / 4),
                        EdgeQuality = bestScore * 0.6f,
                        IsGeometryDerived = true,
                        PortalWidth = portalDimLookup.TryGetValue((room.EnvId, room.CellStruct, bestPoly), out var pd2) ? pd2.w : 0,
                        PortalHeight = pd2.h,
                        PortalArea = pd2.w * pd2.h,
                    });
                    existingRoomTypes.Add((room.EnvId, room.CellStruct));
                    added++;
                }
            }

            if (added > 0)
                Console.WriteLine($"[PortalIndex] Enriched with {added} geometry-matched room(s)");
            return added;
        }
    }
}
