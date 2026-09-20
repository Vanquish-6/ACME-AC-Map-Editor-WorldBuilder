using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using WorldBuilder.Editors.Dungeon.Tools;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public partial class DungeonEditorViewModel {
        [ObservableProperty] private string _selectedItemName = "";
        [ObservableProperty] private string _selectedItemKind = "";
        [ObservableProperty] private Bitmap? _selectedItemPreview;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowValidPlacementBanner))]
        [NotifyPropertyChangedFor(nameof(ShowBlockedPlacementBanner))]
        private bool _placementIsValid = true;

        [ObservableProperty] private string _placementBlockReason = "";
        [ObservableProperty] private string _connectingDoorwayText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SaveButtonText))]
        private bool _hasUnsavedChanges;

        [ObservableProperty] private string _autosaveStatus = "";

        public string SaveButtonText => HasUnsavedChanges ? "Save*" : "Save";
        public bool ShowValidPlacementBanner => ShowPlacementBanner && PlacementIsValid;
        public bool ShowBlockedPlacementBanner => ShowPlacementBanner && !PlacementIsValid;

        public DungeonSceneHierarchyViewModel? SceneHierarchy { get; private set; }
        public DungeonProblemsViewModel? ProblemsPanel { get; private set; }

        private DispatcherTimer? _autosaveTimer;
        private bool _sessionStarted;

        private void StartSceneUx() {
            SceneHierarchy = new DungeonSceneHierarchyViewModel(this);
            ProblemsPanel = new DungeonProblemsViewModel(this);
            CommandHistory.Changed += OnCommandHistoryChanged;
            OnPropertyChanged(nameof(SceneHierarchy));
            OnPropertyChanged(nameof(ProblemsPanel));

            if (_project != null) {
                if (EditorAutosaveService.HasUncleanShutdown(_project.ProjectDirectory)) {
                    AutosaveStatus = "Recovered from last autosave. Your last save is in the project.";
                    StatusText = AutosaveStatus;
                }
                EditorAutosaveService.BeginSession(_project.ProjectDirectory);
                _sessionStarted = true;
            }

            _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _autosaveTimer.Tick += (_, _) => AutosaveTick();
            _autosaveTimer.Start();
        }

        private void StopSceneUx() {
            if (_autosaveTimer != null) {
                _autosaveTimer.Stop();
                _autosaveTimer = null;
            }
            CommandHistory.Changed -= OnCommandHistoryChanged;
            if (_sessionStarted && _project != null) {
                EditorAutosaveService.EndSession(_project.ProjectDirectory);
                _sessionStarted = false;
            }
        }

        private void OnCommandHistoryChanged(object? sender, EventArgs e) {
            HasUnsavedChanges = true;
            SceneHierarchy?.Refresh();
            ProblemsPanel?.Refresh();
        }

        private void AutosaveTick() {
            if (!HasUnsavedChanges || _document == null || _project == null) return;
            _document.ForceSave();
            EditorAutosaveService.Touch(_project.ProjectDirectory, $"dungeon_{_loadedLandblockKey:X4}");
            AutosaveStatus = $"Autosaved {DateTime.Now:HH:mm}";
        }

        internal void RefreshInspectorHeader() {
            if (HasSelectedObject && Selection.HasSelectedObject && _document != null) {
                SelectedItemKind = "Object";
                var cell = _document.GetCell(Selection.SelectedObjCellNum);
                var idx = Selection.SelectedObjIndex;
                var stab = (cell != null && idx >= 0 && idx < cell.StaticObjects.Count)
                    ? cell.StaticObjects[idx]
                    : null;
                SelectedItemName = stab != null ? $"Object 0x{stab.Id:X8}" : "Object";
                SelectedItemPreview = ObjectBrowser?.FindThumbnail(stab?.Id ?? 0);
                return;
            }

            if (HasSelectedCell && Selection.SelectedCell != null) {
                var cellNum = (ushort)(Selection.SelectedCell.CellId & 0xFFFF);
                var dc = _document?.GetCell(cellNum);
                SelectedItemKind = SelectedCellCount > 1 ? $"{SelectedCellCount} rooms" : "Room";
                SelectedItemName = dc != null
                    ? (GetFriendlyRoomName(dc) ?? $"Room 0x{cellNum:X4}")
                    : $"Room 0x{cellNum:X4}";
                SelectedItemPreview = dc != null
                    ? RoomPalette?.FindThumbnailForRoom(dc.EnvironmentId, dc.CellStructure)
                    : null;
                return;
            }

            SelectedItemKind = "";
            SelectedItemName = "";
            SelectedItemPreview = null;
        }

        internal void SyncPlacementFromTool() {
            if (SelectedTool is not RoomPlacementTool tool) return;
            bool valid = !tool.HasPendingPiece || tool.PreviewIsValid;
            var block = tool.PlacementBlockReason ?? "";
            var connecting = tool.ConnectingDoorwayText ?? "";
            var status = tool.FitHint;
            bool changed = PlacementIsValid != valid
                || PlacementBlockReason != block
                || ConnectingDoorwayText != connecting
                || PlacementStatusText != status;
            PlacementIsValid = valid;
            PlacementBlockReason = block;
            ConnectingDoorwayText = connecting;
            PlacementStatusText = status;
            if (!changed) return;
            OnPropertyChanged(nameof(ShowValidPlacementBanner));
            OnPropertyChanged(nameof(ShowBlockedPlacementBanner));
            OnPropertyChanged(nameof(CurrentToolHint));
            OnPropertyChanged(nameof(ShowFitControls));
        }

        internal void ApplyHiddenFlags() {
            if (_scene == null) return;
            foreach (var cell in _scene.GetLoadedCells())
                cell.IsHidden = EditingContext.IsCellHidden((ushort)(cell.CellId & 0xFFFF));
        }

        public string? GetFriendlyRoomName(DungeonCellData cell) {
            var name = RoomPalette?.GetRoomDisplayName(cell.EnvironmentId, cell.CellStructure)
                       ?? RoomPalette?.GetRoomDisplayName((uint)(cell.EnvironmentId | 0x0D000000), cell.CellStructure);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        public void SelectHierarchyNode(DungeonSceneNode node) {
            if (_scene == null || _document == null) return;
            if (node.Kind == DungeonSceneNode.NodeKind.Group) {
                if (!EditingContext.CellGroups.TryGetValue(node.GroupName, out var nums) || nums.Count == 0)
                    return;
                bool first = true;
                foreach (var n in nums) {
                    var loaded = FindLoadedCell(n);
                    if (loaded == null) continue;
                    if (first) {
                        Selection.SelectCell(loaded);
                        first = false;
                    }
                    else {
                        Selection.ToggleCellInSelection(loaded);
                    }
                }
                return;
            }

            if (node.Kind == DungeonSceneNode.NodeKind.Object) {
                var dc = _document.GetCell(node.CellNumber);
                if (dc == null || node.ObjectIndex < 0 || node.ObjectIndex >= dc.StaticObjects.Count) return;
                Selection.SelectObject(node.CellNumber, node.ObjectIndex, dc.StaticObjects[node.ObjectIndex]);
                return;
            }

            SelectCellByNumber(node.CellNumber);
        }

        public void FocusSelection() {
            if (_scene == null || IsPlayerPreview) return;
            if (HasSelectedCell || HasSelectedObject)
                _scene.FocusCameraOnSelection();
            else
                _needsCameraFocus = true;
        }

        public void ToggleCellHidden(ushort cellNum) {
            if (!EditingContext.HiddenCells.Add(cellNum))
                EditingContext.HiddenCells.Remove(cellNum);
            ApplyHiddenFlags();
            SceneHierarchy?.Refresh();
        }

        public void ToggleCellLocked(ushort cellNum) {
            if (!EditingContext.LockedCells.Add(cellNum))
                EditingContext.LockedCells.Remove(cellNum);
            SceneHierarchy?.Refresh();
            StatusText = EditingContext.IsCellLocked(cellNum)
                ? $"Room 0x{cellNum:X4} is locked — moves and deletes are blocked."
                : $"Room 0x{cellNum:X4} unlocked.";
        }

        public void ToggleGroupLocked(string groupName) {
            if (!EditingContext.CellGroups.TryGetValue(groupName, out var nums)) return;
            bool anyUnlocked = nums.Any(n => !EditingContext.IsCellLocked(n));
            foreach (var n in nums) {
                if (anyUnlocked) EditingContext.LockedCells.Add(n);
                else EditingContext.LockedCells.Remove(n);
            }
            SceneHierarchy?.Refresh();
        }

        public void GroupSelectedRooms() {
            if (Selection.SelectedCells.Count < 2) {
                StatusText = "Select at least two rooms to group them.";
                return;
            }
            var name = $"Group {EditingContext.CellGroups.Count + 1}";
            EditingContext.CellGroups[name] = Selection.SelectedCells
                .Select(c => (ushort)(c.CellId & 0xFFFF))
                .ToHashSet();
            StatusText = $"Grouped {Selection.SelectedCells.Count} rooms as {name}.";
            SceneHierarchy?.Refresh();
        }

        public void UngroupRooms(string groupName) {
            EditingContext.CellGroups.Remove(groupName);
            SceneHierarchy?.Refresh();
        }

        public void GoToProblem(DungeonDocument.ValidationResult result) {
            if (result.CellNumber is ushort cellNum) {
                SelectCellByNumber(cellNum);
                if (result.PortalPolygonId is ushort poly) {
                    var roomTool = Tools.OfType<RoomPlacementTool>().FirstOrDefault();
                    roomTool?.FocusPortal(EditingContext, cellNum, poly);
                    if (_scene != null) {
                        _scene.HighlightedPortalCellNum = cellNum;
                        _scene.HighlightedPortalPolyId = poly;
                    }
                }
                _targetCellId = cellNum;
                if (!IsPlayerPreview)
                    _scene?.FocusCameraOnCell(_loadedLandblockKey, cellNum);
            }
            StatusText = result.Explanation ?? result.Message;
        }

        public void DuplicateSelection() {
            SyncContextBeforeInput();
            WorldBuilder.Editors.Dungeon.Tools.SelectTool.DuplicateCurrent(EditingContext);
            RefreshRendering();
        }

        public void PlaceDroppedPrefab(string signature) {
            var entry = RoomPalette?.FindPrefabEntry(signature);
            if (entry == null) {
                StatusText = "That piece is not in the catalog.";
                return;
            }
            OnPrefabSelected(this, entry.Prefab);
        }

        public void PlaceDroppedObject(uint id, bool isSetup, uint? wcid = null) {
            if (!wcid.HasValue && (id & 0xFF000000) == 0x01000000) {
                StatusText = "GfxObj meshes can't be placed in dungeons — pick a Setup.";
                return;
            }
            var item = wcid.HasValue
                ? new Landscape.ViewModels.ObjectBrowserItem(id, wcid.Value, $"WCID {wcid.Value}")
                : new Landscape.ViewModels.ObjectBrowserItem(id, isSetup, null);
            OnObjectPlacementRequested(this, item);
        }

        [RelayCommand]
        private void ConfirmPlacement() {
            SyncContextBeforeInput();
            var tool = Tools.OfType<RoomPlacementTool>().FirstOrDefault();
            if (tool == null) return;
            if (!tool.ConfirmLastPreview(EditingContext))
                StatusText = string.IsNullOrEmpty(tool.PlacementBlockReason)
                    ? "Cannot place that piece here."
                    : tool.PlacementBlockReason;
            SyncPlacementFromTool();
        }

        internal async System.Threading.Tasks.Task<bool> ConfirmDiscardIfDirty(string action) {
            if (!HasUnsavedChanges) return true;
            return await Dialogs.ShowConfirmDiscard(
                "Unsaved changes",
                $"You have unsaved dungeon edits. Discard them and {action}?");
        }

        private LoadedEnvCell? FindLoadedCell(ushort cellNum) {
            if (_scene?.EnvCellManager == null || _document == null) return null;
            var cells = _scene.EnvCellManager.GetLoadedCellsForLandblock(_document.LandblockKey);
            return cells?.FirstOrDefault(c => (c.CellId & 0xFFFF) == cellNum);
        }
    }
}
