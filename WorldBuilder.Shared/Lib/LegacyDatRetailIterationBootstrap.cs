using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

public readonly record struct RetailDatIterations(int CellIteration, int PortalIteration) {
    public static RetailDatIterations ReadFromSeed(string retailSeedDirectory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);

        using var seed = new DefaultDatReaderWriter(
            retailSeedDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        return new RetailDatIterations(
            seed.GetIteration(DatArchive.Cell),
            seed.GetIteration(DatArchive.Portal));
    }

    public int ResolvePortalIteration(int portalIterationOverride) =>
        portalIterationOverride > 0 ? portalIterationOverride : PortalIteration;

    public void ApplyToWriter(DefaultDatReaderWriter writer, int portalIterationOverride) {
        ArgumentNullException.ThrowIfNull(writer);

        writer.SetIteration(DatArchive.Cell, CellIteration);
        writer.SetIteration(DatArchive.Portal, ResolvePortalIteration(portalIterationOverride));
    }

    public static void ApplyToOutputDirectory(
        string outputDirectory,
        RetailDatIterations iterations,
        int portalIterationOverride,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        using var writer = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        iterations.ApplyToWriter(writer, portalIterationOverride);
        writer.Dispose();
        onProgress?.Invoke(
            $"Synced DAT iterations (cell={iterations.CellIteration}, portal={iterations.ResolvePortalIteration(portalIterationOverride)}).");
    }
}
