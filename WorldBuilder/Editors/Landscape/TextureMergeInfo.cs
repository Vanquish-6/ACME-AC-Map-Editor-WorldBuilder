using System.Collections.Generic;
using Acme.Dat;

namespace WorldBuilder.Editors.Landscape {
    /// <summary>
    /// Terrain texture-merge description used by landscape rendering.
    /// </summary>
    public class TextureMergeInfo {
        public enum RotationCorner { NW = 0, SW = 1, SE = 2, NE = 3 }
        public enum SideCorner {
            None = 0, SW = 1, SE = 2, South = 3, NE = 4, East = 6, NW = 8, West = 9, North = 12
        }
        public enum AlphaIndex { Southwest = 0, Southeast = 1, Northeast = 2, Northwest = 3, Side = 5, None = -1 }
        public enum Direction {
            Inside = 0, North = 1, South = 2, East = 3, West = 4,
            NorthWest = 5, SouthWest = 6, NorthEast = 7, SouthEast = 8, Unknown = 9
        }
        public enum WaterType { NotWater = 0, PartiallyWater = 1, EntirelyWater = 2 }
        public enum Rotation { Rot0 = 0, Rot90 = 1, Rot180 = 2, Rot270 = 3 }
        public enum PaletteType { SWTerrain = 0, SETerrain = 1, NETerrain = 2, NWTerrain = 3, Road = 4 }

        public TerrainTex? TerrainBase { get; set; }
        public List<TerrainTex?> TerrainOverlays { get; set; } = new() { null, null, null };
        public List<TerrainAlphaMap?> TerrainAlphaOverlays { get; set; } = new() { null, null, null };
        public List<int> TerrainAlphaIndices { get; set; } = new() { -1, -1, -1 };
        public List<Rotation> TerrainRotations { get; set; } = new() { Rotation.Rot0, Rotation.Rot0, Rotation.Rot0 };
        public TerrainTex? RoadOverlay { get; set; }
        public List<RoadAlphaMap?> RoadAlphaOverlays { get; set; } = new() { null, null };
        public List<int> RoadAlphaIndices { get; set; } = new() { -1, -1 };
        public List<Rotation> RoadRotations { get; set; } = new() { Rotation.Rot0, Rotation.Rot0 };
        public List<uint> TerrainCodes { get; set; } = new();

        public void PostProcessing() {
            Compact(TerrainOverlays, TerrainAlphaOverlays, TerrainAlphaIndices, TerrainRotations);
            CompactRoad(RoadAlphaOverlays, RoadAlphaIndices, RoadRotations);
        }

        public void PrintDebugInfo() { }

        private static void Compact<TOverlay, TAlpha>(
            List<TOverlay?> overlays,
            List<TAlpha?> alphaOverlays,
            List<int> alphaIndices,
            List<Rotation> rotations)
            where TOverlay : class
            where TAlpha : class {
            for (int i = overlays.Count - 1; i >= 0; i--) {
                if (overlays[i] is not null) continue;
                overlays.RemoveAt(i);
                if (i < alphaOverlays.Count) alphaOverlays.RemoveAt(i);
                if (i < alphaIndices.Count) alphaIndices.RemoveAt(i);
                if (i < rotations.Count) rotations.RemoveAt(i);
            }
        }

        private static void CompactRoad(List<RoadAlphaMap?> alphaOverlays, List<int> alphaIndices, List<Rotation> rotations) {
            for (int i = alphaOverlays.Count - 1; i >= 0; i--) {
                if (alphaOverlays[i] is not null) continue;
                alphaOverlays.RemoveAt(i);
                if (i < alphaIndices.Count) alphaIndices.RemoveAt(i);
                if (i < rotations.Count) rotations.RemoveAt(i);
            }
        }
    }

    public class SurfaceInfo {
        public TextureMergeInfo? Surface { get; set; }
        public uint PaletteCode { get; set; }
        public int LandCellCount { get; set; }
        public uint SurfaceNumber { get; set; }
    }
}
