using Acme.Dat;

namespace WorldBuilder.Shared.Lib {
    /// <summary>
    /// Post-export validation using the client's PortalChecksum algorithm on DAT file payloads.
    /// </summary>
    public sealed class DatExportChecksumReport {
        public List<string> Lines { get; } = new();
        public int WarningCount { get; private set; }
        public int TrackedCellMissing { get; private set; }
        public int TrackedPortalMissing { get; private set; }

        public bool HasTrackedMissingEntries => TrackedCellMissing > 0 || TrackedPortalMissing > 0;

        public void AddInfo(string line) => Lines.Add(line);

        public void AddWarning(string line) {
            WarningCount++;
            Lines.Add($"WARN: {line}");
        }

        internal void SetTrackedMissing(string datLabel, int missing) {
            if (datLabel == "client_cell_1.dat") {
                TrackedCellMissing = missing;
            }
            else if (datLabel == "client_portal.dat") {
                TrackedPortalMissing = missing;
            }
        }
    }

    /// <summary>File IDs written during the current export pass (for targeted checksum verification).</summary>
    public sealed class DatExportChecksumTargets {
        public HashSet<uint> CellFileIds { get; } = new();
        public HashSet<uint> PortalFileIds { get; } = new();

        public void TrackCell(uint fileId) {
            if (fileId != 0) CellFileIds.Add(fileId);
        }

        public void TrackPortal(uint fileId) {
            if (fileId != 0) PortalFileIds.Add(fileId);
        }
    }

    public static class DatExportChecksumValidator {
        static readonly string[] DatFilesToValidate = {
            "client_cell_1.dat",
            "client_portal.dat",
        };

        /// <summary>
        /// Overlay deploy finishes with retail portal + DM payload overlays. Legacy checksum
        /// targets list every converted DM portal id; thousands are intentionally absent
        /// (retail bytes at shared ids, or pruned before redeploy). Trim to the final catalog.
        /// </summary>
        public static int AlignPortalChecksumTargetsWithExport(
            string exportDirectory,
            DatExportChecksumTargets targets) {
            ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
            ArgumentNullException.ThrowIfNull(targets);

            if (targets.PortalFileIds.Count == 0) {
                return 0;
            }

            using var reader = new DefaultDatReaderWriter(
                exportDirectory,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);

            var present = reader.Portal().Catalog().Tree
                .Select(file => file.Id)
                .ToHashSet();

            return targets.PortalFileIds.RemoveWhere(id => !present.Contains(id));
        }

        public static DatExportChecksumReport ValidateExportDirectory(
            string exportDirectory,
            DatExportChecksumTargets? targets = null,
            Action<string>? onProgress = null) {
            var report = new DatExportChecksumReport();

            foreach (var datFile in DatFilesToValidate) {
                var path = Path.Combine(exportDirectory, datFile);
                if (!File.Exists(path)) {
                    report.AddWarning($"{datFile} missing after export");
                    continue;
                }

                try {
                    var fileBytes = File.ReadAllBytes(path);
                    int fileChecksum = PortalChecksum.CalcChecksum32(fileBytes);
                    report.AddInfo($"{datFile}: size={fileBytes.Length} file_checksum=0x{fileChecksum:X8}");
                }
                catch (Exception ex) {
                    report.AddWarning($"{datFile}: could not read file for checksum ({ex.Message})");
                }
            }

            try {
                using var reader = new DefaultDatReaderWriter(
                    exportDirectory,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);

                if (targets != null) {
                    ValidateTrackedEntries(reader, "client_cell_1.dat", targets.CellFileIds, report, onProgress);
                    ValidateTrackedEntries(reader, "client_portal.dat", targets.PortalFileIds, report, onProgress);
                }
            }
            catch (Exception ex) {
                report.AddWarning($"Could not open export directory for entry checksums: {ex.Message}");
            }

            if (report.WarningCount == 0)
                report.AddInfo("Export checksum validation completed with no warnings.");
            else
                report.AddWarning($"Export checksum validation finished with {report.WarningCount} warning(s).");

            return report;
        }

        static void ValidateTrackedEntries(
            IDatReaderWriter database,
            string datLabel,
            HashSet<uint> fileIds,
            DatExportChecksumReport report,
            Action<string>? onProgress = null) {
            if (fileIds.Count == 0) {
                report.AddInfo($"{datLabel}: no modified file IDs tracked (skipped per-entry checksum)");
                return;
            }

            int ok = 0;
            int missing = 0;
            int readFail = 0;
            int empty = 0;
            int processed = 0;

            foreach (var fileId in fileIds) {
                processed++;
                if (processed == 1 || processed % 1000 == 0 || processed == fileIds.Count) {
                    onProgress?.Invoke($"Validating checksums for {datLabel} ({processed}/{fileIds.Count})...");
                }
                if (!database.Catalog().Tree.HasFile(fileId)) {
                    missing++;
                    report.AddWarning($"{datLabel} 0x{fileId:X8}: not found in btree after export");
                    continue;
                }

                if (!database.Catalog().TryGetFileBytes(fileId, out byte[]? bytes, false) ||
                    bytes == null || bytes.Length <= 0) {
                    readFail++;
                    report.AddWarning($"{datLabel} 0x{fileId:X8}: TryGetFileBytes failed");
                    continue;
                }

                int length = bytes.Length;
                if (length <= 4) {
                    empty++;
                    report.AddWarning($"{datLabel} 0x{fileId:X8}: payload length {length} bytes");
                    continue;
                }

                int blobChecksum = PortalChecksum.CalcChecksum32(bytes);
                int payloadChecksum = PortalChecksum.CalcChecksum32(bytes.AsSpan(4, length - 4));

                if (blobChecksum == 0 && payloadChecksum == 0)
                    report.AddWarning($"{datLabel} 0x{fileId:X8}: zero checksum (len={length})");

                ok++;
            }

            report.SetTrackedMissing(datLabel, missing);
            report.AddInfo(
                $"{datLabel}: tracked={fileIds.Count} verified={ok} missing={missing} read_fail={readFail} tiny_payload={empty}");
        }
    }
}
