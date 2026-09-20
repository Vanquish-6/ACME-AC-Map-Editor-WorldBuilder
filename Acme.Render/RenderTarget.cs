using Acme.Render.Enums;
using Acme.Render.Vertex;

namespace Acme.Render {
    public class RenderTarget : IDisposable {
        public IFramebuffer Framebuffer { get; }
        public ITexture Texture { get; }
        public Rectangle Viewport { get; }

        public RenderTarget(IGraphicsDevice device, int width, int height) {
            ArgumentNullException.ThrowIfNull(device);
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");

            Texture = device.CreateTexture(TextureFormat.RGBA8, width, height);
            Framebuffer = device.CreateFramebuffer(Texture, width, height, hasDepthStencil: true);
            Viewport = new Rectangle(0, 0, width, height);
        }

        public void Clear(ColorVec color, ClearFlags flags = ClearFlags.Color | ClearFlags.Depth, float depth = 1f, int stencil = 0) {
            // Caller is expected to bind this target first.
        }

        public void Dispose() {
            Framebuffer.Dispose();
            Texture.Dispose();
        }
    }
}
