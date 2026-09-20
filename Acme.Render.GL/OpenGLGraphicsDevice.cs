using Acme.Render;
using Acme.Render.Enums;
using Acme.Render.Vertex;
using Microsoft.Extensions.Logging;
using Silk.NET.OpenGL;
using PolygonMode = Silk.NET.OpenGL.PolygonMode;
using PrimitiveType = Silk.NET.OpenGL.PrimitiveType;

namespace Acme.Render.GL {
    using GL = Silk.NET.OpenGL.GL;
    public unsafe class OpenGLGraphicsDevice : IGraphicsDevice {
        private readonly ILogger _log;
        private readonly Dictionary<RenderState, bool> _renderStates = new();
        private readonly Dictionary<IntPtr, ITexture> _textures = new();
        private readonly Dictionary<IntPtr, ITextureArray> _textureArrays = new();
        private Rectangle _viewport;
        private Rectangle _scissor;
        private BlendFactor _srcBlend = BlendFactor.SrcAlpha;
        private BlendFactor _dstBlend = BlendFactor.OneMinusSrcAlpha;
        private CullMode _cullMode = CullMode.None;
        private Acme.Render.Enums.PolygonMode _polygonMode = Acme.Render.Enums.PolygonMode.Fill;
        private IShader? _shader;

        public GL GL { get; }
        public IntPtr NativeDevice { get; }

        public event EventHandler? OnGraphicsPreReset;
        public event EventHandler? OnGraphicsPostReset;

        public Rectangle Viewport {
            get => _viewport;
            set {
                _viewport = value;
                SetViewportInternal(value);
            }
        }

        public Rectangle Scissor {
            get => _scissor;
            set {
                _scissor = value;
                SetScissorRectInternal(value);
            }
        }

        public BlendFactor SourceBlendFactor {
            get => _srcBlend;
            set {
                _srcBlend = value;
                SetBlendFactorInternal(_srcBlend, _dstBlend);
            }
        }

        public BlendFactor DestBlendFactor {
            get => _dstBlend;
            set {
                _dstBlend = value;
                SetBlendFactorInternal(_srcBlend, _dstBlend);
            }
        }

        public CullMode CullMode {
            get => _cullMode;
            set {
                _cullMode = value;
                SetCullModeInternal(value);
            }
        }

        public Acme.Render.Enums.PolygonMode PolygonMode {
            get => _polygonMode;
            set {
                _polygonMode = value;
                SetPolygonModeInternal(value);
            }
        }

        public IShader? Shader {
            get => _shader;
            set {
                _shader?.Unbind();
                _shader = value;
                _shader?.Bind();
            }
        }

        public OpenGLGraphicsDevice(GL gl, ILogger log) {
            _log = log;
            GL = gl;
            if (GLHelpers.Device == null) {
                GLHelpers.Init(this, log);
            }
        }

        public void Initialize() { }

        public void Clear(ColorVec color, ClearFlags flags, float depth, int stencil) {
            GL.ClearColor(color.R, color.G, color.B, color.A);
            GLHelpers.CheckErrors();
            GL.Clear((uint)Convert(flags));
            GLHelpers.CheckErrors();
        }

        public IIndexBuffer CreateIndexBuffer(int size, BufferUsage usage = BufferUsage.Static) {
            return new ManagedGLIndexBuffer(this, usage, size);
        }

        public IVertexBuffer CreateVertexBuffer(int size, BufferUsage usage = BufferUsage.Static) {
            return new ManagedGLVertexBuffer(this, usage, size);
        }

        public IVertexArray CreateArrayBuffer(IVertexBuffer vertexBuffer, VertexFormat format) {
            return new ManagedGLVertexArray(this, vertexBuffer, format);
        }

        public void DrawElements(Acme.Render.Enums.PrimitiveType type, int numElements, int indiceOffset = 0) {
            GL.DrawElements(Convert(type), (uint)numElements, GLEnum.UnsignedInt, (void*)(indiceOffset * sizeof(uint)));
            GLHelpers.CheckErrors();
        }

