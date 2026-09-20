using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.Lib;
using System;

namespace WorldBuilder.Editors.Skill.Views {
    public partial class SkillEditorView : UserControl {
        private SkillEditorViewModel? _viewModel;

        public SkillEditorView() {
            InitializeComponent();

            if (Design.IsDesignMode) return;

            _viewModel = ProjectManager.Instance.GetProjectService<SkillEditorViewModel>()
                ?? throw new Exception("Failed to get SkillEditorViewModel");

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
