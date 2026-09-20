using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class DungeonObjectBrowserView : UserControl {
        private Point _pressPos;
        private ObjectBrowserItem? _pressItem;
        private bool _dragging;

        public DungeonObjectBrowserView() {
            InitializeComponent();
        }

        private void ObjectCard_PointerPressed(object? sender, PointerPressedEventArgs e) {
            if (sender is not Control c || c.DataContext is not ObjectBrowserItem item) return;
            if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
            _pressPos = e.GetPosition(this);
            _pressItem = item;
            _dragging = false;
        }

        private async void ObjectCard_PointerMoved(object? sender, PointerEventArgs e) {
            if (_pressItem == null || _dragging) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var delta = e.GetPosition(this) - _pressPos;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 8) return;
            _dragging = true;
            await DragDrop.DoDragDrop(e, EditorDragFormats.ForObject(_pressItem.Id, _pressItem.IsSetup, _pressItem.WeenieClassId), DragDropEffects.Copy);
            _pressItem = null;
            _dragging = false;
        }
    }
}
