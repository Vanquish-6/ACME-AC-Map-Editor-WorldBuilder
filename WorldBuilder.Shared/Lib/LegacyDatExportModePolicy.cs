namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Per-mode export behavior for legacy (DM) to retail-format conversion.
/// Delegates to <see cref="LegacyDatMergeRuleResolver"/> when a full policy is available.
/// </summary>
public static class LegacyDatExportModePolicy {
    internal static LegacyDatExportPolicy Policy(LegacyDatExportMode exportMode) =>
        LegacyDatExportPolicy.FromLegacyMode(exportMode);

    internal static bool UsesLegacyOnlyCellWorld(LegacyDatExportMode exportMode) =>
        LegacyDatMergeRuleResolver.UsesLegacyOnlyCellWorld(Policy(exportMode));

    internal static bool UsesEmptyCellShell(LegacyDatExportMode exportMode) =>
        LegacyDatMergeRuleResolver.UsesEmptyCellShell(Policy(exportMode));

    internal static bool UsesEmptyPortalShell(LegacyDatExportMode exportMode) =>
        LegacyDatMergeRuleResolver.UsesEmptyPortalShell(Policy(exportMode));

    internal static bool UsesFullRetailPortalSeed(LegacyDatExportMode exportMode) =>
        LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(Policy(exportMode));

    internal static bool ShouldPruneRetailCellWorld(LegacyDatExportMode exportMode) =>
        LegacyDatMergeRuleResolver.ShouldPruneRetailCellWorld(Policy(exportMode));

    internal static bool ShouldPrunePortalCatalog(LegacyDatExportMode exportMode) =>
        LegacyDatMergeRuleResolver.ShouldPrunePortalCatalog(Policy(exportMode));

    internal static bool ShouldMergeRetailPortalGlobals(LegacyDatExportMode exportMode) =>
        LegacyDatMergeRuleResolver.ShouldMergeRetailPortalGlobals(Policy(exportMode));

    internal static bool UsesWorldClosure(LegacyDatExportPolicy policy) =>
        LegacyDatMergeRuleResolver.UsesWorldClosure(policy);

    internal static bool UsesFullRetailPortalSeed(LegacyDatExportPolicy policy) =>
        LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(policy);

    internal static bool ShouldPruneRetailCellWorld(LegacyDatExportPolicy policy) =>
        LegacyDatMergeRuleResolver.ShouldPruneRetailCellWorld(policy);

    internal static bool ShouldPrunePortalCatalog(LegacyDatExportPolicy policy) =>
        LegacyDatMergeRuleResolver.ShouldPrunePortalCatalog(policy);

    internal static bool ShouldMergeRetailPortalGlobals(LegacyDatExportPolicy policy) =>
        LegacyDatMergeRuleResolver.ShouldMergeRetailPortalGlobals(policy);
}
