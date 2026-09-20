using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.Lib;
using System;

namespace WorldBuilder.Editors.SpellSet.Views {
    public partial class SpellSetEditorView : UserControl {
        private SpellSetEditorViewModel? _viewModel;

        public SpellSetEditorView() {
            InitializeComponent();

            if (Design.IsDesignMode) return;

            _viewModel = ProjectManager.Instance.GetProjectService<SpellSetEditorViewModel>()
                ?? throw new Exception("Failed to get SpellSetEditorViewModel");

            DataContext = _viewModel;

            if (ProjectManager.Instance.CurrentProject != null) {
                _viewModel.Init(ProjectManager.Instance.CurrentProject);
            }
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
