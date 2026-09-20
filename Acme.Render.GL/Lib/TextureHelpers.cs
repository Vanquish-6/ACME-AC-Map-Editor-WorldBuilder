using Acme.Dat;
using Acme.Render.Enums;

namespace Acme.Render.GL.Lib {
    public static class TextureHelpers {
        public static byte[] CreateSolidColorTexture(ColorARGB color, int width, int height) {
            var bytes = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++) {
                bytes[i * 4 + 0] = color.Red;
                bytes[i * 4 + 1] = color.Green;
                bytes[i * 4 + 2] = color.Blue;
                bytes[i * 4 + 3] = color.Alpha;
            }
            return bytes;
        }

        public static ColorARGB[] ExpandPalette256To2048(IReadOnlyList<ColorARGB> colors) {
            if (colors.Count != 256)
                return colors is ColorARGB[] arr ? arr : colors.ToArray();

            var expanded = new ColorARGB[2048];
            for (int i = 0; i < 256; i++) {
                int baseSlot = i * 8;
                var c = colors[i];
                for (int j = 0; j < 8; j++)
                    expanded[baseSlot + j] = c;
            }
            return expanded;
        }

        public static ColorARGB[] ExpandPalette256To2048(IReadOnlyList<uint> colors) =>
            ExpandPalette256To2048(colors.Select(ColorARGB.FromPacked).ToArray());

        static int ResolvePaletteIndex(int palIdx, int paletteLength) {
            return palIdx < paletteLength ? palIdx : 0;
        }

        public static void FillIndex16(byte[] src, Palette palette, Span<byte> dst, int width, int height, bool transparentLowClipIndices = false, bool expand256Palette = true) {
            var colors = palette.ColorValues;
            var useExpanded = expand256Palette && palette.Colors.Count == 256 ? ExpandPalette256To2048(colors) : colors;
            FillIndex16(src, useExpanded, dst, width, height, transparentLowClipIndices);
        }

        public static void FillIndex16(byte[] src, ColorARGB[] palette, Span<byte> dst, int width, int height, bool transparentLowClipIndices = false) {
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    var srcIdx = (y * width + x) * 2;
                    var palIdx = (ushort)(src[srcIdx] | (src[srcIdx + 1] << 8));
                    var dstIdx = (y * width + x) * 4;
                    if (transparentLowClipIndices && palIdx < 8) {
                        dst[dstIdx + 0] = 0;
                        dst[dstIdx + 1] = 0;
                        dst[dstIdx + 2] = 0;
                        dst[dstIdx + 3] = 0;
                        continue;
                    }
                    if (palIdx >= palette.Length) palIdx = 0;
                    var color = palette[palIdx];
                    dst[dstIdx + 0] = color.Red;
                    dst[dstIdx + 1] = color.Green;
                    dst[dstIdx + 2] = color.Blue;
                    dst[dstIdx + 3] = color.Alpha;
                }
            }
        }

        public static bool IsCompressedFormat(PixelFormat format) {
            return format == PixelFormat.PFID_DXT1 ||
                   format == PixelFormat.PFID_DXT3 ||
                   format == PixelFormat.PFID_DXT5;
        }

        public static int GetCompressedLayerSize(int width, int height, TextureFormat format) {
            int blocksWide = Math.Max(1, (width + 3) / 4);
            int blocksHigh = Math.Max(1, (height + 3) / 4);
            int blockSize = format == TextureFormat.DXT1 ? 8 : 16;
            return blocksWide * blocksHigh * blockSize;
        }

        public static byte[] ConvertBgraToRgba(byte[] bgra) {
            var rgba = new byte[bgra.Length];
            for (int i = 0; i < bgra.Length; i += 4) {
                rgba[i + 0] = bgra[i + 2];
                rgba[i + 1] = bgra[i + 1];
                rgba[i + 2] = bgra[i + 0];
                rgba[i + 3] = bgra[i + 3];
            }
            return rgba;
        }

        public static byte[] Color565ToRgba(ushort color565) {
            int r = (color565 >> 11) & 31;
            int g = (color565 >> 5) & 63;
            int b = color565 & 31;
            return [(byte)(r * 255 / 31), (byte)(g * 255 / 63), (byte)(b * 255 / 31), 255];
        }
    }
}
