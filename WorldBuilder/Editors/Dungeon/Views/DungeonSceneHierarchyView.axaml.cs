using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class DungeonSceneHierarchyView : UserControl {
        public DungeonSceneHierarchyView() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
