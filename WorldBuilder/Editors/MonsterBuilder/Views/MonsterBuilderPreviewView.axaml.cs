using Avalonia;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Acme.Render;
using Acme.Render.GL;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.MonsterBuilder;
using WorldBuilder.Views;

namespace WorldBuilder.Editors.MonsterBuilder.Views;

public partial class MonsterBuilderPreviewView : Base3DView {
    StaticObjectManager? _meshes;
    IShader? _lines;
    GL? _gl;
    PerspectiveCamera? _camera;
    PointerPoint? _lastPoint;
    bool _orbit;
    bool _pan;
    float _yaw = 0.7f;
    float _pitch = 0.35f;
    float _zoom = 1f;
    float _distance = 4f;
    Vector3 _target;
    int _revision = -1;
    int _cameraRequest = -1;
    readonly List<uint> _uploaded = [];
    int _renderCacheKey = int.MinValue;
    Matrix4x4 _centerFrame = Matrix4x4.Identity;
    float _cachedDistance = 4f;
    List<float> _cachedLines = [];
    List<float> _cachedUntextured = [];
    uint _instanceVbo;
    uint _linesVao;
    uint _linesVbo;
    int _linesVertexCount;
    uint _soupVao;
    uint _soupVbo;
    int _soupVertexCount;

    MonsterBuilderViewModel? _watched;
    PreviewFrame? _frame;

    public MonsterBuilderPreviewView() {
        InitializeComponent();
        InitializeBase3DView();
        DataContextChanged += (_, _) => {
            if (_watched != null)
                _watched.PropertyChanged -= OnViewModelChanged;
            _watched = DataContext as MonsterBuilderViewModel;
            if (_watched != null)
                _watched.PropertyChanged += OnViewModelChanged;
            CaptureFrame();
        };
    }

