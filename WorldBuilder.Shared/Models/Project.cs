using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Acme.Dat;
using Microsoft.Data.Sqlite;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Shared.Services;

namespace WorldBuilder.Shared.Models {
    public partial class Project : ObservableObject, IDisposable {
        private static JsonSerializerOptions _opts = new JsonSerializerOptions { WriteIndented = true };
        private string _filePath = string.Empty;

        [ObservableProperty] private string _name = string.Empty;

        [ObservableProperty] private Guid _guid;

        [ObservableProperty] private bool _isHosting = false;

        [ObservableProperty] private string _remoteUrl = string.Empty;

        public string ProjectDirectory => Path.GetDirectoryName(FilePath) ?? string.Empty;
        public string DatDirectory => Path.Combine(ProjectDirectory, "dats");
        public string BaseDatDirectory => Path.Combine(DatDirectory, "base");
        public string DatabasePath => Path.Combine(ProjectDirectory, "project.db");

        [JsonIgnore] public string FilePath { get => _filePath; set => SetProperty(ref _filePath, value); }

        [JsonIgnore] public DocumentManager DocumentManager { get; set; }

        [JsonIgnore] public IDatReaderWriter DatReaderWriter { get; set; }

        [JsonIgnore] public CustomTextureStore CustomTextures { get; private set; }

        public DatProjectMode DatMode { get; set; } = DatProjectMode.Retail;

        [JsonIgnore] public bool IsReadOnlyDatProject => DatProjectModeInfo.IsReadOnly(DatMode);

        [JsonIgnore] public bool CanExportDats => !IsReadOnlyDatProject;

        public static Project? FromDisk(string projectFilePath) {
            if (!File.Exists(projectFilePath)) {
                return null;
            }

            var project = JsonSerializer.Deserialize<Project>(File.ReadAllText(projectFilePath), _opts);
            if (project != null) {
                project.FilePath = projectFilePath;
                project.SyncDatModeWithDisk();
                project.InitializeDatReaderWriter();
            }

            return project;
        }

        public static Project? Create(string projectName, string projectFilePath, string baseDatDirectory) {
            if (!DatProjectModeInfo.TryDetectFromDirectory(baseDatDirectory, out var datMode, out _, out var errors)) {
                throw new InvalidOperationException(string.Join(System.Environment.NewLine, errors));
            }

            var projectDir = Path.GetDirectoryName(projectFilePath);
            if (!Directory.Exists(projectDir)) {
                Directory.CreateDirectory(projectDir);
            }

            var datDir = Path.Combine(projectDir, "dats");
            var baseDatDir = Path.Combine(datDir, "base");

            if (!Directory.Exists(baseDatDir)) {
                Directory.CreateDirectory(baseDatDir);
            }

            if (Directory.Exists(baseDatDirectory)) {
                foreach (var datFile in DatProjectModeInfo.GetRequiredFiles(datMode)) {
                    var sourcePath = Path.Combine(baseDatDirectory, datFile);
                    var destPath = Path.Combine(baseDatDir, datFile);

                    if (File.Exists(sourcePath)) {
                        File.Copy(sourcePath, destPath, true);
                    }
                }
            }

            var project = new Project() {
                Name = projectName,
                FilePath = projectFilePath,
                Guid = Guid.NewGuid(),
                DatMode = datMode,
            };

            project.InitializeDatReaderWriter();
            project.Save();
            return project;
        }

        public Project() {
        }

        private void InitializeDatReaderWriter() {
            if (Directory.Exists(BaseDatDirectory)) {
                SyncDatModeWithDisk();
                DatReaderWriter = CreateDatReaderWriter(DatAccessType.Read);
            }
            else {
                throw new DirectoryNotFoundException($"Base dat directory not found: {BaseDatDirectory}");
            }
            CustomTextures = new CustomTextureStore(ProjectDirectory);
        }

        private void SyncDatModeWithDisk() {
            if (DatProjectModeInfo.TryDetectFromDirectory(BaseDatDirectory, out var detectedMode, out _, out _)
                && !HasFilesForMode(DatMode)) {
                DatMode = detectedMode;
            }
        }

