using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace WorldBuilder.Editors.Dungeon {

    public class DungeonPrefab {
        public string Signature { get; set; } = "";
        private int _sourceLandblock;
        public int SourceLandblock {
            get => _sourceLandblock;
            set {
                _sourceLandblock = value;
                if (value != 0 && !SourceLandblocks.Contains(value))
                    SourceLandblocks.Add(value);
            }
        }
        /// <summary>
        /// All landblocks this prefab signature was observed in.
        /// Kept in addition to SourceLandblock for backwards compatibility.
        /// </summary>
        public List<int> SourceLandblocks { get; set; } = new();
        public bool SourceHasBuildings { get; set; }
        public int SourceBuildingCount { get; set; }
        public List<int> SourceBuildingLinkedLandblocks { get; set; } = new();
        public string SourceDungeonName { get; set; } = "";
        public int UsageCount { get; set; } = 1;
        public List<PrefabCell> Cells { get; set; } = new();
        public List<PrefabPortal> InternalPortals { get; set; } = new();
        public List<PrefabOpenFace> OpenFaces { get; set; } = new();
        public int TotalPortals => Cells.Sum(c => c.PortalCount);
        public int OpenPortalCount => OpenFaces.Count;

        public string DisplayName { get; set; } = "";
        public string Category { get; set; } = "";
        public string Style { get; set; } = "";
        public List<string> Tags { get; set; } = new();

        /// <summary>True when ALL cells in this prefab have ceiling polygons.</summary>
        public bool HasFullRoof { get; set; } = true;
        /// <summary>True when at least one cell is missing ceiling polygons.</summary>
        public bool HasPartialRoof { get; set; }
        /// <summary>True when NO cells have ceiling polygons at all.</summary>
        public bool HasNoRoof { get; set; }

        /// <summary>Starter-kit role when this is a 1-cell knowledge-base brick (Hallway, Corner, …).</summary>
        public string KitRole { get; set; } = "";
        /// <summary>How many other rooms in the same kit have a proven retail connection to this one.</summary>
        public int KitLinkCount { get; set; }

        /// <summary>Cardinal direction labels for each open face (N/S/E/W/Up/Down).</summary>
        public List<string> OpenFaceDirections { get; set; } = new();

        /// <summary>Compact summary of open face directions, e.g. "N+S", "N+E+Down".</summary>
        public string ConnectionDirectionSummary {
            get {
                if (OpenFaceDirections.Count == 0) return "";
                var unique = OpenFaceDirections.Distinct().ToList();
                return string.Join("+", unique);
            }
        }

        /// <summary>Classify the open-face normal into a human-readable cardinal direction.</summary>
        public static string ClassifyDirection(float nx, float ny, float nz) {
            var absX = MathF.Abs(nx);
            var absY = MathF.Abs(ny);
            var absZ = MathF.Abs(nz);

            if (absZ > absX && absZ > absY)
                return nz > 0 ? "Up" : "Down";

            if (absX > absY)
                return nx > 0 ? "E" : "W";

            return ny > 0 ? "N" : "S";
        }

        public IEnumerable<int> GetAllSourceLandblocks() {
            if (SourceLandblocks.Count > 0) return SourceLandblocks;
            if (SourceLandblock != 0) return new[] { SourceLandblock };
            return Array.Empty<int>();
        }

        public bool HasSourceLandblock(int landblock) =>
            SourceLandblocks.Contains(landblock) || SourceLandblock == landblock;

        public IEnumerable<int> GetBuildingLinkedSourceLandblocks() {
            if (SourceBuildingLinkedLandblocks.Count > 0) return SourceBuildingLinkedLandblocks;
            if (SourceHasBuildings && SourceLandblock != 0) return new[] { SourceLandblock };
            return Array.Empty<int>();
        }
    }

    public class PrefabCell {
        public int LocalIndex { get; set; }
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public int PortalCount { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float RotW { get; set; } = 1f;
        public List<ushort> Surfaces { get; set; } = new();
        /// <summary>True if this cell has at least one ceiling polygon (normal.Z &lt; -0.7).</summary>
        public bool HasCeiling { get; set; } = true;
    }

    public class PrefabPortal {
        public int CellIndexA { get; set; }
        public ushort PolyIdA { get; set; }
        public int CellIndexB { get; set; }
        public ushort PolyIdB { get; set; }
    }

    public class PrefabOpenFace {
        public int CellIndex { get; set; }
        public ushort PolyId { get; set; }
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public float NormalX { get; set; }
        public float NormalY { get; set; }
        public float NormalZ { get; set; }
        /// <summary>Cardinal direction label derived from normal (N/S/E/W/Up/Down).</summary>
        public string DirectionLabel => DungeonPrefab.ClassifyDirection(NormalX, NormalY, NormalZ);
    }

    public class CatalogRoom {
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public string Category { get; set; } = "";
        public string Size { get; set; } = "";
        public string Style { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int PortalCount { get; set; }
        /// <summary>Verified portal count from actual CellStruct geometry (not CellPortals which can differ).</summary>
        public int VerifiedPortalCount { get; set; }
        /// <summary>Actual portal polygon IDs from the CellStruct.</summary>
        public List<ushort> PortalPolyIds { get; set; } = new();
        public int UsageCount { get; set; }
        public float BoundsWidth { get; set; }
        public float BoundsDepth { get; set; }
        public float BoundsHeight { get; set; }
        public List<string> SourceDungeons { get; set; } = new();
        public List<ushort> SampleSurfaces { get; set; } = new();
        /// <summary>Fraction of observed instances with non-zero RestrictionObj.</summary>
        public float RestrictionRate { get; set; }
        /// <summary>Fraction of observed portals that lead to outside (-1 cell).</summary>
        public float OutsidePortalRate { get; set; }
        /// <summary>Average VisibleCells count on observed instances.</summary>
        public float MeanVisibleCells { get; set; }
        public bool HasCeiling { get; set; } = true;
        public List<PortalDimension> PortalDimensions { get; set; } = new();
    }

    public class PortalDimension {
        public ushort PolyId { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }

    public class RoomStaticSet {
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public List<StaticPlacement> Placements { get; set; } = new();
    }

    public class StaticPlacement {
        public uint ObjectId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float RotW { get; set; } = 1f;
        public int Frequency { get; set; }
    }

    /// <summary>
    /// A complete blueprint of a real dungeon from the DAT. Stores enough data
    /// to reconstruct the dungeon exactly or to re-skin it with different room types.
    /// </summary>
    public class DungeonTemplate {
        public ushort SourceLandblock { get; set; }
        public bool SourceHasBuildings { get; set; }
        public int SourceBuildingCount { get; set; }
        public string DungeonName { get; set; } = "";
        public string Style { get; set; } = "";
        public int CellCount { get; set; }
        public string GraphType { get; set; } = "";
        public int MaxDepth { get; set; }
        public int BranchCount { get; set; }
        public List<TemplateNode> Nodes { get; set; } = new();
        public List<TemplateConnection> Connections { get; set; } = new();
    }

    public class TemplateNode {
        public int Index { get; set; }
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public int PortalCount { get; set; }
        public string Role { get; set; } = "";
        public List<int> ConnectedTo { get; set; } = new();
        /// <summary>Position relative to the first cell (cell-0-relative).</summary>
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float RotW { get; set; } = 1f;
        public List<ushort> Surfaces { get; set; } = new();
    }

    /// <summary>
    /// A portal connection between two nodes in a dungeon template,
    /// with the exact polygon IDs used on each side.
    /// </summary>
    public class TemplateConnection {
        public int NodeA { get; set; }
        public ushort PolyIdA { get; set; }
        public int NodeB { get; set; }
        public ushort PolyIdB { get; set; }
    }
}