    void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        if (e.PropertyName is not (
            nameof(MonsterBuilderViewModel.PreviewRevision)
            or nameof(MonsterBuilderViewModel.SelectedPartIndex)
            or nameof(MonsterBuilderViewModel.ShowSolid)
            or nameof(MonsterBuilderViewModel.ShowWireframe)
            or nameof(MonsterBuilderViewModel.ShowNormals)
            or nameof(MonsterBuilderViewModel.ShowBounds)
            or nameof(MonsterBuilderViewModel.ShowOrigins)
            or nameof(MonsterBuilderViewModel.ShowHierarchy)
            or nameof(MonsterBuilderViewModel.CameraRequest)
            or nameof(MonsterBuilderViewModel.CameraPreset))) {
            return;
        }
        CaptureFrame();
        InvalidateVisual();
    }

    void CaptureFrame() {
        var vm = _watched;
        if (vm?.Session?.WorkingSetup == null) {
            _frame = null;
            return;
        }
        var session = vm.Session;
        _frame = new PreviewFrame {
            Setup = session.WorkingSetup,
            Gfx = session.GfxById,
            Hidden = session.Project.Parts.Where(part => part.Hidden).Select(part => part.Index).ToHashSet(),
            SelectedPart = vm.SelectedPartIndex,
            ShowSolid = vm.ShowSolid,
            ShowWireframe = vm.ShowWireframe,
            ShowNormals = vm.ShowNormals,
            ShowBounds = vm.ShowBounds,
            ShowOrigins = vm.ShowOrigins,
            ShowHierarchy = vm.ShowHierarchy,
            Revision = vm.PreviewRevision,
            CameraRequest = vm.CameraRequest,
            CameraPreset = vm.CameraPreset ?? "",
        };
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnGlInit(GL gl, PixelSize canvasSize) {
        var dats = ProjectManager.Instance.CurrentProject?.DatReaderWriter;
        if (dats == null || Renderer == null) return;
        _gl = gl;
        _meshes = new StaticObjectManager(Renderer, dats) {
            // Textures created in the builder live only in the session until publish.
            RecordOverlay = (type, id) => _watched?.Session?.TryGetTextureRecord(type, id),
        };
        var assembly = typeof(GameScene).Assembly;
        _lines = Renderer.GraphicsDevice.CreateShader(
            "MonsterBuilderLines",
            GameScene.GetEmbeddedResource("WorldBuilder.Shaders.Gizmo.vert", assembly),
            GameScene.GetEmbeddedResource("WorldBuilder.Shaders.Gizmo.frag", assembly));
        _camera = new PerspectiveCamera(new Vector3(0, 4, 2), new WorldBuilder.Lib.Settings.WorldBuilderSettings());
        CanvasSize = canvasSize;
    }

    protected override void OnGlDestroy() {
        if (_meshes != null) {
            foreach (uint id in _uploaded)
                _meshes.DropRenderData(id);
            _meshes.Dispose();
            _meshes = null;
        }
        if (_gl != null) {
            if (_instanceVbo != 0) { _gl.DeleteBuffer(_instanceVbo); _instanceVbo = 0; }
            if (_linesVbo != 0) { _gl.DeleteBuffer(_linesVbo); _linesVbo = 0; }
            if (_linesVao != 0) { _gl.DeleteVertexArray(_linesVao); _linesVao = 0; }
            if (_soupVbo != 0) { _gl.DeleteBuffer(_soupVbo); _soupVbo = 0; }
            if (_soupVao != 0) { _gl.DeleteVertexArray(_soupVao); _soupVao = 0; }
        }
        _linesVertexCount = 0;
        _soupVertexCount = 0;
        _lines?.Dispose();
        _lines = null;
    }

    protected override void OnGlResize(PixelSize canvasSize) => CanvasSize = canvasSize;
    public PixelSize CanvasSize { get; private set; }
    protected override void OnGlKeyDown(KeyEventArgs e) { }
    protected override void OnGlKeyUp(KeyEventArgs e) { }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        Focus();
        OnGlPointerPressed(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) => OnGlPointerReleased(e);

    protected override void OnPointerMoved(PointerEventArgs e) =>
        OnGlPointerMoved(e, new Vector2((float)e.GetPosition(this).X, (float)e.GetPosition(this).Y));

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        OnGlPointerWheelChanged(e);
        e.Handled = true;
    }

    protected override void OnGlPointerPressed(PointerPressedEventArgs e) {
        var point = e.GetCurrentPoint(this);
        _lastPoint = point;
        _orbit = point.Properties.IsLeftButtonPressed;
        _pan = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed;
        e.Pointer.Capture(this);
    }

    protected override void OnGlPointerReleased(PointerReleasedEventArgs e) {
        _orbit = false;
        _pan = false;
        _lastPoint = null;
        e.Pointer.Capture(null);
    }

    protected override void OnGlPointerMoved(PointerEventArgs e, Vector2 mousePositionScaled) {
        if (_lastPoint == null) return;
        var point = e.GetCurrentPoint(this);
        float dx = (float)(point.Position.X - _lastPoint.Value.Position.X);
        float dy = (float)(point.Position.Y - _lastPoint.Value.Position.Y);
        _lastPoint = point;
        if (_orbit) {
            _yaw += dx * 0.01f;
            _pitch = Math.Clamp(_pitch - dy * 0.01f, -1.2f, 1.2f);
            InvalidateVisual();
        }
        else if (_pan) {
            _target += new Vector3(-dx, dy, 0) * _distance * _zoom * 0.002f;
            InvalidateVisual();
        }
    }

    protected override void OnGlPointerWheelChanged(PointerWheelEventArgs e) {
        _zoom = Math.Clamp(_zoom - (float)e.Delta.Y * 0.08f, 0.15f, 12f);
        InvalidateVisual();
    }

    protected override void OnGlRender(double frameTime) {
        if (_gl == null || _meshes == null || _camera == null || Renderer == null) return;
        var vm = _frame;
        if (vm?.Setup == null) {
            _gl.ClearColor(0.08f, 0.07f, 0.1f, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            return;
        }

        ApplyCameraRequest(vm);
        EnsureMeshes(vm);
        EnsureRenderCache(vm);
        var setup = vm.Setup;
        var hidden = vm.Hidden;
        _distance = _cachedDistance;
        var frame = _centerFrame;

        float dist = _distance * _zoom;
        var offset = new Vector3(
            MathF.Cos(_pitch) * MathF.Sin(_yaw) * dist,
            MathF.Cos(_pitch) * MathF.Cos(_yaw) * dist,
            MathF.Sin(_pitch) * dist);
        _camera.SetPosition(_target + offset);
        _camera.LookAt(_target);
        _camera.ScreenSize = new Vector2(CanvasSize.Width, CanvasSize.Height);
        var viewProjection = _camera.GetViewMatrix() * _camera.GetProjectionMatrix();

        var gl = _gl;
        // The shared viewport defaults to a reversed depth test. Object preview uses
        // the standard less-equal test, or every fragment is rejected and the view stays black.
        gl.FrontFace(FrontFaceDirection.CW);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.DepthMask(true);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.ClearColor(0.08f, 0.07f, 0.1f, 1f);
        gl.ClearDepth(1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (vm.ShowSolid || vm.ShowWireframe) {
            var shader = _meshes._objectShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", _camera.Position);
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.4f, 0.2f, 0.8f)));
            shader.SetUniform("uAmbientIntensity", 0.55f);
            shader.SetUniform("uSpecularPower", 24f);
            for (int i = 0; i < setup.Parts.Count; i++) {
                if (hidden.Contains(i)) continue;
                var data = _meshes.GetRenderData(setup.Parts[i], false);
                if (data == null) continue;
                var model = SetupAssembly.LocalToSetup(setup, i) * frame;
                bool selected = i == vm.SelectedPart;
                if (vm.ShowSolid)
                    DrawMesh(gl, data, model, fill: true);
                if (vm.ShowWireframe || selected)
                    DrawMesh(gl, data, model, fill: false);
            }
            shader.Unbind();
        }

        if (_soupVertexCount > 0)
            DrawCachedArrays(gl, viewProjection, _soupVao, _soupVertexCount, GLEnum.Triangles);
        if (_linesVertexCount > 0)
            DrawCachedArrays(gl, viewProjection, _linesVao, _linesVertexCount, GLEnum.Lines);
    }

    void EnsureMeshes(PreviewFrame vm) {
        if (vm.Revision == _revision || _meshes == null)
            return;
        foreach (uint id in _uploaded)
            _meshes.DropRenderData(id);
        _uploaded.Clear();
        foreach (var gfx in vm.Gfx.Values) {
            _meshes.RegisterGfxObj(gfx.Id, gfx);
            _uploaded.Add(gfx.Id);
        }
        _revision = vm.Revision;
        _target = Vector3.Zero;
        _renderCacheKey = int.MinValue;
    }

    void EnsureRenderCache(PreviewFrame vm) {
        if (_meshes == null || vm.Setup == null) return;
        var hash = new HashCode();
        hash.Add(vm.Revision);
        hash.Add(vm.SelectedPart);
        hash.Add(vm.ShowSolid);
        hash.Add(vm.ShowNormals);
        hash.Add(vm.ShowBounds);
        hash.Add(vm.ShowOrigins);
        hash.Add(vm.ShowHierarchy);
        foreach (int part in vm.Hidden.Order())
            hash.Add(part);
        int key = hash.ToHashCode();
        if (key == _renderCacheKey) return;

        var setup = vm.Setup;
        var (min, max) = SetupAssembly.Bounds(setup, id => vm.Gfx.TryGetValue(id, out var gfx) ? gfx : null);
        var center = (min + max) * 0.5f;
        var size = max - min;
        _cachedDistance = MathF.Max(1f, MathF.Max(size.X, MathF.Max(size.Y, size.Z)) * 1.6f);
        _centerFrame = Matrix4x4.CreateTranslation(-center);

        var lines = new List<float>();
        var soup = new List<float>();
        var light = Vector3.Normalize(new Vector3(0.35f, 0.25f, 0.85f));
        for (int i = 0; i < setup.Parts.Count; i++) {
            if (vm.Hidden.Contains(i)) continue;
            var model = SetupAssembly.LocalToSetup(setup, i) * _centerFrame;
            bool selected = i == vm.SelectedPart;
            var data = _meshes.GetRenderData(setup.Parts[i], false);
            bool textured = data is { Batches.Count: > 0, VAO: > 0 };
            if ((!textured || !vm.ShowSolid) && vm.Gfx.TryGetValue(setup.Parts[i], out var rawGfx)) {
                var tint = selected ? new Vector3(1f, 0.72f, 0.35f) : new Vector3(0.72f, 0.68f, 0.78f);
                AppendTriangles(soup, rawGfx, model, light, tint);
            }
            if (vm.ShowOrigins)
                AddAxes(lines, model, selected ? 0.35f : 0.18f);
            if (vm.ShowBounds && vm.Gfx.TryGetValue(setup.Parts[i], out var gfx))
                AddBox(lines, model, GfxObjMesh.Stats(gfx));
            if (vm.ShowNormals && vm.Gfx.TryGetValue(setup.Parts[i], out var normalGfx))
                AddNormals(lines, model, normalGfx);
            if (vm.ShowHierarchy && SetupAssembly.HasParentLink(setup, i, out int parent) && !vm.Hidden.Contains(parent)) {
                var child = Vector3.Transform(Vector3.Zero, model);
                var parentPoint = Vector3.Transform(Vector3.Zero, SetupAssembly.LocalToSetup(setup, parent) * _centerFrame);
                AddLine(lines, child, parentPoint, new Vector3(0.95f, 0.75f, 0.2f));
            }
        }
        _cachedLines = lines;
        _cachedUntextured = soup;
        UploadCachedGeometry(gl: _gl!, lines, soup);
        _renderCacheKey = key;
    }

    unsafe void UploadCachedGeometry(GL gl, List<float> lines, List<float> soup) {
        _linesVertexCount = UploadColorMesh(gl, ref _linesVao, ref _linesVbo, lines);
        _soupVertexCount = UploadColorMesh(gl, ref _soupVao, ref _soupVbo, soup);
    }

    static unsafe int UploadColorMesh(GL gl, ref uint vao, ref uint vbo, List<float> vertices) {
        if (vertices.Count < 6) {
            if (vbo != 0) { gl.DeleteBuffer(vbo); vbo = 0; }
            if (vao != 0) { gl.DeleteVertexArray(vao); vao = 0; }
            return 0;
        }

        if (vao == 0) gl.GenVertexArrays(1, out vao);
        if (vbo == 0) gl.GenBuffers(1, out vbo);
        gl.BindVertexArray(vao);
        gl.BindBuffer(GLEnum.ArrayBuffer, vbo);
        var data = vertices.ToArray();
        fixed (float* ptr = data)
            gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        gl.BindVertexArray(0);
        return data.Length / 6;
    }

    void DrawCachedArrays(GL gl, Matrix4x4 viewProjection, uint vao, int vertexCount, GLEnum mode) {
        if (_lines == null || vao == 0 || vertexCount <= 0) return;
        gl.BindVertexArray(vao);
        _lines.Bind();
        _lines.SetUniform("uViewProjection", viewProjection);
        _lines.SetUniform("uModel", Matrix4x4.Identity);
        _lines.SetUniform("uAlpha", 1f);
        _lines.SetUniform("uBrightness", 1f);
        _lines.SetUniform("uHighlightColor", Vector3.Zero);
        _lines.SetUniform("uHighlightMix", 0f);
        gl.DrawArrays(mode, 0, (uint)vertexCount);
        _lines.Unbind();
        gl.BindVertexArray(0);
    }

    void ApplyCameraRequest(PreviewFrame vm) {
        if (vm.CameraRequest == _cameraRequest) return;
        _cameraRequest = vm.CameraRequest;
        _zoom = 1f;
        _target = Vector3.Zero;
        (_yaw, _pitch) = vm.CameraPreset switch {
            "front" => (0f, 0.15f),
            "back" => (MathF.PI, 0.15f),
            "left" => (-MathF.PI / 2f, 0.15f),
            "right" => (MathF.PI / 2f, 0.15f),
            "top" => (0f, 1.15f),
            _ => (0.7f, 0.35f),
        };
    }

    static void AppendTriangles(List<float> soup, Acme.Dat.GfxObj gfx, Matrix4x4 model, Vector3 light, Vector3 tint) {
        foreach (var poly in gfx.Polygons.Values) {
            if (poly.VertexIds.Count < 3 || poly.Stippling == Acme.Dat.StipplingType.NoPos) continue;
            if (!gfx.VertexArray.Vertices.TryGetValue(poly.VertexIds[0], out var v0)) continue;
            for (int fan = 2; fan < poly.VertexIds.Count; fan++) {
                if (!gfx.VertexArray.Vertices.TryGetValue(poly.VertexIds[fan - 1], out var v1)
                    || !gfx.VertexArray.Vertices.TryGetValue(poly.VertexIds[fan], out var v2))
                    continue;
                var p0 = Vector3.Transform(v0.Origin, model);
                var p1 = Vector3.Transform(v1.Origin, model);
                var p2 = Vector3.Transform(v2.Origin, model);
                var normal = Vector3.Cross(p1 - p0, p2 - p0);
                float length = normal.Length();
                if (length < 1e-8f) continue;
                normal /= length;
                float shade = Math.Clamp(MathF.Abs(Vector3.Dot(normal, light)) + 0.35f, 0f, 1f);
                var color = tint * shade;
                AddVertex(soup, p0, color);
                AddVertex(soup, p1, color);
                AddVertex(soup, p2, color);
            }
        }
    }

    static void AddVertex(List<float> soup, Vector3 position, Vector3 color) {
        soup.Add(position.X);
        soup.Add(position.Y);
        soup.Add(position.Z);
        soup.Add(color.X);
        soup.Add(color.Y);
        soup.Add(color.Z);
    }

    unsafe void DrawMesh(GL gl, StaticObjectRenderData data, Matrix4x4 model, bool fill) {
        if (data.Batches.Count == 0 || data.VAO == 0 || _meshes == null) return;
        if (!TrySetPolygonMode(gl, fill ? GLEnum.Fill : GLEnum.Line) && !fill)
            return;
        if (_instanceVbo == 0)
            gl.GenBuffers(1, out _instanceVbo);
        UpdateInstanceVbo(gl, _instanceVbo, model);
        gl.BindVertexArray(data.VAO);
        gl.BindBuffer(GLEnum.ArrayBuffer, _instanceVbo);
        for (int i = 0; i < 4; i++) {
            uint location = (uint)(3 + i);
            gl.EnableVertexAttribArray(location);
            gl.VertexAttribPointer(location, 4, GLEnum.Float, false, 16 * sizeof(float), (void*)(i * 4 * sizeof(float)));
            gl.VertexAttribDivisor(location, 1);
        }
        foreach (var batch in data.Batches) {
            if (batch.TextureArray == null) continue;
            batch.TextureArray.Bind(0);
            _meshes._objectShader.SetUniform("uTextureArray", 0);
            gl.VertexAttrib1(7, (float)batch.TextureIndex);
            gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
            gl.DrawElementsInstanced(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null, 1);
        }
        gl.BindVertexArray(0);
        TrySetPolygonMode(gl, GLEnum.Fill);
    }

    static bool TrySetPolygonMode(GL gl, GLEnum mode) {
        while (gl.GetError() != GLEnum.NoError) { }
        try {
            gl.PolygonMode(GLEnum.FrontAndBack, mode);
        }
        catch {
            return false;
        }
        return gl.GetError() == GLEnum.NoError;
    }

    static unsafe void UpdateInstanceVbo(GL gl, uint vbo, Matrix4x4 model) {
        gl.BindBuffer(GLEnum.ArrayBuffer, vbo);
        float[] data = [
            model.M11, model.M12, model.M13, model.M14,
            model.M21, model.M22, model.M23, model.M24,
            model.M31, model.M32, model.M33, model.M34,
            model.M41, model.M42, model.M43, model.M44,
        ];
        fixed (float* ptr = data)
            gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
    }

    static void AddAxes(List<float> lines, Matrix4x4 model, float length) {
        var origin = Vector3.Transform(Vector3.Zero, model);
        AddLine(lines, origin, Vector3.Transform(new Vector3(length, 0, 0), model), new Vector3(1, 0.2f, 0.2f));
        AddLine(lines, origin, Vector3.Transform(new Vector3(0, length, 0), model), new Vector3(0.2f, 1, 0.3f));
        AddLine(lines, origin, Vector3.Transform(new Vector3(0, 0, length), model), new Vector3(0.3f, 0.5f, 1));
    }

    static void AddBox(List<float> lines, Matrix4x4 model, MeshStats stats) {
        var min = stats.Min;
        var max = stats.Max;
        Vector3[] corners = [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        for (int i = 0; i < 8; i++)
            corners[i] = Vector3.Transform(corners[i], model);
        int[] edges = [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7];
        var color = new Vector3(0.55f, 0.75f, 0.95f);
        for (int i = 0; i < edges.Length; i += 2)
            AddLine(lines, corners[edges[i]], corners[edges[i + 1]], color);
    }

    static void AddNormals(List<float> lines, Matrix4x4 model, Acme.Dat.GfxObj gfx) {
        var color = new Vector3(0.4f, 0.9f, 0.85f);
        int drawn = 0;
        foreach (var vertex in gfx.VertexArray.Vertices.Values) {
            if (drawn++ > 400) break;
            var start = Vector3.Transform(vertex.Origin, model);
            var end = Vector3.Transform(vertex.Origin + vertex.Normal * 0.05f, model);
            AddLine(lines, start, end, color);
        }
    }

    static void AddLine(List<float> lines, Vector3 a, Vector3 b, Vector3 color) {
        lines.AddRange([a.X, a.Y, a.Z, color.X, color.Y, color.Z, b.X, b.Y, b.Z, color.X, color.Y, color.Z]);
    }

    sealed class PreviewFrame {
        public Acme.Dat.Setup? Setup;
        public Dictionary<uint, Acme.Dat.GfxObj> Gfx = [];
        public HashSet<int> Hidden = [];
        public int SelectedPart = -1;
        public bool ShowSolid = true;
        public bool ShowWireframe;
        public bool ShowNormals;
        public bool ShowBounds;
        public bool ShowOrigins = true;
        public bool ShowHierarchy = true;
        public int Revision;
        public int CameraRequest;
        public string CameraPreset = "";
    }
}
