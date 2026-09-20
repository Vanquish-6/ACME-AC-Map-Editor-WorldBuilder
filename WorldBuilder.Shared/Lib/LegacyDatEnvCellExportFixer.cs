using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Repairs EnvCell portal metadata for export. Legacy Dark Majesty
/// cells often store polygon IDs in OtherPortalId instead of portal array indices.
/// Does not mutate VisibleCells with outdoor LandCell ids (0x0001-0x0040) or inject
/// cell-id visibility stabs — some loaders cast every VisibleCells entry to EnvCell and
/// treats StaticObjects ids as setup/weanie ids in init_static_objects.
/// </summary>
public static class LegacyDatEnvCellExportFixer {
    public static void FixOutputDirectory(
        string outputDirectory,
        int? iteration,
        IList<string>? warnings = null,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var cellIds = EnumerateEnvCellIds(outputDirectory).ToArray();

        using var writer = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        FixCellDatabase(writer, cellIds, iteration, warnings, onProgress);
    }

    public static void FixCellDatabase(
        DefaultDatReaderWriter writer,
        IReadOnlyList<uint> cellIds,
        int? iteration,
        IList<string>? warnings = null,
        Action<string>? onProgress = null) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(cellIds);

        var cellsByLandblock = new Dictionary<uint, List<EnvCell>>();
        foreach (uint id in cellIds) {
            ushort cellNum = (ushort)(id & 0xFFFF);
            if (cellNum is 0xFFFE or 0xFFFF) {
                continue;
            }

            if (!writer.TryGet<EnvCell>(id, out var envCell) || envCell == null) {
                continue;
            }

            uint landblock = id >> 16;
            if (!cellsByLandblock.TryGetValue(landblock, out var list)) {
                list = new List<EnvCell>();
                cellsByLandblock[landblock] = list;
            }

            list.Add(envCell);
        }

        onProgress?.Invoke($"Fixing EnvCell portal transit on {cellsByLandblock.Count:N0} landblock(s)...");
        int portalRewrites = 0;
        int environmentPortalPatches = 0;
        int portalReorders = 0;
        int specializedPortalStructs = 0;
        int processed = 0;

        var allCells = cellsByLandblock.Values.SelectMany(cells => cells).ToList();
        int restoredVariants = RestoreMissingCellStructVariants(writer, allCells, iteration, warnings);
        int rebuiltDrawingBsps = EnsureCellStructDrawingBsps(writer, allCells, iteration, warnings);
        environmentPortalPatches = PatchEnvironmentDrawingPortalRefs(writer, allCells, iteration, warnings);
        specializedPortalStructs = SpecializeSharedCellStructPortalSequences(writer, allCells, iteration, warnings);
        environmentPortalPatches += FixEnvironmentDrawingBspPortalRefs(writer, allCells, iteration, warnings);

        if (restoredVariants > 0) {
            warnings?.Add($"EnvCell: restored {restoredVariants} dangling CellStruct variant(s) referenced by converted cells.");
        }

        if (rebuiltDrawingBsps > 0) {
            warnings?.Add($"EnvCell: synthesized {rebuiltDrawingBsps} missing CellStruct DrawingBSP tree(s) (retail DrawEnvCell derefs them unconditionally).");
        }

        foreach (var (landblock, cells) in cellsByLandblock.OrderBy(pair => pair.Key)) {
            processed++;
            if (processed == 1 || processed % 25 == 0 || processed == cellsByLandblock.Count) {
                onProgress?.Invoke($"Fixing EnvCell portal transit ({processed:N0}/{cellsByLandblock.Count:N0}, landblock 0x{landblock:X4})...");
            }

            var dirtyCellIds = new HashSet<uint>();
            portalReorders += ReorderCellPortalsToMatchCellStruct(writer, cells, dirtyCellIds);
            portalRewrites += RewriteOtherPortalIndices(cells, dirtyCellIds);
            portalRewrites += FixOutsidePortalOtherPortalIds(cells, dirtyCellIds);
            portalRewrites += FixInvalidCellPortalPolygonIds(writer, cells, dirtyCellIds);

            foreach (var cell in cells) {
                if (!dirtyCellIds.Contains(cell.Id)) {
                    continue;
                }

                if (!writer.TrySave(cell, iteration)) {
                    warnings?.Add($"EnvCell: failed to save fixed cell 0x{cell.Id:X8}.");
                }
            }
        }

        if (portalRewrites > 0) {
            warnings?.Add($"EnvCell: rewrote {portalRewrites} OtherPortalId value(s) to retail portal array indices.");
        }

        if (portalReorders > 0) {
            warnings?.Add($"EnvCell: reordered {portalReorders} CellPortal array(s) to match CellStruct portal ordering.");
        }

