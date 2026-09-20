using System;
using System.Collections.Generic;
using System.Linq;
using Acme.Dat;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    /// <summary>
    /// Scans the DAT for objects that naturally appear in landblocks alongside buildings.
    /// These are real town decorations: signs, barrels, crates, benches, lampposts, etc.
    /// Results are cached after first scan.
    /// </summary>
    public static class TownDecorationCatalog {
        private static List<uint>? _cachedDecorations;
        private static readonly object _lock = new();

        public static IReadOnlyList<uint> GetDecorations(IDatReaderWriter dats) {
            if (_cachedDecorations != null) return _cachedDecorations;
            lock (_lock) {
                if (_cachedDecorations != null) return _cachedDecorations;
                _cachedDecorations = Scan(dats);
                return _cachedDecorations;
            }
        }

        private static List<uint> Scan(IDatReaderWriter dats) {
            var buildingModelIds = new HashSet<uint>();
            var decorationCounts = new Dictionary<uint, int>();

            var allLbiIds = dats.Cell().GetAllIdsOfType<LandBlockInfo>().ToArray();
            if (allLbiIds.Length == 0) {
                for (uint x = 0; x < 255; x++) {
                    for (uint y = 0; y < 255; y++) {
                        var infoId = (uint)(((x << 8) | y) << 16 | 0xFFFE);
                        ScanLandblock(infoId, dats, buildingModelIds, decorationCounts);
                    }
                }
            } else {
                foreach (var infoId in allLbiIds) {
                    ScanLandblock(infoId, dats, buildingModelIds, decorationCounts);
                }
            }

            var result = decorationCounts
                .Where(kv => !buildingModelIds.Contains(kv.Key) && kv.Value >= 2)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            Console.WriteLine($"[TownDecorationCatalog] Scanned {allLbiIds.Length} landblocks, found {result.Count} decoration objects (from {decorationCounts.Count} total unique objects near buildings)");
            return result;
        }

        private static void ScanLandblock(uint infoId, IDatReaderWriter dats,
            HashSet<uint> buildingModelIds, Dictionary<uint, int> decorationCounts) {
            if (!dats.TryGet<LandBlockInfo>(infoId, out var lbi)) return;
            if (lbi.Buildings == null || lbi.Buildings.Count == 0) return;

            foreach (var b in lbi.Buildings)
                buildingModelIds.Add(b.ModelId);

            if (lbi.Objects == null) return;
            foreach (var obj in lbi.Objects) {
                if (obj.Id == 0) continue;
                decorationCounts.TryGetValue(obj.Id, out int count);
                decorationCounts[obj.Id] = count + 1;
            }
        }
    }
}
