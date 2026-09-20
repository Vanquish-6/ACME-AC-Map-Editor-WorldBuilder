using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DialogHostAvalonia;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WorldBuilder.Editors.Dungeon;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Editors.CharGen;
using WorldBuilder.Editors.Experience;
using WorldBuilder.Editors.Skill;
using WorldBuilder.Editors.Spell;
using WorldBuilder.Editors.SpellSet;
using WorldBuilder.Editors.Vital;
using WorldBuilder.Editors.Layout;
using WorldBuilder.Editors.Monster;
using WorldBuilder.Editors.Weenie;
using WorldBuilder.Editors.ObjectDebug;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Docking;
using WorldBuilder.Lib.Input;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Views;

namespace WorldBuilder.ViewModels;

public partial class MainViewModel : ViewModelBase {
    private readonly WorldBuilderSettings _settings;
    private readonly InputManager _inputManager;

    private bool _settingsOpen;

    [ObservableProperty]
    private object? _activeEditor;

    [ObservableProperty]
    private EditorNavItem[] _sceneEditors = Array.Empty<EditorNavItem>();

    [ObservableProperty]
    private EditorNavItem[] _contentEditors = Array.Empty<EditorNavItem>();

    [ObservableProperty]
    private EditorNavItem[] _inspectEditors = Array.Empty<EditorNavItem>();

    [ObservableProperty]
    private bool _hideGettingStartedNextTime;

    private bool _gettingStartedQueued;

    partial void OnActiveEditorChanged(object? value) {
        OnPropertyChanged(nameof(DockingPanels));
        OnPropertyChanged(nameof(IsLandscapeEditorActive));
        OnPropertyChanged(nameof(ActiveEditorTitle));
        OnPropertyChanged(nameof(ActiveEditorHint));
        RebuildEditorTabs();
    }

    /// <summary>True when the active editor is the landscape/terrain editor (for showing Landscape menu).</summary>
    public bool IsLandscapeEditorActive => ActiveEditor is LandscapeEditorViewModel;
    public bool IsReadOnlyDatProject => ProjectManager.Instance?.CurrentProject?.IsReadOnlyDatProject == true;
    public bool CanExportDats => ProjectManager.Instance?.CurrentProject != null;
    public bool CanPerformWholeWorldLandscapeMutation => !IsReadOnlyDatProject;
    public string ProjectName => ProjectManager.Instance?.CurrentProject?.Name ?? "No project";
    public string ActiveEditorTitle => ActiveEditor switch {
        LandscapeEditorViewModel => "World",
        DungeonEditorViewModel => "Dungeon",
        SpellEditorViewModel => "Spells",
        SpellSetEditorViewModel => "Spell Sets",
        SkillEditorViewModel => "Skills",
        ExperienceEditorViewModel => "XP Table",
        VitalEditorViewModel => "Vitals",
        CharGenEditorViewModel => "Character Creation",
        WeenieEditorViewModel => "Weenies",
        MonsterEditorViewModel => "Monsters",
        LayoutEditorViewModel => "UI Layout",
        ObjectDebugEditorViewModel => "Object Inspector",
        _ => "ACME"
    };

    public string ActiveEditorHint => ActiveEditor switch {
        LandscapeEditorViewModel => "Fly the map, pick a tool, then paint terrain or place objects. Undo is always safe.",
        DungeonEditorViewModel => "Open or create a dungeon, click a doorway, then click a room piece on the left.",
        SpellEditorViewModel => "Search, pick a spell, edit it, then Save. Copy an existing spell to make a variant.",
        SpellSetEditorViewModel => "Equipment sets grant spells by tier. Pick a set, then add or remove tiers.",
        SkillEditorViewModel => "Skills players train. Pick one on the left, change costs or description, then Save.",
        ExperienceEditorViewModel => "XP required per level. Edit a row, or use Auto-Scale to build a curve.",
        VitalEditorViewModel => "How Health, Stamina, and Mana are calculated from attributes.",
        CharGenEditorViewModel => "Heritage groups and starting towns for new characters.",
        WeenieEditorViewModel => "Weenies are game objects — items, NPCs, portals. Search, then edit properties.",
        MonsterEditorViewModel => "Create or override creatures. Search a weenie, then tweak appearance and save to the DB.",
        LayoutEditorViewModel => "Inspect client UI layout files. This viewer does not change gameplay.",
        ObjectDebugEditorViewModel => "Inspect Setup and GfxObj meshes. Use this when a model looks wrong.",
        _ => "Choose World or Dungeon to build scenes, or Content to edit game data."
    };
    public string ProjectModeText => IsReadOnlyDatProject
        ? "Legacy pre-ToD DATs loaded. View-only mode is active."
        : "Retail DAT project loaded.";