        if (specializedPortalStructs > 0) {
            warnings?.Add($"EnvCell: specialized {specializedPortalStructs} shared CellStruct portal sequence variant(s) for retail export.");
        }

        if (environmentPortalPatches > 0) {
            warnings?.Add($"EnvCell: merged doorway portal polygons on {environmentPortalPatches} environment cell struct(s).");
        }

        if (warnings != null) {
            LegacyDatEnvCellValidator.ValidateEnvironments(writer, allCells, warnings);
        }
    }

    public static int EnsureReferencedEnvironmentsPresent(
        LegacyDatReader legacySource,
        DefaultDatReaderWriter writer,
        IReadOnlyList<uint> cellIds,
        int? iteration,
        IList<string>? warnings = null,
        Action<string>? onProgress = null) {
        ArgumentNullException.ThrowIfNull(legacySource);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(cellIds);

        var referencedEnvironmentIds = new HashSet<uint>();
        foreach (uint id in cellIds) {
            ushort cellNum = (ushort)(id & 0xFFFF);
            if (cellNum is 0xFFFE or 0xFFFF) {
                continue;
            }

            if (!writer.TryGet<EnvCell>(id, out var envCell) || envCell == null || envCell.EnvironmentId == 0) {
                continue;
            }

            referencedEnvironmentIds.Add(0x0D000000u | envCell.EnvironmentId);
        }

        if (referencedEnvironmentIds.Count == 0) {
            return 0;
        }

        onProgress?.Invoke($"Ensuring {referencedEnvironmentIds.Count:N0} referenced Environment object(s) exist in portal...");
        int restored = 0;
        int processed = 0;
        foreach (uint envId in referencedEnvironmentIds.OrderBy(id => id)) {
            processed++;
            if (processed == 1 || processed % 250 == 0 || processed == referencedEnvironmentIds.Count) {
                onProgress?.Invoke($"Restoring referenced Environments ({processed:N0}/{referencedEnvironmentIds.Count:N0})...");
            }

            bool hasReadableEnvironment = TryGetEnvironment(writer, envId, out _);
            if (hasReadableEnvironment) {
                continue;
            }

            if (!legacySource.TryGet<Acme.Dat.Environment>(envId, out var legacyEnvironment) || legacyEnvironment == null) {
                warnings?.Add($"EnvCell: referenced environment 0x{envId:X8} is missing from legacy portal.");
                continue;
            }

            if (!writer.TrySave(legacyEnvironment, iteration)) {
                warnings?.Add($"EnvCell: failed to restore referenced environment 0x{envId:X8}.");
                continue;
            }

            restored++;
        }

        if (restored > 0) {
            onProgress?.Invoke($"Restored {restored:N0} referenced Environment object(s) missing from portal.");
        }

        return restored;
    }

    /// <summary>
    /// Re-writes referenced Environment objects from the legacy decode path when the exported copy
    /// lacks adequate drawing portal refs or still carries shallow fallback BSP roots.
    /// </summary>
    public static int ReassertReferencedEnvironmentsFromLegacy(
        LegacyDatReader legacySource,
        DefaultDatReaderWriter writer,
        IReadOnlyList<uint> cellIds,
        int? iteration,
        IList<string>? warnings = null,
        Action<string>? onProgress = null) {
        ArgumentNullException.ThrowIfNull(legacySource);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(cellIds);

        var referencedEnvironmentIds = new HashSet<uint>();
        foreach (uint id in cellIds) {
            ushort cellNum = (ushort)(id & 0xFFFF);
            if (cellNum is 0xFFFE or 0xFFFF) {
                continue;
            }

            if (!writer.TryGet<EnvCell>(id, out var envCell) || envCell == null || envCell.EnvironmentId == 0) {
                continue;
            }

            referencedEnvironmentIds.Add(0x0D000000u | envCell.EnvironmentId);
        }

        if (referencedEnvironmentIds.Count == 0) {
            return 0;
        }

        onProgress?.Invoke($"Re-asserting {referencedEnvironmentIds.Count:N0} referenced Environment BSP object(s) from legacy...");
        int reasserted = 0;
        int processed = 0;
        foreach (uint envId in referencedEnvironmentIds.OrderBy(id => id)) {
            processed++;
            if (processed == 1 || processed % 250 == 0 || processed == referencedEnvironmentIds.Count) {
                onProgress?.Invoke($"Re-asserting Environment BSP ({processed:N0}/{referencedEnvironmentIds.Count:N0})...");
            }

            if (!legacySource.TryGet<Acme.Dat.Environment>(envId, out var legacyEnvironment) || legacyEnvironment == null) {
                warnings?.Add($"EnvCell: referenced environment 0x{envId:X8} is missing from legacy portal.");
                continue;
            }

            if (!NeedsEnvironmentBspReassert(writer, envId, legacyEnvironment)) {
                continue;
            }

            foreach (CellStruct cellStruct in legacyEnvironment.Cells.Values) {
                LegacyBspRetailNormalizer.EnsureDrawingBspPortalRefs(cellStruct);
            }

            // The legacy table only carries the base structs. The export may have been extended by
            // SpecializeSharedCellStructPortalSequences with per-portal-sequence variants whose ids
            // converted EnvCells already reference; dropping them dangles those cells and AVs the
            // retail client in DrawEnvCell. Rebuild each variant from the freshly decoded legacy
            // base so BSP fidelity and the variant ids both survive.
            if (TryGetEnvironment(writer, envId, out var exportEnvironment)) {
                foreach (var (variantKey, variantStruct) in exportEnvironment.Cells) {
                    if (legacyEnvironment.Cells.ContainsKey(variantKey)) {
                        continue;
                    }

                    var variantPortals = variantStruct.Portals ?? new List<ushort>();
                    CellStruct? baseStruct = FindVariantBaseStruct(legacyEnvironment, variantPortals);
                    if (baseStruct == null) {
                        legacyEnvironment.Cells[variantKey] = variantStruct;
                        continue;
                    }

                    var rebuiltVariant = CloneCellStruct(baseStruct);
                    var validSequence = variantPortals
                        .Where(polyId => rebuiltVariant.Polygons.ContainsKey(polyId))
                        .ToList();
                    if (validSequence.Count > 0) {
                        rebuiltVariant.Portals = validSequence;
                    }

                    LegacyBspRetailNormalizer.EnsureDrawingBspPortalRefs(rebuiltVariant);
                    legacyEnvironment.Cells[variantKey] = rebuiltVariant;
                }
            }

            if (!writer.TrySave(legacyEnvironment, iteration)) {
                warnings?.Add($"EnvCell: failed to re-assert environment 0x{envId:X8} from legacy.");
                continue;
            }

            reasserted++;
        }

        if (reasserted > 0) {
            onProgress?.Invoke($"Re-asserted {reasserted:N0} Environment object(s) with legacy BSP fidelity.");
        }

        return reasserted;
    }

    public static int ReassertEnvCellsFromLegacy(
        LegacyDatReader legacySource,
        DefaultDatReaderWriter writer,
        IReadOnlyList<uint> cellIds,
        int? iteration,
        IList<string>? warnings = null,
        Action<string>? onProgress = null) {
        ArgumentNullException.ThrowIfNull(legacySource);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(cellIds);

        onProgress?.Invoke($"Re-asserting {cellIds.Count:N0} EnvCell object(s) from legacy before retail portal-index repair...");
        int reasserted = 0;
        int processed = 0;

        foreach (uint cellId in cellIds.OrderBy(id => id)) {
            processed++;
            if (processed == 1 || processed % 5000 == 0 || processed == cellIds.Count) {
                onProgress?.Invoke($"Re-asserting EnvCells ({processed:N0}/{cellIds.Count:N0})...");
            }

            ushort cellNum = (ushort)(cellId & 0xFFFF);
            if (cellNum is 0xFFFE or 0xFFFF) {
                continue;
            }

            if (!legacySource.TryGet<EnvCell>(cellId, out var legacyCell) || legacyCell == null) {
                continue;
            }

            if (!writer.TrySave(legacyCell, iteration)) {
                warnings?.Add($"EnvCell: failed to re-assert cell 0x{cellId:X8} from legacy.");
                continue;
            }

            reasserted++;
        }

        if (reasserted > 0) {
            onProgress?.Invoke($"Re-asserted {reasserted:N0} EnvCell object(s) from legacy.");
        }

        return reasserted;
    }

    private static bool NeedsEnvironmentBspReassert(
        DefaultDatReaderWriter writer,
        uint envId,
        Acme.Dat.Environment legacyEnvironment) {
        if (!TryGetEnvironment(writer, envId, out var exportedEnvironment)) {
            return true;
        }

        foreach (var (structId, legacyStruct) in legacyEnvironment.Cells) {
            if (!exportedEnvironment.Cells.TryGetValue(structId, out var exportedStruct)) {
                return true;
            }

            if (!LegacyBspRetailNormalizer.HasAdequateDrawingPortalCoverage(exportedStruct)) {
                return true;
            }

            if (HasShallowLegacyFallbackBsp(exportedStruct)) {
                return true;
            }
        }

        return false;
    }

    private static bool HasShallowLegacyFallbackBsp(CellStruct cellStruct) {
        if (cellStruct.PhysicsBSP?.Root is { Type: BSPNodeType.BPIN } physicsRoot
            && physicsRoot.PosNode?.Type == BSPNodeType.Leaf
            && physicsRoot.NegNode?.Type == BSPNodeType.Leaf
            && physicsRoot.PosNode.Polygons.Count > 0
            && physicsRoot.PosNode.Polygons.SequenceEqual(physicsRoot.NegNode.Polygons)
            && physicsRoot.PosNode == physicsRoot.NegNode) {
            return true;
        }

        if (cellStruct.CellBSP?.Root is { Type: BSPNodeType.BPIN } cellRoot
            && cellRoot.PosNode?.Type == BSPNodeType.Leaf
            && cellRoot.NegNode?.Type == BSPNodeType.Leaf
            && cellRoot.PosNode.LeafIndex != cellRoot.NegNode.LeafIndex) {
            return true;
        }

        return false;
    }

    private static IEnumerable<uint> EnumerateEnvCellIds(string datDirectory) {
        foreach (string fileName in new[] { "client_cell_1.dat", "cell.dat" }) {
            string path = Path.Combine(datDirectory, fileName);
            if (!File.Exists(path)) {
                continue;
            }

            using var database = new LegacyDatDatabase(path, isCellDatabase: true);
            return database.FileIds
                .Where(id => {
                    ushort cellNum = (ushort)(id & 0xFFFF);
                    return cellNum is not 0xFFFE and not 0xFFFF;
                })
                .OrderBy(id => id)
                .ToArray();
        }

        return Array.Empty<uint>();
    }

    /// <summary>
    /// Legacy DM CellPortal.PolygonId often references doorway polys that are not listed in
    /// CellStruct.Portals. Retail clients resolve interior transitions from that array, so
    /// merge missing doorway polygon IDs without rebuilding preserved legacy BSP trees.
    /// </summary>
    internal static int PatchEnvironmentDrawingPortalRefs(
        DefaultDatReaderWriter writer,
        IReadOnlyList<EnvCell> envCells,
        int? iteration,
        IList<string>? warnings) {
        var portalPolysByStruct = new Dictionary<(uint EnvId, ushort StructId), HashSet<ushort>>();

        foreach (var cell in envCells) {
            var key = (0x0D000000u | cell.EnvironmentId, cell.CellStructure);
            if (!portalPolysByStruct.TryGetValue(key, out var set)) {
                set = new HashSet<ushort>();
                portalPolysByStruct[key] = set;
            }

            foreach (var cp in cell.CellPortals) {
                if (cp.PolygonId != 0) {
                    set.Add((ushort)cp.PolygonId);
                }
            }
        }

        int patchedStructs = 0;
        foreach (var ((envId, structId), polyIds) in portalPolysByStruct) {
            if (!TryGetEnvironment(writer, envId, out var environment)) {
                continue;
            }

            if (!environment.Cells.TryGetValue(structId, out var cellStruct)) {
                continue;
            }

            cellStruct.Portals ??= new List<ushort>();
            var existingPortalPolys = new HashSet<ushort>(cellStruct.Portals);
            bool changed = false;

            foreach (ushort polyId in polyIds.OrderBy(id => id)) {
                if (!cellStruct.Polygons.ContainsKey(polyId)) {
                    continue;
                }

                if (!existingPortalPolys.Add(polyId)) {
                    continue;
                }

                cellStruct.Portals.Add(polyId);
                changed = true;
            }

            if (!changed) {
                continue;
            }

            if (!TryReplaceEnvironment(writer, environment, iteration)) {
                warnings?.Add($"EnvCell: failed to patch environment 0x{envId:X8} struct {structId}.");
                continue;
            }

            patchedStructs++;
        }

        return patchedStructs;
    }

    /// <summary>
    /// Repairs EnvCells whose <see cref="EnvCell.CellStructure"/> points at a struct id missing from
    /// the referenced Environment. This happens when an environment is rewritten from the legacy
    /// decode (which only carries the base structs) after
    /// <see cref="SpecializeSharedCellStructPortalSequences"/> extended it with per-portal-sequence
    /// clones: the cells keep the specialized ids while the variants vanish. The retail client
    /// dereferences <c>structure-&gt;drawing_bsp</c> without a null check
    /// (<c>RenderDeviceD3D::DrawEnvCell</c> @ 0x59F170), so every dangling struct id is a guaranteed
    /// AV the moment the cell renders.
    /// </summary>
    internal static int RestoreMissingCellStructVariants(
        DefaultDatReaderWriter writer,
        IReadOnlyList<EnvCell> envCells,
        int? iteration,
        IList<string>? warnings) {
        int restored = 0;

        foreach (var group in envCells.GroupBy(cell => 0x0D000000u | cell.EnvironmentId)) {
            uint envId = group.Key;
            if (!TryGetEnvironment(writer, envId, out var environment)) {
                continue;
            }

            var missingGroups = group
                .Where(cell => !environment.Cells.ContainsKey(cell.CellStructure))
                .GroupBy(cell => cell.CellStructure)
                .ToList();
            if (missingGroups.Count == 0) {
                continue;
            }

            bool changed = false;
            foreach (var missing in missingGroups) {
                var sequence = missing.First().CellPortals
                    .Select(portal => (ushort)portal.PolygonId)
                    .ToList();
                CellStruct? baseStruct = FindVariantBaseStruct(environment, sequence);
                if (baseStruct == null) {
                    warnings?.Add(
                        $"EnvCell: environment 0x{envId:X8} has no base struct to restore variant {missing.Key}; "
                        + $"{missing.Count()} cell(s) remain dangling.");
                    continue;
                }

                var clone = CloneCellStruct(baseStruct);
                var validSequence = sequence.Where(polyId => clone.Polygons.ContainsKey(polyId)).ToList();
                if (validSequence.Count > 0) {
                    clone.Portals = validSequence;
                }

                LegacyBspRetailNormalizer.EnsureDrawingBspPortalRefs(clone);
                environment.Cells[missing.Key] = clone;
                changed = true;
                restored++;
            }

            if (changed && !TryReplaceEnvironment(writer, environment, iteration)) {
                warnings?.Add($"EnvCell: failed to restore struct variants on environment 0x{envId:X8}.");
            }
        }

        return restored;
    }

    /// <summary>
    /// Guarantees every CellStruct referenced by an EnvCell carries a DrawingBSP. Legacy DM ships a
    /// few empty placeholder environments (no polygons, no drawing tree); the DM client tolerated
    /// them but retail <c>DrawEnvCell</c> reads <c>drawing_bsp</c> unconditionally.
    /// </summary>
    internal static int EnsureCellStructDrawingBsps(
        DefaultDatReaderWriter writer,
        IReadOnlyList<EnvCell> envCells,
        int? iteration,
        IList<string>? warnings) {
        int rebuilt = 0;

        foreach (var group in envCells.GroupBy(cell => 0x0D000000u | cell.EnvironmentId)) {
            uint envId = group.Key;
            if (!TryGetEnvironment(writer, envId, out var environment)) {
                continue;
            }

            bool changed = false;
            var referencedStructs = group.Select(cell => (uint)cell.CellStructure).ToHashSet();
            foreach (var (structId, cellStruct) in environment.Cells) {
                if (!referencedStructs.Contains(structId) || cellStruct.DrawingBSP?.Root != null) {
                    continue;
                }

                if (cellStruct.Polygons is { Count: > 0 }) {
                    LegacyBspRetailNormalizer.RebuildRetailDrawingBsp(cellStruct);
                }
                else {
                    // Empty placeholder struct: a minimal BPOL root renders as nothing but keeps
                    // the client's unconditional drawing_bsp dereference safe.
                    cellStruct.DrawingBSP = new DrawingBSPTree {
                        Root = BspGenerator.DrawingPolygonNode(
                            new List<ushort>(),
                            cellStruct.Polygons ?? new Dictionary<ushort, Polygon>(),
                            cellStruct.VertexArray?.Vertices ?? new Dictionary<ushort, SWVertex>()),
                    };
                }

                if (cellStruct.DrawingBSP?.Root != null) {
                    changed = true;
                    rebuilt++;
                }
            }

            if (changed && !TryReplaceEnvironment(writer, environment, iteration)) {
                warnings?.Add($"EnvCell: failed to persist synthesized DrawingBSP on environment 0x{envId:X8}.");
            }
        }

        return rebuilt;
    }

    private static CellStruct? FindVariantBaseStruct(
        Acme.Dat.Environment environment,
        IReadOnlyList<ushort> portalPolygonIds) {
        if (environment.Cells.Count == 0) {
            return null;
        }

        foreach (var cellStruct in environment.Cells.Values) {
            if (cellStruct.Polygons != null
                && portalPolygonIds.All(polyId => cellStruct.Polygons.ContainsKey(polyId))) {
                return cellStruct;
            }
        }

        return environment.Cells.Values.First();
    }

    /// <summary>
    /// DM environments can reuse one CellStruct across many cells while those cells carry different
    /// CellPortal polygon sequences, including duplicate doorway polys. Retail traversal expects the
    /// per-cell CellPortals array to line up with the referenced CellStruct.Portals array, so shared
    /// structs with divergent portal sequences must be specialized into per-sequence clones.
    /// </summary>
    internal static int SpecializeSharedCellStructPortalSequences(
        DefaultDatReaderWriter writer,
        IReadOnlyList<EnvCell> envCells,
        int? iteration,
        IList<string>? warnings) {
        int specializedVariants = 0;

        foreach (var group in envCells.GroupBy(cell => 0x0D000000u | cell.EnvironmentId)) {
            uint envId = group.Key;
            if (!TryGetEnvironment(writer, envId, out var environment)) {
                continue;
            }

            if (!TrySpecializeSharedCellStructPortalSequences(environment, group, out var changedCells, out int variantsAdded)) {
                continue;
            }

            if (!TryReplaceEnvironment(writer, environment, iteration)) {
                warnings?.Add($"EnvCell: failed to specialize shared portal sequences on environment 0x{envId:X8}.");
                continue;
            }

            foreach (var cell in changedCells) {
                if (!writer.TrySave(cell, iteration)) {
                    warnings?.Add($"EnvCell: failed to save specialized cell 0x{cell.Id:X8}.");
                }
            }

            specializedVariants += variantsAdded;
        }

        return specializedVariants;
    }

    internal static bool TrySpecializeSharedCellStructPortalSequences(
        Acme.Dat.Environment environment,
        IEnumerable<EnvCell> envCells,
        out List<EnvCell> changedCells,
        out int variantsAdded) {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(envCells);

        changedCells = new List<EnvCell>();
        variantsAdded = 0;
        bool changed = false;

        foreach (var structGroup in envCells
                     .Where(cell => cell.CellPortals != null && cell.CellPortals.Count > 0)
                     .GroupBy(cell => cell.CellStructure)
                     .ToList()) {
            if (!environment.Cells.TryGetValue(structGroup.Key, out var cellStruct)
                || cellStruct.Portals == null
                || cellStruct.Portals.Count == 0) {
                continue;
            }

            var candidates = new List<(EnvCell Cell, List<ushort> Sequence, string Signature)>();
            foreach (var cell in structGroup) {
                var sequence = cell.CellPortals
                    .Select(portal => (ushort)portal.PolygonId)
                    .ToList();
                if (sequence.Count == 0) {
                    continue;
                }

                if (sequence.Any(polyId => !cellStruct.Polygons.ContainsKey(polyId))) {
                    continue;
                }

                string signature = string.Join(",", sequence.Select(polyId => polyId.ToString("X4")));
                candidates.Add((cell, sequence, signature));
            }

            if (candidates.Count <= 1) {
                continue;
            }

            var signatureGroups = candidates
                .GroupBy(candidate => candidate.Signature)
                .OrderByDescending(signatureGroup => signatureGroup.Count())
                .ToList();
            if (signatureGroups.Count <= 1) {
                continue;
            }

            List<ushort> currentSequence = ResolvePortalPolygonIds(cellStruct);
            string currentSignature = string.Join(",", currentSequence.Select(polyId => polyId.ToString("X4")));

            string canonicalSignature = signatureGroups
                .Select(signatureGroup => signatureGroup.Key)
                .FirstOrDefault(signature => signature == currentSignature)
                ?? signatureGroups[0].Key;

            var assignedStructIds = new Dictionary<string, ushort>(StringComparer.Ordinal) {
                [canonicalSignature] = structGroup.Key,
            };

            if (canonicalSignature != currentSignature) {
                environment.Cells[structGroup.Key].Portals = signatureGroups
                    .First(groupForSignature => groupForSignature.Key == canonicalSignature)
                    .First()
                    .Sequence
                    .ToList();
                changed = true;
            }

            foreach (var signatureGroup in signatureGroups) {
                string signature = signatureGroup.Key;
                ushort targetStructId;

                if (!assignedStructIds.TryGetValue(signature, out targetStructId)) {
                    targetStructId = AllocateNewCellStructId(environment);
                    var clone = CloneCellStruct(cellStruct);
                    clone.Portals = signatureGroup.First().Sequence.ToList();
                    environment.Cells[targetStructId] = clone;
                    assignedStructIds[signature] = targetStructId;
                    variantsAdded++;
                    changed = true;
                }

                foreach (var candidate in signatureGroup) {
                    if (candidate.Cell.CellStructure == targetStructId) {
                        continue;
                    }

                    candidate.Cell.CellStructure = targetStructId;
                    changedCells.Add(candidate.Cell);
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Retail clients pair <see cref="CellStruct.Portals"/> and <see cref="EnvCell.CellPortals"/>
    /// by array index during portal traversal and rendering. Preserve DM cells with repeated
    /// polygon ids as-is, but reorder the safe one-to-one cases to match the struct ordering.
    /// </summary>
    internal static int ReorderCellPortalsToMatchCellStruct(
        DefaultDatReaderWriter writer,
        IReadOnlyList<EnvCell> envCells,
        ISet<uint>? dirtyCellIds = null) {
        int reordered = 0;

        foreach (var cell in envCells) {
            if (cell.CellPortals == null || cell.CellPortals.Count == 0) {
                continue;
            }

            uint envId = 0x0D000000u | cell.EnvironmentId;
            if (!TryGetEnvironment(writer, envId, out var environment)
                || !environment.Cells.TryGetValue(cell.CellStructure, out var cellStruct)
                || !TryReorderCellPortalsToMatchCellStruct(cell, cellStruct)) {
                continue;
            }

            reordered++;
            dirtyCellIds?.Add(cell.Id);
        }

        return reordered;
    }

    internal static bool TryReorderCellPortalsToMatchCellStruct(EnvCell cell, CellStruct cellStruct) {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(cellStruct);

        if (cell.CellPortals == null || cell.CellPortals.Count == 0 || cellStruct.Portals == null || cellStruct.Portals.Count == 0) {
            return false;
        }

        List<ushort> resolvedPortalIds = ResolvePortalPolygonIds(cellStruct);
        if (resolvedPortalIds.Count != cell.CellPortals.Count) {
            return false;
        }

        var cellPortalIds = cell.CellPortals.Select(cp => (ushort)cp.PolygonId).ToList();
        if (cellPortalIds.Distinct().Count() != cellPortalIds.Count) {
            return false;
        }

        if (!resolvedPortalIds.OrderBy(id => id).SequenceEqual(cellPortalIds.OrderBy(id => id))) {
            return false;
        }

        bool alreadyOrdered = true;
        for (int i = 0; i < resolvedPortalIds.Count; i++) {
            if ((ushort)cell.CellPortals[i].PolygonId != resolvedPortalIds[i]) {
                alreadyOrdered = false;
                break;
            }
        }

        if (alreadyOrdered) {
            return false;
        }

        var portalsByPoly = new Dictionary<ushort, CellPortal>(cell.CellPortals.Count);
        foreach (var portal in cell.CellPortals) {
            portalsByPoly.Add((ushort)portal.PolygonId, portal);
        }

        var ordered = new List<CellPortal>(resolvedPortalIds.Count);
        foreach (ushort polyId in resolvedPortalIds) {
            ordered.Add(portalsByPoly[polyId]);
        }

        cell.CellPortals.Clear();
        cell.CellPortals.AddRange(ordered);
        return true;
    }

    internal static CellStruct CloneCellStruct(CellStruct source) {
        ArgumentNullException.ThrowIfNull(source);
        return DatNativeRecords.CloneMessagePack(source);
    }

    private static ushort AllocateNewCellStructId(Acme.Dat.Environment environment) {
        ushort candidate = environment.Cells.Count == 0
            ? (ushort)0
            : (ushort)(environment.Cells.Keys.Max() + 1);

        while (environment.Cells.ContainsKey(candidate)) {
            if (candidate == ushort.MaxValue) {
                throw new InvalidOperationException("Environment cell-struct id space is exhausted.");
            }

            candidate++;
        }

        return candidate;
    }

    /// <summary>
    /// Restores <see cref="DrawingBSPTree"/> portal refs that retail pack/unpack drops, using
    /// preserved legacy trees when present and synthesizing BPOL portal refs otherwise.
    /// </summary>
    internal static int FixEnvironmentDrawingBspPortalRefs(
        DefaultDatReaderWriter writer,
        IReadOnlyList<EnvCell> envCells,
        int? iteration,
        IList<string>? warnings) {
        int patchedEnvironments = 0;

        foreach (var group in envCells.GroupBy(cell => 0x0D000000u | cell.EnvironmentId)) {
            uint envId = group.Key;
            if (!TryGetEnvironment(writer, envId, out var environment)) {
                continue;
            }

            bool changed = false;
            foreach (ushort structId in group.Select(cell => cell.CellStructure).Distinct()) {
                if (!environment.Cells.TryGetValue(structId, out var cellStruct)) {
                    continue;
                }

                if (LegacyBspRetailNormalizer.EnsureDrawingBspPortalRefs(cellStruct)) {
                    changed = true;
                }
            }

            if (!changed) {
                continue;
            }

            if (!TryReplaceEnvironment(writer, environment, iteration)) {
                warnings?.Add($"EnvCell: failed to repair drawing BSP portals on environment 0x{envId:X8}.");
                continue;
            }

            patchedEnvironments++;
        }

        return patchedEnvironments;
    }

    internal static List<ushort> ResolvePortalPolygonIds(CellStruct cellStruct) {
        if (cellStruct.Portals == null || cellStruct.Portals.Count == 0) {
            return [];
        }

        ushort[]? sortedPolyKeys = null;
        var result = new List<ushort>(cellStruct.Portals.Count);

        foreach (var portalRef in cellStruct.Portals) {
            ushort resolvedId = portalRef;
            if (!cellStruct.Polygons.ContainsKey(resolvedId)) {
                sortedPolyKeys ??= cellStruct.Polygons.Keys.OrderBy(key => key).ToArray();
                int index = portalRef;
                if (index >= 0 && index < sortedPolyKeys.Length) {
                    resolvedId = sortedPolyKeys[index];
                }
            }

            result.Add(resolvedId);
        }

        return result;
    }

    internal static int FixInvalidCellPortalPolygonIds(
        DefaultDatReaderWriter writer,
        IReadOnlyList<EnvCell> envCells,
        ISet<uint>? dirtyCellIds = null) {
        int rewritten = 0;

        foreach (var cell in envCells) {
            uint envId = 0x0D000000u | cell.EnvironmentId;
            if (!TryGetEnvironment(writer, envId, out var environment)) {
                continue;
            }

            if (!environment.Cells.TryGetValue(cell.CellStructure, out var cellStruct)) {
                continue;
            }

            foreach (var cp in cell.CellPortals) {
                if (cp.OtherCellId == 0) {
                    continue;
                }

                if (cellStruct.Polygons.ContainsKey((ushort)cp.PolygonId)) {
                    continue;
                }

                if (cellStruct.Portals.Count == 0) {
                    continue;
                }

                cp.PolygonId = cellStruct.Portals[0];
                rewritten++;
                dirtyCellIds?.Add(cell.Id);
            }
        }

        return rewritten;
    }

    private static bool TryGetEnvironment(
        DefaultDatReaderWriter writer,
        uint envId,
        out Acme.Dat.Environment environment) {
        try {
            return writer.TryGet(envId, out environment) && environment != null;
        }
        catch {
            environment = null!;
            return false;
        }
    }

    private static bool TryReplaceEnvironment(
        DefaultDatReaderWriter writer,
        Acme.Dat.Environment environment,
        int? iteration) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(environment);

        return writer.TrySave(environment, iteration);
    }

    internal static int RewriteOtherPortalIndices(IReadOnlyList<EnvCell> envCells, ISet<uint>? dirtyCellIds = null) {
        var byId = envCells.ToDictionary(cell => cell.Id);
        var byNum = envCells.ToDictionary(cell => (ushort)(cell.Id & 0xFFFF));
        int rewritten = 0;

        foreach (var cell in envCells) {
            ushort cellNum = (ushort)(cell.Id & 0xFFFF);

            foreach (var cp in cell.CellPortals) {
                if (cp.OtherCellId == 0xFFFF) {
                    continue;
                }

                uint otherFullId = (cell.Id & 0xFFFF0000u) | cp.OtherCellId;
                if (!byId.TryGetValue(otherFullId, out var otherCell)
                    && !byNum.TryGetValue(cp.OtherCellId, out otherCell)) {
                    continue;
                }

                // DM data is mixed here: many OtherPortalId values are polygon ids that must be
                // translated to the target cell's portal-array index, but some are already valid
                // retail-style indices. Preserve any existing backlink index before attempting the
                // polygon-id rewrite; otherwise repeated doorway polygons collapse onto the first
                // backlink slot and interior transitions break.
                if (cp.OtherPortalId < otherCell.CellPortals.Count
                    && otherCell.CellPortals[cp.OtherPortalId].OtherCellId == cellNum) {
                    continue;
                }

                int bestIndex = -1;
                int backlinksFound = 0;

                for (int j = 0; j < otherCell.CellPortals.Count; j++) {
                    if (otherCell.CellPortals[j].OtherCellId != cellNum) {
                        continue;
                    }

                    backlinksFound++;
                    if (backlinksFound == 1) {
                        bestIndex = j;
                    }

                    if ((ushort)otherCell.CellPortals[j].PolygonId == cp.OtherPortalId) {
                        bestIndex = j;
                        break;
                    }
                }

                if (bestIndex >= 0) {
                    if (cp.OtherPortalId != (ushort)bestIndex) {
                        cp.OtherPortalId = (ushort)bestIndex;
                        rewritten++;
                        dirtyCellIds?.Add(cell.Id);
                    }
                }
                else if (otherCell.CellPortals.Count > 0
                         && cp.OtherPortalId >= otherCell.CellPortals.Count) {
                    cp.OtherPortalId = 0;
                    rewritten++;
                    dirtyCellIds?.Add(cell.Id);
                }
            }
        }

        return rewritten;
    }

    /// <summary>
    /// Legacy DM stores 0xFFFF in OtherPortalId on outside portals. The retail client treats
    /// that field as a landcell/outdoor index during find_transit_cells, which can snap the
    /// player into the wrong interior cell while world coordinates stay outdoors.
    /// </summary>
    internal static int FixOutsidePortalOtherPortalIds(IReadOnlyList<EnvCell> envCells, ISet<uint>? dirtyCellIds = null) {
        int rewritten = 0;

        foreach (var cell in envCells) {
            foreach (var cp in cell.CellPortals) {
                if (cp.OtherCellId != 0xFFFF) {
                    continue;
                }

                // Valid outdoor OtherPortalId values are landcell ids (0x0001-0x0040) or zero.
                if (cp.OtherPortalId is 0 or >= 0x0001 and <= 0x0040) {
                    continue;
                }

                cp.OtherPortalId = 0;
                rewritten++;
                dirtyCellIds?.Add(cell.Id);
            }
        }

        return rewritten;
    }
}
