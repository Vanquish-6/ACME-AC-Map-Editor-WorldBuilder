using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// After slim-merge portal redeploy, re-apply DM CharGen starting areas on the retail CharGen shell.
/// </summary>
public static class LegacyDatSlimMergeCharGenRestore {
    public static void RestoreAfterPortalPrune(
        string legacyDatDirectory,
        string outputDirectory,
        string retailSeedDirectory,
        LegacyDatWorldReferenceClosure? worldClosure,
        IList<string>? warnings = null,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);

        onProgress?.Invoke("Patching DM CharGen starting areas after portal redeploy...");
        using var legacy = new LegacyDatReader(legacyDatDirectory);
        using var output = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        LegacyDatCharGenMapper.ApplySlimMergeStartingAreas(
            legacy,
            output,
            worldClosure,
            warnings);

        output.Dispose();

        LegacyDatPortalCatalogDeduplicator.DeduplicatePortal(
            outputDirectory,
            retailSeedDirectory,
            onProgress);

        string portalPath = Path.Combine(outputDirectory, "client_portal.dat");
        if (File.Exists(portalPath)) {
            DatExportFixer.FixLeafBranchSentinels(portalPath);
            LegacyDatClientExportFixer.SyncHeaderFileSize(portalPath);
        }
    }
}
