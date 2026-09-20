using System.Numerics;
using System.Runtime.InteropServices;
using Acme.Render.Enums;

namespace Acme.Render.Vertex {
    [StructLayout(LayoutKind.Sequential)]
    public struct VertexPositionColorTexture : IVertex {
        private static readonly VertexFormat _format = new(
            new VertexAttribute(VertexAttributeName.Position, 3, VertexAttribType.Float, false, 0),
            new VertexAttribute(VertexAttributeName.Color, 4, VertexAttribType.Float, false, 12),
            new VertexAttribute(VertexAttributeName.TexCoords, 2, VertexAttribType.Float, false, 28)
        );

        public static int Size => 36;
        public static VertexFormat Format => _format;

        int IVertex.Size => Size;
        VertexFormat IVertex.Format => Format;

        public Vector3 Position;
        public ColorVec Color;
        public Vector2 TexCoords;

        public VertexPositionColorTexture(Vector3 position, ColorVec color, Vector2 texCoords) {
            Position = position;
            Color = color;
            TexCoords = texCoords;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VertexPositionNormal : IVertex {
        private static readonly VertexFormat _format = new(
            new VertexAttribute(VertexAttributeName.Position, 3, VertexAttribType.Float, false, 0),
            new VertexAttribute(VertexAttributeName.Normal, 3, VertexAttribType.Float, false, 12)
        );

        public static int Size => 24;
        public static VertexFormat Format => _format;

        int IVertex.Size => Size;
        VertexFormat IVertex.Format => Format;

        public Vector3 Position;
        public Vector3 Normal;

        public VertexPositionNormal(Vector3 position, Vector3 normal) {
            Position = position;
            Normal = normal;
        }
    }
}
