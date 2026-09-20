using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Shared.Models {
    /// <summary>
    /// Represents a captured region of terrain that can be pasted elsewhere.
    /// Optimized for AC's 9x9 vertex grid system.
    /// </summary>
    public class TerrainStamp {
        public string Name { get; set; } = "Untitled Stamp";
        public string Description { get; set; } = "";
        public DateTime Created { get; set; } = DateTime.UtcNow;

        // Dimensions in vertices (e.g., 3x3 = 9 vertices)
        public int WidthInVertices { get; set; }
        public int HeightInVertices { get; set; }

        public string? Filename { get; set; }

        // Height data (byte indices into LandHeightTable)
        public byte[] Heights { get; set; } = Array.Empty<byte>();

        // Terrain type data (WORD: road + type + scenery)
        public ushort[] TerrainTypes { get; set; } = Array.Empty<ushort>();

        // Optional: capture objects within the region
        public List<StaticObject> Objects { get; set; } = new();

        // Metadata for alignment
        public Vector2 OriginalWorldPosition { get; set; }
        public ushort SourceLandblockId { get; set; }

        // Validation
        public bool IsValid() {
            int expectedCount = WidthInVertices * HeightInVertices;
            return Heights.Length == expectedCount &&
                   TerrainTypes.Length == expectedCount;
        }

        // Calculate size in bytes
        public int GetSizeInBytes() {
            return Heights.Length + (TerrainTypes.Length * 2) +
                   (Objects.Count * 64); // Approximate object size
        }
    }
}
