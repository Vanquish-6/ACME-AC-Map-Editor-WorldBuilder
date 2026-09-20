using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Acme.Dat;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    public record GeneratorParams {
        /// <summary>Target number of cells (rooms) in the generated dungeon.</summary>
        public int RoomCount { get; init; } = 10;
        public string Style { get; init; } = "All";
        public int Seed { get; init; } = 0;
        public bool AllowVertical { get; init; } = false;
        public bool LockStyle { get; init; } = true;
        /// <summary>When true, restrict generation to only prefabs whose Signature is in FavoritePrefabSignatures.</summary>
        public bool UseFavoritesOnly { get; init; } = false;
        /// <summary>Prefab signatures to use when UseFavoritesOnly is true.</summary>
        public HashSet<string>? FavoritePrefabSignatures { get; init; }
        /// <summary>Custom prefabs (not in KB) to include when resolving favorites.</summary>
        public List<DungeonPrefab>? CustomPrefabs { get; init; }
        /// <summary>0 = Linear (corridors only), 1 = Moderate (default), 2 = Heavy branching (lots of junctions).</summary>
        public int Branching { get; init; } = 1;
        /// <summary>0 = Small rooms, 1 = Mixed (default), 2 = Large rooms.</summary>
        public int RoomSize { get; init; } = 1;
        /// <summary>Custom wall surface ID (only used when Style == "Custom").</summary>
        public ushort CustomWallSurface { get; init; }
        /// <summary>Custom floor surface ID (only used when Style == "Custom").</summary>
        public ushort CustomFloorSurface { get; init; }
        /// <summary>When true, place static furnishings (torches, furniture, etc.) in generated rooms.</summary>
        public bool FurnishRooms { get; init; } = true;
    }

    /// <summary>
    /// Generates dungeons by chaining prefabs using proven portal transforms from real game data.
    /// Connections use the exact relative offsets/rotations observed in actual AC dungeons,
    /// falling back to geometric snap only when no proven data exists.
    /// </summary>
    public static class DungeonGenerator {

        private const float OverlapMinDistDefault = 6.0f;

        /// <summary>
        /// Shrink factor for AABB overlap tests. Avoids false positives from
        /// floating-point imprecision at shared portal walls. The connecting room
        /// is already excluded by index, so this only needs to cover FP tolerance.
        /// </summary>
        private const float AABBShrink = 0.25f;
        private const float MinPortalArea = 0.01f;
        // AC dungeons are authored below terrain level; keep generated dungeons
        // anchored underground so teleports/loads match expected runtime behavior.
        private const float GeneratedDungeonBaseZ = -50f;
        private const bool EnableAutoFurnish = true;

        /// <summary>
        /// Constrain a quaternion to yaw-only (Z-axis rotation). AC dungeon cells
        /// are always upright — floors flat on XY, gravity along -Z. They only rotate
        /// around Z to face N/S/E/W. Any pitch/roll from computed transforms must be
        /// stripped or rooms end up sideways.
        /// </summary>
        private static Quaternion ConstrainToYaw(Quaternion q) {
            float len = MathF.Sqrt(q.Z * q.Z + q.W * q.W);
            if (len < 0.001f) return Quaternion.Identity;
            return new Quaternion(0, 0, q.Z / len, q.W / len);
        }

        /// <summary>
        /// Verify a room is truly a dead-end by loading its CellStruct and counting
        /// actual portal polygons. The catalog's PortalCount can be stale or wrong.
        /// </summary>
        private static bool IsVerifiedDeadEnd(IDatReaderWriter dats, ushort envId, ushort cellStruct) {
            uint envFileId = (uint)(envId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return false;
            if (!env.Cells.TryGetValue(cellStruct, out var cs)) return false;
            var portalIds = PortalSnapper.GetPortalPolygonIds(cs);
            return portalIds.Count == 1;
        }

        private static float PortalSimilarityScore(PortalGeometryInfo a, PortalGeometryInfo b) {
            float areaRatio = MathF.Min(a.Area, b.Area) / MathF.Max(a.Area, b.Area);
            float widthRatio = (a.Width > 0.5f && b.Width > 0.5f)
                ? MathF.Min(a.Width, b.Width) / MathF.Max(a.Width, b.Width)
                : 1f;
            float heightRatio = (a.Height > 0.5f && b.Height > 0.5f)
                ? MathF.Min(a.Height, b.Height) / MathF.Max(a.Height, b.Height)
                : 1f;
            float vertexScore = a.VertexCount == b.VertexCount ? 1f : 0.9f;
            return areaRatio * 0.6f + widthRatio * 0.25f + heightRatio * 0.1f + vertexScore * 0.05f;
        }

        private static float CompatibleRoomScore(CompatibleRoom cr) {
            float score = cr.EdgeQuality > 0f ? cr.EdgeQuality : MathF.Max(1f, cr.Count);
            if (cr.ExactMatch) score *= 1.03f;
            if (cr.TargetOutsidePortalRate > 0.25f) score *= 0.88f;
            if (cr.TargetRestrictionRate > 0.35f) score *= 0.90f;
            return score;
        }

        public static DungeonDocument? Generate(
            GeneratorParams p,
            List<RoomEntry> availableRooms,
            IDatReaderWriter dats,
            ushort landblockKey) {

            // Auto-retry: if growth stalls badly (< 60% of target), try again with
            // a different seed. This avoids presenting users with stunted dungeons
            // caused by a poor starter or unlucky overlap cascade.
            const int MaxRetries = 3;
            const float AcceptableRatio = 0.60f;

            DungeonDocument? bestDoc = null;
            int bestCells = 0;

            for (int attempt = 0; attempt < MaxRetries; attempt++) {
                var doc = GenerateOnce(p, availableRooms, dats, landblockKey, attempt);
                if (doc == null) continue;
                int cells = doc.Cells.Count;
                if (cells > bestCells) {
                    bestDoc = doc;
                    bestCells = cells;
                }
                if (cells >= p.RoomCount * AcceptableRatio) break;
                Console.WriteLine($"[DungeonGen] Attempt {attempt + 1}: only {cells}/{p.RoomCount} cells ({100f * cells / p.RoomCount:F0}%), retrying...");
            }
            return bestDoc;
        }

        private static DungeonDocument? GenerateOnce(
            GeneratorParams p,
            List<RoomEntry> availableRooms,
            IDatReaderWriter dats,
            ushort landblockKey,
            int retryAttempt) {

            var rng = p.Seed != 0 ? new Random(p.Seed + retryAttempt) : new Random();
            int targetCells = p.RoomCount;
            string sizeTier = targetCells <= 48 ? "small"
                : targetCells <= 96 ? "medium"
                : targetCells <= 160 ? "large"
                : targetCells <= 256 ? "huge"
                : "massive";

            int maxStarterExitsByTier = sizeTier switch {
                "small" => 3,
                "medium" => 4,
                "large" => 5,
                "huge" => 6,
                _ => 7
            };
            int maxStarterCellsByTier = sizeTier switch {
                "small" => 3,
                "medium" => 4,
                "large" => 5,
                "huge" => 6,
                _ => 8
            };
            int growthAttemptMultiplier = sizeTier switch {
                "small" => 18,
                "medium" => 22,
                "large" => 26,
                "huge" => 30,
                _ => 34
            };
            int basePortalRetriesByTier = sizeTier switch {
                "small" => 14,
                "medium" => 18,
                "large" => 22,
                "huge" => 26,
                _ => 30
            };

            var kb = DungeonKnowledgeBuilder.LoadCached();
            if (kb == null || kb.Prefabs.Count == 0) {
                Console.WriteLine("[DungeonGen] No knowledge base — run Analyze Rooms first");
                return null;
            }

            var portalIndex = PortalCompatibilityIndex.Build(kb);
            var geoCache = new PortalGeometryCache(dats);

            // Pre-built lookups from the enriched KB — avoids DAT hits during generation
            var verifiedPortalCounts = new Dictionary<(ushort, ushort), int>();
            var catalogDeadEnds = new HashSet<(ushort, ushort)>();
            foreach (var cr in kb.Catalog) {
                var rk = (cr.EnvId, cr.CellStruct);
                verifiedPortalCounts[rk] = cr.VerifiedPortalCount > 0 ? cr.VerifiedPortalCount : cr.PortalCount;
                if (cr.VerifiedPortalCount == 1) catalogDeadEnds.Add(rk);
            }
            var deadEndLookup = new Dictionary<(ushort, ushort, ushort), List<DeadEndOption>>();
            foreach (var entry in kb.DeadEndIndex) {
                deadEndLookup[(entry.EnvId, entry.CellStruct, entry.PolyId)] = entry.Options;
            }
            var deadEndRoomCatalog = kb.Catalog
                .Where(cr => cr.VerifiedPortalCount == 1 && cr.PortalPolyIds != null && cr.PortalPolyIds.Count > 0)
                .ToList();
            var geometricCapCache = new Dictionary<(ushort envId, ushort cellStruct, ushort polyId), List<CompatibleRoom>>();
            var geometricGrowthCache = new Dictionary<(ushort envId, ushort cellStruct, ushort polyId), List<(CompatibleRoom room, float score, int portals)>>();

            var verticalDirs = new HashSet<string> { "Up", "Down" };

            bool PassesFilters(DungeonPrefab pf) {
                if (pf.OpenFaces.Count < 1) return false;
                if (!p.AllowVertical && pf.OpenFaceDirections.Any(d => verticalDirs.Contains(d))) return false;
                return true;
            }

            bool useFavorites = p.UseFavoritesOnly && p.FavoritePrefabSignatures is { Count: > 0 };
            var favSigs = p.FavoritePrefabSignatures;
            List<DungeonPrefab>? favPrefabs = null;
            var favoriteRoomTypes = new HashSet<(ushort envId, ushort cs)>();

            IEnumerable<DungeonPrefab> sourcePool = kb.Prefabs;
            if (useFavorites) {
                // Search KB + custom prefabs when resolving favorites
                IEnumerable<DungeonPrefab> allKnown = kb.Prefabs;
                if (p.CustomPrefabs is { Count: > 0 })
                    allKnown = allKnown.Concat(p.CustomPrefabs);
                favPrefabs = allKnown.Where(pf => favSigs!.Contains(pf.Signature)).ToList();

                // Decompose favorites into room types — this turns whole-dungeon
                // favorites into the individual corridor/chamber geometries they contain.
                var favSourceLandblocks = new HashSet<int>();
                foreach (var fav in favPrefabs) {
                    foreach (var cell in fav.Cells)
                        favoriteRoomTypes.Add((cell.EnvId, cell.CellStruct));
                    foreach (var sourceLandblock in fav.GetAllSourceLandblocks())
                        favSourceLandblocks.Add(sourceLandblock);
                }

                // Build candidate pool in tiers — prefer pieces where ALL cells use
                // favorite room types (tight match), then fall back to looser matching.
                var strictPool = kb.Prefabs.Where(pf =>
                    pf.Category != "Full Dungeon" &&
                    pf.Cells.All(c => favoriteRoomTypes.Contains((c.EnvId, c.CellStruct)))).ToList();

                // Also include pieces from the exact same source dungeons
                var sourceMatches = kb.Prefabs.Where(pf =>
                    pf.Category != "Full Dungeon" &&
                    pf.GetAllSourceLandblocks().Any(favSourceLandblocks.Contains) &&
                    !strictPool.Contains(pf)).ToList();

                var tightPool = strictPool.Concat(sourceMatches).ToList();

                if (tightPool.Count >= 15) {
                    sourcePool = tightPool;
                    Console.WriteLine($"[DungeonGen] Favorites: {favPrefabs.Count} favorited → " +
                        $"{favoriteRoomTypes.Count} room types, {favSourceLandblocks.Count} source dungeons → " +
                        $"{tightPool.Count} tight-match pieces ({strictPool.Count} all-cells + {sourceMatches.Count} source)");
                }
                else {
                    // Not enough tight matches — fall back to any-cell matching
                    sourcePool = kb.Prefabs.Where(pf =>
                        pf.Category != "Full Dungeon" &&
                        (pf.Cells.Any(c => favoriteRoomTypes.Contains((c.EnvId, c.CellStruct))) ||
                         pf.GetAllSourceLandblocks().Any(favSourceLandblocks.Contains)));
                    var looseCount = sourcePool.Count();
                    Console.WriteLine($"[DungeonGen] Favorites: {favPrefabs.Count} favorited → " +
                        $"{favoriteRoomTypes.Count} room types → tight={tightPool.Count} (too few), loose={looseCount} candidates");
                }
            }

            var candidates = sourcePool
                .Where(pf => p.Style == "All" || pf.Style.Equals(p.Style, StringComparison.OrdinalIgnoreCase))
                .Where(PassesFilters)
                .OrderByDescending(pf => pf.UsageCount)
                .Take(500)
                .ToList();

            if (candidates.Count == 0 && useFavorites) {
                Console.WriteLine($"[DungeonGen] Favorites pool produced 0 candidates after filters, relaxing filters");
                candidates = sourcePool
                    .Where(pf => pf.OpenFaces.Count >= 1)
                    .OrderByDescending(pf => pf.UsageCount)
                    .Take(500)
                    .ToList();
            }

            if (candidates.Count == 0) {
                candidates = kb.Prefabs
                    .Where(pf => pf.OpenFaces.Count >= 1)
                    .OrderByDescending(pf => pf.UsageCount)
                    .Take(500)
                    .ToList();
            }

            if (candidates.Count == 0) {
                Console.WriteLine("[DungeonGen] No suitable prefabs");
                return null;
            }

            // When style is "All" and LockStyle is on, pick the starter first then
            // lock candidates to its style so the dungeon looks consistent.
            string? lockedStyle = null;

            var connectors = candidates.Where(pf => pf.OpenFaces.Count >= 2).ToList();
            var caps = candidates.Where(pf => pf.OpenFaces.Count == 1).ToList();

            // Pick the starter. Each open exit will need a cap room at the end,
            // so exit count directly determines minimum dungeon size.
            // Limit exits to ~25% of target so caps don't dominate the room budget.
            int maxStarterExits = Math.Max(2, Math.Min(maxStarterExitsByTier, Math.Max(2, targetCells / 4)));
            int maxStarterCells = Math.Max(2, Math.Min(maxStarterCellsByTier, Math.Max(2, targetCells / 3)));
            int starterExitSoftCap = useFavorites
                ? Math.Min(3, maxStarterExits)
                : Math.Min(4, maxStarterExits);
            int starterExitRelaxedCap = useFavorites
                ? Math.Max(3, Math.Min(4, maxStarterExits + 1))
                : Math.Min(5, Math.Max(3, maxStarterExits + 1));

            DungeonPrefab starter;
            if (connectors.Count > 0) {
                var scored = connectors
                    .Where(pf => pf.OpenFaces.Count <= starterExitSoftCap && pf.Cells.Count <= maxStarterCells)
                    .Select(pf => {
                        int score = 0;
                        foreach (var of in pf.OpenFaces) {
                            var compat = portalIndex.GetCompatible(of.EnvId, of.CellStruct, of.PolyId);
                            int capOptions = deadEndLookup.TryGetValue((of.EnvId, of.CellStruct, of.PolyId), out var opts)
                                ? opts.Count
                                : 0;
                            // Score direct connectivity
                            score += compat.Count * 4;
                            score += capOptions * 10;
                            if (capOptions == 0)
                                score -= useFavorites ? 20 : 8;
                            // Bonus for 2nd-depth connectivity: rooms reachable
                            // from the rooms at this exit. High depth = rich growth.
                            int depth2 = 0;
                            foreach (var cr in compat.Take(6)) {
                                var catRoom = kb.Catalog.FirstOrDefault(c => c.EnvId == cr.EnvId && c.CellStruct == cr.CellStruct);
                                if (catRoom?.PortalPolyIds != null) {
                                    foreach (var op in catRoom.PortalPolyIds) {
                                        if (op == cr.PolyId) continue;
                                        depth2 += Math.Min(8, portalIndex.GetCompatible(cr.EnvId, cr.CellStruct, op).Count);
                                    }
                                }
                            }
                            score += depth2 * 2;
                        }
                        score -= pf.OpenFaces.Count * (useFavorites ? 80 : 50);
                        score -= pf.Cells.Count * 6;
                        return (pf, score);
                    }).OrderByDescending(x => x.score).ToList();

                // Relax: allow more exits if nothing passed
                if (scored.Count == 0) {
                    scored = connectors
                        .Where(pf => pf.OpenFaces.Count <= starterExitRelaxedCap && pf.Cells.Count <= maxStarterCells)
                        .Select(pf => {
                            int score = 0;
                            foreach (var of in pf.OpenFaces) {
                                int compatCount = portalIndex.GetCompatible(of.EnvId, of.CellStruct, of.PolyId).Count;
                                int capOptions = deadEndLookup.TryGetValue((of.EnvId, of.CellStruct, of.PolyId), out var opts)
                                    ? opts.Count
                                    : 0;
                                score += compatCount * 3;
                                score += capOptions * 8;
                                if (capOptions == 0)
                                    score -= useFavorites ? 16 : 6;
                            }
                            score -= pf.OpenFaces.Count * (useFavorites ? 70 : 40);
                            score -= pf.Cells.Count * 4;
                            return (pf, score);
                        }).OrderByDescending(x => x.score).ToList();
                }

                // Final fallback: any connector, but prefer fewer exits
                if (scored.Count == 0) {
                    scored = connectors
                        .Where(pf => !useFavorites || pf.OpenFaces.Count <= 6)
                        .Select(pf => {
                        int score = 0;
                        foreach (var of in pf.OpenFaces)
                            score += portalIndex.GetCompatible(of.EnvId, of.CellStruct, of.PolyId).Count;
                        // Penalize high exit counts heavily
                        score -= pf.OpenFaces.Count * (useFavorites ? 80 : 50);
                        return (pf, score);
                    }).OrderByDescending(x => x.score).ToList();
                    if (scored.Count == 0) {
                        // Last-resort deterministic pick: smallest connector.
                        var fallbackStarter = connectors
                            .OrderBy(pf => pf.OpenFaces.Count)
                            .ThenBy(pf => pf.Cells.Count)
                            .ThenByDescending(pf => pf.UsageCount)
                            .FirstOrDefault();
                        if (fallbackStarter != null)
                            scored.Add((fallbackStarter, 0));
                    }
                }

                int topN = Math.Max(1, scored.Count / 5);
                starter = scored[rng.Next(topN)].pf;
            }
            else {
                starter = candidates[rng.Next(candidates.Count)];
            }

            if (p.LockStyle && p.Style == "All" && !string.IsNullOrEmpty(starter.Style)) {
                lockedStyle = starter.Style;
                candidates = candidates.Where(pf =>
                    pf.Style.Equals(lockedStyle, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(pf.Style)).ToList();
                connectors = candidates.Where(pf => pf.OpenFaces.Count >= 2).ToList();
                caps = candidates.Where(pf => pf.OpenFaces.Count == 1).ToList();

                // If locking cut candidates too aggressively, pull from full pool
                if (candidates.Count < 10) {
                    lockedStyle = null;
                    candidates = kb.Prefabs
                        .Where(PassesFilters)
                        .OrderByDescending(pf => pf.UsageCount)
                        .Take(500)
                        .ToList();
                    connectors = candidates.Where(pf => pf.OpenFaces.Count >= 2).ToList();
                    caps = candidates.Where(pf => pf.OpenFaces.Count == 1).ToList();
                }
            }

            // Growth/capping room pools derived from DAT catalog room types.
            // Unlike prefab fragments, these are true single-room building blocks and
            // are critical for stable cell-by-cell expansion.
            var growthRoomCatalogWide = kb.Catalog
                .Where(cr => cr.PortalPolyIds != null && cr.PortalPolyIds.Count > 0)
                .Where(cr => (cr.VerifiedPortalCount > 0 ? cr.VerifiedPortalCount : cr.PortalCount) >= 2)
                .ToList();
            if (!string.IsNullOrEmpty(lockedStyle)) {
                growthRoomCatalogWide = growthRoomCatalogWide
                    .Where(cr => string.IsNullOrEmpty(cr.Style) ||
                        cr.Style.Equals(lockedStyle, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var growthRoomCatalogSized = growthRoomCatalogWide;
            if (p.RoomSize == 0) {
                var filtered = growthRoomCatalogWide
                    .Where(cr => cr.BoundsWidth < 15f && cr.BoundsDepth < 15f).ToList();
                if (filtered.Count >= 50) growthRoomCatalogSized = filtered;
            } else if (p.RoomSize >= 2) {
                var filtered = growthRoomCatalogWide
                    .Where(cr => cr.BoundsWidth > 8f || cr.BoundsDepth > 8f).ToList();
                if (filtered.Count >= 50) growthRoomCatalogSized = filtered;
            }

            var growthRoomCatalog = growthRoomCatalogSized;
            if (useFavorites && favoriteRoomTypes.Count > 0) {
                growthRoomCatalog = growthRoomCatalog
                    .Where(cr => favoriteRoomTypes.Contains((cr.EnvId, cr.CellStruct)))
                    .ToList();
            }

            if (growthRoomCatalog.Count < 50) {
                growthRoomCatalog = growthRoomCatalogWide;
            }

            // Cache horizontal portal counts so no-vertical generation can avoid
            // selecting rooms that only continue via Up/Down faces.
            var horizontalPortalCountByRoom = new Dictionary<(ushort envId, ushort cs), int>();
            foreach (var cr in growthRoomCatalogWide) {
                int horizontal = 0;
                foreach (var pid in cr.PortalPolyIds) {
                    var g = geoCache.Get(cr.EnvId, cr.CellStruct, pid);
                    if (g == null || MathF.Abs(g.Normal.Z) <= 0.7f)
                        horizontal++;
                }
                horizontalPortalCountByRoom[(cr.EnvId, cr.CellStruct)] = horizontal;
            }

            var prefabsByRoomType = new Dictionary<(ushort envId, ushort cs), List<DungeonPrefab>>();
            foreach (var pf in candidates) {
                foreach (var of in pf.OpenFaces) {
                    var key = (of.EnvId, of.CellStruct);
                    if (!prefabsByRoomType.TryGetValue(key, out var list)) {
                        list = new List<DungeonPrefab>();
                        prefabsByRoomType[key] = list;
                    }
                    if (!list.Contains(pf)) list.Add(pf);
                }
            }

            // Build lookups from the catalog for overlap detection and bridge preference.
            var roomBounds = new Dictionary<(ushort envId, ushort cs), float>();
            var roomPortalCounts = new Dictionary<(ushort envId, ushort cs), int>();
            foreach (var cr in kb.Catalog) {
                var key = (cr.EnvId, cr.CellStruct);
                if (!roomBounds.ContainsKey(key))
                    roomBounds[key] = MathF.Max(cr.BoundsWidth, cr.BoundsDepth) * 0.5f;
                if (!roomPortalCounts.ContainsKey(key))
                    roomPortalCounts[key] = cr.PortalCount;
            }

            bool useBridges = useFavorites;
            if (useBridges)
                Console.WriteLine($"[DungeonGen] Edge-direct bridging enabled, {roomBounds.Count} catalog rooms with bounds data");

            var favLabel = useFavorites ? $", favorites-only ({favSigs!.Count} sigs)" : "";
            Console.WriteLine($"[DungeonGen] Starter: {starter.DisplayName} ({starter.Cells.Count} cells, {starter.OpenFaces.Count} exits), style={lockedStyle ?? "mixed"}, candidates={candidates.Count} ({prefabsByRoomType.Count} room types){favLabel}, index has {portalIndex.PortalFaceCount} portal faces");
            Console.WriteLine($"[DungeonGen] Size tier: {sizeTier}, target={targetCells}, attempts-mult={growthAttemptMultiplier}, starter caps exits={maxStarterExits}/cells={maxStarterCells}");
            Console.WriteLine($"[DungeonGen] Growth room catalog: {growthRoomCatalog.Count} single-room types (wide={growthRoomCatalogWide.Count})");

            var doc = new DungeonDocument(new Microsoft.Extensions.Logging.Abstractions.NullLogger<DungeonDocument>());
            doc.SetLandblockKey(landblockKey);

            var placedCellNums = PlacePrefabAtOrigin(doc, dats, starter);
            int totalCells = placedCellNums.Count;

            var placedPositions = new List<Vector3>();
            var placedAABBs = new List<RoomAABB>();
            foreach (var cn in placedCellNums) {
                var c = doc.GetCell(cn);
                if (c != null) {
                    placedPositions.Add(c.Origin);
                    TrackPlacedAABB(placedAABBs, geoCache, c.EnvironmentId, c.CellStructure, c.Origin, c.Orientation);
                }
            }

            var frontier = new List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)>();
            CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);

            // --- PHASE 1: Growth ---
            // Target is cell count. Bridge cells don't count toward target.
            int maxAttempts = targetCells * growthAttemptMultiplier;
            int attempts = 0;
            int starterCells = totalCells;
            int contentCells = totalCells;
            int indexedPlacements = 0;
            int bridgePlacements = 0;
            int snapPlacements = 0;
            int retiredPortals = 0;
            int edgeMissCount = 0;

            var frontierFailures = new Dictionary<(ushort cellNum, ushort polyId), int>();
            int maxPortalRetries = useFavorites ? basePortalRetriesByTier + 8 : basePortalRetriesByTier;

            // Reserve headroom for cap cascades: caps placed in pass 1 can open
            // new portals needing pass 2+ caps. Typically adds 2-4 extra cells.
            int cascadeBuffer = sizeTier switch {
                "small" => Math.Max(2, targetCells / 12),
                "medium" => Math.Max(4, targetCells / 10),
                "large" => Math.Max(8, targetCells / 8),
                "huge" => Math.Max(12, targetCells / 7),
                _ => Math.Max(16, targetCells / 6)
            };

            List<CompatibleRoom> GetGeometricGrowthCandidates(
                ushort envId, ushort cellStruct, ushort polyId, int preferredMaxExits) {
                var faceKey = (envId, cellStruct, polyId);
                if (!geometricGrowthCache.TryGetValue(faceKey, out var cached)) {
                    cached = new List<(CompatibleRoom room, float score, int portals)>();
                    var targetGeo = geoCache.Get(envId, cellStruct, polyId);
                    if (targetGeo != null && targetGeo.Area > MinPortalArea) {
                        var seen = new HashSet<(ushort envId, ushort cs, ushort polyId)>();
                        foreach (var room in growthRoomCatalog) {
                            int vpc = room.VerifiedPortalCount > 0 ? room.VerifiedPortalCount : room.PortalCount;
                            if (vpc < 2) continue;
                            if (!p.AllowVertical &&
                                horizontalPortalCountByRoom.TryGetValue((room.EnvId, room.CellStruct), out int horizCount) &&
                                horizCount < 2)
                                continue;

                            float bestScore = 0f;
                            ushort bestPoly = 0;
                            foreach (var rpid in room.PortalPolyIds) {
                                var sourceGeo = geoCache.Get(room.EnvId, room.CellStruct, rpid);
                                if (sourceGeo == null || sourceGeo.Area <= MinPortalArea) continue;
                                if (!p.AllowVertical && MathF.Abs(sourceGeo.Normal.Z) > 0.7f) continue;
                                if (!geoCache.AreCompatible(envId, cellStruct, polyId, room.EnvId, room.CellStruct, rpid))
                                    continue;
                                float sim = PortalSimilarityScore(targetGeo, sourceGeo);
                                if (sim > bestScore) {
                                    bestScore = sim;
                                    bestPoly = rpid;
                                }
                            }

                            if (bestPoly == 0 || bestScore < 0.72f) continue;
                            var sig = (room.EnvId, room.CellStruct, bestPoly);
                            if (!seen.Add(sig)) continue;
                            cached.Add((
                                new CompatibleRoom {
                                    EnvId = room.EnvId,
                                    CellStruct = room.CellStruct,
                                    PolyId = bestPoly,
                                    Count = room.UsageCount
                                },
                                bestScore,
                                vpc
                            ));
                        }

                        // Favorites mode can be too sparse; inject bridge-capable non-favorite
                        // room types as a fallback while keeping favorite rooms highest rank.
                        if (useFavorites) {
                            int injected = 0;
                            foreach (var room in growthRoomCatalogWide) {
                                if (favoriteRoomTypes.Contains((room.EnvId, room.CellStruct)))
                                    continue;

                                int vpc = room.VerifiedPortalCount > 0 ? room.VerifiedPortalCount : room.PortalCount;
                                if (vpc < 2) continue;
                                if (!p.AllowVertical &&
                                    horizontalPortalCountByRoom.TryGetValue((room.EnvId, room.CellStruct), out int horizCount) &&
                                    horizCount < 2)
                                    continue;

                                float bestScore = 0f;
                                ushort bestPoly = 0;
                                foreach (var rpid in room.PortalPolyIds) {
                                    var sourceGeo = geoCache.Get(room.EnvId, room.CellStruct, rpid);
                                    if (sourceGeo == null || sourceGeo.Area <= MinPortalArea) continue;
                                    if (!p.AllowVertical && MathF.Abs(sourceGeo.Normal.Z) > 0.7f) continue;
                                    if (!geoCache.AreCompatible(envId, cellStruct, polyId, room.EnvId, room.CellStruct, rpid))
                                        continue;
                                    float sim = PortalSimilarityScore(targetGeo, sourceGeo);
                                    if (sim > bestScore) {
                                        bestScore = sim;
                                        bestPoly = rpid;
                                    }
                                }

                                // Bridge candidates should be high-confidence so they
                                // connect favorite fragments without making layouts noisy.
                                if (bestPoly == 0 || bestScore < 0.82f) continue;
                                var sig = (room.EnvId, room.CellStruct, bestPoly);
                                if (!seen.Add(sig)) continue;
                                cached.Add((
                                    new CompatibleRoom {
                                        EnvId = room.EnvId,
                                        CellStruct = room.CellStruct,
                                        PolyId = bestPoly,
                                        // Slightly down-rank bridge rooms so favorites remain preferred.
                                        Count = Math.Max(1, room.UsageCount / 2)
                                    },
                                    bestScore * 0.92f,
                                    vpc
                                ));
                                injected++;
                                if (injected >= 48) break;
                            }
                        }
                        cached = cached
                            .OrderByDescending(x => x.score)
                            .ThenByDescending(x => CompatibleRoomScore(x.room))
                            .Take(256)
                            .ToList();
                    }
                    geometricGrowthCache[faceKey] = cached;
                }

                var filtered = cached
                    .Where(x => x.portals <= preferredMaxExits)
                    .Select(x => x.room)
                    .ToList();

                if (filtered.Count == 0 && preferredMaxExits >= 2) {
                    // If strict portal budget yields nothing, allow one extra exit.
                    filtered = cached
                        .Where(x => x.portals <= preferredMaxExits + 1)
                        .Select(x => x.room)
                        .ToList();
                }
                return filtered;
            }

            while (frontier.Count > 0 && attempts < maxAttempts) {
                // Cap-aware stopping: content + expected caps (frontier) should not
                // exceed target minus cascade buffer. This leaves room for the cap
                // cascade so the final doc.Cells.Count lands close to the user's target.
                int growthRooms = contentCells - starterCells;
                if (growthRooms >= 2 && contentCells + frontier.Count >= targetCells - cascadeBuffer)
                    break;

                if (contentCells >= targetCells) break;

                attempts++;

                // Bias frontier selection toward portals that extend the dungeon
                // outward rather than filling in gaps. Prefer portals farthest from
                // the centroid -- this produces elongated layouts like real dungeons.
                int fi;
                if (placedPositions.Count >= 3 && frontier.Count >= 3) {
                    var centroid = Vector3.Zero;
                    foreach (var pp in placedPositions) centroid += pp;
                    centroid /= placedPositions.Count;

                    var scored = new List<(int idx, float dist)>();
                    for (int f = 0; f < frontier.Count; f++) {
                        var fc = doc.GetCell(frontier[f].cellNum);
                        if (fc == null) continue;
                        float dist = (fc.Origin - centroid).LengthSquared();
                        scored.Add((f, dist));
                    }
                    if (scored.Count > 0) {
                        scored.Sort((a, b) => b.dist.CompareTo(a.dist));
                        int topN = Math.Max(1, scored.Count / 3);
                        fi = scored[rng.Next(topN)].idx;
                    } else {
                        fi = rng.Next(frontier.Count);
                    }
                }
                else {
                    fi = rng.Next(frontier.Count);
                }

                var (existingCellNum, existingPolyId, existingEnvId, existingCS) = frontier[fi];
                var frontierKey = (existingCellNum, existingPolyId);

                float progress = (float)contentCells / targetCells;
                float capPressure = (float)(contentCells + frontier.Count) / targetCells;
                int preferredMaxExits;
                if (p.Branching == 0) {
                    preferredMaxExits = 2;
                } else if (p.Branching >= 2) {
                    // Heavy branching: allow junctions through most of growth
                    if (capPressure > 0.92f) preferredMaxExits = 2;
                    else if (progress < 0.40f && capPressure < 0.55f) preferredMaxExits = 4;
                    else if (progress < 0.80f && capPressure < 0.85f) preferredMaxExits = 3;
                    else preferredMaxExits = 2;
                } else {
                    // Moderate (default)
                    if (capPressure > 0.90f) preferredMaxExits = 2;
                    else if (progress < 0.25f && capPressure < 0.5f) preferredMaxExits = 4;
                    else if (progress < 0.65f && capPressure < 0.75f) preferredMaxExits = 3;
                    else preferredMaxExits = 2;
                }

                if (frontier.Count > totalCells * 2 || capPressure > 0.85f)
                    preferredMaxExits = Math.Min(preferredMaxExits, 2);

                var compatible = portalIndex.GetCompatible(existingEnvId, existingCS, existingPolyId);

                var existingCellRef = doc.GetCell(existingCellNum);
                if (existingCellRef == null) {
                    frontier.RemoveAt(fi);
                    continue;
                }

                // --- Primary growth: single-cell placement via edge-direct bridge ---
                // Uses proven transforms from real dungeon data for reliable placement.
                // Sorting prefers rooms matching the target exit count so junctions
                // surface naturally when the dungeon needs branching.
                if (compatible.Count > 0) {
                    var existingCell = existingCellRef;
                    {
                        var bridgeCandidates2 = new List<CompatibleRoom>();
                        foreach (var cr in compatible) {
                            if (cr.IsGeometryDerived) continue;
                            if (cr.PortalArea > 0.01f) {
                                var srcGeo = geoCache.Get(existingEnvId, existingCS, existingPolyId);
                                if (srcGeo != null && srcGeo.Area > 0.01f) {
                                    float areaRatio = MathF.Min(srcGeo.Area, cr.PortalArea) / MathF.Max(srcGeo.Area, cr.PortalArea);
                                    if (areaRatio < 0.85f) continue;
                                }
                            } else if (!geoCache.AreCompatible(existingEnvId, existingCS, existingPolyId,
                                    cr.EnvId, cr.CellStruct, cr.PolyId)) {
                                continue;
                            }
                            var bKey = (cr.EnvId, cr.CellStruct);
                            if (!verifiedPortalCounts.TryGetValue(bKey, out int vpc)) {
                                uint vpEnvFileId = (uint)(cr.EnvId | 0x0D000000);
                                if (dats.TryGet<Acme.Dat.Environment>(vpEnvFileId, out var vpEnv) &&
                                    vpEnv.Cells.TryGetValue(cr.CellStruct, out var vpCS)) {
                                    vpc = PortalSnapper.GetPortalPolygonIds(vpCS).Count;
                                } else {
                                    vpc = 3;
                                }
                            }
                            if (vpc < 2) continue;
                            if (vpc > preferredMaxExits) continue;
                            bridgeCandidates2.Add(cr);
                        }

                        double junctionProb = p.Branching >= 2 ? 0.70 : p.Branching == 1 ? 0.40 : 0.0;
                        bool preferJunctions = preferredMaxExits >= 3 && rng.NextDouble() < junctionProb;
                        bridgeCandidates2.Sort((a, b) => {
                            verifiedPortalCounts.TryGetValue((a.EnvId, a.CellStruct), out int pa);
                            verifiedPortalCounts.TryGetValue((b.EnvId, b.CellStruct), out int pb);
                            int cmp = preferJunctions ? pb.CompareTo(pa) : pa.CompareTo(pb);
                            return cmp != 0 ? cmp : CompatibleRoomScore(b).CompareTo(CompatibleRoomScore(a));
                        });

                        if (useFavorites && bridgeCandidates2.Count > 1) {
                            var scored = bridgeCandidates2.Select(cr => {
                                int connectivity = 0;
                                var catRoom = kb.Catalog.FirstOrDefault(c => c.EnvId == cr.EnvId && c.CellStruct == cr.CellStruct);
                                if (catRoom?.PortalPolyIds != null) {
                                    foreach (var op in catRoom.PortalPolyIds) {
                                        if (op == cr.PolyId) continue;
                                        connectivity += portalIndex.GetCompatible(cr.EnvId, cr.CellStruct, op).Count;
                                    }
                                }
                                return (cr, connectivity);
                            }).OrderByDescending(x => x.connectivity).ToList();
                            bridgeCandidates2 = scored.Select(x => x.cr).ToList();
                        }

                        bool bridgePlaced = false;
                        foreach (var br in bridgeCandidates2.Take(8)) {
                            var bridgeResult = TryPlaceEdgeDirectBridge(doc, dats, geoCache, kb,
                                existingCell, existingCellNum, existingPolyId,
                                br, placedPositions, roomBounds,
                                placedAABBs: placedAABBs);
                            if (bridgeResult != null) {
                                totalCells += 1;
                                contentCells += 1;
                                bridgePlacements++;
                                indexedPlacements++;
                                var bridgeCell = doc.GetCell(bridgeResult.Value)!;
                                placedPositions.Add(bridgeCell.Origin);
                                TrackPlacedAABB(placedAABBs, geoCache, bridgeCell.EnvironmentId, bridgeCell.CellStructure, bridgeCell.Origin, bridgeCell.Orientation);
                                frontier.Clear();
                                frontierFailures.Clear();
                                CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);
                                bridgePlaced = true;
                                break;
                            }
                        }
                        if (bridgePlaced) continue;
                    }
                }
                else {
                    edgeMissCount++;
                }

                // --- Fallback A: geometric room-type growth from catalog ---
                var existingCellForGeo = existingCellRef;

                var growthGeoCandidates = GetGeometricGrowthCandidates(
                    existingEnvId, existingCS, existingPolyId, preferredMaxExits);
                if (growthGeoCandidates.Count > 0) {
                    bool geoGrowthPlaced = false;
                    foreach (var room in growthGeoCandidates.Take(20)) {
                        var roomResult = TryCapWithGeometricSnap(doc, dats, geoCache, kb,
                            existingCellForGeo, existingCellNum, existingPolyId,
                            room, placedPositions, placedAABBs, overlapScale: 1.0f);
                        if (roomResult != null) {
                            totalCells++;
                            contentCells++;
                            snapPlacements++;
                            var c = doc.GetCell(roomResult.Value);
                            if (c != null) {
                                placedPositions.Add(c.Origin);
                                TrackPlacedAABB(placedAABBs, geoCache, c.EnvironmentId, c.CellStructure, c.Origin, c.Orientation);
                            }
                            frontier.Clear();
                            frontierFailures.Clear();
                            CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);
                            geoGrowthPlaced = true;
                            break;
                        }
                    }
                    if (geoGrowthPlaced) continue;
                }

                // --- Fallback B: geo-snap with single-cell prefabs ---
                // Legacy fallback path; retained for compatibility with custom prefab packs.
                {
                    frontierFailures.TryGetValue(frontierKey, out int fails);
                    frontierFailures[frontierKey] = fails + 1;

                    int maxGeoRetries = useFavorites ? 12 : 5;
                    if (fails < maxGeoRetries) {
                        // Strictly 1-cell prefabs only. Use verified portal count from
                        // catalog; fall back to prefab OpenFaces.Count for rooms not in catalog.
                        var pool = candidates
                            .Where(pf => pf.Cells.Count == 1)
                            .Where(pf => {
                                var cell0 = pf.Cells[0];
                                int vpc;
                                if (verifiedPortalCounts.TryGetValue((cell0.EnvId, cell0.CellStruct), out vpc)) {
                                    return vpc >= 2 && vpc <= preferredMaxExits;
                                }
                                // Not in catalog — use prefab open faces as approximation
                                return pf.OpenFaces.Count >= 2 && pf.OpenFaces.Count <= preferredMaxExits;
                            })
                            .ToList();
                        // Never fall back to multi-cell prefabs — retire the portal instead
                        if (pool.Count == 0) pool = candidates
                            .Where(pf => pf.Cells.Count == 1 && pf.OpenFaces.Count <= preferredMaxExits)
                            .ToList();

                        var geoDrivenPool = new List<(DungeonPrefab prefab, float score)>();
                        var targetPortalGeo = geoCache.Get(existingEnvId, existingCS, existingPolyId);
                        if (targetPortalGeo != null && targetPortalGeo.Area > MinPortalArea) {
                            foreach (var pf in pool) {
                                float best = 0f;
                                foreach (var of in pf.OpenFaces) {
                                    var src = geoCache.Get(of.EnvId, of.CellStruct, of.PolyId);
                                    if (src == null || src.Area <= MinPortalArea) continue;
                                    if (!geoCache.AreCompatible(existingEnvId, existingCS, existingPolyId, of.EnvId, of.CellStruct, of.PolyId))
                                        continue;
                                    float sim = PortalSimilarityScore(targetPortalGeo, src);
                                    if (sim > best) best = sim;
                                }
                                if (best > 0f)
                                    geoDrivenPool.Add((pf, best));
                            }
                            if (geoDrivenPool.Count > 0) {
                                // Keep the best-matching rooms first, then allow some randomness.
                                pool = geoDrivenPool
                                    .OrderByDescending(x => x.score)
                                    .Take(40)
                                    .Select(x => x.prefab)
                                    .ToList();
                            }
                        }

                        int retries = Math.Min(useFavorites ? 24 : 12, pool.Count);
                        List<ushort>? fallbackResult = null;
                        for (int r = 0; r < retries; r++) {
                            DungeonPrefab candidate;
                            if (r < pool.Count) {
                                // Deterministic pass through top candidates improves hit rate.
                                candidate = pool[r];
                            } else {
                                candidate = pool[rng.Next(pool.Count)];
                            }
                            fallbackResult = TryAttachPrefab(doc, dats, portalIndex, geoCache,
                                existingCellNum, existingPolyId, candidate, null, placedPositions,
                                placedAABBs);
                            if (fallbackResult != null && fallbackResult.Count > 0) {
                                break;
                            }
                        }
                        if (fallbackResult != null && fallbackResult.Count > 0) {
                            totalCells += fallbackResult.Count;
                            contentCells += fallbackResult.Count;
                            snapPlacements++;
                            foreach (var cn in fallbackResult) {
                                var c = doc.GetCell(cn);
                                if (c != null) {
                                    placedPositions.Add(c.Origin);
                                    TrackPlacedAABB(placedAABBs, geoCache, c.EnvironmentId, c.CellStructure, c.Origin, c.Orientation);
                                }
                            }
                            frontier.Clear();
                            frontierFailures.Clear();
                            CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);
                            continue;
                        }
                    }

                    if (fails + 1 >= maxPortalRetries) {
                        frontier.RemoveAt(fi);
                        retiredPortals++;
                    }
                }
            }

            // If growth fell short of target, rebuild frontier and try again using
            // the FULL KB pool (not just favorites). This creates bridge rooms between
            // favorite-compatible areas, letting the dungeon grow beyond the narrow
            // favorites pool. The bridges get favorite surfaces applied later.
            // Only boost if growth genuinely stalled (content is low AND there's room
            // in the cap budget). If the main loop stopped due to cap-aware estimation
            // (content + frontier >= target), boosting would just add more portals to cap.
            bool allowFavoritesBoost = useFavorites && (
                targetCells >= 64 ||
                contentCells < Math.Max(12, targetCells / 2) ||
                frontier.Count == 0);
            bool boostNeeded = (!useFavorites || allowFavoritesBoost) && (
                               (contentCells < targetCells * 0.5f &&
                                contentCells + frontier.Count < targetCells - cascadeBuffer) ||
                               (frontier.Count == 0 && contentCells < targetCells - 4));
            if (boostNeeded) {
                frontier.Clear();
                frontierFailures.Clear();
                CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);
                int restartCells = 0;

                var widePool = kb.Prefabs
                    .Where(pf => pf.OpenFaces.Count >= 1 && pf.OpenFaces.Count <= 3)
                    .Where(pf => !p.AllowVertical ? !pf.OpenFaceDirections.Any(d => verticalDirs.Contains(d)) : true)
                    .OrderByDescending(pf => pf.UsageCount)
                    .Take(300)
                    .ToList();
                var wideConnectors = widePool.Where(pf => pf.OpenFaces.Count >= 2).ToList();

                if (frontier.Count > 0 && widePool.Count > 0) {
                    int restartBudget = (targetCells - contentCells) * (useFavorites ? (targetCells <= 48 ? 8 : 4) : 8);
                    var restartFailures = new Dictionary<(ushort, ushort), int>();

                    for (int ra = 0; ra < restartBudget && frontier.Count > 0; ra++) {
                        if (contentCells + frontier.Count >= targetCells - cascadeBuffer) break;
                        if (contentCells >= targetCells) break;

                        int fi = rng.Next(frontier.Count);
                        var (cn, pid, eid, cs) = frontier[fi];
                        var fKey = (cn, pid);
                        var cell = doc.GetCell(cn);
                        if (cell == null) { frontier.RemoveAt(fi); continue; }

                        restartFailures.TryGetValue(fKey, out int rf);

                        // Limit prefab size to remaining budget
                        int boostMaxCells = Math.Max(1, targetCells - contentCells - frontier.Count + 1);

                        // Prefer corridors (2 exits). In favorites mode we allow up to 3 exits
                        // for recovery when the pool is sparse.
                        int boostMaxExits = useFavorites ? (targetCells <= 48 ? 4 : 3) : 2;
                        bool placed = false;
                        var compat = portalIndex.GetCompatible(eid, cs, pid);
                        if (compat.Count > 0) {
                            var filteredCompat = compat.Where(cr => {
                                verifiedPortalCounts.TryGetValue((cr.EnvId, cr.CellStruct), out int pc);
                                return pc >= 2 && pc <= boostMaxExits;
                            }).OrderByDescending(CompatibleRoomScore).ToList();
                            if (filteredCompat.Count == 0) filteredCompat = compat.OrderByDescending(CompatibleRoomScore).Take(3).ToList();

                            foreach (var cr in filteredCompat.Take(3)) {
                                if (!geoCache.AreCompatible(eid, cs, pid, cr.EnvId, cr.CellStruct, cr.PolyId)) continue;
                                var bridgeResult = TryPlaceEdgeDirectBridge(doc, dats, geoCache, kb,
                                    cell, cn, pid, cr, placedPositions, roomBounds,
                                    placedAABBs: placedAABBs);
                                if (bridgeResult != null) {
                                    totalCells++; contentCells++; restartCells++;
                                    bridgePlacements++;
                                    var bc = doc.GetCell(bridgeResult.Value)!;
                                    placedPositions.Add(bc.Origin);
                                    TrackPlacedAABB(placedAABBs, geoCache, bc.EnvironmentId, bc.CellStructure, bc.Origin, bc.Orientation);
                                    frontier.Clear(); restartFailures.Clear();
                                    CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);
                                    placed = true; break;
                                }
                            }
                        }

                        if (!placed) {
                            // In boost mode, keep rooms narrow to avoid runaway frontier.
                            var pool = widePool
                                .Where(pf => pf.Cells.Count <= boostMaxCells && pf.OpenFaces.Count >= 2 && pf.OpenFaces.Count <= boostMaxExits)
                                .ToList();
                            if (pool.Count == 0) pool = widePool.Where(pf => pf.Cells.Count <= boostMaxCells && pf.OpenFaces.Count >= 2).ToList();
                            if (pool.Count == 0) pool = wideConnectors.Count > 0 ? wideConnectors : widePool;
                            for (int r = 0; r < Math.Min(10, pool.Count); r++) {
                                var candidate = pool[rng.Next(pool.Count)];
                                var res = TryAttachPrefab(doc, dats, portalIndex, geoCache,
                                    cn, pid, candidate, null, placedPositions, placedAABBs);
                                if (res != null && res.Count > 0) {
                                    totalCells += res.Count; contentCells += res.Count;
                                    restartCells += res.Count; snapPlacements++;
                                    foreach (var c in res) {
                                        var dc = doc.GetCell(c);
                                        if (dc != null) {
                                            placedPositions.Add(dc.Origin);
                                            TrackPlacedAABB(placedAABBs, geoCache, dc.EnvironmentId, dc.CellStructure, dc.Origin, dc.Orientation);
                                        }
                                    }
                                    frontier.Clear(); restartFailures.Clear();
                                    CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);
                                    placed = true; break;
                                }
                            }
                        }

                        if (!placed) {
                            restartFailures[fKey] = rf + 1;
                            if (rf + 1 >= 8) frontier.RemoveAt(fi);
                        }
                    }
                    if (restartCells > 0)
                        Console.WriteLine($"[DungeonGen] Growth boost: placed {restartCells} cells using wider pool");
                }
            }

            Console.WriteLine($"[DungeonGen] Growth: {totalCells} cells ({contentCells} content + {bridgePlacements} bridge), " +
                $"{frontier.Count} open exits, {attempts} attempts, {indexedPlacements} edge-guided" +
                (snapPlacements > 0 ? $", {snapPlacements} geo-snap" : "") +
                $", {retiredPortals} retired");

            int CountOpenPortalsOnCell(ushort cellNum) {
                var dc = doc.GetCell(cellNum);
                if (dc == null) return int.MaxValue;
                uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
                if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return int.MaxValue;
                if (!env.Cells.TryGetValue(dc.CellStructure, out var cs)) return int.MaxValue;

                var allPortals = PortalSnapper.GetPortalPolygonIds(cs);
                var connected = new HashSet<ushort>(dc.CellPortals.Select(cp => cp.PolygonId));
                int open = 0;
                foreach (var pid in allPortals) {
                    if (connected.Contains(pid)) continue;
                    open++;
                }
                return open;
            }

            bool AcceptCapOrRollback(ushort capCellNum) {
                int openOnCap = CountOpenPortalsOnCell(capCellNum);
                if (openOnCap == 0) return true;
                doc.RemoveCell(capCellNum);
                return false;
            }

            // --- PHASE 2: Capping ---
            // Rebuild the open faces list from scratch — the growth phase's frontier
            // was depleted by retirements, but the actual dungeon still has open doorways.
            // Every unconnected portal polygon is a visible hole in a wall.
            frontier.Clear();
            CollectOpenFaces(doc, dats, frontier, null);
            Console.WriteLine($"[DungeonGen] Pre-cap: {frontier.Count} open portals to seal");

            int capsPlaced = 0;
            int capNoIndex = 0, capNoCandidate = 0, capOverlap = 0, capRejectedNonClosing = 0;
            int capOvershootAllowance = sizeTier switch {
                "small" => 1,
                "medium" => 2,
                "large" => 4,
                "huge" => 6,
                _ => 8
            };
            int capCellBudgetSoft = Math.Max(0, (targetCells + capOvershootAllowance) - contentCells);
            // Close-first mode: allow extra cap placements when needed so we do not
            // leave avoidable open portals just because the soft size budget was hit.
            int closeFirstExtraBudget = sizeTier switch {
                "small" => 16,
                "medium" => 24,
                "large" => 40,
                "huge" => 64,
                _ => 96
            };
            if (useFavorites) closeFirstExtraBudget += 12;
            int capCellBudgetHard = capCellBudgetSoft + closeFirstExtraBudget;
            if (frontier.Count > 0) {
                var capFrontier = new List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)>(frontier);
                for (int i = capFrontier.Count - 1; i > 0; i--) {
                    int j = rng.Next(i + 1);
                    (capFrontier[i], capFrontier[j]) = (capFrontier[j], capFrontier[i]);
                }

                foreach (var (capCellNum, capPolyId, capEnvId, capCS) in capFrontier) {
                    if (capsPlaced >= capCellBudgetHard) break;
                    var existingCell = doc.GetCell(capCellNum);
                    if (existingCell == null) continue;

                    // Tier 1: use pre-computed dead-end index (instant lookup, proven transforms)
                    var capCandidates = new List<CompatibleRoom>();
                    if (deadEndLookup.TryGetValue((capEnvId, capCS, capPolyId), out var deadEndOptions)) {
                        foreach (var opt in deadEndOptions) {
                            capCandidates.Add(new CompatibleRoom {
                                EnvId = opt.EnvId, CellStruct = opt.CellStruct, PolyId = opt.PolyId,
                                Count = opt.Count,
                                RelOffset = new Vector3(opt.RelOffsetX, opt.RelOffsetY, opt.RelOffsetZ),
                                RelRot = new Quaternion(opt.RelRotX, opt.RelRotY, opt.RelRotZ, opt.RelRotW)
                            });
                        }
                    }

                    // Tier 2: search compatibility index for dead-ends not in the pre-computed index
                    if (capCandidates.Count == 0) {
                        var capCompatible = portalIndex.GetCompatible(capEnvId, capCS, capPolyId);
                        if (capCompatible.Count == 0) {
                            capNoIndex++;
                        }
                        foreach (var cr in capCompatible) {
                            if (catalogDeadEnds.Contains((cr.EnvId, cr.CellStruct)))
                                capCandidates.Add(cr);
                        }
                    }

                    if (capCandidates.Count == 0) {
                        // Tier 3: deterministic geometric search across verified dead-end room types.
                        // This avoids self-referencing artifacts and catches portal faces that have
                        // no direct edge index hit but still match by actual geometry.
                        var faceKey = (capEnvId, capCS, capPolyId);
                        if (!geometricCapCache.TryGetValue(faceKey, out capCandidates)) {
                            capCandidates = new List<CompatibleRoom>();
                            var targetGeo = geoCache.Get(capEnvId, capCS, capPolyId);
                            if (targetGeo != null && targetGeo.Area > MinPortalArea) {
                                foreach (var deadEnd in deadEndRoomCatalog) {
                                    ushort dePoly = deadEnd.PortalPolyIds[0];
                                    var sourceGeo = geoCache.Get(deadEnd.EnvId, deadEnd.CellStruct, dePoly);
                                    if (sourceGeo == null || sourceGeo.Area <= MinPortalArea) continue;
                                    if (!geoCache.AreCompatible(capEnvId, capCS, capPolyId, deadEnd.EnvId, deadEnd.CellStruct, dePoly))
                                        continue;
                                    float sim = PortalSimilarityScore(targetGeo, sourceGeo);
                                    if (sim < 0.75f) continue;
                                    capCandidates.Add(new CompatibleRoom {
                                        EnvId = deadEnd.EnvId,
                                        CellStruct = deadEnd.CellStruct,
                                        PolyId = dePoly,
                                        Count = deadEnd.UsageCount
                                    });
                                }
                                capCandidates = capCandidates
                                    .OrderByDescending(CompatibleRoomScore)
                                    .Take(16)
                                    .ToList();
                            }
                            geometricCapCache[faceKey] = capCandidates;
                        }
                    }

                    if (capCandidates.Count == 0) { capNoCandidate++; continue; }

                    bool placed = false;
                    foreach (var capRoom in capCandidates.OrderByDescending(CompatibleRoomScore).Take(8)) {
                        var capResult = TryPlaceEdgeDirectBridge(doc, dats, geoCache, kb,
                            existingCell, capCellNum, capPolyId,
                            capRoom, placedPositions, roomBounds,
                            overlapScale: 0.3f,
                            placedAABBs: placedAABBs);
                        if (capResult == null) {
                            capResult = TryPlaceEdgeDirectBridge(doc, dats, geoCache, kb,
                                existingCell, capCellNum, capPolyId,
                                capRoom, placedPositions, roomBounds,
                                overlapScale: 0.1f,
                                placedAABBs: placedAABBs);
                        }
                        if (capResult == null) {
                            capResult = TryCapWithGeometricSnap(doc, dats, geoCache, kb,
                                existingCell, capCellNum, capPolyId,
                                capRoom, placedPositions, placedAABBs);
                        }
                        if (capResult != null) {
                            if (!AcceptCapOrRollback(capResult.Value)) {
                                capRejectedNonClosing++;
                                continue;
                            }
                            totalCells++;
                            capsPlaced++;
                            var capCell = doc.GetCell(capResult.Value)!;
                            placedPositions.Add(capCell.Origin);
                            TrackPlacedAABB(placedAABBs, geoCache, capCell.EnvironmentId, capCell.CellStructure, capCell.Origin, capCell.Orientation);
                            placed = true;
                            break;
                        }
                    }
                    if (!placed) capOverlap++;
                }

                frontier.Clear();
                CollectOpenFaces(doc, dats, frontier, null);
                Console.WriteLine($"[DungeonGen] Capping: placed {capsPlaced} dead-ends, {frontier.Count} remaining " +
                    $"(failed: {capNoIndex} no-index, {capNoCandidate} no-candidate, {capOverlap} overlap, {capRejectedNonClosing} non-closing)");

                // Iterative capping: seal new open portals created by previous caps.
                // Stop when no net progress (remaining count stops decreasing).
                int prevRemaining = frontier.Count;
                for (int capPass = 2; capPass <= 4 && frontier.Count > 0; capPass++) {
                    int passSealed = 0;
                    var passFrontier = new List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)>(frontier);
                    foreach (var (cn, pid, eid, cs) in passFrontier) {
                        if (capsPlaced + passSealed >= capCellBudgetHard) break;
                        var cell = doc.GetCell(cn);
                        if (cell == null) continue;

                        bool sealed_ = false;

                        // Try pre-computed dead-end index first
                        if (deadEndLookup.TryGetValue((eid, cs, pid), out var passDeadEnds)) {
                            foreach (var opt in passDeadEnds) {
                                var cr = new CompatibleRoom {
                                    EnvId = opt.EnvId, CellStruct = opt.CellStruct, PolyId = opt.PolyId,
                                    Count = opt.Count,
                                    RelOffset = new Vector3(opt.RelOffsetX, opt.RelOffsetY, opt.RelOffsetZ),
                                    RelRot = new Quaternion(opt.RelRotX, opt.RelRotY, opt.RelRotZ, opt.RelRotW)
                                };
                                var r = TryPlaceEdgeDirectBridge(doc, dats, geoCache, kb,
                                    cell, cn, pid, cr, placedPositions, roomBounds,
                                    overlapScale: 0.1f, placedAABBs: placedAABBs);
                                if (r != null) {
                                    if (!AcceptCapOrRollback(r.Value)) {
                                        capRejectedNonClosing++;
                                        continue;
                                    }
                                    totalCells++; passSealed++;
                                    var rc = doc.GetCell(r.Value)!;
                                    placedPositions.Add(rc.Origin);
                                    TrackPlacedAABB(placedAABBs, geoCache, rc.EnvironmentId, rc.CellStructure, rc.Origin, rc.Orientation);
                                    sealed_ = true; break;
                                }
                            }
                        }

                        // Fall back to compatibility index search
                        if (!sealed_) {
                            var compat = portalIndex.GetCompatible(eid, cs, pid);
                            foreach (var cr in compat) {
                                if (!catalogDeadEnds.Contains((cr.EnvId, cr.CellStruct))) continue;
                                var r = TryPlaceEdgeDirectBridge(doc, dats, geoCache, kb,
                                    cell, cn, pid, cr, placedPositions, roomBounds,
                                    overlapScale: 0.1f, placedAABBs: placedAABBs);
                                if (r != null) {
                                    if (!AcceptCapOrRollback(r.Value)) {
                                        capRejectedNonClosing++;
                                        continue;
                                    }
                                    totalCells++; passSealed++;
                                    var rc = doc.GetCell(r.Value)!;
                                    placedPositions.Add(rc.Origin);
                                    TrackPlacedAABB(placedAABBs, geoCache, rc.EnvironmentId, rc.CellStructure, rc.Origin, rc.Orientation);
                                    sealed_ = true; break;
                                }
                            }
                        }

                        if (!sealed_) {
                            var faceKey = (eid, cs, pid);
                            if (!geometricCapCache.TryGetValue(faceKey, out var geoCaps)) {
                                geoCaps = new List<CompatibleRoom>();
                                var targetGeo = geoCache.Get(eid, cs, pid);
                                if (targetGeo != null && targetGeo.Area > MinPortalArea) {
                                    foreach (var deadEnd in deadEndRoomCatalog) {
                                        ushort dePoly = deadEnd.PortalPolyIds[0];
                                        var sourceGeo = geoCache.Get(deadEnd.EnvId, deadEnd.CellStruct, dePoly);
                                        if (sourceGeo == null || sourceGeo.Area <= MinPortalArea) continue;
                                        if (!geoCache.AreCompatible(eid, cs, pid, deadEnd.EnvId, deadEnd.CellStruct, dePoly))
                                            continue;
                                        float sim = PortalSimilarityScore(targetGeo, sourceGeo);
                                        if (sim < 0.75f) continue;
                                        geoCaps.Add(new CompatibleRoom {
                                            EnvId = deadEnd.EnvId,
                                            CellStruct = deadEnd.CellStruct,
                                            PolyId = dePoly,
                                            Count = deadEnd.UsageCount
                                        });
                                    }
                                    geoCaps = geoCaps.OrderByDescending(CompatibleRoomScore).Take(12).ToList();
                                }
                                geometricCapCache[faceKey] = geoCaps;
                            }
                            foreach (var cr in geoCaps) {
                                var r = TryCapWithGeometricSnap(doc, dats, geoCache, kb,
                                    cell, cn, pid, cr, placedPositions, placedAABBs);
                                if (r != null) {
                                    if (!AcceptCapOrRollback(r.Value)) {
                                        capRejectedNonClosing++;
                                        continue;
                                    }
                                    totalCells++; passSealed++;
                                    var rc = doc.GetCell(r.Value)!;
                                    placedPositions.Add(rc.Origin);
                                    TrackPlacedAABB(placedAABBs, geoCache, rc.EnvironmentId, rc.CellStructure, rc.Origin, rc.Orientation);
                                    sealed_ = true; break;
                                }
                            }
                        }
                    }
                    if (passSealed == 0) break;
                    capsPlaced += passSealed;
                    frontier.Clear();
                    CollectOpenFaces(doc, dats, frontier, null);
                    Console.WriteLine($"[DungeonGen] Cap pass {capPass}: sealed {passSealed} more, {frontier.Count} remaining");

                    if (frontier.Count >= prevRemaining) break;
                    prevRemaining = frontier.Count;
                }

                if (frontier.Count > 0 && capsPlaced >= capCellBudgetSoft && capsPlaced < capCellBudgetHard) {
                    Console.WriteLine($"[DungeonGen] Capping used close-first reserve: soft={capCellBudgetSoft}, hard={capCellBudgetHard}, placed={capsPlaced}");
                }
            }

            // --- PHASE 3: Report unresolved uncappable portals ---
            // Never create synthetic self-referencing portals: they can produce visible
            // artifacts in the AC client. Any remaining open faces are left unresolved
            // after all verified/dead-end and geometric cap attempts are exhausted.
            frontier.Clear();
            CollectOpenFaces(doc, dats, frontier, null);
            int unresolvedUncappable = frontier.Count;
            if (unresolvedUncappable > 0)
                Console.WriteLine($"[DungeonGen] Unresolved open portals: {unresolvedUncappable} (no synthetic self-links)");

            // Post-check: detect and remove non-neighbor overlapping rooms.
            // Rooms that physically overlap but aren't portal-connected create
            // invisible walls — the player sees a room but can't reach it.
            // Remove the higher-numbered cell in each overlap pair (likely a cap)
            // so the portal reverts to an uncapped outside face.
            var (overlapPairs, overlapSamples) = AuditFinalOverlaps(doc, geoCache);
            if (overlapPairs > 0) {
                var overlapVictims = FindOverlapVictims(doc, geoCache);
                foreach (var victim in overlapVictims)
                    doc.RemoveCell(victim);
                if (overlapVictims.Count > 0) {
                    var victimList = string.Join(", ", overlapVictims.Select(v => $"0x{v:X4}"));
                    Console.WriteLine($"[DungeonGen] Overlap cleanup: removed {overlapVictims.Count} overlapping cell(s): {victimList}");
                    capsPlaced -= overlapVictims.Count;
                }
                var (remainingPairs, remainingSamples) = AuditFinalOverlaps(doc, geoCache);
                var sampleText = remainingSamples.Count > 0 ? $" e.g. {string.Join(", ", remainingSamples)}" : "";
                Console.WriteLine($"[DungeonGen] Overlap audit: {remainingPairs} remaining non-neighbor overlap pair(s) (was {overlapPairs}){sampleText}");
            } else {
                Console.WriteLine("[DungeonGen] Overlap audit: no non-neighbor overlaps detected");
            }

            int actualTotal = doc.Cells.Count;
            int overshoot = actualTotal - targetCells;
            Console.WriteLine($"[DungeonGen] Final: target={targetCells}, actual={actualTotal} " +
                $"(content={contentCells}, caps={capsPlaced}, unresolved={unresolvedUncappable}" +
                (overshoot > 0 ? $", overshoot=+{overshoot}" : $", under={-overshoot}") + ")");

            // Post-generation: fix one-way portals that may have been missed during
            // placement. The game client requires symmetric portal entries.
            int portalFixes = doc.AutoFixPortals();
            if (portalFixes > 0)
                Console.WriteLine($"[DungeonGen] Auto-fixed {portalFixes} one-way portal(s)");

            // Compute portal_side flags from geometry. The AC client uses these to
            // determine which half-space of each portal polygon is the cell interior.
            // Without correct flags, portal traversal and collision detection break.
            int flagFixes = doc.RecomputePortalFlags(dats);
            if (flagFixes > 0)
                Console.WriteLine($"[DungeonGen] Computed portal flags for {flagFixes} portal(s)");

            // Set exact_match on portal pairs whose geometry matches.
            int exactMatchFixes = SetExactMatchFlags(doc, dats, geoCache);
            if (exactMatchFixes > 0)
                Console.WriteLine($"[DungeonGen] Set exact_match on {exactMatchFixes} portal pair(s)");

            // Post-generation: apply surfaces.
            // Favorites: use the surfaces from the favorited prefabs' cells directly.
            // Style: auto-apply the matching theme (wall + floor textures) from the KB.
            // "All": keep whatever surfaces the DAT provided.
            if (useFavorites && favPrefabs is { Count: > 0 }) {
                int applied = ApplyFavoriteSurfaces(doc, favPrefabs);
                Console.WriteLine($"[DungeonGen] Applied surfaces from favorites to {applied}/{doc.Cells.Count} cells");
            }
            else if (p.Style.Equals("Custom", StringComparison.OrdinalIgnoreCase) &&
                     (p.CustomWallSurface != 0 || p.CustomFloorSurface != 0)) {
                ushort wall = p.CustomWallSurface != 0 ? p.CustomWallSurface : (ushort)0x032A;
                ushort floor = p.CustomFloorSurface != 0 ? p.CustomFloorSurface : (ushort)0x032B;
                int themed = ApplyThemeSurfaces(doc, wall, floor);
                Console.WriteLine($"[DungeonGen] Applied custom textures (wall=0x{wall:X4}, floor=0x{floor:X4}) to {themed}/{doc.Cells.Count} cells");
            }
            else {
                string effectiveStyle = lockedStyle ?? (p.Style != "All" && !p.Style.Equals("Custom", StringComparison.OrdinalIgnoreCase) ? p.Style : null) ?? "";
                if (!string.IsNullOrEmpty(effectiveStyle)) {
                    var theme = kb.StyleThemes.FirstOrDefault(t =>
                        t.Name.Equals(effectiveStyle, StringComparison.OrdinalIgnoreCase));
                    if (theme != null) {
                        int themed = ApplyThemeSurfaces(doc, theme.WallSurface, theme.FloorSurface);
                        Console.WriteLine($"[DungeonGen] Applied '{theme.Name}' style textures (wall=0x{theme.WallSurface:X4}, floor=0x{theme.FloorSurface:X4}) to {themed}/{doc.Cells.Count} cells");
                    }
                }
            }

            // Post-generation: furnish rooms with static objects from the KB.
            if (EnableAutoFurnish && p.FurnishRooms && kb.RoomStatics.Count > 0) {
                int furnished = FurnishRooms(doc, kb);
                if (furnished > 0)
                    Console.WriteLine($"[DungeonGen] Furnished {furnished}/{doc.Cells.Count} rooms with static objects");
            }

            doc.ComputeVisibleCells();

            // Log validation summary so issues are visible during development
            var warnings = doc.Validate();
            if (warnings.Count > 0) {
                Console.WriteLine($"[DungeonGen] Validation warnings ({warnings.Count}):");
                foreach (var w in warnings.Take(10))
                    Console.WriteLine($"  - {w}");
                if (warnings.Count > 10)
                    Console.WriteLine($"  ... and {warnings.Count - 10} more");
            }

            return doc;
        }

        private static List<ushort> PlacePrefabAtOrigin(DungeonDocument doc, IDatReaderWriter dats, DungeonPrefab prefab) {
            var cellMap = new Dictionary<int, ushort>();

            var first = prefab.Cells[0];
            var firstCellNum = doc.AddCell(first.EnvId, first.CellStruct,
                new Vector3(0f, 0f, GeneratedDungeonBaseZ), Quaternion.Identity, first.Surfaces.ToList());
            cellMap[0] = firstCellNum;

            PlaceRemainingCells(doc, dats, prefab, cellMap);

            return cellMap.Values.Where(cn => doc.GetCell(cn) != null).ToList();
        }

        /// <summary>
        /// Try to attach a prefab to an existing cell's open portal.
        /// Uses proven transforms from real game data when available.
        /// </summary>
        private static List<ushort>? TryAttachPrefab(
            DungeonDocument doc, IDatReaderWriter dats,
            PortalCompatibilityIndex portalIndex, PortalGeometryCache geoCache,
            ushort existingCellNum, ushort existingPolyId,
            DungeonPrefab prefab, CompatibleRoom? matchedRoom,
            List<Vector3> placedPositions,
            List<RoomAABB>? placedAABBs = null) {

            var existingCell = doc.GetCell(existingCellNum);
            if (existingCell == null) return null;

            if (matchedRoom != null) {
                var result = TryAttachWithProvenTransform(doc, dats, geoCache,
                    existingCell, existingCellNum, existingPolyId,
                    prefab, matchedRoom, placedPositions, placedAABBs);
                if (result != null) return result;
            }

            foreach (var openFace in prefab.OpenFaces) {
                var match = portalIndex.FindMatch(
                    existingCell.EnvironmentId, existingCell.CellStructure, existingPolyId,
                    openFace.EnvId, openFace.CellStruct);

                if (match != null) {
                    if (!geoCache.AreCompatible(
                        existingCell.EnvironmentId, existingCell.CellStructure, existingPolyId,
                        openFace.EnvId, openFace.CellStruct, match.PolyId))
                        continue;

                    var newOrigin = existingCell.Origin + Vector3.Transform(match.RelOffset, existingCell.Orientation);
                    var newRot = ConstrainToYaw(Quaternion.Normalize(existingCell.Orientation * match.RelRot));

                    if (WouldOverlap(newOrigin, placedPositions, prefab, openFace.CellIndex, newRot,
                            excludeOrigin: existingCell.Origin,
                            placedAABBs: placedAABBs, geoCache: geoCache))
                        continue;

                    var cellMap = new Dictionary<int, ushort>();
                    var connectCellNum = doc.AddCell(openFace.EnvId, openFace.CellStruct,
                        newOrigin, newRot,
                        prefab.Cells[openFace.CellIndex].Surfaces.ToList());
                    cellMap[openFace.CellIndex] = connectCellNum;
                    if (!TryConnectPortalsSafe(doc, dats, existingCellNum, existingPolyId, connectCellNum, match.PolyId,
                            allowRemap: true, remapMaxCentroidDist: 0.75f))
                        doc.RemoveCell(connectCellNum);
                    else
                        PlaceRemainingCells(doc, dats, prefab, cellMap);
                    if (doc.GetCell(connectCellNum) != null)
                        return cellMap.Values.Where(cn => doc.GetCell(cn) != null).ToList();
                }
            }

            return TryAttachWithGeometricSnap(doc, dats, geoCache,
                existingCell, existingCellNum, existingPolyId,
                prefab, placedPositions, placedAABBs);
        }

        private static List<ushort>? TryAttachWithProvenTransform(
            DungeonDocument doc, IDatReaderWriter dats, PortalGeometryCache geoCache,
            DungeonCellData existingCell, ushort existingCellNum, ushort existingPolyId,
            DungeonPrefab prefab, CompatibleRoom matchedRoom,
            List<Vector3> placedPositions,
            List<RoomAABB>? placedAABBs = null) {

            PrefabOpenFace? connectingFace = null;
            foreach (var of in prefab.OpenFaces) {
                if (of.EnvId == matchedRoom.EnvId && of.CellStruct == matchedRoom.CellStruct) {
                    connectingFace = of;
                    break;
                }
            }
            if (connectingFace == null) return null;

            var newOrigin = existingCell.Origin + Vector3.Transform(matchedRoom.RelOffset, existingCell.Orientation);
            var newRot = ConstrainToYaw(Quaternion.Normalize(existingCell.Orientation * matchedRoom.RelRot));

            if (WouldOverlap(newOrigin, placedPositions, prefab, connectingFace.CellIndex, newRot,
                    excludeOrigin: existingCell.Origin,
                    placedAABBs: placedAABBs, geoCache: geoCache))
                return null;

            var cellMap = new Dictionary<int, ushort>();
            var prefabCell = prefab.Cells[connectingFace.CellIndex];
            var connectCellNum = doc.AddCell(prefabCell.EnvId, prefabCell.CellStruct,
                newOrigin, newRot, prefabCell.Surfaces.ToList());
            cellMap[connectingFace.CellIndex] = connectCellNum;

            if (!TryConnectPortalsSafe(doc, dats, existingCellNum, existingPolyId, connectCellNum, matchedRoom.PolyId,
                    allowRemap: true, remapMaxCentroidDist: 0.75f)) {
                doc.RemoveCell(connectCellNum);
                return null;
            }

            PlaceRemainingCells(doc, dats, prefab, cellMap);
            return cellMap.Values.Where(cn => doc.GetCell(cn) != null).ToList();
        }

        private static List<ushort>? TryAttachWithGeometricSnap(
            DungeonDocument doc, IDatReaderWriter dats, PortalGeometryCache geoCache,
            DungeonCellData existingCell, ushort existingCellNum, ushort existingPolyId,
            DungeonPrefab prefab, List<Vector3> placedPositions,
            List<RoomAABB>? placedAABBs = null) {

            uint existingEnvFileId = (uint)(existingCell.EnvironmentId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(existingEnvFileId, out var existingEnv)) return null;
            if (!existingEnv.Cells.TryGetValue(existingCell.CellStructure, out var existingCS)) return null;

            var targetGeom = PortalSnapper.GetPortalGeometry(existingCS, existingPolyId);
            if (targetGeom == null) return null;

            var (targetCentroid, targetNormal) = PortalSnapper.TransformPortalToWorld(
                targetGeom.Value, existingCell.Origin, existingCell.Orientation);

            foreach (var openFace in prefab.OpenFaces) {
                var prefabCell = prefab.Cells[openFace.CellIndex];
                uint prefabEnvFileId = (uint)(prefabCell.EnvId | 0x0D000000);
                if (!dats.TryGet<Acme.Dat.Environment>(prefabEnvFileId, out var prefabEnv)) continue;
                if (!prefabEnv.Cells.TryGetValue(prefabCell.CellStruct, out var prefabCS)) continue;

                if (!geoCache.AreCompatible(
                    existingCell.EnvironmentId, existingCell.CellStructure, existingPolyId,
                    prefabCell.EnvId, prefabCell.CellStruct, openFace.PolyId))
                    continue;

                var sourceGeom = PortalSnapper.GetPortalGeometry(prefabCS, openFace.PolyId);
                if (sourceGeom == null) continue;

                var (snapOrigin, snapRot) = PortalSnapper.ComputeFlushSnap(
                    targetCentroid, targetNormal, sourceGeom.Value);

                if (WouldOverlap(snapOrigin, placedPositions, prefab, openFace.CellIndex, snapRot,
                        excludeOrigin: existingCell.Origin,
                        placedAABBs: placedAABBs, geoCache: geoCache))
                    continue;

                var cellMap = new Dictionary<int, ushort>();
                var connectCellNum = doc.AddCell(prefabCell.EnvId, prefabCell.CellStruct,
                    snapOrigin, snapRot, prefabCell.Surfaces.ToList());
                cellMap[openFace.CellIndex] = connectCellNum;

                if (!TryConnectPortalsSafe(doc, dats, existingCellNum, existingPolyId, connectCellNum, openFace.PolyId,
                        allowRemap: true, remapMaxCentroidDist: 0.75f)) {
                    doc.RemoveCell(connectCellNum);
                    continue;
                }
                PlaceRemainingCells(doc, dats, prefab, cellMap);

                return cellMap.Values.Where(cn => doc.GetCell(cn) != null).ToList();
            }

            return null;
        }

        /// <param name="overlapScale">Multiplier for overlap radius (1.0 = normal, 0.1 = relaxed for capping).</param>
        private static ushort? TryPlaceEdgeDirectBridge(
            DungeonDocument doc, IDatReaderWriter dats, PortalGeometryCache geoCache,
            DungeonKnowledgeBase kb,
            DungeonCellData existingCell, ushort existingCellNum, ushort existingPolyId,
            CompatibleRoom bridgeRoom, List<Vector3> placedPositions,
            Dictionary<(ushort, ushort), float>? roomBounds,
            float overlapScale = 1.0f,
            List<RoomAABB>? placedAABBs = null) {

            var newOrigin = existingCell.Origin + Vector3.Transform(bridgeRoom.RelOffset, existingCell.Orientation);
            var newRot = ConstrainToYaw(Quaternion.Normalize(existingCell.Orientation * bridgeRoom.RelRot));

            if (WouldOverlapSingle(newOrigin, placedPositions, bridgeRoom.EnvId, bridgeRoom.CellStruct, roomBounds,
                    excludeOrigin: existingCell.Origin, overlapScale: overlapScale,
                    placedAABBs: placedAABBs, geoCache: geoCache, orientation: newRot))
                return null;

            // Get exact surfaces from the DAT by finding a real EnvCell that uses
            // this room type. The catalog's SampleSurfaces may have the wrong count.
            var surfaces = FindSurfacesFromDat(dats, bridgeRoom.EnvId, bridgeRoom.CellStruct);
            if (surfaces == null || surfaces.Count == 0) {
                var catalogRoom = kb.Catalog.FirstOrDefault(cr =>
                    cr.EnvId == bridgeRoom.EnvId && cr.CellStruct == bridgeRoom.CellStruct);
                if (catalogRoom?.SampleSurfaces != null && catalogRoom.SampleSurfaces.Count > 0)
                    surfaces = new List<ushort>(catalogRoom.SampleSurfaces);
                else
                    surfaces = new List<ushort> { 0x032A };
            }

            var cellNum = doc.AddCell(bridgeRoom.EnvId, bridgeRoom.CellStruct,
                newOrigin, newRot, surfaces);
            if (!TryConnectPortalsSafe(doc, dats, existingCellNum, existingPolyId, cellNum, bridgeRoom.PolyId,
                    allowRemap: true, remapMaxCentroidDist: 0.75f)) {
                doc.RemoveCell(cellNum);
                return null;
            }
            return cellNum;
        }

        /// <summary>
        /// Scan LandBlockInfo entries to find an EnvCell that uses the given room type
        /// and return its surface list. This gives us the exact surface count that the
        /// geometry requires, avoiding rendering errors from mismatched surface slots.
        /// </summary>
        private static List<ushort>? FindSurfacesFromDat(IDatReaderWriter dats, ushort envId, ushort cellStruct) {
            try {
                var lbiIds = dats.Dats.GetAllIdsOfType<LandBlockInfo>().Take(2000).ToArray();
                if (lbiIds.Length == 0) lbiIds = dats.Cell().GetAllIdsOfType<LandBlockInfo>().Take(2000).ToArray();
                foreach (var lbiId in lbiIds) {
                    if (!dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) continue;
                    uint lbId = lbiId >> 16;
                    for (uint i = 0; i < lbi.NumCells && i < 100; i++) {
                        uint cellId = (lbId << 16) | (0x0100 + i);
                        if (!dats.TryGet<EnvCell>(cellId, out var ec)) continue;
                        if (ec.EnvironmentId == envId && ec.CellStructure == cellStruct && ec.Surfaces.Count > 0)
                            return new List<ushort>(ec.Surfaces.Select(s => (ushort)s));
                    }
                }
            } catch { }
            return null;
        }

        /// <summary>
        /// Check if placing a prefab at the given position would overlap existing cells.
        /// Uses AABB intersection testing against all placed rooms, falling back to
        /// origin-distance when AABB data is unavailable.
        /// </summary>
        private static bool WouldOverlap(Vector3 connectOrigin, List<Vector3> existing,
            DungeonPrefab prefab, int connectCellIdx, Quaternion connectRot,
            Dictionary<(ushort, ushort), float>? roomBounds = null,
            Vector3? excludeOrigin = null,
            List<RoomAABB>? placedAABBs = null, PortalGeometryCache? geoCache = null) {

            if (existing.Count == 0) return false;

            var connectPC = prefab.Cells[connectCellIdx];
            var connectOffset = new Vector3(connectPC.OffsetX, connectPC.OffsetY, connectPC.OffsetZ);
            var connectRelRot = Quaternion.Normalize(new Quaternion(connectPC.RotX, connectPC.RotY, connectPC.RotZ, connectPC.RotW));

            Quaternion invRelRot = connectRelRot.LengthSquared() > 0.01f ? Quaternion.Inverse(connectRelRot) : Quaternion.Identity;
            var worldBaseRot = Quaternion.Normalize(connectRot * invRelRot);
            var worldBaseOrigin = connectOrigin - Vector3.Transform(connectOffset, worldBaseRot);

            for (int i = 0; i < prefab.Cells.Count; i++) {
                var pc = prefab.Cells[i];
                Vector3 cellWorldPos;
                Quaternion cellWorldRot;
                if (i == connectCellIdx) {
                    cellWorldPos = connectOrigin;
                    cellWorldRot = connectRot;
                }
                else {
                    var offset = new Vector3(pc.OffsetX, pc.OffsetY, pc.OffsetZ);
                    cellWorldPos = worldBaseOrigin + Vector3.Transform(offset, worldBaseRot);
                    var relRot = Quaternion.Normalize(new Quaternion(pc.RotX, pc.RotY, pc.RotZ, pc.RotW));
                    cellWorldRot = Quaternion.Normalize(worldBaseRot * relRot);
                }

                if (WouldOverlapSingle(cellWorldPos, existing, pc.EnvId, pc.CellStruct,
                        roomBounds, excludeOrigin, overlapScale: 1.0f,
                        placedAABBs, geoCache, cellWorldRot))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Check if placing a single room at the given position would overlap existing cells.
        /// Uses AABB intersection when geometry data is available, with origin-distance fallback.
        /// </summary>
        private static bool WouldOverlapSingle(Vector3 origin, List<Vector3> existing,
            ushort envId, ushort cellStruct, Dictionary<(ushort, ushort), float>? roomBounds,
            Vector3? excludeOrigin = null, float overlapScale = 1.0f,
            List<RoomAABB>? placedAABBs = null, PortalGeometryCache? geoCache = null,
            Quaternion? orientation = null) {

            // AABB path: test actual geometry intersection
            if (placedAABBs != null && placedAABBs.Count > 0 && geoCache != null) {
                var localAABB = geoCache.GetAABB(envId, cellStruct);
                if (localAABB.HasValue) {
                    var rot = orientation ?? Quaternion.Identity;
                    var candidateAABB = localAABB.Value.ToWorldSpace(origin, rot);

                    int excludeIdx = -1;
                    if (excludeOrigin.HasValue) {
                        float bestDist = 1f;
                        for (int k = 0; k < existing.Count && k < placedAABBs.Count; k++) {
                            float d = (existing[k] - excludeOrigin.Value).LengthSquared();
                            if (d < bestDist) {
                                bestDist = d;
                                excludeIdx = k;
                            }
                        }
                    }

                    float clampedScale = MathF.Min(overlapScale, 1f);
                    float effectiveShrink = AABBShrink + (1f - clampedScale) * 0.75f;

                    for (int k = 0; k < placedAABBs.Count; k++) {
                        if (k == excludeIdx) continue;
                        if (candidateAABB.Intersects(placedAABBs[k], effectiveShrink))
                            return true;
                    }
                    return false;
                }
            }

            // Fallback: origin-distance check
            if (overlapScale <= 0f) return false;

            float halfWidth = OverlapMinDistDefault;
            if (roomBounds != null && roomBounds.TryGetValue((envId, cellStruct), out var r))
                halfWidth = MathF.Max(r, 3.0f);
            float radius = halfWidth * 1.5f * overlapScale;

            foreach (var ep in existing) {
                if (excludeOrigin.HasValue && (ep - excludeOrigin.Value).LengthSquared() < 1f)
                    continue;
                if ((origin - ep).Length() < radius) return true;
            }
            return false;
        }

        /// <summary>
        /// Compute world-space AABB for a cell and add it to the tracking list.
        /// </summary>
        private static void TrackPlacedAABB(List<RoomAABB> placedAABBs, PortalGeometryCache geoCache,
            ushort envId, ushort cellStruct, Vector3 origin, Quaternion orientation) {
            var localAABB = geoCache.GetAABB(envId, cellStruct);
            if (localAABB.HasValue) {
                placedAABBs.Add(localAABB.Value.ToWorldSpace(origin, orientation));
            } else {
                placedAABBs.Add(new RoomAABB {
                    Min = origin - new Vector3(OverlapMinDistDefault),
                    Max = origin + new Vector3(OverlapMinDistDefault)
                });
            }
        }

        private static (int pairCount, List<string> samples) AuditFinalOverlaps(
            DungeonDocument doc,
            PortalGeometryCache geoCache) {
            var cells = doc.Cells;
            if (cells.Count < 2) return (0, new List<string>());

            var byId = cells.ToDictionary(c => c.CellNumber, c => c);
            var neighborPairs = new HashSet<(ushort a, ushort b)>();
            foreach (var c in cells) {
                foreach (var p in c.CellPortals) {
                    if (!byId.ContainsKey(p.OtherCellId)) continue;
                    var a = (ushort)Math.Min(c.CellNumber, p.OtherCellId);
                    var b = (ushort)Math.Max(c.CellNumber, p.OtherCellId);
                    neighborPairs.Add((a, b));
                }
            }

            var worldAabbs = new List<(ushort cellNum, RoomAABB aabb)>(cells.Count);
            foreach (var c in cells) {
                var local = geoCache.GetAABB(c.EnvironmentId, c.CellStructure);
                RoomAABB world = local.HasValue
                    ? local.Value.ToWorldSpace(c.Origin, c.Orientation)
                    : new RoomAABB {
                        Min = c.Origin - new Vector3(OverlapMinDistDefault),
                        Max = c.Origin + new Vector3(OverlapMinDistDefault)
                    };
                worldAabbs.Add((c.CellNumber, world));
            }

            int overlaps = 0;
            var samples = new List<string>();
            for (int i = 0; i < worldAabbs.Count; i++) {
                for (int j = i + 1; j < worldAabbs.Count; j++) {
                    var aNum = worldAabbs[i].cellNum;
                    var bNum = worldAabbs[j].cellNum;
                    var key = ((ushort)Math.Min(aNum, bNum), (ushort)Math.Max(aNum, bNum));
                    if (neighborPairs.Contains(key)) continue; // touching neighbors at a portal is expected
                    if (!worldAabbs[i].aabb.Intersects(worldAabbs[j].aabb, AABBShrink)) continue;
                    overlaps++;
                    if (samples.Count < 6) samples.Add($"0x{aNum:X4}-0x{bNum:X4}");
                }
            }
            return (overlaps, samples);
        }

        /// <summary>
        /// Find cells to remove to eliminate non-neighbor overlaps.
        /// For each overlap pair, prefer removing the cell with fewer portal connections
        /// (likely a dead-end cap). Never remove cells that would disconnect the dungeon.
        /// </summary>
        private static List<ushort> FindOverlapVictims(DungeonDocument doc, PortalGeometryCache geoCache) {
            var cells = doc.Cells;
            var byId = cells.ToDictionary(c => c.CellNumber, c => c);
            var neighborPairs = new HashSet<(ushort a, ushort b)>();
            var connectionCount = new Dictionary<ushort, int>();
            foreach (var c in cells) {
                connectionCount[c.CellNumber] = c.CellPortals.Count;
                foreach (var p in c.CellPortals) {
                    if (!byId.ContainsKey(p.OtherCellId)) continue;
                    var a = (ushort)Math.Min(c.CellNumber, p.OtherCellId);
                    var b = (ushort)Math.Max(c.CellNumber, p.OtherCellId);
                    neighborPairs.Add((a, b));
                }
            }

            var worldAabbs = cells.ToDictionary(
                c => c.CellNumber,
                c => {
                    var local = geoCache.GetAABB(c.EnvironmentId, c.CellStructure);
                    return local.HasValue
                        ? local.Value.ToWorldSpace(c.Origin, c.Orientation)
                        : new RoomAABB {
                            Min = c.Origin - new Vector3(OverlapMinDistDefault),
                            Max = c.Origin + new Vector3(OverlapMinDistDefault)
                        };
                });

            var victims = new HashSet<ushort>();
            foreach (var ci in cells) {
                if (victims.Contains(ci.CellNumber)) continue;
                foreach (var cj in cells) {
                    if (ci.CellNumber >= cj.CellNumber) continue;
                    if (victims.Contains(cj.CellNumber)) continue;
                    var key = ((ushort)Math.Min(ci.CellNumber, cj.CellNumber),
                               (ushort)Math.Max(ci.CellNumber, cj.CellNumber));
                    if (neighborPairs.Contains(key)) continue;
                    if (!worldAabbs[ci.CellNumber].Intersects(worldAabbs[cj.CellNumber], AABBShrink)) continue;

                    int connI = connectionCount.GetValueOrDefault(ci.CellNumber, 0);
                    int connJ = connectionCount.GetValueOrDefault(cj.CellNumber, 0);
                    // Only remove dead-end caps (exactly 1 connection). Removing
                    // corridors (2 connections) severs the dungeon graph, stranding
                    // everything beyond them.
                    ushort victim;
                    if (connI == 1) victim = ci.CellNumber;
                    else if (connJ == 1) victim = cj.CellNumber;
                    else continue;
                    victims.Add(victim);
                }
            }
            return victims.OrderByDescending(v => v).ToList();
        }

        private static ushort? TryCapWithGeometricSnap(
            DungeonDocument doc, IDatReaderWriter dats, PortalGeometryCache geoCache,
            DungeonKnowledgeBase kb,
            DungeonCellData existingCell, ushort existingCellNum, ushort existingPolyId,
            CompatibleRoom capRoom, List<Vector3> placedPositions,
            List<RoomAABB>? placedAABBs = null,
            float overlapScale = 0.1f) {

            uint existingEnvFileId = (uint)(existingCell.EnvironmentId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(existingEnvFileId, out var existingEnv)) return null;
            if (!existingEnv.Cells.TryGetValue(existingCell.CellStructure, out var existingCS)) return null;

            var targetGeom = PortalSnapper.GetPortalGeometry(existingCS, existingPolyId);
            if (targetGeom == null) return null;

            var (targetCentroid, targetNormal) = PortalSnapper.TransformPortalToWorld(
                targetGeom.Value, existingCell.Origin, existingCell.Orientation);

            uint capEnvFileId = (uint)(capRoom.EnvId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(capEnvFileId, out var capEnv)) return null;
            if (!capEnv.Cells.TryGetValue(capRoom.CellStruct, out var capCS)) return null;

            var portalIds = PortalSnapper.GetPortalPolygonIds(capCS);
            if (portalIds.Count == 0) return null;

            // Use the specified portal or find the best matching one
            ushort capPolyId = capRoom.PolyId;
            if (!capCS.Polygons.ContainsKey(capPolyId) && portalIds.Count > 0)
                capPolyId = portalIds[0];

            var sourceGeom = PortalSnapper.GetPortalGeometry(capCS, capPolyId);
            if (sourceGeom == null) return null;

            var (snapOrigin, snapRot) = PortalSnapper.ComputeFlushSnap(
                targetCentroid, targetNormal, sourceGeom.Value);

            if (WouldOverlapSingle(snapOrigin, placedPositions, capRoom.EnvId, capRoom.CellStruct, null,
                    excludeOrigin: existingCell.Origin, overlapScale: overlapScale,
                    placedAABBs: placedAABBs, geoCache: geoCache, orientation: snapRot))
                return null;

            var surfaces = FindSurfacesFromDat(dats, capRoom.EnvId, capRoom.CellStruct);
            if (surfaces == null || surfaces.Count == 0) {
                var catalogRoom = kb.Catalog.FirstOrDefault(cr =>
                    cr.EnvId == capRoom.EnvId && cr.CellStruct == capRoom.CellStruct);
                if (catalogRoom?.SampleSurfaces != null && catalogRoom.SampleSurfaces.Count > 0)
                    surfaces = new List<ushort>(catalogRoom.SampleSurfaces);
                else
                    surfaces = new List<ushort> { 0x032A };
            }

            var cellNum = doc.AddCell(capRoom.EnvId, capRoom.CellStruct,
                snapOrigin, snapRot, surfaces);
            if (!TryConnectPortalsSafe(doc, dats, existingCellNum, existingPolyId, cellNum, capPolyId,
                    allowRemap: true, remapMaxCentroidDist: 0.75f)) {
                doc.RemoveCell(cellNum);
                return null;
            }
            return cellNum;
        }

        /// <summary>
        /// Set the exact_match flag on portal pairs where both sides have the same
        /// portal polygon geometry (area, vertex count). The AC client uses this
        /// flag for rendering optimization and proper portal clipping.
        /// </summary>
        private static int SetExactMatchFlags(DungeonDocument doc, IDatReaderWriter dats,
            PortalGeometryCache geoCache) {
            int fixes = 0;
            foreach (var cell in doc.Cells) {
                foreach (var portal in cell.CellPortals) {
                    var otherCell = doc.GetCell(portal.OtherCellId);
                    if (otherCell == null) continue;

                    var geoA = geoCache.Get(cell.EnvironmentId, cell.CellStructure, portal.PolygonId);
                    var geoB = geoCache.Get(otherCell.EnvironmentId, otherCell.CellStructure, portal.OtherPortalId);

                    if (geoA != null && geoB != null) {
                        float areaRatio = MathF.Min(geoA.Area, geoB.Area) / MathF.Max(geoA.Area, geoB.Area);
                        bool isExact = areaRatio > 0.95f && geoA.VertexCount == geoB.VertexCount;

                        // AC client: exact_match is bit 0 of the flags word.
                        // portal_side is bit 1 (inverted in the packed format, but
                        // RecomputePortalFlags handles that separately).
                        if (isExact && (portal.Flags & 0x0001) == 0) {
                            portal.Flags |= 0x0001;
                            fixes++;
                        }
                    }
                }
            }
            if (fixes > 0) doc.MarkDirty();
            return fixes;
        }

        private static void PlaceRemainingCells(
            DungeonDocument doc, IDatReaderWriter dats,
            DungeonPrefab prefab, Dictionary<int, ushort> cellMap) {

            int baseIdx = cellMap.Keys.First();
            var baseCellNum = cellMap[baseIdx];
            var baseDoc = doc.GetCell(baseCellNum);
            if (baseDoc == null) return;

            var basePC = prefab.Cells[baseIdx];
            var baseOffset = new Vector3(basePC.OffsetX, basePC.OffsetY, basePC.OffsetZ);
            var baseRelRot = Quaternion.Normalize(new Quaternion(basePC.RotX, basePC.RotY, basePC.RotZ, basePC.RotW));

            Quaternion invBaseRelRot = baseRelRot.LengthSquared() > 0.01f ? Quaternion.Inverse(baseRelRot) : Quaternion.Identity;
            var worldBaseRot = Quaternion.Normalize(baseDoc.Orientation * invBaseRelRot);
            var worldBaseOrigin = baseDoc.Origin - Vector3.Transform(baseOffset, worldBaseRot);

            for (int i = 0; i < prefab.Cells.Count; i++) {
                if (cellMap.ContainsKey(i)) continue;

                var pc = prefab.Cells[i];
                var offset = new Vector3(pc.OffsetX, pc.OffsetY, pc.OffsetZ);
                var relRot = Quaternion.Normalize(new Quaternion(pc.RotX, pc.RotY, pc.RotZ, pc.RotW));

                var worldOrigin = worldBaseOrigin + Vector3.Transform(offset, worldBaseRot);
                var worldRot = ConstrainToYaw(Quaternion.Normalize(worldBaseRot * relRot));

                var newCellNum = doc.AddCell(pc.EnvId, pc.CellStruct,
                    worldOrigin, worldRot, pc.Surfaces.ToList());
                cellMap[i] = newCellNum;
            }

            foreach (var ip in prefab.InternalPortals) {
                if (cellMap.TryGetValue(ip.CellIndexA, out var cellA) && cellMap.TryGetValue(ip.CellIndexB, out var cellB)) {
                    TryConnectPortalsSafe(doc, dats, cellA, ip.PolyIdA, cellB, ip.PolyIdB,
                        allowRemap: true, remapMaxCentroidDist: 0.35f);
                }
            }

            // Keep prefab placement coherent: drop any newly added cells that are not
            // reachable from the anchor cell after internal portal stitching.
            var placed = new HashSet<ushort>(cellMap.Values);
            if (placed.Count <= 1) return;

            var reachable = new HashSet<ushort>();
            var q = new Queue<ushort>();
            q.Enqueue(baseCellNum);
            reachable.Add(baseCellNum);

            while (q.Count > 0) {
                var cn = q.Dequeue();
                var dc = doc.GetCell(cn);
                if (dc == null) continue;
                foreach (var cp in dc.CellPortals) {
                    ushort other = cp.OtherCellId;
                    if (!placed.Contains(other)) continue;
                    if (reachable.Add(other))
                        q.Enqueue(other);
                }
            }

            var orphaned = placed.Where(cn => cn != baseCellNum && !reachable.Contains(cn)).ToList();
            foreach (var orphan in orphaned)
                doc.RemoveCell(orphan);
            if (orphaned.Count > 0) {
                Console.WriteLine($"[DungeonGen] Pruned {orphaned.Count} orphaned prefab cell(s) after internal portal stitching");
            }
        }

        private static bool TryConnectPortalsSafe(
            DungeonDocument doc, IDatReaderWriter dats,
            ushort cellNumA, ushort requestedPolyA,
            ushort cellNumB, ushort requestedPolyB,
            bool allowRemap,
            float remapMaxCentroidDist) {

            var cellA = doc.GetCell(cellNumA);
            var cellB = doc.GetCell(cellNumB);
            if (cellA == null || cellB == null) return false;

            uint envAId = (uint)(cellA.EnvironmentId | 0x0D000000);
            uint envBId = (uint)(cellB.EnvironmentId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(envAId, out var envA) ||
                !envA.Cells.TryGetValue(cellA.CellStructure, out var csA))
                return false;
            if (!dats.TryGet<Acme.Dat.Environment>(envBId, out var envB) ||
                !envB.Cells.TryGetValue(cellB.CellStructure, out var csB))
                return false;

            var validA = new HashSet<ushort>(PortalSnapper.GetPortalPolygonIds(csA));
            var validB = new HashSet<ushort>(PortalSnapper.GetPortalPolygonIds(csB));
            var usedA = new HashSet<ushort>(cellA.CellPortals.Select(p => p.PolygonId));
            var usedB = new HashSet<ushort>(cellB.CellPortals.Select(p => p.PolygonId));

            if (validA.Contains(requestedPolyA) && validB.Contains(requestedPolyB) &&
                !usedA.Contains(requestedPolyA) && !usedB.Contains(requestedPolyB)) {
                doc.ConnectPortals(cellNumA, requestedPolyA, cellNumB, requestedPolyB);
                return true;
            }

            if (!allowRemap) return false;

            var candA = validA.Where(p => !usedA.Contains(p)).ToList();
            var candB = validB.Where(p => !usedB.Contains(p)).ToList();
            if (candA.Count == 0 || candB.Count == 0) return false;

            float best = float.MaxValue;
            ushort bestA = 0, bestB = 0;
            bool found = false;
            float bestCentroidDist = float.MaxValue;
            float bestDot = 1f;
            int bestVertexDelta = int.MaxValue;

            foreach (var pa in candA) {
                var ga = PortalSnapper.GetPortalGeometry(csA, pa);
                if (ga == null) continue;
                var (ca, na) = PortalSnapper.TransformPortalToWorld(ga.Value, cellA.Origin, cellA.Orientation);
                foreach (var pb in candB) {
                    var gb = PortalSnapper.GetPortalGeometry(csB, pb);
                    if (gb == null) continue;
                    var (cb, nb) = PortalSnapper.TransformPortalToWorld(gb.Value, cellB.Origin, cellB.Orientation);

                    float centroidDist = Vector3.Distance(ca, cb);
                    float oppositePenalty = (1f + Vector3.Dot(Vector3.Normalize(na), Vector3.Normalize(nb))) * 2.5f;
                    float vertexPenalty = MathF.Abs(ga.Value.Vertices.Count - gb.Value.Vertices.Count) * 0.15f;
                    float requestBonus = 0f;
                    if (pa == requestedPolyA) requestBonus -= 0.25f;
                    if (pb == requestedPolyB) requestBonus -= 0.25f;

                    float score = centroidDist + oppositePenalty + vertexPenalty + requestBonus;
                    if (score < best) {
                        best = score;
                        bestA = pa;
                        bestB = pb;
                        bestCentroidDist = centroidDist;
                        bestDot = Vector3.Dot(Vector3.Normalize(na), Vector3.Normalize(nb));
                        bestVertexDelta = Math.Abs(ga.Value.Vertices.Count - gb.Value.Vertices.Count);
                        found = true;
                    }
                }
            }

            if (!found) return false;
            // Strict remap safety gate:
            // - portals must be nearly co-located after placement
            // - normals must be strongly opposing (facing each other)
            // - polygon complexity shouldn't diverge too much
            if (bestCentroidDist > remapMaxCentroidDist || bestDot > -0.70f || bestVertexDelta > 2)
                return false;
            doc.ConnectPortals(cellNumA, bestA, cellNumB, bestB);

            if (bestA != requestedPolyA || bestB != requestedPolyB) {
                Console.WriteLine(
                    $"[DungeonGen] Remapped portal link C{cellNumA:X4}:{requestedPolyA} -> {bestA}, C{cellNumB:X4}:{requestedPolyB} -> {bestB}");
            }
            return true;
        }

        /// <param name="geoCache">When non-null, skip portals with vertical normals (Up/Down).</param>
        private static void CollectOpenFaces(
            DungeonDocument doc, IDatReaderWriter dats,
            List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)> frontier,
            PortalGeometryCache? skipVerticalGeoCache = null) {

            foreach (var dc in doc.Cells) {
                uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
                if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(dc.CellStructure, out var cs)) continue;

                var allPortals = PortalSnapper.GetPortalPolygonIds(cs);
                var connected = new HashSet<ushort>(dc.CellPortals.Select(cp => cp.PolygonId));

                foreach (var pid in allPortals) {
                    if (connected.Contains(pid)) continue;

                    if (skipVerticalGeoCache != null) {
                        var geom = PortalSnapper.GetPortalGeometry(cs, pid);
                        if (geom != null) {
                            var worldNormal = Vector3.Transform(geom.Value.Normal, dc.Orientation);
                            if (MathF.Abs(worldNormal.Z) > 0.7f) continue;
                        }
                    }

                    frontier.Add((dc.CellNumber, pid, dc.EnvironmentId, dc.CellStructure));
                }
            }
        }

        /// <summary>
        /// Apply surfaces from favorited prefabs to generated cells. Builds a lookup of
        /// (envId, cellStruct) → surfaces from the favorites, then overwrites each generated
        /// cell's surfaces with the favorite version. This preserves the exact textures from
        /// the dungeons the user favorited.
        /// </summary>
        private static int ApplyThemeSurfaces(DungeonDocument doc, ushort wallSurface, ushort floorSurface) {
            int applied = 0;
            foreach (var dc in doc.Cells) {
                if (dc.Surfaces.Count == 0) continue;
                dc.Surfaces[0] = wallSurface;
                if (dc.Surfaces.Count >= 2) dc.Surfaces[1] = floorSurface;
                for (int i = 2; i < dc.Surfaces.Count; i++)
                    dc.Surfaces[i] = wallSurface;
                applied++;
            }
            doc.MarkDirty();
            return applied;
        }

        private static int ApplyFavoriteSurfaces(DungeonDocument doc, List<DungeonPrefab> favPrefabs) {
            var surfaceLookup = new Dictionary<(ushort, ushort), List<ushort>>();
            foreach (var fav in favPrefabs) {
                foreach (var cell in fav.Cells) {
                    if (cell.Surfaces.Count == 0) continue;
                    var key = (cell.EnvId, cell.CellStruct);
                    if (!surfaceLookup.ContainsKey(key))
                        surfaceLookup[key] = new List<ushort>(cell.Surfaces);
                }
            }

            int applied = 0;
            foreach (var dc in doc.Cells) {
                var key = (dc.EnvironmentId, dc.CellStructure);
                if (surfaceLookup.TryGetValue(key, out var favSurfaces) && favSurfaces.Count == dc.Surfaces.Count) {
                    dc.Surfaces.Clear();
                    dc.Surfaces.AddRange(favSurfaces);
                    applied++;
                }
            }
            return applied;
        }

        /// <summary>
        /// Retexture all cells in the document to use surfaces matching the target style.
        /// For each cell, looks up the catalog for a room of the same geometry + style.
        /// If found and surface slot count matches, replaces the cell's surfaces.
        /// Falls back to a style-wide "dominant palette" for cells without a direct match.
        /// </summary>
        /// <summary>
        /// Compute what surfaces should be applied to a single cell to match a given style.
        /// Returns the new surface list, or null if no match was found.
        /// Used by the editor's "Match to Style" command for undo-safe per-cell retexturing.
        /// </summary>
        internal static List<ushort>? ComputeRetexturedSurfaces(DungeonCellData dc, DungeonKnowledgeBase kb, string style) {
            var styleSurfaces = new Dictionary<(ushort, ushort), List<ushort>>();
            foreach (var cr in kb.Catalog) {
                if (!cr.Style.Equals(style, StringComparison.OrdinalIgnoreCase)) continue;
                if (cr.SampleSurfaces.Count == 0) continue;
                var key = (cr.EnvId, cr.CellStruct);
                if (!styleSurfaces.ContainsKey(key))
                    styleSurfaces[key] = cr.SampleSurfaces;
            }

            int needed = dc.Surfaces.Count;
            if (needed == 0) return null;

            // Direct match
            var roomKey = (dc.EnvironmentId, dc.CellStructure);
            if (styleSurfaces.TryGetValue(roomKey, out var directMatch) && directMatch.Count == needed)
                return new List<ushort>(directMatch);

            // Build fallback palette for this slot count
            var surfaceLists = styleSurfaces.Values.Where(s => s.Count == needed).ToList();
            if (surfaceLists.Count > 0) {
                var palette = new List<ushort>(needed);
                for (int i = 0; i < needed; i++) {
                    var freqs = new Dictionary<ushort, int>();
                    foreach (var sl in surfaceLists) {
                        freqs.TryGetValue(sl[i], out int f);
                        freqs[sl[i]] = f + 1;
                    }
                    palette.Add(freqs.Count > 0 ? freqs.OrderByDescending(kv => kv.Value).First().Key : (ushort)0x032A);
                }
                return palette;
            }

            // Stretch/shrink nearest slot count
            if (styleSurfaces.Count > 0) {
                var closest = styleSurfaces.Values
                    .OrderBy(s => Math.Abs(s.Count - needed))
                    .First();
                var stretched = new List<ushort>(needed);
                for (int i = 0; i < needed; i++)
                    stretched.Add(closest[i % closest.Count]);
                return stretched;
            }

            return null;
        }

        internal static int RetextureCells(DungeonDocument doc, IDatReaderWriter dats,
            DungeonKnowledgeBase kb, string style) {

            // Build lookup: (envId, cellStruct) → surfaces for the target style
            var styleSurfaces = new Dictionary<(ushort, ushort), List<ushort>>();
            foreach (var cr in kb.Catalog) {
                if (!cr.Style.Equals(style, StringComparison.OrdinalIgnoreCase)) continue;
                if (cr.SampleSurfaces.Count == 0) continue;
                var key = (cr.EnvId, cr.CellStruct);
                if (!styleSurfaces.ContainsKey(key))
                    styleSurfaces[key] = cr.SampleSurfaces;
            }

            // Build a fallback palette: for each required slot count, collect
            // the most common surface ID at each slot position across the style.
            var surfacesBySlotCount = new Dictionary<int, List<List<ushort>>>();
            foreach (var surfaces in styleSurfaces.Values) {
                int count = surfaces.Count;
                if (!surfacesBySlotCount.TryGetValue(count, out var lists)) {
                    lists = new List<List<ushort>>();
                    surfacesBySlotCount[count] = lists;
                }
                lists.Add(surfaces);
            }

            var fallbackPalette = new Dictionary<int, List<ushort>>();
            foreach (var (slotCount, surfaceLists) in surfacesBySlotCount) {
                var palette = new List<ushort>();
                for (int i = 0; i < slotCount; i++) {
                    var freqs = new Dictionary<ushort, int>();
                    foreach (var sl in surfaceLists) {
                        if (i < sl.Count) {
                            freqs.TryGetValue(sl[i], out int f);
                            freqs[sl[i]] = f + 1;
                        }
                    }
                    palette.Add(freqs.Count > 0 ? freqs.OrderByDescending(kv => kv.Value).First().Key : (ushort)0x032A);
                }
                fallbackPalette[slotCount] = palette;
            }

            int retextured = 0;
            foreach (var dc in doc.Cells) {
                if (dc.Surfaces.Count == 0) continue;
                int needed = dc.Surfaces.Count;

                // Direct match: same room type exists in the style
                var roomKey = (dc.EnvironmentId, dc.CellStructure);
                if (styleSurfaces.TryGetValue(roomKey, out var directMatch) && directMatch.Count == needed) {
                    dc.Surfaces.Clear();
                    dc.Surfaces.AddRange(directMatch);
                    retextured++;
                    continue;
                }

                // Fallback: use the dominant palette for this slot count
                if (fallbackPalette.TryGetValue(needed, out var palette)) {
                    dc.Surfaces.Clear();
                    dc.Surfaces.AddRange(palette);
                    retextured++;
                    continue;
                }

                // Last resort: find the closest slot count palette and stretch/shrink
                if (fallbackPalette.Count > 0) {
                    var closest = fallbackPalette.OrderBy(kv => Math.Abs(kv.Key - needed)).First().Value;
                    dc.Surfaces.Clear();
                    for (int i = 0; i < needed; i++)
                        dc.Surfaces.Add(closest[i % closest.Count]);
                    retextured++;
                }
            }

            return retextured;
        }

        /// <summary>
        /// Add static objects (torches, furniture, decorations) to generated rooms
        /// using the most common object placements observed for each room type.
        /// Objects are placed in cell-local coordinates and transformed to world space.
        /// </summary>
        private static int FurnishRooms(DungeonDocument doc, DungeonKnowledgeBase kb) {
            var staticsLookup = new Dictionary<(ushort, ushort), RoomStaticSet>();
            foreach (var rs in kb.RoomStatics)
                staticsLookup.TryAdd((rs.EnvId, rs.CellStruct), rs);

            int furnished = 0;
            foreach (var dc in doc.Cells) {
                if (dc.StaticObjects.Count > 0) continue;
                var key = (dc.EnvironmentId, dc.CellStructure);
                if (!staticsLookup.TryGetValue(key, out var statics)) continue;

                foreach (var sp in statics.Placements) {
                    var localPos = new Vector3(sp.X, sp.Y, sp.Z);
                    var localRot = new Quaternion(sp.RotX, sp.RotY, sp.RotZ, sp.RotW);

                    var worldPos = dc.Origin + Vector3.Transform(localPos, dc.Orientation);
                    var worldRot = Quaternion.Normalize(dc.Orientation * localRot);

                    dc.StaticObjects.Add(new WorldBuilder.Shared.Documents.DungeonStabData {
                        Id = sp.ObjectId,
                        Origin = worldPos,
                        Orientation = worldRot
                    });
                }
                if (statics.Placements.Count > 0) furnished++;
            }

            doc.MarkDirty();
            return furnished;
        }
    }
}
