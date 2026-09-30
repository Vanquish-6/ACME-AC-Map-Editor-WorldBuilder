using System.Numerics;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib.MonsterBuilder;

/// <summary>
/// Minimal retail drawing/physics BSP blobs for a replaced GfxObj.
/// The layout matches the ToD drawing/physics trees ACME already stores as
/// <c>drawing_bsp_bytes</c> / <c>physics_bsp_bytes</c>: a BPOL node lists every
/// drawing polygon, and a solid LEAF lists every physics polygon.
/// Unmodified clones keep the source bytes instead of calling this.
/// </summary>
public static class RetailBspBytes {
    const uint LeafTag = 0x4C454146;
    const uint BpolTag = 0x42504F4C;

    public static byte[] DrawingPolygonNode(IReadOnlyList<ushort> polygonIds, Vector3 center, float radius) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(BpolTag);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write(0f);
        WriteSphere(writer, center, radius);
        writer.Write(polygonIds.Count);
        foreach (ushort id in polygonIds)
            writer.Write(id);
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] PhysicsLeaf(IReadOnlyList<ushort> polygonIds, Vector3 center, float radius) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(LeafTag);
        writer.Write(0);
        writer.Write(1);
        WriteSphere(writer, center, radius);
        writer.Write(polygonIds.Count);
        foreach (ushort id in polygonIds)
            writer.Write(id);
        writer.Flush();
        return stream.ToArray();
    }

    static void WriteSphere(BinaryWriter writer, Vector3 center, float radius) {
        writer.Write(center.X);
        writer.Write(center.Y);
        writer.Write(center.Z);
        writer.Write(MathF.Max(radius, 0.01f));
    }
}
