using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Guarantees the graphics dependency closure the retail client dereferences without null checks
/// (<c>RenderDeviceD3D::DrawEnvCell</c> surface classify loop, <c>CPhysicsPart::Draw</c>) is fully
/// present in the converted export. Walks every shipped renderable root — interior EnvCell surfaces
/// and static objects, LandBlockInfo statics/buildings, and the CharGen heritage preview graph —
/// then copies any referenced <c>Surface</c> / <c>SurfaceTexture</c> / <c>RenderSurface</c> /
/// <c>Palette</c> / <c>GfxObj</c> / <c>Setup</c> that is missing from <c>client_portal.dat</c> +
/// <c>client_highres.dat</c> from the DM legacy source, applying the same RenderSurface id remap the
/// main conversion uses.
///
/// This is the closure analogue of <see cref="LegacyDatServerWorldCompletion"/>: the DM-client world
/// closure (<see cref="LegacyDatWorldReferenceCollector"/>) does not know about surfaces referenced
/// only by server-imported cell statics or the faithful DM CharGen heritage GfxObjs, so those deps
/// were dropped and produced runtime null surfaces (enter-world / create-character AV at 0x59F203).
/// </summary>
public static class LegacyDatRenderClosureCompletion {
    public sealed class Result {
        public int SurfacesAdded { get; internal set; }
        public int SurfaceTexturesAdded { get; internal set; }
        public int RenderSurfacesAdded { get; internal set; }
        public int PalettesAdded { get; internal set; }
        public int GfxObjsAdded { get; internal set; }
        public int SetupsAdded { get; internal set; }
        public int Unresolved { get; internal set; }
        public List<string> UnresolvedSamples { get; } = [];

        public int TotalAdded =>
            SurfacesAdded + SurfaceTexturesAdded + RenderSurfacesAdded + PalettesAdded + GfxObjsAdded + SetupsAdded;
    }

    private sealed class State {
        public required LegacyDatReader Legacy;
        public required DefaultDatReaderWriter Output;
        public required Result Result;
        public int Iteration;
        public readonly HashSet<uint> Seen = [];

        public bool Present(uint id) =>
            Output.ContainsFile(DatArchive.Portal, id)
            || Output.ContainsFile(DatArchive.Highres, id);
    }

    public static Result Complete(
        string legacyDatDirectory,
        string outputDirectory,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var result = new Result();

        // §8.2: clear stale journal + zero free list before Chorizite writes.
        LegacyDatClientExportFixer.PatchCatalogFilesForWriting(outputDirectory, "client_portal.dat");

        var previousMap = LegacyDatConversionService.ActiveRenderSurfaceIdMap.Value;
        bool installedMap = false;

        using var legacy = new LegacyDatReader(legacyDatDirectory);
        if (previousMap == null) {
            LegacyDatConversionService.ActiveRenderSurfaceIdMap.Value =
                LegacyDatConversionService.BuildRenderSurfaceIdMap(legacy.GetAllIdsOfType<RenderSurface>());
            installedMap = true;
        }

        try {
            using var output = new DefaultDatReaderWriter(
                outputDirectory,
                DatAccessType.ReadWrite,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);

            var state = new State {
                Legacy = legacy,
                Output = output,
                Result = result,
                Iteration = output.GetIteration(DatArchive.Portal),
            };

            onProgress?.Invoke("Render closure: collecting referenced graphics roots from shipped objects...");
            var setupRoots = new HashSet<uint>();
            var gfxRoots = new HashSet<uint>();
            var surfaceRoots = new HashSet<uint>();
            CollectCellRoots(output, surfaceRoots, setupRoots, gfxRoots, onProgress);
            CollectCharGenRoots(output, setupRoots, gfxRoots, surfaceRoots, onProgress);

            // Every GfxObj shipped in the portal can be referenced by a saved player appearance,
            // a heritage preview, or a world static — and the retail client dereferences each part's
            // surfaces without a null check. CharGen appearance-list sanitation may have dropped the
            // entry that referenced a given head/face GfxObj, but the GfxObj itself still ships and a
            // pre-existing character's saved ObjDesc still points at it. So ensure the surface
            // closure of ALL DM GfxObjs, not just currently-referenced roots.
            foreach (var file in output.ListFileIds(DatArchive.Portal)) {
                if ((file >> 24) == 0x01) {
                    gfxRoots.Add(file);
                }
            }

            onProgress?.Invoke(
                $"Render closure: {surfaceRoots.Count:N0} surface root(s), {setupRoots.Count:N0} setup root(s), "
                + $"{gfxRoots.Count:N0} gfxobj root(s); completing dependency graph...");

            int gfxProcessed = 0;
            foreach (uint id in surfaceRoots) {
                EnsureSurface(state, id);
            }

            foreach (uint id in gfxRoots) {
                EnsureGfxObj(state, id);
                if (++gfxProcessed % 1000 == 0) {
                    onProgress?.Invoke($"Render closure: walked {gfxProcessed:N0}/{gfxRoots.Count:N0} GfxObj(s)...");
                }
            }

            foreach (uint id in setupRoots) {
                EnsureSetup(state, id);
            }
        }
        finally {
            if (installedMap) {
                LegacyDatConversionService.ActiveRenderSurfaceIdMap.Value = previousMap;
            }
        }

        onProgress?.Invoke(
            $"Render closure: added {result.SurfacesAdded:N0} Surface, {result.SurfaceTexturesAdded:N0} SurfaceTexture, "
            + $"{result.RenderSurfacesAdded:N0} RenderSurface, {result.PalettesAdded:N0} Palette, "
            + $"{result.GfxObjsAdded:N0} GfxObj, {result.SetupsAdded:N0} Setup; {result.Unresolved} unresolved.");
        return result;
    }

