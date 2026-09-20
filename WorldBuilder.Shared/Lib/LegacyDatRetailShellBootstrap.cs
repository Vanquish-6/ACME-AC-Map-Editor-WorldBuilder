using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

public static class LegacyDatRetailShellBootstrap {
    public static void PrepareShell(
        string retailSeedDirectory,
        string outputDirectory,
        string datFileName,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(datFileName);

        string sourcePath = Path.Combine(retailSeedDirectory, datFileName);
        string destinationPath = Path.Combine(outputDirectory, datFileName);
        Directory.CreateDirectory(outputDirectory);
        onProgress?.Invoke($"Creating empty retail shell for {datFileName}...");
        File.Copy(sourcePath, destinationPath, overwrite: true);
        DatExportFixer.PatchFreeBlocksBeforeExport(destinationPath);

        using var writer = new DefaultDatReaderWriter(outputDirectory, DatAccessType.ReadWrite);
        var archive = DatCatalog.ArchiveFromFileName(datFileName);
        DatCatalog.ClearCatalog(writer, archive);
        writer.Flush();

        SyncHeaderFileSize(destinationPath);
        DatExportFixer.PatchFreeBlocksBeforeExport(destinationPath);
        onProgress?.Invoke($"Retail shell ready for {datFileName}.");
    }

    internal static void SyncHeaderFileSize(string datPath) => LegacyDatClientExportFixer.SyncHeaderFileSize(datPath);

    internal static DatArchiveSession ResolveDatabase(IDatReaderWriter dats, string datFileName) =>
        new(dats, DatCatalog.ArchiveFromFileName(datFileName));
}
