using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Acme.Render.GL;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.ObjectDebug.ViewModels {
    public partial class ObjectDebugViewModel : ViewModelBase {
        private OpenGLRenderer _renderer;
        private IDatReaderWriter _dats;
        private StaticObjectManager _objectManager;
        private PortalDatDocument? _portalDoc;
        private GL _gl;

        [ObservableProperty] private string _objectIdText = "";
        [ObservableProperty] private string _status = "";
        [ObservableProperty] private uint? _selectedSetupId;
        [ObservableProperty] private uint? _selectedGfxObjId;
        [ObservableProperty] private bool _isReadOnlyProject;
        [ObservableProperty] private bool _canImport = true;

        partial void OnSelectedSetupIdChanged(uint? value) {
            if (_suppressSelectionLoad) return;
            if (value.HasValue) {
                SelectedGfxObjId = null;
                LoadFromId(value.Value);
            }
        }

        partial void OnSelectedGfxObjIdChanged(uint? value) {
            if (_suppressSelectionLoad) return;
            if (value.HasValue) {
                SelectedSetupId = null;
                LoadFromId(value.Value);
            }
        }

        /// <summary>Skips selection-driven LoadFromId when applying programmatic imports on the GL thread.</summary>
        private bool _suppressSelectionLoad;

        private Action? _requestInvalidateViewport;

        /// <summary>OBJ import defers GPU uploads until the next frame when the OpenGL context is current.</summary>
        private (uint GfxId, GfxObj Gfx, uint SetupId, Setup Setup)? _pendingObjImport;

        /// <summary>Applied on the GL render thread (see <see cref="ProcessPendingPreview"/>).</summary>
        private uint? _pendingPreviewSetupId;
        private bool _pendingClearPreview;

        /// <summary>Deferred load from UI-thread actions (Load button, list selection) — processed on the GL render thread.</summary>
        private (uint Id, bool IsSetup)? _pendingDirectLoad;

        private uint _currentId;
        private bool _isSetup;
        private StaticObjectRenderData? _renderData;
        private Matrix4x4 _modelMatrix = Matrix4x4.Identity;
        private PerspectiveCamera _camera;
        private IEnumerable<uint> _setupIds;
        private IEnumerable<uint> _gfxObjIds;

        [ObservableProperty] private IEnumerable<uint> _filteredSetupIds;
        [ObservableProperty] private IEnumerable<uint> _filteredGfxObjIds;
        [ObservableProperty] private string _searchText = "";

        /// <summary>Portal Surface DID for imported mesh polygons (reuse a retail material).</summary>
        [ObservableProperty] private string _importSurfaceDidText = "0x08000001";

        private float _rotationAngleY = 0f;
        private float _rotationAngleX = 0f;
        private float _zoomDistanceMultiplier = 1f;
        /// <summary>
        /// Base camera orbit distance set when an object is loaded.
        /// Used to build right-handed view/projection matrices directly in <see cref="Render"/>,
        /// bypassing the terrain <see cref="PerspectiveCamera"/> which uses a left-handed system
        /// with -Z up that renders AC objects upside-down in the object preview.
        /// </summary>
        private float _baseCamDist = 5f;
        private Vector3 _cameraTarget = Vector3.Zero;

        public ObjectDebugViewModel() {
        }

        internal void Init(OpenGLRenderer renderer, IDatReaderWriter dats, StaticObjectManager staticObjectManager,
            Action requestInvalidateViewport) {
            _renderer = renderer;
            _dats = dats;
            _objectManager = staticObjectManager;
            _requestInvalidateViewport = requestInvalidateViewport;
            _gl = renderer.GraphicsDevice.GL;

            _camera = new PerspectiveCamera(new Vector3(0, 0, 10), new WorldBuilderSettings());
            IsReadOnlyProject = !_dats.CanWrite;
            CanImport = _dats.CanWrite;

            var project = ProjectManager.Instance.CurrentProject;
            if (project != null && _dats.CanWrite) {
                _portalDoc = project.DocumentManager.GetOrCreateDocumentAsync<PortalDatDocument>(PortalDatDocument.DocumentId).Result;
            }

            var portalSetup = _portalDoc != null
                ? _portalDoc.GetEntryIds().Where(id => (id & 0xFF000000) == 0x02000000)
                : Enumerable.Empty<uint>();
            var portalGfx = _portalDoc != null
                ? _portalDoc.GetEntryIds().Where(id => (id & 0xFF000000) == 0x01000000)
                : Enumerable.Empty<uint>();

            _setupIds = _dats.GetAllIdsOfType<Setup>().Concat(portalSetup).Distinct().OrderBy(id => id);
            _gfxObjIds = _dats.GetAllIdsOfType<GfxObj>().Concat(portalGfx).Distinct().OrderBy(id => id);

            FilteredSetupIds = _setupIds;
            FilteredGfxObjIds = _gfxObjIds;

            _objectManager.SetPortalDatDocument(_portalDoc);
        }

        partial void OnSearchTextChanged(string value) {
            var query = value.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(query)) {
                FilteredSetupIds = _setupIds;
                FilteredGfxObjIds = _gfxObjIds;
            }
            else {
                uint parsed;
                var isHex = query.StartsWith("0x");
                var parseStr = isHex ? query.Substring(2) : query;
                var isNumeric = uint.TryParse(parseStr, isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out parsed);

                FilteredSetupIds = _setupIds.Where(id => {
                    if (isNumeric) return id == parsed;
                    var hex = $"0x{id:X8}";
                    return id.ToString().Contains(query) || hex.Contains(query) || hex.ToLower().Contains(query);
                });

                FilteredGfxObjIds = _gfxObjIds.Where(id => {
                    if (isNumeric) return id == parsed;
                    var hex = $"0x{id:X8}";
                    return id.ToString().Contains(query) || hex.Contains(query) || hex.ToLower().Contains(query);
                });
            }
        }

        [RelayCommand]
        private void Load() {
            Status = "";
            if (!TryParseId(ObjectIdText, out var id, out var isSetup)) {
                Status = "Invalid ID (use hex with 0x or decimal)";
                return;
            }

            _pendingDirectLoad = (id, isSetup);
            _requestInvalidateViewport?.Invoke();
        }

        /// <summary>Loads render data and camera for the given id. Must run with the OpenGL context current when GPU resources are created.</summary>
        private void ApplyLoadedObject(uint id, bool isSetup) {
            _currentId = id;
            _isSetup = isSetup;
            _renderData = _objectManager.GetRenderData(id, isSetup);
            if (_renderData == null) {
                Status = "Object not found in DATs";
                return;
            }

            // Pre-load all Setup parts so we can log any failures once instead of silently missing them.
            if (isSetup && _renderData.SetupParts != null) {
                int loaded = 0, failed = 0, empty = 0;
                foreach (var (partId, _) in _renderData.SetupParts) {
                    var partData = _objectManager.GetRenderData(partId, false);
                    if (partData == null)           { failed++; Console.WriteLine($"[ObjectDebug] Setup 0x{id:X8} part 0x{partId:X8} FAILED (null)"); }
                    else if (partData.Batches.Count == 0) { empty++;  Console.WriteLine($"[ObjectDebug] Setup 0x{id:X8} part 0x{partId:X8} has no batches (empty geometry)"); }
                    else                             { loaded++; }
                }
                Console.WriteLine($"[ObjectDebug] Setup 0x{id:X8}: {_renderData.SetupParts.Count} parts — {loaded} OK, {failed} null, {empty} empty-batches");
            }

            var (min, max) = EstimateObjectBounds(_renderData);
            var size = max - min;
            var center = (min + max) * 0.5f;

            var maxDim = MathF.Max(MathF.Max(size.X, size.Y), size.Z);
            var scale = 1f;
            _modelMatrix = Matrix4x4.CreateScale(scale) *
                           Matrix4x4.CreateTranslation(-center);

            var baseCamDist = maxDim * 1.5f;
            _baseCamDist = baseCamDist;
            _cameraTarget = Vector3.Zero; // model matrix centers the object at origin
            _zoomDistanceMultiplier = 1f;
            _rotationAngleY = MathF.PI / 4f;   // 45° azimuth
            _rotationAngleX = MathF.PI / 6f;   // 30° elevation

            Console.WriteLine($"Loaded 0x{id:X8} ({(isSetup ? "Setup" : "GfxObj")}): {size}");
            Console.WriteLine($"AABB: {min} - {max}");
            Console.WriteLine($"Center: {center}");
            Console.WriteLine($"Scale: {scale}");
            Console.WriteLine($"BaseCamDist: {baseCamDist}");

            Status = $"Loaded 0x{id:X8} ({(isSetup ? "Setup" : "GfxObj")})";
        }

        [RelayCommand]
        private void LoadFromId(uint id) {
            ObjectIdText = "0x" + id.ToString("X8");
            Load();
        }

        [RelayCommand]
        private async Task ExportObjAsync() {
            Status = "";
            if (_dats == null) {
                Status = "DATs not loaded";
                return;
            }
            if (!TryParseId(ObjectIdText, out var id, out var isSetup)) {
                Status = "Invalid ID (use hex with 0x or decimal)";
                return;
            }

            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow == null) {
                Status = "No main window";
                return;
            }

            var topLevel = desktop.MainWindow;
            var tag = isSetup ? "setup" : "gfxobj";
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Export Wavefront OBJ",
                SuggestedFileName = $"0x{id:X8}_{tag}.obj",
                DefaultExtension = "obj",
                FileTypeChoices = new[] {
                    new FilePickerFileType("Wavefront OBJ") { Patterns = new[] { "*.obj" } }
                }
            });
            if (file == null)
                return;

            try {
                await using var stream = await file.OpenWriteAsync();
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (isSetup) {
                    if (!WavefrontMeshExport.TryWriteSetup(_dats, id, writer, out var err))
                        Status = err ?? "Export failed";
                    else
                        Status = $"Exported Setup 0x{id:X8}";
                }
                else {
                    if (!_dats.TryGet<GfxObj>(id, out var gfx) || gfx == null)
                        Status = "GfxObj not found";
                    else {
                        WavefrontMeshExport.WriteGfxObj(gfx, id, writer);
                        Status = $"Exported GfxObj 0x{id:X8}";
                    }
                }
            }
            catch (Exception ex) {
                Status = ex.Message;
            }
        }

        [RelayCommand]
        private async Task ImportObjAsync() {
            Status = "";
            if (_dats == null) {
                Status = "DATs not loaded";
                return;
            }
            if (!_dats.CanWrite) {
                Status = "Legacy pre-ToD object debug is view-only. Import is disabled.";
                return;
            }
            if (_portalDoc == null) {
                Status = "No portal document";
                return;
            }
            if (!TryParseSurfaceDid(ImportSurfaceDidText, out var surfaceDid)) {
                Status = "Invalid Surface DID (hex with 0x or decimal)";
                return;
            }
            if (!_dats.TryGet<Surface>(surfaceDid, out var s) || s == null) {
                Status = $"Surface 0x{surfaceDid:X8} not found in portal";
                return;
            }

            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow == null) {
                Status = "No main window";
                return;
            }

            var topLevel = desktop.MainWindow;
            var picked = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Import Wavefront OBJ",
                AllowMultiple = false,
                FileTypeFilter = new[] {
                    new FilePickerFileType("Wavefront OBJ") { Patterns = new[] { "*.obj" } }
                }
            });
            var file = picked?.Count > 0 ? picked[0] : null;
            if (file == null)
                return;

            string objText;
            try {
                await using var stream = await file.OpenReadAsync();
                using var reader = new StreamReader(stream);
                objText = await reader.ReadToEndAsync();
            }
            catch (Exception ex) {
                Status = ex.Message;
                return;
            }

            var existingGfx = _dats.GetAllIdsOfType<GfxObj>()
                .Concat(_portalDoc.GetEntryIds().Where(id => (id & 0xFF000000) == 0x01000000));
            var existingSetup = _dats.GetAllIdsOfType<Setup>()
                .Concat(_portalDoc.GetEntryIds().Where(id => (id & 0xFF000000) == 0x02000000));
            var gfxId = ObjSingleMeshImporter.AllocateNextId(0x01000000, existingGfx);
            var setupId = ObjSingleMeshImporter.AllocateNextId(0x02000000, existingSetup);

            if (!ObjSingleMeshImporter.TryBuild(objText, surfaceDid, gfxId, setupId, out var gfx, out var setup, out var buildErr)) {
                Status = buildErr ?? "Import build failed";
                return;
            }

            _portalDoc.SetEntry<GfxObj>(gfxId, gfx!);
            _portalDoc.SetEntry<Setup>(setupId, setup!);

            // RegisterGfxObj uploads textures; that requires the OpenGL context, which is only current during
            // composition render — not on the async continuation thread after await.
            _pendingObjImport = (gfxId, gfx!, setupId, setup!);
            _requestInvalidateViewport?.Invoke();
            Status = "Finishing GPU upload…";
        }

        private void ProcessPendingObjImport() {
            if (!_pendingObjImport.HasValue) return;

            var p = _pendingObjImport.Value;
            _pendingObjImport = null;

            try {
                _objectManager.RegisterGfxObj(p.GfxId, p.Gfx);
                _objectManager.RegisterSetup(p.SetupId, p.Setup);

                _gfxObjIds = _gfxObjIds.Append(p.GfxId).Distinct().OrderBy(id => id);
                _setupIds = _setupIds.Append(p.SetupId).Distinct().OrderBy(id => id);
                OnSearchTextChanged(SearchText);

                _suppressSelectionLoad = true;
                try {
                    SelectedGfxObjId = null;
                    SelectedSetupId = p.SetupId;
                }
                finally {
                    _suppressSelectionLoad = false;
                }

                ObjectIdText = "0x" + p.SetupId.ToString("X8");
                ApplyLoadedObject(p.SetupId, true);
                Status = $"Imported Setup 0x{p.SetupId:X8} (GfxObj 0x{p.GfxId:X8})";
            }
            catch (Exception ex) {
                Status = ex.Message;
            }
        }

        static bool TryParseSurfaceDid(string input, out uint id) {
            id = 0;
            if (string.IsNullOrWhiteSpace(input))
                return false;
            var trimmed = input.Trim();
            var styles = System.Globalization.NumberStyles.Integer;
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                styles = System.Globalization.NumberStyles.HexNumber;
                trimmed = trimmed[2..];
            }
            return uint.TryParse(trimmed, styles, null, out id);
        }

        private bool TryParseId(string input, out uint id, out bool isSetup) {
            id = 0;
            isSetup = (id & 0x02000000) != 0;

            if (string.IsNullOrWhiteSpace(input)) return false;
            var trimmed = input.Trim();

            var styles = System.Globalization.NumberStyles.Integer;
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                styles = System.Globalization.NumberStyles.HexNumber;
                trimmed = trimmed[2..];
            }

            if (!uint.TryParse(trimmed, styles, null, out id))
                return false;

            isSetup = (id & 0x02000000) != 0;
            return true;
        }

        private (Vector3 Min, Vector3 Max) EstimateObjectBounds(StaticObjectRenderData data) {
            var bounds = _objectManager?.GetBounds(_currentId, _isSetup);
            if (bounds.HasValue)
                return bounds.Value;
            // Fallback when bounds aren't computable (e.g. no vertices loaded yet)
            return (new Vector3(-1, -1, 0), new Vector3(1, 1, 1));
        }

        /// <summary>Queue a Setup DID preview (or clear). Safe before <see cref="Init"/>; runs when GL is ready.</summary>
        public void RequestPreviewSetup(uint setupDid) {
            if (setupDid == 0) {
                RequestClearPreview();
                return;
            }
            _pendingClearPreview = false;
            _pendingPreviewSetupId = setupDid;
            _requestInvalidateViewport?.Invoke();
        }

        public void RequestClearPreview() {
            _pendingPreviewSetupId = null;
            _pendingClearPreview = true;
            _requestInvalidateViewport?.Invoke();
        }

        void ProcessPendingDirectLoad() {
            if (_objectManager == null || !_pendingDirectLoad.HasValue)
                return;
            var (id, isSetup) = _pendingDirectLoad.Value;
            _pendingDirectLoad = null;
            ApplyLoadedObject(id, isSetup);
        }

        void ProcessPendingPreview() {
            if (_objectManager == null)
                return;
            if (_pendingClearPreview) {
                _pendingClearPreview = false;
                if (_renderData != null) {
                    _objectManager.ReleaseRenderData(_currentId, _isSetup);
                    _renderData = null;
                }
                return;
            }
            if (!_pendingPreviewSetupId.HasValue)
                return;
            uint sid = _pendingPreviewSetupId.Value;
            _pendingPreviewSetupId = null;
            if (_renderData != null) {
                _objectManager.ReleaseRenderData(_currentId, _isSetup);
                _renderData = null;
            }
            ApplyLoadedObject(sid, true);
        }

        public void RotateAround(float deltaY, float deltaX) {
            _rotationAngleY += deltaY * 0.01f;
            _rotationAngleX += deltaX * 0.01f;
            _rotationAngleX = Math.Clamp(_rotationAngleX, -MathF.PI / 2.1f, MathF.PI / 2.1f);
        }

        public void Zoom(float delta) {
            _zoomDistanceMultiplier = Math.Clamp(_zoomDistanceMultiplier - delta * 0.1f, 0.5f, 10f);
        }

        public unsafe void Render(PixelSize canvasSize) {
            ProcessPendingDirectLoad();
            ProcessPendingPreview();
            ProcessPendingObjImport();
            if (_renderData == null) return;

            var gl = _gl;
            gl.FrontFace(FrontFaceDirection.CW);
            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);
            gl.ClearColor(0.1f, 0.1f, 0.1f, 1f);
            gl.ClearDepth(1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // Use PerspectiveCamera directly — matches the proven Heritage preview approach
            // and the landscape renderer's left-handed convention with -Z worldUp.
            float dist = _baseCamDist * _zoomDistanceMultiplier;
            var camOffset = new Vector3(
                MathF.Cos(_rotationAngleX) * MathF.Sin(_rotationAngleY) * dist,
                MathF.Cos(_rotationAngleX) * MathF.Cos(_rotationAngleY) * dist,
                MathF.Sin(_rotationAngleX) * dist
            );
            _camera.SetPosition(_cameraTarget + camOffset);
            _camera.LookAt(_cameraTarget);
            _camera.ScreenSize = new Vector2(canvasSize.Width, canvasSize.Height);
            var vp = _camera.GetViewMatrix() * _camera.GetProjectionMatrix();
            var cameraPos = _camera.Position;

            var shader = _objectManager._objectShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", vp);
            shader.SetUniform("uCameraPosition", cameraPos);
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.5f, 0.3f, -0.3f)));
            shader.SetUniform("uAmbientIntensity", 0.5f);
            shader.SetUniform("uSpecularPower", 32f);

            if (_renderData.IsSetup) {
                RenderSetup(_renderData, _modelMatrix);
            }
            else {
                RenderGfxObj(_renderData, _modelMatrix);
            }

            shader.Unbind();
        }

        private unsafe void RenderGfxObj(StaticObjectRenderData data, Matrix4x4 model) {
            if (data.Batches.Count == 0 || data.VAO == 0) return;

            var gl = _gl;
            var instanceVbo = CreateInstanceVbo(new[] { model });

            gl.BindVertexArray(data.VAO);

            gl.BindBuffer(GLEnum.ArrayBuffer, instanceVbo);
            for (int i = 0; i < 4; i++) {
                var attrLoc = (uint)(3 + i);
                gl.EnableVertexAttribArray(attrLoc);
                gl.VertexAttribPointer(attrLoc, 4, GLEnum.Float, false, 16 * sizeof(float), (void*)(i * 4 * sizeof(float)));
                gl.VertexAttribDivisor(attrLoc, 1);
            }
            gl.BindBuffer(GLEnum.ArrayBuffer, 0);

            foreach (var batch in data.Batches) {
                if (batch.TextureArray == null) {
                    Console.WriteLine($"Warning: TextureArray null for batch surface 0x{batch.SurfaceId:X8}");
                    continue;
                }

                if (!data.LocalAtlases[(batch.TextureSize.Width, batch.TextureSize.Height, batch.TextureFormat)].HasTexture(batch.Key)) {
                    Console.WriteLine($"Warning: Mismatch for surface 0x{batch.SurfaceId:X8}");
                }

                batch.TextureArray.Bind(0);
                var shader = _objectManager._objectShader;
                shader.SetUniform("uTextureArray", 0);
                gl.DisableVertexAttribArray(7);
                gl.VertexAttrib1((uint)7, (float)batch.TextureIndex);

                gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                gl.DrawElementsInstanced(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null, 1);
            }

            gl.BindVertexArray(0);
            gl.DeleteBuffer(instanceVbo);
        }

        private unsafe void RenderSetup(StaticObjectRenderData setupData, Matrix4x4 parentModel) {
            var gl = _gl;
            for (int i = 0; i < setupData.SetupParts.Count; i++) {
                var (gfxId, localTransform) = setupData.SetupParts[i];
                var worldModel = localTransform * parentModel;

                var childData = _objectManager.GetRenderData(gfxId, false);
                if (childData != null) {
                    RenderGfxObj(childData, worldModel);
                }
            }
        }

        private unsafe uint CreateInstanceVbo(Matrix4x4[] matrices) {
            uint vbo;
            _gl.GenBuffers(1, out vbo);
            _gl.BindBuffer(GLEnum.ArrayBuffer, vbo);

            var data = new float[matrices.Length * 16];
            for (int i = 0; i < matrices.Length; i++) {
                var m = matrices[i];
                data[i * 16 + 0] = m.M11; data[i * 16 + 1] = m.M12; data[i * 16 + 2] = m.M13; data[i * 16 + 3] = m.M14;
                data[i * 16 + 4] = m.M21; data[i * 16 + 5] = m.M22; data[i * 16 + 6] = m.M23; data[i * 16 + 7] = m.M24;
                data[i * 16 + 8] = m.M31; data[i * 16 + 9] = m.M32; data[i * 16 + 10] = m.M33; data[i * 16 + 11] = m.M34;
                data[i * 16 + 12] = m.M41; data[i * 16 + 13] = m.M42; data[i * 16 + 14] = m.M43; data[i * 16 + 15] = m.M44;
            }

            fixed (float* ptr = data)
                _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);

            return vbo;
        }

        public void Dispose() {
            if (_renderData != null) {
                _objectManager.ReleaseRenderData(_currentId, _isSetup);
                _renderData = null;
            }
        }
    }
}