    private static void CollectCellRoots(
        DefaultDatReaderWriter output,
        HashSet<uint> surfaceRoots,
        HashSet<uint> setupRoots,
        HashSet<uint> gfxRoots,
        Action<string>? onProgress) {
        int processed = 0;
        foreach (var file in output.Cell().Catalog().Tree) {
            uint low = file.Id & 0xFFFF;
            if (low is 0xFFFF) {
                continue; // LandBlock terrain, not interior
            }

            if (low == 0xFFFE) {
                if (output.TryGet(file.Id, out LandBlockInfo? lbi) && lbi != null) {
                    foreach (var stab in lbi.Objects) {
                        AddSetupOrGfx(stab.Id, setupRoots, gfxRoots);
                    }

                    foreach (var building in lbi.Buildings) {
                        AddSetupOrGfx(building.ModelId, setupRoots, gfxRoots);
                    }
                }

                continue;
            }

            if (low < 0x0100) {
                continue; // outdoor surface cell
            }

            if (!output.TryGet(file.Id, out EnvCell? cell) || cell == null) {
                continue;
            }

            foreach (ushort surfaceLow in cell.Surfaces) {
                surfaceRoots.Add(0x08000000u | surfaceLow);
            }

            foreach (var stab in cell.StaticObjects) {
                AddSetupOrGfx(stab.Id, setupRoots, gfxRoots);
            }

            if (++processed % 100000 == 0) {
                onProgress?.Invoke($"Render closure: scanned {processed:N0} cells for roots...");
            }
        }
    }

    private static void CollectCharGenRoots(
        DefaultDatReaderWriter output,
        HashSet<uint> setupRoots,
        HashSet<uint> gfxRoots,
        HashSet<uint> surfaceRoots,
        Action<string>? onProgress) {
        if (!output.TryGet(LegacyDatCharGenMapper.CharGenId, out CharGen? charGen) || charGen == null) {
            return;
        }

        foreach (var group in charGen.HeritageGroups.Values) {
            AddSetupOrGfx(group.SetupId, setupRoots, gfxRoots);
            AddSetupOrGfx(group.EnvironmentSetupId, setupRoots, gfxRoots);
            foreach (var sex in group.Genders.Values) {
                AddSetupOrGfx(sex.SetupId, setupRoots, gfxRoots);
                CollectObjDescRoots(sex.BaseObjDesc, gfxRoots);
                foreach (var hair in sex.HairStyles) {
                    CollectObjDescRoots(hair.ObjDesc, gfxRoots);
                }

                foreach (var eye in sex.EyeStrips) {
                    CollectObjDescRoots(eye.ObjDesc, gfxRoots);
                    CollectObjDescRoots(eye.BaldObjDesc, gfxRoots);
                }

                foreach (var nose in sex.NoseStrips) {
                    CollectObjDescRoots(nose.ObjDesc, gfxRoots);
                }

                foreach (var mouth in sex.MouthStrips) {
                    CollectObjDescRoots(mouth.ObjDesc, gfxRoots);
                }
            }
        }
    }

