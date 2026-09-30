using Avalonia.Controls;
#if DEBUG
using Avalonia;
using Avalonia.Diagnostics;
#endif

namespace WorldBuilder.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
    }
}
