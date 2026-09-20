using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using WorldBuilder.Lib;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon.Tools {

    /// <summary>
    /// Abstract base for dungeon editor sub-tools. Sub-tools are nested under
    /// a parent DungeonToolBase and receive the same lifecycle and input events.
    /// </summary>
    public abstract partial class DungeonSubToolBase : ViewModelBase {
        public abstract string Name { get; }
        public abstract string IconGlyph { get; }

        [ObservableProperty] private bool _isSelected;

        protected DungeonEditingContext Ctx { get; }

        protected DungeonSubToolBase(DungeonEditingContext ctx) => Ctx = ctx;

        public abstract void OnActivated();
        public abstract void OnDeactivated();

        public abstract bool HandleMouseDown(MouseState mouseState);
        public abstract bool HandleMouseUp(MouseState mouseState);
        public abstract bool HandleMouseMove(MouseState mouseState);

        public virtual bool HandleKeyDown(KeyEventArgs e) => false;
        public virtual void Update(double deltaTime) { }
    }
}
