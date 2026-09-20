using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Region terrain uses stable retail <c>0x05……</c> SurfaceTexture IDs shared with the seed.
/// Full DM export must replace those slots with converted DM payloads, not leave retail stubs.
/// </summary>
public static class LegacyDatRegionTerrainPortalExporter {
    public static HashSet<uint> CollectTerrainSurfaceTextureIds(Region region) {
        ArgumentNullException.ThrowIfNull(region);

        var ids = new HashSet<uint>();
        var texMerge = region.TerrainInfo.LandSurfaces.TexMerge;

        void TrackSurfaceTexture(uint surfaceTextureId) {
            if (surfaceTextureId != 0) {
                ids.Add(surfaceTextureId);
            }
        }

        foreach (TMTerrainDesc desc in texMerge.TerrainDesc) {
            TrackSurfaceTexture(desc.TerrainTex.TextureId);
            TrackSurfaceTexture(desc.TerrainTex.DetailTextureId);
        }

        foreach (RoadAlphaMap overlay in texMerge.RoadMaps) {
            TrackSurfaceTexture(overlay.TextureId);
        }

        foreach (TerrainAlphaMap overlay in texMerge.CornerTerrainMaps) {
            TrackSurfaceTexture(overlay.TextureId);
        }

        foreach (TerrainAlphaMap overlay in texMerge.SideTerrainMaps) {
            TrackSurfaceTexture(overlay.TextureId);
        }

        return ids;
    }

