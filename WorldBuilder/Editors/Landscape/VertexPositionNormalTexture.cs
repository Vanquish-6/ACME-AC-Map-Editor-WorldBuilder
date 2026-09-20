using Acme.Render.Enums;
using Acme.Render.Vertex;
using System.Numerics;
using System.Runtime.InteropServices;

namespace WorldBuilder.Editors.Landscape {
    /// <summary>
    /// Represents a vertex with position and normal
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VertexPositionNormalTexture : IVertex {
        private static readonly VertexFormat _format = new VertexFormat(
            new VertexAttribute(VertexAttributeName.Position, 3, VertexAttribType.Float, false, 0),
            new VertexAttribute(VertexAttributeName.Normal, 3, VertexAttribType.Float, false, 12),
            new VertexAttribute(VertexAttributeName.TexCoord0, 2, VertexAttribType.Float, false, 24)
        );

        /// <summary>
        /// The size of the vertex, in bytes
        /// </summary>
        public static int Size => Marshal.SizeOf<VertexPositionNormalTexture>();

        /// <summary>
        /// The vertex format for this vertex type
        /// </summary>
        public static VertexFormat Format => _format;

        int IVertex.Size => Size;
        VertexFormat IVertex.Format => Format;

        /// <summary>
        /// The position
        /// </summary>
        public Vector3 Position;

        /// <summary>
        /// The normal
        /// </summary>
        public Vector3 Normal;

        /// <summary>
        /// The normal
        /// </summary>
        public Vector2 UV;

        /// <summary>
        /// Constructs a vertex
        /// </summary>
        /// <param name="position"></param>
        /// <param name="normal"></param>
        /// <param name="uv"></param>
        public VertexPositionNormalTexture(Vector3 position, Vector3 normal, Vector2 uv) {
            Position = position;
            Normal = normal;
            UV = uv;
        }
    }
}
