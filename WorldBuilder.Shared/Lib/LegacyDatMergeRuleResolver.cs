namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Maps export policy toggles to per-layer merge decisions.
/// </summary>
public static class LegacyDatMergeRuleResolver {
    public static LegacyDatExportPolicy Resolve(LegacyToRetailConversionOptions options) =>
        options.ExportPolicy ?? LegacyDatExportPolicy.FromLegacyMode(options.ExportMode);

    public static bool UsesLegacyOnlyCellWorld(LegacyDatExportPolicy policy) =>
        policy.World == LegacyDatWorldSource.DmOnly;

    public static bool UsesEmptyCellShell(LegacyDatExportPolicy policy) =>
        policy.World == LegacyDatWorldSource.DmOnly;

    public static bool UsesEmptyPortalShell(LegacyDatExportPolicy policy) =>
        policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog;

    public static bool UsesFullRetailPortalSeed(LegacyDatExportPolicy policy) =>
        policy.Deploy == LegacyDatPortalDeployPolicy.OverlayOnRetailSeed
        && policy.Portal is LegacyDatPortalCatalogPolicy.WorldClosure
            or LegacyDatPortalCatalogPolicy.FullDmCatalog;

    public static bool ShouldPruneRetailCellWorld(LegacyDatExportPolicy policy) =>
        UsesLegacyOnlyCellWorld(policy);

    public static bool ShouldPrunePortalCatalog(LegacyDatExportPolicy policy) =>
        policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog;

    public static bool ShouldMergeRetailPortalGlobals(LegacyDatExportPolicy policy) =>
        policy.Portal == LegacyDatPortalCatalogPolicy.WorldClosure
        && policy.Deploy is LegacyDatPortalDeployPolicy.SingleConvertedCatalog
            or LegacyDatPortalDeployPolicy.OverlayOnRetailSeed;

    /// <summary>
    /// After overlaying DM portal objects onto a full retail <c>client_portal.dat</c>, prune strays
    /// while keeping world-closure IDs and shell keep-sets.
    /// </summary>
    public static bool ShouldPruneSlimMergePortal(LegacyDatExportPolicy policy) =>
        policy.Portal == LegacyDatPortalCatalogPolicy.WorldClosure
        && policy.Deploy == LegacyDatPortalDeployPolicy.OverlayOnRetailSeed;

    public static bool UsesWorldClosure(LegacyDatExportPolicy policy) =>
        policy.Portal == LegacyDatPortalCatalogPolicy.WorldClosure;

    public static bool DefersPortalConversionPass(LegacyDatExportPolicy policy) =>
        UsesWorldClosure(policy);

    public static bool IncludePhase3LocalData(LegacyDatExportPolicy policy) =>
        policy.Shell.Ui == LegacyDatContentSource.Dm;

    public static bool ShouldApplyDmPortalStringPatches(LegacyDatExportPolicy policy) =>
        policy.Shell.Ui == LegacyDatContentSource.DmPortalStrings;

    public static bool ShouldMergeRetailUiShellAssets(LegacyDatExportPolicy policy) =>
        policy.Shell.Ui == LegacyDatContentSource.Retail
        || policy.Shell.ConnectionLoginUiShell;

    /// <summary>
    /// Legacy DM currently ships only <c>cell.dat</c> + <c>portal.dat</c>; probe runs against the DM
    /// source on 2026-06-08 showed the retail shell surface IDs used by connect / char-select
    /// (<c>0x0600610F</c>, <c>0x06006EBC</c>, <c>0x060022BA</c>) are absent there. Until shell-art import
    /// or remap exists, DM-string presets still rely on the retail shell bytes at those shared IDs.
    /// CharGen appearance chains are handled separately via <see cref="ShouldPatchDmStartingAreas"/>.
    /// </summary>
    public static bool ShouldOverwriteRetailUiShellPortalAssets(LegacyDatExportPolicy policy) =>
        policy.Shell.ConnectionLoginUiShell;

    public static bool ShouldPatchConnectionScreenStrings(LegacyDatExportPolicy policy) =>
        policy.Shell.PatchConnectionScreenStrings
        && policy.Shell.Ui is LegacyDatContentSource.DmPortalStrings or LegacyDatContentSource.Retail;

