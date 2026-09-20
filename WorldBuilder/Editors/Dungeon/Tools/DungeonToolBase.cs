using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WorldBuilder.Lib;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon.Tools {

    /// <summary>
    /// Abstract base for dungeon editor tools. Mirrors the landscape editor's
    /// ToolViewModelBase with the same lifecycle and input handling contract.
    /// </summary>
    public abstract partial class DungeonToolBase : ViewModelBase {
        public abstract string Name { get; }
        public abstract string IconGlyph { get; }
        public virtual string Description => Name;

        [ObservableProperty] private bool _isSelected;
        [ObservableProperty] private string _statusText = "";

        public abstract ObservableCollection<DungeonSubToolBase> AllSubTools { get; }
        public bool HasSubTools => AllSubTools.Count > 0;

        [ObservableProperty] private DungeonSubToolBase? _selectedSubTool;

        public abstract void OnActivated();
        public abstract void OnDeactivated();

        public abstract bool HandleMouseDown(MouseState mouseState, DungeonEditingContext ctx);
        public abstract bool HandleMouseUp(MouseState mouseState, DungeonEditingContext ctx);
        public abstract bool HandleMouseMove(MouseState mouseState, DungeonEditingContext ctx);

        public virtual bool HandleKeyDown(KeyEventArgs e, DungeonEditingContext ctx) => false;

        public virtual void Update(double deltaTime, DungeonEditingContext ctx) { }

        [RelayCommand]
        public virtual void ActivateSubTool(DungeonSubToolBase subTool) {
            if (SelectedSubTool != null) {
                SelectedSubTool.IsSelected = false;
                SelectedSubTool.OnDeactivated();
            }
            SelectedSubTool = subTool;
            SelectedSubTool.IsSelected = true;
            SelectedSubTool.OnActivated();
        }
    }
}
