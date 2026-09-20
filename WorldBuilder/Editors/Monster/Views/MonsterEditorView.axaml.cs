using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Monster.Views {
    public partial class MonsterEditorView : UserControl {
        public MonsterEditorView() {
            InitializeComponent();
            if (Design.IsDesignMode) return;

            var vm = ProjectManager.Instance.GetProjectService<MonsterEditorViewModel>()
                ?? throw new InvalidOperationException("MonsterEditorViewModel not registered.");
            DataContext = vm;
            if (ProjectManager.Instance.CurrentProject != null)
                vm.Init(ProjectManager.Instance.CurrentProject);
        }

        void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
