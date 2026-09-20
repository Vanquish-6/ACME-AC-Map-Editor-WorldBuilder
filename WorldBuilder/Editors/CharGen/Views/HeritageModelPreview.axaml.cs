using Avalonia;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Silk.NET.OpenGL;
using System;
using System.Numerics;
using WorldBuilder.Lib;
using WorldBuilder.Views;

namespace WorldBuilder.Editors.CharGen.Views {
    public partial class HeritageModelPreview : Base3DView {
        private HeritageModelPreviewViewModel? _vm;
        private PointerPoint? _lastPointerPoint;
        private bool _isRotating;

        public PixelSize CanvasSize { get; private set; }
        public HeritageModelPreviewViewModel? ViewModel => _vm;

        public event Action? GlInitialized;

        public HeritageModelPreview() {
            InitializeComponent();
            InitializeBase3DView();
            _vm = new HeritageModelPreviewViewModel();
            DataContext = _vm;
        }

        public void SetModelIds(uint setupId, uint envSetupId) {
            _vm?.SetModelIds(setupId, envSetupId);
        }

        protected override void OnGlInit(GL gl, PixelSize canvasSize) {
            CanvasSize = canvasSize;
            var project = ProjectManager.Instance.CurrentProject;
            if (project?.DatReaderWriter == null) return;

            _vm?.Init(Renderer!, project.DatReaderWriter);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => GlInitialized?.Invoke());
        }

        protected override void OnGlRender(double frameTime) {
            _vm?.Render(CanvasSize);
        }

        protected override void OnGlResize(PixelSize canvasSize) {
            CanvasSize = canvasSize;
        }

        protected override void OnGlDestroy() {
            _vm?.Dispose();
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
            _vm?.Zoom(-(float)e.Delta.Y);
            InvalidateVisual();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
