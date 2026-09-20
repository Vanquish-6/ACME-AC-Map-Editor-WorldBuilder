using Acme.Render;
using Acme.Render.GL;
using Acme.Dat;
using System;
using Silk.NET.OpenGL;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Lib;
using WorldBuilder.Rendering;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Numerics;
using System.IO;
using Acme.Render.Vertex;

namespace WorldBuilder.Editors.Landscape {
    public class SceneContext : IDisposable {
        public OpenGLRenderer Renderer { get; }
        public TerrainGPUResourceManager GPUManager { get; }
        public StaticObjectManager ObjectManager { get; }
        public EnvCellManager EnvCellManager { get; }
        public IShader TerrainShader { get; }
        public IShader SphereShader { get; }
        public IShader GizmoShader { get; }
        public IShader PreviewShader { get; }
        public IShader SkyShader { get; }
        public TransformGizmo Gizmo { get; }

        public uint SkyVAO { get; private set; }
        public uint SkyVBO { get; private set; }

        public uint PreviewVAO { get; set; }
        public uint PreviewVBO { get; set; }
        public uint PreviewEBO { get; set; }

        public uint SphereVAO { get; set; }
        public uint SphereVBO { get; set; }
        public uint SphereIBO { get; set; }
        public uint SphereInstanceVBO { get; set; }
        public int SphereIndexCount { get; set; }

        // Per-context instance buffer for static objects
        public uint InstanceVBO { get; set; }
        public int InstanceBufferCapacity { get; set; }
        public float[] InstanceUploadBuffer { get; set; } = Array.Empty<float>();

        // Track which VAOs have had instance attribs configured (avoids 12 redundant GL calls per model per frame)
        public HashSet<uint> ConfiguredInstanceVAOs { get; } = new();

        // Queues for background loading specific to this context
        public ConcurrentQueue<PreparedChunkData> ChunkUploadQueue { get; } = new();
        public ConcurrentQueue<PreparedModelData> ModelUploadQueue { get; } = new();
        public Queue<(uint Id, bool IsSetup)> ModelWarmupQueue { get; } = new();
        public HashSet<uint> ModelsPreparing { get; } = new();
        public HashSet<ulong> ChunksInFlight { get; } = new();

        public SceneContext(OpenGLRenderer renderer, IDatReaderWriter dats, TextureDiskCache textureCache) {
            Renderer = renderer;

            GPUManager = new TerrainGPUResourceManager(renderer);
            ObjectManager = new StaticObjectManager(renderer, dats, textureCache);
            EnvCellManager = new EnvCellManager(renderer, dats, ObjectManager._objectShader, textureCache);

            // Initialize shaders
            var assembly = typeof(OpenGLRenderer).Assembly;
            TerrainShader = renderer.GraphicsDevice.CreateShader("Landscape",
                GameScene.GetEmbeddedResource("Acme.Render.GL.Shaders.Landscape.vert", assembly),
                GameScene.GetEmbeddedResource("Acme.Render.GL.Shaders.Landscape.frag", assembly));

            SphereShader = renderer.GraphicsDevice.CreateShader("Sphere",
                GameScene.GetEmbeddedResource("WorldBuilder.Shaders.Sphere.vert", typeof(GameScene).Assembly),
                GameScene.GetEmbeddedResource("WorldBuilder.Shaders.Sphere.frag", typeof(GameScene).Assembly));

            GizmoShader = renderer.GraphicsDevice.CreateShader("Gizmo",
                GameScene.GetEmbeddedResource("WorldBuilder.Shaders.Gizmo.vert", typeof(GameScene).Assembly),
                GameScene.GetEmbeddedResource("WorldBuilder.Shaders.Gizmo.frag", typeof(GameScene).Assembly));

            Gizmo = new TransformGizmo();
            Gizmo.Initialize(renderer.GraphicsDevice.GL, GizmoShader);

            var openGlAssembly = typeof(OpenGLRenderer).Assembly;
            PreviewShader = renderer.GraphicsDevice.CreateShader("Preview",
                GameScene.GetEmbeddedResource("Acme.Render.GL.Shaders.Preview.vert", openGlAssembly),
                GameScene.GetEmbeddedResource("Acme.Render.GL.Shaders.Preview.frag", openGlAssembly));

            SkyShader = renderer.GraphicsDevice.CreateShader("Sky",
                GameScene.GetEmbeddedResource("Acme.Render.GL.Shaders.Sky.vert", openGlAssembly),
                GameScene.GetEmbeddedResource("Acme.Render.GL.Shaders.Sky.frag", openGlAssembly));

            InitializeSphereGeometry();
            InitializeSkyGeometry();
        }

