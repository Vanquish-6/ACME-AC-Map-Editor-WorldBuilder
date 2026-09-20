using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Acme.Dat;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    public static class DungeonKnowledgeBuilder {
        private sealed class RoomUsageAggregate {
            public int Count;
            public HashSet<string> DungeonNames = new();
            public List<ushort> SampleSurfaces = new();
            public int RestrictedInstances;
            public int VisibleCellsSum;
            public int OutsidePortals;
            public int TotalPortals;
        }

        private static readonly string KnowledgePath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "ACME WorldBuilder", "dungeon_knowledge.json");

        private static readonly object CacheLock = new();
        private static DungeonKnowledgeBase? _memoryCache;

        public static DungeonKnowledgeBase? LoadCached() {
            lock (CacheLock) {
                if (_memoryCache != null)
                    return _memoryCache;
            try {
                if (!File.Exists(KnowledgePath)) return null;
                var json = File.ReadAllText(KnowledgePath);
                var kb = JsonSerializer.Deserialize<DungeonKnowledgeBase>(json, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                if (kb != null && kb.Version < DungeonKnowledgeBase.CurrentVersion) {
                    Console.WriteLine($"[DungeonKnowledge] Stale knowledge base (v{kb.Version}, need v{DungeonKnowledgeBase.CurrentVersion}) — deleting for rebuild");
                    try { File.Delete(KnowledgePath); } catch { }
                    return null;
                }
                    _memoryCache = kb;
                return kb;
            }
            catch { return null; }
            }
        }

        /// <summary>True if the cached KB exists and is up to date. Does not parse the 37MB file.</summary>
        public static bool IsCachedValid() {
            lock (CacheLock) {
                if (_memoryCache != null)
                    return _memoryCache.Version >= DungeonKnowledgeBase.CurrentVersion && _memoryCache.Prefabs.Count >= 100;
            }
            try {
                if (!File.Exists(KnowledgePath)) return false;
                using var reader = new StreamReader(KnowledgePath);
                var head = new System.Text.StringBuilder();
                for (int i = 0; i < 12 && !reader.EndOfStream; i++)
                    head.AppendLine(reader.ReadLine());
                var text = head.ToString();
                const string marker = "\"version\"";
                int at = text.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0) return false;
                int colon = text.IndexOf(':', at + marker.Length);
                if (colon < 0) return false;
                int start = colon + 1;
                while (start < text.Length && (text[start] == ' ' || text[start] == '\r' || text[start] == '\n'))
                    start++;
                int end = start;
                while (end < text.Length && char.IsDigit(text[end]))
                    end++;
                if (end == start) return false;
                return int.TryParse(text[start..end], out int version) && version >= DungeonKnowledgeBase.CurrentVersion;
            }
            catch { return false; }
        }

        public static void Save(DungeonKnowledgeBase kb) {
            try {
                var dir = Path.GetDirectoryName(KnowledgePath);
                if (dir != null) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(kb, new JsonSerializerOptions {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.WriteAllText(KnowledgePath, json);
                lock (CacheLock) {
                    _memoryCache = kb;
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[DungeonKnowledge] Save error: {ex.Message}");
            }
        }

        public static DungeonKnowledgeBase Build(IDatReaderWriter dats) {
            Console.WriteLine("[DungeonKnowledge] Building knowledge base from DAT...");

            var edgeCounts = new Dictionary<string, (AdjacencyEdge edge, int count)>();
            var allPrefabs = new List<DungeonPrefab>();
            var prefabSignatures = new Dictionary<string, int>();
            var roomUsage = new Dictionary<(ushort envId, ushort cs), RoomUsageAggregate>();

            // Collect static objects per room type across all dungeons
            var roomStaticsRaw = new Dictionary<(ushort envId, ushort cs), List<List<(uint id, Vector3 pos, Quaternion rot)>>>();

            // Collect full dungeon graph structures as templates
            var templates = new List<DungeonTemplate>();

            var lbiIds = GetDungeonLandblockIds(dats);
            Console.WriteLine($"[DungeonKnowledge] Scanning {lbiIds.Length} dungeon landblocks...");

            var dungeonNameLookup = new Dictionary<ushort, string>();
            foreach (var entry in Lib.LocationDatabase.Dungeons) {
                if (!dungeonNameLookup.ContainsKey(entry.LandblockId) && !string.IsNullOrEmpty(entry.Name?.Trim()))
                    dungeonNameLookup[entry.LandblockId] = entry.Name.Trim();
            }

            int dungeonsScanned = 0;
            int totalLbiIds = lbiIds.Length;
            var scannedLandblocks = new HashSet<ushort>();
            for (int li = 0; li < totalLbiIds; li++) {
                var lbiId = lbiIds[li];
                if (!dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) continue;
                bool hasBuildings = lbi.Buildings != null && lbi.Buildings.Count > 0;
                int buildingCount = lbi.Buildings?.Count ?? 0;
                uint lbId = lbiId >> 16;

                var cells = new Dictionary<ushort, EnvCell>();
                for (uint i = 0; i < lbi.NumCells; i++) {
                    ushort cellNum = (ushort)(0x0100 + i);
                    uint cellId = (lbId << 16) | cellNum;
                    if (dats.TryGet<EnvCell>(cellId, out var ec))
                        cells[cellNum] = ec;
                }

                if (cells.Count < 2) continue;
                dungeonsScanned++;

                var lbKey = (ushort)(lbId & 0xFFFF);
                scannedLandblocks.Add(lbKey);
                dungeonNameLookup.TryGetValue(lbKey, out var dungeonName);
                AccumulateRoomUsageAndStatics(cells, dungeonName, roomUsage, roomStaticsRaw);

                ExtractAdjacencyEdges(cells, edgeCounts, dats);

                // Include building-interior landblocks as well so provenance and
                // landblock-level analysis reflect all dungeon sources.
                if (cells.Count <= 120) {
                    ExtractPrefabs(cells, dats, lbKey, dungeonName ?? "", hasBuildings, buildingCount, allPrefabs, prefabSignatures);
                }

                if (cells.Count >= 3 && cells.Count <= 60 && templates.Count < 2000) {
                    var tmpl = ExtractTemplate(cells, lbKey, dungeonName, hasBuildings, buildingCount, dats);
                    if (tmpl != null) templates.Add(tmpl);
                }

                if (dungeonsScanned % 500 == 0)
                    Console.WriteLine($"[DungeonKnowledge] Progress: {dungeonsScanned} dungeons, {edgeCounts.Count} edges, {allPrefabs.Count} prefabs");
            }

            // Fallback: some dungeons are discoverable from location start-cell data even when
            // their landblock is absent from the normal LBI sweep. Include those components.
            int fallbackScanned = 0;
            var fallbackSeenComponents = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Lib.LocationDatabase.Dungeons) {
                var lbKey = entry.LandblockId;
                if (scannedLandblocks.Contains(lbKey)) continue;
                if (!TryCollectDungeonCellsFromStartCell(dats, entry.CellId, out var cells)) continue;
                if (cells.Count < 2) continue;

                var componentKey = $"{lbKey:X4}:{string.Join(',', cells.Keys.OrderBy(k => k))}";
                if (!fallbackSeenComponents.Add(componentKey)) continue;

                uint lbiId = ((uint)lbKey << 16) | 0xFFFE;
                bool hasBuildings = false;
                int buildingCount = 0;
                if (dats.TryGet<LandBlockInfo>(lbiId, out var lbi)) {
                    hasBuildings = lbi.Buildings != null && lbi.Buildings.Count > 0;
                    buildingCount = lbi.Buildings?.Count ?? 0;
                }

                var dungeonName = entry.Name?.Trim() ?? "";
                AccumulateRoomUsageAndStatics(cells, dungeonName, roomUsage, roomStaticsRaw);
                ExtractAdjacencyEdges(cells, edgeCounts, dats);
                if (cells.Count <= 120) {
                    ExtractPrefabs(cells, dats, lbKey, dungeonName, hasBuildings, buildingCount, allPrefabs, prefabSignatures);
                }
                if (cells.Count >= 3 && cells.Count <= 60 && templates.Count < 2000) {
                    var tmpl = ExtractTemplate(cells, lbKey, dungeonName, hasBuildings, buildingCount, dats);
                    if (tmpl != null) templates.Add(tmpl);
                }

                dungeonsScanned++;
                fallbackScanned++;
            }

            // Additional fallback for building-linked interiors: some dungeon entry points
            // live on building portals rather than as directly readable start cells.
            foreach (var lbiId in lbiIds) {
                if (!dats.TryGet<LandBlockInfo>(lbiId, out var lbi)) continue;
                if (lbi.Buildings == null || lbi.Buildings.Count == 0) continue;

                ushort lbKey = (ushort)((lbiId >> 16) & 0xFFFF);
                // If this landblock already produced normal dungeon-cell content,
                // no need to do the building-portal fallback.
                if (scannedLandblocks.Contains(lbKey)) continue;

                var seedCellNums = new HashSet<ushort>();
                foreach (var building in lbi.Buildings) {
                    foreach (var portal in building.Portals) {
                        if (portal.OtherCellId >= 0x0100 && portal.OtherCellId != ushort.MaxValue)
                            seedCellNums.Add(portal.OtherCellId);
                        foreach (var stabCell in portal.StabList) {
                            if (stabCell >= 0x0100 && stabCell != ushort.MaxValue)
                                seedCellNums.Add(stabCell);
                        }
                    }
                }

                if (seedCellNums.Count == 0) continue;
                if (!TryCollectDungeonCellsFromStartCells(dats, (uint)lbKey, seedCellNums, out var cells)) continue;
                if (cells.Count < 2) continue;

                var componentKey = $"{lbKey:X4}:{string.Join(',', cells.Keys.OrderBy(k => k))}";
                if (!fallbackSeenComponents.Add(componentKey)) continue;

                dungeonNameLookup.TryGetValue(lbKey, out var dungeonName);
                var resolvedName = dungeonName ?? "";
                bool hasBuildings = true;
                int buildingCount = lbi.Buildings.Count;

                AccumulateRoomUsageAndStatics(cells, resolvedName, roomUsage, roomStaticsRaw);
                ExtractAdjacencyEdges(cells, edgeCounts, dats);
                if (cells.Count <= 120) {
                    ExtractPrefabs(cells, dats, lbKey, resolvedName, hasBuildings, buildingCount, allPrefabs, prefabSignatures);
                }
                if (cells.Count >= 3 && cells.Count <= 60 && templates.Count < 2000) {
                    var tmpl = ExtractTemplate(cells, lbKey, resolvedName, hasBuildings, buildingCount, dats);
                    if (tmpl != null) templates.Add(tmpl);
                }

                dungeonsScanned++;
                fallbackScanned++;
            }
            if (fallbackScanned > 0)
                Console.WriteLine($"[DungeonKnowledge] Added {fallbackScanned} fallback dungeons from location/building start-cells");

            var edges = edgeCounts.Values
                .Select(v => { v.edge.Count = v.count; return v.edge; })
                .OrderByDescending(e => e.Count)
                .ToList();

            bool IsBuildingOnlyPrefab(DungeonPrefab prefab) {
                var allSources = prefab.GetAllSourceLandblocks().ToHashSet();
                var buildingSources = prefab.GetBuildingLinkedSourceLandblocks().ToHashSet();
                // If we don't have any source provenance, fall back to the boolean.
                if (allSources.Count == 0)
                    return prefab.SourceHasBuildings;
                // Building-only means every known source is building-linked.
                return allSources.IsSubsetOf(buildingSources);
            }

            var uniquePrefabs = allPrefabs
                .GroupBy(p => p.Signature)
                .Select(g => {
                    var best = g.OrderByDescending(x => x.Cells.Count).First();
                    best.UsageCount = g.Count();
                    // Preserve full provenance for landblock-level analysis.
                    best.SourceLandblocks = g
                        .SelectMany(p => p.GetAllSourceLandblocks())
                        .Where(lb => lb != 0)
                        .Distinct()
                        .OrderBy(lb => lb)
                        .ToList();
                    best.SourceBuildingLinkedLandblocks = g
                        .SelectMany(p => p.GetBuildingLinkedSourceLandblocks())
                        .Where(lb => lb != 0)
                        .Distinct()
                        .OrderBy(lb => lb)
                        .ToList();
                    best.SourceHasBuildings = best.SourceBuildingLinkedLandblocks.Count > 0 || g.Any(p => p.SourceHasBuildings);
                    best.SourceBuildingCount = g.Max(p => p.SourceBuildingCount);
                    if (best.SourceLandblock == 0 && best.SourceLandblocks.Count > 0)
                        best.SourceLandblock = best.SourceLandblocks[0];
                    return best;
                })
                .Where(p => !IsBuildingOnlyPrefab(p))
                .OrderByDescending(p => p.UsageCount)
                .Take(3000)
                .ToList();

            var catalog = BuildCatalog(roomUsage, dats);
            var roomStatics = BuildRoomStatics(roomStaticsRaw);
            var gridConstants = ComputeGridConstants(edges);

            PrefabNamer.NameAll(uniquePrefabs, catalog);

            // Build dead-end index: for each portal face, pre-compute which
            // verified dead-end rooms can cap it (1 portal polygon).
            var deadEndIndex = BuildDeadEndIndex(edges, dats);

            var styleThemes = ExtractStyleThemes(catalog, dats);

            Console.WriteLine($"[DungeonKnowledge] Done: {dungeonsScanned} dungeons, {edges.Count} edges, " +
                $"{uniquePrefabs.Count} prefabs, {catalog.Count} catalog rooms, " +
                $"{roomStatics.Count} room statics, {templates.Count} templates, " +
                $"{deadEndIndex.Count} dead-end entries, {styleThemes.Count} themes, " +
                $"grid H={gridConstants.h:F1} V={gridConstants.v:F1}");

            var kb = new DungeonKnowledgeBase {
                Version = DungeonKnowledgeBase.CurrentVersion,
                AnalyzedAt = DateTime.UtcNow,
                DungeonsScanned = dungeonsScanned,
                TotalEdges = edges.Count,
                TotalPrefabs = uniquePrefabs.Count,
                TotalCatalogRooms = catalog.Count,
                GridStepH = gridConstants.h,
                GridStepV = gridConstants.v,
                Edges = edges,
                Prefabs = uniquePrefabs,
                Catalog = catalog,
                RoomStatics = roomStatics,
                Templates = templates,
                DeadEndIndex = deadEndIndex,
                StyleThemes = styleThemes
            };

            Save(kb);
            return kb;
        }

        private static uint[] GetDungeonLandblockIds(IDatReaderWriter dats) {
            var lbiIds = dats.GetAllIdsOfType<LandBlockInfo>().ToArray();
            if (lbiIds.Length == 0) {
                var brute = new List<uint>();
                for (uint x = 0; x < 256; x++) {
                    for (uint y = 0; y < 256; y++) {
                        var infoId = ((x << 8) | y) << 16 | 0xFFFE;
                        if (dats.TryGet<LandBlockInfo>(infoId, out var lbi) && lbi.NumCells > 0)
                            brute.Add(infoId);
                    }
                }
                lbiIds = brute.ToArray();
            }
            return lbiIds;
        }

        private static void AccumulateRoomUsageAndStatics(
            Dictionary<ushort, EnvCell> cells,
            string? dungeonName,
            Dictionary<(ushort envId, ushort cs), RoomUsageAggregate> roomUsage,
            Dictionary<(ushort envId, ushort cs), List<List<(uint id, Vector3 pos, Quaternion rot)>>> roomStaticsRaw) {

            foreach (var (_, ec) in cells) {
                var rk = ((ushort)ec.EnvironmentId, (ushort)ec.CellStructure);
                if (!roomUsage.TryGetValue(rk, out var ru)) {
                    ru = new RoomUsageAggregate();
                    roomUsage[rk] = ru;
                }
                ru.Count++;
                if (!string.IsNullOrEmpty(dungeonName)) ru.DungeonNames.Add(dungeonName);
                var ecSurfaces = ec.Surfaces?.Select(s => (ushort)s).ToList() ?? new List<ushort>();
                if (ecSurfaces.Count > ru.SampleSurfaces.Count && ecSurfaces.Count > 0)
                    ru.SampleSurfaces = ecSurfaces;
                ru.VisibleCellsSum += ec.VisibleCells?.Count ?? 0;
                if (ec.RestrictionObj != 0) ru.RestrictedInstances++;
                if (ec.CellPortals != null) {
                    ru.TotalPortals += ec.CellPortals.Count;
                    ru.OutsidePortals += ec.CellPortals.Count(cp => cp.OtherCellId == ushort.MaxValue);
                }

                // Collect static objects for this room type instance.
                // Convert from landblock-absolute to cell-relative coordinates
                // so they can be placed correctly in generated cells at different positions.
                if (ec.StaticObjects != null && ec.StaticObjects.Count > 0) {
                    if (!roomStaticsRaw.TryGetValue(rk, out var instanceList)) {
                        instanceList = new List<List<(uint, Vector3, Quaternion)>>();
                        roomStaticsRaw[rk] = instanceList;
                    }
                    if (instanceList.Count < 20) {
                        var cellOrigin = ec.Position.Origin;
                        var cellRot = ec.Position.Orientation;
                        var invCellRot = Quaternion.Conjugate(cellRot);

                        var objs = new List<(uint id, Vector3 pos, Quaternion rot)>();
                        foreach (var stab in ec.StaticObjects) {
                            var relPos = Vector3.Transform(stab.Frame.Origin - cellOrigin, invCellRot);
                            var relRot = Quaternion.Normalize(invCellRot * stab.Frame.Orientation);
                            objs.Add((stab.Id, relPos, relRot));
                        }
                        instanceList.Add(objs);
                    }
                }
            }
        }

        private static bool TryCollectDungeonCellsFromStartCell(
            IDatReaderWriter dats,
            uint startCellId,
            out Dictionary<ushort, EnvCell> cells) {

            cells = new Dictionary<ushort, EnvCell>();
            uint lbId = startCellId >> 16;
            ushort startCellNum = (ushort)(startCellId & 0xFFFF);
            if (startCellNum == 0 || startCellNum == ushort.MaxValue) return false;
            return TryCollectDungeonCellsFromStartCells(dats, lbId, new[] { startCellNum }, out cells);
        }

        private static bool TryCollectDungeonCellsFromStartCells(
            IDatReaderWriter dats,
            uint lbId,
            IEnumerable<ushort> startCellNums,
            out Dictionary<ushort, EnvCell> cells) {

            cells = new Dictionary<ushort, EnvCell>();

            var queue = new Queue<ushort>();
            var queued = new HashSet<ushort>();
            foreach (var startCellNum in startCellNums) {
                if (startCellNum < 0x0100 || startCellNum == ushort.MaxValue) continue;
                if (queued.Add(startCellNum))
                    queue.Enqueue(startCellNum);
            }
            if (queue.Count == 0) return false;

            while (queue.Count > 0 && cells.Count < 2048) {
                var cellNum = queue.Dequeue();
                uint cellId = (lbId << 16) | cellNum;
                if (!dats.TryGet<EnvCell>(cellId, out var ec)) continue;
                if (!cells.TryAdd(cellNum, ec)) continue;

                if (ec.CellPortals == null) continue;
                foreach (var portal in ec.CellPortals) {
                    ushort other = portal.OtherCellId;
                    if (other == ushort.MaxValue || other < 0x0100) continue;
                    if (!queued.Contains(other)) {
                        queued.Add(other);
                        queue.Enqueue(other);
                    }
                }
            }

            return cells.Count > 0;
        }

        private static List<CatalogRoom> BuildCatalog(
            Dictionary<(ushort envId, ushort cs), RoomUsageAggregate> roomUsage,
            IDatReaderWriter dats) {

            var catalog = new List<CatalogRoom>();
            foreach (var (key, data) in roomUsage) {
                uint envFileId = (uint)(key.envId | 0x0D000000);
                if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(key.cs, out var cellStruct)) continue;

                var portalIds = PortalSnapper.GetPortalPolygonIds(cellStruct);
                int portalCount = portalIds.Count;

                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                if (cellStruct.VertexArray?.Vertices != null) {
                    foreach (var v in cellStruct.VertexArray.Vertices.Values) {
                        if (v.Origin.X < minX) minX = v.Origin.X; if (v.Origin.X > maxX) maxX = v.Origin.X;
                        if (v.Origin.Y < minY) minY = v.Origin.Y; if (v.Origin.Y > maxY) maxY = v.Origin.Y;
                        if (v.Origin.Z < minZ) minZ = v.Origin.Z; if (v.Origin.Z > maxZ) maxZ = v.Origin.Z;
                    }
                }
                float width = maxX - minX, depth = maxY - minY, height = maxZ - minZ;
                if (!float.IsFinite(width)) width = 0;
                if (!float.IsFinite(depth)) depth = 0;
                if (!float.IsFinite(height)) height = 0;

                string category = portalCount switch {
                    0 => "Sealed Room", 1 => "Dead End", 2 => "Corridor",
                    3 => "T-Junction", 4 => "Crossroads", _ => $"{portalCount}-Way Hub"
                };
                float maxDim = MathF.Max(width, depth);
                string size = maxDim < 10 ? "Small" : maxDim < 25 ? "Medium" : "Large";
                string style = PrefabNamer.InferStyle(string.Join(" ", data.DungeonNames));
                float restrictionRate = data.Count > 0 ? (float)data.RestrictedInstances / data.Count : 0f;
                float outsidePortalRate = data.TotalPortals > 0 ? (float)data.OutsidePortals / data.TotalPortals : 0f;
                float meanVisibleCells = data.Count > 0 ? (float)data.VisibleCellsSum / data.Count : 0f;

                // Compute portal polygon dimensions
                var portalDims = new List<PortalDimension>();
                foreach (var pid in portalIds) {
                    var geom = PortalSnapper.GetPortalGeometry(cellStruct, pid);
                    if (geom == null) continue;
                    var verts = geom.Value.Vertices;
                    if (verts.Count < 3) continue;
                    float pMinX = float.MaxValue, pMaxX = float.MinValue;
                    float pMinY = float.MaxValue, pMaxY = float.MinValue;
                    float pMinZ = float.MaxValue, pMaxZ = float.MinValue;
                    foreach (var pv in verts) {
                        if (pv.X < pMinX) pMinX = pv.X; if (pv.X > pMaxX) pMaxX = pv.X;
                        if (pv.Y < pMinY) pMinY = pv.Y; if (pv.Y > pMaxY) pMaxY = pv.Y;
                        if (pv.Z < pMinZ) pMinZ = pv.Z; if (pv.Z > pMaxZ) pMaxZ = pv.Z;
                    }
                    float pw = MathF.Max(pMaxX - pMinX, pMaxY - pMinY);
                    float ph = pMaxZ - pMinZ;
                    portalDims.Add(new PortalDimension { PolyId = pid, Width = pw, Height = ph });
                }

                catalog.Add(new CatalogRoom {
                    EnvId = key.envId, CellStruct = key.cs,
                    Category = category, Size = size, Style = style,
                    DisplayName = $"{size} {style} {category}",
                    PortalCount = portalCount,
                    VerifiedPortalCount = portalCount,
                    PortalPolyIds = new List<ushort>(portalIds),
                    UsageCount = data.Count,
                    BoundsWidth = width, BoundsDepth = depth, BoundsHeight = height,
                    SourceDungeons = data.DungeonNames.Take(5).OrderBy(n => n).ToList(),
                    SampleSurfaces = data.SampleSurfaces.Take(10).ToList(),
                    RestrictionRate = restrictionRate,
                    OutsidePortalRate = outsidePortalRate,
                    MeanVisibleCells = meanVisibleCells,
                    PortalDimensions = portalDims
                });
            }
            return catalog.OrderByDescending(r => r.UsageCount).ToList();
        }

        private static void ExtractAdjacencyEdges(
            Dictionary<ushort, EnvCell> cells,
            Dictionary<string, (AdjacencyEdge edge, int count)> edgeCounts,
            IDatReaderWriter dats) {

            foreach (var (cellNum, cell) in cells) {
                if (cell.CellPortals == null) continue;
                foreach (var portal in cell.CellPortals) {
                    if (!cells.TryGetValue(portal.OtherCellId, out var otherCell)) continue;
                    var envA = (ushort)cell.EnvironmentId;
                    var csA = (ushort)cell.CellStructure;
                    var envB = (ushort)otherCell.EnvironmentId;
                    var csB = (ushort)otherCell.CellStructure;

                    // portal.OtherPortalId from the DAT is an array index into the
                    // other cell's CellPortals/CellStruct.Portals array, NOT a polygon ID.
                    // Resolve it to the actual polygon ID for correct edge keying.
                    ushort otherPolyId = ResolveOtherPortalId(portal, otherCell, dats);

                    string key = envA < envB || (envA == envB && csA <= csB)
                        ? $"{envA:X4}_{csA}_{portal.PolygonId:X4}->{envB:X4}_{csB}_{otherPolyId:X4}"
                        : $"{envB:X4}_{csB}_{otherPolyId:X4}->{envA:X4}_{csA}_{portal.PolygonId:X4}";

                    if (!edgeCounts.TryGetValue(key, out var existing)) {
                        var invCellRot = Quaternion.Conjugate(cell.Position.Orientation);
                        var relOffset = Vector3.Transform(otherCell.Position.Origin - cell.Position.Origin, invCellRot);
                        var relRot = Quaternion.Normalize(invCellRot * otherCell.Position.Orientation);

                        var edge = new AdjacencyEdge {
                            EnvIdA = envA, CellStructA = csA, PolyIdA = (ushort)portal.PolygonId,
                            EnvIdB = envB, CellStructB = csB, PolyIdB = otherPolyId,
                            RelOffsetX = relOffset.X, RelOffsetY = relOffset.Y, RelOffsetZ = relOffset.Z,
                            RelRotX = relRot.X, RelRotY = relRot.Y, RelRotZ = relRot.Z, RelRotW = relRot.W,
                            RestrictionA = cell.RestrictionObj != 0,
                            RestrictionB = otherCell.RestrictionObj != 0,
                            VisibleCellCountA = cell.VisibleCells?.Count ?? 0,
                            VisibleCellCountB = otherCell.VisibleCells?.Count ?? 0,
                        };

                        ComputeEdgePortalGeometry(dats, envA, csA, (ushort)portal.PolygonId,
                            out float wA, out float hA, out float aA, out int vcA);
                        edge.WidthA = wA; edge.HeightA = hA; edge.AreaA = aA; edge.VertexCountA = vcA;

                        ComputeEdgePortalGeometry(dats, envB, csB, otherPolyId,
                            out float wB, out float hB, out float aB, out int vcB);
                        edge.WidthB = wB; edge.HeightB = hB; edge.AreaB = aB; edge.VertexCountB = vcB;

                        // Portal flags from real data
                        bool exactMatch = ((ushort)portal.Flags & 0x0001) != 0;
                        int portalSideA = ((ushort)portal.Flags & 0x0002) != 0 ? 0 : 1;
                        edge.ExactMatch = exactMatch;
                        edge.PortalSideA = portalSideA;

                        // Find the back-portal on the other cell to get side B's portal_side
                        if (otherCell.CellPortals != null) {
                            var backPortal = otherCell.CellPortals.FirstOrDefault(
                                p => p.OtherCellId == cellNum && (ushort)p.PolygonId == otherPolyId);
                            if (backPortal != null)
                                edge.PortalSideB = ((ushort)backPortal.Flags & 0x0002) != 0 ? 0 : 1;
                        }

                        existing = (edge, 0);
                    }
                    edgeCounts[key] = (existing.edge, existing.count + 1);
                }
            }
        }

        /// <summary>
        /// Resolve CellPortal.OtherPortalId from an array index to the actual polygon ID.
        /// The AC client stores other_portal_id as an index into the other cell's portals
        /// array; this method looks up the polygon ID at that index.
        /// </summary>
        private static ushort ResolveOtherPortalId(
            CellPortal portal,
            EnvCell otherCell,
            IDatReaderWriter dats) {

            int idx = portal.OtherPortalId;

            // Strategy 1: look up the polygon ID from the other cell's CellPortals
            // (preserves the same order as CellStruct.Portals in the DAT)
            if (otherCell.CellPortals != null && idx >= 0 && idx < otherCell.CellPortals.Count) {
                return (ushort)otherCell.CellPortals[idx].PolygonId;
            }

            // Strategy 2: resolve via the other cell's CellStruct portal list
            uint otherEnvFileId = (uint)(otherCell.EnvironmentId | 0x0D000000);
            if (dats.TryGet<Acme.Dat.Environment>(otherEnvFileId, out var otherEnv) &&
                otherEnv.Cells.TryGetValue((ushort)otherCell.CellStructure, out var otherCS)) {
                var otherPortalPolys = PortalSnapper.GetPortalPolygonIds(otherCS);
                if (idx >= 0 && idx < otherPortalPolys.Count)
                    return otherPortalPolys[idx];
            }

            return (ushort)portal.OtherPortalId;
        }

        private static void ComputeEdgePortalGeometry(IDatReaderWriter dats,
            ushort envId, ushort cellStruct, ushort polyId,
            out float width, out float height, out float area, out int vertexCount) {
            width = 0; height = 0; area = 0; vertexCount = 0;
            uint envFileId = (uint)(envId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return;
            if (!env.Cells.TryGetValue(cellStruct, out var cs)) return;
            var geom = PortalSnapper.GetPortalGeometry(cs, polyId);
            if (geom == null) return;

            var verts = geom.Value.Vertices;
            vertexCount = verts.Count;
            if (verts.Count < 3) return;

            // Area via cross product
            var cross = Vector3.Zero;
            for (int i = 1; i < verts.Count - 1; i++)
                cross += Vector3.Cross(verts[i] - verts[0], verts[i + 1] - verts[0]);
            area = cross.Length() * 0.5f;

            // Bounding box for width/height
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var v in verts) {
                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
            }
            width = MathF.Max(maxX - minX, maxY - minY);
            height = maxZ - minZ;
        }

        private static void ExtractPrefabs(
            Dictionary<ushort, EnvCell> cells,
            IDatReaderWriter dats,
            ushort sourceLandblock,
            string dungeonName,
            bool sourceHasBuildings,
            int sourceBuildingCount,
            List<DungeonPrefab> allPrefabs,
            Dictionary<string, int> prefabSignatures) {

            if (cells.Count < 2 || cells.Count > 120) return;
            if (allPrefabs.Count > 200000) return;

            var adj = new Dictionary<ushort, List<(ushort neighbor, ushort myPoly, ushort theirPoly)>>();
            foreach (var (cellNum, cell) in cells) {
                adj[cellNum] = new List<(ushort, ushort, ushort)>();
                if (cell.CellPortals == null) continue;
                foreach (var portal in cell.CellPortals) {
                    if (!cells.TryGetValue(portal.OtherCellId, out var otherCell)) continue;
                    ushort theirPoly = ResolveOtherPortalId(portal, otherCell, dats);
                    adj[cellNum].Add((portal.OtherCellId, (ushort)portal.PolygonId, theirPoly));
                }
            }

            int perDungeonCount = 0;
            int perDungeonCap = 50;

            foreach (var (startCell, _) in cells) {
                if (!adj.TryGetValue(startCell, out var neighbors)) continue;
                if (perDungeonCount >= perDungeonCap) break;

                foreach (var (n1, _, _) in neighbors) {
                    if (perDungeonCount >= perDungeonCap) break;
                    TryAddPrefab(cells, dats, adj, new[] { startCell, n1 }, sourceLandblock, dungeonName,
                        sourceHasBuildings, sourceBuildingCount, allPrefabs, prefabSignatures, 20, ref perDungeonCount);

                    if (!adj.TryGetValue(n1, out var n1Neighbors)) continue;
                    foreach (var (n2, _, _) in n1Neighbors) {
                        if (n2 == startCell) continue;
                        if (perDungeonCount >= perDungeonCap) break;
                        TryAddPrefab(cells, dats, adj, new[] { startCell, n1, n2 }, sourceLandblock, dungeonName,
                            sourceHasBuildings, sourceBuildingCount, allPrefabs, prefabSignatures, 10, ref perDungeonCount);
                    }
                }
            }

            foreach (var (startCell, _) in cells) {
                if (!adj.TryGetValue(startCell, out var neighbors)) continue;
                if (perDungeonCount >= perDungeonCap) break;

                foreach (var (n1, _, _) in neighbors) {
                    if (perDungeonCount >= perDungeonCap) break;
                    var chain = new List<ushort> { startCell, n1 };
                    var visited = new HashSet<ushort> { startCell, n1 };

                    for (int step = 0; step < 4 && perDungeonCount < perDungeonCap; step++) {
                        var last = chain[^1];
                        if (!adj.TryGetValue(last, out var lastNeighbors)) break;

                        var next = lastNeighbors.FirstOrDefault(n => !visited.Contains(n.neighbor));
                        if (next.neighbor == 0 && !cells.ContainsKey(0)) break;
                        if (visited.Contains(next.neighbor)) break;

                        chain.Add(next.neighbor);
                        visited.Add(next.neighbor);

                        if (chain.Count >= 4) {
                            TryAddPrefab(cells, dats, adj, chain.ToArray(), sourceLandblock, dungeonName,
                                sourceHasBuildings, sourceBuildingCount, allPrefabs, prefabSignatures, 5, ref perDungeonCount);
                        }
                    }
                }
            }

            if (cells.Count >= 5 && cells.Count <= 15) {
                var allCellNums = cells.Keys.ToArray();
                TryAddPrefab(cells, dats, adj, allCellNums, sourceLandblock, dungeonName,
                    sourceHasBuildings, sourceBuildingCount, allPrefabs, prefabSignatures, 1, ref perDungeonCount);
            }
        }

        private static void TryAddPrefab(
            Dictionary<ushort, EnvCell> cells, IDatReaderWriter dats,
            Dictionary<ushort, List<(ushort neighbor, ushort myPoly, ushort theirPoly)>> adj,
            ushort[] cellNums, ushort sourceLandblock, string dungeonName,
            bool sourceHasBuildings, int sourceBuildingCount,
            List<DungeonPrefab> allPrefabs, Dictionary<string, int> prefabSignatures,
            int maxSigCount, ref int perDungeonCount) {

            if (allPrefabs.Count > 200000) return;
            var prefab = BuildPrefab(cells, dats, adj, cellNums, sourceLandblock);
            if (prefab == null) return;
            prefab.SourceDungeonName = dungeonName;
            prefab.SourceHasBuildings = sourceHasBuildings;
            prefab.SourceBuildingCount = sourceBuildingCount;
            if (sourceHasBuildings && sourceLandblock != 0 && !prefab.SourceBuildingLinkedLandblocks.Contains(sourceLandblock))
                prefab.SourceBuildingLinkedLandblocks.Add(sourceLandblock);

            prefabSignatures.TryGetValue(prefab.Signature, out var sigCount);
            if (sigCount < maxSigCount) {
                allPrefabs.Add(prefab);
                prefabSignatures[prefab.Signature] = sigCount + 1;
                perDungeonCount++;
            }
        }

        private static DungeonPrefab? BuildPrefab(
            Dictionary<ushort, EnvCell> allCells,
            IDatReaderWriter dats,
            Dictionary<ushort, List<(ushort neighbor, ushort myPoly, ushort theirPoly)>> adj,
            ushort[] cellNums,
            ushort sourceLandblock) {

            var cellSet = new HashSet<ushort>(cellNums);
            var firstCell = allCells[cellNums[0]];
            var originPos = firstCell.Position.Origin;
            var originRot = firstCell.Position.Orientation;
            var invRot = Quaternion.Conjugate(originRot);

            var prefab = new DungeonPrefab { SourceLandblock = sourceLandblock };
            var cellIndexMap = new Dictionary<ushort, int>();

            for (int i = 0; i < cellNums.Length; i++) {
                var cn = cellNums[i];
                var ec = allCells[cn];
                cellIndexMap[cn] = i;

                var relPos = Vector3.Transform(ec.Position.Origin - originPos, invRot);
                var relRot = Quaternion.Normalize(invRot * ec.Position.Orientation);
                int portalCount = ec.CellPortals?.Count ?? 0;

                prefab.Cells.Add(new PrefabCell {
                    LocalIndex = i,
                    EnvId = (ushort)ec.EnvironmentId,
                    CellStruct = (ushort)ec.CellStructure,
                    PortalCount = portalCount,
                    OffsetX = relPos.X, OffsetY = relPos.Y, OffsetZ = relPos.Z,
                    RotX = relRot.X, RotY = relRot.Y, RotZ = relRot.Z, RotW = relRot.W,
                    Surfaces = ec.Surfaces?.Select(s => (ushort)s).ToList() ?? new List<ushort>()
                });
            }

            foreach (var cn in cellNums) {
                if (!adj.TryGetValue(cn, out var neighbors)) continue;
                int myIdx = cellIndexMap[cn];
                var ec = allCells[cn];

                foreach (var (neighbor, myPoly, theirPoly) in neighbors) {
                    if (cellSet.Contains(neighbor)) {
                        int otherIdx = cellIndexMap[neighbor];
                        if (myIdx < otherIdx) {
                            prefab.InternalPortals.Add(new PrefabPortal {
                                CellIndexA = myIdx, PolyIdA = myPoly,
                                CellIndexB = otherIdx, PolyIdB = theirPoly
                            });
                        }
                    }
                    else {
                        float nx = 0, ny = 0, nz = 0;
                        uint envFileId = (uint)(ec.EnvironmentId | 0x0D000000);
                        if (dats.TryGet<Acme.Dat.Environment>(envFileId, out var env) &&
                            env.Cells.TryGetValue((ushort)ec.CellStructure, out var cs)) {
                            var geom = PortalSnapper.GetPortalGeometry(cs, myPoly);
                            if (geom != null) {
                                var cellRot = Quaternion.Normalize(new Quaternion(
                                    prefab.Cells[myIdx].RotX, prefab.Cells[myIdx].RotY,
                                    prefab.Cells[myIdx].RotZ, prefab.Cells[myIdx].RotW));
                                var worldNormal = Vector3.Transform(geom.Value.Normal, cellRot);
                                nx = worldNormal.X; ny = worldNormal.Y; nz = worldNormal.Z;
                            }
                        }

                        prefab.OpenFaces.Add(new PrefabOpenFace {
                            CellIndex = myIdx, PolyId = myPoly,
                            EnvId = (ushort)ec.EnvironmentId,
                            CellStruct = (ushort)ec.CellStructure,
                            NormalX = nx, NormalY = ny, NormalZ = nz
                        });
                    }
                }
            }

            var sig = string.Join("|",
                prefab.Cells.OrderBy(c => c.EnvId).ThenBy(c => c.CellStruct)
                    .Select(c => $"{c.EnvId:X4}_{c.CellStruct}")) +
                $"_P{prefab.InternalPortals.Count}_O{prefab.OpenFaces.Count}";
            prefab.Signature = sig;

            if (prefab.Cells.Count < 2) return null;
            return prefab;
        }

        /// <summary>
        /// Consolidate per-room-type static object observations into a representative set.
        /// For each room type, pick the object placements that appear across the most instances.
        /// </summary>
        private static List<RoomStaticSet> BuildRoomStatics(
            Dictionary<(ushort envId, ushort cs), List<List<(uint id, Vector3 pos, Quaternion rot)>>> raw) {

            var result = new List<RoomStaticSet>();
            foreach (var (key, instances) in raw) {
                if (instances.Count == 0) continue;

                // Pick the instance with the most objects as representative
                var best = instances.OrderByDescending(i => i.Count).First();
                if (best.Count == 0) continue;

                var set = new RoomStaticSet { EnvId = key.envId, CellStruct = key.cs };
                foreach (var (id, pos, rot) in best) {
                    // Count how many other instances also have this object ID
                    int freq = instances.Count(inst => inst.Any(o => o.id == id));
                    set.Placements.Add(new StaticPlacement {
                        ObjectId = id,
                        X = pos.X, Y = pos.Y, Z = pos.Z,
                        RotX = rot.X, RotY = rot.Y, RotZ = rot.Z, RotW = rot.W,
                        Frequency = freq
                    });
                }
                // Only keep objects that appear in at least 30% of instances
                int threshold = Math.Max(1, instances.Count * 3 / 10);
                set.Placements.RemoveAll(p => p.Frequency < threshold);
                if (set.Placements.Count > 0)
                    result.Add(set);
            }
            Console.WriteLine($"[DungeonKnowledge] Built static sets for {result.Count} room types ({result.Sum(s => s.Placements.Count)} total objects)");
            return result;
        }

        /// <summary>
        /// Extract a complete dungeon blueprint -- positions, orientations, portal
        /// connections, surfaces, and graph structure. This is enough data to
        /// reconstruct the dungeon exactly or to re-skin it with different room types.
        /// </summary>
        private static DungeonTemplate? ExtractTemplate(
            Dictionary<ushort, EnvCell> cells, ushort sourceLandblock, string dungeonName,
            bool sourceHasBuildings, int sourceBuildingCount,
            IDatReaderWriter dats) {

            var cellNums = cells.Keys.OrderBy(k => k).ToList();
            var indexMap = new Dictionary<ushort, int>();
            for (int i = 0; i < cellNums.Count; i++)
                indexMap[cellNums[i]] = i;

            var adj = new Dictionary<int, List<int>>();
            for (int i = 0; i < cellNums.Count; i++) adj[i] = new List<int>();

            var connections = new List<TemplateConnection>();
            var drawnEdges = new HashSet<(int, int)>();

            foreach (var (cn, ec) in cells) {
                if (ec.CellPortals == null) continue;
                int myIdx = indexMap[cn];
                foreach (var portal in ec.CellPortals) {
                    if (!indexMap.TryGetValue(portal.OtherCellId, out int otherIdx)) continue;
                    if (!adj[myIdx].Contains(otherIdx)) adj[myIdx].Add(otherIdx);

                    var edgeKey = (Math.Min(myIdx, otherIdx), Math.Max(myIdx, otherIdx));
                    if (drawnEdges.Add(edgeKey)) {
                        ushort resolvedOtherPoly = (ushort)portal.OtherPortalId;
                        if (cells.TryGetValue(portal.OtherCellId, out var otherCellForResolve))
                            resolvedOtherPoly = ResolveOtherPortalId(portal, otherCellForResolve, dats);
                        connections.Add(new TemplateConnection {
                            NodeA = myIdx,
                            PolyIdA = (ushort)portal.PolygonId,
                            NodeB = otherIdx,
                            PolyIdB = resolvedOtherPoly
                        });
                    }
                }
            }

            // BFS to compute depth and detect graph type
            var visited = new HashSet<int>();
            var queue = new Queue<(int node, int depth)>();
            queue.Enqueue((0, 0));
            visited.Add(0);
            int maxDepth = 0;
            int branchCount = 0;

            while (queue.Count > 0) {
                var (node, depth) = queue.Dequeue();
                if (depth > maxDepth) maxDepth = depth;
                foreach (var n in adj[node]) {
                    if (visited.Add(n)) queue.Enqueue((n, depth + 1));
                }
            }

            foreach (var (_, neighbors) in adj) {
                if (neighbors.Count >= 3) branchCount++;
            }

            bool hasCycles = cells.Values.Sum(c => c.CellPortals?.Count ?? 0) / 2 > cellNums.Count - 1;
            string graphType = hasCycles ? "Complex" : branchCount == 0 ? "Linear" : "Tree";

            // Store positions relative to the first cell so templates are origin-independent
            var firstCell = cells[cellNums[0]];
            var originPos = firstCell.Position.Origin;
            var originRot = firstCell.Position.Orientation;
            var invRot = Quaternion.Conjugate(originRot);

            var nodes = new List<TemplateNode>();
            for (int i = 0; i < cellNums.Count; i++) {
                var ec = cells[cellNums[i]];
                int degree = adj[i].Count;
                string role = degree == 0 ? "Isolated" : degree == 1 ? "DeadEnd" : degree == 2 ? "Corridor" : "Junction";
                if (i == 0) role = "Entry";

                var relPos = Vector3.Transform(ec.Position.Origin - originPos, invRot);
                var relRot = Quaternion.Normalize(invRot * ec.Position.Orientation);

                nodes.Add(new TemplateNode {
                    Index = i,
                    EnvId = (ushort)ec.EnvironmentId,
                    CellStruct = (ushort)ec.CellStructure,
                    PortalCount = ec.CellPortals?.Count ?? 0,
                    Role = role,
                    ConnectedTo = adj[i],
                    OffsetX = relPos.X, OffsetY = relPos.Y, OffsetZ = relPos.Z,
                    RotX = relRot.X, RotY = relRot.Y, RotZ = relRot.Z, RotW = relRot.W,
                    Surfaces = ec.Surfaces?.Select(s => (ushort)s).ToList() ?? new List<ushort>()
                });
            }

            string style = PrefabNamer.InferStyle(dungeonName);

            return new DungeonTemplate {
                SourceLandblock = sourceLandblock,
                SourceHasBuildings = sourceHasBuildings,
                SourceBuildingCount = sourceBuildingCount,
                DungeonName = dungeonName,
                Style = style,
                CellCount = cellNums.Count,
                GraphType = graphType,
                MaxDepth = maxDepth,
                BranchCount = branchCount,
                Nodes = nodes,
                Connections = connections
            };
        }

        /// <summary>
        /// Build a lookup of dead-end rooms available for each portal face.
        /// Groups edges by portal face, then filters for target rooms that are
        /// verified dead-ends (exactly 1 portal polygon in their CellStruct).
        /// </summary>
        private static List<DeadEndEntry> BuildDeadEndIndex(List<AdjacencyEdge> edges, IDatReaderWriter dats) {
            // Cache verified dead-end status per room type
            var deadEndCache = new Dictionary<(ushort envId, ushort cs), bool>();
            bool IsDeadEnd(ushort envId, ushort cs) {
                var key = (envId, cs);
                if (deadEndCache.TryGetValue(key, out var cached)) return cached;
                uint envFileId = (uint)(envId | 0x0D000000);
                bool result = false;
                if (dats.TryGet<Acme.Dat.Environment>(envFileId, out var env) &&
                    env.Cells.TryGetValue(cs, out var cellStruct)) {
                    result = PortalSnapper.GetPortalPolygonIds(cellStruct).Count == 1;
                }
                deadEndCache[key] = result;
                return result;
            }

            // Build index from A-side perspective: for portal face (envA, csA, polyA),
            // collect dead-end rooms on the B side
            var index = new Dictionary<(ushort, ushort, ushort), List<DeadEndOption>>();
            foreach (var edge in edges) {
                // Check if B is a dead-end
                if (IsDeadEnd(edge.EnvIdB, edge.CellStructB)) {
                    var faceKey = (edge.EnvIdA, edge.CellStructA, edge.PolyIdA);
                    if (!index.TryGetValue(faceKey, out var list)) {
                        list = new List<DeadEndOption>();
                        index[faceKey] = list;
                    }
                    list.Add(new DeadEndOption {
                        EnvId = edge.EnvIdB, CellStruct = edge.CellStructB, PolyId = edge.PolyIdB,
                        Count = edge.Count,
                        RelOffsetX = edge.RelOffsetX, RelOffsetY = edge.RelOffsetY, RelOffsetZ = edge.RelOffsetZ,
                        RelRotX = edge.RelRotX, RelRotY = edge.RelRotY, RelRotZ = edge.RelRotZ, RelRotW = edge.RelRotW
                    });
                }

                // Check if A is a dead-end (reverse direction)
                if (IsDeadEnd(edge.EnvIdA, edge.CellStructA)) {
                    var faceKey = (edge.EnvIdB, edge.CellStructB, edge.PolyIdB);
                    if (!index.TryGetValue(faceKey, out var list)) {
                        list = new List<DeadEndOption>();
                        index[faceKey] = list;
                    }
                    var invRot = Quaternion.Inverse(new Quaternion(edge.RelRotX, edge.RelRotY, edge.RelRotZ, edge.RelRotW));
                    var invOffset = Vector3.Transform(-new Vector3(edge.RelOffsetX, edge.RelOffsetY, edge.RelOffsetZ), invRot);
                    list.Add(new DeadEndOption {
                        EnvId = edge.EnvIdA, CellStruct = edge.CellStructA, PolyId = edge.PolyIdA,
                        Count = edge.Count,
                        RelOffsetX = invOffset.X, RelOffsetY = invOffset.Y, RelOffsetZ = invOffset.Z,
                        RelRotX = invRot.X, RelRotY = invRot.Y, RelRotZ = invRot.Z, RelRotW = invRot.W
                    });
                }
            }

            var result = new List<DeadEndEntry>();
            foreach (var (faceKey, options) in index) {
                result.Add(new DeadEndEntry {
                    EnvId = faceKey.Item1, CellStruct = faceKey.Item2, PolyId = faceKey.Item3,
                    Options = options.OrderByDescending(o => o.Count).Take(5).ToList()
                });
            }

            Console.WriteLine($"[DungeonKnowledge] Dead-end index: {result.Count} portal faces with dead-end options, " +
                $"{deadEndCache.Count(kv => kv.Value)} verified dead-end room types");
            return result;
        }

        /// <summary>
        /// Compute the most common horizontal and vertical grid step from edge offsets.
        /// </summary>
        private static readonly HashSet<PixelFormat> GpuFriendlyFormats = new() {
            PixelFormat.PFID_A8R8G8B8,
            PixelFormat.PFID_R8G8B8,
            PixelFormat.PFID_INDEX16,
            PixelFormat.PFID_DXT1,
            PixelFormat.PFID_DXT3,
            PixelFormat.PFID_DXT5,
        };

        private static bool IsSurfaceRenderable(IDatReaderWriter dats, ushort surfaceId) {
            uint fullId = (uint)(surfaceId | 0x08000000);
            if (!dats.TryGet<Surface>(fullId, out var surface)) return false;
            if (surface.Type.HasFlag(SurfaceType.Base1Solid)) return true;
            if (!dats.TryGet<SurfaceTexture>(surface.OrigTextureId, out var surfTex)) return false;
            if (surfTex.Textures == null || surfTex.Textures.Count == 0) return false;
            var rsId = surfTex.Textures.Last();
            if (!dats.TryGet<RenderSurface>(rsId, out var rs)) return false;
            return GpuFriendlyFormats.Contains(rs.FormatEnum);
        }

        private static List<StyleTheme> ExtractStyleThemes(List<CatalogRoom> catalog, IDatReaderWriter dats) {
            var themes = new List<StyleTheme>();
            var byStyle = catalog
                .Where(cr => !string.IsNullOrEmpty(cr.Style) && cr.SampleSurfaces.Count >= 2)
                .GroupBy(cr => cr.Style, StringComparer.OrdinalIgnoreCase);

            foreach (var group in byStyle) {
                var wallFreq = new Dictionary<ushort, int>();
                var floorFreq = new Dictionary<ushort, int>();
                int sampleCount = 0;

                foreach (var room in group) {
                    ushort wall = room.SampleSurfaces[0];
                    ushort floor = room.SampleSurfaces[1];
                    wallFreq.TryGetValue(wall, out int wc);
                    wallFreq[wall] = wc + 1;
                    floorFreq.TryGetValue(floor, out int fc);
                    floorFreq[floor] = fc + 1;
                    sampleCount++;
                }

                if (sampleCount < 3) continue;

                // Pick the most common surface that resolves to a GPU-friendly format.
                // INDEX16 and other palette-based formats often fail to upload to GPU.
                var topWall = wallFreq.OrderByDescending(kv => kv.Value)
                    .FirstOrDefault(kv => IsSurfaceRenderable(dats, kv.Key));
                var topFloor = floorFreq.OrderByDescending(kv => kv.Value)
                    .FirstOrDefault(kv => IsSurfaceRenderable(dats, kv.Key));
                if (topWall.Value == 0 || topFloor.Value == 0) continue;

                themes.Add(new StyleTheme {
                    Name = group.Key,
                    WallSurface = topWall.Key,
                    FloorSurface = topFloor.Key,
                    SampleCount = sampleCount
                });
            }

            return themes.OrderByDescending(t => t.SampleCount).ToList();
        }

        private static (float h, float v) ComputeGridConstants(List<AdjacencyEdge> edges) {
            var hSteps = new Dictionary<float, int>();
            var vSteps = new Dictionary<float, int>();

            foreach (var e in edges) {
                float absX = MathF.Abs(e.RelOffsetX);
                float absY = MathF.Abs(e.RelOffsetY);
                float absZ = MathF.Abs(e.RelOffsetZ);

                float hStep = MathF.Max(absX, absY);
                if (hStep > 1f) {
                    float rounded = MathF.Round(hStep);
                    hSteps.TryGetValue(rounded, out int hc);
                    hSteps[rounded] = hc + e.Count;
                }
                if (absZ > 1f) {
                    float rounded = MathF.Round(absZ);
                    vSteps.TryGetValue(rounded, out int vc);
                    vSteps[rounded] = vc + e.Count;
                }
            }

            float gridH = hSteps.Count > 0 ? hSteps.OrderByDescending(kv => kv.Value).First().Key : 10f;
            float gridV = vSteps.Count > 0 ? vSteps.OrderByDescending(kv => kv.Value).First().Key : 6f;

            return (gridH, gridV);
        }
    }
}
