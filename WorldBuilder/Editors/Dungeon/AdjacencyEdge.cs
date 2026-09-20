namespace WorldBuilder.Editors.Dungeon {

    public class AdjacencyEdge {
        public ushort EnvIdA { get; set; }
        public ushort CellStructA { get; set; }
        public ushort PolyIdA { get; set; }
        public ushort EnvIdB { get; set; }
        public ushort CellStructB { get; set; }
        public ushort PolyIdB { get; set; }
        public int Count { get; set; }
        public float RelOffsetX { get; set; }
        public float RelOffsetY { get; set; }
        public float RelOffsetZ { get; set; }
        public float RelRotX { get; set; }
        public float RelRotY { get; set; }
        public float RelRotZ { get; set; }
        public float RelRotW { get; set; } = 1f;

        // Portal geometry for side A
        public float WidthA { get; set; }
        public float HeightA { get; set; }
        public float AreaA { get; set; }
        public int VertexCountA { get; set; }

        // Portal geometry for side B
        public float WidthB { get; set; }
        public float HeightB { get; set; }
        public float AreaB { get; set; }
        public int VertexCountB { get; set; }

        // Portal flags from real data
        public bool ExactMatch { get; set; }
        public int PortalSideA { get; set; }
        public int PortalSideB { get; set; }

        // Per-side environment hints from sampled real cells
        public bool RestrictionA { get; set; }
        public bool RestrictionB { get; set; }
        public int VisibleCellCountA { get; set; }
        public int VisibleCellCountB { get; set; }
    }
}
