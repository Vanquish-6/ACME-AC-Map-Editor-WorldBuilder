using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Messages;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib;
using static WorldBuilder.ViewModels.SplashPageViewModel;

namespace WorldBuilder.ViewModels;

public partial class CreateProjectViewModel : SplashPageViewModelBase, INotifyDataErrorInfo {
    private readonly Dictionary<string, List<string>> _errors = new();
    private readonly ILogger<CreateProjectViewModel> _log;
    private readonly WorldBuilderSettings _settings;
    private DatProjectMode? _detectedDatMode;

    [ObservableProperty]
    private string _baseDatDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectLocation))]
    private string _projectName = "New Project";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectLocation))]
    private string _location = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    private bool _canProceed;

    [ObservableProperty]
    private List<string> _baseDatDirectoryErrors = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBaseDatDirectorySummary))]
    private string _baseDatDirectorySummary = string.Empty;

    [ObservableProperty]
    private List<string> _projectNameErrors = new();

    [ObservableProperty]
    private List<string> _locationErrors = new();

    public string ProjectLocation => Path.Combine(Location, ProjectName);
    public bool HasBaseDatDirectorySummary => !string.IsNullOrWhiteSpace(BaseDatDirectorySummary);
    public bool IsLegacyBaseDatDirectory => _detectedDatMode == DatProjectMode.LegacyPreTod;

    public new event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public new bool HasErrors => _errors.Any();

    public new IEnumerable GetErrors(string? propertyName) {
        if (string.IsNullOrEmpty(propertyName))
            return _errors.Values.SelectMany(e => e);
        return _errors.TryGetValue(propertyName, out var errors) ? errors : Enumerable.Empty<string>();
    }

    public CreateProjectViewModel(WorldBuilderSettings settings, ILogger<CreateProjectViewModel> log) {
        _log = log;
        _settings = settings;
        _location = settings.App.ProjectsDirectory;
        ValidateLocation();
        UpdateCanProceed();
        PropertyChanged += (s, e) => {
            switch (e.PropertyName) {
                case nameof(BaseDatDirectory):
                    ValidateBaseDatDirectory();
                    UpdateCanProceed();
                    break;
                case nameof(ProjectName):
                    ValidateProjectName();
                    ValidateLocation();
                    UpdateCanProceed();
                    break;
                case nameof(Location):
                    ValidateLocation();
                    UpdateCanProceed();
                    break;
            }
        };
    }

    [RelayCommand]
    private async Task BrowseBaseDatDirectory() {
        var files = await TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions() {
            Title = "Choose Base DAT directory",
            AllowMultiple = false,
            SuggestedStartLocation = await TopLevel.StorageProvider.TryGetFolderFromPathAsync(_settings.App.ProjectsDirectory)
        });
        if (files.Count == 0) return;
        var localPath = files[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath)) {
            BaseDatDirectory = localPath;
        }
    }

    [RelayCommand]
    private async Task BrowseLocation() {
        var files = await TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions() {
            Title = "Choose project location",
            AllowMultiple = false,
            SuggestedStartLocation = await TopLevel.StorageProvider.TryGetFolderFromPathAsync(_settings.App.ProjectsDirectory)
        });
        if (files.Count == 0) return;
        var localPath = files[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath)) {
            Location = localPath;
        }
    }

    [RelayCommand]
    private void GoBack() {
        WeakReferenceMessenger.Default.Send(new SplashPageChangedMessage(SplashPage.ProjectSelection));
    }

    [RelayCommand(CanExecute = nameof(CanProceed))]
    private void GoNext() {
        WeakReferenceMessenger.Default.Send(new SplashPageChangedMessage(SplashPage.Loading));
        WeakReferenceMessenger.Default.Send(new StartProjectCreateMessage(ProjectName, ProjectLocation, BaseDatDirectory));
    }

    [RelayCommand(CanExecute = nameof(CanConvertLegacyDatsToRetail))]
    private async Task ConvertLegacyDatsToRetail() {
        if (!IsLegacyBaseDatDirectory) {
            return;
        }

        LegacyDatExportPolicy? exportPolicy = await LegacyDatExportPolicyPicker.PickExportPolicyAsync("CreateProjectDialogHost");
        if (exportPolicy == null) {
            return;
        }

        string? retailSeedDirectory = await PickFolderAsync(
            "Choose retail seed DAT directory",
            _settings.App.ProjectsDirectory);
        if (string.IsNullOrWhiteSpace(retailSeedDirectory)) {
            return;
        }

        string? outputDirectory = await PickFolderAsync(
            "Choose output folder for converted retail DATs",
            Location);
        if (string.IsNullOrWhiteSpace(outputDirectory)) {
            return;
        }

        string? serverWorldDataDirectory = await PickFolderAsync(
            "Optional extra world data folder — Cancel to skip",
            Location);
        if (!string.IsNullOrWhiteSpace(serverWorldDataDirectory)
            && !LegacyDatServerWorldCompletion.TryResolveSource(serverWorldDataDirectory, out _)) {
            await LegacyDatConversionDialogs.ShowMessageAsync(
                "CreateProjectDialogHost",
                "World data not detected",
                "That folder could not be used for world completion. Conversion will continue without it.");
            serverWorldDataDirectory = null;
        }

        try {
            var result = await LegacyDatConversionDialogs.RunWithProgressAsync(
                "CreateProjectDialogHost",
                "Converting Legacy DATs",
                onProgress => LegacyDatConversionService.Convert(
                    new LegacyToRetailConversionOptions {
                        LegacyDatDirectory = BaseDatDirectory,
                        RetailSeedDirectory = retailSeedDirectory,
                        OutputDirectory = outputDirectory,
                        ExportMode = exportPolicy.ToLegacyMode(),
                        ExportPolicy = exportPolicy,
                        ServerWorldDataDirectory = serverWorldDataDirectory,
                    },
                    onProgress));

            BaseDatDirectory = result.OutputDirectory;

            string summary = result.ChecksumReport.WarningCount == 0
                ? $"Legacy DATs were converted using {exportPolicy.GetDisplayName()}."
                : $"Legacy DATs were converted with {result.ChecksumReport.WarningCount} checksum warning(s). Review the manifest before continuing.";

            await LegacyDatConversionDialogs.ShowMessageAsync(
                "CreateProjectDialogHost",
                "Retail conversion complete",
                $"{summary}\n\nOutput folder:\n{result.OutputDirectory}\n\nManifest:\n{result.ManifestPath}\n\nThe project form now points at the converted retail DATs.");
        }
        catch (Exception ex) {
            await LegacyDatConversionDialogs.ShowMessageAsync(
                "CreateProjectDialogHost",
                "Retail conversion failed",
                ex.Message);
        }
    }

    private bool CanConvertLegacyDatsToRetail() => IsLegacyBaseDatDirectory;

    private void ValidateBaseDatDirectory() {
        var errors = new List<string>();
        BaseDatDirectorySummary = string.Empty;
        _detectedDatMode = null;

        if (string.IsNullOrWhiteSpace(BaseDatDirectory)) {
            errors.Add("Base DAT directory is required.");
        }
        else {
            if (!DatProjectModeInfo.TryDetectFromDirectory(BaseDatDirectory, out var detectedMode, out var summary, out var validationErrors)) {
                errors.AddRange(validationErrors);
                BaseDatDirectorySummary = summary;
            }
            else {
                _detectedDatMode = detectedMode;
                BaseDatDirectorySummary = summary;
            }
        }

        BaseDatDirectoryErrors = errors;
        SetErrors(nameof(BaseDatDirectory), errors);
        OnPropertyChanged(nameof(IsLegacyBaseDatDirectory));
        ConvertLegacyDatsToRetailCommand.NotifyCanExecuteChanged();
    }

    private void ValidateProjectName() {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ProjectName)) {
            errors.Add("Project name is required.");
        }
        else {
            var invalidChars = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).ToArray();
            if (ProjectName.IndexOfAny(invalidChars) >= 0)
                errors.Add("Project name contains invalid characters.");
            var reservedNames = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (reservedNames.Contains(ProjectName.ToUpperInvariant()))
                errors.Add("Project name cannot be a reserved system name.");
            if (ProjectName.EndsWith(".") || ProjectName.EndsWith(" "))
                errors.Add("Project name cannot end with a period or space.");
        }
        ProjectNameErrors = errors;
        SetErrors(nameof(ProjectName), errors);
    }

    private void ValidateLocation() {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Location)) {
            errors.Add("Location is required.");
        }
        else if (!string.IsNullOrWhiteSpace(ProjectName)) {
            var projectPath = Path.Combine(Location, ProjectName);
            if (Directory.Exists(projectPath))
                errors.Add($"A directory named '{ProjectName}' already exists in the specified location.");
        }
        LocationErrors = errors;
        SetErrors(nameof(Location), errors);
    }

    private void UpdateCanProceed() {
        CanProceed = !HasErrors &&
                     !string.IsNullOrWhiteSpace(BaseDatDirectory) &&
                     !string.IsNullOrWhiteSpace(ProjectName) &&
                     !string.IsNullOrWhiteSpace(Location);
    }

    private void SetErrors(string propertyName, List<string> errors) {
        if (errors.Any())
            _errors[propertyName] = errors;
        else
            _errors.Remove(propertyName);
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        OnPropertyChanged(nameof(HasErrors));
    }

    private async Task<string?> PickFolderAsync(string title, string startDirectory) {
        var suggested = Directory.Exists(startDirectory)
            ? await TopLevel.StorageProvider.TryGetFolderFromPathAsync(startDirectory)
            : await TopLevel.StorageProvider.TryGetFolderFromPathAsync(_settings.App.ProjectsDirectory);

        var folders = await TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = suggested,
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
