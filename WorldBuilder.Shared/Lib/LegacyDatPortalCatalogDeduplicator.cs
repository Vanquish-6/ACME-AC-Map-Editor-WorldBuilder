using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

public static class LegacyDatPortalCatalogDeduplicator {
    public static int DeduplicatePortal(
        string outputDirectory,
        string? retailSeedDirectory = null,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        using var writer = new DefaultDatReaderWriter(outputDirectory, DatAccessType.ReadWrite);
        var ids = writer.ListFileIds(DatArchive.Portal);
        var keep = ids.Where(id => id != 0).Distinct().ToHashSet();
        if (ids.Length == keep.Count) {
            return 0;
        }

        onProgress?.Invoke($"Deduplicating client_portal.dat catalog...");
        DatCatalog.RebuildKeepSet(writer, DatArchive.Portal, keep);
        writer.Flush();
        return ids.Length - keep.Count;
    }

    internal static bool TryReplacePortalFile(
        IDatReaderWriter seed,
        IDatReaderWriter output,
        uint id,
        int portalIteration,
        object? seedFileTemplates = null) {
        _ = seedFileTemplates;
        return DatCatalog.CopyFile(seed, output, DatArchive.Portal, id, portalIteration);
    }

        internal static bool TryReplacePortalPayload(
        IDatReaderWriter output,
        uint id,
        byte[] bytes,
        int portalIteration,
        object? seedFileTemplates = null) {
        _ = seedFileTemplates;
        return output.TryWriteFileBytes(DatArchive.Portal, id, bytes, portalIteration);
    }

    internal static void RemoveExistingPortalEntry(IDatReaderWriter output, uint id) =>
        LegacyDatCatalogEntryReplacer.RemoveExistingEntry(output, DatArchive.Portal, id);

    internal static int CountCatalogEntries(IDatReaderWriter writer, uint id) =>
        LegacyDatCatalogEntryReplacer.CountCatalogEntries(writer, DatArchive.Portal, id);
}
