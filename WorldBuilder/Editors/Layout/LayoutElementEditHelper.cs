using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WorldBuilder.Editors.Layout {

    public static class LayoutElementEditHelper {

        /// <summary>
        /// <see cref="ElementDesc.Pack"/> only writes X/Y/W/H/Z when these flags are set on <see cref="ElementDesc.StateDesc"/>.
        /// </summary>
        public static void EnsureGeometryIncorporation(ElementDesc el) {
            el.StateDesc ??= new StateDesc();
            el.StateDesc.IncorporationFlags |= IncorporationFlags.X | IncorporationFlags.Y | IncorporationFlags.Width |
                                               IncorporationFlags.Height | IncorporationFlags.ZLevel;
        }

        public static bool TryParseDatHex(string? text, out uint value) {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.StartsWith("0X", StringComparison.Ordinal))
                t = t[2..];
            return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Sets the first <see cref="MediaDescImage.File"/> on default state, else template, else any state; otherwise appends an image on the template.
        /// </summary>
        public static bool TrySetPrimaryImageSurface(ElementDesc el, uint fileId) {
            fileId = LayoutMediaHelper.NormalizeSurfaceId(fileId);
            if (fileId == 0) return false;

            if (el.States != null && el.States.TryGetValue(el.DefaultState, out var def)) {
                if (TrySetFirstImage(def, fileId)) return true;
            }

            if (el.StateDesc != null && TrySetFirstImage(el.StateDesc, fileId)) return true;

            if (el.States != null) {
                foreach (var kv in el.States.OrderBy(k => (uint)k.Key)) {
                    if (TrySetFirstImage(kv.Value, fileId)) return true;
                }
            }

            el.StateDesc ??= new StateDesc();
            el.StateDesc.Media ??= new List<MediaDesc>();
            el.StateDesc.Media.Add(new MediaDescImage { File = fileId, DrawMode = 0 });
            return true;
        }

        static bool TrySetFirstImage(StateDesc sd, uint fileId) {
            if (sd.Media == null) return false;
            foreach (var m in sd.Media) {
                if (m is MediaDescImage img) {
                    img.File = fileId;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Appends a <see cref="MediaDescImage"/> to the template <see cref="ElementDesc.StateDesc"/>.</summary>
        public static bool TryAddTemplateImage(ElementDesc el, uint fileId) {
            fileId = LayoutMediaHelper.NormalizeSurfaceId(fileId);
            if (fileId == 0) return false;

            el.StateDesc ??= new StateDesc();
            el.StateDesc.Media ??= new List<MediaDesc>();
            el.StateDesc.Media.Add(new MediaDescImage { File = fileId, DrawMode = 0 });
            return true;
        }
    }
}