    public KeyGesture? ExitGesture => _inputManager.GetKeyGesture(InputActions.AppExit);
    public KeyGesture? GotoLandblockGesture => _inputManager.GetKeyGesture(InputActions.NavigationGoToLandblock);

    private static readonly ObservableCollection<IDockable> _emptyPanels = new();

    public ObservableCollection<IDockable> DockingPanels {
        get {
            var dm = GetActiveDockingManager();
            return dm?.AllPanels ?? _emptyPanels;
        }
    }

    private DockingManager? GetActiveDockingManager() {
        return ActiveEditor switch {
            DungeonEditorViewModel de => de.DockingManager,
            LandscapeEditorViewModel le => le.DockingManager,
            _ => null,
        };
    }

    public MainViewModel() {
        _settings = new WorldBuilderSettings();
        _inputManager = new InputManager(_settings);
    }

    public MainViewModel(WorldBuilderSettings settings) {
        _settings = settings;
        _inputManager = new InputManager(_settings);
        ActiveEditor = ProjectManager.Instance?.GetProjectService<LandscapeEditorViewModel>();
        RebuildEditorTabs();
        QueueGettingStartedIfNeeded();
        if (ProjectManager.Instance != null) {
            ProjectManager.Instance.CurrentProjectChanged += (_, _) => {
                OnPropertyChanged(nameof(IsReadOnlyDatProject));
                OnPropertyChanged(nameof(ProjectModeText));
                OnPropertyChanged(nameof(ProjectName));
                OnPropertyChanged(nameof(CanExportDats));
                OnPropertyChanged(nameof(CanPerformWholeWorldLandscapeMutation));
                ConvertLegacyDatsToRetailCommand.NotifyCanExecuteChanged();
                RebuildEditorTabs();
            };
        }
    }

    private void QueueGettingStartedIfNeeded() {
        if (_gettingStartedQueued || !_settings.App.ShowGettingStarted) return;
        _gettingStartedQueued = true;
        Dispatcher.UIThread.Post(() => {
            if (_settings.App.ShowGettingStarted)
                _ = OpenGettingStarted();
        }, DispatcherPriority.Background);
    }

    private void RebuildEditorTabs() {
        SceneEditors = new EditorNavItem[] {
            Tab("landscape", "World", "Paint the outdoor world, place objects, and fly the map.", ActiveEditor is LandscapeEditorViewModel),
            Tab("dungeon", "Dungeon", "Open, generate, and edit indoor landblocks.", ActiveEditor is DungeonEditorViewModel),
        };
        ContentEditors = new EditorNavItem[] {
            Tab("spells", "Spells", "Edit spell table entries and components.", ActiveEditor is SpellEditorViewModel),
            Tab("weenies", "Weenies", "Search and edit weenies (items, NPCs, portals).", ActiveEditor is WeenieEditorViewModel),
            Tab("monsters", "Monsters", "Create and override creature weenies.", ActiveEditor is MonsterEditorViewModel),
            Tab("skills", "Skills", "Edit the skill table.", ActiveEditor is SkillEditorViewModel),
            Tab("spellsets", "Spell Sets", "Edit spells granted by equipment sets.", ActiveEditor is SpellSetEditorViewModel),
            Tab("chargen", "Character", "Edit character creation heritages and starting towns.", ActiveEditor is CharGenEditorViewModel),
            Tab("experience", "XP Table", "Edit XP requirements by level.", ActiveEditor is ExperienceEditorViewModel),
            Tab("vitals", "Vitals", "Edit how Health, Stamina, and Mana are calculated.", ActiveEditor is VitalEditorViewModel),
        };
        InspectEditors = new EditorNavItem[] {
            Tab("layout", "UI Layout", "Inspect client UI layout files.", ActiveEditor is LayoutEditorViewModel),
            Tab("debug", "Objects", "Inspect Setup and GfxObj meshes.", ActiveEditor is ObjectDebugEditorViewModel),
        };
    }

    private EditorNavItem Tab(string id, string title, string description, bool selected) =>
        new() {
            Id = id,
            Title = title,
            Description = description,
            IsSelected = selected,
            IsVisible = true,
            SelectCommand = SelectEditorCommand,
        };

