using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon {
    /// <summary>
    /// Panel ViewModel for Instance Placements (world-database generators/items/portals in dungeons).
    /// Shows the list of placed instances and a manual-entry fallback.
    /// Weenie browsing/placement is in the Object Browser.
    /// </summary>
    public partial class InstancePlacementsPanelViewModel : ViewModelBase {
        private readonly DungeonEditorViewModel _editor;

        public ObservableCollection<InstancePlacementItemViewModel> InstancePlacementItems => _editor.InstancePlacementItems;
        public ObservableCollection<ushort> InstancePlacementCellNumbers => _editor.InstancePlacementCellNumbers;

        public string NewPlacementWcid {
            get => _editor.NewPlacementWcid;
            set => _editor.NewPlacementWcid = value;
        }
        public ushort? NewPlacementCellNumber {
            get => _editor.NewPlacementCellNumber;
            set => _editor.NewPlacementCellNumber = value;
        }
        public string NewPlacementX {
            get => _editor.NewPlacementX;
            set => _editor.NewPlacementX = value;
        }
        public string NewPlacementY {
            get => _editor.NewPlacementY;
            set => _editor.NewPlacementY = value;
        }
        public string NewPlacementZ {
            get => _editor.NewPlacementZ;
            set => _editor.NewPlacementZ = value;
        }

        public IRelayCommand AddInstancePlacementCommand => _editor.AddInstancePlacementCommand;
        public IRelayCommand AddPlacementAtSelectedRoomCommand => _editor.AddPlacementAtSelectedRoomCommand;
        public IRelayCommand RemoveInstancePlacementCommand => _editor.RemoveInstancePlacementCommand;

        public bool HasDungeon => _editor.HasDungeon;

        public InstancePlacementsPanelViewModel(DungeonEditorViewModel editor) {
            _editor = editor;
            _editor.PropertyChanged += (_, e) => {
                if (e.PropertyName is nameof(DungeonEditorViewModel.NewPlacementWcid) or nameof(DungeonEditorViewModel.NewPlacementCellNumber)
                    or nameof(DungeonEditorViewModel.NewPlacementX) or nameof(DungeonEditorViewModel.NewPlacementY) or nameof(DungeonEditorViewModel.NewPlacementZ))
                    OnPropertyChanged(e.PropertyName);
                if (e.PropertyName == nameof(DungeonEditorViewModel.HasDungeon))
                    OnPropertyChanged(nameof(HasDungeon));
            };
        }
    }
}
