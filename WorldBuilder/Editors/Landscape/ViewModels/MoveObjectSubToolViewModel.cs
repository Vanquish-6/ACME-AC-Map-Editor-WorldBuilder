using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class MoveObjectSubToolViewModel : SubToolViewModelBase {
        public override string Name => "Move";
        public override string IconGlyph => "✥";

        private bool _isDragging;
        private Vector3 _dragStartPosition;
        private readonly CommandHistory _commandHistory;
        private readonly SnapSettings _snapSettings;

        // Multi-move tracking
        private List<(ushort LbKey, int Index, Vector3 OriginalPos)> _dragEntries = new();

        public MoveObjectSubToolViewModel(TerrainEditingContext context, CommandHistory commandHistory) : base(context) {
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
            _snapSettings = context.TerrainSystem.Settings.Landscape.Snap;
        }

        private Vector3 ApplyGridSnap(Vector3 position) {
            if (!_snapSettings.SnapToGrid || _snapSettings.GridSize <= 0) return position;
            float g = _snapSettings.GridSize;
            position.X = MathF.Round(position.X / g) * g;
            position.Y = MathF.Round(position.Y / g) * g;
            return position;
        }

        public override void OnActivated() { }
        public override void OnDeactivated() {
            if (_isDragging) {
                FinalizeDrag();
            }
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.LeftPressed) return false;

            var sel = Context.ObjectSelection;

            // Click to select
            if (mouseState.ObjectHit.HasValue && mouseState.ObjectHit.Value.Hit) {
                var hit = mouseState.ObjectHit.Value;

                // Check if clicked object is already in the current selection
                bool isInSelection = sel.SelectedEntries.Any(e =>
                    e.LandblockKey == hit.LandblockKey && e.ObjectIndex == hit.ObjectIndex);

                if (!isInSelection) {
                    // Ctrl+Click = add to selection; plain click = replace selection
                    if (mouseState.CtrlPressed) {
                        sel.ToggleSelectFromHit(hit);
                    }
                    else {
                        sel.SelectFromHit(hit);
                    }
                    return true;
                }

                // Clicking an already-selected object: start dragging all selected
                if (sel.HasSelection) {
                    var nonScenery = sel.SelectedEntries.Where(e => !e.IsScenery && e.ObjectIndex >= 0).ToList();
                    if (nonScenery.Count == 0) return false;

                    _isDragging = true;
                    _dragStartPosition = mouseState.TerrainHit?.HitPosition ?? hit.HitPosition;
                    _dragEntries = nonScenery.Select(e => (e.LandblockKey, e.ObjectIndex, e.Object.Origin)).ToList();
                    return true;
                }
            }
            else {
                sel.Deselect();
            }

            return false;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
            if (_isDragging) {
                FinalizeDrag();
                return true;
            }
            return false;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            if (!_isDragging || _dragEntries.Count == 0) return false;
            if (!mouseState.IsOverTerrain || !mouseState.TerrainHit.HasValue) return false;

            var currentTerrainPos = mouseState.TerrainHit.Value.HitPosition;
            var delta = currentTerrainPos - _dragStartPosition;

            foreach (var (lbKey, index, originalPos) in _dragEntries) {
                var newPosition = originalPos + delta;
                newPosition = ApplyGridSnap(newPosition);

                float terrainZ = Context.GetHeightAtPosition(newPosition.X, newPosition.Y);
                float originalOffset = originalPos.Z - Context.GetHeightAtPosition(originalPos.X, originalPos.Y);
                newPosition.Z = _snapSettings.SnapToTerrain ? terrainZ : terrainZ + originalOffset;

                var docId = $"landblock_{lbKey:X4}";
                var doc = Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                if (doc != null && index < doc.StaticObjectCount) {
                    var obj = doc.GetStaticObject(index);
                    var updated = new StaticObject {
                        Id = obj.Id,
                        IsSetup = obj.IsSetup,
                        Origin = newPosition,
                        Orientation = obj.Orientation,
                        Scale = obj.Scale
                    };
                    doc.UpdateStaticObject(index, updated);
                }
            }

            // Refresh selection state and invalidate rendering
            Context.ObjectSelection.RefreshAllFromDocuments(docId =>
                Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult());
            Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();

            return true;
        }

        private void FinalizeDrag() {
            _isDragging = false;

            if (_dragEntries.Count == 0) return;

            // Build commands for each moved object
            var commands = new List<ICommand>();
            foreach (var (lbKey, index, originalPos) in _dragEntries) {
                var docId = $"landblock_{lbKey:X4}";
                var doc = Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
                if (doc == null || index >= doc.StaticObjectCount) continue;

                var currentObj = doc.GetStaticObject(index);
                var newPosition = currentObj.Origin;

                if (Vector3.Distance(newPosition, originalPos) < 0.01f) continue;

                commands.Add(new MoveObjectCommand(Context, lbKey, index, originalPos, newPosition));
            }

            if (commands.Count > 0) {
                if (commands.Count == 1) {
                    _commandHistory.ExecuteCommand(commands[0]);
                }
                else {
                    var compound = new CompoundCommand($"Move {commands.Count} objects", commands);
                    _commandHistory.ExecuteCommand(compound);
                }
            }

            _dragEntries.Clear();
        }
    }
}
