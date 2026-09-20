using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.Editors.ObjectDebug;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.ObjectDebug.Views;

public partial class ObjectDebugEditorView : UserControl {
    public ObjectDebugEditorView() {
        InitializeComponent();

        if (Design.IsDesignMode) return;

        var vm = ProjectManager.Instance.GetProjectService<ObjectDebugEditorViewModel>()
            ?? throw new System.Exception("Failed to get ObjectDebugEditorViewModel");
        DataContext = vm;
    }

    private void InitializeComponent() {
        AvaloniaXamlLoader.Load(this);
    }
}
