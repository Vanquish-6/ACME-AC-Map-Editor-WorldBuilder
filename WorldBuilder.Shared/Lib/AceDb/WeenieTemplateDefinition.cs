using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WorldBuilder.Shared.Lib.AceDb {

    /// <summary>Starter scaffold for scalar weenie properties (JSON-defined templates).</summary>
    public sealed class WeenieTemplateDefinition {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string? Description { get; init; }
        public uint WeenieType { get; init; }
        public IReadOnlyList<(ushort Type, int Value)> Ints { get; init; } = Array.Empty<(ushort, int)>();
        public IReadOnlyList<(ushort Type, long Value)> Int64s { get; init; } = Array.Empty<(ushort, long)>();
        public IReadOnlyList<(ushort Type, bool Value)> Bools { get; init; } = Array.Empty<(ushort, bool)>();
        public IReadOnlyList<(ushort Type, double Value)> Floats { get; init; } = Array.Empty<(ushort, double)>();
        public IReadOnlyList<(ushort Type, string Value)> Strings { get; init; } = Array.Empty<(ushort, string)>();
        public IReadOnlyList<(ushort Type, uint Value)> DataIds { get; init; } = Array.Empty<(ushort, uint)>();
        public IReadOnlyList<(ushort Type, ulong Value)> InstanceIds { get; init; } = Array.Empty<(ushort, ulong)>();
    }

    public static class WeenieTemplateJson {
        /// <summary>Parses a JSON array of templates, or a single template object.</summary>
        public static IReadOnlyList<WeenieTemplateDefinition> ParseBundle(string json) {
            json = json.Trim();
            if (string.IsNullOrEmpty(json)) return Array.Empty<WeenieTemplateDefinition>();

            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var list = new List<WeenieTemplateDefinition>();

            if (root.ValueKind == JsonValueKind.Array) {
                foreach (var el in root.EnumerateArray()) {
                    if (TryParseOne(el, out var d) && d != null)
                        list.Add(d);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object) {
                if (TryParseOne(root, out var d) && d != null)
                    list.Add(d);
            }

            return list;
        }

        static bool TryParseOne(JsonElement el, out WeenieTemplateDefinition? def) {
            def = null;
            if (el.ValueKind != JsonValueKind.Object) return false;

            var id = GetString(el, "id");
            var title = GetString(el, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return false;

            var desc = GetString(el, "description");
            if (string.IsNullOrWhiteSpace(desc)) desc = null;

            if (!el.TryGetProperty("weenieType", out var wtEl) || !wtEl.TryGetUInt32(out var weenieType))
                weenieType = 1;

            def = new WeenieTemplateDefinition {
                Id = id.Trim(),
                Title = title.Trim(),
                Description = desc?.Trim(),
                WeenieType = weenieType,
                Ints = ReadIntProps(el, "ints"),
                Int64s = ReadInt64Props(el, "int64s"),
                Bools = ReadBoolProps(el, "bools"),
                Floats = ReadFloatProps(el, "floats"),
                Strings = ReadStringProps(el, "strings"),
                DataIds = ReadUintProps(el, "dataIds"),
                InstanceIds = ReadUlongProps(el, "instanceIds"),
            };
            return true;
        }

        static string? GetString(JsonElement parent, string name) {
            if (!parent.TryGetProperty(name, out var p)) return null;
            return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        static List<(ushort, int)> ReadIntProps(JsonElement parent, string arrayName) {
            var list = new List<(ushort, int)>();
            if (!parent.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray()) {
                if (!item.TryGetProperty("type", out var t) || !TryGetUInt16(t, out var type)) continue;
                if (item.TryGetProperty("value", out var v) && v.TryGetInt32(out var value))
                    list.Add((type, value));
            }
            return list;
        }

        static List<(ushort, long)> ReadInt64Props(JsonElement parent, string arrayName) {
            var list = new List<(ushort, long)>();
            if (!parent.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray()) {
                if (!item.TryGetProperty("type", out var t) || !TryGetUInt16(t, out var type)) continue;
                if (item.TryGetProperty("value", out var v) && v.TryGetInt64(out var value))
                    list.Add((type, value));
            }
            return list;
        }

        static List<(ushort, bool)> ReadBoolProps(JsonElement parent, string arrayName) {
            var list = new List<(ushort, bool)>();
            if (!parent.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray()) {
                if (!item.TryGetProperty("type", out var t) || !TryGetUInt16(t, out var type)) continue;
                if (item.TryGetProperty("value", out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    list.Add((type, v.GetBoolean()));
            }
            return list;
        }

        static List<(ushort, double)> ReadFloatProps(JsonElement parent, string arrayName) {
            var list = new List<(ushort, double)>();
            if (!parent.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray()) {
                if (!item.TryGetProperty("type", out var t) || !TryGetUInt16(t, out var type)) continue;
                if (item.TryGetProperty("value", out var v) && v.TryGetDouble(out var value))
                    list.Add((type, value));
            }
            return list;
        }

        static List<(ushort, string)> ReadStringProps(JsonElement parent, string arrayName) {
            var list = new List<(ushort, string)>();
            if (!parent.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray()) {
                if (!item.TryGetProperty("type", out var t) || !TryGetUInt16(t, out var type)) continue;
                if (item.TryGetProperty("value", out var v)) {
                    var s = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
                    list.Add((type, s));
                }
            }
            return list;
        }

        static List<(ushort, uint)> ReadUintProps(JsonElement parent, string arrayName) {
            var list = new List<(ushort, uint)>();
            if (!parent.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray()) {
                if (!item.TryGetProperty("type", out var t) || !TryGetUInt16(t, out var type)) continue;
                if (item.TryGetProperty("value", out var v) && TryGetUInt32(v, out var value))
                    list.Add((type, value));
            }
            return list;
        }

        static List<(ushort, ulong)> ReadUlongProps(JsonElement parent, string arrayName) {
            var list = new List<(ushort, ulong)>();
            if (!parent.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray()) {
                if (!item.TryGetProperty("type", out var t) || !TryGetUInt16(t, out var type)) continue;
                if (item.TryGetProperty("value", out var v) && TryGetUInt64(v, out var value))
                    list.Add((type, value));
            }
            return list;
        }

        static bool TryGetUInt32(JsonElement v, out uint value) {
            value = 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out value)) return true;
            if (v.ValueKind == JsonValueKind.String && uint.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, null, out value)) return true;
            if (v.ValueKind == JsonValueKind.String) {
                var s = v.GetString();
                if (s != null && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value))
                    return true;
            }
            return false;
        }

        static bool TryGetUInt64(JsonElement v, out ulong value) {
            value = 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out value)) return true;
            if (v.ValueKind == JsonValueKind.String && ulong.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, null, out value)) return true;
            return false;
        }

        static bool TryGetUInt16(JsonElement t, out ushort type) {
            type = 0;
            if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var i) && i is >= 0 and <= 65535) {
                type = (ushort)i;
                return true;
            }
            if (t.ValueKind == JsonValueKind.String && ushort.TryParse(t.GetString(), out type)) return true;
            return false;
        }
    }
}
