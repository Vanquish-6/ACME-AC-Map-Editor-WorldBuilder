using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class WorldMapPanelViewModel : ViewModelBase {
        private readonly TerrainSystem _terrainSystem;
        private readonly LandSurfaceManager _surfaceManager;
        private readonly DispatcherTimer _updateTimer;

        private Dictionary<TerrainTextureType, (byte R, byte G, byte B)>? _terrainColors;

        [ObservableProperty] private WriteableBitmap? _mapBitmap;
        [ObservableProperty] private bool _isBuilding;
        [ObservableProperty] private double _zoom = 1.0;
        [ObservableProperty] private double _panX = 0.0;
        [ObservableProperty] private double _panY = 0.0;
        [ObservableProperty] private Vector3 _cameraPosition = Vector3.Zero;
        [ObservableProperty] private float _cameraYaw = 0f;
        [ObservableProperty] private HashSet<ushort>? _loadedLandblocks;

        public event Action? RenderInvalidated;

        private const int MapSize = 254;

        public WorldMapPanelViewModel(TerrainSystem terrainSystem, LandSurfaceManager surfaceManager) {
            _terrainSystem = terrainSystem ?? throw new ArgumentNullException(nameof(terrainSystem));
            _surfaceManager = surfaceManager ?? throw new ArgumentNullException(nameof(surfaceManager));

            _updateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, OnUpdateTick);
            _updateTimer.Start();

            _ = BuildMapBitmapAsync();
        }

        private void OnUpdateTick(object? sender, EventArgs e) {
            var camera = _terrainSystem.Scene.PerspectiveCamera;
            var newPos = camera.Position;
            var newYaw = camera.Yaw;
            var newLoaded = _terrainSystem.Scene.VisibleLandblocks;

            bool changed = newPos != CameraPosition || Math.Abs(newYaw - CameraYaw) > 0.01f || newLoaded != LoadedLandblocks;

            if (changed) {
                CameraPosition = newPos;
                CameraYaw = newYaw;
                LoadedLandblocks = newLoaded;
                RenderInvalidated?.Invoke();
            }
        }

        public async Task BuildMapBitmapAsync() {
            if (IsBuilding) return;
            IsBuilding = true;

            try {
                var colors = await Task.Run(() => _surfaceManager.GetTerrainAverageColors());
                _terrainColors = colors;

                var pixels = await Task.Run(() => BuildPixels(colors));

                var bitmap = new WriteableBitmap(
                    new PixelSize(MapSize, MapSize),
                    new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Rgba8888,
                    Avalonia.Platform.AlphaFormat.Opaque);

                using (var fb = bitmap.Lock()) {
                    Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
                }

                MapBitmap = bitmap;
            }
            catch (Exception ex) {
                Console.WriteLine($"[WorldMap] Failed to build map bitmap: {ex.Message}");
            }
            finally {
                IsBuilding = false;
            }
        }

        private byte[] BuildPixels(Dictionary<TerrainTextureType, (byte R, byte G, byte B)> colors) {
            var pixels = new byte[MapSize * MapSize * 4];

            for (int x = 1; x <= MapSize; x++) {
                for (int y = 1; y <= MapSize; y++) {
                    ushort key = (ushort)((x << 8) | y);
                    var entries = _terrainSystem.GetLandblockTerrain(key);

                    byte r, g, b;

                    if (entries != null && entries.Length > 0) {
                        // Tally dominant terrain type
                        var typeCounts = new Dictionary<byte, int>();
                        foreach (var entry in entries) {
                            typeCounts.TryGetValue(entry.Type, out var count);
                            typeCounts[entry.Type] = count + 1;
                        }

                        byte dominant = typeCounts.OrderByDescending(kv => kv.Value).First().Key;

                        if (colors.TryGetValue((TerrainTextureType)dominant, out var c)) {
                            r = c.R;
                            g = c.G;
                            b = c.B;
                        }
                        else {
                            r = 60; g = 60; b = 60;
                        }
                    }
                    else {
                        r = 20; g = 20; b = 30;
                    }

                    // Map: pixel (x-1, y-1) = landblock (x, y)
                    // In AC coords: lbX = (key >> 8) & 0xFF, lbY = key & 0xFF
                    // We store x along pixel-X (left=1, right=254) and y along pixel-Y (top=1, bottom=254)
                    int px = x - 1;
                    int py = MapSize - y; // flip Y so north is up
                    int offset = (py * MapSize + px) * 4;
                    pixels[offset + 0] = r;
                    pixels[offset + 1] = g;
                    pixels[offset + 2] = b;
                    pixels[offset + 3] = 255;
                }
            }

            return pixels;
        }

        /// <summary>
        /// Convert a screen-space point within the map control to landblock coordinates (1-254).
        /// </summary>
        public (float lbX, float lbY) ScreenToLandblock(double screenX, double screenY, double controlWidth, double controlHeight) {
            double cellSize = GetCellSize(controlWidth, controlHeight);
            double lbX = (screenX - PanX) / cellSize + 1.0;
            double lbY = MapSize - ((screenY - PanY) / cellSize) + 1.0; // flip Y back
            return ((float)lbX, (float)lbY);
        }

        /// <summary>
        /// Convert landblock coordinates to screen-space point within the map control.
        /// </summary>
        public (double sx, double sy) LandblockToScreen(float lbX, float lbY, double controlWidth, double controlHeight) {
            double cellSize = GetCellSize(controlWidth, controlHeight);
            double sx = (lbX - 1.0) * cellSize + PanX;
            double sy = (MapSize - (lbY - 1.0)) * cellSize + PanY; // flip Y
            return (sx, sy);
        }

        public double GetCellSize(double controlWidth, double controlHeight) {
            double baseSize = Math.Min(controlWidth, controlHeight);
            return baseSize / MapSize * Zoom;
        }

        public void HandleClick(double screenX, double screenY, double controlWidth, double controlHeight) {
            var (lbX, lbY) = ScreenToLandblock(screenX, screenY, controlWidth, controlHeight);

            lbX = Math.Clamp(lbX, 1f, MapSize);
            lbY = Math.Clamp(lbY, 1f, MapSize);

            float worldX = (lbX - 1f) * 192f + 96f;
            float worldY = (lbY - 1f) * 192f + 96f;
            float worldZ = CameraPosition.Z;

            var scene = _terrainSystem.Scene;
            var newPos = new Vector3(worldX, worldY, worldZ);
            scene.PerspectiveCamera.SetPosition(newPos);
            scene.TopDownCamera.LookAt(newPos);
        }

        public void HandleScroll(double delta, double originX, double originY, double controlWidth, double controlHeight) {
            double factor = delta > 0 ? 1.2 : 1.0 / 1.2;
            double newZoom = Math.Clamp(Zoom * factor, 0.5, 32.0);

            // Zoom toward origin point
            double ratio = newZoom / Zoom;
            PanX = originX - (originX - PanX) * ratio;
            PanY = originY - (originY - PanY) * ratio;
            Zoom = newZoom;

            RenderInvalidated?.Invoke();
        }

        public void HandleDrag(double deltaX, double deltaY) {
            PanX += deltaX;
            PanY += deltaY;
            RenderInvalidated?.Invoke();
        }

        public void CenterOnCamera(double controlWidth, double controlHeight) {
            double cellSize = GetCellSize(controlWidth, controlHeight);
            float lbX = CameraPosition.X / 192f;
            float lbY = CameraPosition.Y / 192f;

            PanX = controlWidth / 2.0 - (lbX) * cellSize;
            PanY = controlHeight / 2.0 - (MapSize - lbY) * cellSize;

            RenderInvalidated?.Invoke();
        }

        public void Dispose() {
            _updateTimer.Stop();
        }
    }
}
