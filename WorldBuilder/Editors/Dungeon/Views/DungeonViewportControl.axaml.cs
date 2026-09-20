using Avalonia;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Silk.NET.OpenGL;
using System;
using System.Numerics;
using WorldBuilder.Lib;
using WorldBuilder.Views;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class DungeonViewportControl : Base3DView {
        private DungeonEditorViewModel? _viewModel;
        private bool _didInit;

        public DungeonViewportControl() {
            InitializeComponent();
            InitializeBase3DView();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnDataContextChanged(EventArgs e) {
            base.OnDataContextChanged(e);
            _viewModel = DataContext as DungeonEditorViewModel;
        }

        protected override void OnGlInit(GL gl, PixelSize canvasSize) {
            if (_viewModel != null && Renderer != null) {
                _viewModel.OnRendererReady(Renderer);
                _didInit = true;
            }
        }

        protected override void OnGlRender(double deltaTime) {
            if (!_didInit || _viewModel == null) return;
            var renderSize = CurrentRenderSize;
            _viewModel.RenderFrame(deltaTime, renderSize, InputState);
        }

        protected override void OnGlResize(PixelSize canvasSize) { }

        protected override void OnGlDestroy() { }

        protected override void OnGlKeyDown(KeyEventArgs e) {
            _viewModel?.HandleKeyDown(e);
        }

        protected override void OnGlKeyUp(KeyEventArgs e) { }

        protected override void OnGlPointerMoved(PointerEventArgs e, Vector2 mousePositionScaled) {
            UpdateMouseState(e.GetPosition(this), e.GetCurrentPoint(this).Properties);
            _viewModel?.HandlePointerMoved(InputState);
        }

        protected override void OnGlPointerWheelChanged(PointerWheelEventArgs e) {
            _viewModel?.HandlePointerWheel(e);
        }

        protected override void OnGlPointerPressed(PointerPressedEventArgs e) {
            var props = e.GetCurrentPoint(this).Properties;
            UpdateMouseState(e.GetPosition(this), props);

            _viewModel?.HandlePointerPressed(InputState);
            e.Pointer.Capture(this);
        }

        protected override void OnGlPointerReleased(PointerReleasedEventArgs e) {
            UpdateMouseState(e.GetPosition(this), e.GetCurrentPoint(this).Properties);
            _viewModel?.HandlePointerReleased(InputState);
            e.Pointer.Capture(null);
        }

        protected override void UpdateMouseState(Avalonia.Point position, PointerPointProperties properties) {
            var scale = InputScale;
            if (scale == Vector2.Zero) scale = Vector2.One;

            var width = (int)(Bounds.Width * scale.X);
            var height = (int)(Bounds.Height * scale.Y);
            if (width == 0) width = (int)Bounds.Width;
            if (height == 0) height = (int)Bounds.Height;

            InputState.UpdateMouseStateBasic(position, properties, width, height, scale);
        }
    }
}
