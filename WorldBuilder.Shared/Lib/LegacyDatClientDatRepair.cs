using Acme.Dat;
using System.Collections.Generic;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Repairs an existing converted retail DAT folder in-place (leaf sentinels, headers, portal metadata)
/// without re-running the full legacy conversion.
/// </summary>
public static class LegacyDatClientDatRepair {
    internal static bool ShouldApplyLocalUiOverrides(LegacyDatExportPolicy policy) =>
        LegacyDatMergeRuleResolver.ShouldApplyDmPortalStringPatches(policy)
        || LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(policy)
        || LegacyDatMergeRuleResolver.ShouldPatchDmInGameStrings(policy)
        || LegacyDatMergeRuleResolver.ShouldPatchCharGenLayoutShape(policy)
        || LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(policy);

    public static void Repair(
        string exportDirectory,
        string retailSeedDirectory,
        Action<string>? onProgress = null) =>
        Repair(
            exportDirectory,
            retailSeedDirectory,
            LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm),
            onProgress);

    public static void Repair(
        string exportDirectory,
        string retailSeedDirectory,
        LegacyDatExportPolicy policy,
        Action<string>? onProgress = null) =>
        Repair(exportDirectory, retailSeedDirectory, policy, legacyDatDirectory: null, onProgress);

    public static void Repair(
        string exportDirectory,
        string retailSeedDirectory,
        LegacyDatExportPolicy policy,
        string? legacyDatDirectory,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentNullException.ThrowIfNull(policy);

        var filesToPatch = new List<string>(LegacyDatClientExportFixer.ClientCatalogDatFiles);
        if (ShouldApplyLocalUiOverrides(policy)) {
            filesToPatch.Add("client_local_English.dat");
        }

        onProgress?.Invoke("Patching catalog free-block headers before repair writes...");
        LegacyDatClientExportFixer.PatchCatalogFilesForWriting(exportDirectory, filesToPatch.ToArray());

        if (!string.IsNullOrWhiteSpace(legacyDatDirectory)
            && LegacyDatMergeRuleResolver.ShouldEnsureRegionTerrainPortalAssets(policy)) {
            onProgress?.Invoke("Refreshing region terrain SurfaceTexture/RenderSurface payloads from legacy...");
            using var legacySource = new LegacyDatReader(legacyDatDirectory);
            using var terrainWriter = new DefaultDatReaderWriter(
                exportDirectory,
                DatAccessType.ReadWrite,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            int portalIteration = terrainWriter.GetIteration(DatArchive.Portal);
            LegacyDatConversionService.ActiveRenderSurfaceIdMap.Value = LegacyDatConversionService.BuildRenderSurfaceIdMap(
                legacySource.GetAllIdsOfType<RenderSurface>());
            try {
                LegacyDatRegionTerrainPortalExporter.EnsureFromLegacy(
                    legacySource,
                    terrainWriter,
                    portalIteration,
                    onProgress);
            }
            finally {
                LegacyDatConversionService.ActiveRenderSurfaceIdMap.Value = null;
            }

            terrainWriter.Dispose();
        }

        if (LegacyDatMergeRuleResolver.UsesRetailPortalOverlayDeploy(policy)) {
            onProgress?.Invoke(
                "Redeploying client_portal.dat onto retail seed (preserve B-tree root per retail_client_dat_spec)...");
            LegacyDatPortalOverlayRedeploy.Redeploy(exportDirectory, retailSeedDirectory, policy, onProgress);

            if (policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog) {
                int portalIteration;
                using (var reader = new DefaultDatReaderWriter(
                    exportDirectory,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    portalIteration = reader.GetIteration(DatArchive.Portal);
                }

                LegacyDatFullDmPortalKeeper.GapFillMissingShellGlobals(
                    exportDirectory,
                    retailSeedDirectory,
                    policy,
                    portalIteration,
                    onProgress);
            }
        }

        // The overlay redeploy above re-copies the retail seed portal and re-overlays converted payloads,
        // which leaves the RETAIL seed Region 0x13000000 (retail terrain tiling) in place. For DM-only world
        // we must re-assert the DM Region after the redeploy, exactly like the main conversion pass does
        // (LegacyDatConversionService.Convert). Otherwise the repaired export ships retail terrain even though
        // the rest of the pipeline produced the DM Region. Verified via per-stage Region trace:
        // post-complete-client-export = DM (1/3), post-client-dat-repair = retail seed (2/4) before this fix.
        if (policy.World == LegacyDatWorldSource.DmOnly
            && !string.IsNullOrWhiteSpace(legacyDatDirectory)) {
            onProgress?.Invoke("Re-asserting DM Region 0x13000000 after repair redeploy...");
            int regionPortalIteration;
            using (var reader = new DefaultDatReaderWriter(
                exportDirectory,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand)) {
                regionPortalIteration = reader.GetIteration(DatArchive.Portal);
            }

            using var regionSource = new LegacyDatReader(legacyDatDirectory);
            LegacyDatRegionTerrainPortalExporter.EnsureDmRegionWins(
                regionSource,
                exportDirectory,
                regionPortalIteration,
                onProgress);
            LegacyDatRegionTerrainPortalExporter.EnsureDmWorldPortalObjectsWin(
                regionSource,
                exportDirectory,
                regionPortalIteration,
                onProgress);
            LegacyDatRegionTerrainPortalExporter.EnsureDmSharedOutdoorTextureSlotsWin(
                regionSource,
                retailSeedDirectory,
                exportDirectory,
                regionPortalIteration,
                onProgress);
            LegacyDatRegionTerrainPortalExporter.EnsureDmWorldReferencedMaterialsWin(
                regionSource,
                retailSeedDirectory,
                exportDirectory,
                regionPortalIteration,
                onProgress);
            using (var envWriter = new DefaultDatReaderWriter(
                    exportDirectory,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                var envCellIds = regionSource.GetEnvCellIds();
                LegacyDatEnvCellExportFixer.EnsureReferencedEnvironmentsPresent(
                    regionSource,
                    envWriter,
                    envCellIds,
                    regionPortalIteration,
                    warnings: null,
                    onProgress);
                LegacyDatEnvCellExportFixer.ReassertReferencedEnvironmentsFromLegacy(
                    regionSource,
                    envWriter,
                    envCellIds,
                    regionPortalIteration,
                    warnings: null,
                    onProgress);
                // Portal redeploy mutates client_portal.dat, not client_cell_1.dat. Rewriting every EnvCell
                // here a second time is redundant after the main conversion pass and has proven fragile on
                // large Full DM exports. Keep the targeted environment refresh + portal-index repair only.
                LegacyDatEnvCellExportFixer.FixCellDatabase(
                    envWriter,
                    envCellIds,
                    regionPortalIteration,
                    warnings: null,
                    onProgress);
            }

            if (policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog) {
                LegacyDatFullDmPortalKeeper.GapFillMissingShellGlobals(
                    exportDirectory,
                    retailSeedDirectory,
                    policy,
                    regionPortalIteration,
                    onProgress);
            }

            // Render dependency closure: ensure every shipped cell static + CharGen heritage GfxObj
            // resolves its full Surface/SurfaceTexture/RenderSurface/Palette graph (DrawEnvCell AV
            // guard). Runs after the DM world objects + region are re-asserted.
            onProgress?.Invoke("Repair: completing render dependency closure...");
            LegacyDatRenderClosureCompletion.Complete(legacyDatDirectory, exportDirectory, onProgress);
        }

        LegacyDatConversionBisect.SnapshotPortal(exportDirectory, "repair-post-reassert", onProgress);

        onProgress?.Invoke("Deduplicating client_portal.dat catalog ids after overlay/reassert passes...");
        LegacyDatPortalCatalogDeduplicator.DeduplicatePortal(exportDirectory, retailSeedDirectory, onProgress);

        onProgress?.Invoke("Repairing portal B-tree entry metadata...");
        LegacyDatPortalBootstrap.RepairPortalFileEntryMetadata(exportDirectory, retailSeedDirectory, onProgress);

        LegacyDatConversionBisect.SnapshotPortal(exportDirectory, "repair-post-metadata", onProgress);

        if ((LegacyDatMergeRuleResolver.ShouldApplyDmCharGenExport(policy)
                || LegacyDatMergeRuleResolver.ShouldPatchDmStartingAreas(policy))
            && !string.IsNullOrWhiteSpace(legacyDatDirectory)) {
            LegacyDatFullDmPortalKeeper.RestoreDmCharGenTable(
                legacyDatDirectory,
                exportDirectory,
                retailSeedDirectory,
                policy,
                onProgress);
        }

        LegacyDatConversionBisect.SnapshotPortal(exportDirectory, "repair-post-chargen-restore", onProgress);

        ReapplyLocalUiOverrides(
            exportDirectory,
            policy,
            legacyDatDirectory,
            onProgress);

        if (LegacyDatMergeRuleResolver.UsesRetailPortalOverlayDeploy(policy)) {
            onProgress?.Invoke(
                "Rebuilding final client_portal.dat from retail seed + finished DM payload set...");
            LegacyDatPortalOverlayRedeploy.Redeploy(exportDirectory, retailSeedDirectory, policy, onProgress);
        }

        LegacyDatConversionBisect.SnapshotPortal(exportDirectory, "repair-post-final-redeploy", onProgress);

        // Terrain blend maps must end as PFID_CUSTOM_LSCAPE_ALPHA for the retail terrain compositor to
        // blend them (DM ships A8R8G8B8 -> checkerboard). Run LAST, after the legacy terrain refresh,
        // render closure, and final portal redeploy have settled the terrain RenderSurfaces.
        if (LegacyDatMergeRuleResolver.ShouldEnsureRegionTerrainPortalAssets(policy)) {
            onProgress?.Invoke("Repair: re-encoding terrain blend maps to retail LSCAPE_ALPHA format...");
            LegacyDatTerrainAlphaCompletion.Complete(exportDirectory, onProgress);
        }

        // Bake combine-failing DM static/effect object textures (lifestone, ...) INDEX16 -> A8R8G8B8.
        if (policy.World == LegacyDatWorldSource.DmOnly) {
            onProgress?.Invoke("Repair: baking combine-failing static object textures to A8R8G8B8...");
            LegacyDatObjectTextureCombineFixer.Complete(exportDirectory, onProgress);
        }

        LegacyDatClientExportFixer.CompleteClientExport(exportDirectory, policy, onProgress, retailSeedDirectory);

        LegacyDatClientExportFixer.RestoreClientTransactionJournals(
            retailSeedDirectory,
            exportDirectory,
            policy,
            onProgress);

        onProgress?.Invoke("Validating retail client DAT contract...");
        var validation = RetailClientDatExportValidator.Validate(
            exportDirectory,
            new RetailClientDatExportValidator.Options {
                ExportPolicy = policy,
                RetailSeedDirectory = retailSeedDirectory,
                RequireOverlayPortalRoot = LegacyDatMergeRuleResolver.ShouldRequireOverlayPortalRoot(policy),
            });

        if (!validation.IsValid) {
            throw new InvalidOperationException(
                "Repair completed but export folder fails retail client DAT validation:"
                + System.Environment.NewLine
                + string.Join(System.Environment.NewLine, validation.Errors));
        }

        onProgress?.Invoke("Refreshing conversion manifest metadata...");
        LegacyDatExportManifest.WritePolicyMetadata(
            exportDirectory,
            policy,
            retailSeedDirectory,
            legacyDatDirectory);

        onProgress?.Invoke("Repair complete.");
    }

    internal static void ReapplyLocalUiOverrides(
        string exportDirectory,
        LegacyDatExportPolicy policy,
        string? legacyDatDirectory,
        Action<string>? onProgress = null,
        List<string>? warnings = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        ArgumentNullException.ThrowIfNull(policy);

        bool shouldPatchDmStrings = LegacyDatMergeRuleResolver.ShouldApplyDmPortalStringPatches(policy);
        bool shouldPatchConnectionStrings = LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(policy);
        bool shouldPatchInGameStrings = LegacyDatMergeRuleResolver.ShouldPatchDmInGameStrings(policy);
        bool shouldPatchCharGenLayout = LegacyDatMergeRuleResolver.ShouldPatchCharGenLayoutShape(policy);
        bool shouldPatchInGameLayout = LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(policy);
        if (!shouldPatchDmStrings
            && !shouldPatchConnectionStrings
            && !shouldPatchInGameStrings
            && !shouldPatchCharGenLayout
            && !shouldPatchInGameLayout) {
            return;
        }

        string localPath = Path.Combine(exportDirectory, "client_local_English.dat");
        if (!File.Exists(localPath)) {
            warnings?.Add("DM UI local overrides: client_local_English.dat is missing; patch skipped.");
            return;
        }

        LegacyDatClientExportFixer.PatchCatalogFilesForWriting(exportDirectory, "client_local_English.dat");

        string stringPatchReportPath = Path.Combine(exportDirectory, "dm_ui_string_patch_report.txt");
        string layoutPatchReportPath = Path.Combine(exportDirectory, "dm_ui_layout_patch_report.txt");

        using var localWriter = new DefaultDatReaderWriter(
            exportDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        using var legacySource = shouldPatchDmStrings && !string.IsNullOrWhiteSpace(legacyDatDirectory)
            ? new LegacyDatReader(legacyDatDirectory)
            : null;

        if (shouldPatchDmStrings) {
            if (legacySource == null) {
                string message = "DM UI local overrides: legacy DAT directory is unavailable; char-create StringTable patch skipped.";
                warnings?.Add(message);
                onProgress?.Invoke(message);
            }
            else {
                onProgress?.Invoke("Reapplying DM char-create StringTable text...");
                LegacyDatDmStringTableExporter.ApplyCharGenPortalStrings(
                    legacySource,
                    localWriter,
                    warnings,
                    stringPatchReportPath);
            }
        }

        if (shouldPatchConnectionStrings) {
            onProgress?.Invoke("Reapplying DM connection / pre-game StringTable text...");
            LegacyDatDmStringTableExporter.ApplyConnectionScreenStrings(
                localWriter,
                warnings,
                stringPatchReportPath);
            LegacyDatDmStringTableExporter.ApplyScreenStrings(
                localWriter,
                warnings,
                stringPatchReportPath,
                LegacyDatDmStringTableMapNames.PreGameExe);
        }

        if (shouldPatchInGameStrings) {
            onProgress?.Invoke("Reapplying DM in-game StringTable text...");
            LegacyDatDmStringTableExporter.ApplyInGameScreenStrings(
                localWriter,
                warnings,
                stringPatchReportPath);
        }

        if (shouldPatchCharGenLayout) {
            onProgress?.Invoke("Reapplying DM char-create layout overrides...");
            LegacyDatDmLayoutExporter.ApplyLayoutSpec(
                localWriter,
                warnings,
                layoutPatchReportPath,
                LegacyDatDmLayoutSpecNames.CharGenWizard);
        }

        if (shouldPatchInGameLayout) {
            onProgress?.Invoke("Reapplying DM in-game layout overrides...");
            LegacyDatDmLayoutExporter.ApplyLayoutSpec(
                localWriter,
                warnings,
                layoutPatchReportPath,
                LegacyDatDmLayoutSpecNames.InGamePanels,
                appendReport: shouldPatchCharGenLayout);
        }

        localWriter.Dispose();
        LegacyDatClientExportFixer.PatchCatalogFilesForWriting(exportDirectory, "client_local_English.dat");
    }
}