    [RelayCommand]
    private void SelectEditor(string? id) {
        switch (id) {
            case "landscape": SwitchToLandscapeEditor(); break;
            case "dungeon": SwitchToDungeonEditor(); break;
            case "spells": SwitchToSpellEditor(); break;
            case "spellsets": SwitchToSpellSetEditor(); break;
            case "skills": SwitchToSkillEditor(); break;
            case "experience": SwitchToExperienceEditor(); break;
            case "vitals": SwitchToVitalEditor(); break;
            case "chargen": SwitchToCharGenEditor(); break;
            case "weenies": SwitchToWeenieEditor(); break;
            case "monsters": SwitchToMonsterEditor(); break;
            case "layout": SwitchToLayoutEditor(); break;
            case "debug": SwitchToObjectDebugEditor(); break;
        }
    }

    [RelayCommand]
    private void TogglePanelVisibility(object? parameter) {
        if (parameter is string id) {
            GetActiveDockingManager()?.TogglePanelVisibility(id);
        }
    }

    public KeyGesture? UndoGesture => _inputManager.GetKeyGesture(InputActions.EditUndo);
    public KeyGesture? RedoGesture => _inputManager.GetKeyGesture(InputActions.EditRedo);
    public KeyGesture? CopyGesture => _inputManager.GetKeyGesture(InputActions.EditCopy);
    public KeyGesture? PasteGesture => _inputManager.GetKeyGesture(InputActions.EditPaste);
    public KeyGesture? DeleteGesture => _inputManager.GetKeyGesture(InputActions.EditDelete);
    public KeyGesture? DuplicateGesture => _inputManager.GetKeyGesture(InputActions.EditDuplicate);
    public KeyGesture? FocusSelectionGesture => _inputManager.GetKeyGesture(InputActions.EditFocusSelection);

    private LandscapeEditorViewModel? GetLandscapeEditor() =>
        ProjectManager.Instance.GetProjectService<LandscapeEditorViewModel>();

    [RelayCommand]
    private void Undo() {
        if (IsReadOnlyDatProject) return;
        if (ActiveEditor is DungeonEditorViewModel de) { de.UndoCommand.Execute(null); return; }
        GetLandscapeEditor()?.UndoCommand.Execute(null);
    }

    [RelayCommand]
    private void Redo() {
        if (IsReadOnlyDatProject) return;
        if (ActiveEditor is DungeonEditorViewModel de) { de.RedoCommand.Execute(null); return; }
        GetLandscapeEditor()?.RedoCommand.Execute(null);
    }

    [RelayCommand]
    private void Copy() {
        if (IsReadOnlyDatProject) return;
        if (ActiveEditor is DungeonEditorViewModel de) { de.CopySelectedCells(); return; }
        GetLandscapeEditor()?.CopySelectedObjectCommand.Execute(null);
    }

    [RelayCommand]
    private void Paste() {
        if (IsReadOnlyDatProject) return;
        if (ActiveEditor is DungeonEditorViewModel de) { de.PasteCells(); return; }
        GetLandscapeEditor()?.PasteObjectCommand.Execute(null);
    }

    [RelayCommand]
    private void Duplicate() {
        if (IsReadOnlyDatProject) return;
        if (ActiveEditor is DungeonEditorViewModel de) { de.DuplicateSelection(); return; }
        GetLandscapeEditor()?.DuplicateSelection();
    }

    [RelayCommand]
    private void FocusSelection() {
        if (ActiveEditor is DungeonEditorViewModel de) { de.FocusSelection(); return; }
        GetLandscapeEditor()?.FocusSelection();
    }

    [RelayCommand]
    private void Delete() {
        if (IsReadOnlyDatProject) return;
        if (ActiveEditor is DungeonEditorViewModel de) {
            if (de.HasSelectedObject) de.DeleteSelectedObjectCommand.Execute(null);
            else de.DeleteSelectedCellCommand.Execute(null);
            return;
        }
        GetLandscapeEditor()?.DeleteSelectedObjectCommand.Execute(null);
    }