    public static bool ShouldPatchDmInGameStrings(LegacyDatExportPolicy policy) =>
        policy.Shell.PatchInGameScreenStrings
        && policy.Shell.Ui is LegacyDatContentSource.DmPortalStrings or LegacyDatContentSource.Retail;

    public static bool ShouldPatchCharGenLayoutShape(LegacyDatExportPolicy policy) =>
        policy.Shell.PatchCharGenLayoutShape
        && policy.Shell.Ui is LegacyDatContentSource.DmPortalStrings or LegacyDatContentSource.Retail;

    public static bool ShouldPatchInGameLayoutShape(LegacyDatExportPolicy policy) =>
        policy.Shell.PatchInGameLayoutShape
        && policy.Shell.Ui is LegacyDatContentSource.DmPortalStrings or LegacyDatContentSource.Retail;

    public static bool ShouldApplyLegacyCharGenStartingAreas(LegacyDatExportPolicy policy) =>
        policy.Shell.CharGen == LegacyDatContentSource.Retail
        && (UsesWorldClosure(policy) || ShouldPatchDmStartingAreas(policy));

    /// <summary>
    /// Retail-CharGen overlay exports keep the retail CharGen heritage/setup shell and only patch
    /// DM starting areas. The playable Full DM preset intentionally takes this path so live
    /// characters and weenies continue to validate against retail CharGen semantics. The
    /// experimental faithful preset bypasses it and exports DM CharGen data instead.
    /// </summary>
    public static bool ShouldPatchDmStartingAreas(LegacyDatExportPolicy policy) =>
        policy.Shell.CharGen == LegacyDatContentSource.Retail
        &&
        policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog
        && UsesFullRetailPortalSeed(policy);

    public static bool ShouldApplyDmCharGenExport(LegacyDatExportPolicy policy) =>
        policy.Shell.CharGen == LegacyDatContentSource.Dm
        && !ShouldPatchDmStartingAreas(policy);

    /// <summary>
    /// Full DM keeps retail Region terrain descriptor IDs; legacy DM art must replace retail
    /// <c>SurfaceTexture</c>/<c>RenderSurface</c> payloads at those shared slots.
    /// </summary>
    public static bool ShouldEnsureRegionTerrainPortalAssets(LegacyDatExportPolicy policy) =>
        policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog
        && UsesFullRetailPortalSeed(policy);

    public static bool ShouldRequireCharGenStartingAreas(LegacyDatExportPolicy policy) =>
        ShouldPatchDmStartingAreas(policy)
        || policy.Shell.CharGen == LegacyDatContentSource.Retail;

    public static bool ShouldExportAllLegacyAppearanceTables(string objectType, LegacyDatExportPolicy policy) {
        if (objectType is not ("ClothingTable" or "PalSet" or "Palette")) {
            return false;
        }

        return policy.Shell.AppearanceTables == LegacyDatContentSource.Dm;
    }

    public static bool ShouldForceRetailPortalGlobal(uint id, LegacyDatExportPolicy policy) {
        if (policy.Shell.SpellTables == LegacyDatContentSource.Retail && (id >> 24) == 0x0E) {
            return true;
        }

        if (policy.Shell.CharGen == LegacyDatContentSource.Retail
            && id == LegacyDatRetailClientShellCollector.CharGenId) {
            return policy.GapFill == LegacyDatRetailGapFillPolicy.ShellReserve;
        }

        if (policy.Shell.Ui is LegacyDatContentSource.Retail or LegacyDatContentSource.DmPortalStrings
            && LegacyDatPortalBootstrap.ShouldKeepRetailPortalClientMetadata(id)) {
            return policy.GapFill == LegacyDatRetailGapFillPolicy.ShellReserve;
        }

        if ((id >> 24) == 0x13
            && policy.World != LegacyDatWorldSource.DmOnly
            && policy.Shell.SpellTables != LegacyDatContentSource.Dm) {
            // DM-only world must ship the converted DM Region (its terrain descriptors + tiling),
            // not the retail seed Region. Forcing retail here made the client composite DM ground
            // with retail tiling/recipe. Only keep the retail Region for retail-world modes.
            return policy.GapFill != LegacyDatRetailGapFillPolicy.Off;
        }

        return false;
    }

