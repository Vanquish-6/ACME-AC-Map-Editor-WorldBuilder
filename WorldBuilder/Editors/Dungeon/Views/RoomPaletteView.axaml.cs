using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using System;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class RoomPaletteView : UserControl {
        private Point _pressPos;
        private PrefabListEntry? _pressEntry;
        private bool _dragging;

        public RoomPaletteView() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        private void PrefabItem_PointerPressed(object? sender, PointerPressedEventArgs e) {
            if (sender is not Control c || c.DataContext is not PrefabListEntry entry) return;
            if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
            _pressPos = e.GetPosition(this);
            _pressEntry = entry;
            _dragging = false;
            e.Pointer.Capture(c);
        }

        private async void PrefabItem_PointerMoved(object? sender, PointerEventArgs e) {
            if (_pressEntry == null || _dragging) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var delta = e.GetPosition(this) - _pressPos;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 8) return;
            _dragging = true;
            var data = EditorDragFormats.ForPrefab(_pressEntry.Prefab.Signature);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
            _pressEntry = null;
            _dragging = false;
        }

        private void PrefabItem_PointerReleased(object? sender, PointerReleasedEventArgs e) {
            e.Pointer.Capture(null);
            if (_dragging) {
                _pressEntry = null;
                _dragging = false;
                return;
            }
            if (_pressEntry != null && DataContext is RoomPaletteViewModel vm) {
                if (vm.SelectedPrefab == _pressEntry)
                    vm.ReselectCurrentPrefab();
                else
                    vm.SelectedPrefab = _pressEntry;
            }
            _pressEntry = null;
        }

        private void PrefabItem_PointerEntered(object? sender, PointerEventArgs e) {
            if (sender is Control c && c.DataContext is PrefabListEntry entry &&
                DataContext is RoomPaletteViewModel vm) {
                vm.NotifyPrefabHover(entry.Prefab);
            }
        }

        private void PrefabItem_PointerExited(object? sender, PointerEventArgs e) {
            if (DataContext is RoomPaletteViewModel vm) {
                vm.NotifyPrefabHover(null);
            }
        }

        private void FavoriteStar_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
            PrefabListEntry? entry = null;
            if (sender is Control ctrl) {
                var dc = ctrl.DataContext;
                if (dc is PrefabListEntry ple) entry = ple;
                if (entry == null) {
                    var parent = ctrl.Parent;
                    while (parent != null && entry == null) {
                        if (parent.DataContext is PrefabListEntry parentPle) entry = parentPle;
                        parent = parent.Parent;
                    }
                }
            }

            if (entry != null && DataContext is RoomPaletteViewModel vm)
                vm.TogglePrefabFavorite(entry);
        }
    }
}
