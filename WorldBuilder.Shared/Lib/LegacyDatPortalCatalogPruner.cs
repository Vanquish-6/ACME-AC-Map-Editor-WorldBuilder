using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

public static class LegacyDatPortalCatalogPruner {
    public static int PrunePortal(
        string outputDirectory,
        IReadOnlySet<uint> keepIds,
        string? retailSeedDirectory = null,
        Action<string>? onProgress = null) =>
        PruneDatabase(outputDirectory, "client_portal.dat", keepIds, "portal.dat", onProgress);

    public static int PruneDatabase(
        string outputDirectory,
        string datFileName,
        IReadOnlySet<uint> keepIds,
        string progressLabel,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(datFileName);
        ArgumentNullException.ThrowIfNull(keepIds);

        using var writer = new DefaultDatReaderWriter(outputDirectory, DatAccessType.ReadWrite);
        var archive = DatCatalog.ArchiveFromFileName(datFileName);
        int original = writer.ListFileIds(archive).Length;
        onProgress?.Invoke($"Rebuilding {progressLabel} keep-set ({keepIds.Count:N0} ids)...");
        int kept = DatCatalog.RebuildKeepSet(writer, archive, keepIds);
        writer.Flush();
        int removed = Math.Max(0, original - kept);
        onProgress?.Invoke($"{progressLabel} prune complete ({removed:N0} removed, {kept:N0} retained).");
        return removed;
    }
}
