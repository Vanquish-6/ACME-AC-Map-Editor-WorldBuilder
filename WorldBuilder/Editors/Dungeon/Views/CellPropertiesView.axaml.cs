using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class CellPropertiesView : UserControl {
        public CellPropertiesView() {
            InitializeComponent();
        }
        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
