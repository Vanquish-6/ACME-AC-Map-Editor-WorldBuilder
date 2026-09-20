using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.Lib;
using System;

namespace WorldBuilder.Editors.Spell.Views {
    public partial class SpellEditorView : UserControl {
        private SpellEditorViewModel? _viewModel;

        public SpellEditorView() {
            InitializeComponent();

            if (Design.IsDesignMode) return;

            _viewModel = ProjectManager.Instance.GetProjectService<SpellEditorViewModel>()
                ?? throw new Exception("Failed to get SpellEditorViewModel");

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