    private static void CollectObjDescRoots(ObjDesc objDesc, HashSet<uint> gfxRoots) {
        if (objDesc == null) {
            return;
        }

        foreach (var part in objDesc.AnimPartChanges) {
            if ((part.PartId >> 24) == 0x01) {
                gfxRoots.Add(part.PartId);
            }
        }
    }

    private static void AddSetupOrGfx(uint id, HashSet<uint> setupRoots, HashSet<uint> gfxRoots) {
        switch (id >> 24) {
            case 0x02:
                setupRoots.Add(id);
                break;
            case 0x01:
                gfxRoots.Add(id);
                break;
        }
    }

    private static void EnsureSetup(State state, uint id) {
        if (id == 0 || (id >> 24) != 0x02 || !state.Seen.Add(id)) {
            return;
        }

        if (!state.Present(id)) {
            if (state.Legacy.TryGet<Setup>(id, out var setup) && setup != null) {
                setup.Id = id;
                if (state.Output.TrySave(setup, state.Iteration)) {
                    state.Result.SetupsAdded++;
                }

                foreach (var part in setup.Parts) {
                    EnsureGfxObj(state, part);
                }

                return;
            }

            Unresolved(state, "setup", id);
            return;
        }

        // Present already: still walk its parts so their surfaces are covered.
        if (state.Output.ContainsFile(DatArchive.Portal, id)
            && state.Output.TryGet<Setup>(id, out var existing) && existing != null) {
            foreach (var part in existing.Parts) {
                EnsureGfxObj(state, part);
            }
        }
    }

    private static void EnsureGfxObj(State state, uint id) {
        if (id == 0 || (id >> 24) != 0x01 || !state.Seen.Add(id)) {
            return;
        }

        GfxObj? gfx = null;
        if (!state.Present(id)) {
            if (state.Legacy.TryGet<GfxObj>(id, out gfx) && gfx != null) {
                gfx.Id = id;
                if (state.Output.TrySave(gfx, state.Iteration)) {
                    state.Result.GfxObjsAdded++;
                }
            }
            else {
                // Genuinely absent from DM and retail (collision/position stubs the DM client
                // portal never shipped). The retail client dereferences static-object and
                // setup-part GfxObjs without a null check, so write a minimal empty no-draw
                // placeholder (Flags=0, empty vertex array) to keep the reference resolvable.
                if (WritePlaceholderGfxObj(state, id)) {
                    state.Result.GfxObjsAdded++;
                }
                else {
                    Unresolved(state, "gfxobj", id);
                }

                return;
            }
        }
        else if (state.Output.ContainsFile(DatArchive.Portal, id)) {
            state.Output.TryGet<GfxObj>(id, out gfx);
        }

        if (gfx != null) {
            foreach (var surface in gfx.Surfaces) {
                EnsureSurface(state, surface);
            }
        }
    }

    private static bool WritePlaceholderGfxObj(State state, uint id) {
        var placeholder = new GfxObj {
            Id = id,
            Flags = 0,
            VertexArray = new VertexArray { VertexType = 0 },
            SortCenter = default,
        };
        return state.Output.TrySave(placeholder, state.Iteration);
    }

