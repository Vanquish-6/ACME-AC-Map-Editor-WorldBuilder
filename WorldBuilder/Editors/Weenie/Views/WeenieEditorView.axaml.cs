using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.Lib;
using System;

namespace WorldBuilder.Editors.Weenie.Views {
    public partial class WeenieEditorView : UserControl {
        public WeenieEditorView() {
            InitializeComponent();
            if (Design.IsDesignMode) return;

            var vm = ProjectManager.Instance.GetProjectService<WeenieEditorViewModel>()
                ?? throw new InvalidOperationException("WeenieEditorViewModel not registered.");
            DataContext = vm;
            if (ProjectManager.Instance.CurrentProject != null)
                vm.Init(ProjectManager.Instance.CurrentProject);
        }

        void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
