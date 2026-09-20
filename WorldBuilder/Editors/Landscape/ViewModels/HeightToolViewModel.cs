using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class HeightToolViewModel : ToolViewModelBase {
        public override string Name => "Height";
        public override string IconGlyph => "⛰️";
        public override string Description => "Raise, lower, flatten, or smooth terrain height.";

        [ObservableProperty]
        private ObservableCollection<SubToolViewModelBase> _subTools = new();

        public override ObservableCollection<SubToolViewModelBase> AllSubTools => SubTools;

        public HeightToolViewModel(
            HeightRaiseLowerSubToolViewModel raiseLowerSubTool,
            HeightSetSubToolViewModel setHeightSubTool,
            HeightSmoothSubToolViewModel smoothSubTool) {
            SubTools.Add(raiseLowerSubTool);
            SubTools.Add(setHeightSubTool);
            SubTools.Add(smoothSubTool);
        }

        public override void OnActivated() {
            SelectedSubTool?.OnActivated();
        }

        public override void OnDeactivated() {
            SelectedSubTool?.OnDeactivated();
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            return SelectedSubTool?.HandleMouseDown(mouseState) ?? false;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
            return SelectedSubTool?.HandleMouseUp(mouseState) ?? false;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            return SelectedSubTool?.HandleMouseMove(mouseState) ?? false;
        }

        public override void Update(double deltaTime) {
            SelectedSubTool?.Update(deltaTime);
        }
    }
}
