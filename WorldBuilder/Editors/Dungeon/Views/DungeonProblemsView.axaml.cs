using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class DungeonProblemsView : UserControl {
        public DungeonProblemsView() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