        public IShader CreateShader(string name, string vertexCode, string fragmentCode) {
            return new GLSLShader(this, name, vertexCode, fragmentCode, _log);
        }

        public IShader CreateShader(string name, string shaderDirectory) {
            return new GLSLShader(this, name, shaderDirectory, _log);
        }

        public ITexture CreateTexture(TextureFormat format, int width, int height, byte[]? data = null) {
            var texture = CreateTextureInternal(format, width, height, data);
            _textures[texture.NativePtr] = texture;
            return texture;
        }

        public ITexture? CreateTexture(TextureFormat format, string filename) {
            var texture = CreateTextureInternal(format, filename);
            if (texture != null) _textures[texture.NativePtr] = texture;
            return texture;
        }

        public ITextureArray CreateTextureArray(TextureFormat format, int width, int height, int size) {
            var array = new ManagedGLTextureArray(this, format, width, height, size);
            _textureArrays[array.NativePtr] = array;
            return array;
        }

        private ITexture CreateTextureInternal(TextureFormat format, int width, int height, byte[]? data = null) {
            if (format != TextureFormat.RGBA8) {
                throw new NotImplementedException($"Texture format {format} is not supported.");
            }
            return new ManagedGLTexture(this, data, width, height);
        }

        private ITexture? CreateTextureInternal(TextureFormat format, string filename) {
            if (format != TextureFormat.RGBA8) {
                throw new NotImplementedException($"Texture format {format} is not supported.");
            }
            return new ManagedGLTexture(this, filename);
        }

        public ITexture? GetTexture(IntPtr nativePtr) {
            return _textures.TryGetValue(nativePtr, out var t) ? t : null;
        }

        public void ReleaseTexture(ITexture texture) {
            _textures.Remove(texture.NativePtr);
            texture.Dispose();
        }

        public ITextureArray? GetTextureArray(IntPtr nativePtr) {
            return _textureArrays.TryGetValue(nativePtr, out var t) ? t : null;
        }

        public void ReleaseTextureArray(ITextureArray textureArray) {
            _textureArrays.Remove(textureArray.NativePtr);
            textureArray.Dispose();
        }

        public void BindTexture(ITexture texture, int slot = 0) {
            texture.Bind(slot);
        }

        public void BeginFrame() {
            GL.Viewport(_viewport.X, _viewport.Y, (uint)_viewport.Width, (uint)_viewport.Height);
            GLHelpers.CheckErrors();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GLHelpers.CheckErrors();
        }

        public void EndFrame() { }

        public bool GetRenderState(RenderState state) {
            return _renderStates.TryGetValue(state, out var enabled) && enabled;
        }

        public void SetRenderState(RenderState state, bool enabled) {
            _renderStates[state] = enabled;
            SetRenderStateInternal(state, enabled);
        }

        protected void SetRenderStateInternal(RenderState state, bool enabled) {
            switch (state) {
                case RenderState.AlphaBlend:
                    if (enabled) GL.Enable(EnableCap.Blend);
                    else GL.Disable(EnableCap.Blend);
                    GLHelpers.CheckErrors();
                    break;
                case RenderState.DepthTest:
                    if (enabled) GL.Enable(EnableCap.DepthTest);
                    else GL.Disable(EnableCap.DepthTest);
                    GLHelpers.CheckErrors();
                    break;
                case RenderState.ScissorTest:
                    if (enabled) GL.Enable(EnableCap.ScissorTest);
                    else GL.Disable(EnableCap.ScissorTest);
                    GLHelpers.CheckErrors();
                    break;
                case RenderState.DepthWrite:
                    GL.DepthMask(enabled);
                    GLHelpers.CheckErrors();
                    break;
                case RenderState.Fog:
                case RenderState.Lighting:
                    break;
            }
        }

        protected void SetBlendFactorInternal(BlendFactor srcBlendFactor, BlendFactor dstBlendFactor) {
            GL.BlendFunc(Convert(srcBlendFactor), Convert(dstBlendFactor));
            GLHelpers.CheckErrors();
        }

