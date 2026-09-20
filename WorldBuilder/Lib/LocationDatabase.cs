using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace WorldBuilder.Lib {
    public class LocationEntry {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public uint CellId { get; set; }
        public ushort LandblockId => (ushort)(CellId >> 16);
        public Vector3 Position { get; set; }
        public Quaternion Orientation { get; set; }

        public string LandblockHex => $"{LandblockId:X4}";
        public string CellIdHex => $"{CellId:X8}";
    }

    /// <summary>
    /// Loads and provides searchable access to the embedded Locations.txt
    /// resource. All entries, all types. Consumers can filter by type.
    /// </summary>
    public static class LocationDatabase {
        private static List<LocationEntry>? _entries;
        private static readonly object _lock = new();

        public static IReadOnlyList<LocationEntry> All {
            get {
                EnsureLoaded();
                return _entries!;
            }
        }

        public static IEnumerable<LocationEntry> Dungeons =>
            All.Where(e => e.Type.Equals("Dungeon", StringComparison.OrdinalIgnoreCase));

        public static IEnumerable<LocationEntry> Search(string query, string? typeFilter = null) {
            if (string.IsNullOrWhiteSpace(query))
                return typeFilter != null ? All.Where(e => e.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)) : All;

            var q = query.Trim();
            var source = typeFilter != null
                ? All.Where(e => e.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                : (IEnumerable<LocationEntry>)All;

            // Try hex landblock match first
            if (q.Length <= 8 && uint.TryParse(q.TrimStart('0', 'x', 'X'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexVal)) {
                ushort searchLb = q.Length <= 4 ? (ushort)hexVal : (ushort)(hexVal >> 16);
                var hexMatches = source.Where(e => e.LandblockId == searchLb).ToList();
                if (hexMatches.Count > 0) return hexMatches;
            }

            return source.Where(e =>
                e.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Finds the first location whose name matches (exact or contains) and optional type.
        /// e.g. GetFirstByName("Yaraq", "Town") for the town, GetFirstByName("A Red Rat Lair", "Dungeon") for the dungeon.
        /// </summary>
        public static LocationEntry? GetFirstByName(string name, string? typeFilter = null) {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var matches = Search(name, typeFilter).ToList();
            if (matches.Count == 0) return null;
            // Prefer exact name match
            var exact = matches.FirstOrDefault(e => e.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            return exact ?? matches[0];
        }

        /// <summary>
        /// Returns a random overworld landblock (excludes Dungeon type). Optional type filter e.g. "Town".
        /// </summary>
        public static LocationEntry? GetRandomLocation(string? typeFilter = null, Random? rng = null) {
            rng ??= new Random();
            var source = typeFilter != null
                ? All.Where(e => e.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                : All.Where(e => !e.Type.Equals("Dungeon", StringComparison.OrdinalIgnoreCase));
            var list = source.ToList();
            if (list.Count == 0) return null;
            return list[rng.Next(list.Count)];
        }

        private static void EnsureLoaded() {
            if (_entries != null) return;
            lock (_lock) {
                if (_entries != null) return;
                _entries = LoadFromEmbeddedResource();
            }
        }

        private static List<LocationEntry> LoadFromEmbeddedResource() {
            var assembly = typeof(LocationDatabase).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Locations.txt", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null) {
                Console.WriteLine("Warning: Locations.txt embedded resource not found");
                return new List<LocationEntry>();
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return new List<LocationEntry>();

            using var reader = new StreamReader(stream);
            var entries = new List<LocationEntry>();

            string? line;
            while ((line = reader.ReadLine()) != null) {
                var entry = ParseLine(line);
                if (entry != null) entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// Parses a line: "Name | Type | 0xCellId [x y z] qw qx qy qz"
        /// </summary>
        internal static LocationEntry? ParseLine(string line) {
            if (string.IsNullOrWhiteSpace(line)) return null;

            var parts = line.Split('|');
            if (parts.Length < 3) return null;

            var name = parts[0].Trim();
            var type = parts[1].Trim();
            var rest = parts[2].Trim();

            // Parse "0xCellId [x y z] qw qx qy qz"
            var tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 1) return null;

            var hexStr = tokens[0];
            if (hexStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hexStr = hexStr[2..];
            if (!uint.TryParse(hexStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cellId))
                return null;

            var entry = new LocationEntry {
                Name = name,
                Type = type,
                CellId = cellId
            };

            // Parse position [x y z] - strip brackets
            if (tokens.Length >= 4) {
                var xStr = tokens[1].TrimStart('[');
                var yStr = tokens[2];
                var zStr = tokens[3].TrimEnd(']');
                if (float.TryParse(xStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) &&
                    float.TryParse(yStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var py) &&
                    float.TryParse(zStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pz)) {
                    entry.Position = new Vector3(px, py, pz);
                }
            }

            // Parse quaternion qw qx qy qz
            if (tokens.Length >= 8) {
                if (float.TryParse(tokens[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var qw) &&
                    float.TryParse(tokens[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var qx) &&
                    float.TryParse(tokens[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var qy) &&
                    float.TryParse(tokens[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var qz)) {
                    entry.Orientation = new Quaternion(qx, qy, qz, qw);
                }
            }

            return entry;
        }
    }
}
