using System.Collections.ObjectModel;

namespace WorldBuilder.Shared.Lib {
    public enum DatProjectMode {
        Retail = 0,
        LegacyPreTod = 1,
    }

    public static class DatProjectModeInfo {
        public static readonly ReadOnlyCollection<string> RetailDatFiles = Array.AsReadOnly(new[] {
            "client_cell_1.dat",
            "client_portal.dat",
            "client_highres.dat",
            "client_local_English.dat",
        });

        public static readonly ReadOnlyCollection<string> LegacyPreTodDatFiles = Array.AsReadOnly(new[] {
            "cell.dat",
            "portal.dat",
        });

        public static IReadOnlyList<string> GetRequiredFiles(DatProjectMode mode) => mode switch {
            DatProjectMode.Retail => RetailDatFiles,
            DatProjectMode.LegacyPreTod => LegacyPreTodDatFiles,
            _ => RetailDatFiles,
        };

        public static string GetDisplayName(DatProjectMode mode) => mode switch {
            DatProjectMode.Retail => "Retail",
            DatProjectMode.LegacyPreTod => "Legacy pre-ToD",
            _ => "Unknown",
        };

        public static string GetCapabilitySummary(DatProjectMode mode) => mode switch {
            DatProjectMode.Retail => "Retail DATs detected. Full editor and export support is available.",
            DatProjectMode.LegacyPreTod => "Legacy pre-ToD DATs detected. This project will open in read-only view mode.",
            _ => "Unknown DAT mode.",
        };

        public static bool IsReadOnly(DatProjectMode mode) => mode != DatProjectMode.Retail;

        public static bool TryDetectFromDirectory(
            string datDirectory,
            out DatProjectMode mode,
            out string summary,
            out IReadOnlyList<string> errors) {
            var issues = new List<string>();

            if (string.IsNullOrWhiteSpace(datDirectory)) {
                mode = DatProjectMode.Retail;
                summary = string.Empty;
                errors = new[] { "Base DAT directory is required." };
                return false;
            }

            if (!Directory.Exists(datDirectory)) {
                mode = DatProjectMode.Retail;
                summary = string.Empty;
                errors = new[] { "Base DAT directory does not exist." };
                return false;
            }

            bool hasRetail = RetailDatFiles.All(file => File.Exists(Path.Combine(datDirectory, file)));
            if (hasRetail) {
                mode = DatProjectMode.Retail;
                summary = GetCapabilitySummary(mode);
                errors = Array.Empty<string>();
                return true;
            }

            bool hasLegacy = LegacyPreTodDatFiles.All(file => File.Exists(Path.Combine(datDirectory, file)));
            if (hasLegacy) {
                mode = DatProjectMode.LegacyPreTod;
                summary = GetCapabilitySummary(mode);
                errors = Array.Empty<string>();
                return true;
            }

            issues.Add($"Retail DATs require: {string.Join(", ", RetailDatFiles)}");
            issues.Add($"Legacy pre-ToD DATs require: {string.Join(", ", LegacyPreTodDatFiles)}");

            mode = DatProjectMode.Retail;
            summary = "No supported DAT layout was detected in the selected directory.";
            errors = issues;
            return false;
        }
    }
}
