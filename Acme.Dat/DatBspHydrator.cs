using System.Buffers.Binary;
using System.Numerics;

namespace Acme.Dat;

/// <summary>
/// Rehydrates the editor-facing BSP trees from the raw byte fields carried by
/// the native DAT interchange records.
/// </summary>
internal static class DatBspHydrator {
    private const uint LeafTag = 0x4C454146;
    private const uint PortalTag = 0x504F5254;

    public static void Hydrate(GfxObj gfx) {
        if (gfx.Physics?.PhysicsBspBytes is { Length: > 0 } physicsBytes
            && TryReadPhysics(physicsBytes, out var physicsRoot)) {
            gfx.PhysicsBSP = new PhysicsBSPTree { Root = physicsRoot };
        }

        if (gfx.DrawingBspBytes is { Length: > 0 } drawingBytes
            && TryReadDrawing(drawingBytes, out var drawingRoot)) {
            gfx.DrawingBSP = new DrawingBSPTree { Root = drawingRoot };
        }
    }

    public static void Hydrate(CellStruct cell) {
        if (cell.CellBspBytes is { Length: > 0 } cellBytes
            && TryReadCell(cellBytes, out var cellRoot)) {
            cell.CellBSP = new CellBSPTree { Root = cellRoot };
        }

        if (cell.PhysicsBspBytes is { Length: > 0 } physicsBytes
            && TryReadPhysics(physicsBytes, out var physicsRoot)) {
            cell.PhysicsBSP = new PhysicsBSPTree { Root = physicsRoot };
        }

        if (cell.DrawingTailBytes is { Length: > 0 } drawingBytes
            && TryReadDrawing(drawingBytes, out var drawingRoot)) {
            cell.DrawingBSP = new DrawingBSPTree { Root = drawingRoot };
        }
    }

    private static bool TryReadPhysics(byte[] bytes, out PhysicsBSPNode? root) {
        root = null;
        try {
            var reader = new BspReader(bytes);
            root = ReadPhysics(reader);
            return root != null;
        }
        catch {
            root = null;
            return false;
        }
    }

    private static bool TryReadDrawing(byte[] bytes, out DrawingBSPNode? root) {
        root = null;
        try {
            var reader = new BspReader(bytes);
            root = ReadDrawing(reader);
            return root != null;
        }
        catch {
            root = null;
            return false;
        }
    }

    private static bool TryReadCell(byte[] bytes, out CellBSPNode? root) {
        root = null;
        try {
            var reader = new BspReader(bytes);
            root = ReadCell(reader);
            return root != null;
        }
        catch {
            root = null;
            return false;
        }
    }

    private static PhysicsBSPNode ReadPhysics(BspReader reader) {
        uint rawType = reader.ReadUInt32();
        if (rawType == LeafTag) {
            var leaf = new PhysicsBSPNode {
                Type = BSPNodeType.Leaf,
                LeafIndex = reader.ReadInt32(),
            };
            SetSolid(leaf, reader.ReadInt32());
            leaf.BoundingSphere = ReadSphere(reader);
            leaf.Polygons = ReadPolygonIds(reader);
            reader.Align(4);
            return leaf;
        }

        var type = MapType(rawType);
        var node = new PhysicsBSPNode {
            Type = type,
            SplittingPlane = ReadPlane(reader),
            LeafIndex = -1,
            Polygons = [],
        };
        ReadPhysicsChildren(reader, node);
        node.BoundingSphere = ReadSphere(reader);
        return node;
    }

    private static void ReadPhysicsChildren(BspReader reader, PhysicsBSPNode node) {
        switch (node.Type) {
            case BSPNodeType.BPnn:
            case BSPNodeType.BPIn:
                node.PosNode = ReadPhysics(reader);
                break;
            case BSPNodeType.BpIN:
            case BSPNodeType.BpnN:
                node.NegNode = ReadPhysics(reader);
                break;
            default:
                node.PosNode = ReadPhysics(reader);
                node.NegNode = ReadPhysics(reader);
                break;
        }
    }

    private static DrawingBSPNode ReadDrawing(BspReader reader) {
        uint rawType = reader.ReadUInt32();
        if (rawType == LeafTag) {
            var leaf = new DrawingBSPNode {
                Type = BSPNodeType.Leaf,
                LeafIndex = reader.ReadInt32(),
                Polygons = [],
                Portals = [],
            };
            reader.Align(4);
            return leaf;
        }

        if (rawType == PortalTag) {
            var portal = new DrawingBSPNode {
                Type = BSPNodeType.Portal,
                SplittingPlane = ReadPlane(reader),
                LeafIndex = -1,
                Polygons = [],
                Portals = [],
                PosNode = ReadDrawing(reader),
                NegNode = ReadDrawing(reader),
                BoundingSphere = ReadSphere(reader),
            };
            uint portalPolygonCount = ReadCount(reader);
            uint portalCount = ReadCount(reader);
            for (uint i = 0; i < portalPolygonCount; i++) {
                portal.Polygons.Add(reader.ReadUInt16());
            }

            for (uint i = 0; i < portalCount; i++) {
                portal.Portals.Add(new PortalRef {
                    PolygonId = (ushort)reader.ReadInt16(),
                    PortalIndex = (ushort)reader.ReadInt16(),
                });
            }

            reader.Align(4);
            return portal;
        }

        var type = MapType(rawType);
        var node = new DrawingBSPNode {
            Type = type,
            SplittingPlane = ReadPlane(reader),
            LeafIndex = -1,
            Polygons = [],
            Portals = [],
        };
        if (type != BSPNodeType.BPOL) {
            ReadDrawingChildren(reader, node);
        }

        node.BoundingSphere = ReadSphere(reader);
        uint polygonCount = ReadCount(reader);
        for (uint i = 0; i < polygonCount; i++) {
            node.Polygons.Add(reader.ReadUInt16());
        }

        reader.Align(4);
        return node;
    }