        protected void SetScissorRectInternal(Rectangle scissor) {
            var gtop = (int)_viewport.Height - scissor.Y - scissor.Height;
            GL.Scissor(scissor.X, gtop, (uint)scissor.Width, (uint)scissor.Height);
            GLHelpers.CheckErrors();
        }

        protected void SetViewportInternal(Rectangle viewport) {
            GL.Viewport(viewport.X, viewport.Y, (uint)viewport.Width, (uint)viewport.Height);
            GLHelpers.CheckErrors();
        }

        protected void SetPolygonModeInternal(Acme.Render.Enums.PolygonMode polygonMode) {
            GL.PolygonMode(GLEnum.FrontAndBack, Convert(polygonMode));
            GLHelpers.CheckErrors();
        }

        protected void SetCullModeInternal(CullMode cullMode) {
            switch (cullMode) {
                case CullMode.None:
                    GL.Disable(EnableCap.CullFace);
                    break;
                case CullMode.Front:
                    GL.Enable(EnableCap.CullFace);
                    GL.CullFace(GLEnum.Front);
                    break;
                case CullMode.Back:
                    GL.Enable(EnableCap.CullFace);
                    GL.CullFace(GLEnum.Back);
                    break;
            }
        }

        private GLEnum Convert(Acme.Render.Enums.PolygonMode mode) => mode switch {
            Acme.Render.Enums.PolygonMode.Fill => GLEnum.Fill,
            Acme.Render.Enums.PolygonMode.Line => GLEnum.Line,
            Acme.Render.Enums.PolygonMode.Point => GLEnum.Point,
            _ => GLEnum.Fill,
        };

        private GLEnum Convert(ClearFlags flags) {
            GLEnum mask = 0;
            if ((flags & ClearFlags.Color) == ClearFlags.Color) mask |= GLEnum.ColorBufferBit;
            if ((flags & ClearFlags.Depth) == ClearFlags.Depth) mask |= GLEnum.DepthBufferBit;
            if ((flags & ClearFlags.Stencil) == ClearFlags.Stencil) mask |= GLEnum.StencilBufferBit;
            return mask;
        }

        private GLEnum Convert(BlendFactor factor) => factor switch {
            BlendFactor.One => GLEnum.One,
            BlendFactor.SrcAlpha => GLEnum.SrcAlpha,
            BlendFactor.OneMinusSrcAlpha => GLEnum.OneMinusSrcAlpha,
            BlendFactor.DstAlpha => GLEnum.DstAlpha,
            BlendFactor.OneMinusDstAlpha => GLEnum.OneMinusDstAlpha,
            _ => GLEnum.One,
        };

        private PrimitiveType Convert(Acme.Render.Enums.PrimitiveType type) => type switch {
            Acme.Render.Enums.PrimitiveType.PointList => PrimitiveType.Points,
            Acme.Render.Enums.PrimitiveType.LineList => PrimitiveType.Lines,
            Acme.Render.Enums.PrimitiveType.LineStrip => PrimitiveType.LineStrip,
            Acme.Render.Enums.PrimitiveType.TriangleList => PrimitiveType.Triangles,
            Acme.Render.Enums.PrimitiveType.TriangleStrip => PrimitiveType.TriangleStrip,
            _ => throw new NotImplementedException($"Primitive type {type} is not supported."),
        };

        public IFramebuffer CreateFramebuffer(ITexture texture, int width, int height, bool hasDepthStencil = true) {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (width <= 0 || height <= 0) throw new ArgumentException("Width and height must be positive.");
            return new ManagedGLFramebuffer(GL, texture, width, height, hasDepthStencil);
        }

        public void BindFramebuffer(IFramebuffer? framebuffer) {
            uint fboId = framebuffer != null ? (uint)framebuffer.NativeHandle.ToInt32() : 0;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fboId);
        }

        public IUniformBuffer CreateUniformBuffer(BufferUsage usage, int size) {
            throw new NotImplementedException();
        }

        public void Dispose() { }
    }
}
