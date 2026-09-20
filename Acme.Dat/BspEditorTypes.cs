using System.Numerics;
using MessagePack;

namespace Acme.Dat;

public enum BSPNodeType : uint {
    Leaf = 0x4C454146, // LEAF
    Portal = 0x504F5254, // PORT
    BPIN = 0x4250494E,
    BPIn = 0x4250496E,
    BpIN = 0x4270494E,
    BPnn = 0x42506E6E,
    BPnN = 0x42506E4E,
    BpnN = 0x42706E4E,
    BPOL = 0x42504F4C,
}

[Flags]
public enum GfxObjFlags : uint {
    HasPhysics = 0x00000001,
    HasDrawing = 0x00000002,
    HasDIDDegrade = 0x00000008,
}

public sealed class Vec2Duv {
    public float U { get; set; }
    public float V { get; set; }
}

public sealed class SWVertex {
    public Vector3 Origin { get; set; }
    public Vector3 Normal { get; set; }
    public List<Vec2Duv> UVs { get; set; } = [];
}

public sealed class VertexArray {
    public VertexType VertexType { get; set; } = VertexType.CSWVertexType;
    public Dictionary<ushort, SWVertex> Vertices { get; set; } = new();
}

public sealed class SphereBounds {
    public Vector3 Origin { get; set; }
    public float Radius { get; set; }

    public static implicit operator SphereBounds(Sphere sphere) => new() {
        Origin = sphere.Origin,
        Radius = sphere.Radius,
    };
}

public sealed class PortalRef {
    public ushort PortalIndex { get; set; }
    public ushort PolygonId { get; set; }
    public ushort PolyId { get => PolygonId; set => PolygonId = value; }
}

public sealed class PhysicsBSPNode {
    public BSPNodeType Type { get; set; }
    public Plane SplittingPlane { get; set; }
    public int LeafIndex { get; set; } = -1;
    public int Solid { get; set; }
    public SphereBounds? BoundingSphere { get; set; }
    public List<ushort> Polygons { get; set; } = [];
    public PhysicsBSPNode? PosNode { get; set; }
    public PhysicsBSPNode? NegNode { get; set; }
}

public sealed class DrawingBSPNode {
    public BSPNodeType Type { get; set; }
    public Plane SplittingPlane { get; set; }
    public int LeafIndex { get; set; } = -1;
    public SphereBounds? BoundingSphere { get; set; }
    public List<ushort> Polygons { get; set; } = [];
    public List<PortalRef> Portals { get; set; } = [];
    public DrawingBSPNode? PosNode { get; set; }
    public DrawingBSPNode? NegNode { get; set; }
}

public sealed class CellBSPNode {
    public BSPNodeType Type { get; set; }
    public Plane SplittingPlane { get; set; }
    public int LeafIndex { get; set; } = -1;
    public CellBSPNode? PosNode { get; set; }
    public CellBSPNode? NegNode { get; set; }
}

public sealed class PhysicsBSPTree {
    public PhysicsBSPNode? Root { get; set; }
}

public sealed class DrawingBSPTree {
    public DrawingBSPNode? Root { get; set; }
}

public sealed class CellBSPTree {
    public CellBSPNode? Root { get; set; }
}

public enum DatParticleType {
    Unknown = 0,
    Still = 1,
    LocalVelocity = 2,
    ParabolicLVGA = 3,
    ParabolicLVGAGR = 4,
    Swarm = 5,
    Explode = 6,
    Implode = 7,
    ParabolicLVLA = 8,
    ParabolicLVLALR = 9,
    ParabolicGVGA = 10,
    ParabolicGVGAGR = 0x0B,
    GlobalVelocity = 0x0C,
    NumParticleType = 0x0D,
}

public enum ParticleEmitterType {
    Unknown = 0,
}

public sealed partial class LandDefs {
    [IgnoreMember]
    public uint LBlockLength { get => LblockLength; set => LblockLength = value; }
}

public sealed partial class GfxObj {
    [IgnoreMember] public VertexArray VertexArray { get; set; } = new();
    [IgnoreMember] public Dictionary<ushort, Polygon> Polygons { get; set; } = new();
    [IgnoreMember] public Dictionary<ushort, Polygon> PhysicsPolygons { get; set; } = new();
    [IgnoreMember] public PhysicsBSPTree? PhysicsBSP { get; set; }
    [IgnoreMember] public DrawingBSPTree? DrawingBSP { get; set; }
    [IgnoreMember] public GfxObjFlags GfxFlags { get => (GfxObjFlags)Flags; set => Flags = (uint)value; }
}

public sealed partial class CellStruct {
    [IgnoreMember] public VertexArray VertexArray { get; set; } = new();
    [IgnoreMember] public Dictionary<ushort, Polygon> Polygons { get; set; } = new();
    [IgnoreMember] public Dictionary<ushort, Polygon> PhysicsPolygons { get; set; } = new();
    [IgnoreMember] public CellBSPTree? CellBSP { get; set; }
    [IgnoreMember] public PhysicsBSPTree? PhysicsBSP { get; set; }
    [IgnoreMember] public DrawingBSPTree? DrawingBSP { get; set; }
}

public sealed partial class ParticleEmitter {
    [IgnoreMember] public DatParticleType ParticleKind { get => (DatParticleType)ParticleType; set => ParticleType = (int)value; }
}
