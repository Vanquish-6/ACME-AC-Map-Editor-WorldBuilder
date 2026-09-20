using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Shared.Lib.AceDb {
    /// <summary>
    /// Compares old vs new terrain heights for modified landblocks and produces
    /// SQL UPDATE statements to reposition outdoor landblock_instance rows.
    /// </summary>
    public class InstanceRepositionService {

        /// <summary>
        /// Result of a reposition run.
        /// </summary>
        public class RepositionResult {
            public int InstancesChecked { get; set; }
            public int InstancesUpdated { get; set; }
            public int LandblocksProcessed { get; set; }
            public string? SqlFilePath { get; set; }
            public bool AppliedDirectly { get; set; }
            public string? Error { get; set; }
        }

        /// <summary>
        /// Runs the full reposition workflow: query DB, compute deltas, write SQL, optionally apply.
        /// </summary>
        public async Task<RepositionResult> RunAsync(
            AceDbSettings settings,
            RepositionContext ctx,
            CancellationToken ct = default) {

            var result = new RepositionResult();

            try {
                using var connector = new AceDbConnector(settings);

                var instances = await connector.GetOutdoorInstancesAsync(ctx.ModifiedLandblocks, ct);
                result.InstancesChecked = instances.Count;
                result.LandblocksProcessed = ctx.ModifiedLandblocks.Count;

                var updates = ComputeDeltas(instances, ctx, settings.Threshold);
                result.InstancesUpdated = updates.Count;

                if (updates.Count > 0) {
                    var sql = GenerateSql(updates, ctx, settings);
                    var sqlPath = Path.Combine(ctx.ExportDirectory, "reposition.sql");
                    await File.WriteAllTextAsync(sqlPath, sql, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
                    result.SqlFilePath = sqlPath;

                    if (settings.ApplyDirectly) {
                        var updateSql = GenerateExecutableSql(updates);
                        await connector.ExecuteSqlAsync(updateSql, ct);
                        result.AppliedDirectly = true;
                    }
                }
            }
            catch (Exception ex) {
                result.Error = ex.Message;
            }

            return result;
        }

        private List<InstanceUpdate> ComputeDeltas(
            List<LandblockInstanceRecord> instances,
            RepositionContext ctx,
            float threshold) {

            var updates = new List<InstanceUpdate>();

            foreach (var inst in instances) {
                if (!inst.IsOutdoor) continue;

                ushort lbId = inst.LandblockId;
                if (!ctx.OldTerrain.TryGetValue(lbId, out var oldEntries)) continue;
                if (!ctx.NewTerrain.TryGetValue(lbId, out var newEntries)) continue;

                uint landblockX = (uint)(lbId >> 8) & 0xFF;
                uint landblockY = (uint)lbId & 0xFF;

                float oldZ = TerrainHeightSampler.SampleHeightTriangle(
                    oldEntries, ctx.LandHeightTable,
                    inst.OriginX, inst.OriginY,
                    landblockX, landblockY);

                float newZ = TerrainHeightSampler.SampleHeightTriangle(
                    newEntries, ctx.LandHeightTable,
                    inst.OriginX, inst.OriginY,
                    landblockX, landblockY);

                float delta = newZ - oldZ;

                if (MathF.Abs(delta) < threshold) continue;

                updates.Add(new InstanceUpdate {
                    Record = inst,
                    OldTerrainZ = oldZ,
                    NewTerrainZ = newZ,
                    Delta = delta,
                    NewOriginZ = inst.OriginZ + delta
                });
            }

            return updates;
        }

        private static string GenerateSql(
            List<InstanceUpdate> updates,
            RepositionContext ctx,
            AceDbSettings settings) {

            var sb = new StringBuilder();
            sb.AppendLine("-- ACME WorldBuilder: Instance Reposition");
            sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            var lbList = string.Join(", ", FormatLandblockIds(ctx.ModifiedLandblocks));
            sb.AppendLine($"-- Modified landblocks: {lbList}");
            sb.AppendLine($"-- Threshold: {settings.Threshold} units");
            sb.AppendLine($"-- Instances updated: {updates.Count}");
            sb.AppendLine();
            sb.AppendLine($"USE `{settings.Database}`;");
            sb.AppendLine();

            ushort currentLb = 0;
            int countForLb = 0;

            foreach (var u in updates) {
                if (u.Record.LandblockId != currentLb) {
                    if (currentLb != 0) sb.AppendLine();
                    currentLb = u.Record.LandblockId;
                    countForLb = 0;
                    foreach (var u2 in updates) {
                        if (u2.Record.LandblockId == currentLb) countForLb++;
                    }
                    sb.AppendLine($"-- Landblock 0x{currentLb:X4}: {countForLb} instances adjusted");
                }

                string sign = u.Delta >= 0 ? "+" : "";
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "UPDATE `landblock_instance` SET `origin_Z` = {0:F6} WHERE `guid` = {1}; -- was {2:F6}, delta {3}{4:F6}",
                    u.NewOriginZ, u.Record.Guid, u.Record.OriginZ, sign, u.Delta));
            }

            return sb.ToString();
        }

        private static string GenerateExecutableSql(List<InstanceUpdate> updates) {
            var sb = new StringBuilder();
            foreach (var u in updates) {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "UPDATE `landblock_instance` SET `origin_Z` = {0:F6} WHERE `guid` = {1};",
                    u.NewOriginZ, u.Record.Guid));
            }
            return sb.ToString();
        }

        private static IEnumerable<string> FormatLandblockIds(IEnumerable<ushort> ids) {
            foreach (var id in ids) {
                yield return $"0x{id:X4}";
            }
        }

        private class InstanceUpdate {
            public required LandblockInstanceRecord Record { get; init; }
            public float OldTerrainZ { get; init; }
            public float NewTerrainZ { get; init; }
            public float Delta { get; init; }
            public float NewOriginZ { get; init; }
        }
    }
}
