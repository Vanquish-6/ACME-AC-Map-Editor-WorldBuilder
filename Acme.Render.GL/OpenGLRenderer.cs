using Acme.Render;
using Microsoft.Extensions.Logging;
using Silk.NET.OpenGL;
using System.Numerics;

namespace Acme.Render.GL {
    using GL = Silk.NET.OpenGL.GL;
    public class OpenGLRenderer : IRenderer {
        private readonly ILogger _log;

        public OpenGLGraphicsDevice GraphicsDevice { get; }
        IGraphicsDevice IRenderer.GraphicsDevice => GraphicsDevice;

        public OpenGLRenderer(GL gl, ILogger log, int width, int height) {
            _log = log;
            GraphicsDevice = new OpenGLGraphicsDevice(gl, log) {
                Viewport = new Rectangle(0, 0, width, height)
            };
        }

        public RenderTarget CreateRenderTarget(int width, int height) {
            return new RenderTarget(GraphicsDevice, width, height);
        }

        public void BindRenderTarget(RenderTarget? target) {
            GraphicsDevice.BindFramebuffer(target?.Framebuffer);
            if (target != null) {
                GraphicsDevice.Viewport = target.Viewport;
            }
        }

        public void Resize(int width, int height) {
            GraphicsDevice.Viewport = new Rectangle(0, 0, width, height);
        }

        public void Dispose() {
            GraphicsDevice.Dispose();
        }
    }
}