    public static int EnsureFromLegacy(
        LegacyDatReader source,
        DefaultDatReaderWriter writer,
        int? iteration,
        Action<string>? onProgress = null) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writer);

        if (!source.TryGet<Region>(0x13000000, out Region? region) && !source.TryGet<Region>(0x130F0000, out region)) {
            return 0;
        }

        uint[] surfaceTextureIds = CollectTerrainSurfaceTextureIds(region!).OrderBy(id => id).ToArray();

        onProgress?.Invoke(
            $"Ensuring region terrain portal assets ({surfaceTextureIds.Length:N0} SurfaceTexture slot(s))...");

        int written = 0;
        foreach (uint surfaceTextureId in surfaceTextureIds) {
            uint syntheticRenderSurfaceId = LegacyDatDecoders.CreateSyntheticRenderSurfaceId(surfaceTextureId);
            if (LegacyDatConversionService.TryCopyRenderSurfaceForExport(source, writer, syntheticRenderSurfaceId, iteration)) {
                written++;
            }

            if (LegacyDatConversionService.TryCopySurfaceTextureForExport(source, writer, surfaceTextureId, iteration)) {
                written++;
            }
        }

        return written;
    }

    /// <summary>Retail seed stores terrain <c>SurfaceTexture</c> as a short pointer record; DM legacy embeds pixels inline.</summary>
    internal static bool IsLikelyRetailSurfaceTextureStub(byte[] bytes) =>
        bytes.Length > 0 && bytes.Length <= 32;

    /// <summary>
    /// For DM-only world, write the decoded DM Region to <c>0x13000000</c> as the FINAL portal step so it
    /// wins over the retail seed Region. The Full-DM overlay redeploy copies the full retail seed (incl. its
    /// Region) and only overlays selected converted payloads, leaving the retail Region's terrain descriptors
    /// (wrong tiling + retail-only terrain-type ordering) on top of DM <c>LandBlock</c> data. DM uses terrain
    /// types retail lacks (Argila/Olthoi/Volcano/etc.), so the retail Region maps DM cells to the wrong
    /// textures. <c>DM_ONLY_MAPPING.md</c> requires Region = DM (legacy <c>0x130F0000</c> aliased to
    /// <c>0x13000000</c>). The DM Region's <c>0x05……</c> SurfaceTexture refs resolve to the converted DM
    /// SurfaceTextures already written, so no remap is needed here.
    /// </summary>
    public static bool EnsureDmRegionWins(
        LegacyDatReader source,
        string outputDirectory,
        int? iteration,
        Action<string>? onProgress = null) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!source.TryGet<Region>(0x13000000, out var dmRegion) || dmRegion == null) {
            return false;
        }

        onProgress?.Invoke("Writing DM Region 0x13000000 last so the DM terrain recipe wins over the retail seed...");

        using var writer = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        // TrySave replaces the retail seed Region entry in place; no delete-before-write
        // (Tree.TryDelete corrupts B-tree ordering — see LegacyDatCatalogEntryReplacer).
        return writer.TrySave(dmRegion, iteration);
    }

    /// <summary>
    /// Full-DM overlay redeploy copies the retail seed portal catalog, then re-overlays converted payloads
    /// byte-for-byte. That works for most objects, but some shared retail IDs (notably heritage
    /// <c>Setup</c> slots and large shell chains) fail to stick via raw-byte overlay — the export keeps
    /// retail <c>Setup</c>/<c>GfxObj</c> bytes even though the main conversion pass decoded DM art.
    /// Re-serialize every DM <c>Setup</c> and <c>GfxObj</c> from the legacy reader after redeploy
    /// (in-place replace) so world statics/buildings render DM models on the retail client.
    /// </summary>
    public static int EnsureDmWorldPortalObjectsWin(
        LegacyDatReader source,
        string outputDirectory,
        int? iteration,
        Action<string>? onProgress = null) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        onProgress?.Invoke("Re-asserting DM Setup and GfxObj portal objects after overlay redeploy...");

        using var writer = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        int setupWritten = EnsureAllFromLegacy<Setup>(source, writer, iteration, onProgress);
        int gfxWritten = EnsureAllFromLegacy<GfxObj>(source, writer, iteration, onProgress);

        onProgress?.Invoke(
            $"DM world portal objects: wrote {setupWritten:N0} Setup(s) and {gfxWritten:N0} GfxObj(s).");
        return setupWritten + gfxWritten;
    }

    static int EnsureAllFromLegacy<T>(
        LegacyDatReader source,
        DefaultDatReaderWriter writer,
        int? iteration,
        Action<string>? onProgress) where T : class, Acme.Dat.IDatRecord, new() {
        var ids = source.GetAllIdsOfType<T>().ToList();
        if (ids.Count == 0) {
            return 0;
        }

        string label = typeof(T).Name;
        int written = 0;
        for (int i = 0; i < ids.Count; i++) {
            uint id = ids[i];
            if (i == 0 || (i + 1) % 2000 == 0 || i + 1 == ids.Count) {
                onProgress?.Invoke($"  {label}: {i + 1:N0}/{ids.Count:N0}...");
            }

            if (!source.TryGet<T>(id, out var obj) || obj == null) {
                continue;
            }

            if (writer.TrySave(obj, iteration)) {
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// After overlay redeploy, re-assert DM world material objects (Surface, SurfaceTexture,
    /// RenderSurface, Palette) referenced by the world closure. Shared retail terrain/scenery
    /// slots are left to <see cref="EnsureDmSharedOutdoorTextureSlotsWin"/>, but shared world
    /// material IDs must allow the DM payload to win over the retail seed.
    /// </summary>
    public static int EnsureDmWorldReferencedMaterialsWin(
        LegacyDatReader source,
        string retailSeedDirectory,
        string outputDirectory,
        int? iteration,
        Action<string>? onProgress = null) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        onProgress?.Invoke("Collecting DM world material closure for portal material restore...");
        LegacyDatWorldReferenceClosure worldClosure = LegacyDatWorldReferenceCollector.Collect(source, onProgress);

        var previousRenderSurfaceMap = LegacyDatConversionService.ActiveRenderSurfaceIdMap.Value;
        bool installedRenderSurfaceMap = false;
        if (previousRenderSurfaceMap == null) {
            LegacyDatConversionService.ActiveRenderSurfaceIdMap.Value = LegacyDatConversionService.BuildRenderSurfaceIdMap(
                source.GetAllIdsOfType<RenderSurface>());
            installedRenderSurfaceMap = true;
        }

        int written;
        try {
            using var seed = new DefaultDatReaderWriter(
                retailSeedDirectory,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            using var writer = new DefaultDatReaderWriter(
                outputDirectory,
                DatAccessType.ReadWrite,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            var seedFileTemplates = seed.Portal().Catalog().Tree.ToDictionary(file => file.Id);
            int portalIteration = iteration ?? writer.GetIteration(DatArchive.Portal);

            written = EnsureClosureMaterials<Surface>(
                source,
                writer,
                seedFileTemplates,
                worldClosure,
                "Surface",
                portalIteration,
                onProgress);
            written += EnsureClosureSurfaceTextures(
                source,
                writer,
                seedFileTemplates,
                worldClosure,
                portalIteration,
                onProgress);
            written += EnsureClosureRenderSurfaces(
                source,
                writer,
                seedFileTemplates,
                worldClosure,
                portalIteration,
                onProgress);
            written += EnsureClosureMaterials<Palette>(
                source,
                writer,
                seedFileTemplates,
                worldClosure,
                "Palette",
                portalIteration,
                onProgress);
        }
        finally {
            if (installedRenderSurfaceMap) {
                LegacyDatConversionService.ActiveRenderSurfaceIdMap.Value = previousRenderSurfaceMap;
            }
        }

        if (written > 0) {
            string retailPortalPath = Path.Combine(retailSeedDirectory, "client_portal.dat");
            string exportPortalPath = Path.Combine(outputDirectory, "client_portal.dat");
            LegacyDatClientExportFixer.PreserveRetailPortalBtreeRoot(retailPortalPath, exportPortalPath);
            LegacyDatRetailShellBootstrap.SyncHeaderFileSize(exportPortalPath);
            DatExportFixer.PatchFreeBlocksBeforeExport(exportPortalPath);
        }

        onProgress?.Invoke($"DM world material restore: wrote {written:N0} Surface/SurfaceTexture/RenderSurface/Palette object(s).");
        return written;
    }

    static int EnsureClosureMaterials<T>(
        LegacyDatReader source,
        DefaultDatReaderWriter writer,
        IReadOnlyDictionary<uint, DatBTreeFile> seedFileTemplates,
        LegacyDatWorldReferenceClosure closure,
        string closureType,
        int portalIteration,
        Action<string>? onProgress) where T : class, Acme.Dat.IDatRecord, new() {
        if (!closure.IdsByType.TryGetValue(closureType, out HashSet<uint>? trackedIds) || trackedIds.Count == 0) {
            return 0;
        }

        var ids = trackedIds.OrderBy(id => id).ToList();
        string label = typeof(T).Name;
        int written = 0;
        for (int i = 0; i < ids.Count; i++) {
            uint id = ids[i];
            if (i == 0 || (i + 1) % 2000 == 0 || i + 1 == ids.Count) {
                onProgress?.Invoke($"  Restoring {label}: {i + 1:N0}/{ids.Count:N0}...");
            }

            if (!source.TryGet<T>(id, out T? obj) || obj == null) {
                continue;
            }

            byte[] bytes = PackPortalObject(obj);
            if (LegacyDatPortalCatalogDeduplicator.TryReplacePortalPayload(
                writer.Portal(),
                id,
                bytes,
                portalIteration,
                seedFileTemplates)) {
                written++;
            }
        }

        return written;
    }

    static int EnsureClosureSurfaceTextures(
        LegacyDatReader source,
        DefaultDatReaderWriter writer,
        IReadOnlyDictionary<uint, DatBTreeFile> seedFileTemplates,
        LegacyDatWorldReferenceClosure closure,
        int portalIteration,
        Action<string>? onProgress) {
        if (!closure.IdsByType.TryGetValue("SurfaceTexture", out HashSet<uint>? trackedIds) || trackedIds.Count == 0) {
            return 0;
        }

        var ids = trackedIds.OrderBy(id => id).ToList();
        int written = 0;
        for (int i = 0; i < ids.Count; i++) {
            uint id = ids[i];
            if (i == 0 || (i + 1) % 2000 == 0 || i + 1 == ids.Count) {
                onProgress?.Invoke($"  Restoring SurfaceTexture: {i + 1:N0}/{ids.Count:N0}...");
            }

            if (!source.TryGet<SurfaceTexture>(id, out SurfaceTexture? surfaceTexture) || surfaceTexture == null) {
                continue;
            }

            LegacyDatConversionService.RemapSurfaceTextureRenderSurfaceIdsForRetail(surfaceTexture);
            byte[] bytes = PackPortalObject(surfaceTexture);
            if (LegacyDatPortalCatalogDeduplicator.TryReplacePortalPayload(
                writer.Portal(),
                id,
                bytes,
                portalIteration,
                seedFileTemplates)) {
                written++;
            }
        }

        return written;
    }

    static int EnsureClosureRenderSurfaces(
        LegacyDatReader source,
        DefaultDatReaderWriter writer,
        IReadOnlyDictionary<uint, DatBTreeFile> seedFileTemplates,
        LegacyDatWorldReferenceClosure closure,
        int portalIteration,
        Action<string>? onProgress) {
        if (!closure.IdsByType.TryGetValue("RenderSurface", out HashSet<uint>? trackedIds) || trackedIds.Count == 0) {
            return 0;
        }

        var ids = trackedIds.OrderBy(id => id).ToList();
        int written = 0;
        for (int i = 0; i < ids.Count; i++) {
            uint syntheticId = ids[i];
            if (i == 0 || (i + 1) % 2000 == 0 || i + 1 == ids.Count) {
                onProgress?.Invoke($"  Restoring RenderSurface: {i + 1:N0}/{ids.Count:N0}...");
            }

            if (!source.TryGet<RenderSurface>(syntheticId, out RenderSurface? renderSurface) || renderSurface == null) {
                continue;
            }

            LegacyDatConversionService.RemapRenderSurfaceForRetail(renderSurface);
            if (writer.TryGet<RenderSurface>(renderSurface.Id, out RenderSurface? existing) && existing != null) {
                renderSurface = LegacyDatConversionService.OverlayRenderSurfaceOntoRetail(existing, renderSurface);
                renderSurface.Id = LegacyDatConversionService.ConvertRenderSurfaceIdToRetail(syntheticId);
            }

            byte[] bytes = PackPortalObject(renderSurface);
            if (LegacyDatPortalCatalogDeduplicator.TryReplacePortalPayload(
                writer.Portal(),
                renderSurface.Id,
                bytes,
                portalIteration,
                seedFileTemplates)) {
                written++;
            }
        }

        return written;
    }

    public static int EnsureDmSharedOutdoorTextureSlotsWin(
        LegacyDatReader source,
        string retailSeedDirectory,
        string outputDirectory,
        int? iteration,
        Action<string>? onProgress) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

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

        HashSet<uint> surfaceTextureIds = CollectOutdoorSharedSurfaceTextureIds(source, seed, onProgress);
        if (surfaceTextureIds.Count == 0) {
            return 0;
        }

        int portalIteration = output.GetIteration(DatArchive.Portal);
        var seedFileTemplates = seed.Portal().Catalog().Tree.ToDictionary(file => file.Id);
        int restoredSurfaceTextures = 0;
        int restoredRenderSurfaces = 0;

        onProgress?.Invoke($"Rebinding DM outdoor/region texture slots onto retail portal IDs ({surfaceTextureIds.Count:N0} SurfaceTexture slot(s))...");
        int processed = 0;
        foreach (uint surfaceTextureId in surfaceTextureIds.OrderBy(id => id)) {
            processed++;
            if (processed == 1 || processed % 1000 == 0 || processed == surfaceTextureIds.Count) {
                onProgress?.Invoke($"  Outdoor texture slots: {processed:N0}/{surfaceTextureIds.Count:N0}...");
            }

            if (!seed.TryGet<SurfaceTexture>(surfaceTextureId, out SurfaceTexture? retailSurfaceTexture)
                || retailSurfaceTexture == null
                || retailSurfaceTexture.Textures.Count == 0) {
                continue;
            }

            uint syntheticRenderSurfaceId = LegacyDatDecoders.CreateSyntheticRenderSurfaceId(surfaceTextureId);
            if (!source.TryGet<RenderSurface>(syntheticRenderSurfaceId, out RenderSurface? dmRenderSurface)
                || dmRenderSurface == null) {
                continue;
            }

            foreach (var renderSurfaceRef in retailSurfaceTexture.Textures) {
                uint retailRenderSurfaceId = renderSurfaceRef;
                var replacement = CloneRenderSurfaceForRetailSlot(dmRenderSurface, retailRenderSurfaceId, output);
                byte[] renderSurfaceBytes = PackPortalObject(replacement);
                if (LegacyDatPortalCatalogDeduplicator.TryReplacePortalPayload(
                    output.Portal(),
                    retailRenderSurfaceId,
                    renderSurfaceBytes,
                    portalIteration,
                    seedFileTemplates)) {
                    restoredRenderSurfaces++;
                }
            }

            if (seed.TryGetFileBytes(DatArchive.Portal, surfaceTextureId, out byte[]? retailSurfaceTextureBytes)
                && retailSurfaceTextureBytes != null
                && LegacyDatPortalCatalogDeduplicator.TryReplacePortalPayload(
                    output.Portal(),
                    surfaceTextureId,
                    retailSurfaceTextureBytes,
                    portalIteration,
                    seedFileTemplates)) {
                restoredSurfaceTextures++;
            }
        }

        onProgress?.Invoke(
            $"DM outdoor/region texture slots: restored {restoredSurfaceTextures:N0} SurfaceTexture slot(s) and {restoredRenderSurfaces:N0} retail RenderSurface slot(s).");
        return restoredSurfaceTextures + restoredRenderSurfaces;
    }

    static HashSet<uint> CollectOutdoorSharedSurfaceTextureIds(
        LegacyDatReader source,
        IDatReaderWriter seedPortal,
        Action<string>? onProgress) {
        LegacyDatWorldReferenceClosure closure = LegacyDatWorldReferenceCollector.CreateSeed(source);
        foreach (uint landBlockInfoId in source.GetLandBlockInfoIds()) {
            if (source.TryGet<LandBlockInfo>(landBlockInfoId, out LandBlockInfo? landBlockInfo) && landBlockInfo != null) {
                LegacyDatWorldReferenceCollector.TrackLandBlockInfo(closure, landBlockInfo);
            }
        }

        LegacyDatWorldReferenceCollector.ExpandPending(source, closure, onProgress);
        if (!closure.IdsByType.TryGetValue("SurfaceTexture", out HashSet<uint>? trackedIds) || trackedIds.Count == 0) {
            return new HashSet<uint>();
        }

        return trackedIds
            .Where(id => seedPortal.Catalog().Tree.HasFile(id))
            .ToHashSet();
    }

    static RenderSurface CloneRenderSurfaceForRetailSlot(
        RenderSurface dmRenderSurface,
        uint retailRenderSurfaceId,
        DefaultDatReaderWriter output) {
        ArgumentNullException.ThrowIfNull(dmRenderSurface);
        ArgumentNullException.ThrowIfNull(output);

        var clone = new RenderSurface {
            Id = dmRenderSurface.Id,
            Width = dmRenderSurface.Width,
            Height = dmRenderSurface.Height,
            Format = dmRenderSurface.Format,
            DefaultPaletteId = dmRenderSurface.DefaultPaletteId,
            SourceData = dmRenderSurface.SourceData?.ToArray() ?? Array.Empty<byte>(),
        };
        LegacyDatConversionService.RemapRenderSurfaceForRetail(clone);
        clone.Id = retailRenderSurfaceId;

        if (output.TryGet<RenderSurface>(retailRenderSurfaceId, out RenderSurface? existing) && existing != null) {
            clone = LegacyDatConversionService.OverlayRenderSurfaceOntoRetail(existing, clone);
            clone.Id = retailRenderSurfaceId;
        }

        return clone;
    }

    static byte[] PackPortalObject<T>(T obj) where T : class, IDatRecord, new() =>
        DatNativeRecords.Pack(obj);
}
