using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon.Tools {

    /// <summary>
    /// ViewModel for the dungeon toolbox panel. Wraps the editor VM's tool state
    /// and forwards selection commands. Matches landscape's ToolboxViewModel pattern.
    /// </summary>
    public partial class DungeonToolboxViewModel : ViewModelBase {
        private readonly DungeonEditorViewModel _editor;

        public DungeonEditorViewModel Editor => _editor;
        public ObservableCollection<DungeonToolBase> Tools => _editor.Tools;
        public DungeonToolBase? SelectedTool => _editor.SelectedTool;
        public DungeonSubToolBase? SelectedSubTool => _editor.SelectedSubTool;
        public string CurrentToolHint => _editor.CurrentToolHint;
        public bool ShowEmptyHint => !_editor.HasOptionsSelection;

        public IRelayCommand SelectToolCommand => _editor.SelectToolCommand;
        public IRelayCommand SelectSubToolCommand => _editor.SelectSubToolCommand;

        public DungeonToolboxViewModel(DungeonEditorViewModel editor) {
            _editor = editor;
            _editor.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(DungeonEditorViewModel.SelectedTool)
                    || e.PropertyName == nameof(DungeonEditorViewModel.SelectedSubTool)
                    || e.PropertyName == nameof(DungeonEditorViewModel.CurrentToolHint)) {
                    OnPropertyChanged(nameof(SelectedTool));
                    OnPropertyChanged(nameof(SelectedSubTool));
                    OnPropertyChanged(nameof(CurrentToolHint));
                }
                if (e.PropertyName == nameof(DungeonEditorViewModel.HasOptionsSelection)
                    || e.PropertyName == nameof(DungeonEditorViewModel.HasSelectedCell)
                    || e.PropertyName == nameof(DungeonEditorViewModel.HasSelectedObject)) {
                    OnPropertyChanged(nameof(ShowEmptyHint));
                }
            };
        }
    }
}
