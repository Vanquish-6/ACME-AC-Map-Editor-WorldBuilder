using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace WorldBuilder.Lib.Settings {

    public partial class DungeonEditorSettings : ObservableObject {
        private DungeonUIState _uiState = new();
        public DungeonUIState UIState { get => _uiState; set => SetProperty(ref _uiState, value); }
    }

    public partial class DungeonUIState : ObservableObject {
        public double LeftPanelWidth { get; set; } = 280;
        public double RightPanelWidth { get; set; } = 220;
        public List<DockingPanelState> DockingLayout { get; set; } = new();
        public string LeftDockMode { get; set; } = "Tabbed";
        public string RightDockMode { get; set; } = "Tabbed";
        public string TopDockMode { get; set; } = "Tabbed";
        public string BottomDockMode { get; set; } = "Tabbed";
    }
}
