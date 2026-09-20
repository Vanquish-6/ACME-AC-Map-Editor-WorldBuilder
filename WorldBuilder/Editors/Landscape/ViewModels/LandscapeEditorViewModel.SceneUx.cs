using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class LandscapeEditorViewModel {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SaveButtonText))]
        private bool _hasUnsavedChanges;

        [ObservableProperty] private string _autosaveStatus = "";

        public string SaveButtonText => HasUnsavedChanges ? "Save*" : "Save";

        private DispatcherTimer? _autosaveTimer;
        private bool _sessionStarted;

        private void StartSceneUx() {
            if (_project != null) {
                if (EditorAutosaveService.HasUncleanShutdown(_project.ProjectDirectory)) {
                    AutosaveStatus = "Recovered from last autosave. Your last save is in the project.";
                }
                EditorAutosaveService.BeginSession(_project.ProjectDirectory);
                _sessionStarted = true;
            }

            if (TerrainSystem?.History != null)
                TerrainSystem.History.HistoryChanged += OnLandscapeHistoryChanged;

            _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _autosaveTimer.Tick += (_, _) => AutosaveTick();
            _autosaveTimer.Start();
        }

        private void StopSceneUx() {
            if (_autosaveTimer != null) {
                _autosaveTimer.Stop();
                _autosaveTimer = null;
            }
            if (TerrainSystem?.History != null)
                TerrainSystem.History.HistoryChanged -= OnLandscapeHistoryChanged;
            if (_sessionStarted && _project != null) {
                EditorAutosaveService.EndSession(_project.ProjectDirectory);
                _sessionStarted = false;
            }
        }

        private void OnLandscapeHistoryChanged(object? sender, EventArgs e) => HasUnsavedChanges = true;

        private void AutosaveTick() {
            if (!HasUnsavedChanges || TerrainSystem?.TerrainDoc == null || _project == null) return;
            TerrainSystem.TerrainDoc.ForceSave();
            EditorAutosaveService.Touch(_project.ProjectDirectory, "landscape");
            AutosaveStatus = $"Autosaved {DateTime.Now:HH:mm}";
        }

        [RelayCommand]
        private void SaveLandscape() {
            TerrainSystem?.TerrainDoc?.ForceSave();
            HasUnsavedChanges = false;
            AutosaveStatus = $"Saved {DateTime.Now:HH:mm}";
        }

        public void PlaceDroppedObject(uint id, bool isSetup, uint? wcid) {
            ObjectBrowser?.BeginPlacementFromDrop(id, isSetup, wcid);
        }

        public void FocusSelection() {
            var sel = TerrainSystem?.EditingContext.ObjectSelection;
            if (sel == null || TerrainSystem == null) return;
            Vector3 target;
            if (sel.HasEnvCellSelection && sel.SelectedEnvCell != null)
                target = sel.SelectedEnvCell.WorldPosition;
            else if (sel.SelectedEntries.Count > 0) {
                target = Vector3.Zero;
                foreach (var entry in sel.SelectedEntries)
                    target += entry.Object.Origin;
                target /= sel.SelectedEntries.Count;
            }
            else return;

            TerrainSystem.Scene.PerspectiveCamera.SetPosition(target + new Vector3(0, -50f, 30f));
            TerrainSystem.Scene.PerspectiveCamera.LookAt(target);
            TerrainSystem.Scene.TopDownCamera.SetPosition(target + new Vector3(0, 0, 80f));
        }

        public void DuplicateSelection() {
            CopySelectedObject();
            PasteObject();
        }

        private void AdjustActiveBrushRadius(float delta) {
            if (SelectedSubTool is not IBrushSettings brush) return;
            brush.BrushRadius = Math.Clamp(brush.BrushRadius + delta, 0.5f, 200f);
        }
    }
}
