namespace Acme.Render.Enums {
    public enum TextureFormat {
        RGBA8 = 0,
        RGB8 = 1,
        A8 = 2,
        Rgba32f = 3,
        DXT1 = 4,
        DXT3 = 5,
        DXT5 = 6,
    }

    public enum BufferUsage {
        Static = 0,
        Dynamic = 1,
        Stream = 2,
    }

    public enum PrimitiveType {
        PointList = 0,
        LineList = 1,
        LineStrip = 2,
        TriangleList = 3,
        TriangleStrip = 4,
    }

    public enum VertexAttribType {
        Float = 0,
        Int = 1,
        UnsignedInt = 2,
        UnsignedByte = 3,
        Byte = 4,
    }

    public enum VertexAttributeName {
        Position = 0,
        Color = 1,
        TexCoords = 2,
        Normal = 3,
        TexCoord0 = 4,
        TexCoord1 = 5,
        TexCoord2 = 6,
        TexCoord3 = 7,
        TexCoord4 = 8,
        TexCoord5 = 9,
        TexCoord6 = 10,
    }

    public enum RenderState {
        AlphaBlend = 0,
        DepthTest = 1,
        DepthWrite = 2,
        ScissorTest = 3,
        Lighting = 4,
        Fog = 5,
    }

    public enum BlendFactor {
        One = 0,
        SrcAlpha = 1,
        OneMinusSrcAlpha = 2,
        DstAlpha = 3,
        OneMinusDstAlpha = 4,
    }

    [Flags]
    public enum ClearFlags {
        Color = 1,
        Depth = 2,
        Stencil = 4,
    }

    public enum PolygonMode {
        Fill = 0,
        Line = 1,
        Point = 2,
    }

    public enum CullMode {
        None = 0,
        Front = 1,
        Back = 2,
    }
}
