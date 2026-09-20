using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// DM-only (Full DM catalog) portal keep-set and retail gap-fill for client boot.
/// Hybrid slim merge uses retail overlay instead; this path must explicitly retain
/// CharGen heritage chains, spell tables, and UI-linked portal refs after prune.
/// </summary>
public static class LegacyDatFullDmPortalKeeper {
    public static HashSet<uint> CollectKeepIds(
        string outputDirectory,
        string retailSeedDirectory,
        DatExportChecksumTargets convertedPortalTargets,
        LegacyDatExportPolicy policy) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentNullException.ThrowIfNull(convertedPortalTargets);
        ArgumentNullException.ThrowIfNull(policy);

        var keep = new HashSet<uint>(convertedPortalTargets.PortalFileIds);
        keep.Add(0x13000000u);
        keep.Add(LegacyDatPortalFileIds.Iteration);
        keep.Add(LegacyDatRetailClientShellCollector.CharGenId);

        using var output = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);
        using var seed = new DefaultDatReaderWriter(
            retailSeedDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        var portal = output.Portal();
        foreach (uint id in LegacyDatDmClientShellCollector.CollectPortalIds(portal)) {
            keep.Add(id);
        }

        if (LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(policy) && true) {
            foreach (uint id in LegacyDatRetailUiShellCollector.CollectPortalIds(seed)) {
                keep.Add(id);
            }
        }
        else if (policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog
            && LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(policy)
            && true) {
            foreach (uint id in LegacyDatRetailUiShellCollector.CollectPortalIds(seed)) {
                keep.Add(id);
            }
        }

        if (LegacyDatMergeRuleResolver.ShouldPreserveRetailHighresPortalAssets(policy)) {
            foreach (uint id in LegacyDatRetailHighresShellCollector.CollectPortalIds(seed)) {
                keep.Add(id);
            }
        }

        foreach (uint id in portal.Catalog().Tree.Select(file => file.Id)) {
            if (ShouldRetainExistingFullDmObject(id, policy)) {
                keep.Add(id);
            }
        }

        return keep;
    }

    public static int GapFillMissingShellGlobals(
        string outputDirectory,
        string retailSeedDirectory,
        LegacyDatExportPolicy policy,
        int? portalIteration,
        Action<string>? onProgress = null) {
        if (policy.Portal != LegacyDatPortalCatalogPolicy.FullDmCatalog) {
            return 0;
        }

        if (policy.GapFill == LegacyDatRetailGapFillPolicy.Off
            && policy.Shell.SpellTables != LegacyDatContentSource.GapFillRetail) {
            return 0;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);

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

        onProgress?.Invoke("Gap-filling retail shell globals for DM export...");
        foreach (DatBTreeFile file in seedPortal.Catalog().Tree) {
            if (!ShouldGapFillFromRetail(file.Id, policy)) {
                continue;
            }

            if (outputPortal.Catalog().Tree.HasFile(file.Id)) {
                continue;
            }

            if (LegacyDatPortalBootstrap.TryCopyPortalFile(
                seedPortal,
                outputPortal,
                file.Id,
                resolvedPortalIteration,
                seedFileTemplates)) {
                copied++;
            }
        }

        if (LegacyDatMergeRuleResolver.ShouldPatchDmStartingAreas(policy)) {
            // Retail CharGen semantics require the full retail heritage/setup render chain, not just
            // missing IDs. Shared 0x01/0x02/0x05/0x06/0x08 assets at retail shell IDs must replace
            // converted DM payloads or face/clothing slots resolve against the wrong setup chain.
            var charGenShellIds = LegacyDatRetailClientShellCollector.CollectPortalIds(seedPortal);
            charGenShellIds.Remove(LegacyDatRetailClientShellCollector.CharGenId);
            copied += CopyRetailShellSet(
                charGenShellIds,
                overwriteExisting: true,
                "Overlaying retail CharGen shell portal assets for DM export",
                seedPortal,
                outputPortal,
                resolvedPortalIteration,
                seedFileTemplates,
                onProgress);
        }

        if (policy.Shell.AppearanceTables == LegacyDatContentSource.Retail) {
            // Playable Full DM keeps retail appearance-table semantics. Re-apply the retail
            // PalSet / ClothingTable closure after DM world material restore so shared palette /
            // texture slots used by player models do not drift back to DM payloads.
            copied += CopyRetailShellSet(
                LegacyDatRetailAppearanceShellCollector.CollectPortalIds(seedPortal),
                overwriteExisting: true,
                "Overlaying retail appearance shell portal assets for DM export",
                seedPortal,
                outputPortal,
                resolvedPortalIteration,
                seedFileTemplates,
                onProgress);
        }

        if (LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(policy) && true) {
            // Retail LayoutDesc in local.dat + DM portal payloads at shared IDs = DM connect look on retail.
            // Gap-fill missing shell refs only; do not replace existing DM art (see ShouldOverwriteRetailUiShellPortalAssets).
            bool overwriteUiShell = LegacyDatMergeRuleResolver.ShouldOverwriteRetailUiShellPortalAssets(policy);
            copied += CopyRetailShellSet(
                LegacyDatRetailUiShellCollector.CollectPortalIds(seed, seedPortal),
                overwriteExisting: overwriteUiShell,
                overwriteUiShell
                    ? "Overlaying retail UI-shell portal assets for DM export (replacing DM payloads at shared IDs)"
                    : "Gap-filling missing UI-shell portal assets for DM export (keeping DM connect/login art)",
                seedPortal,
                outputPortal,
                resolvedPortalIteration,
                seedFileTemplates,
                onProgress);
        }

        if (true) {
            if (LegacyDatMergeRuleResolver.ShouldPreserveRetailHighresPortalAssets(policy)) {
                // Slim merge: preserve full highres→portal chain after prune.
                copied += CopyRetailShellSet(
                    LegacyDatRetailHighresShellCollector.CollectPortalIds(seed.HighRes(), seedPortal),
                    overwriteExisting: false,
                    "Overlaying retail highres-shell portal assets for DM export",
                    seedPortal,
                    outputPortal,
                    resolvedPortalIteration,
                    seedFileTemplates,
                    onProgress);
            }
            else if (LegacyDatMergeRuleResolver.ShouldOverwriteRetailUiShellPortalAssets(policy)) {
                // Full DM: only restore highres portal deps that also belong to login/char-create UI shell.
                var clientUiPortalIds = CollectClientUiPortalShellIds(seed, policy);
                var highresClientPortalIds = LegacyDatRetailHighresShellCollector.CollectPortalIds(
                    seed.HighRes(),
                    seedPortal);
                highresClientPortalIds.IntersectWith(clientUiPortalIds);
                if (highresClientPortalIds.Count > 0) {
                    copied += CopyRetailShellSet(
                        highresClientPortalIds,
                        overwriteExisting: true,
                        "Overlaying retail highres-linked client UI portal assets for DM export",
                        seedPortal,
                        outputPortal,
                        resolvedPortalIteration,
                        seedFileTemplates,
                        onProgress);
                }
            }
        }

        // Enum/file-map portal metadata (0x11..0x78) must stay retail-compatible. Full DM already
        // has DM objects at many of these IDs, so gap-fill alone leaves retail client init walking
        // converted bytes for ~100+ metadata records.
        copied += CopyRetailShellSet(
            CollectRetailPortalClientMetadataIds(seedPortal),
            overwriteExisting: true,
            "Overlaying retail portal client metadata for DM export",
            seedPortal,
            outputPortal,
            resolvedPortalIteration,
            seedFileTemplates,
            onProgress);

        if (policy.Shell.SpellTables == LegacyDatContentSource.GapFillRetail) {
            // Native client resolves spell/skill/vital tables from portal via GetByEnum (group 2).
            // Gap-fill only copied missing IDs; converted DM payloads at shared 0x0E slots must stay retail.
            // CharGen (0x0E000002) keeps DM starting areas — do not retail-overlay it here.
            copied += CopyRetailShellSet(
                CollectRetailPortalTableIds(seedPortal, policy),
                overwriteExisting: true,
                "Overlaying retail portal spell/skill/vital tables for DM export",
                seedPortal,
                outputPortal,
                resolvedPortalIteration,
                seedFileTemplates,
                onProgress);
        }

        output.Dispose();
        onProgress?.Invoke($"Applied retail shell portal overlay/gap-fill to {copied:N0} object(s).");
        return copied;
    }

    public static void RestoreDmCharGenTable(
        string legacyDatDirectory,
        string outputDirectory,
        string retailSeedDirectory,
        LegacyDatExportPolicy policy,
        Action<string>? onProgress = null) {
        if (!LegacyDatMergeRuleResolver.ShouldPatchDmStartingAreas(policy)) {
            if (!LegacyDatMergeRuleResolver.ShouldApplyDmCharGenExport(policy)) {
                return;
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);

            onProgress?.Invoke("Restoring DM CharGen export after portal shell overlay...");
            using var retailSeed = new DefaultDatReaderWriter(
                retailSeedDirectory,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            using var legacySource = new LegacyDatReader(legacyDatDirectory);
            using var writer = new DefaultDatReaderWriter(
                outputDirectory,
                DatAccessType.ReadWrite,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            var retailSeedFileTemplates = retailSeed.Portal().Catalog().Tree.ToDictionary(file => file.Id);
            LegacyDatCharGenMapper.ApplyDmCharGenExport(
                legacySource,
                writer,
                retailSeedDirectory,
                worldClosure: null,
                warnings: null,
                retailSeedFileTemplates);
            writer.Dispose();
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);

        onProgress?.Invoke("Restoring retail CharGen shell + DM starting areas after portal overlay...");
        using var seed = new DefaultDatReaderWriter(
            retailSeedDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);
        using var legacy = new LegacyDatReader(legacyDatDirectory);
        using var output = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        var seedPortal = seed.Portal();
        var outputPortal = output.Portal();
        var seedFileTemplates = seedPortal.Catalog().Tree.ToDictionary(file => file.Id);
        LegacyDatPortalBootstrap.TryCopyPortalFile(
            seedPortal,
            outputPortal,
            LegacyDatRetailClientShellCollector.CharGenId,
            seedPortal.CurrentIteration,
            seedFileTemplates);

        LegacyDatCharGenMapper.ApplySlimMergeStartingAreas(
            legacy,
            output,
            worldClosure: null,
            warnings: null,
            seedFileTemplates);
        LegacyDatCharGenSpawnCellExporter.EnsureSpawnCells(legacy, output, onProgress);
        output.Dispose();
    }

    internal static HashSet<uint> CollectClientUiPortalShellIds(
        DefaultDatReaderWriter seed,
        LegacyDatExportPolicy policy) {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(policy);

        var seedPortal = seed;
        var ids = new HashSet<uint>();

        if (true && LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(policy)) {
            ids.UnionWith(LegacyDatRetailUiShellCollector.CollectPortalIds(seed, seedPortal));
        }

        if (LegacyDatMergeRuleResolver.ShouldPatchDmStartingAreas(policy)) {
            foreach (uint id in LegacyDatRetailClientShellCollector.CollectPortalIds(seedPortal)) {
                ids.Add(id);
            }
        }

        return ids;
    }

    internal static HashSet<uint> CollectRetailPortalClientMetadataIds(IDatReaderWriter seedPortal) {
        ArgumentNullException.ThrowIfNull(seedPortal);

        var ids = new HashSet<uint>();
        foreach (DatBTreeFile file in seedPortal.Catalog().Tree) {
            if (LegacyDatPortalBootstrap.ShouldKeepRetailPortalClientMetadata(file.Id)) {
                ids.Add(file.Id);
            }
        }

        return ids;
    }

    internal static HashSet<uint> CollectRetailPortalTableIds(
        IDatReaderWriter seedPortal,
        LegacyDatExportPolicy policy) {
        ArgumentNullException.ThrowIfNull(seedPortal);
        ArgumentNullException.ThrowIfNull(policy);

        var ids = new HashSet<uint>();
        foreach (DatBTreeFile file in seedPortal.Catalog().Tree) {
            if ((file.Id >> 24) != 0x0E) {
                continue;
            }

            if (LegacyDatMergeRuleResolver.ShouldPatchDmStartingAreas(policy)
                && file.Id == LegacyDatRetailClientShellCollector.CharGenId) {
                continue;
            }

            ids.Add(file.Id);
        }

        return ids;
    }

    private static int CopyRetailShellSet(
        HashSet<uint> ids,
        bool overwriteExisting,
        string progressLabel,
        IDatReaderWriter seedPortal,
        IDatReaderWriter outputPortal,
        int resolvedPortalIteration,
        IReadOnlyDictionary<uint, DatBTreeFile> seedFileTemplates,
        Action<string>? onProgress) {
        int copied = 0;
        onProgress?.Invoke($"{progressLabel} ({ids.Count:N0})...");
        foreach (uint id in ids) {
            if (!overwriteExisting && outputPortal.Catalog().Tree.HasFile(id)) {
                continue;
            }

            if (LegacyDatPortalBootstrap.TryCopyPortalFile(
                seedPortal,
                outputPortal,
                id,
                resolvedPortalIteration,
                seedFileTemplates)) {
                copied++;
            }
        }

        return copied;
    }

    internal static bool ShouldGapFillFromRetail(uint id, LegacyDatExportPolicy policy) {
        if (policy.Shell.SpellTables == LegacyDatContentSource.GapFillRetail && (id >> 24) == 0x0E) {
            return true;
        }

        if (policy.GapFill == LegacyDatRetailGapFillPolicy.Off) {
            return false;
        }

        if ((id >> 24) is 0x0F or 0x10 && policy.Shell.AppearanceTables == LegacyDatContentSource.Dm) {
            return false;
        }

        if (LegacyDatPortalBootstrap.ShouldKeepRetailPortalClientMetadata(id)) {
            return true;
        }

        return policy.Shell.SpellTables == LegacyDatContentSource.Retail
            && LegacyDatPortalBootstrap.ShouldKeepRetailPortalGlobal(id);
    }

    private static bool ShouldRetainExistingFullDmObject(uint id, LegacyDatExportPolicy policy) {
        if ((id >> 24) == 0x0E
            && LegacyDatMergeRuleResolver.ShouldPatchDmStartingAreas(policy)
            && id == LegacyDatRetailClientShellCollector.CharGenId) {
            return true;
        }

        if (policy.Shell.SpellTables == LegacyDatContentSource.GapFillRetail && (id >> 24) == 0x0E) {
            return true;
        }

        if (LegacyDatPortalBootstrap.ShouldKeepRetailPortalClientMetadata(id)
            && policy.GapFill != LegacyDatRetailGapFillPolicy.Off) {
            return true;
        }

        return false;
    }
}

/// <summary>
/// Expands DM CharGen heritage refs (including legacy 0x31...... metadata IDs) and render chains.
/// </summary>
public static class LegacyDatDmClientShellCollector {
    public static HashSet<uint> CollectPortalIds(IDatReaderWriter portal) {
        ArgumentNullException.ThrowIfNull(portal);

        var keep = new HashSet<uint>();
        var pending = new Queue<uint>();

        void Enqueue(uint id) {
            if (id == 0 || !keep.Add(id)) {
                return;
            }

            pending.Enqueue(id);
        }

        Enqueue(LegacyDatRetailClientShellCollector.CharGenId);
        if (portal.TryGet(LegacyDatRetailClientShellCollector.CharGenId, out CharGen? charGen) && charGen != null) {
            foreach (var (_, group) in charGen.HeritageGroups) {
                Enqueue(group.SetupId);
                Enqueue(group.EnvironmentSetupId);
            }
        }

        while (pending.Count > 0) {
            uint id = pending.Dequeue();
            if ((id >> 24) == 0x31) {
                continue;
            }

            LegacyDatRetailPortalReferenceExpander.ExpandPortalReference(portal, id, Enqueue);
        }

        return keep;
    }
}
