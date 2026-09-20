using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WorldBuilder.Editors.Dungeon.Tools.Views {
    public partial class DungeonToolboxView : UserControl {
        public DungeonToolboxView() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
