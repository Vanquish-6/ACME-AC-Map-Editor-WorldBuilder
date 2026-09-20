using Acme.Dat;
using System;
using WorldBuilder.Shared.Lib;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// Analyzes dungeon cells in the DAT to find the most-used room types.
    /// Use this to build curated "starter" presets from real game data.
    /// Resolves dungeon names from LocationDatabase for better discoverability.
    /// </summary>
    public static class DungeonRoomAnalyzer {

        public record RoomUsage(
            uint EnvFileId,
            ushort CellStructIndex,
            ushort EnvironmentId,
            int PortalCount,
            int UsageCount,
            List<ushort> SampleLandblockIds,
            List<string> SampleDungeonNames);

        public record AnalysisReport(
            DateTime AnalyzedAt,
            int TotalLandblocksScanned,
            int TotalCellsScanned,
            int UniqueRoomTypes,
            Dictionary<int, List<RoomUsage>> ByPortalCount,
            List<RoomUsage> TopStarterCandidates);

        /// <summary>
        /// Run analysis on the DAT. Scans all LandBlockInfo entries in cell.dat,
        /// counts (EnvironmentId, CellStruct) usage, and returns a report suitable
        /// for building preset lists.
        /// </summary>
        public static AnalysisReport Run(IDatReaderWriter dats) {
            var usageCount = new Dictionary<(ushort envId, ushort cellStruct), (int count, HashSet<ushort> landblocks)>();
            var portalCountCache = new Dictionary<(ushort envId, ushort cellStruct), int>();

            var lbiIds = dats.Dats.GetAllIdsOfType<LandBlockInfo>().ToArray();
            if (lbiIds.Length == 0) {
                lbiIds = dats.Cell().GetAllIdsOfType<LandBlockInfo>().ToArray();
            }
            if (lbiIds.Length == 0) {
                // Brute-force: GetAllIdsOfType often returns 0 for LandBlockInfo. Iterate all landblock IDs.
                System.Console.WriteLine("[DungeonRoomAnalyzer] GetAllIdsOfType returned 0, brute-force scanning landblocks...");
                var brute = new List<uint>();
                for (uint x = 0; x < 256; x++) {
                    for (uint y = 0; y < 256; y++) {
                        var infoId = ((x << 8) | y) << 16 | 0xFFFE;
                        if (dats.TryGet<LandBlockInfo>(infoId, out var lbi) && lbi.NumCells > 0)
                            brute.Add(infoId);
                    }
                }
                lbiIds = brute.ToArray();
                System.Console.WriteLine($"[DungeonRoomAnalyzer] Found {lbiIds.Length} landblocks with cells");
            }

            int totalCells = 0;
            foreach (var lbiId in lbiIds) {
                if (!dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) continue;

                uint lbId = lbiId >> 16;
                ushort lbKey = (ushort)(lbId & 0xFFFF);

                for (uint i = 0; i < lbi.NumCells; i++) {
                    uint cellId = (lbId << 16) | (0x0100 + i);
                    if (!dats.TryGet<EnvCell>(cellId, out var envCell)) continue;

                    var key = ((ushort)envCell.EnvironmentId, (ushort)envCell.CellStructure);
                    if (!usageCount.TryGetValue(key, out var entry)) {
                        entry = (0, new HashSet<ushort>());
                        usageCount[key] = entry;
                    }
                    entry.landblocks.Add(lbKey);
                    usageCount[key] = (entry.count + 1, entry.landblocks);
                    totalCells++;
                }
            }

            // Resolve portal counts from Environment
            foreach (var kvp in usageCount.Keys.ToList()) {
                uint envFileId = (uint)(kvp.envId | 0x0D000000);
                if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(kvp.cellStruct, out var cellStruct)) continue;

                var portalIds = PortalSnapper.GetPortalPolygonIds(cellStruct);
                portalCountCache[kvp] = portalIds.Count;
            }

            // Build RoomUsage entries, resolving dungeon names from LocationDatabase
            var allUsages = new List<RoomUsage>();
            foreach (var kvp in usageCount) {
                var (key, (count, landblocks)) = (kvp.Key, kvp.Value);
                portalCountCache.TryGetValue(key, out var portals);
                var sampleLbs = landblocks.Take(10).OrderBy(x => x).ToList();
                var dungeonNames = ResolveDungeonNames(sampleLbs);
                allUsages.Add(new RoomUsage(
                    (uint)(key.envId | 0x0D000000),
                    key.cellStruct,
                    key.envId,
                    portals,
                    count,
                    sampleLbs,
                    dungeonNames));
            }

            // Group by portal count
            var byPortalCount = allUsages
                .GroupBy(u => u.PortalCount)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(u => u.UsageCount).ToList());

            // Top starter candidates: pick top 2-3 per portal count (1, 2, 3, 4)
            var topStarter = new List<RoomUsage>();
            foreach (var pc in new[] { 1, 2, 3, 4 }) {
                if (byPortalCount.TryGetValue(pc, out var list)) {
                    topStarter.AddRange(list.Take(3));
                }
            }

            return new AnalysisReport(
                DateTime.UtcNow,
                lbiIds.Length,
                totalCells,
                allUsages.Count,
                byPortalCount,
                topStarter);
        }

        /// <summary>
        /// Save report to JSON and a human-readable summary.
        /// </summary>
        public static void SaveReport(AnalysisReport report, string outputPath) {
            var dir = Path.GetDirectoryName(outputPath) ?? ".";
            Directory.CreateDirectory(dir);

            var jsonPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(outputPath) + ".json");
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(jsonPath, json);

            var txtPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(outputPath) + ".txt");
            File.WriteAllText(txtPath, FormatSummary(report));
        }

        public static string FormatSummary(AnalysisReport report) {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Dungeon Room Analysis Report ===");
            sb.AppendLine($"Analyzed: {report.AnalyzedAt:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine($"Landblocks scanned: {report.TotalLandblocksScanned}");
            sb.AppendLine($"Total cells: {report.TotalCellsScanned}");
            sb.AppendLine($"Unique room types: {report.UniqueRoomTypes}");
            sb.AppendLine();

            sb.AppendLine("--- TOP STARTER CANDIDATES (for preset list) ---");
            foreach (var r in report.TopStarterCandidates) {
                var namePart = r.SampleDungeonNames.Count > 0
                    ? $" e.g. {string.Join(", ", r.SampleDungeonNames.Take(2))}"
                    : "";
                sb.AppendLine($"  {r.PortalCount}P: Env=0x{r.EnvFileId:X8} CellStruct={r.CellStructIndex}  (used {r.UsageCount}x){namePart}");
            }
            sb.AppendLine();

            sb.AppendLine("--- BY PORTAL COUNT ---");
            foreach (var kvp in report.ByPortalCount.OrderBy(x => x.Key)) {
                sb.AppendLine($"  {kvp.Key} portal(s): {kvp.Value.Count} room types");
                foreach (var r in kvp.Value.Take(5)) {
                    sb.AppendLine($"    Env=0x{r.EnvFileId:X8} CellStruct={r.CellStructIndex}  (used {r.UsageCount}x)");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Resolve landblock IDs to dungeon names from LocationDatabase.
        /// Returns distinct names (one per dungeon) for display/tooltips.
        /// </summary>
        private static List<string> ResolveDungeonNames(List<ushort> landblockIds) {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lbId in landblockIds) {
                var matches = LocationDatabase.Dungeons
                    .Where(e => e.LandblockId == lbId)
                    .Select(e => e.Name.Trim())
                    .Where(n => !string.IsNullOrEmpty(n));
                foreach (var name in matches.Take(2)) names.Add(name);
            }
            return names.OrderBy(n => n).ToList();
        }
    }
}
