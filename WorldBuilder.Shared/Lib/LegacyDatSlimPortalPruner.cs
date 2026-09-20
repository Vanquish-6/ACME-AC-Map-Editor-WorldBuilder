using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Removes unreferenced portal.dat entries after slim merge conversion.
/// Slim merge overlays converted assets onto a full retail portal copy, then prunes strays.
/// </summary>
public static class LegacyDatSlimPortalPruner {
    public static int Prune(
        string outputDirectory,
        string retailSeedDirectory,
        DatExportChecksumTargets convertedPortalTargets,
        LegacyDatExportPolicy policy,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentNullException.ThrowIfNull(convertedPortalTargets);
        ArgumentNullException.ThrowIfNull(policy);

        var keepIds = LegacyDatPortalBootstrap.CollectSlimMergeKeepIds(retailSeedDirectory, convertedPortalTargets, policy);

        return LegacyDatPortalCatalogPruner.PrunePortal(outputDirectory, keepIds, null, onProgress);
    }
}
