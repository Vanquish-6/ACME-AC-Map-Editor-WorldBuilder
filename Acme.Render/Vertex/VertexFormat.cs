using Acme.Render.Enums;

namespace Acme.Render.Vertex {
    public interface IVertex {
        int Size { get; }
        VertexFormat Format { get; }
    }

    public struct VertexAttribute {
        public VertexAttributeName Name;
        public int Size;
        public VertexAttribType Type;
        public bool Normalized;
        public int Offset;

        public VertexAttribute(VertexAttributeName name, int size, VertexAttribType type, bool normalized, int offset) {
            Name = name;
            Size = size;
            Type = type;
            Normalized = normalized;
            Offset = offset;
        }
    }

    public class VertexFormat {
        public VertexAttribute[] Attributes { get; }
        public int Stride { get; }

        public VertexFormat(params VertexAttribute[] attributes) {
            Attributes = attributes ?? Array.Empty<VertexAttribute>();
            var stride = 0;
            foreach (var attr in Attributes) {
                stride = Math.Max(stride, attr.Offset + attr.Size * BytesPerComponent(attr.Type));
            }
            Stride = stride;
        }

        private static int BytesPerComponent(VertexAttribType type) => type switch {
            VertexAttribType.Float => 4,
            VertexAttribType.Int => 4,
            VertexAttribType.UnsignedInt => 4,
            VertexAttribType.Byte => 1,
            VertexAttribType.UnsignedByte => 1,
            _ => 4,
        };
    }
}