        private bool HasFilesForMode(DatProjectMode mode) {
            return DatProjectModeInfo.GetRequiredFiles(mode)
                .All(file => File.Exists(Path.Combine(BaseDatDirectory, file)));
        }

        private IDatReaderWriter CreateDatReaderWriter(DatAccessType accessType) {
            return DatMode switch {
                DatProjectMode.Retail => new DefaultDatReaderWriter(
                    BaseDatDirectory, accessType, FileCachingStrategy.OnDemand, IndexCachingStrategy.OnDemand),
                DatProjectMode.LegacyPreTod => new LegacyDatReader(BaseDatDirectory),
                _ => throw new NotSupportedException($"Unsupported DAT mode: {DatMode}"),
            };
        }

        /// <summary>
        /// Recreates read-only DAT handles after another process (or a short-lived ReadWrite connection)
        /// wrote new portal files — e.g. OBJ import into <c>client_portal.dat</c>.
        /// </summary>
        public void ReloadDatReadersAfterExternalWrite() {
            if (IsReadOnlyDatProject) {
                return;
            }

            var oldProj = DatReaderWriter;
            var oldDoc = DocumentManager?.Dats;
            var fresh = CreateDatReaderWriter(DatAccessType.Read);
            DatReaderWriter = fresh;
            if (DocumentManager != null)
                DocumentManager.Dats = fresh;
            if (!ReferenceEquals(oldProj, oldDoc)) {
                oldProj?.Dispose();
                oldDoc?.Dispose();
            }
            else {
                oldProj?.Dispose();
            }
        }

