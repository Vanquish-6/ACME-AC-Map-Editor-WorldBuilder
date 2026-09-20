using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Post-export checks that mirror what the retail client loads during init.
/// </summary>
public static class LegacyDatClientExportValidator {
    public const uint ConnectLayoutId = 0x21000001;
    public const uint ConnectErrorsStringTableId = 0x23000010;

    public static IReadOnlyList<string> Validate(string exportDirectory, LegacyDatExportPolicy policy) {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        ArgumentNullException.ThrowIfNull(policy);

        var errors = new List<string>();

        foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
            string path = Path.Combine(exportDirectory, datFile);
            if (!File.Exists(path)) {
                errors.Add($"Missing required file: {datFile}");
            }
        }

        if (errors.Count > 0) {
            return errors;
        }

        foreach (string datFile in new[] { "client_cell_1.dat", "client_portal.dat", "client_local_English.dat" }) {
            string path = Path.Combine(exportDirectory, datFile);
            if (!File.Exists(path)) {
                continue;
            }

            var issues = DatExportFixer.InspectReadCompatibility(path);
            if (issues.HasIssues) {
                var parts = new List<string>();
                if (issues.NodesWithInvalidFileCount > 0) {
                    parts.Add($"{issues.NodesWithInvalidFileCount} node(s) have invalid file counts");
                }

                if (issues.NodesWithBranch0Sentinel > 0) {
                    parts.Add($"{issues.NodesWithBranch0Sentinel} node(s) have branch[0]=0xCDCDCDCD");
                }

                if (issues.ActiveBranchSentinels > 0) {
                    parts.Add($"{issues.ActiveBranchSentinels} active branch slot(s) contain 0xCDCDCDCD");
                }

                if (issues.ActiveBranchInvalidOffsets > 0) {
                    parts.Add($"{issues.ActiveBranchInvalidOffsets} active branch slot(s) point outside the DAT");
                }

                errors.Add(
                    $"{datFile}: B-tree read-compatibility issue(s): {string.Join(", ", parts)}. Retail client init may fail while walking the catalog.");
            }
        }

        if (errors.Count > 0) {
            return errors;
        }

        try {
            using var reader = new DefaultDatReaderWriter(
                exportDirectory,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);

            ValidateCatalogDatabase(
                reader.Cell(),
                "client_cell_1.dat",
                requireIteration: true,
                errors);

            ValidateCatalogDatabase(
                reader,
                "client_portal.dat",
                requireIteration: true,
                errors);

            if (!reader.TryGet<Region>(0x13000000, out _)) {
                errors.Add("client_portal.dat: Region 0x13000000 is not readable (required for terrain).");
            }

            if (policy.Shell.CharGen != LegacyDatContentSource.Dm) {
                if (!reader.TryGet<CharGen>(LegacyDatRetailClientShellCollector.CharGenId, out _)) {
                    errors.Add(
                        $"client_portal.dat: CharGen 0x{LegacyDatRetailClientShellCollector.CharGenId:X8} is not readable (login/char create).");
                }
            }

            if (LegacyDatMergeRuleResolver.ShouldApplyDmPortalStringPatches(policy)
                || LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(policy)
                || LegacyDatMergeRuleResolver.ShouldPatchCharGenLayoutShape(policy)
                || LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(policy)) {
                if (!reader.TryGet<LayoutDesc>(ConnectLayoutId, out _)) {
                    errors.Add(
                        $"client_local_English.dat: connect layout 0x{ConnectLayoutId:X8} is not readable.");
                }

                if (LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(policy)
                    && !reader.TryGet<StringTable>(ConnectErrorsStringTableId, out _)) {
                    errors.Add(
                        $"client_local_English.dat: connection StringTable 0x{ConnectErrorsStringTableId:X8} is not readable.");
                }

                if (LegacyDatMergeRuleResolver.ShouldPatchCharGenLayoutShape(policy)) {
                    ValidateSpecLayouts(
                        reader,
                        LegacyDatDmLayoutSpecNames.CharGenWizard,
                        "char-gen layout",
                        errors);
                }

                if (LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(policy)) {
                    ValidateSpecLayouts(
                        reader,
                        LegacyDatDmLayoutSpecNames.InGamePanels,
                        "in-game layout",
                        errors);
                }
            }
        }
        catch (Exception ex) {
            errors.Add($"Could not open export DATs for client validation: {ex.Message}");
        }

        return errors;
    }

    static void ValidateCatalogDatabase(
        IDatReaderWriter database,
        string label,
        bool requireIteration,
        List<string> errors) {
        if (requireIteration && !database.Catalog().Tree.HasFile(LegacyDatPortalFileIds.Iteration)) {
            errors.Add($"{label}: iteration object 0x{LegacyDatPortalFileIds.Iteration:X8} missing from catalog.");
            return;
        }

        if (!database.Catalog().TryGetFileBytes(LegacyDatPortalFileIds.Iteration, out byte[]? iterBytes, false)
            || iterBytes == null
            || iterBytes.Length <= 4) {
            if (requireIteration) {
                errors.Add($"{label}: iteration object 0x{LegacyDatPortalFileIds.Iteration:X8} has no payload.");
            }
        }
    }

    static void ValidateSpecLayouts(
        DefaultDatReaderWriter reader,
        string specName,
        string label,
        List<string> errors) {
        var spec = LegacyDatDmLayoutSpec.LoadEmbeddedSpec(specName);
        foreach (uint layoutId in spec.Layouts.Select(layout => layout.LayoutIdValue).Distinct().OrderBy(id => id)) {
            if (!reader.TryGet<LayoutDesc>(layoutId, out _)) {
                errors.Add($"client_local_English.dat: {label} 0x{layoutId:X8} is not readable.");
            }
        }
    }
}
