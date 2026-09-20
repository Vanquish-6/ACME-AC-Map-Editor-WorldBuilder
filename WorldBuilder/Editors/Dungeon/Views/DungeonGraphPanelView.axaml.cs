using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class DungeonGraphPanelView : UserControl {
        private DungeonGraphPanelViewModel? _vm;
        private DungeonGraphView? _graphView;

        public DungeonGraphPanelView() {
            InitializeComponent();
            _graphView = this.FindControl<DungeonGraphView>("GraphView");
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnDataContextChanged(EventArgs e) {
            base.OnDataContextChanged(e);
            if (_vm != null) _vm.RefreshRequested -= OnRefresh;
            _vm = DataContext as DungeonGraphPanelViewModel;
            _graphView ??= this.FindControl<DungeonGraphView>("GraphView");
            if (_vm == null || _graphView == null) return;

            _graphView.DataContext = _vm.Editor;
            _vm.RefreshRequested += OnRefresh;

            // Defer the initial draw until layout has created/measured child controls.
            Dispatcher.UIThread.Post(() => {
                if (_vm == null || _graphView == null) return;
                _graphView.Refresh(_vm.Editor.GetCurrentDocument(), _vm.Editor.GetSelectedCellNumber());
            }, DispatcherPriority.Loaded);
        }

        private void OnRefresh(DungeonDocument? doc, ushort? selectedCell) {
            _graphView?.Refresh(doc, selectedCell);
        }
    }
}
