namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Reads <c>worldbuilder_legacy_conversion_manifest.txt</c> written by legacy conversion.
/// </summary>
public static class LegacyDatExportManifest {
    public static bool TryLoadPolicy(string exportDirectory, out LegacyDatExportPolicy policy) {
        policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);
        if (!TryReadManifest(exportDirectory, out string[] lines)) {
            return false;
        }

        foreach (string line in lines) {
            if (!line.StartsWith("export_preset=", StringComparison.Ordinal)) {
                continue;
            }

            string value = line["export_preset=".Length..].Trim();
            if (Enum.TryParse(value, ignoreCase: true, out LegacyDatExportPreset preset)) {
                policy = LegacyDatExportPolicy.FromPreset(preset);
                return true;
            }
        }

        return false;
    }

    public static string? TryLoadLegacyDatDirectory(string exportDirectory) {
        if (!TryReadManifest(exportDirectory, out string[] lines)) {
            return null;
        }

        foreach (string line in lines) {
            if (!line.StartsWith("legacy_dat_dir=", StringComparison.Ordinal)) {
                continue;
            }

            string path = line["legacy_dat_dir=".Length..].Trim();
            return Directory.Exists(path) ? path : null;
        }

        return null;
    }

    public static string? TryLoadRetailSeedDirectory(string exportDirectory) {
        if (!TryReadManifest(exportDirectory, out string[] lines)) {
            return null;
        }

        foreach (string line in lines) {
            if (!line.StartsWith("retail_seed_dir=", StringComparison.Ordinal)) {
                continue;
            }

            string path = line["retail_seed_dir=".Length..].Trim();
            return Directory.Exists(path) ? path : null;
        }

        return null;
    }

    public static bool TryLoadPolicyFromJson(string exportDirectory, out LegacyDatExportPolicy policy) {
        policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);
        if (!TryReadManifest(exportDirectory, out string[] lines)) {
            return false;
        }

        int jsonStart = Array.FindIndex(lines, line => line == "[export_policy_json]");
        if (jsonStart < 0 || jsonStart + 1 >= lines.Length) {
            return false;
        }

        var jsonLines = new List<string>();
        for (int i = jsonStart + 1; i < lines.Length; i++) {
            string line = lines[i];
            if (jsonLines.Count > 0
                && line.StartsWith('[')
                && line.EndsWith(']')
                && !line.Equals("[export_policy_json]", StringComparison.Ordinal)) {
                break;
            }

            if (jsonLines.Count == 0 && string.IsNullOrWhiteSpace(line)) {
                continue;
            }

            jsonLines.Add(line);
        }

        string json = string.Join(System.Environment.NewLine, jsonLines);
        if (string.IsNullOrWhiteSpace(json)) {
            return false;
        }

        if (LegacyDatExportPolicy.TryParseManifestJson(json) is { } parsed) {
            policy = parsed;
            return true;
        }

        return false;
    }

    public static void WritePolicyMetadata(
        string exportDirectory,
        LegacyDatExportPolicy policy,
        string? retailSeedDirectory,
        string? legacyDatDirectory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        ArgumentNullException.ThrowIfNull(policy);

        string path = Path.Combine(exportDirectory, "worldbuilder_legacy_conversion_manifest.txt");
        string[] existingLines = File.Exists(path) ? File.ReadAllLines(path) : [];
        int tailStart = FindFirstSectionAfterPolicyJson(existingLines);

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"generated_utc={DateTime.UtcNow:O}");
        if (!string.IsNullOrWhiteSpace(legacyDatDirectory)) {
            builder.AppendLine($"legacy_dat_dir={Path.GetFullPath(legacyDatDirectory)}");
        }

        if (!string.IsNullOrWhiteSpace(retailSeedDirectory)) {
            builder.AppendLine($"retail_seed_dir={Path.GetFullPath(retailSeedDirectory)}");
        }

        builder.AppendLine($"output_dir={Path.GetFullPath(exportDirectory)}");
        builder.AppendLine($"export_mode={policy.ToLegacyMode()}");
        builder.AppendLine($"export_preset={policy.Preset?.ToString() ?? "custom"}");
        builder.AppendLine("later_local_data_pass_enabled=False");
        builder.AppendLine();
        builder.AppendLine("[export_policy_json]");
        builder.AppendLine(policy.ToManifestJson());
        builder.AppendLine();
        builder.AppendLine("[repair]");
        builder.AppendLine("manifest_policy_refreshed=True");
        builder.AppendLine("note=Policy metadata above reflects the repaired export. Any preserved audit/findings/warnings sections below may describe the original conversion pass.");

        if (tailStart >= 0) {
            builder.AppendLine();
            builder.Append(string.Join(System.Environment.NewLine, existingLines.Skip(tailStart)));
            if (!builder.ToString().EndsWith(System.Environment.NewLine, StringComparison.Ordinal)) {
                builder.AppendLine();
            }
        }

        File.WriteAllText(path, builder.ToString());
    }

    static bool TryReadManifest(string exportDirectory, out string[] lines) {
        lines = [];
        string path = Path.Combine(exportDirectory, "worldbuilder_legacy_conversion_manifest.txt");
        if (!File.Exists(path)) {
            return false;
        }

        lines = File.ReadAllLines(path);
        return true;
    }

    static int FindFirstSectionAfterPolicyJson(string[] lines) {
        int jsonStart = Array.FindIndex(lines, line => line == "[export_policy_json]");
        if (jsonStart < 0) {
            return Array.FindIndex(lines, line => line.StartsWith('[') && line.EndsWith(']'));
        }

        bool sawJsonContent = false;
        for (int i = jsonStart + 1; i < lines.Length; i++) {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line) && !sawJsonContent) {
                continue;
            }

            if (line.StartsWith('[')
                && line.EndsWith(']')
                && !line.Equals("[export_policy_json]", StringComparison.Ordinal)) {
                return i;
            }

            if (!string.IsNullOrWhiteSpace(line)) {
                sawJsonContent = true;
            }
        }

        return -1;
    }
}
