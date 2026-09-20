using Acme.Dat;
using System.Diagnostics;

namespace WorldBuilder.Shared.Lib {
    /// <summary>
    /// Computes the portal.dat object IDs referenced by legacy world cell data.
    /// Used by <see cref="LegacyDatExportMode.SlimMerge"/> to avoid converting the entire DM catalog.
    /// </summary>
    public sealed class LegacyDatWorldReferenceClosure {
        private readonly Dictionary<string, HashSet<uint>> _idsByType = new(StringComparer.Ordinal);
        private readonly Queue<(string Type, uint Id)> _pending = new();

        internal int PendingCount => _pending.Count;

        internal LegacyDatWorldReferenceClosure() { }

        public IReadOnlyDictionary<string, HashSet<uint>> IdsByType => _idsByType;

        public int TotalPortalObjects => _idsByType.Values.Sum(set => set.Count);

        public bool Contains(string objectType, uint id) {
            return _idsByType.TryGetValue(objectType, out var ids) && ids.Contains(id);
        }

        internal void TrackDirect(string objectType, uint id) {
            if (id == 0 || Contains(objectType, id)) {
                return;
            }

            if (!_idsByType.TryGetValue(objectType, out var ids)) {
                ids = new HashSet<uint>();
                _idsByType[objectType] = ids;
            }

            ids.Add(id);
            _pending.Enqueue((objectType, id));
        }

        internal (string Type, uint Id) DequeuePending() {
            return _pending.Dequeue();
        }
    }

    public static class LegacyDatWorldReferenceCollector {
        public static LegacyDatWorldReferenceClosure CreateSeed(LegacyDatReader source) {
            ArgumentNullException.ThrowIfNull(source);

            var closure = new LegacyDatWorldReferenceClosure();
            foreach (uint regionId in source.GetAllIdsOfType<Region>()) {
                closure.TrackDirect("Region", regionId);
            }

            return closure;
        }

        public static void TrackLandBlockInfo(LegacyDatWorldReferenceClosure closure, LandBlockInfo landBlockInfo) {
            ArgumentNullException.ThrowIfNull(closure);
            ArgumentNullException.ThrowIfNull(landBlockInfo);

            foreach (Stab stab in landBlockInfo.Objects) {
                TrackPortalObjectId(closure, stab.Id);
            }

            foreach (BuildingInfo building in landBlockInfo.Buildings) {
                TrackPortalObjectId(closure, building.ModelId);
            }
        }

        public static void TrackEnvCell(LegacyDatWorldReferenceClosure closure, EnvCell envCell) {
            ArgumentNullException.ThrowIfNull(closure);
            ArgumentNullException.ThrowIfNull(envCell);

            if (envCell.EnvironmentId != 0) {
                closure.TrackDirect("Environment", LegacyDatPortalFileIds.Environment(envCell.EnvironmentId));
            }

            foreach (uint surfaceId in envCell.Surfaces) {
                closure.TrackDirect("Surface", LegacyDatPortalFileIds.Surface(surfaceId));
            }

            foreach (Stab stab in envCell.StaticObjects) {
                TrackPortalObjectId(closure, stab.Id);
            }
        }

        public static void ExpandPending(
            LegacyDatReader source,
            LegacyDatWorldReferenceClosure closure,
            Action<string>? onProgress = null) {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(closure);

            var stopwatch = Stopwatch.StartNew();
            long lastReportMs = 0;
            int expanded = 0;
            while (closure.PendingCount > 0) {
                (string type, uint id) = DequeuePending(closure);
                type = NormalizePortalObjectType(type, id);
                expanded++;
                if (LegacyConvertProgress.ShouldReport(expanded, stopwatch.ElapsedMilliseconds, ref lastReportMs)) {
                    onProgress?.Invoke(
                        $"Expanding {type} 0x{id:X8} ({expanded:N0} processed, {closure.TotalPortalObjects:N0} tracked, {closure.PendingCount:N0} pending)...");
                }

                switch (type) {
                    case "Setup":
                        ExpandSetup(source, closure, id);
                        break;
                    case "GfxObj":
                        ExpandGfxObj(source, closure, id);
                        break;
                    case "Surface":
                        ExpandSurface(source, closure, id);
                        break;
                    case "SurfaceTexture":
                        ExpandSurfaceTexture(closure, id);
                        break;
                    case "RenderSurface":
                        // Synthetic render surfaces are tracked from SurfaceTexture IDs.
                        // Palettes are tracked from Surface expansion.
                        break;
                    case "Environment":
                        // EnvCell surfaces are tracked directly; Environment geometry uses those indices.
                        break;
                    case "Region":
                        ExpandRegion(source, closure, id);
                        break;
                }
            }
        }

