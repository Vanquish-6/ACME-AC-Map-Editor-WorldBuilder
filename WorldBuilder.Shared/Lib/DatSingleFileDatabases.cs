using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

public static class DatDirectoryLayout {
    public static string Isolate(string path, string retailFileName) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Directory.Exists(path)) {
            return Path.GetFullPath(path);
        }

        if (!File.Exists(path)) {
            throw new FileNotFoundException("DAT path not found.", path);
        }

        string full = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(full)!;
        if (string.Equals(Path.GetFileName(full), retailFileName, StringComparison.OrdinalIgnoreCase)) {
            return parent;
        }

        string dir = Path.Combine(Path.GetTempPath(), "acme-dat-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(full, Path.Combine(dir, retailFileName), overwrite: true);
        return dir;
    }

    public static string RetailFileName(DatArchive archive) => archive switch {
        DatArchive.Cell => "client_cell_1.dat",
        DatArchive.Local => "client_local_English.dat",
        DatArchive.Highres => "client_highres.dat",
        _ => "client_portal.dat",
    };

    public static DatArchive ArchiveFromPath(string path) {
        string name = Path.GetFileName(path);
        return name.ToLowerInvariant() switch {
            "client_cell_1.dat" or "cell.dat" => DatArchive.Cell,
            "client_local_english.dat" or "language.dat" => DatArchive.Local,
            "client_highres.dat" => DatArchive.Highres,
            _ => DatArchive.Portal,
        };
    }
}

public class CellDatabase : DefaultDatReaderWriter {
    public CellDatabase(string path, DatAccessType accessType)
        : base(DatDirectoryLayout.Isolate(path, DatDirectoryLayout.RetailFileName(DatArchive.Cell)), accessType, DatArchive.Cell) {
    }
}

public class LocalDatabase : DefaultDatReaderWriter {
    public LocalDatabase(string path, DatAccessType accessType)
        : base(DatDirectoryLayout.Isolate(path, DatDirectoryLayout.RetailFileName(DatArchive.Local)), accessType, DatArchive.Local) {
    }
}

public class PortalDatabase : DefaultDatReaderWriter {
    public PortalDatabase(string path, DatAccessType accessType)
        : base(DatDirectoryLayout.Isolate(path, DatDirectoryLayout.RetailFileName(DatArchive.Portal)), accessType, DatArchive.Portal) {
    }
}
