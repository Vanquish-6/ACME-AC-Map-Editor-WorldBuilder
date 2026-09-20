using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Acme.Render.GL;
using Silk.NET.OpenGL;
using System;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Editors.ObjectDebug.ViewModels;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Views;

namespace WorldBuilder.Editors.Weenie.Views {

    /// <summary>Minimal 3D viewport for rotating a Setup DID (shared render path with Object Debug).</summary>
    public partial class WeenieSetupPreviewView : Base3DView {
        public static readonly StyledProperty<uint> SetupDidProperty =
            AvaloniaProperty.Register<WeenieSetupPreviewView, uint>(nameof(SetupDid));

        public uint SetupDid {
            get => GetValue(SetupDidProperty);
            set => SetValue(SetupDidProperty, value);
        }

        private ObjectDebugViewModel? _vm;
        private IDatReaderWriter? _dats;
        private StaticObjectManager? _staticObjectManager;
        private PointerPoint? _lastPointerPoint;
        private bool _isRotating;
        /// <summary>
        /// Snapshot of <see cref="SetupDid"/> that is safe to read from the GL render thread.
        /// Written only from the UI thread (property-changed handler / constructor).
        /// </summary>
        private volatile uint _cachedSetupDid;

        public WeenieSetupPreviewView() {
            InitializeComponent();
            InitializeBase3DView();
            _vm = new ObjectDebugViewModel();
        }

        static WeenieSetupPreviewView() {
            SetupDidProperty.Changed.AddClassHandler<WeenieSetupPreviewView>((v, _) => v.OnSetupDidChanged());
        }

        // Called on the UI thread whenever SetupDid changes.
        void OnSetupDidChanged() {
            _cachedSetupDid = SetupDid;   // cache before touching _vm (safe: UI thread)
            ApplySetupDid(_cachedSetupDid);
        }

        // Safe to call from any thread — never touches AvaloniaObject properties.
        void ApplySetupDid(uint did) {
            if (_vm == null) return;
            if (did == 0)
                _vm.RequestClearPreview();
            else
                _vm.RequestPreviewSetup(did);
            // InvalidateVisual requires the UI thread — post if we're on the render thread.
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                InvalidateVisual();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        }

        protected override void OnGlDestroy() {
            _vm?.RequestClearPreview();
            _vm?.Dispose();
        }

        /// <summary>Calls <see cref="InvalidateVisual"/> on the UI thread regardless of the calling thread.</summary>
        void ThreadSafeInvalidate() {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                InvalidateVisual();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        }

        protected override void OnGlInit(GL gl, PixelSize canvasSize) {
            _dats = ProjectManager.Instance.CurrentProject?.DatReaderWriter;
            CanvasSize = canvasSize;

            // Prefer the main landscape scene's StaticObjectManager so texture arrays are created
            // on the proven terrain GL context. Avalonia/ANGLE uses a shared context group, so
            // the terrain's GL resources (VAOs, textures, etc.) are valid here too.
            // Falling back to a fresh manager only when the terrain editor hasn't started yet.
            var mainSceneManager =
                ProjectManager.Instance.GetProjectService<Editors.Landscape.ViewModels.LandscapeEditorViewModel>()
                    ?.TerrainSystem?.Scene?.AnyObjectManager;

            _staticObjectManager = mainSceneManager ?? new StaticObjectManager(Renderer, _dats!);

            // Wrap InvalidateVisual so vm callbacks are safe to call from any thread.
            _vm!.Init(Renderer, _dats!, _staticObjectManager, ThreadSafeInvalidate);
            // Use _cachedSetupDid — safe from the GL render thread (no AvaloniaObject access).
            ApplySetupDid(_cachedSetupDid);
        }

        protected override void OnGlKeyDown(KeyEventArgs e) { }

        protected override void OnGlKeyUp(KeyEventArgs e) { }

        protected override void OnGlPointerMoved(PointerEventArgs e, Vector2 mousePositionScaled) {
            var point = e.GetCurrentPoint(this);
            if (_isRotating && point.Properties.IsLeftButtonPressed) {
                if (_lastPointerPoint.HasValue) {
                    var deltaX = (float)(point.Position.X - _lastPointerPoint.Value.Position.X);
                    var deltaY = (float)(point.Position.Y - _lastPointerPoint.Value.Position.Y);
                    _vm?.RotateAround(deltaY * 0.5f, -deltaX * 0.5f);
                    InvalidateVisual();
                }
                _lastPointerPoint = point;
            }
        }

        protected override void OnGlPointerPressed(PointerPressedEventArgs e) {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed) {
                _isRotating = true;
                _lastPointerPoint = point;
                e.Pointer.Capture(this);
            }
        }

        protected override void OnGlPointerReleased(PointerReleasedEventArgs e) {
            if (_isRotating) {
                _isRotating = false;
                _lastPointerPoint = null;
                e.Pointer.Capture(null);
            }
        }

        protected override void OnGlPointerWheelChanged(PointerWheelEventArgs e) {
            var delta = (float)e.Delta.Y;
            _vm?.Zoom(-delta);
            InvalidateVisual();
        }

        protected override void OnGlRender(double frameTime) {
            _vm?.Render(CanvasSize);
        }

        public PixelSize CanvasSize { get; private set; }

        protected override void OnGlResize(PixelSize canvasSize) => CanvasSize = canvasSize;

        void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