    private static void ReadDrawingChildren(BspReader reader, DrawingBSPNode node) {
        switch (node.Type) {
            case BSPNodeType.BPnn:
            case BSPNodeType.BPIn:
                node.PosNode = ReadDrawing(reader);
                break;
            case BSPNodeType.BpIN:
            case BSPNodeType.BpnN:
                node.NegNode = ReadDrawing(reader);
                break;
            default:
                node.PosNode = ReadDrawing(reader);
                node.NegNode = ReadDrawing(reader);
                break;
        }
    }

    private static CellBSPNode ReadCell(BspReader reader) {
        uint rawType = reader.ReadUInt32();
        if (rawType == LeafTag) {
            var leaf = new CellBSPNode {
                Type = BSPNodeType.Leaf,
                LeafIndex = reader.ReadInt32(),
            };
            reader.Align(4);
            return leaf;
        }

        var type = MapType(rawType);
        var node = new CellBSPNode {
            Type = type,
            SplittingPlane = ReadPlane(reader),
            LeafIndex = -1,
        };
        switch (type) {
            case BSPNodeType.BPnn:
            case BSPNodeType.BPIn:
                node.PosNode = ReadCell(reader);
                break;
            case BSPNodeType.BpIN:
            case BSPNodeType.BpnN:
                node.NegNode = ReadCell(reader);
                break;
            default:
                node.PosNode = ReadCell(reader);
                node.NegNode = ReadCell(reader);
                break;
        }

        return node;
    }

    private static List<ushort> ReadPolygonIds(BspReader reader) {
        uint count = ReadCount(reader);
        var ids = new List<ushort>(checked((int)count));
        for (uint i = 0; i < count; i++) {
            ids.Add(reader.ReadUInt16());
        }

        return ids;
    }

    private static uint ReadCount(BspReader reader) {
        uint count = reader.ReadUInt32();
        if (count > 0x100000) {
            throw new InvalidDataException("BSP polygon count is too large.");
        }

        return count;
    }

    private static Plane ReadPlane(BspReader reader) =>
        new(reader.ReadVector3(), reader.ReadSingle());

    private static SphereBounds ReadSphere(BspReader reader) => new() {
        Origin = reader.ReadVector3(),
        Radius = reader.ReadSingle(),
    };

    private static BSPNodeType MapType(uint rawType) => rawType switch {
        0x42506E6E => BSPNodeType.BPnn,
        0x4250496E => BSPNodeType.BPIn,
        0x4270494E => BSPNodeType.BpIN,
        0x42706E4E => BSPNodeType.BpnN,
        0x4250494E => BSPNodeType.BPIN,
        0x42506E4E => BSPNodeType.BPnN,
        0x42504F4C => BSPNodeType.BPOL,
        _ => throw new InvalidDataException($"Unknown BSP node type 0x{rawType:X8}."),
    };

    private static void SetSolid(PhysicsBSPNode node, int value) => node.Solid = value;

    private sealed class BspReader {
        private readonly byte[] _bytes;
        private int _offset;

        public BspReader(byte[] bytes) => _bytes = bytes;

        public uint ReadUInt32() {
            Ensure(sizeof(uint));
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(_offset));
            _offset += sizeof(uint);
            return value;
        }

        public int ReadInt32() => checked((int)ReadUInt32());

        public ushort ReadUInt16() {
            Ensure(sizeof(ushort));
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(_offset));
            _offset += sizeof(ushort);
            return value;
        }

        public short ReadInt16() => checked((short)ReadUInt16());

        public float ReadSingle() {
            Ensure(sizeof(float));
            float value = BitConverter.ToSingle(_bytes, _offset);
            _offset += sizeof(float);
            return value;
        }

        public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());

        public void Align(int alignment) {
            int padding = (alignment - (_offset % alignment)) % alignment;
            Ensure(padding);
            _offset += padding;
        }

        void Ensure(int count) {
            if (count < 0 || _offset > _bytes.Length - count) {
                throw new InvalidDataException("BSP byte payload ended unexpectedly.");
            }
        }
    }
}
