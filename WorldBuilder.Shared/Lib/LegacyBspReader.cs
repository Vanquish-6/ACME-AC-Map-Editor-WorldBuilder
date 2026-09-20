using Acme.Dat;
using System.Numerics;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Reads legacy Dark Majesty BSP trees and maps them to retail node types.
/// </summary>
internal static class LegacyBspReader {
    private const uint LegacyBspPort = 0x504F5254;
    private const uint LegacyBspLeaf = 0x4C454146;

    private enum LegacyBspKind {
        Drawing,
        Physics,
        Cell,
    }

    internal static CellBSPNode ReadCellNode(DatBinReader reader) =>
        ReadCellNode(reader, LegacyBspKind.Cell);

    internal static PhysicsBSPNode ReadPhysicsNode(DatBinReader reader) =>
        ReadPhysicsNode(reader, LegacyBspKind.Physics);

    internal static DrawingBSPNode ReadDrawingNode(DatBinReader reader) =>
        ReadDrawingNode(reader, LegacyBspKind.Drawing);

    internal static void FillMissingRetailBsp(CellStruct cellStruct) {
        var verts = cellStruct.VertexArray?.Vertices ?? new Dictionary<ushort, SWVertex>();
        var drawPolys = cellStruct.Polygons ?? new Dictionary<ushort, Polygon>();
        if (cellStruct.PhysicsPolygons is not { Count: > 0 } && drawPolys.Count > 0) {
            cellStruct.PhysicsPolygons = new Dictionary<ushort, Polygon>(drawPolys);
        }

        var physicsPolys = cellStruct.PhysicsPolygons is { Count: > 0 }
            ? cellStruct.PhysicsPolygons
            : drawPolys;

        cellStruct.CellBSP ??= new CellBSPTree();
        if (cellStruct.CellBSP.Root == null) {
            cellStruct.CellBSP.Root = BspGenerator.BuildLegacyCellRootForExport(drawPolys, verts);
        }

        cellStruct.PhysicsBSP ??= new PhysicsBSPTree();
        if (cellStruct.PhysicsBSP.Root == null) {
            cellStruct.PhysicsBSP.Root = BspGenerator.BuildLegacyPhysicsRootForExport(physicsPolys, verts);
        }

        if (drawPolys.Count > 0 && (cellStruct.DrawingBSP?.Root == null)) {
            cellStruct.DrawingBSP = new DrawingBSPTree {
                Root = BspGenerator.BuildLegacyDrawingRootForExport(
                    drawPolys,
                    verts,
                    BspGenerator.BuildPortalRefs(cellStruct, drawPolys)),
            };
        }
    }

    internal static void FillMissingRetailBsp(GfxObj gfxObj) {
        var verts = gfxObj.VertexArray?.Vertices ?? new Dictionary<ushort, SWVertex>();

        if (gfxObj.Flags.HasFlag(GfxObjFlags.HasPhysics)
            && gfxObj.PhysicsPolygons is { Count: > 0 }
            && (gfxObj.PhysicsBSP?.Root == null)) {
            gfxObj.PhysicsBSP = new PhysicsBSPTree {
                Root = BspGenerator.BuildLegacyPhysicsRootForExport(gfxObj.PhysicsPolygons, verts),
            };
        }

        if (gfxObj.Flags.HasFlag(GfxObjFlags.HasDrawing)
            && gfxObj.Polygons is { Count: > 0 }
            && (gfxObj.DrawingBSP?.Root == null)) {
            var keys = gfxObj.Polygons.Keys.OrderBy(id => id).ToList();
            gfxObj.DrawingBSP = new DrawingBSPTree {
                Root = BspGenerator.DrawingPolygonNode(keys, gfxObj.Polygons, verts),
            };
        }
    }

    private static CellBSPNode ReadCellNode(DatBinReader reader, LegacyBspKind kind) {
        uint rawType = reader.ReadUInt32();
        if (rawType == LegacyBspLeaf) {
            var leaf = new CellBSPNode {
                Type = BSPNodeType.Leaf,
                LeafIndex = reader.ReadInt32(),
            };
            reader.Align(4);
            return leaf;
        }

        if (rawType == LegacyBspPort) {
            throw new InvalidDataException("Legacy cell BSP unexpectedly contained a PORT node.");
        }

        var plane = ReadPlane(reader);
        var type = DecodeLegacyBspType(rawType);
        var node = new CellBSPNode {
            Type = MapBranchType(type),
            SplittingPlane = plane,
            LeafIndex = -1,
        };

        switch (type) {
            case "BPnn":
            case "BPIn":
                node.PosNode = ReadCellNode(reader, kind);
                break;
            case "BpIN":
            case "BpnN":
                node.NegNode = ReadCellNode(reader, kind);
                break;
            case "BPIN":
            case "BPnN":
                node.PosNode = ReadCellNode(reader, kind);
                node.NegNode = ReadCellNode(reader, kind);
                break;
        }

        return node;
    }

