using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Removes retail Dereth world content from output cell.dat after conversion.
/// Conversion seeds from the full retail cell file, then this pass keeps only
/// landblocks, land block info, and env cells present in the legacy source.
/// </summary>
public static class LegacyDatCellWorldPruner {
    public static HashSet<uint> CollectKeepIds(LegacyDatReader legacySource) {
        ArgumentNullException.ThrowIfNull(legacySource);
        var keepIds = legacySource.GetAllCellFileIds();
        foreach (uint id in LegacyDatCharGenMapper.CollectStartingAreaCellFileIds(legacySource)) {
            keepIds.Add(id);
        }

        keepIds.Add(LegacyDatPortalFileIds.Iteration);
        return keepIds;
    }

    public static int Prune(
        string outputDirectory,
        LegacyDatReader legacySource,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(legacySource);

        var keepIds = CollectKeepIds(legacySource);

        using var writer = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        int before = writer.ListFileIds(DatArchive.Cell).Length;
        onProgress?.Invoke($"Pruning retail world cell objects (keeping {keepIds.Count:N0} legacy ids)...");
        int kept = DatCatalog.RebuildKeepSet(writer, DatArchive.Cell, keepIds);
        writer.Flush();
        int deleted = Math.Max(0, before - kept);
        onProgress?.Invoke($"Cell world prune complete ({deleted:N0} retail object(s) removed, {keepIds.Count:N0} legacy cell object(s) retained).");
        return deleted;
    }
}
