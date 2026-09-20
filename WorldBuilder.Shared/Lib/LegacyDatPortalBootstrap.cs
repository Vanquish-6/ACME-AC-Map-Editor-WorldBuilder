using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Retail portal.dat globals required for a playable client shell in slim merge.
/// Full DM intentionally omits these retail globals.
/// </summary>
public static class LegacyDatPortalBootstrap {
    public static HashSet<uint> CollectSlimMergeKeepIds(
        string retailSeedDirectory,
        DatExportChecksumTargets convertedPortalTargets,
        LegacyDatExportPolicy? policy = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentNullException.ThrowIfNull(convertedPortalTargets);
        policy ??= LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);

        var keep = new HashSet<uint>(convertedPortalTargets.PortalFileIds);

        using var seed = new DefaultDatReaderWriter(
            retailSeedDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        foreach (DatBTreeFile file in seed.Portal().Catalog().Tree) {
            if (ShouldKeepRetailPortalForClosureMerge(file.Id, policy)) {
                keep.Add(file.Id);
            }
        }

        if (policy.Shell.CharGen == LegacyDatContentSource.Retail) {
            foreach (uint id in LegacyDatRetailClientShellCollector.CollectPortalIds(seed)) {
                keep.Add(id);
            }
        }

        if (LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(policy)) {
            foreach (uint id in LegacyDatRetailUiShellCollector.CollectPortalIds(seed)) {
                keep.Add(id);
            }
        }

        foreach (uint id in LegacyDatRetailHighresShellCollector.CollectPortalIds(seed)) {
            keep.Add(id);
        }

        keep.Add(0x13000000u);
        keep.Add(LegacyDatPortalFileIds.Iteration);
        keep.Add(LegacyDatRetailClientShellCollector.CharGenId);
        return keep;
    }

    internal static bool ShouldKeepRetailPortalForClosureMerge(uint id, LegacyDatExportPolicy policy) =>
        LegacyDatMergeRuleResolver.ShouldKeepRetailPortalForClosureMerge(id, policy);

    public static int MergeRetailGlobals(
        string outputDirectory,
        string retailSeedDirectory,
        DatExportChecksumTargets convertedPortalTargets,
        int? portalIteration,
        LegacyDatExportPolicy policy,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentNullException.ThrowIfNull(convertedPortalTargets);
        ArgumentNullException.ThrowIfNull(policy);

        bool hasConvertedRegion = convertedPortalTargets.PortalFileIds.Contains(0x13000000u);

        using var seed = new DefaultDatReaderWriter(
            retailSeedDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);
        using var output = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        var seedPortal = seed.Portal();
        var outputPortal = output.Portal();
        int resolvedPortalIteration = portalIteration ?? seedPortal.CurrentIteration;
        var seedFileTemplates = seedPortal.Catalog().Tree.ToDictionary(file => file.Id);
        int copied = 0;

        onProgress?.Invoke("Merging required retail portal globals...");
        foreach (DatBTreeFile file in seedPortal.Catalog().Tree) {
            if (!ShouldKeepRetailPortalForClosureMerge(file.Id, policy)) {
                continue;
            }

            if (hasConvertedRegion && file.Id == 0x13000000u) {
                continue;
            }

            if (TryCopyPortalFile(seedPortal, outputPortal, file.Id, resolvedPortalIteration, seedFileTemplates)) {
                copied++;
            }
        }

        var clientShellIds = policy.Shell.CharGen == LegacyDatContentSource.Retail
            ? LegacyDatRetailClientShellCollector.CollectPortalIds(seedPortal)
            : new HashSet<uint>();
        onProgress?.Invoke($"Merging retail client-shell portal assets ({clientShellIds.Count:N0})...");
        foreach (uint id in clientShellIds) {
            if (TryCopyPortalFile(seedPortal, outputPortal, id, resolvedPortalIteration, seedFileTemplates)) {
                copied++;
            }
        }

        var uiShellIds = LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(policy)
            ? LegacyDatRetailUiShellCollector.CollectPortalIds(seed.Local(), seedPortal)
            : new HashSet<uint>();
        onProgress?.Invoke($"Merging retail UI-shell portal assets ({uiShellIds.Count:N0})...");
        foreach (uint id in uiShellIds) {
            if (TryCopyPortalFile(seedPortal, outputPortal, id, resolvedPortalIteration, seedFileTemplates)) {
                copied++;
            }
        }

        var highresShellIds = LegacyDatRetailHighresShellCollector.CollectPortalIds(
            seed.HighRes(),
            seedPortal);
        onProgress?.Invoke($"Merging retail highres-shell portal assets ({highresShellIds.Count:N0})...");
        foreach (uint id in highresShellIds) {
            if (TryCopyPortalFile(seedPortal, outputPortal, id, resolvedPortalIteration, seedFileTemplates)) {
                copied++;
            }
        }

        output.Dispose();
        onProgress?.Invoke($"Merged {copied:N0} retail portal global object(s).");
        return copied;
    }

    public static int RepairPortalFileEntryMetadata(
        string outputDirectory,
        string retailSeedDirectory,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);

        LegacyDatClientExportFixer.PatchCatalogFilesForWriting(outputDirectory, "client_portal.dat");

