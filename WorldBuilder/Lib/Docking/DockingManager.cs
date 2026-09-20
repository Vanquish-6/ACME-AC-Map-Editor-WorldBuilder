using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace WorldBuilder.Lib.Docking {
    public partial class DockingManager : ObservableObject {
        private readonly ObservableCollection<IDockable> _allPanels = new();

        public ObservableCollection<IDockable> LeftPanels { get; } = new();
        public ObservableCollection<IDockable> RightPanels { get; } = new();
        public ObservableCollection<IDockable> TopPanels { get; } = new();
        public ObservableCollection<IDockable> BottomPanels { get; } = new();
        public ObservableCollection<IDockable> CenterPanels { get; } = new();
        public ObservableCollection<IDockable> FloatingPanels { get; } = new();
        public ObservableCollection<IDockable> ClosedPanels { get; } = new();

        [ObservableProperty]
        private bool _hasClosedPanels;

        [ObservableProperty]
        private Orientation _centerOrientation = Orientation.Horizontal;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLeftTabbed), nameof(IsLeftSectioned))]
        private DockRegionMode _leftMode = DockRegionMode.Tabbed;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsRightTabbed), nameof(IsRightSectioned))]
        private DockRegionMode _rightMode = DockRegionMode.Tabbed;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTopTabbed), nameof(IsTopSectioned))]
        private DockRegionMode _topMode = DockRegionMode.Tabbed;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBottomTabbed), nameof(IsBottomSectioned))]
        private DockRegionMode _bottomMode = DockRegionMode.Tabbed;

        public bool IsLeftTabbed => LeftMode == DockRegionMode.Tabbed;
        public bool IsLeftSectioned => LeftMode == DockRegionMode.Sections;
        public bool IsRightTabbed => RightMode == DockRegionMode.Tabbed;
        public bool IsRightSectioned => RightMode == DockRegionMode.Sections;
        public bool IsTopTabbed => TopMode == DockRegionMode.Tabbed;
        public bool IsTopSectioned => TopMode == DockRegionMode.Sections;
        public bool IsBottomTabbed => BottomMode == DockRegionMode.Tabbed;
        public bool IsBottomSectioned => BottomMode == DockRegionMode.Sections;

        public ObservableCollection<IDockable> AllPanels => _allPanels;

        public void Clear() {
            _allPanels.Clear();
            LeftPanels.Clear();
            RightPanels.Clear();
            TopPanels.Clear();
            BottomPanels.Clear();
            CenterPanels.Clear();
            FloatingPanels.Clear();
            ClosedPanels.Clear();
            HasClosedPanels = false;
        }

        public void RegisterPanel(IDockable panel) {
            if (_allPanels.Any(p => p.Id == panel.Id)) return;
            _allPanels.Add(panel);
            UpdatePanelLocation(panel);
        }

        public void UnregisterPanel(IDockable panel) {
            _allPanels.Remove(panel);
            RemoveFromCollections(panel);
        }

        public void UpdatePanelLocation(IDockable panel) {
            RemoveFromCollections(panel);

            if (!panel.IsVisible) {
                if (!ClosedPanels.Contains(panel))
                    ClosedPanels.Add(panel);
                HasClosedPanels = ClosedPanels.Count > 0;
                return;
            }

            ClosedPanels.Remove(panel);
            HasClosedPanels = ClosedPanels.Count > 0;

            switch (panel.Location) {
                case DockLocation.Left:
                    LeftPanels.Add(panel);
                    break;
                case DockLocation.Right:
                    RightPanels.Add(panel);
                    break;
                case DockLocation.Top:
                    TopPanels.Add(panel);
                    break;
                case DockLocation.Bottom:
                    BottomPanels.Add(panel);
                    break;
                case DockLocation.Center:
                    CenterPanels.Add(panel);
                    break;
                case DockLocation.Floating:
                    FloatingPanels.Add(panel);
                    break;
            }
        }

        private void RemoveFromCollections(IDockable panel) {
            LeftPanels.Remove(panel);
            RightPanels.Remove(panel);
            TopPanels.Remove(panel);
            BottomPanels.Remove(panel);
            CenterPanels.Remove(panel);
            FloatingPanels.Remove(panel);
        }

        [RelayCommand]
        public void TogglePanelVisibility(string panelId) {
            var panel = _allPanels.FirstOrDefault(p => p.Id == panelId);
            if (panel != null) {
                panel.IsVisible = !panel.IsVisible;
                // Property setter will call UpdatePanelLocation
            }
        }

        public void MovePanel(IDockable panel, DockLocation location) {
            panel.Location = location;
            UpdatePanelLocation(panel);
        }

        [RelayCommand]
        public void ToggleCenterOrientation() {
            CenterOrientation = CenterOrientation == Orientation.Horizontal
                ? Orientation.Vertical
                : Orientation.Horizontal;
        }

        [RelayCommand]
        public void MovePanelUp(IDockable panel) {
            var list = GetCollectionForLocation(panel.Location);
            int idx = list.IndexOf(panel);
            if (idx > 0) {
                list.Move(idx, idx - 1);
            }
        }

        [RelayCommand]
        public void MovePanelDown(IDockable panel) {
            var list = GetCollectionForLocation(panel.Location);
            int idx = list.IndexOf(panel);
            if (idx < list.Count - 1) {
                list.Move(idx, idx + 1);
            }
        }

        private ObservableCollection<IDockable> GetCollectionForLocation(DockLocation location) {
            return location switch {
                DockLocation.Left => LeftPanels,
                DockLocation.Right => RightPanels,
                DockLocation.Top => TopPanels,
                DockLocation.Bottom => BottomPanels,
                DockLocation.Center => CenterPanels,
                DockLocation.Floating => FloatingPanels,
                _ => new ObservableCollection<IDockable>()
            };
        }

        [RelayCommand]
        private void ToggleLeftMode() =>
            LeftMode = LeftMode == DockRegionMode.Tabbed ? DockRegionMode.Sections : DockRegionMode.Tabbed;

        [RelayCommand]
        private void ToggleRightMode() =>
            RightMode = RightMode == DockRegionMode.Tabbed ? DockRegionMode.Sections : DockRegionMode.Tabbed;

        [RelayCommand]
        private void ToggleTopMode() =>
            TopMode = TopMode == DockRegionMode.Tabbed ? DockRegionMode.Sections : DockRegionMode.Tabbed;

        [RelayCommand]
        private void ToggleBottomMode() =>
            BottomMode = BottomMode == DockRegionMode.Tabbed ? DockRegionMode.Sections : DockRegionMode.Tabbed;

        public DockRegionMode GetModeForLocation(DockLocation location) {
            return location switch {
                DockLocation.Left => LeftMode,
                DockLocation.Right => RightMode,
                DockLocation.Top => TopMode,
                DockLocation.Bottom => BottomMode,
                _ => DockRegionMode.Tabbed
            };
        }

        public void SetModeForLocation(DockLocation location, DockRegionMode mode) {
            switch (location) {
                case DockLocation.Left: LeftMode = mode; break;
                case DockLocation.Right: RightMode = mode; break;
                case DockLocation.Top: TopMode = mode; break;
                case DockLocation.Bottom: BottomMode = mode; break;
            }
        }
    }
}
