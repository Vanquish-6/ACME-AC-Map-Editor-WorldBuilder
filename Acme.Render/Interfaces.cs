using System.Numerics;
using Acme.Render.Enums;
using Acme.Render.Vertex;

namespace Acme.Render {
    public interface ITexture : IDisposable {
        IntPtr NativePtr { get; }
        int Width { get; }
        int Height { get; }
        TextureFormat Format { get; }
        void Bind(int slot = 0);
        void Unbind();
        void SetData(Rectangle rectangle, byte[] data);
    }

    public interface ITextureArray : IDisposable {
        int Slot { get; }
        int Width { get; }
        int Height { get; }
        int Size { get; }
        TextureFormat Format { get; }
        IntPtr NativePtr { get; }
        void Bind(int slot = 0);
        int AddLayer(byte[] data);
        int AddLayer(Span<byte> data);
        void UpdateLayer(int layer, byte[] data);
        void RemoveLayer(int layer);
        void Unbind();
    }

    public interface IShader : IDisposable {
        string Name { get; }
        uint ProgramId { get; }
        void Load(string vertSource, string fragSource);
        void SetUniform(string name, Matrix4x4 value);
        void SetUniform(string name, int value);
        void SetUniform(string name, Vector2 value);
        void SetUniform(string name, Vector3 value);
        void SetUniform(string name, Vector4 value);
        void SetUniform(string name, float value);
        void SetUniform(string name, float[] values);
        void SetUniform(string name, Vector3[] values);
        void Bind();
        void Unbind();
    }

    public interface IBuffer : IDisposable {
        int Size { get; }
        BufferUsage Usage { get; }
        void Bind();
        void Unbind();
    }

    public interface IVertexBuffer : IBuffer {
        void SetData<T>(T[] data) where T : IVertex;
        void SetData<T>(Span<T> data) where T : IVertex;
        void SetSubData<T>(T[] data, int destinationOffsetBytes, int sourceOffsetElements = 0, int lengthElements = 0) where T : IVertex;
        void SetSubData<T>(Span<T> data, int destinationOffsetBytes, int sourceOffsetElements = 0, int lengthElements = 0) where T : IVertex;
    }

    public interface IIndexBuffer : IBuffer {
        void SetData(uint[] data);
        void SetData(Span<uint> data);
        void SetSubData(Span<uint> data, int destinationOffsetBytes, int sourceOffsetElements = 0, int lengthElements = 0);
        void SetSubData(uint[] data, int destinationOffsetBytes, int sourceOffsetElements = 0, int lengthElements = 0);
    }

    public interface IVertexArray : IDisposable {
        void SetVertexBuffer(IVertexBuffer buffer, VertexFormat format);
        void Bind();
        void Unbind();
    }

    public interface IFramebuffer : IDisposable {
        ITexture Texture { get; }
        IntPtr NativeHandle { get; }
    }

    public interface IUniformBuffer : IDisposable {
        void Bind(uint bindingPoint);
        void Unbind();
        void SetData<T>(T[] data) where T : unmanaged;
        void SetData<T>(Span<T> data) where T : unmanaged;
        void SetSubData<T>(Span<T> data, int destinationOffsetBytes, int sourceOffsetElements = 0, int lengthElements = 0) where T : unmanaged;
    }

    public interface IGraphicsDevice : IDisposable {
        Rectangle Viewport { get; set; }
        Rectangle Scissor { get; set; }
        BlendFactor SourceBlendFactor { get; set; }
        BlendFactor DestBlendFactor { get; set; }
        CullMode CullMode { get; set; }
        PolygonMode PolygonMode { get; set; }
        IShader? Shader { get; set; }
        IntPtr NativeDevice { get; }

        event EventHandler? OnGraphicsPreReset;
        event EventHandler? OnGraphicsPostReset;

        void Initialize();
        void Clear(ColorVec color, ClearFlags flags, float depth = 1f, int stencil = 0);
        IVertexBuffer CreateVertexBuffer(int size, BufferUsage usage = BufferUsage.Static);
        IIndexBuffer CreateIndexBuffer(int size, BufferUsage usage = BufferUsage.Static);
        IVertexArray CreateArrayBuffer(IVertexBuffer vertexBuffer, VertexFormat format);
        IShader CreateShader(string name, string vertexCode, string fragmentCode);
        IShader CreateShader(string name, string shaderDirectory);
        void BindTexture(ITexture texture, int slot = 0);
        void DrawElements(PrimitiveType type, int numElements, int indiceOffset = 0);
        void SetRenderState(RenderState state, bool enabled);
        bool GetRenderState(RenderState state);
        void BeginFrame();
        void EndFrame();
        IFramebuffer CreateFramebuffer(ITexture texture, int width, int height, bool hasDepthStencil = true);
        void BindFramebuffer(IFramebuffer? framebuffer);
        ITextureArray CreateTextureArray(TextureFormat format, int width, int height, int size);
        ITextureArray? GetTextureArray(IntPtr nativePtr);
        void ReleaseTextureArray(ITextureArray textureArray);
        ITexture CreateTexture(TextureFormat format, int width, int height, byte[]? data = null);
        ITexture? CreateTexture(TextureFormat format, string filename);
        ITexture? GetTexture(IntPtr nativePtr);
        void ReleaseTexture(ITexture texture);
        IUniformBuffer CreateUniformBuffer(BufferUsage usage, int size);
    }

    public interface IRenderer : IDisposable {
        IGraphicsDevice GraphicsDevice { get; }
        RenderTarget CreateRenderTarget(int width, int height);
        void BindRenderTarget(RenderTarget? target);
        void Resize(int width, int height);
    }
}
