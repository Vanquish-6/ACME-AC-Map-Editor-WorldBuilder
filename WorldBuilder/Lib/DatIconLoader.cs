using Avalonia;
using Avalonia.Media.Imaging;
using Acme.Render.GL.Lib;
using Acme.Dat;
using System;
using System.Runtime.InteropServices;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Lib {
    /// <summary>
    /// Loads DAT icon/texture IDs (0x06xxxxxx range) into Avalonia WriteableBitmaps.
    /// Reuses decompression logic from the dungeon surface browser.
    /// </summary>
    public static class DatIconLoader {
        public static WriteableBitmap? LoadIcon(IDatReaderWriter dats, uint iconId, int size = 32) {
            try {
                if (iconId == 0) return null;
                if (!dats.TryGet<RenderSurface>(iconId, out var rs)) return null;
                return WorldBuilder.Editors.Dungeon.SurfaceBrowserViewModel.TryBitmapFromRenderSurface(rs, dats, size);
            }
            catch {
                return null;
            }
        }

        /// <summary>
        /// Loads a SurfaceTexture (0x05xxxxxx) directly into a bitmap, following the
        /// SurfaceTexture → Textures.Last() → RenderSurface chain.
        /// Uses the full decode path including INDEX16/palette support.
        /// </summary>
        public static WriteableBitmap? LoadSurfaceTextureIcon(IDatReaderWriter dats, uint surfaceTextureId, int size = 64) {
            try {
                if (surfaceTextureId == 0) return null;
                if (!dats.TryGet<SurfaceTexture>(surfaceTextureId, out var st) || st?.Textures == null || st.Textures.Count == 0) return null;
                var rsId = st.Textures[st.Textures.Count - 1];
                if (!dats.TryGet<RenderSurface>(rsId, out var rs) || rs == null) return null;
                return WorldBuilder.Editors.Dungeon.SurfaceBrowserViewModel.TryBitmapFromRenderSurface(rs, dats, size);
            }
            catch {
                return null;
            }
        }

        public static WriteableBitmap? LoadSurfaceIcon(IDatReaderWriter dats, uint surfaceId, int size = 32) {
            try {
                if (surfaceId == 0) return null;
                if (!dats.TryGet<Surface>(surfaceId, out var surface)) return null;

                if (surface.Type.HasFlag(SurfaceType.Base1Solid)) {
                    var solidData = TextureHelpers.CreateSolidColorTexture(surface.ColorValue, size, size);
                    return CreateBitmap(solidData, size, size);
                }

                if (!dats.TryGet<SurfaceTexture>(surface.OrigTextureId, out var surfTex) ||
                    surfTex.Textures?.Count == 0) return null;

                var rsId = surfTex.Textures[surfTex.Textures.Count - 1];
                if (!dats.TryGet<RenderSurface>(rsId, out var rs)) return null;
                uint paletteOverrideId = surface.OrigPaletteId != 0
                    ? (uint)surface.OrigPaletteId
                    : rs.DefaultPaletteId;
                bool transparentLowClipIndices = surface.Type.HasFlag(SurfaceType.Base1ClipMap);
                return WorldBuilder.Editors.Dungeon.SurfaceBrowserViewModel.TryBitmapFromRenderSurface(rs, dats, size, paletteOverrideId, transparentLowClipIndices);
            }
            catch {
                return null;
            }
        }

        private static WriteableBitmap? RenderSurfaceToBitmap(RenderSurface rs, int size) {
            int w = rs.Width, h = rs.Height;
            byte[]? rgba = null;

            switch (rs.FormatEnum) {
                case PixelFormat.PFID_A8R8G8B8:
                    rgba = SwizzleBgraToRgba(rs.SourceData, w * h);
                    break;
                case PixelFormat.PFID_R8G8B8:
                    rgba = new byte[w * h * 4];
                    for (int i = 0; i < w * h; i++) {
                        rgba[i * 4 + 0] = rs.SourceData[i * 3 + 2];
                        rgba[i * 4 + 1] = rs.SourceData[i * 3 + 1];
                        rgba[i * 4 + 2] = rs.SourceData[i * 3 + 0];
                        rgba[i * 4 + 3] = 255;
                    }
                    break;
                case PixelFormat.PFID_DXT1:
                    rgba = DecompressDxt1(rs.SourceData, w, h);
                    break;
                case PixelFormat.PFID_DXT3:
                case PixelFormat.PFID_DXT5:
                    rgba = DecompressDxt5(rs.SourceData, w, h,
                        rs.FormatEnum == PixelFormat.PFID_DXT3);
                    break;
                default:
                    return null;
            }

            if (rgba == null) return null;
            if (w != size || h != size)
                rgba = DownsampleNearest(rgba, w, h, size, size);
            return CreateBitmap(rgba, size, size);
        }

        internal static WriteableBitmap CreateBitmap(byte[] rgba, int width, int height) {
            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Rgba8888);
            using (var fb = bitmap.Lock()) {
                Marshal.Copy(rgba, 0, fb.Address, Math.Min(rgba.Length, fb.RowBytes * height));
            }
            return bitmap;
        }

        internal static byte[] SwizzleBgraToRgba(byte[] bgra, int pixelCount) {
            var rgba = new byte[pixelCount * 4];
            for (int i = 0; i < pixelCount; i++) {
                int idx = i * 4;
                rgba[idx + 0] = bgra[idx + 2];
                rgba[idx + 1] = bgra[idx + 1];
                rgba[idx + 2] = bgra[idx + 0];
                rgba[idx + 3] = bgra[idx + 3];
            }
            return rgba;
        }

        private static byte[] DownsampleNearest(byte[] src, int srcW, int srcH, int dstW, int dstH) {
            var dst = new byte[dstW * dstH * 4];
            for (int y = 0; y < dstH; y++) {
                int srcY = y * srcH / dstH;
                for (int x = 0; x < dstW; x++) {
                    int srcX = x * srcW / dstW;
                    int si = (srcY * srcW + srcX) * 4;
                    int di = (y * dstW + x) * 4;
                    dst[di] = src[si]; dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2]; dst[di + 3] = src[si + 3];
                }
            }
            return dst;
        }

        private static byte[] DecompressDxt1(byte[] data, int width, int height) {
            var rgba = new byte[width * height * 4];
            int blocksW = Math.Max(1, (width + 3) / 4);
            int blocksH = Math.Max(1, (height + 3) / 4);
            int offset = 0;
            for (int by = 0; by < blocksH; by++) {
                for (int bx = 0; bx < blocksW; bx++) {
                    if (offset + 8 > data.Length) break;
                    ushort c0 = (ushort)(data[offset] | (data[offset + 1] << 8));
                    ushort c1 = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
                    uint lt = (uint)(data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16) | (data[offset + 7] << 24));
                    offset += 8;
                    var colors = new byte[4][];
                    colors[0] = TextureHelpers.Color565ToRgba(c0);
                    colors[1] = TextureHelpers.Color565ToRgba(c1);
                    if (c0 > c1) {
                        colors[2] = new byte[] { (byte)((2 * colors[0][0] + colors[1][0] + 1) / 3), (byte)((2 * colors[0][1] + colors[1][1] + 1) / 3), (byte)((2 * colors[0][2] + colors[1][2] + 1) / 3), 255 };
                        colors[3] = new byte[] { (byte)((colors[0][0] + 2 * colors[1][0] + 1) / 3), (byte)((colors[0][1] + 2 * colors[1][1] + 1) / 3), (byte)((colors[0][2] + 2 * colors[1][2] + 1) / 3), 255 };
                    } else {
                        colors[2] = new byte[] { (byte)((colors[0][0] + colors[1][0]) / 2), (byte)((colors[0][1] + colors[1][1]) / 2), (byte)((colors[0][2] + colors[1][2]) / 2), 255 };
                        colors[3] = new byte[] { 0, 0, 0, 0 };
                    }
                    for (int row = 0; row < 4; row++)
                        for (int col = 0; col < 4; col++) {
                            int px = bx * 4 + col, py = by * 4 + row;
                            if (px >= width || py >= height) continue;
                            int idx = (int)((lt >> (2 * (row * 4 + col))) & 0x03);
                            int di = (py * width + px) * 4;
                            rgba[di] = colors[idx][0]; rgba[di + 1] = colors[idx][1];
                            rgba[di + 2] = colors[idx][2]; rgba[di + 3] = colors[idx][3];
                        }
                }
            }
            return rgba;
        }

        private static byte[] DecompressDxt5(byte[] data, int width, int height, bool isDxt3) {
            var rgba = new byte[width * height * 4];
            int blocksW = Math.Max(1, (width + 3) / 4);
            int blocksH = Math.Max(1, (height + 3) / 4);
            int offset = 0;
            for (int by = 0; by < blocksH; by++) {
                for (int bx = 0; bx < blocksW; bx++) {
                    if (offset + 16 > data.Length) break;
                    byte[] alphas = new byte[16];
                    if (isDxt3) {
                        for (int i = 0; i < 4; i++) {
                            ushort ab = (ushort)(data[offset + i * 2] | (data[offset + i * 2 + 1] << 8));
                            for (int j = 0; j < 4; j++)
                                alphas[i * 4 + j] = (byte)(((ab >> (j * 4)) & 0xF) * 17);
                        }
                    } else {
                        byte a0 = data[offset], a1 = data[offset + 1];
                        ulong ab = 0;
                        for (int i = 2; i < 8; i++) ab |= (ulong)data[offset + i] << ((i - 2) * 8);
                        for (int i = 0; i < 16; i++) {
                            int code = (int)((ab >> (3 * i)) & 0x07);
                            if (code == 0) alphas[i] = a0;
                            else if (code == 1) alphas[i] = a1;
                            else if (a0 > a1) alphas[i] = (byte)(((8 - code) * a0 + (code - 1) * a1) / 7);
                            else if (code == 6) alphas[i] = 0;
                            else if (code == 7) alphas[i] = 255;
                            else alphas[i] = (byte)(((6 - code) * a0 + (code - 1) * a1) / 5);
                        }
                    }
                    offset += 8;
                    ushort c0 = (ushort)(data[offset] | (data[offset + 1] << 8));
                    ushort c1 = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
                    uint lt = (uint)(data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16) | (data[offset + 7] << 24));
                    offset += 8;
                    var colors = new byte[4][];
                    colors[0] = TextureHelpers.Color565ToRgba(c0);
                    colors[1] = TextureHelpers.Color565ToRgba(c1);
                    colors[2] = new byte[] { (byte)((2 * colors[0][0] + colors[1][0] + 1) / 3), (byte)((2 * colors[0][1] + colors[1][1] + 1) / 3), (byte)((2 * colors[0][2] + colors[1][2] + 1) / 3), 255 };
                    colors[3] = new byte[] { (byte)((colors[0][0] + 2 * colors[1][0] + 1) / 3), (byte)((colors[0][1] + 2 * colors[1][1] + 1) / 3), (byte)((colors[0][2] + 2 * colors[1][2] + 1) / 3), 255 };
                    for (int row = 0; row < 4; row++)
                        for (int col = 0; col < 4; col++) {
                            int px = bx * 4 + col, py = by * 4 + row;
                            if (px >= width || py >= height) continue;
                            int ci = (int)((lt >> (2 * (row * 4 + col))) & 0x03);
                            int di = (py * width + px) * 4;
                            rgba[di] = colors[ci][0]; rgba[di + 1] = colors[ci][1];
                            rgba[di + 2] = colors[ci][2]; rgba[di + 3] = alphas[row * 4 + col];
                        }
                }
            }
            return rgba;
        }
    }
}
