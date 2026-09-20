using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Bakes the INDEX16 textures of specific static/effect objects into <see cref="PixelFormat.PFID_A8R8G8B8"/>
/// (palette colours resolved, alpha 255) so they bypass the retail client's INDEX16+palette
/// texture-combine path.
///
/// The retail client (<c>CSurface__InitLoad</c> → <c>SetTextureAndPalette</c> →
/// <c>ImgTex__CreateCombinedTexture</c>) builds a GPU texture for an INDEX16/P8 surface by combining the
/// indexed image with its palette; if that combine fails it RELEASES the texture map and the object
/// draws dark/untextured. DM ships some static objects (e.g. the lifestone, Setup <c>0x020002EE</c>) as
/// small INDEX16 textures whose combine fails on the retail engine, so they rendered as a dark crystal
/// instead of the translucent blue one (ACME's format-agnostic renderer drew them fine — confirming the
/// data is correct and the gap is the runtime combine). Verified fix: converting the textures to
/// A8R8G8B8 (identical DM colours, translucency/luminosity still come from the surface material) makes
/// the retail client render them directly.
///
/// SCOPE: applied only to a curated list of NON-recoloured static/effect setups. A blanket INDEX16→
/// A8R8G8B8 conversion is intentionally avoided — it would break ClothingTable/PalShift palette
/// recolouring (clothing, creatures) and clip-map cut-out transparency (see the reverted clip-map bake).
/// </summary>
public static class LegacyDatObjectTextureCombineFixer {
    /// <summary>
    /// Static/effect objects (by Setup id) whose DM INDEX16 textures fail the retail combine and must
    /// be baked to A8R8G8B8. Confirmed in-client. Add ids here as more are verified.
    /// </summary>
    public static readonly uint[] DefaultSetupIds = [
        0x020002EEu, // Life Stone (WCID 509) — blue translucent crystal
    ];

    public sealed class Result {
        public int Converted { get; internal set; }
        public int AlreadyOk { get; internal set; }
        public int Skipped { get; internal set; }
    }

    public static Result Complete(string outputDirectory, Action<string>? onProgress = null) =>
        Complete(outputDirectory, DefaultSetupIds, onProgress);

    public static Result Complete(string outputDirectory, IEnumerable<uint> setupIds, Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(setupIds);

        var result = new Result();

        LegacyDatClientExportFixer.PatchCatalogFilesForWriting(outputDirectory, "client_portal.dat");

        using var output = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        int iteration = output.GetIteration(DatArchive.Portal);
        var doneTextures = new HashSet<uint>();

        foreach (uint setupId in setupIds) {
            if (!output.TryGet<Setup>(setupId, out Setup? setup) || setup == null) {
                continue;
            }

            foreach (var part in setup.Parts) {
                if (part == 0 || !output.TryGet(part, out GfxObj? gfx) || gfx == null) {
                    continue;
                }

                foreach (var surfaceRef in gfx.Surfaces) {
                    if (!output.TryGet(surfaceRef, out Surface? surface) || surface == null) {
                        continue;
                    }

                    if (!(surface.Type.HasFlag(SurfaceType.Base1Image) || surface.Type.HasFlag(SurfaceType.Base1ClipMap))) {
                        continue;
                    }

                    uint textureId = surface.OrigTextureId;
                    if (textureId is < 0x05000000 or > 0x05FFFFFF || !doneTextures.Add(textureId)) {
                        continue;
                    }

                    if (BakeTexture(output, textureId, surface.OrigPaletteId, iteration, result)) {
                        onProgress?.Invoke($"Object texture combine fix: baked 0x{textureId:X8} (setup 0x{setupId:X8}) INDEX16 -> A8R8G8B8.");
                    }
                }
            }
        }

        if (result.Converted > 0) {
            onProgress?.Invoke($"Object texture combine fix: baked {result.Converted} INDEX16 texture(s) to A8R8G8B8 "
                + $"({result.AlreadyOk} already ok, {result.Skipped} skipped).");
        }

        return result;
    }

    private static bool BakeTexture(DefaultDatReaderWriter output, uint textureId, uint surfacePaletteId, int iteration, Result result) {
        if (!output.TryGet(textureId, out SurfaceTexture? st) || st == null || st.Textures.Count == 0) {
            return false;
        }

        uint renderSurfaceId = st.Textures[^1];
        if (!output.TryGet(renderSurfaceId, out RenderSurface? rs) || rs == null || rs.SourceData == null) {
            return false;
        }

        if (rs.Format == (uint)PixelFormat.PFID_A8R8G8B8) {
            result.AlreadyOk++;
            return false;
        }

        if (rs.Format != (uint)PixelFormat.PFID_INDEX16) {
            result.Skipped++;
            return false;
        }

        uint paletteId = surfacePaletteId != 0 ? surfacePaletteId : rs.DefaultPaletteId;
        if (!output.TryGet(paletteId, out Palette? palette) || palette == null || palette.Colors.Count == 0) {
            result.Skipped++;
            return false;
        }

        int pixelCount = rs.Width * rs.Height;
        if (pixelCount <= 0 || rs.SourceData.Length < pixelCount * 2) {
            result.Skipped++;
            return false;
        }

        var bgra = new byte[pixelCount * 4];
        int paletteCount = palette.Colors.Count;
        for (int i = 0; i < pixelCount; i++) {
            int index = (rs.SourceData[i * 2] | (rs.SourceData[i * 2 + 1] << 8)) % paletteCount;
            var color = ColorARGB.FromPacked(palette.Colors[index]);
            bgra[i * 4 + 0] = color.Blue;
            bgra[i * 4 + 1] = color.Green;
            bgra[i * 4 + 2] = color.Red;
            bgra[i * 4 + 3] = 255;
        }

        rs.Format = (uint)PixelFormat.PFID_A8R8G8B8;
        rs.SourceData = bgra;
        rs.DefaultPaletteId = 0;
        if (output.TrySave(rs, iteration)) {
            result.Converted++;
            return true;
        }

        return false;
    }
}