    private static PhysicsBSPNode ReadPhysicsNode(DatBinReader reader, LegacyBspKind kind) {
        uint rawType = reader.ReadUInt32();
        if (rawType == LegacyBspLeaf) {
            int leafIndex = reader.ReadInt32();
            int solid = reader.ReadInt32();
            var sphere = ReadSphere(reader);
            var polygons = ReadPolygonIdList(reader);
            reader.Align(4);
            var leaf = new PhysicsBSPNode {
                Type = BSPNodeType.Leaf,
                LeafIndex = leafIndex,
                BoundingSphere = sphere,
                Polygons = polygons,
            };
            BspGenerator.SetPhysicsSolid(leaf, solid);
            return leaf;
        }

        if (rawType == LegacyBspPort) {
            throw new InvalidDataException("Legacy physics BSP unexpectedly contained a PORT node.");
        }

        var plane = ReadPlane(reader);
        var type = DecodeLegacyBspType(rawType);
        var node = new PhysicsBSPNode {
            Type = MapBranchType(type),
            SplittingPlane = plane,
            LeafIndex = -1,
            Polygons = new List<ushort>(),
        };

        switch (type) {
            case "BPnn":
            case "BPIn":
                node.PosNode = ReadPhysicsNode(reader, kind);
                break;
            case "BpIN":
            case "BpnN":
                node.NegNode = ReadPhysicsNode(reader, kind);
                break;
            case "BPIN":
            case "BPnN":
                node.PosNode = ReadPhysicsNode(reader, kind);
                node.NegNode = ReadPhysicsNode(reader, kind);
                break;
        }

        node.BoundingSphere = ReadSphere(reader);
        return node;
    }

    private static DrawingBSPNode ReadDrawingNode(DatBinReader reader, LegacyBspKind kind) {
        uint rawType = reader.ReadUInt32();
        if (rawType == LegacyBspLeaf) {
            var leaf = new DrawingBSPNode {
                Type = BSPNodeType.Leaf,
                LeafIndex = reader.ReadInt32(),
                Polygons = new List<ushort>(),
                Portals = new List<PortalRef>(),
            };
            reader.Align(4);
            return leaf;
        }

        if (rawType == LegacyBspPort) {
            var plane = ReadPlane(reader);
            var node = new DrawingBSPNode {
                Type = BSPNodeType.Portal,
                SplittingPlane = plane,
                LeafIndex = -1,
                Polygons = new List<ushort>(),
                Portals = new List<PortalRef>(),
                PosNode = ReadDrawingNode(reader, kind),
                NegNode = ReadDrawingNode(reader, kind),
            };

            node.BoundingSphere = ReadSphere(reader);
            uint polygonCount = reader.ReadUInt32();
            uint portalCount = reader.ReadUInt32();
            for (int i = 0; i < polygonCount; i++) {
                node.Polygons.Add(reader.ReadUInt16());
            }

            for (int i = 0; i < portalCount; i++) {
                node.Portals.Add(new PortalRef {
                    PolyId = (ushort)reader.ReadInt16(),
                    PortalIndex = (ushort)reader.ReadInt16(),
                });
            }

            reader.Align(4);
            return node;
        }

        var branchPlane = ReadPlane(reader);
        var branchType = DecodeLegacyBspType(rawType);
        var branchNode = new DrawingBSPNode {
            Type = MapBranchType(branchType),
            SplittingPlane = branchPlane,
            LeafIndex = -1,
            Polygons = new List<ushort>(),
            Portals = new List<PortalRef>(),
        };

        switch (branchType) {
            case "BPnn":
            case "BPIn":
                branchNode.PosNode = ReadDrawingNode(reader, kind);
                break;
            case "BpIN":
            case "BpnN":
                branchNode.NegNode = ReadDrawingNode(reader, kind);
                break;
            case "BPIN":
            case "BPnN":
                branchNode.PosNode = ReadDrawingNode(reader, kind);
                branchNode.NegNode = ReadDrawingNode(reader, kind);
                break;
        }

        branchNode.BoundingSphere = ReadSphere(reader);
        uint branchPolyCount = reader.ReadUInt32();
        for (int i = 0; i < branchPolyCount; i++) {
            branchNode.Polygons.Add(reader.ReadUInt16());
        }

        reader.Align(4);
        return branchNode;
    }

    private static List<ushort> ReadPolygonIdList(DatBinReader reader) {
        uint polygonCount = reader.ReadUInt32();
        var polygons = new List<ushort>((int)polygonCount);
        for (int i = 0; i < polygonCount; i++) {
            polygons.Add(reader.ReadUInt16());
        }

        return polygons;
    }

    private static Plane ReadPlane(DatBinReader reader) {
        var normal = reader.ReadVector3();
        float d = reader.ReadSingle();
        return new Plane(normal, d);
    }

    private static Sphere ReadSphere(DatBinReader reader) {
        return new Sphere {
            Origin = reader.ReadVector3(),
            Radius = reader.ReadSingle(),
        };
    }

    private static BSPNodeType MapBranchType(string type) => type switch {
        "BPnn" => BSPNodeType.BPnn,
        "BPIn" => BSPNodeType.BPIn,
        "BpIN" => BSPNodeType.BpIN,
        "BpnN" => BSPNodeType.BpnN,
        "BPIN" => BSPNodeType.BPIN,
        "BPnN" => BSPNodeType.BPnN,
        _ => BSPNodeType.BPIN,
    };

    private static string DecodeLegacyBspType(uint rawType) {
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, rawType);
        bytes.Reverse();
        return System.Text.Encoding.ASCII.GetString(bytes);
    }
}
