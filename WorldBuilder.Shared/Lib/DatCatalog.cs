using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

public static class DatCatalog {
    public static DatArchive ArchiveFromFileName(string datFileName) => Path.GetFileName(datFileName) switch {
        "client_portal.dat" or "portal.dat" => DatArchive.Portal,
        "client_cell_1.dat" or "cell.dat" => DatArchive.Cell,
        "client_local_English.dat" or "language.dat" => DatArchive.Local,
        "client_highres.dat" => DatArchive.Highres,
        _ => throw new ArgumentException($"Unsupported DAT file '{datFileName}'.", nameof(datFileName)),
    };

    public static bool CopyFile(
        IDatReaderWriter source,
        IDatReaderWriter dest,
        DatArchive archive,
        uint id,
        int iteration) {
        if (id == 0 || id == DatRecordTable.IterationId) {
            return false;
        }

        if (!source.TryGetFileBytes(archive, id, out var bytes) || bytes == null) {
            return false;
        }

        return dest.TryWriteFileBytes(archive, id, bytes, iteration);
    }

    public static int RebuildKeepSet(
        IDatReaderWriter writer,
        DatArchive archive,
        IReadOnlySet<uint> keepIds) {
        int iteration = writer.GetIteration(archive);
        var payloads = new Dictionary<uint, byte[]>();
        foreach (uint id in writer.ListFileIds(archive)) {
            if (id == DatRecordTable.IterationId || !keepIds.Contains(id)) {
                continue;
            }

            if (writer.TryGetFileBytes(archive, id, out var bytes) && bytes != null) {
                payloads[id] = bytes;
            }
        }

        writer.ResetTree(archive);
        foreach (var (id, bytes) in payloads) {
            writer.TryWriteFileBytes(archive, id, bytes, iteration);
        }

        writer.SetIteration(archive, iteration);
        return payloads.Count;
    }

    public static void ClearCatalog(IDatReaderWriter writer, DatArchive archive) {
        int iteration = writer.GetIteration(archive);
        writer.ResetTree(archive);
        writer.SetIteration(archive, iteration);
    }
}
