using System;
using System.Collections.Generic;
using System.Linq;
using Acme.Dat;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    /// <summary>
    /// Learns which building models behave like "complete" structures in retail-style
    /// settlement landblocks: multiple buildings, decorations, and variety. Halves and
    /// facades tend to appear 2+ times in the same landblock next to their mirror piece.
    /// </summary>
    public static class RetailTownBuildingScanner {
        /// <summary>
        /// Per model: how many town-like landblocks contain it, how often it is the only
        /// copy there, and the max copies seen in any single town-like landblock.
        /// </summary>
        public record RetailTownModelStats {
            public uint ModelId { get; init; }
            public int TownLandblockHits { get; init; }
            public int SingletonTownHits { get; init; }
            public int MaxCopiesInOneTownLb { get; init; }
            public float SingletonRatio =>
                TownLandblockHits > 0 ? (float)SingletonTownHits / TownLandblockHits : 0f;
        }

        /// <summary>
        /// Heuristic "this outdoor landblock looks like a town/hamlet" — not dungeons,
        /// not a lone hut. Tuned from typical AC surface LBI patterns.
        /// </summary>
        public static bool IsTownLikeLandblock(LandBlockInfo lbi) {
            int bCount = lbi.Buildings?.Count ?? 0;
            if (bCount < 3) return false;

            int objCount = lbi.Objects?.Count ?? 0;
            if (objCount < 12) return false;

            int distinctModels = lbi.Buildings.Select(x => x.ModelId).Distinct().Count();
            if (distinctModels < 2 && bCount < 6) return false;

            return true;
        }

        /// <summary>
        /// Scans all LandBlockInfo and aggregates stats for building models in town-like blocks.
        /// </summary>
        public static Dictionary<uint, RetailTownModelStats> Scan(IDatReaderWriter dats) {
            var acc = new Dictionary<uint, (int hits, int singletons, int maxOneLb)>();

            var allLbiIds = dats.Cell().GetAllIdsOfType<LandBlockInfo>().ToArray();
            IEnumerable<uint> lbiSource;
            if (allLbiIds.Length > 0) {
                lbiSource = allLbiIds;
            } else {
                var brute = new List<uint>(255 * 255);
                for (uint x = 0; x < 255; x++)
                    for (uint y = 0; y < 255; y++)
                        brute.Add((uint)(((x << 8) | y) << 16 | 0xFFFE));
                lbiSource = brute;
            }

            int townLbCount = 0;
            foreach (var infoId in lbiSource) {
                if (!dats.TryGet<LandBlockInfo>(infoId, out var lbi)) continue;
                if (lbi.Buildings == null || lbi.Buildings.Count == 0) continue;
                if (!IsTownLikeLandblock(lbi)) continue;

                townLbCount++;

                var counts = new Dictionary<uint, int>();
                foreach (var b in lbi.Buildings) {
                    uint mid = b.ModelId;
                    counts.TryGetValue(mid, out int c);
                    counts[mid] = c + 1;
                }

                foreach (var (mid, cnt) in counts) {
                    if (!acc.TryGetValue(mid, out var t))
                        t = (0, 0, 0);
                    int singleton = cnt == 1 ? 1 : 0;
                    int maxOne = Math.Max(t.maxOneLb, cnt);
                    acc[mid] = (t.hits + 1, t.singletons + singleton, maxOne);
                }
            }

            var result = acc.ToDictionary(
                kv => kv.Key,
                kv => new RetailTownModelStats {
                    ModelId = kv.Key,
                    TownLandblockHits = kv.Value.hits,
                    SingletonTownHits = kv.Value.singletons,
                    MaxCopiesInOneTownLb = kv.Value.maxOneLb
                });

            Console.WriteLine($"[RetailTown] Town-like surface landblocks: {townLbCount}");
            Console.WriteLine($"[RetailTown] Building models seen in those blocks: {result.Count}");
            return result;
        }

        /// <summary>
        /// True if retail town data suggests a standalone, complete building.
        /// </summary>
        /// <param name="isPairedHalf">Global avg-per-landblock paired-half heuristic.</param>
        public static bool PassesRetailTownHeuristic(bool isPairedHalf, RetailTownModelStats? retail) {
            if (retail == null || retail.TownLandblockHits == 0)
                return true;

            if (retail.MaxCopiesInOneTownLb >= 3)
                return false;

            if (retail.MaxCopiesInOneTownLb >= 2 && isPairedHalf)
                return false;

            if (retail.TownLandblockHits >= 4 && retail.SingletonRatio < 0.45f)
                return false;

            if (retail.TownLandblockHits >= 8 && retail.SingletonRatio < 0.55f)
                return false;

            return true;
        }

        public static void LogTopRetailTownModels(
            Dictionary<uint, RetailTownModelStats> retail,
            IReadOnlyList<BuildingAnalyzer.BuildingProfile> profiles,
            int take = 25) {

            var byId = profiles.ToDictionary(p => p.ModelId);
            var lines = retail.Values
                .OrderByDescending(r => r.TownLandblockHits)
                .ThenByDescending(r => r.SingletonRatio)
                .Take(take)
                .ToList();

            Console.WriteLine($"[RetailTown] --- Top {take} building models in town-like landblocks (retail DAT) ---");
            foreach (var r in lines) {
                byId.TryGetValue(r.ModelId, out var p);
                string cells = p != null ? $"{p.CellCount}c/{p.PortalCount}p avgLb={p.AvgPerLandblock:F1}" : "?";
                Console.WriteLine(
                    $"[RetailTown]   0x{r.ModelId:X8}: townLBs={r.TownLandblockHits}, " +
                    $"singletonRatio={r.SingletonRatio:P0}, maxInOneLB={r.MaxCopiesInOneTownLb}, {cells}");
            }
        }
    }
}