        using var seed = new DefaultDatReaderWriter(
            retailSeedDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);
        using var output = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        var seedPortal = seed.Portal();
        var outputPortal = output.Portal();
        int resolvedPortalIteration = seedPortal.CurrentIteration;
        var seedFileTemplates = seedPortal.Catalog().Tree.ToDictionary(file => file.Id);

        // Single catalog scan: distinct entries to process + the set of genuinely duplicated ids.
        // (Counting entries per id inside the loop is O(n^2) over a ~80k-entry catalog.)
        var portalFiles = new List<DatBTreeFile>();
        var duplicateIds = new HashSet<uint>();
        var seenIds = new HashSet<uint>();
        foreach (DatBTreeFile entry in outputPortal.Catalog().Tree) {
            if (seenIds.Add(entry.Id)) {
                portalFiles.Add(entry);
            }
            else {
                duplicateIds.Add(entry.Id);
            }
        }

        int repaired = 0;

        onProgress?.Invoke("Repairing portal B-tree entry metadata...");
        foreach (DatBTreeFile file in portalFiles) {
            if (!outputPortal.TryGetFileBytes(file.Id, out byte[] bytes, false)) {
                continue;
            }

            bool hasDuplicates = duplicateIds.Contains(file.Id);

            if (seedFileTemplates.TryGetValue(file.Id, out DatBTreeFile template)) {
                // Only copy retail catalog metadata when the payload is still retail bytes.
                // Full DM reuses many retail IDs for converted DM objects; stamping the retail
                // template version onto DM payloads breaks the native client during InitLoad.
                if (seedPortal.TryGetFileBytes(file.Id, out byte[]? seedBytes, false)
                    && seedBytes != null
                    && seedBytes.AsSpan().SequenceEqual(bytes)) {
                    if (hasDuplicates) {
                        LegacyDatPortalCatalogDeduplicator.RemoveExistingPortalEntry(outputPortal, file.Id);
                    }

                    if (outputPortal.TryWriteFileBytes(file.Id, bytes, bytes.Length, template)) {
                        repaired++;
                    }

                    continue;
                }

                if (!hasDuplicates) {
                    continue;
                }

                // Collapse duplicate DM payload entries back to a single catalog row while preserving
                // the current non-retail portal metadata instead of stamping the retail seed template.
                LegacyDatPortalCatalogDeduplicator.RemoveExistingPortalEntry(outputPortal, file.Id);
                if (outputPortal.TryWriteFileBytes(file.Id, bytes, bytes.Length, file)) {
                    repaired++;
                }

                continue;
            }

            if (!hasDuplicates && file.Iteration == resolvedPortalIteration) {
                continue;
            }

            if (hasDuplicates) {
                LegacyDatPortalCatalogDeduplicator.RemoveExistingPortalEntry(outputPortal, file.Id);
                var collapsed = file;
                collapsed.Iteration = resolvedPortalIteration;
                if (outputPortal.TryWriteFileBytes(file.Id, bytes, bytes.Length, collapsed)) {
                    repaired++;
                }

                continue;
            }

            if (outputPortal.TryWriteFileBytes(file.Id, bytes, bytes.Length, resolvedPortalIteration)) {
                repaired++;
            }
        }

        output.Dispose();
        onProgress?.Invoke($"Repaired {repaired:N0} portal file entry metadata record(s).");
        return repaired;
    }

    internal static bool TryCopyPortalFile(
        IDatReaderWriter seedPortal,
        IDatReaderWriter outputPortal,
        uint id,
        int portalIteration,
        IReadOnlyDictionary<uint, DatBTreeFile> seedFileTemplates) {
        if (!seedPortal.Catalog().TryGetFileBytes(id, out byte[] bytes, false)) {
            return false;
        }

        return LegacyDatPortalCatalogDeduplicator.TryReplacePortalFile(
            seedPortal,
            outputPortal,
            id,
            portalIteration,
            seedFileTemplates);
    }

    private static bool TryCopyPortalFile(
        IDatReaderWriter seedPortal,
        IDatReaderWriter outputPortal,
        uint id,
        int? portalIteration) {
        int resolvedPortalIteration = portalIteration ?? seedPortal.Portal().CurrentIteration;
        var seedFileTemplates = seedPortal.Portal().Catalog().Tree.ToDictionary(file => file.Id);
        return TryCopyPortalFile(seedPortal, outputPortal, id, resolvedPortalIteration, seedFileTemplates);
    }

    internal static bool ShouldKeepRetailPortalForSlimMerge(uint id) =>
        ShouldKeepRetailPortalGlobal(id) || ShouldKeepRetailPortalClientMetadata(id);

    internal static bool ShouldKeepRetailPortalGlobal(uint id) {
        return (id >> 24) switch {
            0x0E => true, // Spell/skill/experience/vital/CharGen tables
            // PalSet and ClothingTable are converted from DM when present; do not force retail copies.
            0x13 => true, // Region
            _ => false,
        };
    }

    /// <summary>
    /// Portal metadata the retail client needs to resolve local/highres filenames and enum maps.
    /// Slim merge prunes unreferenced assets but must retain these non-world objects from seed.
    /// </summary>
    internal static bool ShouldKeepRetailPortalClientMetadata(uint id) {
        return (id >> 24) switch {
            0x11 or 0x12 or 0x14 or 0x15 or 0x16 or 0x17 or 0x18
            or 0x20 or 0x22 or 0x25 or 0x26 or 0x27
            or 0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x39 or 0x40 or 0x78 => true,
            _ => false,
        };
    }
}
