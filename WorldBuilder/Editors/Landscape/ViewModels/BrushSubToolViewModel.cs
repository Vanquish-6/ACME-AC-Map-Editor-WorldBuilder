using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
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
    public partial class BrushSubToolViewModel : SubToolViewModelBase, IBrushSettings {
        public override string Name => "Brush";
        public override string IconGlyph => "🖌️";

        [ObservableProperty]
        private float _brushRadius = 5f;

        [ObservableProperty]
        private float _brushFalloff = 0.65f;

        [ObservableProperty]
        private TerrainTextureType _selectedTerrainType;

        [ObservableProperty]
        private List<TerrainTextureType> _availableTerrainTypes;

        private bool _isPainting;
        private TerrainRaycast.TerrainRaycastHit _currentHitPosition;
        private TerrainRaycast.TerrainRaycastHit _lastHitPosition;
        private readonly CommandHistory _commandHistory;
        private readonly Dictionary<ushort, List<(int VertexIndex, byte OriginalType, byte NewType)>> _pendingChanges;

        public BrushSubToolViewModel(TerrainEditingContext context, CommandHistory commandHistory) : base(context) {
            _availableTerrainTypes = context.TerrainSystem.Scene.SurfaceManager.GetAvailableTerrainTextures()
                .Select(t => t.TerrainType).ToList();
            _selectedTerrainType = _availableTerrainTypes.First();
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
            _pendingChanges = new Dictionary<ushort, List<(int, byte, byte)>>();
        }

        [RelayCommand]
        private void SelectTerrainType(TerrainTextureType type) {
            SelectedTerrainType = type;
        }

        partial void OnBrushRadiusChanged(float value) {
            if (value < 0.5f) BrushRadius = 0.5f;
            if (value > 200f) BrushRadius = 200f;
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
            UpdatePreviewTextureIndex();
            _lastHitPosition = _currentHitPosition = new TerrainRaycast.TerrainRaycastHit();
            _pendingChanges.Clear();
        }

        public override void OnDeactivated() {
            Context.BrushActive = false;
            Context.PreviewTextureAtlasIndex = -1;
            Context.ActiveVertices.Clear();
            if (_isPainting) {
                FinalizePainting();
            }
        }

        partial void OnSelectedTerrainTypeChanged(TerrainTextureType value) {
            UpdatePreviewTextureIndex();
        }

        private void UpdatePreviewTextureIndex() {
            Context.PreviewTextureAtlasIndex = Context.TerrainSystem.Scene.SurfaceManager
                .GetAtlasIndexForTerrainType(SelectedTerrainType);
        }

        public override void Update(double deltaTime) {
            Context.BrushCenter = new Vector2(_currentHitPosition.NearestVertice.X, _currentHitPosition.NearestVertice.Y);
            Context.BrushRadius = BrushRadius;
            Context.BrushFalloff = BrushFalloff;

            // Disable texture preview while actively painting (real changes are visible)
            if (_isPainting) {
                Context.PreviewTextureAtlasIndex = -1;
            }
            else {
                UpdatePreviewTextureIndex();
            }

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

            // Collect all changes to be applied
            var batchChanges = new Dictionary<ushort, Dictionary<byte, uint>>();
            byte newType = (byte)SelectedTerrainType;

            float worldR = global::WorldBuilder.Editors.Landscape.BrushFalloff.WorldRadius(BrushRadius);
            foreach (var (lbId, vIndex, vertPos) in affected) {
                if (!landblockDataCache.TryGetValue(lbId, out var data)) {
                    data = Context.TerrainSystem.GetLandblockTerrain(lbId);
                    if (data == null) continue;
                    landblockDataCache[lbId] = data;
                }

                if (!_pendingChanges.TryGetValue(lbId, out var list)) {
                    list = new List<(int, byte, byte)>();
                    _pendingChanges[lbId] = list;
                }

                if (list.Any(c => c.VertexIndex == vIndex)) continue;

                float dist = Vector2.Distance(new Vector2(vertPos.X, vertPos.Y), new Vector2(centerPosition.X, centerPosition.Y));
                float weight = global::WorldBuilder.Editors.Landscape.BrushFalloff.Weight(dist, worldR, BrushFalloff);
                if (weight < 0.5f) continue;

                byte original = data[vIndex].Type;
                if (original == newType) continue;

                list.Add((vIndex, original, newType));

                // Prepare batch change
                if (!batchChanges.TryGetValue(lbId, out var lbChanges)) {
                    lbChanges = new Dictionary<byte, uint>();
                    batchChanges[lbId] = lbChanges;
                }

                var newEntry = data[vIndex] with { Type = newType };
                lbChanges[(byte)vIndex] = newEntry.ToUInt();
            }

            // Apply all changes in a single batch operation
            if (batchChanges.Count > 0) {
                var modifiedLandblocks = Context.TerrainSystem.UpdateLandblocksBatch(TerrainField.Type, batchChanges);
                Context.MarkLandblocksModified(modifiedLandblocks);
            }
        }

        private void FinalizePainting() {
            if (_pendingChanges.Count == 0) return;

            var command = new PaintCommand(Context, SelectedTerrainType, _pendingChanges);
            _commandHistory.ExecuteCommand(command);

            _pendingChanges.Clear();
        }
    }
}