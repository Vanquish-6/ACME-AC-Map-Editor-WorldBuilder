using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class HeightSmoothSubToolViewModel : SubToolViewModelBase, IBrushSettings {
        public override string Name => "Smooth";
        public override string IconGlyph => "🔄";

        [ObservableProperty]
        private float _brushRadius = 5f;

        [ObservableProperty]
        private float _brushFalloff = 0.65f;

        [ObservableProperty]
        private float _strength = 0.5f;

        private bool _isPainting;
        private TerrainRaycast.TerrainRaycastHit _currentHitPosition;
        private TerrainRaycast.TerrainRaycastHit _lastHitPosition;
        private readonly CommandHistory _commandHistory;
        private readonly Dictionary<ushort, List<(int VertexIndex, byte OriginalValue, byte NewValue)>> _pendingChanges;

        public HeightSmoothSubToolViewModel(TerrainEditingContext context, CommandHistory commandHistory) : base(context) {
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
            _pendingChanges = new Dictionary<ushort, List<(int, byte, byte)>>();
        }

        partial void OnBrushRadiusChanged(float value) {
            if (value < 0.5f) BrushRadius = 0.5f;
            if (value > 200f) BrushRadius = 200f;
        }

        partial void OnStrengthChanged(float value) {
            if (value < 0.0f) Strength = 0.0f;
            if (value > 1.0f) Strength = 1.0f;
        }

        partial void OnBrushFalloffChanged(float value) {
            BrushFalloff = Math.Clamp(value, 0f, 1f);
            Context.BrushFalloff = BrushFalloff;
        }

        public override void OnActivated() {
            Context.ActiveVertices.Clear();
            Context.BrushActive = true;
            Context.BrushRadius = BrushRadius;
            Context.BrushFalloff = BrushFalloff;
            _lastHitPosition = _currentHitPosition = new TerrainRaycast.TerrainRaycastHit();
            _pendingChanges.Clear();
        }

        public override void OnDeactivated() {
            Context.BrushActive = false;
            Context.ActiveVertices.Clear();
            if (_isPainting) {
                FinalizePainting();
            }
        }

        public override void Update(double deltaTime) {
            Context.BrushCenter = new Vector2(_currentHitPosition.NearestVertice.X, _currentHitPosition.NearestVertice.Y);
            Context.BrushRadius = BrushRadius;
            Context.BrushFalloff = BrushFalloff;
            _lastHitPosition = _currentHitPosition;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
            if (_isPainting && !mouseState.LeftPressed) {
                _isPainting = false;
                FinalizePainting();
                return true;
            }
            return false;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            if (!mouseState.IsOverTerrain || !mouseState.TerrainHit.HasValue) return false;

            var hitResult = mouseState.TerrainHit.Value;
            _currentHitPosition = hitResult;

            if (_isPainting) {
                ApplyPreviewChanges(hitResult.NearestVertice);
                return true;
            }

            return false;
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.IsOverTerrain || !mouseState.TerrainHit.HasValue || !mouseState.LeftPressed) return false;
            if (Context.LayerLocked) return false;

            _isPainting = true;
            _pendingChanges.Clear();
            var hitResult = mouseState.TerrainHit.Value;
            ApplyPreviewChanges(hitResult.NearestVertice);

            return true;
        }

        private void ApplyPreviewChanges(Vector3 centerPosition) {
            var affected = PaintCommand.GetAffectedVertices(centerPosition, BrushRadius, Context);
            var landblockDataCache = new Dictionary<ushort, TerrainEntry[]>();

            // First pass: compute the average height of all affected vertices
            double heightSum = 0;
            int heightCount = 0;

            foreach (var (lbId, vIndex, _) in affected) {
                if (!landblockDataCache.TryGetValue(lbId, out var data)) {
                    data = Context.TerrainSystem.GetLandblockTerrain(lbId);
                    if (data == null) continue;
                    landblockDataCache[lbId] = data;
                }

                heightSum += data[vIndex].Height;
                heightCount++;
            }

            if (heightCount == 0) return;

            double avgHeight = heightSum / heightCount;

            // Second pass: blend each vertex toward the average
            var batchChanges = new Dictionary<ushort, Dictionary<byte, uint>>();

            foreach (var (lbId, vIndex, vertPos) in affected) {
                if (!landblockDataCache.TryGetValue(lbId, out var data)) continue;

                if (!_pendingChanges.TryGetValue(lbId, out var list)) {
                    list = new List<(int, byte, byte)>();
                    _pendingChanges[lbId] = list;
                }

                if (list.Any(c => c.VertexIndex == vIndex)) continue;

                float dist = Vector2.Distance(new Vector2(vertPos.X, vertPos.Y), new Vector2(centerPosition.X, centerPosition.Y));
                float worldR = global::WorldBuilder.Editors.Landscape.BrushFalloff.WorldRadius(BrushRadius);
                float weight = global::WorldBuilder.Editors.Landscape.BrushFalloff.Weight(dist, worldR, BrushFalloff);
                byte original = data[vIndex].Height;
                double blended = original + (avgHeight - original) * Strength * weight;
                byte newHeight = (byte)Math.Clamp((int)Math.Round(blended), 0, 255);

                if (original == newHeight) continue;

                list.Add((vIndex, original, newHeight));

                if (!batchChanges.TryGetValue(lbId, out var lbChanges)) {
                    lbChanges = new Dictionary<byte, uint>();
                    batchChanges[lbId] = lbChanges;
                }

                var newEntry = data[vIndex] with { Height = newHeight };
                lbChanges[(byte)vIndex] = newEntry.ToUInt();
            }

            if (batchChanges.Count > 0) {
                var modifiedLandblocks = Context.TerrainSystem.UpdateLandblocksBatch(TerrainField.Height, batchChanges);
                Context.MarkLandblocksModified(modifiedLandblocks);
            }
        }

        private void FinalizePainting() {
            if (_pendingChanges.Count == 0) return;

            var command = new HeightChangeCommand(Context, "Smooth terrain", _pendingChanges);
            _commandHistory.ExecuteCommand(command);

            _pendingChanges.Clear();
        }
    }
}