    private static void EnsureSurface(State state, uint id) {
        if (id == 0 || (id >> 24) != 0x08 || !state.Seen.Add(id)) {
            return;
        }

        Surface? surface = null;
        if (!state.Present(id)) {
            if (state.Legacy.TryGet<Surface>(id, out surface) && surface != null) {
                surface.Id = id;
                if (state.Output.TrySave(surface, state.Iteration)) {
                    state.Result.SurfacesAdded++;
                }
            }
            else {
                Unresolved(state, "surface", id);
                return;
            }
        }
        else if (state.Output.ContainsFile(DatArchive.Portal, id)) {
            state.Output.TryGet<Surface>(id, out surface);
        }

        if (surface == null) {
            return;
        }

        if (surface.OrigPaletteId != 0) {
            EnsurePalette(state, surface.OrigPaletteId);
        }

        if (surface.OrigTextureId != 0) {
            EnsureSurfaceTexture(state, surface.OrigTextureId);
        }
    }

    private static void EnsureSurfaceTexture(State state, uint id) {
        if (id == 0 || (id >> 24) != 0x05 || !state.Seen.Add(id)) {
            return;
        }

        SurfaceTexture? surfaceTexture = null;
        if (!state.Present(id)) {
            if (state.Legacy.TryGet<SurfaceTexture>(id, out surfaceTexture) && surfaceTexture != null) {
                LegacyDatConversionService.RemapSurfaceTextureRenderSurfaceIdsForRetail(surfaceTexture);
                surfaceTexture.Id = id;
                if (state.Output.TrySave(surfaceTexture, state.Iteration)) {
                    state.Result.SurfaceTexturesAdded++;
                }
            }
            else {
                Unresolved(state, "surftex", id);
                return;
            }
        }
        else if (state.Output.ContainsFile(DatArchive.Portal, id)) {
            state.Output.TryGet<SurfaceTexture>(id, out surfaceTexture);
        }

        if (surfaceTexture == null) {
            return;
        }

        // DM stores the bitmap inside the SurfaceTexture; the reader splits out a synthetic
        // RenderSurface keyed off the SurfaceTexture id. Ensure both the remapped Textures[] ids
        // and that synthetic source resolve.
        foreach (var renderSurfaceId in surfaceTexture.Textures) {
            EnsureRenderSurface(state, renderSurfaceId, id);
        }
    }

    private static void EnsureRenderSurface(State state, uint retailId, uint sourceSurfaceTextureId) {
        if (retailId == 0 || !state.Seen.Add(retailId)) {
            return;
        }

        if (state.Present(retailId)) {
            return; // resolvable from portal or highres
        }

        // The DM render surface lives at the synthetic id derived from the source SurfaceTexture.
        uint syntheticId = LegacyDatDecoders.CreateSyntheticRenderSurfaceId(sourceSurfaceTextureId);
        if (state.Legacy.TryGet<RenderSurface>(syntheticId, out var renderSurface) && renderSurface != null) {
            LegacyDatConversionService.RemapRenderSurfaceForRetail(renderSurface);
            renderSurface.Id = LegacyDatConversionService.ConvertRenderSurfaceIdToRetail(syntheticId);
            if (state.Output.TrySave(renderSurface, state.Iteration)) {
                state.Result.RenderSurfacesAdded++;
            }

            if (renderSurface.DefaultPaletteId != 0) {
                EnsurePalette(state, renderSurface.DefaultPaletteId);
            }

            return;
        }

        Unresolved(state, "rendersurface", retailId);
    }

    private static void EnsurePalette(State state, uint id) {
        if (id == 0 || (id >> 24) != 0x04 || !state.Seen.Add(id)) {
            return;
        }

        if (state.Present(id)) {
            return;
        }

        if (state.Legacy.TryGet<Palette>(id, out var palette) && palette != null) {
            palette.Id = id;
            if (state.Output.TrySave(palette, state.Iteration)) {
                state.Result.PalettesAdded++;
            }

            return;
        }

        Unresolved(state, "palette", id);
    }

    private static void Unresolved(State state, string kind, uint id) {
        state.Result.Unresolved++;
        if (state.Result.UnresolvedSamples.Count < 24) {
            state.Result.UnresolvedSamples.Add($"{kind} 0x{id:X8}");
        }
    }
}
