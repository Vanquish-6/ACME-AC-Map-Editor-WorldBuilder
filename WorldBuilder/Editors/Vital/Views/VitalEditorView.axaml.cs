using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.Lib;
using System;

namespace WorldBuilder.Editors.Vital.Views {
    public partial class VitalEditorView : UserControl {
        private VitalEditorViewModel? _viewModel;

        public VitalEditorView() {
            InitializeComponent();

            if (Design.IsDesignMode) return;

            _viewModel = ProjectManager.Instance.GetProjectService<VitalEditorViewModel>()
                ?? throw new Exception("Failed to get VitalEditorViewModel");

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
