using Avalonia;
using Acme.Render.GL;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using Silk.NET.OpenGL;
using System;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.CharGen {
    public partial class HeritageModelPreviewViewModel : ObservableObject {
        private OpenGLRenderer? _renderer;
        private IDatReaderWriter? _dats;
        private StaticObjectManager? _objectManager;
        private GL? _gl;

        [ObservableProperty] private string _status = "No model loaded";

        private uint _setupId;
        private uint _envSetupId;
        private uint _currentId;
        private bool _isSetup;
        private StaticObjectRenderData? _renderData;
        private Matrix4x4 _modelMatrix = Matrix4x4.Identity;
        private PerspectiveCamera? _camera;

        private float _rotationAngleY = MathF.PI / 4f;
        private float _rotationAngleX = MathF.PI / 6f;
        private float _zoomDistanceMultiplier = 1f;
        private float _baseCamDist = 5f;

        public bool IsInitialized => _renderer != null;
        public uint CurrentId => _currentId;

        public event Action<uint>? ApplyToSetupRequested;
        public event Action<uint>? ApplyToEnvSetupRequested;

        public ThumbnailRenderService? ThumbnailService { get; private set; }

        internal void Init(OpenGLRenderer renderer, IDatReaderWriter dats) {
            _renderer = renderer;
            _dats = dats;
            _gl = renderer.GraphicsDevice.GL;
            _objectManager = new StaticObjectManager(renderer, dats);
            _camera = new PerspectiveCamera(new Vector3(0, 0, 10), new WorldBuilderSettings());
            ThumbnailService = new ThumbnailRenderService(renderer, _objectManager);

            if (_setupId != 0 || _envSetupId != 0) {
                LoadModel(_setupId != 0 ? _setupId : _envSetupId);
            }
        }

        public void SetModelIds(uint setupId, uint envSetupId) {
            _setupId = setupId;
            _envSetupId = envSetupId;
            if (setupId != 0) LoadModel(setupId);
            else if (envSetupId != 0) LoadModel(envSetupId);
        }

        public void PreviewById(uint id) {
            LoadModel(id);
        }

        [RelayCommand]
        private void ApplyToSetup() {
            if (_currentId != 0) {
                ApplyToSetupRequested?.Invoke(_currentId);
                _setupId = _currentId;
                Status = $"Applied 0x{_currentId:X8} as Setup ID";
            }
        }

        [RelayCommand]
        private void ApplyToEnvSetup() {
            if (_currentId != 0) {
                ApplyToEnvSetupRequested?.Invoke(_currentId);
                _envSetupId = _currentId;
                Status = $"Applied 0x{_currentId:X8} as Env Setup";
            }
        }

        private void LoadModel(uint id) {
            if (_objectManager == null || _dats == null || id == 0) {
                _renderData = null;
                Status = "No model loaded";
                return;
            }

            if (_renderData != null) {
                _objectManager.ReleaseRenderData(_currentId, _isSetup);
                _renderData = null;
            }

            _currentId = id;
            _isSetup = (id & 0xFF000000) == 0x02000000;
            _renderData = _objectManager.GetRenderData(id, _isSetup);

            if (_renderData == null) {
                Status = $"0x{id:X8} — not found in DAT";
                return;
            }

            var bounds = _objectManager.GetBounds(id, _isSetup);
            if (bounds != null) {
                var (boundsMin, boundsMax) = bounds.Value;
                var center = (boundsMin + boundsMax) * 0.5f;
                var extents = boundsMax - boundsMin;
                _baseCamDist = MathF.Max(MathF.Max(extents.X, extents.Y), extents.Z) * 2.5f;
                _modelMatrix = Matrix4x4.CreateTranslation(-center);
            }
            else {
                _baseCamDist = 5f;
                _modelMatrix = Matrix4x4.Identity;
            }

            _zoomDistanceMultiplier = 1f;
            _rotationAngleY = MathF.PI / 4f;
            _rotationAngleX = MathF.PI / 6f;
            UpdateCameraPosition();

            string info = "";
            try {
                if (_isSetup && _dats.TryGet<Setup>(id, out var setup)) {
                    int parts = setup.Parts?.Count ?? 0;
                    info = $" — {parts} part{(parts != 1 ? "s" : "")}";
                }
            }
            catch { }
            Status = $"0x{id:X8}{info}";
        }

        private void UpdateCameraPosition() {
            if (_camera == null) return;
            var dist = _baseCamDist * _zoomDistanceMultiplier;
            var offset = new Vector3(
                MathF.Cos(_rotationAngleX) * MathF.Sin(_rotationAngleY) * dist,
                MathF.Cos(_rotationAngleX) * MathF.Cos(_rotationAngleY) * dist,
                MathF.Sin(_rotationAngleX) * dist
            );
            _camera.SetPosition(offset);
            _camera.LookAt(Vector3.Zero);
        }

        public void RotateAround(float deltaYaw, float deltaPitch) {
            _rotationAngleY += deltaYaw * 0.01f;
            _rotationAngleX += deltaPitch * 0.01f;
            _rotationAngleX = Math.Clamp(_rotationAngleX, -MathF.PI / 2.1f, MathF.PI / 2.1f);
            UpdateCameraPosition();
        }

        public void Zoom(float delta) {
            _zoomDistanceMultiplier = Math.Clamp(_zoomDistanceMultiplier - delta * 0.1f, 0.3f, 10f);
            UpdateCameraPosition();
        }

        public unsafe void Render(PixelSize canvasSize) {
            if (_renderData == null || _objectManager == null || _gl == null || _camera == null) return;

            var gl = _gl;
            gl.FrontFace(FrontFaceDirection.CW);
            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);

            gl.ClearColor(0.07f, 0.04f, 0.13f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            _camera.ScreenSize = new Vector2(canvasSize.Width, canvasSize.Height);
            var vp = _camera.GetViewMatrix() * _camera.GetProjectionMatrix();

            var shader = _objectManager._objectShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", vp);
            shader.SetUniform("uCameraPosition", _camera.Position);
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.5f, 0.3f, -0.3f)));
            shader.SetUniform("uAmbientIntensity", 0.5f);
            shader.SetUniform("uSpecularPower", 32f);

            if (_renderData.IsSetup) {
                foreach (var (partId, partTransform) in _renderData.SetupParts) {
                    var partData = _objectManager.GetRenderData(partId, false);
                    if (partData != null)
                        RenderSingleObject(partData, partTransform * _modelMatrix);
                }
            }
            else {
                RenderSingleObject(_renderData, _modelMatrix);
            }

            gl.BindVertexArray(0);
            gl.UseProgram(0);

            ThumbnailService?.ProcessQueue(_renderer!);
        }

        private unsafe void RenderSingleObject(StaticObjectRenderData renderData, Matrix4x4 transform) {
            if (_gl == null || _objectManager == null || renderData.Batches.Count == 0) return;

            _gl.BindVertexArray(renderData.VAO);
            for (int i = 0; i < 4; i++)
                _gl.DisableVertexAttribArray((uint)(3 + i));
            _gl.DisableVertexAttribArray(7);

            _gl.VertexAttrib4(3, transform.M11, transform.M12, transform.M13, transform.M14);
            _gl.VertexAttrib4(4, transform.M21, transform.M22, transform.M23, transform.M24);
            _gl.VertexAttrib4(5, transform.M31, transform.M32, transform.M33, transform.M34);
            _gl.VertexAttrib4(6, transform.M41, transform.M42, transform.M43, transform.M44);

            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;
                try {
                    batch.TextureArray.Bind(0);
                    _objectManager._objectShader.SetUniform("uTextureArray", 0);
                    _gl.VertexAttrib1(7, (float)batch.TextureIndex);
                    _gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                    _gl.DrawElements(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null);
                }
                catch { }
            }
            _gl.BindVertexArray(0);
        }

        public void Dispose() {
            ThumbnailService?.Dispose();
            if (_renderData != null && _objectManager != null) {
                _objectManager.ReleaseRenderData(_currentId, _isSetup);
                _renderData = null;
            }
            _objectManager?.Dispose();
        }
    }
}
