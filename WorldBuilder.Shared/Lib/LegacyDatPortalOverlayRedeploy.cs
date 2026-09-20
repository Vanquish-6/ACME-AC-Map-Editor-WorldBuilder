using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Rebuilds <c>client_portal.dat</c> from the retail seed B-tree, then overlays converted payloads
/// from the export. Clears duplicate catalog entries and restores the retail portal root layout.
/// </summary>
public static class LegacyDatPortalOverlayRedeploy {
    readonly record struct PortalOverlaySnapshotEntry(byte[] Bytes, DatBTreeFile Template);

    public static int Redeploy(
        string exportDirectory,
        string retailSeedDirectory,
        Action<string>? onProgress = null) =>
        Redeploy(
            exportDirectory,
            retailSeedDirectory,
            LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm),
            onProgress);

    public static int Redeploy(
        string exportDirectory,
        string retailSeedDirectory,
        LegacyDatExportPolicy policy,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentNullException.ThrowIfNull(policy);

        string exportPortalPath = Path.Combine(exportDirectory, "client_portal.dat");
        string retailPortalPath = Path.Combine(retailSeedDirectory, "client_portal.dat");
        if (!File.Exists(exportPortalPath) || !File.Exists(retailPortalPath)) {
            return 0;
        }

        onProgress?.Invoke("Snapshotting converted portal payloads before retail portal redeploy...");
        var overlayPayloads = SnapshotNonRetailPortalPayloads(exportDirectory, retailSeedDirectory, policy);

        onProgress?.Invoke("Replacing client_portal.dat with retail seed B-tree...");
        File.Copy(retailPortalPath, exportPortalPath, overwrite: true);
        DatExportFixer.PatchFreeBlocksBeforeExport(exportPortalPath);

        using var seed = new DefaultDatReaderWriter(
            retailSeedDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);
        using var output = new DefaultDatReaderWriter(
            exportDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        var seedPortal = seed;
        var outputPortal = output.Portal();
        int written = 0;
        int processed = 0;

        onProgress?.Invoke($"Overlaying {overlayPayloads.Count:N0} converted portal payload(s) onto retail seed...");
        foreach (var (id, snapshot) in overlayPayloads) {
            processed++;
            if (processed == 1 || processed % 5000 == 0 || processed == overlayPayloads.Count) {
                onProgress?.Invoke($"Overlaying portal payloads ({processed:N0}/{overlayPayloads.Count:N0})...");
            }

            if (id == LegacyDatPortalFileIds.Iteration) {
                continue;
            }

            if (outputPortal.TryWriteFileBytes(id, snapshot.Bytes, snapshot.Bytes.Length, snapshot.Template)) {
                written++;
            }
        }

        output.Dispose();

        PreserveRetailPortalBtreeRoot(retailPortalPath, exportPortalPath);
        LegacyDatRetailShellBootstrap.SyncHeaderFileSize(exportPortalPath);
        DatExportFixer.PatchFreeBlocksBeforeExport(exportPortalPath);

        onProgress?.Invoke($"Portal redeploy complete ({written:N0} converted payload(s) overlaid).");
        return written;
    }

    private static void PreserveRetailPortalBtreeRoot(string retailPortalPath, string exportPortalPath) {
        LegacyDatClientExportFixer.PreserveRetailPortalBtreeRoot(retailPortalPath, exportPortalPath);
    }

    private static Dictionary<uint, PortalOverlaySnapshotEntry> SnapshotNonRetailPortalPayloads(
        string exportDirectory,
        string retailSeedDirectory,
        LegacyDatExportPolicy policy) {
        using var export = new DefaultDatReaderWriter(
            exportDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);
        using var retail = new DefaultDatReaderWriter(
            retailSeedDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        var exportPortal = export.Portal();
        var retailPortal = retail.Portal();
        var protectedRetailIds = CollectProtectedRetailSeedPayloadIds(retail, policy);
        var overlay = new Dictionary<uint, PortalOverlaySnapshotEntry>();
        var seen = new HashSet<uint>();

        foreach (DatBTreeFile file in exportPortal.Catalog().Tree) {
            if (!seen.Add(file.Id)) {
                continue;
            }

            if (file.Id == LegacyDatPortalFileIds.Iteration) {
                continue;
            }

            if (!exportPortal.Catalog().Tree.TryGetFile(file.Id, out DatBTreeFile template)) {
                continue;
            }

            if (!exportPortal.TryGetFileBytes(file.Id, out byte[]? exportBytes, false) || exportBytes == null) {
                continue;
            }

            bool hasRetailPayload = retailPortal.TryGetFileBytes(file.Id, out byte[]? retailBytes, false)
                && retailBytes != null;
            bool matchesRetailPayload = hasRetailPayload
                && retailBytes!.AsSpan().SequenceEqual(exportBytes);

            if (matchesRetailPayload) {
                continue;
            }

            // Retail metadata bands should stay retail-compatible when the seed already provides
            // that exact ID. If the seed lacks the ID entirely, keep the converted payload instead
            // of silently dropping it during redeploy.
            if (!ShouldOverlayConvertedPayload(file.Id, hasRetailPayload, protectedRetailIds)) {
                continue;
            }

            overlay[file.Id] = new PortalOverlaySnapshotEntry(exportBytes, template);
        }

        return overlay;
    }

    internal static HashSet<uint> CollectProtectedRetailSeedPayloadIds(
        DefaultDatReaderWriter retail,
        LegacyDatExportPolicy policy) {
        ArgumentNullException.ThrowIfNull(retail);
        ArgumentNullException.ThrowIfNull(policy);

        var protectedIds = new HashSet<uint>();

        if (policy.Shell.CharGen == LegacyDatContentSource.Retail) {
            foreach (uint id in LegacyDatRetailClientShellCollector.CollectPortalIds(retail)) {
                if (id != LegacyDatRetailClientShellCollector.CharGenId) {
                    protectedIds.Add(id);
                }
            }
        }

        if (LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(policy)) {
            protectedIds.UnionWith(LegacyDatRetailUiShellCollector.CollectPortalIds(retail));
        }

        if (LegacyDatMergeRuleResolver.ShouldPreserveRetailHighresPortalAssets(policy)) {
            protectedIds.UnionWith(LegacyDatRetailHighresShellCollector.CollectPortalIds(retail));
        }

        return protectedIds;
    }

    private static bool ShouldKeepRetailSeedPortalPayload(uint id, IReadOnlySet<uint> protectedRetailIds) {
        if (id == LegacyDatPortalFileIds.Iteration) {
            return true;
        }

        if (protectedRetailIds.Contains(id)) {
            return true;
        }

        if (LegacyDatPortalBootstrap.ShouldKeepRetailPortalClientMetadata(id)) {
            return true;
        }

        if ((id >> 24) == 0x0E && id != LegacyDatRetailClientShellCollector.CharGenId) {
            return true;
        }

        return false;
    }

    internal static bool ShouldOverlayConvertedPayload(
        uint id,
        bool retailSeedHasId,
        IReadOnlySet<uint>? protectedRetailIds = null) =>
        !retailSeedHasId
        || !ShouldKeepRetailSeedPortalPayload(id, protectedRetailIds ?? EmptyProtectedRetailIds);

    private static readonly IReadOnlySet<uint> EmptyProtectedRetailIds = new HashSet<uint>();
}
