using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Re-encodes the converted Region's terrain blend maps (corner / side / road alpha maps in the
/// <c>TexMerge</c>) from DM's <see cref="PixelFormat.PFID_A8R8G8B8"/> (4 bytes/px) into retail's
/// <see cref="PixelFormat.PFID_CUSTOM_LSCAPE_ALPHA"/> (0xF4, 1 byte/px).
///
/// DM (pre-ToD) stores these masks as plain ARGB; the retail (post-ToD) terrain compositor
/// (<c>TexMerge::MakeNewSurface</c> / <c>ImgTex::MergeTexture</c>) only recognises
/// <c>PFID_CUSTOM_LSCAPE_ALPHA</c> surfaces as landscape blend masks, so DM's ARGB maps are skipped
/// and terrain transitions/roads render as a hard per-cell green/tan checkerboard instead of blending.
/// Confirmed by comparing the retail seed Region (alpha maps = LSCAPE_ALPHA 512x512, 1 B/px) vs the
/// DM export (A8R8G8B8 64x64, 4 B/px). See <c>docs/DM_LOGIN_NO_CRASH.md</c> (terrain section).
///
/// The mask value lives in the alpha byte (the §7.6 alpha-only widening sets B=G=R=A=mask), so the
/// LSCAPE_ALPHA byte is taken from the alpha channel. Texture dimensions are left at DM's native size
/// (proportional to DM's smaller <c>baseTexSize</c>), which blends correctly.
/// </summary>
public static class LegacyDatTerrainAlphaCompletion {
    public sealed class Result {
        public int Converted { get; internal set; }
        public int AlreadyOk { get; internal set; }
        public int Missing { get; internal set; }
    }

    public static Result Complete(string outputDirectory, Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var result = new Result();

        // §8.2: clear stale journal + zero free list before Chorizite writes.
        LegacyDatClientExportFixer.PatchCatalogFilesForWriting(outputDirectory, "client_portal.dat");

        using var output = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        int iteration = output.GetIteration(DatArchive.Portal);

        if (!output.TryGet<Region>(0x13000000u, out Region? region) || region == null) {
            return result;
        }

        var texMerge = region.TerrainInfo?.LandSurfaces?.TexMerge;
        if (texMerge == null) {
            return result;
        }

        var alphaMapIds = new List<uint>();
        alphaMapIds.AddRange(texMerge.CornerTerrainMaps.Select(m => m.TextureId));
        alphaMapIds.AddRange(texMerge.SideTerrainMaps.Select(m => m.TextureId));
        alphaMapIds.AddRange(texMerge.RoadMaps.Select(m => m.TextureId));

        foreach (uint surfaceTextureId in alphaMapIds.Distinct()) {
            if (surfaceTextureId is < 0x05000000 or > 0x05FFFFFF) {
                continue;
            }

            if (!output.TryGet(surfaceTextureId, out SurfaceTexture? st) || st == null || st.Textures.Count == 0) {
                result.Missing++;
                continue;
            }

            uint renderSurfaceId = st.Textures[^1];
            if (!output.TryGet(renderSurfaceId, out RenderSurface? rs) || rs == null || rs.SourceData == null) {
                result.Missing++;
                continue;
            }

            if (rs.Format == (uint)PixelFormat.PFID_CUSTOM_LSCAPE_ALPHA) {
                result.AlreadyOk++;
                continue;
            }

            if (rs.Format != (uint)PixelFormat.PFID_A8R8G8B8) {
                continue;
            }

            int pixelCount = rs.Width * rs.Height;
            if (pixelCount <= 0 || rs.SourceData.Length < pixelCount * 4) {
                continue;
            }

            var alpha = new byte[pixelCount];
            for (int i = 0; i < pixelCount; i++) {
                alpha[i] = rs.SourceData[i * 4 + 3];
            }

            rs.Format = (uint)PixelFormat.PFID_CUSTOM_LSCAPE_ALPHA;
            rs.SourceData = alpha;
            rs.DefaultPaletteId = 0;
            if (output.TrySave(rs, iteration)) {
                result.Converted++;
            }
        }

        if (result.Converted > 0 || result.Missing > 0) {
            onProgress?.Invoke(
                $"Terrain blend maps: re-encoded {result.Converted} to LSCAPE_ALPHA "
                + $"({result.AlreadyOk} already ok, {result.Missing} missing).");
        }

        return result;
    }
}
