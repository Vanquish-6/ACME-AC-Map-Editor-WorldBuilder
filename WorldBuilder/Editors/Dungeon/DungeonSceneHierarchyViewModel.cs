using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using WorldBuilder.Shared.Documents;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon {
    public partial class DungeonSceneNode : ViewModelBase {
        public enum NodeKind { Room, Object, Group }

        public NodeKind Kind { get; init; }
        public ushort CellNumber { get; init; }
        public int ObjectIndex { get; init; } = -1;
        public string GroupName { get; init; } = "";

        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _detail = "";
        [ObservableProperty] private bool _isHidden;
        [ObservableProperty] private bool _isLocked;
        [ObservableProperty] private bool _isSelected;

        public string KindLabel => Kind switch {
            NodeKind.Object => "Obj",
            NodeKind.Group => "Grp",
            _ => "Room"
        };

        public string VisibilityLabel => IsHidden ? "Hidden" : "Shown";
        public string LockLabel => IsLocked ? "Locked" : "Unlocked";
        public bool CanHide => Kind != NodeKind.Group;
        public bool CanLock => Kind == NodeKind.Room || Kind == NodeKind.Group;
    }

    public partial class DungeonSceneHierarchyViewModel : ViewModelBase {
        private readonly DungeonEditorViewModel _editor;

        [ObservableProperty] private ObservableCollection<DungeonSceneNode> _nodes = new();
        [ObservableProperty] private DungeonSceneNode? _selectedNode;
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private string _statusText = "Select a room or object, then Hide, Lock, or Frame.";

        public DungeonSceneHierarchyViewModel(DungeonEditorViewModel editor) {
            _editor = editor;
        }

        public void Refresh() {
            var selectedCell = _editor.GetSelectedCellNumber();
            var doc = _editor.GetCurrentDocument();
            var ctx = _editor.EditingContext;
            var query = SearchText?.Trim() ?? "";

            var list = new ObservableCollection<DungeonSceneNode>();
            if (doc != null) {
                foreach (var group in ctx.CellGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)) {
                    if (!Matches(query, group.Key, "group")) continue;
                    list.Add(new DungeonSceneNode {
                        Kind = DungeonSceneNode.NodeKind.Group,
                        GroupName = group.Key,
                        Name = group.Key,
                        Detail = $"{group.Value.Count} rooms",
                        IsLocked = group.Value.Any(ctx.IsCellLocked)
                    });
                }

                foreach (var cell in doc.Cells.OrderBy(c => c.CellNumber)) {
                    var name = _editor.GetFriendlyRoomName(cell) ?? $"Room 0x{cell.CellNumber:X4}";
                    if (!Matches(query, name, cell.CellNumber.ToString("X4"))) continue;
                    list.Add(new DungeonSceneNode {
                        Kind = DungeonSceneNode.NodeKind.Room,
                        CellNumber = cell.CellNumber,
                        Name = name,
                        Detail = $"0x{cell.CellNumber:X4}  ·  {cell.StaticObjects.Count} objects",
                        IsHidden = ctx.IsCellHidden(cell.CellNumber),
                        IsLocked = ctx.IsCellLocked(cell.CellNumber),
                        IsSelected = selectedCell == cell.CellNumber
                    });

                    if (cell.StaticObjects.Count == 0) continue;
                    for (int i = 0; i < cell.StaticObjects.Count; i++) {
                        var stab = cell.StaticObjects[i];
                        var objName = $"Object 0x{stab.Id:X8}";
                        if (!Matches(query, objName, name)) continue;
                        list.Add(new DungeonSceneNode {
                            Kind = DungeonSceneNode.NodeKind.Object,
                            CellNumber = cell.CellNumber,
                            ObjectIndex = i,
                            Name = "    " + objName,
                            Detail = name,
                            IsHidden = ctx.IsCellHidden(cell.CellNumber),
                            IsLocked = ctx.IsCellLocked(cell.CellNumber)
                        });
                    }
                }
            }

            Nodes = list;
            StatusText = doc == null
                ? "Open a dungeon to see rooms and objects."
                : $"{doc.Cells.Count} rooms";
        }

        private static bool Matches(string query, params string?[] parts) {
            if (string.IsNullOrWhiteSpace(query)) return true;
            foreach (var p in parts) {
                if (!string.IsNullOrEmpty(p) && p.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        partial void OnSearchTextChanged(string value) => Refresh();

        partial void OnSelectedNodeChanged(DungeonSceneNode? value) {
            if (value == null) return;
            _editor.SelectHierarchyNode(value);
        }

        [RelayCommand]
        private void FrameSelected() {
            _editor.FocusSelection();
        }

        [RelayCommand]
        private void ToggleHidden(DungeonSceneNode? node) {
            node ??= SelectedNode;
            if (node == null) return;
            _editor.ToggleCellHidden(node.CellNumber);
            Refresh();
        }

        [RelayCommand]
        private void ToggleLocked(DungeonSceneNode? node) {
            node ??= SelectedNode;
            if (node == null) return;
            if (node.Kind == DungeonSceneNode.NodeKind.Group)
                _editor.ToggleGroupLocked(node.GroupName);
            else
                _editor.ToggleCellLocked(node.CellNumber);
            Refresh();
        }

        [RelayCommand]
        private void GroupSelected() {
            _editor.GroupSelectedRooms();
            Refresh();
        }

        [RelayCommand]
        private void UngroupSelected() {
            if (SelectedNode?.Kind == DungeonSceneNode.NodeKind.Group)
                _editor.UngroupRooms(SelectedNode.GroupName);
            Refresh();
        }
    }
}