    public static bool ShouldKeepRetailPortalForClosureMerge(uint id, LegacyDatExportPolicy policy) {
        if (ShouldForceRetailPortalGlobal(id, policy)) {
            return true;
        }

        if (LegacyDatPortalBootstrap.ShouldKeepRetailPortalClientMetadata(id)) {
            return policy.GapFill != LegacyDatRetailGapFillPolicy.Off
                || policy.Shell.Ui is LegacyDatContentSource.Retail or LegacyDatContentSource.DmPortalStrings;
        }

        if (LegacyDatPortalBootstrap.ShouldKeepRetailPortalGlobal(id)) {
            if ((id >> 24) is 0x0F or 0x10) {
                return policy.Shell.AppearanceTables == LegacyDatContentSource.Retail;
            }

            return policy.Shell.SpellTables == LegacyDatContentSource.Retail
                || policy.Shell.SpellTables == LegacyDatContentSource.GapFillRetail;
        }

        return false;
    }

    public static LegacyDatMergeWinner ResolvePortalObjectWinner(
        string objectType,
        uint id,
        LegacyDatExportPolicy policy,
        bool inWorldClosure) {
        if (policy.Portal == LegacyDatPortalCatalogPolicy.FullRetailPlusDmOverlay) {
            return LegacyDatMergeWinner.DmConvert;
        }

        if (policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog) {
            if (objectType is "ClothingTable" or "PalSet" or "Palette"
                && policy.Shell.AppearanceTables == LegacyDatContentSource.Retail) {
                return LegacyDatMergeWinner.Skip;
            }

            return LegacyDatMergeWinner.DmConvert;
        }

        if (ShouldExportAllLegacyAppearanceTables(objectType, policy)) {
            return LegacyDatMergeWinner.DmConvert;
        }

        if (ShouldKeepRetailPortalForClosureMerge(id, policy)) {
            return LegacyDatMergeWinner.RetailCopy;
        }

        return inWorldClosure ? LegacyDatMergeWinner.DmConvert : LegacyDatMergeWinner.Skip;
    }

    public static bool ShouldCopyRetailLocalSeed(LegacyDatExportPolicy policy) =>
        policy.Shell.Ui is LegacyDatContentSource.Retail or LegacyDatContentSource.DmPortalStrings;

    public static bool ShouldCopyRetailHighresSeed(LegacyDatExportPolicy policy) =>
        true;

    /// <summary>
    /// Retail <c>client_highres.dat</c> can remain on disk for every preset, but only retail-shell
    /// overlay modes should preserve its portal-side world graph. Full DM reuses many retail world
    /// IDs with different DM payloads, so keeping the retail highres portal chain alive can
    /// reintroduce retail art/setup semantics into the DM catalog.
    /// </summary>
    public static bool ShouldPreserveRetailHighresPortalAssets(LegacyDatExportPolicy policy) =>
        policy.Portal == LegacyDatPortalCatalogPolicy.WorldClosure;

    public static bool UsesRetailPortalOverlayDeploy(LegacyDatExportPolicy policy) =>
        policy.Deploy == LegacyDatPortalDeployPolicy.OverlayOnRetailSeed
        && policy.Portal is LegacyDatPortalCatalogPolicy.WorldClosure
            or LegacyDatPortalCatalogPolicy.FullDmCatalog;

    /// <summary>
    /// Retail client init requires the seed portal B-tree root when using overlay deploy
    /// (<see cref="RetailClientDatSpec"/> portal_init invariant).
    /// </summary>
    public static bool ShouldRequireOverlayPortalRoot(LegacyDatExportPolicy policy) =>
        policy.Deploy == LegacyDatPortalDeployPolicy.OverlayOnRetailSeed;

    public static bool UsesSlimMergeOverlayFinalize(LegacyDatExportPolicy policy) =>
        UsesRetailPortalOverlayDeploy(policy);
}
