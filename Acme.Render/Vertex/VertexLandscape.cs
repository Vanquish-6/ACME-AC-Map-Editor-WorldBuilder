using System.Numerics;
using System.Runtime.InteropServices;
using Acme.Render.Enums;

namespace Acme.Render.Vertex {
    /// <summary>
    /// Landscape vertex. Binary layout matches Chorizite.Core 0.0.17 (Marshal.SizeOf = 48):
    /// Offset  0: Vector3 Position
    /// Offset 12: Vector3 Normal
    /// Offset 24: uint PackedBase      (shader loc 2: uvec4 via 4 unsigned bytes)
    /// Offset 28: uint PackedOverlay0 (loc 3)
    /// Offset 32: uint PackedOverlay1 (loc 4)
    /// Offset 36: uint PackedOverlay2 (loc 5)
    /// Offset 40: uint PackedRoad0    (loc 6)
    /// Offset 44: uint PackedRoad1    (loc 7)
    /// PackTexCoord: low byte has UV in bits 4-5 (U) and 6-7 (V) as 0..2 for -1..1;
    /// byte 2 = texIdx, byte 3 = alphaIdx.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VertexLandscape : IVertex {
        private static readonly VertexFormat _format = new(
            new VertexAttribute(VertexAttributeName.Position, 3, VertexAttribType.Float, false, 0),
            new VertexAttribute(VertexAttributeName.Normal, 3, VertexAttribType.Float, false, 12),
            new VertexAttribute(VertexAttributeName.TexCoord0, 4, VertexAttribType.UnsignedByte, false, 24),
            new VertexAttribute(VertexAttributeName.TexCoord1, 4, VertexAttribType.UnsignedByte, false, 28),
            new VertexAttribute(VertexAttributeName.TexCoord2, 4, VertexAttribType.UnsignedByte, false, 32),
            new VertexAttribute(VertexAttributeName.TexCoord3, 4, VertexAttribType.UnsignedByte, false, 36),
            new VertexAttribute(VertexAttributeName.TexCoord4, 4, VertexAttribType.UnsignedByte, false, 40),
            new VertexAttribute(VertexAttributeName.TexCoord5, 4, VertexAttribType.UnsignedByte, false, 44)
        );

        public static int Size => 48;
        public static VertexFormat Format => _format;

        int IVertex.Size => Size;
        VertexFormat IVertex.Format => Format;

        public Vector3 Position;
        public Vector3 Normal;
        public uint PackedBase;
        public uint PackedOverlay0;
        public uint PackedOverlay1;
        public uint PackedOverlay2;
        public uint PackedRoad0;
        public uint PackedRoad1;

        public static uint PackTexCoord(float u, float v, byte texIdx, byte alphaIdx) {
            uint packedU = (uint)((int)u + 1) & 3u;
            uint packedV = (uint)((int)v + 1) & 3u;
            return (packedU << 4) | (packedV << 6) | ((uint)texIdx << 16) | ((uint)alphaIdx << 24);
        }

        public void SetBase(float u, float v, byte texIdx, byte alphaIdx) =>
            PackedBase = PackTexCoord(u, v, texIdx, alphaIdx);

        public void SetOverlay0(float u, float v, byte texIdx, byte alphaIdx) =>
            PackedOverlay0 = PackTexCoord(u, v, texIdx, alphaIdx);

        public void SetOverlay1(float u, float v, byte texIdx, byte alphaIdx) =>
            PackedOverlay1 = PackTexCoord(u, v, texIdx, alphaIdx);

        public void SetOverlay2(float u, float v, byte texIdx, byte alphaIdx) =>
            PackedOverlay2 = PackTexCoord(u, v, texIdx, alphaIdx);

        public void SetRoad0(float u, float v, byte texIdx, byte alphaIdx) =>
            PackedRoad0 = PackTexCoord(u, v, texIdx, alphaIdx);

        public void SetRoad1(float u, float v, byte texIdx, byte alphaIdx) =>
            PackedRoad1 = PackTexCoord(u, v, texIdx, alphaIdx);
    }
}
