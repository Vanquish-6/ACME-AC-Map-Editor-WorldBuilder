using System;
using WorldBuilder.Shared.Documents;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon {
    public class DungeonGraphPanelViewModel : ViewModelBase {
        public DungeonEditorViewModel Editor { get; }

        public event Action<DungeonDocument?, ushort?>? RefreshRequested;

        public DungeonGraphPanelViewModel(DungeonEditorViewModel editor) {
            Editor = editor;
        }

        public void RequestRefresh(DungeonDocument? doc, ushort? selectedCell) {
            RefreshRequested?.Invoke(doc, selectedCell);
        }
    }
}