    [RelayCommand]
    private void Exit() {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.Shutdown();
        }
    }

    [RelayCommand]
    private void OpenSettingsWindow() {
        if (_settingsOpen) return;

        var settingsWindow = new SettingsWindow {
            DataContext = _settings
        };

        settingsWindow.Closed += (s, e) => {
            _settingsOpen = false;
        };

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            settingsWindow.Show();
            _settingsOpen = true;
        }
        else {
            throw new Exception("Unable to open settings window");
        }
    }

    [RelayCommand]
    private async Task GotoLandblock() {
        var landscapeEditor = ProjectManager.Instance.GetProjectService<LandscapeEditorViewModel>();
        if (landscapeEditor != null) {
            await landscapeEditor.GotoLandblockCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task OpenExportDatsWindow() {
        if (IsReadOnlyDatProject) {
            await ShowLegacyModeDialogAsync(
                "Export unavailable",
                "Legacy pre-ToD projects are opened in read-only mode. Export and DAT mutation are only available for retail DAT projects.");
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            if (desktop.MainWindow == null) throw new Exception("Unable to open export DATs window, main window is null.");

            var project = ProjectManager.Instance.CurrentProject
                ?? throw new Exception("No project open, cannot export DATs.");
            var repoService = ProjectManager.Instance.GetProjectService<InstanceRepositionService>()
                ?? new InstanceRepositionService();

            var exportWindow = new ExportDatsWindow();
            exportWindow.DataContext = new ExportDatsWindowViewModel(_settings, project, exportWindow, repoService);

            await exportWindow.ShowDialog(desktop.MainWindow);
        }
        else {
            throw new Exception("Unable to open settings window");
        }
    }

    private static string DescribePolicy(LegacyDatExportPolicy policy) =>
        policy.GetDisplayName();

    [RelayCommand(CanExecute = nameof(CanConvertLegacyDatsToRetail))]
    private async Task ConvertLegacyDatsToRetail() {
        var project = ProjectManager.Instance?.CurrentProject;
        if (project == null || !project.IsReadOnlyDatProject) {
            return;
        }

        LegacyDatExportPolicy? exportPolicy = await LegacyDatExportPolicyPicker.PickExportPolicyAsync();
        if (exportPolicy == null) {
            return;
        }

        string? retailSeedDirectory = await PickFolderAsync(
            "Choose retail seed DAT directory",
            project.ProjectDirectory);
        if (string.IsNullOrWhiteSpace(retailSeedDirectory)) {
            return;
        }

        string? outputDirectory = await PickFolderAsync(
            "Choose output folder for converted retail DATs",
            project.ProjectDirectory);
        if (string.IsNullOrWhiteSpace(outputDirectory)) {
            return;
        }

        string? serverWorldDataDirectory = await PickFolderAsync(
            "Optional extra world data folder — Cancel to skip",
            project.ProjectDirectory);
        if (!string.IsNullOrWhiteSpace(serverWorldDataDirectory)
            && !LegacyDatServerWorldCompletion.TryResolveSource(serverWorldDataDirectory, out _)) {
            await LegacyDatConversionDialogs.ShowMessageAsync(
                "MainDialogHost",
                "World data not detected",
                "That folder could not be used for world completion. Conversion will continue without it.");
            serverWorldDataDirectory = null;
        }

        try {
            var result = await LegacyDatConversionDialogs.RunWithProgressAsync(
                "MainDialogHost",
                "Converting Legacy DATs",
                onProgress => LegacyDatConversionService.Convert(
                    new LegacyToRetailConversionOptions {
                        LegacyDatDirectory = project.BaseDatDirectory,
                        RetailSeedDirectory = retailSeedDirectory,
                        OutputDirectory = outputDirectory,
                        ExportMode = exportPolicy.ToLegacyMode(),
                        ExportPolicy = exportPolicy,
                        ServerWorldDataDirectory = serverWorldDataDirectory,
                    },
                    onProgress));

            string summary = result.ChecksumReport.WarningCount == 0
                ? $"The legacy source DATs were converted using {DescribePolicy(exportPolicy)}."
                : $"The conversion completed with {result.ChecksumReport.WarningCount} checksum warning(s). Review the manifest before using the retail output.";

            await LegacyDatConversionDialogs.ShowMessageAsync(
                "MainDialogHost",
                "Retail conversion complete",
                $"{summary}\n\nOutput folder:\n{result.OutputDirectory}\n\nManifest:\n{result.ManifestPath}\n\nNext step: create a new retail project using that converted DAT folder as the base DAT directory.");
        }
        catch (Exception ex) {
            await LegacyDatConversionDialogs.ShowMessageAsync(
                "MainDialogHost",
                "Retail conversion failed",
                ex.Message);
        }
    }

    private bool CanConvertLegacyDatsToRetail() => IsReadOnlyDatProject;

    [RelayCommand]
    private async Task RepairExportedClientDats() {
        string? exportDirectory = await PickFolderAsync(
            "Choose folder with client_*.dat to repair",
            ProjectManager.Instance?.CurrentProject?.ProjectDirectory);
        if (string.IsNullOrWhiteSpace(exportDirectory)) {
            return;
        }

        string? retailSeedDirectory = await PickFolderAsync(
            "Choose retail seed DAT directory (ac-updates)",
            exportDirectory);
        if (string.IsNullOrWhiteSpace(retailSeedDirectory)) {
            return;
        }

        // Prefer the policy recorded by the conversion that produced this export; fall back to Full DM.
        LegacyDatExportPolicy policy = LegacyDatExportManifest.TryLoadPolicyFromJson(exportDirectory, out var manifestPolicy)
            ? manifestPolicy
            : LegacyDatExportManifest.TryLoadPolicy(exportDirectory, out var presetPolicy)
                ? presetPolicy
                : LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
        string? legacyDatDirectory = null;
        var project = ProjectManager.Instance?.CurrentProject;
        if (project is { IsReadOnlyDatProject: true }
            && !string.IsNullOrWhiteSpace(project.BaseDatDirectory)) {
            legacyDatDirectory = project.BaseDatDirectory;
        }

        try {
            await LegacyDatConversionDialogs.RunWithProgressAsync<bool>(
                "MainDialogHost",
                "Repairing client DATs",
                onProgress => {
                    LegacyDatClientDatRepair.Repair(
                        exportDirectory,
                        retailSeedDirectory,
                        policy,
                        legacyDatDirectory,
                        onProgress);

                    RetailClientDatValidationReport report = RetailClientDatExportValidator.Validate(
                        exportDirectory,
                        new RetailClientDatExportValidator.Options {
                            ExportPolicy = policy,
                            RequireOverlayPortalRoot =
                                LegacyDatMergeRuleResolver.ShouldRequireOverlayPortalRoot(policy),
                        });
                    if (!report.IsValid) {
                        throw new InvalidOperationException(string.Join(Environment.NewLine, report.Errors));
                    }

                    return true;
                });

            await LegacyDatConversionDialogs.ShowMessageAsync(
                "MainDialogHost",
                "Client DAT repair complete",
                $"Repaired files in:\n{exportDirectory}\n\nCopy all four client_*.dat files to your AC client folder.");
        }
        catch (Exception ex) {
            await LegacyDatConversionDialogs.ShowMessageAsync(
                "MainDialogHost",
                "Client DAT repair failed",
                ex.Message);
        }
    }

    [RelayCommand]
    private void OpenKeyboardShortcuts() {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
             var vm = new KeyboardMappingViewModel(_inputManager, _settings);
             var window = new KeyboardMappingWindow {
                 DataContext = vm
             };
             window.Show(desktop.MainWindow);
        }
    }

    [RelayCommand]
    private async Task OpenGettingStarted() {
        HideGettingStartedNextTime = false;
        try {
            var view = new GettingStartedView { DataContext = this };
            await DialogHost.Show(view, "MainDialogHost");
        }
        catch (Exception ex) {
            Console.WriteLine($"[GettingStarted] Could not show guide: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CloseGettingStarted() {
        ApplyGettingStartedPreference();
        DialogHost.Close("MainDialogHost");
    }

    [RelayCommand]
    private void StartInWorld() {
        ApplyGettingStartedPreference();
        DialogHost.Close("MainDialogHost");
        SwitchToLandscapeEditor();
    }

    [RelayCommand]
    private void StartInDungeon() {
        ApplyGettingStartedPreference();
        DialogHost.Close("MainDialogHost");
        SwitchToDungeonEditor();
    }

    private void ApplyGettingStartedPreference() {
        if (!HideGettingStartedNextTime) return;
        _settings.App.ShowGettingStarted = false;
        _settings.Save();
    }

    [RelayCommand]
    private void SwitchToLandscapeEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<LandscapeEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToDungeonEditor() {
        if (TryShowUnsupportedLegacyEditor("Dungeon Editor")) return;
        ActiveEditor = ProjectManager.Instance?.GetProjectService<DungeonEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToSpellEditor() {
        if (TryShowUnsupportedLegacyEditor("Spell Editor")) return;
        ActiveEditor = ProjectManager.Instance?.GetProjectService<SpellEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToSpellSetEditor() {
        if (TryShowUnsupportedLegacyEditor("Spell Set Editor")) return;
        ActiveEditor = ProjectManager.Instance?.GetProjectService<SpellSetEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToSkillEditor() {
        if (TryShowUnsupportedLegacyEditor("Skill Editor")) return;
        ActiveEditor = ProjectManager.Instance?.GetProjectService<SkillEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToExperienceEditor() {
        if (TryShowUnsupportedLegacyEditor("Experience Table Editor")) return;
        ActiveEditor = ProjectManager.Instance?.GetProjectService<ExperienceEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToVitalEditor() {
        if (TryShowUnsupportedLegacyEditor("Vital Table Editor")) return;
        ActiveEditor = ProjectManager.Instance?.GetProjectService<VitalEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToCharGenEditor() {
        if (TryShowUnsupportedLegacyEditor("Character Creation Editor")) return;
        ActiveEditor = ProjectManager.Instance?.GetProjectService<CharGenEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToLayoutEditor() {
        if (TryShowUnsupportedLegacyEditor("UI Layout Viewer")) return;
        ActiveEditor = ProjectManager.Instance?.GetProjectService<LayoutEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToObjectDebugEditor() {
        ActiveEditor = ProjectManager.Instance?.GetProjectService<ObjectDebugEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToWeenieEditor() {
        if (TryShowUnsupportedLegacyEditor("Weenie Editor")) return;
        ActiveEditor = ProjectManager.Instance?.GetProjectService<WeenieEditorViewModel>();
    }

    [RelayCommand]
    private void SwitchToMonsterEditor() {
        if (TryShowUnsupportedLegacyEditor("Monster Creator")) return;
        ActiveEditor = ProjectManager.Instance?.GetProjectService<MonsterEditorViewModel>();
    }

    [RelayCommand]
    private void AnalyzeDungeonRooms() {
        if (TryShowUnsupportedLegacyEditor("Dungeon analysis")) return;
        var dungeonEditor = ProjectManager.Instance?.GetProjectService<DungeonEditorViewModel>();
        dungeonEditor?.AnalyzeRoomsCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenLogFolder() {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ACME WorldBuilder", "Logs");

        if (!Directory.Exists(logDir)) {
            Directory.CreateDirectory(logDir);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            Process.Start(new ProcessStartInfo("explorer.exe", logDir));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            Process.Start("open", logDir);
        }
        else {
            Process.Start("xdg-open", logDir);
        }
    }

    // Landscape editor menu (only relevant when landscape editor is active)
    [RelayCommand]
    private void LandscapeTogglePerformanceOverlay() {
        var le = GetLandscapeEditor();
        if (le != null) le.ShowPerformanceOverlay = !le.ShowPerformanceOverlay;
    }

    [RelayCommand]
    private void LandscapeClearCache() {
        GetLandscapeEditor()?.ClearCacheCommand.Execute(null);
    }

    [RelayCommand]
    private async Task LandscapeFreshStart() {
        if (IsReadOnlyDatProject) return;
        var le = GetLandscapeEditor();
        if (le != null) await le.FreshStartCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task LandscapeImportHeightmap() {
        if (IsReadOnlyDatProject) return;
        var le = GetLandscapeEditor();
        if (le != null) await le.ImportHeightmapCommand.ExecuteAsync(null);
    }

    private bool TryShowUnsupportedLegacyEditor(string editorName) {
        if (!IsReadOnlyDatProject) {
            return false;
        }

        ActiveEditor = new ReadOnlyInfoViewModel(
            editorName,
            "Legacy pre-ToD projects currently support the Landscape viewer and Object Debug viewer only. This session is read-only, so retail-only editors and mutation tools are disabled.",
            actionText: "Convert Legacy DATs To Retail...",
            actionCommand: ConvertLegacyDatsToRetailCommand);
        return true;
    }

    private async Task<string?> PickFolderAsync(string title, string startDirectory) {
        IStorageFolder? suggested = null;
        if (Directory.Exists(startDirectory)) {
            suggested = await TopLevel.StorageProvider.TryGetFolderFromPathAsync(startDirectory);
        }

        var folders = await TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = suggested,
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private static async Task ShowLegacyModeDialogAsync(string title, string message) {
        await DialogHost.Show(new StackPanel {
            Margin = new Thickness(18),
            Spacing = 10,
            Children = {
                new TextBlock { Text = title, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 },
                new Button {
                    Content = "OK",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Command = new RelayCommand(() => DialogHost.Close("MainDialogHost"))
                }
            }
        }, "MainDialogHost");
    }
}
