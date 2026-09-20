using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Lib;
using System;
using System.ComponentModel;

namespace WorldBuilder.Editors.CharGen.Views {
    public partial class CharGenEditorView : UserControl {
        private CharGenEditorViewModel? _viewModel;
        private HeritageModelPreview? _modelPreview;
        private HeritageDetailViewModel? _subscribedDetail;

        public CharGenEditorView() {
            InitializeComponent();

            if (Design.IsDesignMode) return;

            _viewModel = ProjectManager.Instance.GetProjectService<CharGenEditorViewModel>()
                ?? throw new Exception("Failed to get CharGenEditorViewModel");

            DataContext = _viewModel;

            if (ProjectManager.Instance.CurrentProject != null) {
                _viewModel.Init(ProjectManager.Instance.CurrentProject);
            }

            _modelPreview = this.FindControl<HeritageModelPreview>("ModelPreview");

            if (_modelPreview?.ViewModel != null) {
                _modelPreview.ViewModel.ApplyToSetupRequested += OnApplyToSetup;
                _modelPreview.ViewModel.ApplyToEnvSetupRequested += OnApplyToEnvSetup;
                _modelPreview.GlInitialized += OnPreviewGlInitialized;
            }

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            WireObjectBrowser();
            UpdateModelPreview();
        }

        private void OnPreviewGlInitialized() {
            _viewModel?.InitObjectBrowser(() => _modelPreview?.ViewModel?.ThumbnailService);
            WireObjectBrowser();
        }

        private void WireObjectBrowser() {
            if (_viewModel?.ObjectBrowser != null) {
                _viewModel.ObjectBrowser.PlacementRequested -= OnBrowserItemSelected;
                _viewModel.ObjectBrowser.PlacementRequested += OnBrowserItemSelected;
            }
        }

        private void OnBrowserItemSelected(object? sender, ObjectBrowserItem item) {
            _modelPreview?.ViewModel?.PreviewById(item.Id);
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(CharGenEditorViewModel.SelectedDetail)) {
                if (_subscribedDetail != null)
                    _subscribedDetail.PropertyChanged -= OnDetailPropertyChanged;
                _subscribedDetail = _viewModel?.SelectedDetail;
                if (_subscribedDetail != null)
                    _subscribedDetail.PropertyChanged += OnDetailPropertyChanged;
                UpdateModelPreview();
            }
            else if (e.PropertyName == nameof(CharGenEditorViewModel.ObjectBrowser)) {
                WireObjectBrowser();
            }
        }

        private void OnDetailPropertyChanged(object? sender, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(HeritageDetailViewModel.SetupId) ||
                e.PropertyName == nameof(HeritageDetailViewModel.EnvironmentSetupId)) {
                UpdateModelPreview();
            }
        }

        private void OnApplyToSetup(uint id) {
            if (_viewModel?.SelectedDetail != null)
                _viewModel.SelectedDetail.SetupId = id;
        }

        private void OnApplyToEnvSetup(uint id) {
            if (_viewModel?.SelectedDetail != null)
                _viewModel.SelectedDetail.EnvironmentSetupId = id;
        }

        private void UpdateModelPreview() {
            if (_modelPreview == null || _viewModel?.SelectedDetail == null) return;
            _modelPreview.SetModelIds(
                _viewModel.SelectedDetail.SetupId,
                _viewModel.SelectedDetail.EnvironmentSetupId);
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
