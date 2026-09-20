using System;
using System.Collections.Generic;
using System.Numerics;

namespace WorldBuilder.Editors.Dungeon {

    public class DungeonKnowledgeBase {
        /// <summary>Increment when the edge/prefab data format changes to trigger auto-rebuild.</summary>
        public const int CurrentVersion = 14;
        public int Version { get; set; }
        public DateTime AnalyzedAt { get; set; }
        public int DungeonsScanned { get; set; }
        public int TotalEdges { get; set; }
        public int TotalPrefabs { get; set; }
        public int TotalCatalogRooms { get; set; }

        /// <summary>Horizontal grid step observed in real dungeons (typically 10).</summary>
        public float GridStepH { get; set; } = 10f;
        /// <summary>Vertical grid step observed in real dungeons (typically 6).</summary>
        public float GridStepV { get; set; } = 6f;

        public List<AdjacencyEdge> Edges { get; set; } = new();
        public List<DungeonPrefab> Prefabs { get; set; } = new();
        public List<CatalogRoom> Catalog { get; set; } = new();
        public List<RoomStaticSet> RoomStatics { get; set; } = new();
        public List<DungeonTemplate> Templates { get; set; } = new();

        /// <summary>
        /// Pre-computed dead-end rooms available for each portal face.
        /// Key: "envId_cellStruct_polyId" (string for JSON serialization).
        /// Value: list of verified dead-end rooms that can cap that portal.
        /// </summary>
        public List<DeadEndEntry> DeadEndIndex { get; set; } = new();

        /// <summary>Dominant wall+floor surface pairs per dungeon style for theming.</summary>
        public List<StyleTheme> StyleThemes { get; set; } = new();
    }

    public class StyleTheme {
        public string Name { get; set; } = "";
        public ushort WallSurface { get; set; }
        public ushort FloorSurface { get; set; }
        public int SampleCount { get; set; }
    }

    public class CompatibleRoom {
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public ushort PolyId { get; set; }
        public int Count { get; set; }
        public Vector3 RelOffset { get; set; }
        public Quaternion RelRot { get; set; } = Quaternion.Identity;

        /// <summary>Portal geometry of the target side (the room being placed).</summary>
        public float PortalWidth { get; set; }
        public float PortalHeight { get; set; }
        public float PortalArea { get; set; }
        public int PortalVertexCount { get; set; }

        /// <summary>Composite preference score (higher is better).</summary>
        public float EdgeQuality { get; set; }
        public bool ExactMatch { get; set; }
        public bool RestrictionTarget { get; set; }
        public int TargetVisibleCellCount { get; set; }
        public float TargetOutsidePortalRate { get; set; }
        public float TargetRestrictionRate { get; set; }

        /// <summary>
        /// True when this entry was derived from portal geometry matching rather than
        /// a proven edge from a real dungeon. The generator must use geometric snap
        /// (not RelOffset/RelRot) to place these rooms.
        /// </summary>
        public bool IsGeometryDerived { get; set; }
    }

    /// <summary>
    /// Pre-computed dead-end options for a specific portal face.
    /// </summary>
    public class DeadEndEntry {
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public ushort PolyId { get; set; }
        public List<DeadEndOption> Options { get; set; } = new();
    }

    public class DeadEndOption {
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public ushort PolyId { get; set; }
        public int Count { get; set; }
        public float RelOffsetX { get; set; }
        public float RelOffsetY { get; set; }
        public float RelOffsetZ { get; set; }
        public float RelRotX { get; set; }
        public float RelRotY { get; set; }
        public float RelRotZ { get; set; }
        public float RelRotW { get; set; } = 1f;
    }
}