        public void Save() {
            var tmp = Path.GetTempFileName();
            try {
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, _opts));
                File.Move(tmp, FilePath, overwrite: true);
            }
            finally {
                if (File.Exists(tmp)) {
                    File.Delete(tmp);
                }
            }
        }

        /// <summary>
        /// World database connection settings for instance repositioning on export.
        /// Persisted per-project in the project JSON.
        /// </summary>
        public AceDbSettings? AceDb { get; set; }

        /// <summary>
        /// Outdoor landblock instance placements (generators/items/portals) added from the Terrain editor.
        /// Exported to landblock_instances.sql when the world database is configured.
        /// </summary>
        public List<OutdoorInstancePlacement> OutdoorInstancePlacements { get; set; } = new();

        /// <summary>
        /// Called during export to write custom textures and update Region.
        /// Set by the UI layer (TextureImportService) since image decoding requires platform deps.
        /// </summary>
        [JsonIgnore]
        public Action<IDatReaderWriter, int?>? OnExportCustomTextures { get; set; }

        /// <summary>
        /// Called after DAT export with terrain change context so the UI layer can
        /// run the instance reposition workflow asynchronously.
        /// </summary>
        [JsonIgnore]
        public Func<RepositionContext, Task>? OnExportReposition { get; set; }

        /// <summary>
        /// Called after dungeon DAT export with any dungeon documents that have
        /// InstancePlacements (generators/items/portals for landblock_instance).
        /// The UI layer can generate SQL and write dungeon_instances.sql (and optionally apply).
        /// </summary>
        [JsonIgnore]
        public Action<string, IReadOnlyList<DungeonDocument>>? OnExportDungeonInstances { get; set; }

        public bool ExportDats(string exportDirectory, int portalIteration, Action<string>? onProgress = null) {
            if (!CanExportDats) {
                throw new InvalidOperationException("Legacy pre-ToD projects are read-only and cannot be exported.");
            }

            if (!Directory.Exists(exportDirectory)) {
                Directory.CreateDirectory(exportDirectory);
            }

            // Copy base dats from project's base directory
            var datFiles = new[] {
                "client_cell_1.dat", "client_portal.dat", "client_highres.dat", "client_local_English.dat"
            };

            onProgress?.Invoke("Copying base DAT files...");
            foreach (var datFile in datFiles) {
                var sourcePath = Path.Combine(BaseDatDirectory, datFile);
                var destPath = Path.Combine(exportDirectory, datFile);

                if (File.Exists(sourcePath)) {
                    File.Copy(sourcePath, destPath, true);
                }
            }

            var terrainDoc = DocumentManager.GetOrCreateDocumentAsync<TerrainDocument>("terrain").Result;

            // Collect all layers that are marked for export
            var exportLayers = new List<TerrainLayer>();
            if (terrainDoc.TerrainData.RootItems != null) {
                CollectExportLayers(terrainDoc.TerrainData.RootItems, exportLayers);
            }

            // No reverse -- iterate top-to-bottom for first-non-null per field
            // (CollectExportLayers already returns in tree order = top-to-bottom)

            // Identify all landblocks that need to be saved (modified in base OR any export layer)
            var modifiedLandblocks = new HashSet<ushort>(terrainDoc.TerrainData.Landblocks.Keys);
            var layerDocs = new Dictionary<string, LayerDocument>();

            foreach (var layer in exportLayers) {
                var layerDoc = DocumentManager.GetOrCreateDocumentAsync<LayerDocument>(layer.DocumentId).Result;
                if (layerDoc != null) {
                    layerDocs[layer.DocumentId] = layerDoc;
                    foreach (var lbKey in layerDoc.TerrainData.Landblocks.Keys) {
                        modifiedLandblocks.Add(lbKey);
                    }
                }
            }

            bool hasDocumentExports = false;
            foreach (var (_, doc) in DocumentManager.ActiveDocs) {
                if (doc is LandblockDocument lbDoc && (lbDoc.IsDirty || lbDoc.LoadedFromProjection)) {
                    hasDocumentExports = true;
                    break;
                }
                if (doc is DungeonDocument) {
                    hasDocumentExports = true;
                    break;
                }
                if (doc is PortalDatDocument portalDoc && portalDoc.EntryCount > 0) {
                    hasDocumentExports = true;
                    break;
                }
                if (doc is LayoutDatDocument layoutDoc && layoutDoc.EntryCount > 0) {
                    hasDocumentExports = true;
                    break;
                }
            }

            bool hasCustomTextureExports = CustomTextures.Entries.Count > 0;
            if (modifiedLandblocks.Count == 0 && !hasDocumentExports && !hasCustomTextureExports) {
                onProgress?.Invoke("No DAT changes detected; export will keep copied DATs unchanged.");
                Console.WriteLine("[Export] No DAT changes detected; copied DATs left untouched.");
                return true;
            }

            // Patch DAT headers to prevent Chorizite's block allocator from
            // reusing in-use blocks (retail DATs use a linked-list free chain
            // but Chorizite assumes contiguous allocation).
            onProgress?.Invoke("Patching DAT free block headers...");
            foreach (var datFile in datFiles) {
                var destPath = Path.Combine(exportDirectory, datFile);
                DatExportFixer.PatchFreeBlocksBeforeExport(destPath);
            }

            var writer = new DefaultDatReaderWriter(exportDirectory, DatAccessType.ReadWrite);
            var checksumTargets = new DatExportChecksumTargets();

            if (portalIteration == DatReaderWriter.GetIteration(DatArchive.Portal)) {
                portalIteration = 0;
            }

            const int LANDBLOCK_SIZE = 81;

            onProgress?.Invoke($"Preparing {modifiedLandblocks.Count} terrain landblocks...");

            // Capture old terrain from base DATs for reposition delta calculation
            var oldTerrain = new Dictionary<ushort, TerrainEntry[]>();
            var newTerrain = new Dictionary<ushort, TerrainEntry[]>();

            foreach (var lbKey in modifiedLandblocks) {
                var baseLbId = (uint)(lbKey << 16) | 0xFFFF;
                if (DatReaderWriter.TryGet<LandBlock>(baseLbId, out var baseLb)) {
                    var entries = new TerrainEntry[LANDBLOCK_SIZE];
                    for (int i = 0; i < LANDBLOCK_SIZE; i++) {
                        entries[i] = new TerrainEntry(
                            ((TerrainInfo)baseLb.Terrain[i]).Road,
                            ((TerrainInfo)baseLb.Terrain[i]).Scenery,
                            (byte)((TerrainInfo)baseLb.Terrain[i]).Type,
                            baseLb.Height[i]);
                    }
                    oldTerrain[lbKey] = entries;
                }
            }

            // Process and save each modified landblock
            int terrainWritten = 0;
            foreach (var lbKey in modifiedLandblocks) {
                if (++terrainWritten % 1000 == 0) {
                    onProgress?.Invoke($"Writing terrain {terrainWritten} / {modifiedLandblocks.Count}...");
                }
                var lbId = (uint)(lbKey << 16) | 0xFFFF;

                var currentEntries = terrainDoc.GetLandblockInternal(lbKey);
                if (currentEntries == null) {
                    continue;
                }

                // Apply changes from each export layer using per-field masks (top-to-bottom)
                var resolved = new byte[LANDBLOCK_SIZE];
                foreach (var layer in exportLayers) {
                    if (!layerDocs.TryGetValue(layer.DocumentId, out var layerDoc)) continue;
                    if (!layerDoc.TerrainData.Landblocks.TryGetValue(lbKey, out var sparseCells)) continue;
                    layerDoc.TerrainData.FieldMasks.TryGetValue(lbKey, out var sparseMasks);

                    foreach (var (cellIdx, cellValue) in sparseCells) {
                        byte layerMask = (sparseMasks != null && sparseMasks.TryGetValue(cellIdx, out var m))
                            ? m
                            : TerrainFieldMask.All;

                        byte unclaimed = (byte)(layerMask & ~resolved[cellIdx]);
                        if (unclaimed == 0) continue;

                        var entry = new TerrainEntry(cellValue);
                        var current = currentEntries[cellIdx];

                        currentEntries[cellIdx] = new TerrainEntry(
                            road:    (unclaimed & TerrainFieldMask.Road) != 0    ? entry.Road    : current.Road,
                            scenery: (unclaimed & TerrainFieldMask.Scenery) != 0 ? entry.Scenery : current.Scenery,
                            type:    (unclaimed & TerrainFieldMask.Type) != 0    ? entry.Type    : current.Type,
                            height:  (unclaimed & TerrainFieldMask.Height) != 0  ? entry.Height  : current.Height
                        );

                        resolved[cellIdx] |= unclaimed;
                    }
                }

                // Snapshot the composited terrain for reposition
                var snapshot = new TerrainEntry[LANDBLOCK_SIZE];
                Array.Copy(currentEntries, snapshot, LANDBLOCK_SIZE);
                newTerrain[lbKey] = snapshot;

                if (!writer.TryGet<LandBlock>(lbId, out var lb)) {
                    continue;
                }

                for (var i = 0; i < LANDBLOCK_SIZE; i++) {
                    var entry = currentEntries[i];
                    lb.Terrain[i] = new TerrainInfo {
                        Road = entry.Road,
                        Scenery = entry.Scenery,
                        Type = (TerrainTextureType)entry.Type
                    };
                    lb.Height[i] = entry.Height;
                }

                if (writer.TrySave(lb, portalIteration))
                    checksumTargets.TrackCell(lbId);
            }

            // Reposition DAT static objects (LandBlockInfo.Objects) to match new terrain heights
            float[]? repoHeightTable = null;
            if (DatReaderWriter.TryGet<Region>(0x13000000, out var repoRegion)) {
                repoHeightTable = repoRegion.LandDefs.LandHeightTable;
            }

            if (repoHeightTable != null && oldTerrain.Count > 0 && newTerrain.Count > 0) {
                onProgress?.Invoke("Repositioning DAT statics...");
                int repoCount = 0;
                foreach (var lbKey in modifiedLandblocks) {
                    if (!oldTerrain.TryGetValue(lbKey, out var oldEntries)) continue;
                    if (!newTerrain.TryGetValue(lbKey, out var newEntries2)) continue;

                    var infoId = (uint)(lbKey << 16) | 0xFFFE;
                    if (!writer.TryGet<LandBlockInfo>(infoId, out var lbi)) continue;
                    if (lbi.Objects == null || lbi.Objects.Count == 0) continue;

                    uint landblockX = (uint)(lbKey >> 8) & 0xFF;
                    uint landblockY = (uint)(lbKey & 0xFF);
                    bool anyMoved = false;

                    foreach (var stab in lbi.Objects) {
                        float localX = stab.Frame.Origin.X;
                        float localY = stab.Frame.Origin.Y;
                        if (localX < 0 || localX > 192f || localY < 0 || localY > 192f) continue;

                        float oldZ = TerrainHeightSampler.SampleHeightTriangle(
                            oldEntries, repoHeightTable, localX, localY, landblockX, landblockY);
                        float newZ = TerrainHeightSampler.SampleHeightTriangle(
                            newEntries2, repoHeightTable, localX, localY, landblockX, landblockY);

                        float delta = newZ - oldZ;
                        if (MathF.Abs(delta) < 0.01f) continue;

                        stab.Frame.Origin = new System.Numerics.Vector3(
                            stab.Frame.Origin.X,
                            stab.Frame.Origin.Y,
                            stab.Frame.Origin.Z + delta);
                        anyMoved = true;
                    }

                    if (anyMoved) {
                        if (writer.TrySave(lbi, portalIteration))
                            checksumTargets.TrackCell(infoId);
                        repoCount++;
                    }
                }
                onProgress?.Invoke($"Repositioned statics in {repoCount} landblocks");
            }

            onProgress?.Invoke("Writing static objects and dungeons...");

            var dungeonManifestLines = new List<string>();

            // Export static object changes from LandblockDocuments, dungeon data, and portal table edits
            // Only save dirty documents -- streamed-in but unmodified docs must not overwrite
            // the base DAT content (which may have been repositioned above).
            foreach (var (docId, doc) in DocumentManager.ActiveDocs) {
                if (doc is LandblockDocument lbDoc) {
                    if (!lbDoc.IsDirty && !lbDoc.LoadedFromProjection) continue;
                    if (lbDoc.SaveToDats(writer, portalIteration).GetAwaiter().GetResult() &&
                        TryParseLandblockDocId(docId, out ushort lbKey)) {
                        checksumTargets.TrackCell((uint)(lbKey << 16) | 0xFFFF);
                        checksumTargets.TrackCell((uint)(lbKey << 16) | 0xFFFE);
                    }
                }
                else if (doc is DungeonDocument dungeonDoc) {
                    var validation = dungeonDoc.ValidateComprehensive();
                    int valErrors = validation.Count(v => v.Severity == DungeonDocument.ValidationSeverity.Error);
                    int valWarnings = validation.Count(v => v.Severity == DungeonDocument.ValidationSeverity.Warning);
                    var fingerprint = BuildDungeonFingerprint(dungeonDoc);

                    onProgress?.Invoke(
                        $"Exporting {docId}: cells={dungeonDoc.Cells.Count}, validation={valErrors} error(s)/{valWarnings} warning(s)");
                    Console.WriteLine(
                        $"[Export] Dungeon {docId}: path={exportDirectory}, validation={valErrors} error(s), {valWarnings} warning(s), {fingerprint}");

                    if (valErrors > 0) {
                        foreach (var err in validation.Where(v => v.Severity == DungeonDocument.ValidationSeverity.Error).Take(10)) {
                            Console.WriteLine($"[Export]   {err}");
                        }
                    }

                    if (dungeonDoc.SaveToDats(writer, portalIteration).GetAwaiter().GetResult()) {
                        uint lb = dungeonDoc.LandblockKey;
                        checksumTargets.TrackCell((uint)(lb << 16) | 0xFFFF);
                        checksumTargets.TrackCell((uint)(lb << 16) | 0xFFFE);
                        foreach (var cell in dungeonDoc.Cells)
                            checksumTargets.TrackCell((uint)(lb << 16) | cell.CellNumber);
                    }
                    dungeonManifestLines.Add($"{docId}: {fingerprint}; validationErrors={valErrors}; validationWarnings={valWarnings}");
                    dungeonManifestLines.Add($"{docId}.teleloc_center_cell={BuildCenteredTeleportLine(dungeonDoc, writer)}");
                }
                else if (doc is PortalDatDocument portalDoc) {
                    if (portalDoc.SaveToDats(writer, portalIteration).GetAwaiter().GetResult()) {
                        foreach (var fileId in portalDoc.GetEntryIds())
                            checksumTargets.TrackPortal(fileId);
                    }
                }
                else if (doc is LayoutDatDocument layoutDoc) {
                    layoutDoc.SaveToDats(writer, portalIteration);
                }
            }

            // Dungeon instance placements (generators/items/portals) for the world database
            var dungeonsWithPlacements = new List<DungeonDocument>();
            foreach (var (_, doc) in DocumentManager.ActiveDocs) {
                if (doc is DungeonDocument dng && dng.InstancePlacements.Count > 0)
                    dungeonsWithPlacements.Add(dng);
            }
            if (dungeonsWithPlacements.Count > 0)
                OnExportDungeonInstances?.Invoke(exportDirectory, dungeonsWithPlacements);

            var exportManifestPath = Path.Combine(exportDirectory, "worldbuilder_export_manifest.txt");
            if (dungeonManifestLines.Count > 0) {
                var manifest = new StringBuilder();
                manifest.AppendLine($"export_utc={DateTime.UtcNow:O}");
                manifest.AppendLine($"export_dir={exportDirectory}");
                manifest.AppendLine($"portal_iteration={portalIteration}");
                foreach (var line in dungeonManifestLines)
                    manifest.AppendLine(line);
                File.WriteAllText(exportManifestPath, manifest.ToString());
                onProgress?.Invoke($"Wrote export manifest: {exportManifestPath}");
                Console.WriteLine($"[Export] Wrote manifest: {exportManifestPath}");
            }

            onProgress?.Invoke("Writing custom textures...");

            // Write custom imported textures and update Region for terrain replacements
            try {
                OnExportCustomTextures?.Invoke(writer, portalIteration);
            }
            catch (Exception ex) {
                Console.WriteLine($"[Export] Error writing custom textures: {ex.Message}");
            }

            // TODO: all other dat iterations
            writer.SetIteration(DatArchive.Portal, portalIteration);

            onProgress?.Invoke("Running instance reposition...");

            // Instance reposition: build context and invoke hook if wired
            if (OnExportReposition != null && oldTerrain.Count > 0 && newTerrain.Count > 0) {
                float[]? heightTable = null;
                if (DatReaderWriter.TryGet<Region>(0x13000000, out var region)) {
                    heightTable = region.LandDefs.LandHeightTable;
                }

                if (heightTable != null) {
                    var ctx = new RepositionContext {
                        ModifiedLandblocks = modifiedLandblocks.ToArray(),
                        OldTerrain = oldTerrain,
                        NewTerrain = newTerrain,
                        LandHeightTable = heightTable,
                        ExportDirectory = exportDirectory
                    };
                    OnExportReposition(ctx).GetAwaiter().GetResult();
                }
            }

            writer.Dispose();

            // Fix B-tree leaf node branch sentinels: Chorizite writes 0xCDCDCDCD
            // for unused slots but loaders expect 0x00000000 to identify leaf nodes.
            onProgress?.Invoke("Fixing DAT B-tree leaf nodes...");
            foreach (var datFile in datFiles) {
                var destPath = Path.Combine(exportDirectory, datFile);
                DatExportFixer.FixLeafBranchSentinels(destPath);
            }

            string conversionManifestPath = Path.Combine(BaseDatDirectory, "worldbuilder_legacy_conversion_manifest.txt");
            string exportConversionManifestPath = Path.Combine(exportDirectory, "worldbuilder_legacy_conversion_manifest.txt");
            if (File.Exists(conversionManifestPath)
                && !string.Equals(
                    Path.GetFullPath(conversionManifestPath),
                    Path.GetFullPath(exportConversionManifestPath),
                    StringComparison.OrdinalIgnoreCase)) {
                File.Copy(conversionManifestPath, exportConversionManifestPath, overwrite: true);
            }

            LegacyDatExportPolicy clientPolicy = LegacyDatExportManifest.TryLoadPolicyFromJson(exportDirectory, out var manifestPolicy)
                ? manifestPolicy
                : LegacyDatExportManifest.TryLoadPolicy(exportDirectory, out var presetPolicy)
                    ? presetPolicy
                    : LegacyDatExportManifest.TryLoadPolicyFromJson(BaseDatDirectory, out var baseManifestPolicy)
                        ? baseManifestPolicy
                        : LegacyDatExportManifest.TryLoadPolicy(BaseDatDirectory, out var basePresetPolicy)
                            ? basePresetPolicy
                            : LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);

            string? retailSeedDirectory = LegacyDatExportManifest.TryLoadRetailSeedDirectory(exportDirectory)
                ?? LegacyDatExportManifest.TryLoadRetailSeedDirectory(BaseDatDirectory);
            if (retailSeedDirectory == null && Directory.Exists(BaseDatDirectory)
                && File.Exists(Path.Combine(BaseDatDirectory, "client_portal.dat"))) {
                retailSeedDirectory = BaseDatDirectory;
            }

            string? legacyDatDirectory = LegacyDatExportManifest.TryLoadLegacyDatDirectory(exportDirectory)
                ?? LegacyDatExportManifest.TryLoadLegacyDatDirectory(BaseDatDirectory);

            bool runFullDmFinalize = !string.IsNullOrWhiteSpace(legacyDatDirectory)
                && !string.IsNullOrWhiteSpace(retailSeedDirectory)
                && LegacyDatMergeRuleResolver.UsesRetailPortalOverlayDeploy(clientPolicy)
                && LegacyDatMergeRuleResolver.ShouldEnsureRegionTerrainPortalAssets(clientPolicy);

            if (runFullDmFinalize) {
                onProgress?.Invoke("Finalizing Full DM client DATs (portal overlay, terrain textures, CharGen)...");
                LegacyDatClientDatRepair.Repair(
                    exportDirectory,
                    retailSeedDirectory!,
                    clientPolicy,
                    legacyDatDirectory,
                    onProgress);
            }
            else {
                LegacyDatClientDatRepair.ReapplyLocalUiOverrides(
                    exportDirectory,
                    clientPolicy,
                    legacyDatDirectory,
                    onProgress);
                onProgress?.Invoke("Finalizing client DATs for retail client init (journals, headers, portal catalog)...");
                LegacyDatClientExportFixer.CompleteClientExport(
                    exportDirectory,
                    clientPolicy,
                    onProgress,
                    retailSeedDirectory ?? exportDirectory);
            }

            onProgress?.Invoke("Validating retail client load requirements...");
            var clientValidation = RetailClientDatExportValidator.Validate(
                exportDirectory,
                new RetailClientDatExportValidator.Options {
                    ExportPolicy = clientPolicy,
                    RetailSeedDirectory = retailSeedDirectory,
                    RequireOverlayPortalRoot =
                        LegacyDatMergeRuleResolver.ShouldRequireOverlayPortalRoot(clientPolicy),
                });

            foreach (string error in clientValidation.Errors) {
                Console.WriteLine($"[Export] CLIENT DAT ERROR: {error}");
            }

            foreach (string warning in clientValidation.Warnings) {
                Console.WriteLine($"[Export] CLIENT DAT WARN: {warning}");
            }

            if (!clientValidation.IsValid) {
                throw new InvalidOperationException(
                    "Export failed retail client DAT validation. Do not point the game client at this folder until repaired."
                    + System.Environment.NewLine
                    + string.Join(System.Environment.NewLine, clientValidation.Errors));
            }

            onProgress?.Invoke("Validating export checksums...");
            var checksumReport = DatExportChecksumValidator.ValidateExportDirectory(exportDirectory, checksumTargets);
            foreach (var line in checksumReport.Lines) {
                if (line.StartsWith("WARN:", StringComparison.Ordinal))
                    Console.WriteLine($"[Export] {line}");
                else
                    Console.WriteLine($"[Export] {line}");
            }

            if (File.Exists(exportManifestPath) || checksumReport.Lines.Count > 0) {
                var manifest = new StringBuilder();
                if (File.Exists(exportManifestPath))
                    manifest.Append(File.ReadAllText(exportManifestPath));
                if (!manifest.ToString().EndsWith('\n'))
                    manifest.AppendLine();
                manifest.AppendLine("[checksum_validation]");
                foreach (var line in checksumReport.Lines)
                    manifest.AppendLine(line);
                File.WriteAllText(exportManifestPath, manifest.ToString());
                onProgress?.Invoke($"Updated export manifest with checksum results");
            }

            return true;
        }

        private static string BuildDungeonFingerprint(DungeonDocument doc) {
            int cellCount = doc.Cells.Count;
            if (cellCount == 0) return "empty";

            var first = doc.Cells.OrderBy(c => c.CellNumber).First();
            float minZ = doc.Cells.Min(c => c.Origin.Z);
            float maxZ = doc.Cells.Max(c => c.Origin.Z);
            int linkedPortals = doc.Cells.Sum(c => c.CellPortals.Count(p => p.OtherCellId != 0 && p.OtherCellId != 0xFFFF));
            int staticCount = doc.Cells.Sum(c => c.StaticObjects.Count);

            return
                $"lb=0x{doc.LandblockKey:X4}, cells={cellCount}, firstCell=0x{first.CellNumber:X4}," +
                $" firstEnv=0x{first.EnvironmentId:X4}, firstPos=({first.Origin.X:F3},{first.Origin.Y:F3},{first.Origin.Z:F3})," +
                $" zRange=[{minZ:F3},{maxZ:F3}], linkedPortals={linkedPortals}, statics={staticCount}";
        }

        private static string BuildCenteredTeleportLine(DungeonDocument doc, IDatReaderWriter dats) {
            if (doc.Cells.Count == 0) return "N/A";

            // Pick the cell closest to the dungeon's center of mass, then use that
            // cell's floor centroid as a safe in-dungeon teleport target.
            var center = new Vector3(
                doc.Cells.Average(c => c.Origin.X),
                doc.Cells.Average(c => c.Origin.Y),
                doc.Cells.Average(c => c.Origin.Z));

            var anchor = doc.Cells
                .OrderBy(c => Vector3.DistanceSquared(c.Origin, center))
                .ThenBy(c => c.CellNumber)
                .First();

            var pos = ComputeCellFloorCentroid(anchor, dats);
            uint cellId = ((uint)doc.LandblockKey << 16) | anchor.CellNumber;

            return string.Format(
                CultureInfo.InvariantCulture,
                "0x{0:X8} [{1:F6} {2:F6} {3:F6}] 1.000000 0.000000 0.000000 0.000000",
                cellId,
                pos.X,
                pos.Y,
                pos.Z);
        }

        private static Vector3 ComputeCellFloorCentroid(DungeonCellData cell, IDatReaderWriter dats) {
            uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env))
                return cell.Origin;
            if (!env.Cells.TryGetValue(cell.CellStructure, out var cs))
                return cell.Origin;
            if (cs.VertexArray?.Vertices == null || cs.VertexArray.Vertices.Count == 0)
                return cell.Origin;

            var rot = cell.Orientation;
            if (rot.LengthSquared() < 0.01f) rot = Quaternion.Identity;
            rot = Quaternion.Normalize(rot);

            float minZ = float.MaxValue;
            var centroid = Vector3.Zero;
            int count = 0;

            foreach (var vtx in cs.VertexArray.Vertices.Values) {
                var worldPos = cell.Origin + Vector3.Transform(vtx.Origin, rot);
                centroid += worldPos;
                if (worldPos.Z < minZ) minZ = worldPos.Z;
                count++;
            }

            if (count == 0) return cell.Origin;
            centroid /= count;
            centroid.Z = minZ + 0.005f;
            return centroid;
        }

        private static bool TryParseLandblockDocId(string docId, out ushort lbKey) {
            lbKey = 0;
            if (!docId.StartsWith("landblock_", StringComparison.OrdinalIgnoreCase))
                return false;
            return ushort.TryParse(
                docId.AsSpan("landblock_".Length),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out lbKey);
        }

        private void CollectExportLayers(IEnumerable<TerrainLayerBase> items, List<TerrainLayer> result) {
            foreach (var item in items) {
                if (!item.IsExport) continue;

                if (item is TerrainLayerGroup group) {
                    CollectExportLayers(group.Children, result);
                }
                else if (item is TerrainLayer layer) {
                    result.Add(layer);
                }
            }
        }

        public void Dispose() {
            DatReaderWriter?.Dispose();
        }
    }
}
