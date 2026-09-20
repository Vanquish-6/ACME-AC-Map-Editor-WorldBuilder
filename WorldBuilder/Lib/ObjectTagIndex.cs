using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace WorldBuilder.Lib {
    /// <summary>
    /// Provides keyword-based search for DAT objects (GfxObj / Setup).
    /// Loads a JSON index mapping hex object IDs to keyword arrays,
    /// and builds an inverted index for fast lookup.
    /// </summary>
    public class ObjectTagIndex {
        private readonly Dictionary<uint, string[]> _tags = new();
        private readonly Dictionary<string, List<uint>> _invertedIndex = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        /// <summary>
        /// Number of objects in the index.
        /// </summary>
        public int Count => _tags.Count;

        /// <summary>
        /// Whether the index was loaded successfully.
        /// </summary>
        public bool IsLoaded => _loaded;

        /// <summary>
        /// Loads the tag index from the embedded resource "Data/object-tags.json".
        /// Returns false if the resource is not found (graceful fallback).
        /// </summary>
        public bool LoadFromEmbeddedResource() {
            try {
                var assembly = typeof(ObjectTagIndex).Assembly;
                var resourceName = "WorldBuilder.Data.object-tags.json";

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) {
                    // Fallback: search all resource names
                    var allNames = assembly.GetManifestResourceNames();
                    Console.WriteLine($"[ObjectTagIndex] Resource '{resourceName}' not found. Available: {string.Join(", ", allNames)}");
                    var fallback = allNames.FirstOrDefault(n => n.EndsWith("object-tags.json", StringComparison.OrdinalIgnoreCase));
                    if (fallback == null) {
                        Console.WriteLine("[ObjectTagIndex] No object-tags.json embedded resource found");
                        return false;
                    }
                    Console.WriteLine($"[ObjectTagIndex] Using fallback resource name: {fallback}");
                    using var fallbackStream = assembly.GetManifestResourceStream(fallback);
                    if (fallbackStream == null) return false;
                    Load(fallbackStream);
                    return true;
                }

                Load(stream);
                return true;
            }
            catch (Exception ex) {
                Console.WriteLine($"[ObjectTagIndex] Error loading embedded resource: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Loads the tag index from a JSON stream.
        /// Expected format: { "01000AEC": ["cactus", "green", "desert"], ... }
        /// </summary>
        public void Load(Stream jsonStream) {
            using var doc = JsonDocument.Parse(jsonStream);
            var root = doc.RootElement;

            foreach (var prop in root.EnumerateObject()) {
                if (!uint.TryParse(prop.Name, NumberStyles.HexNumber, null, out var objectId)) continue;

                var keywords = new List<string>();

                // Handle both array and single-string values
                // (PowerShell's ConvertTo-Json emits single-element arrays as bare strings)
                if (prop.Value.ValueKind == JsonValueKind.Array) {
                    foreach (var keyword in prop.Value.EnumerateArray()) {
                        var word = keyword.GetString();
                        if (!string.IsNullOrWhiteSpace(word)) {
                            keywords.Add(word);
                        }
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.String) {
                    var word = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(word)) {
                        keywords.Add(word);
                    }
                }

                if (keywords.Count == 0) continue;

                var keywordArray = keywords.ToArray();
                _tags[objectId] = keywordArray;

                // Build inverted index
                foreach (var kw in keywordArray) {
                    var lower = kw.ToLowerInvariant();
                    if (!_invertedIndex.TryGetValue(lower, out var ids)) {
                        ids = new List<uint>();
                        _invertedIndex[lower] = ids;
                    }
                    ids.Add(objectId);
                }
            }

            _loaded = true;
            Console.WriteLine($"[ObjectTagIndex] Loaded {_tags.Count} objects, {_invertedIndex.Count} unique keywords");
        }

        /// <summary>
        /// Searches for objects matching the given query string.
        /// Splits the query into words and returns objects that match ALL words
        /// (each word is matched as a prefix against keywords).
        /// </summary>
        public HashSet<uint> Search(string query) {
            if (!_loaded || string.IsNullOrWhiteSpace(query)) return new HashSet<uint>();

            var queryWords = query.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (queryWords.Length == 0) return new HashSet<uint>();

            HashSet<uint>? result = null;

            foreach (var word in queryWords) {
                var matches = new HashSet<uint>();

                // Prefix matching: "cact" matches "cactus"
                foreach (var (keyword, ids) in _invertedIndex) {
                    if (keyword.StartsWith(word, StringComparison.OrdinalIgnoreCase)) {
                        foreach (var id in ids) {
                            matches.Add(id);
                        }
                    }
                }

                // Intersect with previous results (AND logic across words)
                if (result == null) {
                    result = matches;
                }
                else {
                    result.IntersectWith(matches);
                }

                if (result.Count == 0) break;
            }

            return result ?? new HashSet<uint>();
        }

        /// <summary>
        /// Gets the keyword tags for a specific object, or empty array if not found.
        /// </summary>
        public string[] GetTags(uint objectId) {
            return _tags.TryGetValue(objectId, out var tags) ? tags : Array.Empty<string>();
        }

        /// <summary>
        /// Gets a display-friendly tag string for an object (comma-separated).
        /// Returns null if no tags exist.
        /// </summary>
        public string? GetTagString(uint objectId) {
            if (!_tags.TryGetValue(objectId, out var tags) || tags.Length == 0) return null;
            return string.Join(", ", tags);
        }
    }
}
