using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class DungeonHistoryPanelView : UserControl {
        public DungeonHistoryPanelView() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
