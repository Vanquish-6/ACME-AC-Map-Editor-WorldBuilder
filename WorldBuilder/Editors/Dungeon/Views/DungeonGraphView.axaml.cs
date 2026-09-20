using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class DungeonGraphView : UserControl {
        private DungeonEditorViewModel? _viewModel;
        private readonly Dictionary<ushort, Vector2> _nodePositions = new();
        private readonly Dictionary<ushort, Border> _nodeVisuals = new();
        private ushort? _hoveredNode;
        private Vector2 _panOffset;
        private float _zoom = 1f;
        private Point _lastPanPoint;
        private bool _isPanning;

        public DungeonGraphView() {
            InitializeComponent();
            GraphCanvas.PointerPressed += OnCanvasPointerPressed;
            GraphCanvas.PointerMoved += OnCanvasPointerMoved;
            GraphCanvas.PointerReleased += OnCanvasPointerReleased;
            GraphCanvas.PointerWheelChanged += OnCanvasWheel;
        }

        protected override void OnDataContextChanged(EventArgs e) {
            base.OnDataContextChanged(e);
            _viewModel = DataContext as DungeonEditorViewModel;
        }

        public void Refresh(DungeonDocument? document, ushort? selectedCellNum) {
            GraphCanvas.Children.Clear();
            _nodePositions.Clear();
            _nodeVisuals.Clear();
            if (document == null || document.Cells.Count == 0) return;

            ComputeLayout(document);
            RenderGraph(document, selectedCellNum);
        }

        private void ComputeLayout(DungeonDocument document) {
            if (document.Cells.Count == 0) return;

            var cells = document.Cells;
            var adjacency = new Dictionary<ushort, List<ushort>>();
            foreach (var cell in cells) {
                adjacency[cell.CellNumber] = cell.CellPortals
                    .Select(p => p.OtherCellId)
                    .Where(id => cells.Any(c => c.CellNumber == id))
                    .ToList();
            }

            // BFS layered layout from first cell
            var visited = new HashSet<ushort>();
            var layers = new List<List<ushort>>();
            var queue = new Queue<ushort>();
            var first = cells[0].CellNumber;
            queue.Enqueue(first);
            visited.Add(first);

            while (queue.Count > 0) {
                var layer = new List<ushort>();
                int count = queue.Count;
                for (int i = 0; i < count; i++) {
                    var current = queue.Dequeue();
                    layer.Add(current);
                    if (adjacency.TryGetValue(current, out var neighbors)) {
                        foreach (var n in neighbors) {
                            if (visited.Add(n)) queue.Enqueue(n);
                        }
                    }
                }
                layers.Add(layer);
            }

            // Add disconnected cells as their own layer
            var disconnected = cells.Where(c => !visited.Contains(c.CellNumber)).Select(c => c.CellNumber).ToList();
            if (disconnected.Count > 0) layers.Add(disconnected);

            const float layerSpacing = 60f;
            const float nodeSpacing = 50f;

            for (int l = 0; l < layers.Count; l++) {
                var layer = layers[l];
                float startX = -(layer.Count - 1) * nodeSpacing * 0.5f;
                for (int n = 0; n < layer.Count; n++) {
                    _nodePositions[layer[n]] = new Vector2(startX + n * nodeSpacing, l * layerSpacing);
                }
            }
        }

        private void RenderGraph(DungeonDocument document, ushort? selectedCellNum) {
            var canvas = GraphCanvas;
            double cw = canvas.Bounds.Width;
            double ch = canvas.Bounds.Height;
            if (cw < 10 || ch < 10) { cw = 260; ch = 400; }

            // Compute bounds for centering
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var pos in _nodePositions.Values) {
                if (pos.X < minX) minX = pos.X;
                if (pos.X > maxX) maxX = pos.X;
                if (pos.Y < minY) minY = pos.Y;
                if (pos.Y > maxY) maxY = pos.Y;
            }

            float rangeX = maxX - minX + 80;
            float rangeY = maxY - minY + 80;
            float scale = Math.Min((float)cw / rangeX, (float)ch / rangeY) * _zoom;
            scale = Math.Max(scale, 0.3f);
            float cx = (minX + maxX) * 0.5f;
            float cy = (minY + maxY) * 0.5f;

            Point ToScreen(Vector2 p) => new Point(
                (p.X - cx) * scale + cw * 0.5 + _panOffset.X,
                (p.Y - cy) * scale + ch * 0.5 + _panOffset.Y);

            // Build set of neighbor cell numbers for highlighting
            var neighborNums = new HashSet<ushort>();
            if (selectedCellNum.HasValue) {
                var selCell = document.Cells.FirstOrDefault(c => c.CellNumber == selectedCellNum.Value);
                if (selCell != null) {
                    foreach (var cp in selCell.CellPortals) {
                        if (cp.OtherCellId != 0 && cp.OtherCellId != 0xFFFF)
                            neighborNums.Add(cp.OtherCellId);
                    }
                }
            }

            // Draw edges first (dim for unrelated, bright for selected cell connections)
            var drawnEdges = new HashSet<(ushort, ushort)>();
            foreach (var cell in document.Cells) {
                if (!_nodePositions.TryGetValue(cell.CellNumber, out var fromPos)) continue;
                var fromScreen = ToScreen(fromPos);

                foreach (var portal in cell.CellPortals) {
                    var key = (Math.Min(cell.CellNumber, portal.OtherCellId), Math.Max(cell.CellNumber, portal.OtherCellId));
                    if (!drawnEdges.Add(key)) continue;
                    if (!_nodePositions.TryGetValue(portal.OtherCellId, out var toPos)) continue;
                    var toScreen = ToScreen(toPos);

                    bool touchesSelection = selectedCellNum.HasValue &&
                        (cell.CellNumber == selectedCellNum.Value || portal.OtherCellId == selectedCellNum.Value);

                    var line = new Line {
                        StartPoint = fromScreen,
                        EndPoint = toScreen,
                        Stroke = new SolidColorBrush(Color.Parse(touchesSelection ? "#ffcc33" : "#2a4477")),
                        StrokeThickness = touchesSelection ? 2.5 : 1.2,
                    };
                    canvas.Children.Add(line);
                }
            }

            // Draw nodes
            const double nodeSize = 28;
            foreach (var cell in document.Cells) {
                if (!_nodePositions.TryGetValue(cell.CellNumber, out var pos)) continue;
                var screenPos = ToScreen(pos);

                bool isSelected = selectedCellNum.HasValue && cell.CellNumber == selectedCellNum.Value;
                bool isNeighbor = neighborNums.Contains(cell.CellNumber);
                bool isDisconnected = cell.CellPortals.Count == 0 && document.Cells.Count > 1;

                string bgColor, borderColor;
                if (isSelected) {
                    bgColor = "#7c4dbd"; borderColor = "#c0a0ff";
                } else if (isNeighbor) {
                    bgColor = "#1a4466"; borderColor = "#55aadd";
                } else if (isDisconnected) {
                    bgColor = "#663333"; borderColor = "#cc6666";
                } else {
                    bgColor = "#2a1d42"; borderColor = "#4a3a6e";
                }

                var node = new Border {
                    Width = nodeSize,
                    Height = nodeSize,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.Parse(bgColor)),
                    BorderBrush = new SolidColorBrush(Color.Parse(borderColor)),
                    BorderThickness = new Thickness(isSelected ? 2 : isNeighbor ? 1.5 : 1),
                    Child = new TextBlock {
                        Text = $"{cell.CellNumber:X2}",
                        FontSize = 8,
                        Foreground = new SolidColorBrush(Color.Parse(
                            isSelected ? "#ffffff" : isNeighbor ? "#aaddff" : "#c0b0d8")),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        FontFamily = new FontFamily("Consolas"),
                    },
                    Tag = cell.CellNumber,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };

                Canvas.SetLeft(node, screenPos.X - nodeSize / 2);
                Canvas.SetTop(node, screenPos.Y - nodeSize / 2);

                node.PointerPressed += (s, e) => {
                    if (s is Border b && b.Tag is ushort cn) {
                        _viewModel?.SelectCellByNumberPublic(cn);
                        e.Handled = true;
                    }
                };

                canvas.Children.Add(node);
                _nodeVisuals[cell.CellNumber] = node;
            }
        }

        private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e) {
            if (e.GetCurrentPoint(GraphCanvas).Properties.IsMiddleButtonPressed ||
                e.GetCurrentPoint(GraphCanvas).Properties.IsRightButtonPressed) {
                _isPanning = true;
                _lastPanPoint = e.GetPosition(GraphCanvas);
                e.Handled = true;
            }
        }

        private void OnCanvasPointerMoved(object? sender, PointerEventArgs e) {
            if (_isPanning) {
                var pos = e.GetPosition(GraphCanvas);
                _panOffset += new Vector2((float)(pos.X - _lastPanPoint.X), (float)(pos.Y - _lastPanPoint.Y));
                _lastPanPoint = pos;
                RefreshCurrentGraph();
            }
        }

        private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e) {
            _isPanning = false;
        }

        private void OnCanvasWheel(object? sender, PointerWheelEventArgs e) {
            _zoom *= e.Delta.Y > 0 ? 1.15f : 0.87f;
            _zoom = Math.Clamp(_zoom, 0.2f, 5f);
            RefreshCurrentGraph();
        }

        private void RefreshCurrentGraph() {
            if (_viewModel == null) return;
            // Re-render with existing positions
            var doc = _viewModel.GetCurrentDocument();
            if (doc == null) return;
            var selectedNum = _viewModel.GetSelectedCellNumber();

            GraphCanvas.Children.Clear();
            _nodeVisuals.Clear();
            RenderGraph(doc, selectedNum);
        }
    }
}