        private unsafe void InitializeSphereGeometry() {
            var gl = Renderer.GraphicsDevice.GL;
            var vertices = CreateSphere(8, 6);
            var indices = CreateSphereIndices(8, 6);
            SphereIndexCount = indices.Length;

            gl.GenVertexArrays(1, out uint vao);
            SphereVAO = vao;
            gl.BindVertexArray(SphereVAO);

            gl.GenBuffers(1, out uint vbo);
            SphereVBO = vbo;
            gl.BindBuffer(GLEnum.ArrayBuffer, SphereVBO);
            fixed (VertexPositionNormal* ptr = vertices) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(vertices.Length * VertexPositionNormal.Size), ptr,
                    GLEnum.StaticDraw);
            }

            int stride = VertexPositionNormal.Size;
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, (uint)stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, (uint)stride, (void*)(3 * sizeof(float)));

            gl.GenBuffers(1, out uint instVbo);
            SphereInstanceVBO = instVbo;
            gl.BindBuffer(GLEnum.ArrayBuffer, SphereInstanceVBO);
            gl.BufferData(GLEnum.ArrayBuffer, 0, null, GLEnum.DynamicDraw);
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 4, GLEnum.Float, false, (uint)sizeof(Vector4), null);
            gl.VertexAttribDivisor(2, 1);

            gl.GenBuffers(1, out uint ibo);
            SphereIBO = ibo;
            gl.BindBuffer(GLEnum.ElementArrayBuffer, SphereIBO);
            fixed (uint* iptr = indices) {
                gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), iptr,
                    GLEnum.StaticDraw);
            }

            gl.BindVertexArray(0);
        }

        private unsafe void InitializeSkyGeometry() {
            var gl = Renderer.GraphicsDevice.GL;
            // Full-screen quad in NDC: two triangles covering [-1,1] x [-1,1].
            // No depth write — sky sits behind everything at z = 0.9999 in the shader.
            float[] verts = [
                -1f, -1f,
                 1f, -1f,
                 1f,  1f,
                -1f,  1f,
            ];
            uint[] indices = [0u, 1u, 2u, 0u, 2u, 3u];

            gl.GenVertexArrays(1, out uint vao);
            SkyVAO = vao;
            gl.BindVertexArray(SkyVAO);

            gl.GenBuffers(1, out uint vbo);
            SkyVBO = vbo;
            gl.BindBuffer(GLEnum.ArrayBuffer, SkyVBO);
            fixed (float* ptr = verts) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), ptr, GLEnum.StaticDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 2 * sizeof(float), (void*)0);

            gl.GenBuffers(1, out uint ebo);
            gl.BindBuffer(GLEnum.ElementArrayBuffer, ebo);
            fixed (uint* ptr = indices) {
                gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), ptr, GLEnum.StaticDraw);
            }

            gl.BindVertexArray(0);
        }

        private static VertexPositionNormal[] CreateSphere(int longitudeSegments, int latitudeSegments) {
            var vertices = new List<VertexPositionNormal>();
            for (int lat = 0; lat <= latitudeSegments; lat++) {
                float theta = lat * MathF.PI / latitudeSegments;
                float sinTheta = MathF.Sin(theta);
                float cosTheta = MathF.Cos(theta);
                for (int lon = 0; lon <= longitudeSegments; lon++) {
                    float phi = lon * 2 * MathF.PI / longitudeSegments;
                    float sinPhi = MathF.Sin(phi);
                    float cosPhi = MathF.Cos(phi);
                    float x = cosPhi * sinTheta;
                    float y = cosTheta;
                    float z = sinPhi * sinTheta;
                    Vector3 position = new Vector3(x, y, z);
                    Vector3 normal = Vector3.Normalize(position);
                    vertices.Add(new VertexPositionNormal(position, normal));
                }
            }

            return vertices.ToArray();
        }

        private static uint[] CreateSphereIndices(int longitudeSegments, int latitudeSegments) {
            var indices = new List<uint>();
            for (int lat = 0; lat < latitudeSegments; lat++) {
                for (int lon = 0; lon < longitudeSegments; lon++) {
                    uint current = (uint)(lat * (longitudeSegments + 1) + lon);
                    uint next = current + (uint)(longitudeSegments + 1);
                    indices.Add(current);
                    indices.Add(next);
                    indices.Add(current + 1);
                    indices.Add(current + 1);
                    indices.Add(next);
                    indices.Add(next + 1);
                }
            }

            return indices.ToArray();
        }

        public unsafe void RenderStampPreview(PreviewMeshData? data, bool dirty, float ambient, ITextureArray terrainAtlas, ICamera camera, Matrix4x4 viewProjection) {
            if (data == null) return;
            var gl = Renderer.GraphicsDevice.GL;
            if (PreviewVAO == 0) {
                gl.GenVertexArrays(1, out uint vao);
                PreviewVAO = vao;
                gl.GenBuffers(1, out uint vbo);
                PreviewVBO = vbo;
                gl.GenBuffers(1, out uint ebo);
                PreviewEBO = ebo;
            }
            if (dirty) {
                gl.BindVertexArray(PreviewVAO);
                gl.BindBuffer(GLEnum.ArrayBuffer, PreviewVBO);
                unsafe {
                    fixed (PreviewVertex* ptr = data.Vertices) {
                        gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Vertices.Length * sizeof(PreviewVertex)), ptr, GLEnum.DynamicDraw);
                    }
                }
                gl.BindBuffer(GLEnum.ElementArrayBuffer, PreviewEBO);
                unsafe {
                    fixed (uint* ptr = data.Indices) {
                        gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(data.Indices.Length * sizeof(uint)), ptr, GLEnum.DynamicDraw);
                    }
                }
                gl.VertexAttribPointer(0, 3, GLEnum.Float, false, (uint)sizeof(PreviewVertex), (void*)0);
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(1, 2, GLEnum.Float, false, (uint)sizeof(PreviewVertex), (void*)sizeof(Vector3));
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(2, 1, GLEnum.Float, false, (uint)sizeof(PreviewVertex), (void*)(sizeof(Vector3) + sizeof(Vector2)));
                gl.EnableVertexAttribArray(2);
                gl.BindVertexArray(0);
            }
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);
            PreviewShader.Bind();
            PreviewShader.SetUniform("xAmbient", ambient);
            PreviewShader.SetUniform("xWorld", Matrix4x4.Identity);
            PreviewShader.SetUniform("xView", camera.GetViewMatrix());
            PreviewShader.SetUniform("xProjection", camera.GetProjectionMatrix());
            PreviewShader.SetUniform("uAlpha", 0.8f);
            terrainAtlas.Bind(0);
            PreviewShader.SetUniform("xOverlays", 0);
            gl.BindVertexArray(PreviewVAO);
            gl.DrawElements(GLEnum.Triangles, (uint)data.Indices.Length, GLEnum.UnsignedInt, null);
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Enable(EnableCap.CullFace);
            gl.Disable(EnableCap.Blend);
        }

        public void Dispose() {
            var gl = Renderer.GraphicsDevice.GL;
            gl.DeleteBuffer(SphereVBO);
            gl.DeleteBuffer(SphereIBO);
            gl.DeleteBuffer(SphereInstanceVBO);
            gl.DeleteVertexArray(SphereVAO);
            if (SkyVBO != 0) gl.DeleteBuffer(SkyVBO);
            if (SkyVAO != 0) gl.DeleteVertexArray(SkyVAO);
            if (InstanceVBO != 0) gl.DeleteBuffer(InstanceVBO);
            if (PreviewVBO != 0) gl.DeleteBuffer(PreviewVBO);
            if (PreviewEBO != 0) gl.DeleteBuffer(PreviewEBO);
            if (PreviewVAO != 0) gl.DeleteVertexArray(PreviewVAO);
            Gizmo.Dispose(gl);

            EnvCellManager.Dispose();
            ObjectManager.Dispose();
            GPUManager.Dispose();
        }
    }
}
