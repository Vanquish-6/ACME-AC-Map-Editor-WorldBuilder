using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

internal static class LegacyDatCatalogEntryReplacer {
    internal static bool TryReplaceFile<T>(IDatReaderWriter writer, T file, int? iteration = 0)
        where T : class, IDatRecord, new() {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(file);
        return writer.TrySave(file, iteration);
    }

    internal static int CountCatalogEntries(IDatReaderWriter writer, uint id) {
        if (writer is DatArchiveSession session) {
            return CountCatalogEntries(writer, session.Archive, id);
        }

        if (writer is DefaultDatReaderWriter def) {
            return CountCatalogEntries(writer, def.PrimaryArchive, id);
        }

        return CountCatalogEntries(writer, DatArchive.Portal, id)
            + CountCatalogEntries(writer, DatArchive.Cell, id)
            + CountCatalogEntries(writer, DatArchive.Local, id)
            + CountCatalogEntries(writer, DatArchive.Highres, id);
    }

    internal static int CountCatalogEntries(IDatReaderWriter writer, DatArchive archive, uint id) =>
        writer.ListFileIds(archive).Count(fileId => fileId == id);

    internal static void RemoveExistingEntry(IDatReaderWriter writer, DatArchive archive, uint id) {
        if (id == 0 || id == DatRecordTable.IterationId) {
            return;
        }

        var keep = writer.ListFileIds(archive).Where(fileId => fileId != id).ToHashSet();
        DatCatalog.RebuildKeepSet(writer, archive, keep);
    }
}
