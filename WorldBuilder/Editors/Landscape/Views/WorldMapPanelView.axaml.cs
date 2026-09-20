using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System;
using WorldBuilder.Editors.Landscape.ViewModels;

namespace WorldBuilder.Editors.Landscape.Views {
    public partial class WorldMapPanelView : UserControl {
        private WorldMapCanvas? _mapCanvas;
        private WorldMapPanelViewModel? _vm;

        private bool _isDragging;
        private Point _lastDragPoint;

        public WorldMapPanelView() {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e) {
            if (DataContext is not WorldMapPanelViewModel vm) return;

            _vm = vm;

            var border = this.FindControl<Border>("MapBorder");
            if (border != null && _mapCanvas == null) {
                _mapCanvas = new WorldMapCanvas();
                _mapCanvas.SetViewModel(vm);
                border.Child = _mapCanvas;

                border.PointerPressed += OnPointerPressed;
                border.PointerMoved += OnPointerMoved;
                border.PointerReleased += OnPointerReleased;
                border.PointerWheelChanged += OnPointerWheelChanged;
                border.PointerExited += OnPointerExited;
            }

            var centerBtn = this.FindControl<Button>("CenterBtn");
            if (centerBtn != null)
                centerBtn.Click += (_, _) => CenterOnCamera();

            var rebuildBtn = this.FindControl<Button>("RebuildBtn");
            if (rebuildBtn != null)
                rebuildBtn.Click += async (_, _) => await vm.BuildMapBitmapAsync();
        }

        private void CenterOnCamera() {
            if (_vm == null || _mapCanvas == null) return;
            _vm.CenterOnCamera(_mapCanvas.Bounds.Width, _mapCanvas.Bounds.Height);
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e) {
            if (_vm == null || _mapCanvas == null) return;

            var pt = e.GetPosition(_mapCanvas);
            var props = e.GetCurrentPoint(_mapCanvas).Properties;

            if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt))) {
                _isDragging = true;
                _lastDragPoint = pt;
                e.Pointer.Capture(_mapCanvas);
            }
            else if (props.IsLeftButtonPressed) {
                _vm.HandleClick(pt.X, pt.Y, _mapCanvas.Bounds.Width, _mapCanvas.Bounds.Height);
                UpdateStatusBar(pt);
            }
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e) {
            if (_vm == null || _mapCanvas == null) return;

            var pt = e.GetPosition(_mapCanvas);

            if (_isDragging) {
                double dx = pt.X - _lastDragPoint.X;
                double dy = pt.Y - _lastDragPoint.Y;
                _lastDragPoint = pt;
                _vm.HandleDrag(dx, dy);
            }

            UpdateStatusBar(pt);
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) {
            if (_isDragging) {
                _isDragging = false;
                e.Pointer.Capture(null);
            }
        }

        private void OnPointerExited(object? sender, PointerEventArgs e) {
            _isDragging = false;
            var status = this.FindControl<TextBlock>("StatusText");
            if (status != null) status.Text = "";
        }

        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e) {
            if (_vm == null || _mapCanvas == null) return;
            var pt = e.GetPosition(_mapCanvas);
            _vm.HandleScroll(e.Delta.Y, pt.X, pt.Y, _mapCanvas.Bounds.Width, _mapCanvas.Bounds.Height);
        }

        private void UpdateStatusBar(Point pt) {
            if (_vm == null || _mapCanvas == null) return;

            var status = this.FindControl<TextBlock>("StatusText");
            if (status == null) return;

            var (lbX, lbY) = _vm.ScreenToLandblock(pt.X, pt.Y, _mapCanvas.Bounds.Width, _mapCanvas.Bounds.Height);
            int ix = (int)Math.Clamp(lbX, 1, 254);
            int iy = (int)Math.Clamp(lbY, 1, 254);
            ushort lbKey = (ushort)((ix << 8) | iy);

            status.Text = $"LB {lbKey:X4}  ({ix:D3}, {iy:D3})  Zoom: {_vm.Zoom:F1}x";
        }
    }
}
