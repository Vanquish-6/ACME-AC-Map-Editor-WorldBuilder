using System.Text;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib {
    public enum LegacyDatConversionPhase {
        Phase1WorldData = 0,
        Phase2AssetGraph = 1,
        Phase3LocalData = 2,
    }

    public enum LegacyDatConversionStatus {
        Supported = 0,
        Synthetic = 1,
        Fallback = 2,
        Deferred = 3,
        Unsupported = 4,
    }

    public enum LegacyDatConversionSeverity {
        Info = 0,
        Warning = 1,
        Error = 2,
    }

    public sealed class LegacyDatConversionFinding {
        public required string ObjectType { get; init; }
        public required LegacyDatConversionSeverity Severity { get; init; }
        public required string Message { get; init; }
    }

    public sealed class LegacyDatConversionTypeAudit {
        public required string ObjectType { get; init; }
        public required string SourceDat { get; init; }
        public required LegacyDatConversionPhase Phase { get; init; }
        public string Notes { get; init; } = string.Empty;
        public int SourceCount { get; set; }
        public int ConvertibleCount { get; set; }
        public int WrittenCount { get; set; }
        public int SyntheticCount { get; set; }
        public int FallbackCount { get; set; }
        public int DeferredCount { get; set; }
        public int UnsupportedCount { get; set; }
    }

    public sealed class LegacyDatConversionAudit {
        public required string LegacyDatDirectory { get; init; }
        public required string SourceVersion { get; init; }
        public required int SourceIteration { get; init; }
        public required bool HasLanguageDat { get; init; }
        public IReadOnlyList<LegacyDatConversionTypeAudit> Types { get; init; } = Array.Empty<LegacyDatConversionTypeAudit>();
        public IReadOnlyList<LegacyDatConversionFinding> Findings { get; init; } = Array.Empty<LegacyDatConversionFinding>();

        public int TotalSourceObjects => Types.Sum(type => type.SourceCount);
        public int TotalConvertibleObjects => Types.Sum(type => type.ConvertibleCount);
        public bool HasErrors => Findings.Any(finding => finding.Severity == LegacyDatConversionSeverity.Error);
        public bool HasWarnings => Findings.Any(finding => finding.Severity == LegacyDatConversionSeverity.Warning);
    }

    public sealed class LegacyToRetailConversionOptions {
        public required string LegacyDatDirectory { get; init; }
        public required string RetailSeedDirectory { get; init; }
        public required string OutputDirectory { get; init; }
        public LegacyDatExportMode ExportMode { get; init; } = LegacyDatExportMode.FullMerge;
        public LegacyDatExportPolicy? ExportPolicy { get; init; }
        public int PortalIteration { get; init; }
        public bool IncludePhase2LocalData { get; init; }

        /// <summary>
        /// Optional extra world-completion folder (manifest + chunks). When set on a DM-only
        /// world export, LandBlock / LandBlockInfo / EnvCell records missing from the client
        /// <c>cell.dat</c> subset are imported from that folder.
        /// </summary>
        public string? ServerWorldDataDirectory { get; init; }

        internal LegacyDatExportPolicy ResolvedPolicy =>
            ExportPolicy ?? LegacyDatExportPolicy.FromLegacyMode(ExportMode);

        internal bool IncludePhase3LocalData =>
            IncludePhase2LocalData || LegacyDatMergeRuleResolver.IncludePhase3LocalData(ResolvedPolicy);
    }

    public sealed class LegacyToRetailConversionResult {
        public required LegacyDatConversionAudit Audit { get; init; }
        public required string OutputDirectory { get; init; }
        public required string ManifestPath { get; init; }
        public required DatExportChecksumReport ChecksumReport { get; init; }
        public required IReadOnlyDictionary<string, int> WrittenCounts { get; init; }
        public IReadOnlyList<string> ConversionWarnings { get; init; } = Array.Empty<string>();

        public int TotalWritten => WrittenCounts.Values.Sum();
    }

    internal sealed record LegacyDatConversionClassification(
        LegacyDatConversionStatus Status,
        string? Message = null);

    internal sealed record LegacyDatConversionTypeDefinition(
        string ObjectType,
        string SourceDat,
        LegacyDatConversionPhase Phase,
        string Notes,
        bool EnabledForConversion,
        Func<LegacyDatReader, IReadOnlyList<uint>> EnumerateIds,
        Func<LegacyDatConversionAuditContext, uint, LegacyDatConversionClassification> Classify,
        Func<LegacyDatReader, DefaultDatReaderWriter, uint, int?, bool> Copy);

    internal sealed class LegacyDatConversionAuditContext : IDisposable {
        private readonly string _datDirectory;

        public LegacyDatReader Source { get; }
        public LegacyDatDatabase CellDatabase { get; }
        public LegacyDatDatabase PortalDatabase { get; }
        public LegacyDatDatabase? LocalDatabase { get; }

        public LegacyDatVersion Version => PortalDatabase.Version;
        public int Iteration => PortalDatabase.Iteration;
        public uint PreferredTerrainTextureId => PortalDatabase.FileIds
            .Where(fileId => (fileId >> 24) == 0x05)
            .OrderBy(fileId => fileId)
            .FirstOrDefault();

        public LegacyDatConversionAuditContext(string datDirectory) {
            _datDirectory = datDirectory;
            Source = new LegacyDatReader(datDirectory);
            CellDatabase = new LegacyDatDatabase(Path.Combine(datDirectory, "cell.dat"), isCellDatabase: true);
            PortalDatabase = new LegacyDatDatabase(Path.Combine(datDirectory, "portal.dat"), isCellDatabase: false);

            string localPath = Path.Combine(datDirectory, "language.dat");
            if (File.Exists(localPath)) {
                LocalDatabase = new LegacyDatDatabase(localPath, isCellDatabase: false);
            }
        }

        public void Dispose() {
            Source.Dispose();
            CellDatabase.Dispose();
            PortalDatabase.Dispose();
            LocalDatabase?.Dispose();
        }
    }

    public static class LegacyDatConversionService {
        internal static readonly System.Threading.AsyncLocal<IReadOnlyDictionary<uint, uint>?> ActiveRenderSurfaceIdMap = new();
        private static readonly IReadOnlyList<LegacyDatConversionTypeDefinition> TypeDefinitions = new[] {
            CreateType<LandBlock>("LandBlock", "cell.dat", LegacyDatConversionPhase.Phase1WorldData, "Terrain cells and heights."),
            CreateType<LandBlockInfo>("LandBlockInfo", "cell.dat", LegacyDatConversionPhase.Phase1WorldData, "Outdoor statics, buildings, and restrictions."),
            CreateType<EnvCell>("EnvCell", "cell.dat", LegacyDatConversionPhase.Phase1WorldData, "Dungeon cells and portal links."),
            CreateType<Setup>("Setup", "portal.dat", LegacyDatConversionPhase.Phase1WorldData, "3D setup definitions used by world objects."),
            CreateType<GfxObj>(
                "GfxObj",
                "portal.dat",
                LegacyDatConversionPhase.Phase2AssetGraph,
                "Legacy GfxObj BSP trees are rebuilt from decoded polygon and vertex data before serialization."),
            CreateType<ParticleEmitter>("ParticleEmitter", "portal.dat", LegacyDatConversionPhase.Phase1WorldData, "Particle system definitions."),
            CreateType<Scene>("Scene", "portal.dat", LegacyDatConversionPhase.Phase1WorldData, "Scene graph data referenced by setups."),
            CreateType<Surface>("Surface", "portal.dat", LegacyDatConversionPhase.Phase1WorldData, "Legacy materials adapted into retail surface objects."),
            CreateType<Palette>("Palette", "portal.dat", LegacyDatConversionPhase.Phase1WorldData, "Palette data referenced by textures."),
            CreateType<PalSet>(
                "PalSet",
                "portal.dat",
                LegacyDatConversionPhase.Phase1WorldData,
                "Palette sets used by creature clothing colorization."),
            CreateType<ClothingTable>(
                "ClothingTable",
                "portal.dat",
                LegacyDatConversionPhase.Phase1WorldData,
                "Clothing sub-palette tables used by creature appearance."),
            CreateType<RenderTexture>("RenderTexture", "portal.dat", LegacyDatConversionPhase.Phase1WorldData, "Render texture payloads."),
            CreateType<RenderSurface>(
                "RenderSurface",
                "portal.dat",
                LegacyDatConversionPhase.Phase1WorldData,
                "Dark Majesty surface textures are widened into synthetic retail render surfaces.",
                classify: (ctx, id) => {
                    if (!ctx.Source.TryGet<RenderSurface>(id, out _)) {
                        return new LegacyDatConversionClassification(
                            LegacyDatConversionStatus.Unsupported,
                            $"RenderSurface 0x{id:X8} could not be materialized from the legacy source.");
                    }

                    if (LegacyDatReader.IsSyntheticRenderSurfaceId(id)) {
                        return new LegacyDatConversionClassification(
                            LegacyDatConversionStatus.Synthetic,
                            $"RenderSurface IDs generated from legacy SurfaceTexture entries will be remapped into retail 0x06...... IDs during conversion.");
                    }

                    return new LegacyDatConversionClassification(LegacyDatConversionStatus.Supported);
                },
                copy: CopyRenderSurface),
            CreateType<SurfaceTexture>(
                "SurfaceTexture",
                "portal.dat",
                LegacyDatConversionPhase.Phase1WorldData,
                "Texture-to-surface bindings.",
                copy: CopySurfaceTexture),
            CreateType<Acme.Dat.Environment>(
                "Environment",
                "portal.dat",
                LegacyDatConversionPhase.Phase2AssetGraph,
                "Legacy Environment cell structures preserve DM BSP trees from the source payload and only synthesize missing retail drawing BSP when absent."),
            CreateType<Region>(
                "Region",
                "portal.dat",
                LegacyDatConversionPhase.Phase1WorldData,
                "Legacy region data is exported to the retail alias ID 0x13000000.",
                classify: (ctx, id) => ClassifyRegion(ctx, id)),
            CreateType<LayoutDesc>(
                "LayoutDesc",
                "language.dat",
                LegacyDatConversionPhase.Phase3LocalData,
                "UI layout definitions copied from legacy language.dat into client_local_English.dat."),
            CreateType<StringTable>(
                "StringTable",
                "language.dat",
                LegacyDatConversionPhase.Phase3LocalData,
                "String tables copied from legacy language.dat into client_local_English.dat."),
        };

        public static LegacyDatConversionAudit Audit(string legacyDatDirectory, bool includePhase2LocalData = false) {
            EnsureLegacySourceDirectory(legacyDatDirectory);

            using var context = new LegacyDatConversionAuditContext(legacyDatDirectory);
            var findings = new List<LegacyDatConversionFinding>();
            var typeAudits = new List<LegacyDatConversionTypeAudit>(TypeDefinitions.Count);

            foreach (var definition in TypeDefinitions) {
                var ids = definition.EnumerateIds(context.Source);
                var audit = new LegacyDatConversionTypeAudit {
                    ObjectType = definition.ObjectType,
                    SourceDat = definition.SourceDat,
                    Phase = definition.Phase,
                    Notes = definition.Notes,
                    SourceCount = ids.Count,
                };

                if (ids.Count == 0) {
                    typeAudits.Add(audit);
                    continue;
                }

                if (ShouldDeferDefinition(definition, includePhase2LocalData)) {
                    audit.DeferredCount = ids.Count;
                    findings.Add(new LegacyDatConversionFinding {
                        ObjectType = definition.ObjectType,
                        Severity = LegacyDatConversionSeverity.Info,
                        Message = $"{definition.ObjectType}: {ids.Count} source object(s) deferred. {definition.Notes}",
                    });
                    typeAudits.Add(audit);
                    continue;
                }

                SummarizeClassification(definition, context, ids, audit, findings);
                typeAudits.Add(audit);
            }

            if (context.LocalDatabase != null && !includePhase2LocalData) {
                findings.Add(new LegacyDatConversionFinding {
                    ObjectType = "client_local_English.dat",
                    Severity = LegacyDatConversionSeverity.Warning,
                    Message = "language.dat was detected, but local/layout/string conversion is intentionally deferred until the later local-data conversion pass.",
                });
            }

            return new LegacyDatConversionAudit {
                LegacyDatDirectory = Path.GetFullPath(legacyDatDirectory),
                SourceVersion = GetVersionDisplayName(context.Version),
                SourceIteration = context.Iteration,
                HasLanguageDat = context.LocalDatabase != null,
                Types = typeAudits,
                Findings = findings,
            };
        }

        public static LegacyToRetailConversionResult Convert(
            LegacyToRetailConversionOptions options,
            Action<string>? onProgress = null) {
            ArgumentNullException.ThrowIfNull(options);

            string legacyDatDirectory = Path.GetFullPath(options.LegacyDatDirectory);
            string retailSeedDirectory = Path.GetFullPath(options.RetailSeedDirectory);
            string outputDirectory = Path.GetFullPath(options.OutputDirectory);
            var policy = options.ResolvedPolicy;

            EnsureLegacySourceDirectory(legacyDatDirectory);
            EnsureRetailSeedDirectory(retailSeedDirectory);
            EnsureDistinctDirectories(legacyDatDirectory, retailSeedDirectory, outputDirectory);

            onProgress = LegacyConvertProgress.Wrap(onProgress);
            var retailIterations = RetailDatIterations.ReadFromSeed(retailSeedDirectory);
            int resolvedPortalIteration = retailIterations.ResolvePortalIteration(options.PortalIteration);

            onProgress?.Invoke("Auditing legacy DAT catalog...");
            var audit = Audit(legacyDatDirectory, options.IncludePhase3LocalData);
            var conversionWarnings = new List<string>();
            var writtenCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            if (policy.Shell.CharGen == LegacyDatContentSource.Retail
                && policy.Shell.AppearanceTables == LegacyDatContentSource.Dm) {
                conversionWarnings.Add(
                    "CharGen/appearance mix: retail CharGen shell with DM appearance tables can mismatch face and clothing texture slots."
                    + " If character faces, noses, or clothing render incorrectly, prefer the playable Full DM preset"
                    + " with retail appearance tables, or use Full DM Faithful only for experimental testing.");
            }

            if (policy.Shell.CharGen == LegacyDatContentSource.Dm) {
                conversionWarnings.Add(
                    "DM CharGen export is experimental: ACME currently maps legacy heritage refs and starting areas,"
                    + " but does not fully translate the retail CharGen appearance lists used for face/hair/nose validation."
                    + " If characters render with incorrect facial features or starter clothing, prefer the playable Full DM preset.");
            }

            if (LegacyDatMergeRuleResolver.IncludePhase3LocalData(policy) && !audit.HasLanguageDat) {
                throw new InvalidOperationException(
                    "Export policy requests DM UI conversion, but the legacy source directory has no language.dat.");
            }

            Directory.CreateDirectory(outputDirectory);

            onProgress?.Invoke($"Preparing output DAT seed ({DescribePolicy(policy)})...");
            PrepareOutputSeed(retailSeedDirectory, outputDirectory, policy, onProgress);
            onProgress?.Invoke("Patching retail DAT headers for writing...");
            PatchRetailSeedForWriting(outputDirectory);

            using var source = new LegacyDatReader(legacyDatDirectory);
            LegacyDatWorldReferenceClosure? worldClosure = null;
            if (LegacyDatMergeRuleResolver.UsesWorldClosure(policy)) {
                worldClosure = LegacyDatWorldReferenceCollector.CreateSeed(source);
                if (LegacyDatMergeRuleResolver.ShouldApplyLegacyCharGenStartingAreas(policy)) {
                    LegacyDatCharGenMapper.TrackStartingAreasInWorldClosure(source, worldClosure);
                }
            }

            using var writer = new DefaultDatReaderWriter(
                outputDirectory,
                DatAccessType.ReadWrite,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            retailIterations.ApplyToWriter(writer, options.PortalIteration);
            DatExportChecksumReport checksumReport;
            try {
                foreach (var definition in TypeDefinitions) {
                    if (ShouldDeferDefinition(definition, options.IncludePhase3LocalData)) {
                        var deferredIds = definition.EnumerateIds(source);
                        if (deferredIds.Count > 0) {
                            conversionWarnings.Add($"{definition.ObjectType}: skipped {deferredIds.Count} object(s); {definition.Notes}");
                        }
                        continue;
                    }

                    if (!ShouldConvertDefinitionInPrimaryPass(definition)) {
                        continue;
                    }

                    int written = ConvertDefinitionObjects(
                        definition,
                        source,
                        writer,
                        options,
                        policy,
                        worldClosure,
                        conversionWarnings,
                        writtenCounts,
                        onProgress);

                    if (worldClosure != null && definition.ObjectType == "EnvCell") {
                        onProgress?.Invoke("Expanding referenced portal assets...");
                        LegacyDatWorldReferenceCollector.ExpandPending(source, worldClosure, onProgress);
                        onProgress?.Invoke($"World closure selected {worldClosure.TotalPortalObjects:N0} referenced portal object(s).");
                    }
                }

                IEnumerable<uint> renderSurfaceIds = source.GetAllIdsOfType<RenderSurface>();
                if (worldClosure != null) {
                    renderSurfaceIds = renderSurfaceIds.Where(id => worldClosure.Contains("RenderSurface", id));
                }

                onProgress?.Invoke("Building render surface ID map...");
                ActiveRenderSurfaceIdMap.Value = BuildRenderSurfaceIdMap(renderSurfaceIds);

                foreach (var definition in TypeDefinitions) {
                    if (ShouldDeferDefinition(definition, options.IncludePhase3LocalData)) {
                        continue;
                    }

                    if (definition.SourceDat != "portal.dat") {
                        continue;
                    }

                    var ids = definition.EnumerateIds(source);
                    if (ids.Count == 0) {
                        continue;
                    }

                    ids = FilterIdsForExport(policy, worldClosure, definition, ids);
                    if (ids.Count == 0) {
                        continue;
                    }

                    ConvertDefinitionObjects(
                        definition,
                        source,
                        writer,
                        options,
                        policy,
                        worldClosure: null,
                        conversionWarnings,
                        writtenCounts,
                        onProgress,
                        ids);
                }

                if (LegacyDatMergeRuleResolver.ShouldEnsureRegionTerrainPortalAssets(policy)) {
                    int terrainWritten = LegacyDatRegionTerrainPortalExporter.EnsureFromLegacy(
                        source,
                        writer,
                        options.PortalIteration,
                        onProgress);
                    conversionWarnings.Add(
                        $"Region terrain portal pass: refreshed {terrainWritten} SurfaceTexture/RenderSurface write(s) for DM land textures.");
                }

                if (LegacyDatMergeRuleResolver.ShouldApplyLegacyCharGenStartingAreas(policy)) {
                    onProgress?.Invoke("Mapping legacy CharGen starting areas...");
                    LegacyDatCharGenMapper.ApplySlimMergeStartingAreas(
                        source,
                        writer,
                        worldClosure,
                        conversionWarnings);
                }
                else if (LegacyDatMergeRuleResolver.ShouldApplyDmCharGenExport(policy)) {
                    onProgress?.Invoke("Exporting legacy CharGen...");
                    LegacyDatCharGenMapper.ApplyDmCharGenExport(
                        source,
                        writer,
                        retailSeedDirectory,
                        worldClosure,
                        conversionWarnings);
                }

                onProgress?.Invoke("Fixing EnvCell portal transit for retail client...");
                LegacyDatEnvCellExportFixer.FixCellDatabase(
                    writer,
                    source.GetEnvCellIds(),
                    options.PortalIteration,
                    conversionWarnings,
                    onProgress);
                LegacyDatEnvCellExportFixer.EnsureReferencedEnvironmentsPresent(
                    source,
                    writer,
                    source.GetEnvCellIds(),
                    options.PortalIteration,
                    conversionWarnings,
                    onProgress);

                if (LegacyDatMergeRuleResolver.ShouldApplyLegacyCharGenStartingAreas(policy)) {
                    LegacyDatCharGenSpawnCellExporter.EnsureSpawnCells(source, writer, onProgress);
                }

                retailIterations.ApplyToWriter(writer, options.PortalIteration);
                writer.Dispose();

                var checksumTargets = BuildChecksumTargets(source, policy, worldClosure, options.IncludePhase3LocalData);
                MaybeReportCheckpointValidation(outputDirectory, checksumTargets, "post-convert-pre-prune", onProgress);
                if (LegacyDatExportModePolicy.ShouldPruneRetailCellWorld(policy)) {
                    LegacyDatCellWorldPruner.Prune(outputDirectory, source, onProgress);
                    if (LegacyDatMergeRuleResolver.ShouldApplyLegacyCharGenStartingAreas(policy)) {
                        using var spawnWriter = new DefaultDatReaderWriter(
                            outputDirectory,
                            DatAccessType.ReadWrite,
                            FileCachingStrategy.Never,
                            IndexCachingStrategy.OnDemand);
                        LegacyDatCharGenSpawnCellExporter.EnsureSpawnCells(source, spawnWriter, onProgress);
                        spawnWriter.Dispose();
                    }
                }

                if (LegacyDatMergeRuleResolver.ShouldMergeRetailPortalGlobals(policy)) {
                    LegacyDatPortalBootstrap.MergeRetailGlobals(
                        outputDirectory,
                        retailSeedDirectory,
                        checksumTargets,
                        resolvedPortalIteration,
                        policy,
                        onProgress);
                    LegacyDatConversionBisect.SnapshotPortal(outputDirectory, "post-merge-retail-globals", onProgress);
                    if (LegacyDatMergeRuleResolver.ShouldPruneSlimMergePortal(policy)) {
                        onProgress?.Invoke("Redeploying slim-merge portal catalog (retail seed + converted overlays)...");
                        LegacyDatPortalOverlayRedeploy.Redeploy(
                            outputDirectory,
                            retailSeedDirectory,
                            policy,
                            onProgress);
                        LegacyDatConversionBisect.SnapshotPortal(outputDirectory, "post-slim-redeploy", onProgress);
                    }

                    if (LegacyDatMergeRuleResolver.ShouldApplyLegacyCharGenStartingAreas(policy)) {
                        LegacyDatSlimMergeCharGenRestore.RestoreAfterPortalPrune(
                            legacyDatDirectory,
                            outputDirectory,
                            retailSeedDirectory,
                            worldClosure,
                            conversionWarnings,
                            onProgress);
                        LegacyDatConversionBisect.SnapshotPortal(outputDirectory, "post-chargen-restore", onProgress);
                    }
                }
                else if (LegacyDatExportModePolicy.ShouldPrunePortalCatalog(policy)) {
                    var portalKeepIds = policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog
                        ? LegacyDatFullDmPortalKeeper.CollectKeepIds(
                            outputDirectory,
                            retailSeedDirectory,
                            checksumTargets,
                            policy)
                        : new HashSet<uint>(checksumTargets.PortalFileIds);

                    // In-place delete only — do not clear the retail portal shell (that rebuilds the
                    // B-tree and breaks client init). Overlay redeploy restores the seed root afterward.
                    LegacyDatPortalCatalogPruner.PrunePortal(
                        outputDirectory,
                        portalKeepIds,
                        retailSeedDirectory: null,
                        onProgress);

                    if (LegacyDatMergeRuleResolver.UsesRetailPortalOverlayDeploy(policy)) {
                        onProgress?.Invoke(
                            "Redeploying client_portal.dat onto retail seed (preserve B-tree root per retail_client_dat_spec)...");
                        LegacyDatPortalOverlayRedeploy.Redeploy(
                            outputDirectory,
                            retailSeedDirectory,
                            policy,
                            onProgress);
                        LegacyDatConversionBisect.SnapshotPortal(outputDirectory, "post-fulldm-portal-redeploy", onProgress);

                        if (policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog) {
                            LegacyDatFullDmPortalKeeper.GapFillMissingShellGlobals(
                                outputDirectory,
                                retailSeedDirectory,
                                policy,
                                resolvedPortalIteration,
                                onProgress);

                            if (LegacyDatMergeRuleResolver.ShouldApplyDmCharGenExport(policy)
                                || LegacyDatMergeRuleResolver.ShouldPatchDmStartingAreas(policy)) {
                                LegacyDatFullDmPortalKeeper.RestoreDmCharGenTable(
                                    legacyDatDirectory,
                                    outputDirectory,
                                    retailSeedDirectory,
                                    policy,
                                    onProgress);
                            }
                        }
                    }
                }

                // DM-only world must ship the DM Region (its terrain descriptors / tiling / DM-specific
                // terrain types), not the retail seed Region the overlay redeploy leaves behind. Write it
                // last so nothing downstream can restore retail. (Retail-world modes keep the retail Region.)
                if (policy.World == LegacyDatWorldSource.DmOnly) {
                    LegacyDatRegionTerrainPortalExporter.EnsureDmRegionWins(
                        source,
                        outputDirectory,
                        resolvedPortalIteration,
                        onProgress);
                    LegacyDatRegionTerrainPortalExporter.EnsureDmWorldPortalObjectsWin(
                        source,
                        outputDirectory,
                        resolvedPortalIteration,
                        onProgress);
                    LegacyDatRegionTerrainPortalExporter.EnsureDmSharedOutdoorTextureSlotsWin(
                        source,
                        retailSeedDirectory,
                        outputDirectory,
                        resolvedPortalIteration,
                        onProgress);
                    LegacyDatRegionTerrainPortalExporter.EnsureDmWorldReferencedMaterialsWin(
                        source,
                        retailSeedDirectory,
                        outputDirectory,
                        resolvedPortalIteration,
                        onProgress);

                    // Bulk DM Setup/GfxObj write can clobber retail shell dependencies at shared IDs.
                    // Re-run shell gap-fill so ConnectionLoginUiShell assets win for the current retail
                    // connect / patch / char-select shell path.
                    if (policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog
                        && LegacyDatMergeRuleResolver.UsesRetailPortalOverlayDeploy(policy)) {
                        LegacyDatFullDmPortalKeeper.GapFillMissingShellGlobals(
                            outputDirectory,
                            retailSeedDirectory,
                            policy,
                            resolvedPortalIteration,
                            onProgress);
                    }

                    LegacyDatConversionBisect.SnapshotPortal(outputDirectory, "post-dm-region-write", onProgress);

                    using (var envWriter = new DefaultDatReaderWriter(
                            outputDirectory,
                            DatAccessType.ReadWrite,
                            FileCachingStrategy.Never,
                            IndexCachingStrategy.OnDemand)) {
                        var envCellIds = source.GetEnvCellIds();
                        int envPortalIteration = envWriter.GetIteration(DatArchive.Portal);
                        LegacyDatEnvCellExportFixer.EnsureReferencedEnvironmentsPresent(
                            source,
                            envWriter,
                            envCellIds,
                            envPortalIteration,
                            conversionWarnings,
                            onProgress);
                        LegacyDatEnvCellExportFixer.ReassertReferencedEnvironmentsFromLegacy(
                            source,
                            envWriter,
                            envCellIds,
                            envPortalIteration,
                            conversionWarnings,
                            onProgress);
                        LegacyDatEnvCellExportFixer.FixCellDatabase(
                            envWriter,
                            envCellIds,
                            envPortalIteration,
                            conversionWarnings,
                            onProgress);
                    }
                }

                MaybeReportCheckpointValidation(outputDirectory, checksumTargets, "post-prune", onProgress);

                onProgress?.Invoke("Finalizing DAT files for client compatibility...");
                LegacyDatClientExportFixer.FinalizeForClient(
                    retailSeedDirectory,
                    outputDirectory,
                    retailIterations,
                    options.PortalIteration,
                    onProgress,
                    policy);

                MaybeReportCheckpointValidation(outputDirectory, checksumTargets, "post-finalize-for-client", onProgress);

                bool shouldPatchDmStrings = LegacyDatMergeRuleResolver.ShouldApplyDmPortalStringPatches(policy);
                bool shouldPatchConnectionStrings = LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(policy);
                bool shouldPatchCharGenLayout = LegacyDatMergeRuleResolver.ShouldPatchCharGenLayoutShape(policy);
                bool shouldPatchInGameLayout = LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(policy);
                if (shouldPatchDmStrings || shouldPatchConnectionStrings || shouldPatchCharGenLayout || shouldPatchInGameLayout) {
                    string stringPatchReportPath = Path.Combine(outputDirectory, "dm_ui_string_patch_report.txt");
                    string layoutPatchReportPath = Path.Combine(outputDirectory, "dm_ui_layout_patch_report.txt");
                    using var localWriter = new DefaultDatReaderWriter(
                        outputDirectory,
                        DatAccessType.ReadWrite,
                        FileCachingStrategy.Never,
                        IndexCachingStrategy.OnDemand);
                    retailIterations.ApplyToWriter(localWriter, options.PortalIteration);

                    if (shouldPatchDmStrings) {
                        onProgress?.Invoke("Patching retail StringTable with DM char-create text...");
                        var patchReport = LegacyDatDmStringTableExporter.ApplyCharGenPortalStrings(
                            source,
                            localWriter,
                            conversionWarnings,
                            stringPatchReportPath);
                        conversionWarnings.Add(
                            $"DM UI strings: patched {patchReport.Patched} char-create StringTable row(s); see dm_ui_string_patch_report.txt.");
                    }

                    if (shouldPatchConnectionStrings) {
                        onProgress?.Invoke("Patching connection / login screen text...");
                        var connectionReport = LegacyDatDmStringTableExporter.ApplyConnectionScreenStrings(
                            localWriter,
                            conversionWarnings,
                            stringPatchReportPath);
                        conversionWarnings.Add(
                            $"DM connection UI: patched {connectionReport.Patched} StringTable row(s).");

                        onProgress?.Invoke("Patching additional pre-game retail shell text...");
                        var preGameReport = LegacyDatDmStringTableExporter.ApplyScreenStrings(
                            localWriter,
                            conversionWarnings,
                            stringPatchReportPath,
                            LegacyDatDmStringTableMapNames.PreGameExe);
                        conversionWarnings.Add(
                            $"DM pre-game UI: patched {preGameReport.Patched} StringTable row(s).");
                    }

                    bool shouldPatchInGameStrings = LegacyDatMergeRuleResolver.ShouldPatchDmInGameStrings(options.ResolvedPolicy);
                    if (shouldPatchInGameStrings) {
                        onProgress?.Invoke("Patching in-game panel text...");
                        var inGameReport = LegacyDatDmStringTableExporter.ApplyInGameScreenStrings(
                            localWriter,
                            conversionWarnings,
                            stringPatchReportPath);
                        conversionWarnings.Add(
                            $"DM in-game UI: patched {inGameReport.Patched} StringTable row(s).");
                    }

                    if (shouldPatchCharGenLayout) {
                        onProgress?.Invoke("Evaluating DM char-create layout spec...");
                        var layoutReport = LegacyDatDmLayoutExporter.ApplyLayoutSpec(
                            localWriter,
                            conversionWarnings,
                            layoutPatchReportPath,
                            LegacyDatDmLayoutSpecNames.CharGenWizard);
                        conversionWarnings.Add(
                            $"DM char-create layout: validated {layoutReport.ValidatedLayouts} layout(s), applied {layoutReport.PatchedElements} element edit(s), blocked {layoutReport.BlockedFeatures} feature(s); see dm_ui_layout_patch_report.txt.");
                    }

                    if (shouldPatchInGameLayout) {
                        onProgress?.Invoke("Evaluating DM in-game layout spec...");
                        var layoutReport = LegacyDatDmLayoutExporter.ApplyLayoutSpec(
                            localWriter,
                            conversionWarnings,
                            layoutPatchReportPath,
                            LegacyDatDmLayoutSpecNames.InGamePanels,
                            appendReport: shouldPatchCharGenLayout);
                        conversionWarnings.Add(
                            $"DM in-game layout: validated {layoutReport.ValidatedLayouts} layout(s), applied {layoutReport.PatchedElements} element edit(s), blocked {layoutReport.BlockedFeatures} feature(s); see dm_ui_layout_patch_report.txt.");
                    }
                }

                if (shouldPatchDmStrings || shouldPatchConnectionStrings || shouldPatchCharGenLayout || shouldPatchInGameLayout) {
                    // After localWriter.Dispose(): Chorizite leaves a short empty journal; reset header before final export.
                    onProgress?.Invoke("Preparing patched client_local_English.dat header for client init...");
                    LegacyDatClientExportFixer.PatchCatalogFilesForWriting(
                        outputDirectory,
                        "client_local_English.dat");
                }

                LegacyDatConversionBisect.SnapshotPortal(outputDirectory, "post-string-patch", onProgress);

                if (!string.IsNullOrWhiteSpace(options.ServerWorldDataDirectory)
                    && LegacyDatMergeRuleResolver.UsesLegacyOnlyCellWorld(policy)) {
                    onProgress?.Invoke("Completing world data...");
                    var completion = LegacyDatServerWorldCompletion.Complete(
                        options.ServerWorldDataDirectory,
                        outputDirectory,
                        onProgress);
                    if (completion.TotalAdded > 0 || completion.DecodeFailures > 0 || completion.WriteFailures > 0) {
                        conversionWarnings.Add(
                            $"World completion: imported {completion.EnvCellsAdded} EnvCell(s), "
                            + $"{completion.LandBlockInfosAdded} LandBlockInfo(s), {completion.LandBlocksAdded} LandBlock(s) "
                            + $"from extra world data; {completion.DecodeFailures} decode / {completion.WriteFailures} write failure(s).");
                    }
                }

                // Guarantee the graphics dependency closure (Surface/SurfaceTexture/RenderSurface/
                // Palette/GfxObj/Setup) for every shipped cell static + CharGen heritage. The
                // DM-client world closure misses surfaces referenced only by server-imported statics
                // or faithful CharGen GfxObjs; a dangling ref is a runtime null surface and a client
                // AV in RenderDeviceD3D::DrawEnvCell. Must run after world completion + CharGen.
                if (LegacyDatMergeRuleResolver.UsesLegacyOnlyCellWorld(policy)
                    && !string.IsNullOrWhiteSpace(legacyDatDirectory)) {
                    onProgress?.Invoke("Completing render dependency closure for shipped cells + CharGen...");
                    var closure = LegacyDatRenderClosureCompletion.Complete(
                        legacyDatDirectory,
                        outputDirectory,
                        onProgress);
                    if (closure.TotalAdded > 0 || closure.Unresolved > 0) {
                        conversionWarnings.Add(
                            $"Render closure: added {closure.SurfacesAdded} Surface, {closure.SurfaceTexturesAdded} SurfaceTexture, "
                            + $"{closure.RenderSurfacesAdded} RenderSurface, {closure.PalettesAdded} Palette, "
                            + $"{closure.GfxObjsAdded} GfxObj, {closure.SetupsAdded} Setup; {closure.Unresolved} unresolved.");
                    }
                }

                // Retail terrain compositor only blends PFID_CUSTOM_LSCAPE_ALPHA masks; DM ships them as
                // A8R8G8B8 -> terrain transitions/roads checkerboard. Re-encode them in place.
                onProgress?.Invoke("Re-encoding terrain blend maps to retail LSCAPE_ALPHA format...");
                var terrainAlpha = LegacyDatTerrainAlphaCompletion.Complete(outputDirectory, onProgress);
                if (terrainAlpha.Converted > 0 || terrainAlpha.Missing > 0) {
                    conversionWarnings.Add(
                        $"Terrain blend maps: re-encoded {terrainAlpha.Converted} to LSCAPE_ALPHA "
                        + $"({terrainAlpha.AlreadyOk} already ok, {terrainAlpha.Missing} missing).");
                }

                // Some DM static/effect objects (e.g. lifestone) ship small INDEX16 textures whose retail
                // INDEX16+palette combine fails -> they render dark. Bake those to A8R8G8B8 (DM colours kept).
                onProgress?.Invoke("Baking combine-failing static object textures to A8R8G8B8...");
                var objTex = LegacyDatObjectTextureCombineFixer.Complete(outputDirectory, onProgress);
                if (objTex.Converted > 0) {
                    conversionWarnings.Add($"Object textures: baked {objTex.Converted} INDEX16 -> A8R8G8B8 (static/effect combine fix).");
                }

                onProgress?.Invoke("Completing client DAT export (leaf sentinels, headers, local)...");
                LegacyDatClientExportFixer.CompleteClientExport(
                    outputDirectory,
                    policy,
                    onProgress,
                    retailSeedDirectory);

                MaybeReportCheckpointValidation(outputDirectory, checksumTargets, "post-complete-client-export", onProgress);

                onProgress?.Invoke("Final portal metadata + client DAT repair pass...");
                LegacyDatClientDatRepair.Repair(
                    outputDirectory,
                    retailSeedDirectory,
                    policy,
                    legacyDatDirectory,
                    onProgress);

                MaybeReportCheckpointValidation(outputDirectory, checksumTargets, "post-client-dat-repair", onProgress);

                onProgress?.Invoke("Validating export checksums...");
                if (LegacyDatMergeRuleResolver.UsesRetailPortalOverlayDeploy(policy)) {
                    int droppedPortalChecksumTargets = DatExportChecksumValidator
                        .AlignPortalChecksumTargetsWithExport(outputDirectory, checksumTargets);
                    if (droppedPortalChecksumTargets > 0) {
                        conversionWarnings.Add(
                            $"Checksum: {droppedPortalChecksumTargets} legacy portal id(s) are not in the final overlay catalog "
                            + "(expected for OverlayOnRetailSeed — retail seed entries or pruned refs; not export failures).");
                        onProgress?.Invoke(
                            $"Aligned portal checksum targets with final catalog ({droppedPortalChecksumTargets:N0} legacy-only id(s) dropped from tracking).");
                    }
                }

                checksumReport = DatExportChecksumValidator.ValidateExportDirectory(outputDirectory, checksumTargets, onProgress);

                onProgress?.Invoke("Validating retail client load requirements...");
                var retailReport = RetailClientDatExportValidator.Validate(
                    outputDirectory,
                    new RetailClientDatExportValidator.Options {
                        ExportPolicy = policy,
                        RequireOverlayPortalRoot = LegacyDatMergeRuleResolver.ShouldRequireOverlayPortalRoot(policy),
                    });

                bool portalCatalogWasPruned = LegacyDatMergeRuleResolver.ShouldPrunePortalCatalog(policy)
                    || LegacyDatMergeRuleResolver.ShouldPruneSlimMergePortal(policy);
                bool checksumBlocksExport = portalCatalogWasPruned
                    ? checksumReport.TrackedCellMissing > 0
                    : checksumReport.HasTrackedMissingEntries;

                if (checksumBlocksExport || !retailReport.IsValid) {
                    var failure = new StringBuilder();
                    failure.AppendLine("Export failed client/server DAT validation. Do not deploy this folder.");
                    if (checksumReport.TrackedPortalMissing > 0 && !portalCatalogWasPruned) {
                        failure.AppendLine(
                            $"client_portal.dat: {checksumReport.TrackedPortalMissing} converted object(s) missing from the catalog (often caused by truncating the portal file).");
                    }

                    if (checksumReport.TrackedCellMissing > 0) {
                        failure.AppendLine(
                            $"client_cell_1.dat: {checksumReport.TrackedCellMissing} converted object(s) missing from the catalog.");
                    }

                    foreach (string error in retailReport.Errors) {
                        failure.AppendLine(error);
                    }

                    throw new InvalidOperationException(failure.ToString().TrimEnd());
                }
            }
            finally {
                ActiveRenderSurfaceIdMap.Value = null;
            }

            onProgress?.Invoke("Writing conversion manifest...");
            string manifestPath = Path.Combine(outputDirectory, "worldbuilder_legacy_conversion_manifest.txt");
            File.WriteAllText(
                manifestPath,
                BuildManifest(audit, writtenCounts, checksumReport, conversionWarnings, options, policy, outputDirectory));

            return new LegacyToRetailConversionResult {
                Audit = MergeAuditWriteCounts(audit, writtenCounts),
                OutputDirectory = outputDirectory,
                ManifestPath = manifestPath,
                ChecksumReport = checksumReport,
                WrittenCounts = writtenCounts,
                ConversionWarnings = conversionWarnings,
            };
        }

        private static string DescribePolicy(LegacyDatExportPolicy policy) =>
            policy.GetDisplayName();

        private static void MaybeReportCheckpointValidation(
            string outputDirectory,
            DatExportChecksumTargets checksumTargets,
            string stageName,
            Action<string>? onProgress) {
            if (!string.Equals(
                System.Environment.GetEnvironmentVariable("ACME_LEGACY_CONVERT_CHECKPOINTS"),
                "1",
                StringComparison.Ordinal)) {
                return;
            }

            var report = DatExportChecksumValidator.ValidateExportDirectory(outputDirectory, checksumTargets);
            onProgress?.Invoke(
                $"Checkpoint {stageName}: portal missing={report.TrackedPortalMissing}, cell missing={report.TrackedCellMissing}, warnings={report.WarningCount}.");
            LegacyDatConversionBisect.SnapshotPortal(outputDirectory, stageName, onProgress);
        }

        private static LegacyDatConversionAudit MergeAuditWriteCounts(
            LegacyDatConversionAudit audit,
            IReadOnlyDictionary<string, int> writtenCounts) {
            var mergedTypes = audit.Types
                .Select(type => new LegacyDatConversionTypeAudit {
                    ObjectType = type.ObjectType,
                    SourceDat = type.SourceDat,
                    Phase = type.Phase,
                    Notes = type.Notes,
                    SourceCount = type.SourceCount,
                    ConvertibleCount = type.ConvertibleCount,
                    WrittenCount = writtenCounts.TryGetValue(type.ObjectType, out var written) ? written : 0,
                    SyntheticCount = type.SyntheticCount,
                    FallbackCount = type.FallbackCount,
                    DeferredCount = type.DeferredCount,
                    UnsupportedCount = type.UnsupportedCount,
                })
                .ToArray();

            return new LegacyDatConversionAudit {
                LegacyDatDirectory = audit.LegacyDatDirectory,
                SourceVersion = audit.SourceVersion,
                SourceIteration = audit.SourceIteration,
                HasLanguageDat = audit.HasLanguageDat,
                Types = mergedTypes,
                Findings = audit.Findings,
            };
        }

        internal static IReadOnlyList<uint> FilterIdsForExport(
            LegacyDatExportPolicy policy,
            LegacyDatWorldReferenceClosure? worldClosure,
            LegacyDatConversionTypeDefinition definition,
            IReadOnlyList<uint> ids) {
            if (policy.Portal == LegacyDatPortalCatalogPolicy.FullRetailPlusDmOverlay
                || policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog
                || definition.SourceDat == "cell.dat") {
                return ids;
            }

            if (worldClosure == null) {
                return Array.Empty<uint>();
            }

            if (LegacyDatMergeRuleResolver.ShouldExportAllLegacyAppearanceTables(definition.ObjectType, policy)) {
                return ids;
            }

            return ids.Where(id => worldClosure.Contains(definition.ObjectType, id)).ToArray();
        }

        internal static IReadOnlyList<uint> FilterIdsForExport(
            LegacyDatExportMode exportMode,
            LegacyDatWorldReferenceClosure? worldClosure,
            LegacyDatConversionTypeDefinition definition,
            IReadOnlyList<uint> ids) =>
            FilterIdsForExport(LegacyDatExportPolicy.FromLegacyMode(exportMode), worldClosure, definition, ids);

        private static int ConvertDefinitionObjects(
            LegacyDatConversionTypeDefinition definition,
            LegacyDatReader source,
            DefaultDatReaderWriter writer,
            LegacyToRetailConversionOptions options,
            LegacyDatExportPolicy policy,
            LegacyDatWorldReferenceClosure? worldClosure,
            List<string> conversionWarnings,
            Dictionary<string, int> writtenCounts,
            Action<string>? onProgress,
            IReadOnlyList<uint>? idsOverride = null) {
            var ids = idsOverride ?? definition.EnumerateIds(source);
            if (ids.Count == 0) {
                return 0;
            }

            onProgress?.Invoke($"Converting {definition.ObjectType} ({ids.Count} object(s))...");
            int written = 0;
            int processed = 0;
            foreach (uint id in ids) {
                if (++processed % 500 == 0) {
                    onProgress?.Invoke($"Converting {definition.ObjectType} {processed}/{ids.Count}...");
                }

                if (!TryConvertDefinitionObject(definition, source, writer, id, options.PortalIteration, worldClosure)) {
                    conversionWarnings.Add($"{definition.ObjectType}: failed to convert 0x{id:X8}.");
                    continue;
                }

                written++;
            }

            writtenCounts[definition.ObjectType] = written;
            return written;
        }

        internal static bool ShouldConvertDefinitionInPrimaryPass(LegacyDatConversionTypeDefinition definition) {
            ArgumentNullException.ThrowIfNull(definition);
            // Portal objects must wait until the synthetic RenderSurface remap table exists.
            return definition.SourceDat != "portal.dat";
        }

        private static bool TryConvertDefinitionObject(
            LegacyDatConversionTypeDefinition definition,
            LegacyDatReader source,
            DefaultDatReaderWriter writer,
            uint id,
            int? iteration,
            LegacyDatWorldReferenceClosure? worldClosure) {
            if (worldClosure != null && definition.ObjectType == "LandBlockInfo") {
                if (!source.TryGet<LandBlockInfo>(id, out var landBlockInfo) || landBlockInfo == null) {
                    return false;
                }

                LegacyDatWorldReferenceCollector.TrackLandBlockInfo(worldClosure, landBlockInfo);
                return writer.TrySave(landBlockInfo, iteration);
            }

            if (worldClosure != null && definition.ObjectType == "EnvCell") {
                if (!source.TryGet<EnvCell>(id, out var envCell) || envCell == null) {
                    return false;
                }

                LegacyDatWorldReferenceCollector.TrackEnvCell(worldClosure, envCell);
                return writer.TrySave(envCell, iteration);
            }

            return definition.Copy(source, writer, id, iteration);
        }

        private static DatExportChecksumTargets BuildChecksumTargets(
            LegacyDatReader source,
            LegacyDatExportPolicy policy,
            LegacyDatWorldReferenceClosure? worldClosure,
            bool includePhase3LocalData) {
            var targets = new DatExportChecksumTargets();
            foreach (uint id in source.GetAllIdsOfType<LandBlock>())
                targets.TrackCell(id);
            foreach (uint id in source.GetAllIdsOfType<LandBlockInfo>())
                targets.TrackCell(id);
            foreach (uint id in source.GetAllIdsOfType<EnvCell>())
                targets.TrackCell(id);

            TrackPortalType<Setup>(source, targets, policy, worldClosure);
            TrackPortalType<GfxObj>(source, targets, policy, worldClosure);
            TrackPortalType<ParticleEmitter>(source, targets, policy, worldClosure);
            TrackPortalType<Scene>(source, targets, policy, worldClosure);
            TrackPortalType<Surface>(source, targets, policy, worldClosure);
            TrackPortalType<Palette>(source, targets, policy, worldClosure);
            TrackPortalType<PalSet>(source, targets, policy, worldClosure);
            TrackPortalType<ClothingTable>(source, targets, policy, worldClosure);
            TrackPortalType<RenderTexture>(source, targets, policy, worldClosure);
            foreach (uint id in source.GetAllIdsOfType<RenderSurface>()) {
                if (LegacyDatMergeRuleResolver.UsesWorldClosure(policy)
                    && (worldClosure == null || !worldClosure.Contains("RenderSurface", id))) {
                    continue;
                }

                targets.TrackPortal(ConvertRenderSurfaceIdToRetail(id));
            }
            TrackPortalType<SurfaceTexture>(source, targets, policy, worldClosure);
            TrackPortalType<Region>(source, targets, policy, worldClosure);
            TrackPortalType<Acme.Dat.Environment>(source, targets, policy, worldClosure);

            if (includePhase3LocalData) {
                // Local DAT checksum validation is not yet wired; objects are still converted and written.
            }

            return targets;
        }

        internal static IReadOnlyDictionary<uint, uint> BuildRenderSurfaceIdMap(IEnumerable<uint> renderSurfaceIds) {
            ArgumentNullException.ThrowIfNull(renderSurfaceIds);

            var sourceIds = renderSurfaceIds.ToList();
            var map = new Dictionary<uint, uint>();
            var assignedTargets = new HashSet<uint>(
                sourceIds.Where(id => !LegacyDatReader.IsSyntheticRenderSurfaceId(id) && (id >> 24) == 0x06));

            uint nextCandidate = 0x0600FF00u;
            foreach (uint syntheticId in sourceIds
                .Where(LegacyDatReader.IsSyntheticRenderSurfaceId)
                .Distinct()
                .OrderBy(id => id)) {
                uint preferred = 0x06000000u | (syntheticId & 0x00FFFFFFu);
                uint assigned = preferred;
                if (assignedTargets.Contains(assigned)) {
                    assigned = NextAvailableRetailRenderSurfaceId(assignedTargets, nextCandidate);
                    nextCandidate = assigned + 1;
                }

                assignedTargets.Add(assigned);
                map[syntheticId] = assigned;
            }

            return map;
        }

        private static uint NextAvailableRetailRenderSurfaceId(HashSet<uint> assignedTargets, uint startCandidate) {
            uint candidate = startCandidate < 0x06000000u ? 0x0600FF00u : startCandidate;
            while (true) {
                if ((candidate >> 24) != 0x06) {
                    candidate = 0x0600FF00u;
                }

                if (!assignedTargets.Contains(candidate)) {
                    return candidate;
                }

                if ((candidate & 0x00FFFFFFu) == 0x00FFFFFFu) {
                    throw new InvalidOperationException("Unable to allocate a free retail RenderSurface ID for synthetic legacy textures.");
                }

                candidate++;
            }
        }

        private static void TrackPortalType<T>(
            LegacyDatReader source,
            DatExportChecksumTargets targets,
            LegacyDatExportPolicy policy,
            LegacyDatWorldReferenceClosure? worldClosure)
            where T : class, Acme.Dat.IDatRecord, new() {
            string objectType = typeof(T).Name switch {
                nameof(Acme.Dat.Environment) => "Environment",
                _ => typeof(T).Name,
            };

            foreach (uint id in source.GetAllIdsOfType<T>()) {
                if (LegacyDatMergeRuleResolver.UsesWorldClosure(policy)
                    && !LegacyDatMergeRuleResolver.ShouldExportAllLegacyAppearanceTables(objectType, policy)
                    && (worldClosure == null || !worldClosure.Contains(objectType, id))) {
                    continue;
                }

                targets.TrackPortal(id);
            }
        }

        private static void SummarizeClassification(
            LegacyDatConversionTypeDefinition definition,
            LegacyDatConversionAuditContext context,
            IReadOnlyList<uint> ids,
            LegacyDatConversionTypeAudit audit,
            List<LegacyDatConversionFinding> findings) {
            var syntheticExamples = new List<uint>();
            var fallbackExamples = new List<uint>();
            var unsupportedExamples = new List<uint>();
            string? supportedInfoMessage = null;

            foreach (uint id in ids) {
                var classification = definition.Classify(context, id);
                switch (classification.Status) {
                    case LegacyDatConversionStatus.Supported:
                        audit.ConvertibleCount++;
                        if (!string.IsNullOrWhiteSpace(classification.Message)) {
                            supportedInfoMessage ??= classification.Message;
                        }
                        break;
                    case LegacyDatConversionStatus.Synthetic:
                        audit.ConvertibleCount++;
                        audit.SyntheticCount++;
                        AddExample(syntheticExamples, id);
                        break;
                    case LegacyDatConversionStatus.Fallback:
                        audit.ConvertibleCount++;
                        audit.FallbackCount++;
                        AddExample(fallbackExamples, id);
                        break;
                    case LegacyDatConversionStatus.Deferred:
                        audit.DeferredCount++;
                        break;
                    case LegacyDatConversionStatus.Unsupported:
                        audit.UnsupportedCount++;
                        AddExample(unsupportedExamples, id);
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(supportedInfoMessage)) {
                findings.Add(new LegacyDatConversionFinding {
                    ObjectType = definition.ObjectType,
                    Severity = LegacyDatConversionSeverity.Info,
                    Message = $"{definition.ObjectType}: {supportedInfoMessage}",
                });
            }

            AddSummaryFinding(
                findings,
                definition.ObjectType,
                LegacyDatConversionSeverity.Warning,
                audit.SyntheticCount,
                "synthetic retail object(s) will be generated",
                syntheticExamples);
            AddSummaryFinding(
                findings,
                definition.ObjectType,
                LegacyDatConversionSeverity.Warning,
                audit.FallbackCount,
                "fallback object(s) will be written because the legacy source cannot be decoded faithfully",
                fallbackExamples);
            AddSummaryFinding(
                findings,
                definition.ObjectType,
                LegacyDatConversionSeverity.Error,
                audit.UnsupportedCount,
                "object(s) could not be decoded from the legacy source and will be skipped",
                unsupportedExamples);
        }

        private static void AddSummaryFinding(
            List<LegacyDatConversionFinding> findings,
            string objectType,
            LegacyDatConversionSeverity severity,
            int count,
            string message,
            IReadOnlyList<uint> examples) {
            if (count <= 0) {
                return;
            }

            string suffix = examples.Count > 0
                ? $" Examples: {string.Join(", ", examples.Select(id => $"0x{id:X8}"))}."
                : string.Empty;

            findings.Add(new LegacyDatConversionFinding {
                ObjectType = objectType,
                Severity = severity,
                Message = $"{objectType}: {count} {message}.{suffix}",
            });
        }

        private static void AddExample(List<uint> examples, uint id) {
            if (examples.Count < 3) {
                examples.Add(id);
            }
        }

        private static LegacyDatConversionClassification ClassifyRegion(LegacyDatConversionAuditContext context, uint id) {
            if (!context.Source.TryGet<Region>(id, out _)) {
                return new LegacyDatConversionClassification(
                    LegacyDatConversionStatus.Unsupported,
                    $"Region 0x{id:X8} could not be decoded from the legacy portal DAT.");
            }

            uint actualId = LegacyDatDecoders.ResolveRegionFileId(id);
            if (!context.PortalDatabase.TryReadFileBytes(actualId, out var regionBytes)) {
                return new LegacyDatConversionClassification(
                    LegacyDatConversionStatus.Fallback,
                    $"Region 0x{id:X8} is missing from the legacy portal DAT; a fallback retail region will be written.");
            }

            if (context.Version == LegacyDatVersion.DarkMajesty
                && !LegacyDatDecoders.TryDecodeRegion(regionBytes, context.Version, context.PreferredTerrainTextureId, out _)) {
                return new LegacyDatConversionClassification(
                    LegacyDatConversionStatus.Fallback,
                    $"Region 0x{id:X8} could not be decoded cleanly; conversion will fall back to a synthetic retail-safe region.");
            }

            if (id != actualId) {
                return new LegacyDatConversionClassification(
                    LegacyDatConversionStatus.Supported,
                    "Legacy region ID 0x130F0000 will be exported under the retail alias ID 0x13000000.");
            }

            return new LegacyDatConversionClassification(LegacyDatConversionStatus.Supported);
        }

        private static LegacyDatConversionTypeDefinition CreateType<T>(
            string objectType,
            string sourceDat,
            LegacyDatConversionPhase phase,
            string notes,
            Func<LegacyDatConversionAuditContext, uint, LegacyDatConversionClassification>? classify = null,
            Func<LegacyDatReader, DefaultDatReaderWriter, uint, int?, bool>? copy = null)
            where T : class, Acme.Dat.IDatRecord, new() {
            return new LegacyDatConversionTypeDefinition(
                objectType,
                sourceDat,
                phase,
                notes,
                EnabledForConversion: true,
                source => source.GetAllIdsOfType<T>().Distinct().OrderBy(id => id).ToArray(),
                classify ?? ((ctx, id) => DefaultClassification<T>(ctx, id)),
                copy ?? ((source, writer, id, iteration) => TryCopy<T>(source, writer, id, iteration)));
        }

        private static LegacyDatConversionTypeDefinition CreateDeferredType<T>(
            string objectType,
            string sourceDat,
            LegacyDatConversionPhase phase,
            string notes)
            where T : class, Acme.Dat.IDatRecord, new() {
            return new LegacyDatConversionTypeDefinition(
                objectType,
                sourceDat,
                phase,
                notes,
                EnabledForConversion: false,
                source => source.GetAllIdsOfType<T>().Distinct().OrderBy(id => id).ToArray(),
                (_, _) => new LegacyDatConversionClassification(LegacyDatConversionStatus.Deferred),
                (_, _, _, _) => false);
        }

        private static LegacyDatConversionClassification DefaultClassification<T>(
            LegacyDatConversionAuditContext context,
            uint id)
            where T : class, Acme.Dat.IDatRecord, new() {
            return context.Source.TryGet<T>(id, out _)
                ? new LegacyDatConversionClassification(LegacyDatConversionStatus.Supported)
                : new LegacyDatConversionClassification(
                    LegacyDatConversionStatus.Unsupported,
                    $"{typeof(T).Name} 0x{id:X8} could not be decoded from the legacy source.");
        }

        private static bool TryCopy<T>(
            LegacyDatReader source,
            DefaultDatReaderWriter writer,
            uint id,
            int? iteration)
            where T : class, Acme.Dat.IDatRecord, new() {
            if (!source.TryGet<T>(id, out var file) || file == null) {
                return false;
            }

            // TrySave replaces an existing catalog id in place (DatReaderWriter updates the B-tree
            // entry and reuses the payload block chain), so converted DM objects at shared retail IDs
            // overwrite the seed payload directly. Never delete-before-write: Tree.TryDelete corrupts
            // B-tree ordering at scale (see LegacyDatCatalogEntryReplacer).
            return writer.TrySave(file, iteration);
        }

        internal static bool TryCopyRenderSurfaceForExport(
            LegacyDatReader source,
            DefaultDatReaderWriter writer,
            uint id,
            int? iteration) =>
            CopyRenderSurface(source, writer, id, iteration);

        private static bool CopyRenderSurface(
            LegacyDatReader source,
            DefaultDatReaderWriter writer,
            uint id,
            int? iteration) {
            if (!source.TryGet<RenderSurface>(id, out var renderSurface) || renderSurface == null) {
                return false;
            }

            RemapRenderSurfaceForRetail(renderSurface);
            if (writer.TryGet<RenderSurface>(renderSurface.Id, out var existing) && existing != null) {
                renderSurface = OverlayRenderSurfaceOntoRetail(existing, renderSurface);
            }

            return writer.TrySave(renderSurface, iteration);
        }

        internal static RenderSurface OverlayRenderSurfaceOntoRetail(RenderSurface existing, RenderSurface converted) {
            ArgumentNullException.ThrowIfNull(existing);
            ArgumentNullException.ThrowIfNull(converted);

            if (existing.Format == converted.Format
                && existing.Width == converted.Width
                && existing.Height == converted.Height) {
                return new RenderSurface {
                    Id = existing.Id,
                    Width = existing.Width,
                    Height = existing.Height,
                    Format = existing.Format,
                    DefaultPaletteId = converted.DefaultPaletteId != 0 ? converted.DefaultPaletteId : existing.DefaultPaletteId,
                    SourceData = converted.SourceData,
                };
            }

            return converted;
        }

        internal static bool TryCopySurfaceTextureForExport(
            LegacyDatReader source,
            DefaultDatReaderWriter writer,
            uint id,
            int? iteration) =>
            CopySurfaceTexture(source, writer, id, iteration);

        private static bool CopySurfaceTexture(
            LegacyDatReader source,
            DefaultDatReaderWriter writer,
            uint id,
            int? iteration) {
            if (!source.TryGet<SurfaceTexture>(id, out var surfaceTexture) || surfaceTexture == null) {
                return false;
            }

            RemapSurfaceTextureRenderSurfaceIdsForRetail(surfaceTexture);
            return writer.TrySave(surfaceTexture, iteration);
        }

        internal static void RemapRenderSurfaceForRetail(RenderSurface renderSurface) {
            ArgumentNullException.ThrowIfNull(renderSurface);
            if (renderSurface.SourceData != null) {
                renderSurface.SourceData = TrimRenderSurfaceSourceDataForRetail(renderSurface);
            }

            // NOTE: PFID_INDEX16 source data carries DIRECT palette indices (0..palette.Count-1).
            // DM and retail share this convention and we copy the referenced Palette unchanged, so
            // the indices must pass through verbatim. A previous ×8 "scale" pushed indices far past
            // the 256-colour palette (e.g. 54..231 → 432..1848), so the retail client read garbage
            // palette entries → wrong colours, landscape-on-body, checkerboard ground. Do not scale.
            renderSurface.Id = ConvertRenderSurfaceIdToRetail(renderSurface.Id);
        }

        internal static byte[] TrimRenderSurfaceSourceDataForRetail(RenderSurface renderSurface) {
            ArgumentNullException.ThrowIfNull(renderSurface);
            ArgumentNullException.ThrowIfNull(renderSurface.SourceData);

            int bytesPerPixel = ((PixelFormat)renderSurface.Format) switch {
                PixelFormat.PFID_INDEX16 => 2,
                PixelFormat.PFID_A8R8G8B8 => 4,
                PixelFormat.PFID_R8G8B8 => 3,
                _ => 0,
            };

            if (bytesPerPixel <= 0 || renderSurface.Width <= 0 || renderSurface.Height <= 0) {
                return renderSurface.SourceData;
            }

            long expectedLength = (long)renderSurface.Width * renderSurface.Height * bytesPerPixel;
            if (renderSurface.SourceData.Length <= expectedLength || expectedLength > int.MaxValue) {
                return renderSurface.SourceData;
            }

            return renderSurface.SourceData.AsSpan(0, (int)expectedLength).ToArray();
        }

        internal static void RemapSurfaceTextureRenderSurfaceIdsForRetail(SurfaceTexture surfaceTexture) {
            ArgumentNullException.ThrowIfNull(surfaceTexture);
            for (int i = 0; i < surfaceTexture.Textures.Count; i++) {
                surfaceTexture.Textures[i] = ConvertRenderSurfaceIdToRetail(surfaceTexture.Textures[i]);
            }
        }

        internal static uint ConvertRenderSurfaceIdToRetail(uint renderSurfaceId) {
            if (!LegacyDatReader.IsSyntheticRenderSurfaceId(renderSurfaceId)) {
                return renderSurfaceId;
            }

            var activeMap = ActiveRenderSurfaceIdMap.Value;
            if (activeMap != null && activeMap.TryGetValue(renderSurfaceId, out var remapped)) {
                return remapped;
            }

            return 0x06000000u | (renderSurfaceId & 0x00FFFFFFu);
        }

        private static bool ShouldDeferDefinition(
            LegacyDatConversionTypeDefinition definition,
            bool includePhase3LocalData) {
            if (!definition.EnabledForConversion) {
                return true;
            }

            return definition.Phase switch {
                LegacyDatConversionPhase.Phase1WorldData => false,
                LegacyDatConversionPhase.Phase2AssetGraph => false,
                LegacyDatConversionPhase.Phase3LocalData => !includePhase3LocalData,
                _ => true,
            };
        }

        private static void PrepareOutputSeed(
            string retailSeedDirectory,
            string outputDirectory,
            LegacyDatExportPolicy policy,
            Action<string>? onProgress = null) {
            switch (policy.Portal) {
                case LegacyDatPortalCatalogPolicy.FullRetailPlusDmOverlay:
                    CopyRetailSeedFiles(retailSeedDirectory, outputDirectory, onProgress);
                    return;
            }

            var companionFiles = new List<string>();
            if (LegacyDatMergeRuleResolver.ShouldCopyRetailLocalSeed(policy)) {
                companionFiles.Add("client_local_English.dat");
            }
            else if (File.Exists(Path.Combine(retailSeedDirectory, "client_local_English.dat"))) {
                LegacyDatRetailShellBootstrap.PrepareShell(
                    retailSeedDirectory,
                    outputDirectory,
                    "client_local_English.dat",
                    onProgress);
            }

            if (LegacyDatMergeRuleResolver.ShouldCopyRetailHighresSeed(policy)) {
                companionFiles.Add("client_highres.dat");
            }

            if (LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(policy)) {
                companionFiles.Add("client_portal.dat");
            }

            if (companionFiles.Count > 0) {
                CopyRetailDatFiles(
                    retailSeedDirectory,
                    outputDirectory,
                    onProgress,
                    companionFiles.ToArray());
            }

            if (policy.World == LegacyDatWorldSource.DmOnly) {
                LegacyDatRetailShellBootstrap.PrepareShell(
                    retailSeedDirectory,
                    outputDirectory,
                    "client_cell_1.dat",
                    onProgress);
            }

            if (policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog
                && !LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(policy)) {
                LegacyDatRetailShellBootstrap.PrepareShell(
                    retailSeedDirectory,
                    outputDirectory,
                    "client_portal.dat",
                    onProgress);
            }
            else if (LegacyDatMergeRuleResolver.ShouldMergeRetailPortalGlobals(policy)
                && !LegacyDatMergeRuleResolver.UsesRetailPortalOverlayDeploy(policy)) {
                LegacyDatRetailShellBootstrap.PrepareShell(
                    retailSeedDirectory,
                    outputDirectory,
                    "client_portal.dat",
                    onProgress);
            }
        }

        private static void CopyRetailSeedFiles(
            string retailSeedDirectory,
            string outputDirectory,
            Action<string>? onProgress = null) {
            CopyRetailDatFiles(
                retailSeedDirectory,
                outputDirectory,
                onProgress,
                DatProjectModeInfo.RetailDatFiles.ToArray());
        }

        private static void CopyRetailDatFiles(
            string retailSeedDirectory,
            string outputDirectory,
            Action<string>? onProgress,
            params string[] datFiles) {
            foreach (string datFile in datFiles) {
                onProgress?.Invoke($"Copying seed file {datFile}...");
                string sourcePath = Path.Combine(retailSeedDirectory, datFile);
                string destinationPath = Path.Combine(outputDirectory, datFile);
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }

        private static void PatchRetailSeedForWriting(string outputDirectory) {
            var filesToPatch = new List<string>(LegacyDatClientExportFixer.ClientCatalogDatFiles);
            filesToPatch.Add("client_local_English.dat");
            foreach (string datFile in filesToPatch) {
                string datPath = Path.Combine(outputDirectory, datFile);
                if (File.Exists(datPath)) {
                    LegacyDatClientExportFixer.ClearEmptyTransaction(datPath);
                    DatExportFixer.PatchFreeBlocksBeforeExport(datPath);
                }
            }
        }

        private static void FixLeafSentinels(string outputDirectory) {
            foreach (string datFile in LegacyDatClientExportFixer.ClientCatalogDatFiles) {
                string datPath = Path.Combine(outputDirectory, datFile);
                if (File.Exists(datPath)) {
                    DatExportFixer.FixLeafBranchSentinels(datPath);
                }
            }
        }

        private static void EnsureLegacySourceDirectory(string datDirectory) {
            if (!DatProjectModeInfo.TryDetectFromDirectory(datDirectory, out var mode, out _, out var errors)
                || mode != DatProjectMode.LegacyPreTod) {
                throw new InvalidOperationException(
                    $"Expected a legacy pre-ToD DAT directory. {string.Join(" ", errors)}");
            }
        }

        private static void EnsureRetailSeedDirectory(string datDirectory) {
            if (!DatProjectModeInfo.TryDetectFromDirectory(datDirectory, out var mode, out _, out var errors)
                || mode != DatProjectMode.Retail) {
                throw new InvalidOperationException(
                    $"Expected a retail DAT seed directory. {string.Join(" ", errors)}");
            }
        }

        private static void EnsureDistinctDirectories(
            string legacyDatDirectory,
            string retailSeedDirectory,
            string outputDirectory) {
            string legacy = NormalizeDirectory(legacyDatDirectory);
            string retailSeed = NormalizeDirectory(retailSeedDirectory);
            string output = NormalizeDirectory(outputDirectory);

            if (legacy == retailSeed) {
                throw new InvalidOperationException("The retail seed directory must be different from the legacy source directory.");
            }

            if (legacy == output) {
                throw new InvalidOperationException("The output directory must be different from the legacy source directory.");
            }

            if (retailSeed == output) {
                throw new InvalidOperationException("The output directory must be different from the retail seed directory.");
            }
        }

        private static string NormalizeDirectory(string path) {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string GetVersionDisplayName(LegacyDatVersion version) => version switch {
            LegacyDatVersion.Tod => "Trials of Atlantis",
            LegacyDatVersion.DarkMajesty => "Dark Majesty",
            _ => "Unknown",
        };

        private static string BuildManifest(
            LegacyDatConversionAudit audit,
            IReadOnlyDictionary<string, int> writtenCounts,
            DatExportChecksumReport checksumReport,
            IReadOnlyList<string> conversionWarnings,
            LegacyToRetailConversionOptions options,
            LegacyDatExportPolicy policy,
            string outputDirectory) {
            var builder = new StringBuilder();
            builder.AppendLine($"generated_utc={DateTime.UtcNow:O}");
            builder.AppendLine($"legacy_dat_dir={Path.GetFullPath(options.LegacyDatDirectory)}");
            builder.AppendLine($"retail_seed_dir={Path.GetFullPath(options.RetailSeedDirectory)}");
            builder.AppendLine($"output_dir={outputDirectory}");
            builder.AppendLine($"source_version={audit.SourceVersion}");
            builder.AppendLine($"source_iteration={audit.SourceIteration}");
            builder.AppendLine($"export_mode={options.ExportMode}");
            builder.AppendLine($"export_preset={policy.Preset?.ToString() ?? "custom"}");
            builder.AppendLine($"later_local_data_pass_enabled={options.IncludePhase3LocalData}");
            builder.AppendLine();
            builder.AppendLine("[export_policy_json]");
            builder.AppendLine(policy.ToManifestJson());
            builder.AppendLine();
            builder.AppendLine("[audit]");
            foreach (var type in audit.Types) {
                builder.AppendLine(
                    $"{type.ObjectType}: source={type.SourceCount}, convertible={type.ConvertibleCount}, written={writtenCounts.GetValueOrDefault(type.ObjectType)}, synthetic={type.SyntheticCount}, fallback={type.FallbackCount}, deferred={type.DeferredCount}, unsupported={type.UnsupportedCount}");
            }

            builder.AppendLine();
            builder.AppendLine("[findings]");
            foreach (var finding in audit.Findings) {
                builder.AppendLine($"{finding.Severity}: {finding.Message}");
            }

            builder.AppendLine();
            builder.AppendLine("[conversion_warnings]");
            if (conversionWarnings.Count == 0) {
                builder.AppendLine("None");
            }
            else {
                foreach (string warning in conversionWarnings) {
                    builder.AppendLine(warning);
                }
            }

            builder.AppendLine();
            builder.AppendLine("[checksum_validation]");
            foreach (string line in checksumReport.Lines) {
                builder.AppendLine(line);
            }

            if (LegacyDatMergeRuleResolver.UsesRetailPortalOverlayDeploy(policy)) {
                builder.AppendLine();
                builder.AppendLine("[client_launch_deploy]");
                if (policy.Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog) {
                    builder.AppendLine("client_portal.dat=full retail portal with converted Full DM catalog overlaid in-place");
                }
                else {
                    builder.AppendLine("client_portal.dat=full retail portal with converted DM world assets overlaid in-place");
                    builder.AppendLine("RenderSurface IDs replace existing retail 0x06 entries; SurfaceTexture IDs may be added freely.");
                }

                builder.AppendLine("Copy all client_*.dat files from this folder into the AC client install.");
            }
            else if (policy.Deploy == LegacyDatPortalDeployPolicy.SingleConvertedCatalog) {
                builder.AppendLine();
                builder.AppendLine("[client_launch_deploy]");
                builder.AppendLine("client_portal.dat=single converted/pruned portal catalog (no retail launch swap)");
                builder.AppendLine("Copy all client_*.dat files from this folder into the AC client install.");
            }

            return builder.ToString();
        }
    }
}
