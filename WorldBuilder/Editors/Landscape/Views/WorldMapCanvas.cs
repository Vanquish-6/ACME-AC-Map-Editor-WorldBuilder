using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using WorldBuilder.Editors.Landscape.ViewModels;

namespace WorldBuilder.Editors.Landscape.Views {
    /// <summary>
    /// The inner drawable map canvas — renders the terrain bitmap, loaded landblock overlay, and camera marker.
    /// </summary>
    public class WorldMapCanvas : Control {
        private WorldMapPanelViewModel? _vm;

        private static readonly IBrush LoadedBrush = new SolidColorBrush(Color.FromArgb(60, 180, 140, 255));
        private static readonly IPen LoadedPen = new Pen(new SolidColorBrush(Color.FromArgb(100, 180, 140, 255)), 1);
        private static readonly IBrush CameraFill = new SolidColorBrush(Color.FromArgb(220, 255, 220, 80));
        private static readonly IPen CameraPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 255, 200, 0)), 1.5);
        private static readonly IBrush NoBitmapBrush = new SolidColorBrush(Color.FromArgb(60, 100, 80, 160));
        private static readonly IBrush GridBg = new SolidColorBrush(Color.FromArgb(255, 8, 6, 18));

        public void SetViewModel(WorldMapPanelViewModel vm) {
            if (_vm != null) _vm.RenderInvalidated -= OnRenderInvalidated;
            _vm = vm;
            if (_vm != null) _vm.RenderInvalidated += OnRenderInvalidated;
            InvalidateVisual();
        }

        private void OnRenderInvalidated() {
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx) {
            base.Render(ctx);

            var bounds = Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            ctx.DrawRectangle(GridBg, null, new Rect(0, 0, bounds.Width, bounds.Height));

            if (_vm == null) return;

            double cellSize = _vm.GetCellSize(bounds.Width, bounds.Height);
            double panX = _vm.PanX;
            double panY = _vm.PanY;
            int mapSize = 254;

            // 1. Draw terrain bitmap
            var bitmap = _vm.MapBitmap;
            if (bitmap != null) {
                double mapW = cellSize * mapSize;
                double mapH = cellSize * mapSize;
                var destRect = new Rect(panX, panY, mapW, mapH);
                ctx.DrawImage(bitmap, destRect);
            }
            else {
                // Placeholder while building
                double mapW = cellSize * mapSize;
                double mapH = cellSize * mapSize;
                ctx.DrawRectangle(NoBitmapBrush, null, new Rect(panX, panY, mapW, mapH));

                var buildingText = new FormattedText("Building map...",
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12, Brushes.Gray);
                ctx.DrawText(buildingText,
                    new Point(panX + mapW / 2 - buildingText.Width / 2, panY + mapH / 2 - buildingText.Height / 2));
            }

            // 2. Draw loaded landblock highlights
            var loaded = _vm.LoadedLandblocks;
            if (loaded != null && cellSize >= 1.0) {
                foreach (var lb in loaded) {
                    int lx = (lb >> 8) & 0xFF;
                    int ly = lb & 0xFF;

                    if (lx < 1 || lx > mapSize || ly < 1 || ly > mapSize) continue;

                    double sx = (lx - 1) * cellSize + panX;
                    double sy = (mapSize - ly) * cellSize + panY;
                    ctx.DrawRectangle(LoadedBrush, LoadedPen, new Rect(sx, sy, cellSize, cellSize));
                }
            }

            // 3. Draw camera position marker
            DrawCameraMarker(ctx, _vm.CameraPosition, _vm.CameraYaw, cellSize, panX, panY, mapSize, bounds);
        }

        private static void DrawCameraMarker(DrawingContext ctx, Vector3 cameraPos, float yaw,
            double cellSize, double panX, double panY, int mapSize, Rect bounds) {
            float lbX = cameraPos.X / 192f;
            float lbY = cameraPos.Y / 192f;

            double sx = lbX * cellSize + panX;
            double sy = (mapSize - lbY) * cellSize + panY;

            // Clamp to visible area with margin
            const double margin = 8;
            bool clamped = sx < margin || sx > bounds.Width - margin || sy < margin || sy > bounds.Height - margin;
            sx = Math.Clamp(sx, margin, bounds.Width - margin);
            sy = Math.Clamp(sy, margin, bounds.Height - margin);

            double arrowSize = clamped ? 8.0 : 6.0;

            // Yaw is in degrees (CCW from +X). Convert to radians for trig.
            // Screen Y inversion is handled by the (sy - sin) pattern below.
            double angle = yaw * Math.PI / 180.0;

            double tipX = sx + Math.Cos(angle) * arrowSize;
            double tipY = sy - Math.Sin(angle) * arrowSize;
            double leftX = sx + Math.Cos(angle + Math.PI * 0.75) * arrowSize * 0.6;
            double leftY = sy - Math.Sin(angle + Math.PI * 0.75) * arrowSize * 0.6;
            double rightX = sx + Math.Cos(angle - Math.PI * 0.75) * arrowSize * 0.6;
            double rightY = sy - Math.Sin(angle - Math.PI * 0.75) * arrowSize * 0.6;

            var geometry = new StreamGeometry();
            using (var gc = geometry.Open()) {
                gc.BeginFigure(new Point(tipX, tipY), true);
                gc.LineTo(new Point(leftX, leftY));
                gc.LineTo(new Point(rightX, rightY));
                gc.EndFigure(true);
            }

            ctx.DrawGeometry(CameraFill, CameraPen, geometry);

            // Small dot at exact position
            ctx.DrawEllipse(CameraFill, null, new Point(sx, sy), 2, 2);
        }
    }
}
