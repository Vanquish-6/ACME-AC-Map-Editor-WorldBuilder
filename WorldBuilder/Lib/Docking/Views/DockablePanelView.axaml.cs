using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace WorldBuilder.Lib.Docking {
    public partial class DockablePanelView : UserControl {
        public DockablePanelView() {
            InitializeComponent();

            var headerBorder = this.FindControl<Border>("HeaderBorder");
            if (headerBorder != null) {
                headerBorder.PointerPressed += Header_PointerPressed;
            }
        }

        private void Header_PointerPressed(object? sender, PointerPressedEventArgs e) {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window != null && window.SystemDecorations == SystemDecorations.BorderOnly) {
                    window.BeginMoveDrag(e);
                }
            }
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