        /// <summary>
        /// Full scan used by tests and diagnostics. Production slim merge tracks references while converting cell.dat.
        /// </summary>
        public static LegacyDatWorldReferenceClosure Collect(LegacyDatReader source, Action<string>? onProgress = null) {
            ArgumentNullException.ThrowIfNull(source);

            var closure = CreateSeed(source);

            IReadOnlyList<uint> landBlockInfoIds = source.GetLandBlockInfoIds();
            for (int i = 0; i < landBlockInfoIds.Count; i++) {
                if (source.TryGet<LandBlockInfo>(landBlockInfoIds[i], out var landBlockInfo) && landBlockInfo != null) {
                    TrackLandBlockInfo(closure, landBlockInfo);
                }
            }

            IReadOnlyList<uint> envCellIds = source.GetEnvCellIds();
            for (int i = 0; i < envCellIds.Count; i++) {
                if (source.TryGet<EnvCell>(envCellIds[i], out var envCell) && envCell != null) {
                    TrackEnvCell(closure, envCell);
                }
            }

            ExpandPending(source, closure, onProgress);
            onProgress?.Invoke($"World reference scan complete ({closure.TotalPortalObjects:N0} portal object(s)).");
            return closure;
        }

        private static void TrackPortalObjectId(LegacyDatWorldReferenceClosure closure, uint id) {
            switch (id >> 24) {
                case 0x01:
                    closure.TrackDirect("GfxObj", id);
                    break;
                case 0x02:
                    closure.TrackDirect("Setup", id);
                    break;
                case 0x08:
                    closure.TrackDirect("Surface", id);
                    break;
                case 0x0D:
                    closure.TrackDirect("Environment", id);
                    break;
                case 0x32:
                    closure.TrackDirect("ParticleEmitter", id);
                    break;
            }
        }

        private static (string Type, uint Id) DequeuePending(LegacyDatWorldReferenceClosure closure) {
            return closure.DequeuePending();
        }

        private static void ExpandSetup(LegacyDatReader source, LegacyDatWorldReferenceClosure closure, uint setupId) {
            if ((setupId >> 24) != 0x02) {
                ExpandPortalObjectById(source, closure, setupId);
                return;
            }

            source.TryCollectSetupReferences(
                setupId,
                partId => closure.TrackDirect("GfxObj", partId));
        }

        private static string NormalizePortalObjectType(string type, uint id) {
            if (LegacyDatReader.IsSyntheticRenderSurfaceId(id) || (id >> 24) == 0x06) {
                return "RenderSurface";
            }

            return (id >> 24) switch {
                0x01 => "GfxObj",
                0x02 => "Setup",
                0x04 => "Palette",
                0x05 => "SurfaceTexture",
                0x08 => "Surface",
                0x0D => "Environment",
                0x32 => "ParticleEmitter",
                _ => type,
            };
        }

        private static void ExpandPortalObjectById(LegacyDatReader source, LegacyDatWorldReferenceClosure closure, uint id) {
            switch (id >> 24) {
                case 0x01:
                    ExpandGfxObj(source, closure, id);
                    break;
                case 0x02:
                    ExpandSetup(source, closure, id);
                    break;
                case 0x08:
                    ExpandSurface(source, closure, id);
                    break;
                case 0x32:
                    closure.TrackDirect("ParticleEmitter", id);
                    break;
            }
        }

        private static void ExpandGfxObj(LegacyDatReader source, LegacyDatWorldReferenceClosure closure, uint gfxObjId) {
            source.TryCollectGfxObjReferences(
                gfxObjId,
                surfaceId => closure.TrackDirect("Surface", surfaceId));
        }

        private static void ExpandSurface(LegacyDatReader source, LegacyDatWorldReferenceClosure closure, uint surfaceId) {
            source.TryCollectSurfaceReferences(
                surfaceId,
                textureId => closure.TrackDirect("SurfaceTexture", textureId),
                paletteId => closure.TrackDirect("Palette", paletteId));
        }

        private static void ExpandSurfaceTexture(LegacyDatWorldReferenceClosure closure, uint surfaceTextureId) {
            closure.TrackDirect("RenderSurface", LegacyDatDecoders.CreateSyntheticRenderSurfaceId(surfaceTextureId));
        }

        private static void ExpandRegion(LegacyDatReader source, LegacyDatWorldReferenceClosure closure, uint regionId) {
            if (!source.TryGet<Region>(regionId, out var region) || region == null) {
                return;
            }

            var texMerge = region.TerrainInfo.LandSurfaces.TexMerge;
            foreach (var desc in texMerge.TerrainDesc) {
                closure.TrackDirect("SurfaceTexture", (uint)desc.TerrainTex.TextureId);
            }

            foreach (var overlay in texMerge.RoadMaps) {
                closure.TrackDirect("SurfaceTexture", (uint)overlay.TextureId);
            }

            foreach (var overlay in texMerge.CornerTerrainMaps) {
                closure.TrackDirect("SurfaceTexture", (uint)overlay.TextureId);
            }

            foreach (var overlay in texMerge.SideTerrainMaps) {
                closure.TrackDirect("SurfaceTexture", (uint)overlay.TextureId);
            }
        }
    }
}
