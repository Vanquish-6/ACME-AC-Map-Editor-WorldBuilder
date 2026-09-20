using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.ViewModels {
    public partial class ExportDatsWindowViewModel : ViewModelBase {
        private readonly WorldBuilderSettings _settings;
        private readonly Project _project;
        private readonly Window _window;
        private readonly InstanceRepositionService _repositionService;
        private readonly string[] datFiles = new[]
        {
            "client_cell_1.dat",
            "client_portal.dat",
            "client_highres.dat",
            "client_local_English.dat"
        };
        private bool _isValidating;

        [ObservableProperty]
        private string _exportDirectory = string.Empty;

        [ObservableProperty]
        private int _portalIteration = 0;

        [ObservableProperty]
        private int _currentPortalIteration = 0;

        [ObservableProperty]
        private bool _overwriteFiles = false;

        [ObservableProperty]
        private bool _hasDirectoryError = false;

        [ObservableProperty]
        private string _directoryErrorMessage = string.Empty;

        [ObservableProperty]
        private bool _hasIterationError = false;

        [ObservableProperty]
        private string _iterationErrorMessage = string.Empty;

        [ObservableProperty]
        private bool _canExport = false;

        // World database reposition settings
        [ObservableProperty]
        private bool _enableReposition = false;

        [ObservableProperty]
        private string _dbHost = "localhost";

        [ObservableProperty]
        private int _dbPort = 3306;

        [ObservableProperty]
        private string _dbDatabase = "";

        [ObservableProperty]
        private string _dbUser = "root";

        [ObservableProperty]
        private string _dbPassword = string.Empty;

        [ObservableProperty]
        private bool _applyDirectly = false;

        [ObservableProperty]
        private float _repositionThreshold = 0.05f;

        [ObservableProperty]
        private string _connectionTestResult = string.Empty;

        [ObservableProperty]
        private bool _hasConnectionTestResult = false;

        [ObservableProperty]
        private bool _connectionTestSuccess = false;

        partial void OnExportDirectoryChanged(string value) {
            Validate();
        }

        partial void OnPortalIterationChanged(int value) {
            Validate();
        }

        partial void OnOverwriteFilesChanged(bool value) {
            Validate();
        }

        public ExportDatsWindowViewModel(WorldBuilderSettings settings, Project project,
            Window window, InstanceRepositionService repositionService) {
            _settings = settings;
            _project = project;
            _window = window;
            _repositionService = repositionService;

            ExportDirectory = _settings.App.ProjectsDirectory;
            CurrentPortalIteration = _project.DocumentManager.Dats.GetIteration(DatArchive.Portal);
            PortalIteration = _project.DocumentManager.Dats.GetIteration(DatArchive.Portal);

            // Load saved world database settings from project, else pre-fill from app settings
            if (_project.AceDb != null) {
                EnableReposition = _project.AceDb.EnableReposition;
                DbHost = _project.AceDb.Host;
                DbPort = _project.AceDb.Port;
                DbDatabase = _project.AceDb.Database;
                DbUser = _project.AceDb.User;
                DbPassword = _project.AceDb.Password;
                ApplyDirectly = _project.AceDb.ApplyDirectly;
                RepositionThreshold = _project.AceDb.Threshold;
            }
            else if (_settings.AceDbConnection != null) {
                DbHost = _settings.AceDbConnection.Host;
                DbPort = _settings.AceDbConnection.Port;
                DbDatabase = _settings.AceDbConnection.Database;
                DbUser = _settings.AceDbConnection.User;
                DbPassword = _settings.AceDbConnection.Password;
            }

            Validate();
        }

        private AceDbSettings BuildAceDbSettings() => new AceDbSettings {
            Host = DbHost,
            Port = DbPort,
            Database = DbDatabase,
            User = DbUser,
            Password = DbPassword,
            EnableReposition = EnableReposition,
            ApplyDirectly = ApplyDirectly,
            Threshold = RepositionThreshold,
        };

        [RelayCommand]
        public async Task TestConnection() {
            HasConnectionTestResult = false;
            var aceSettings = BuildAceDbSettings();
            using var connector = new AceDbConnector(aceSettings);
            var error = await connector.TestConnectionAsync();

            if (error == null) {
                ConnectionTestResult = "Connection successful!";
                ConnectionTestSuccess = true;
            }
            else {
                ConnectionTestResult = $"Connection failed: {error}";
                ConnectionTestSuccess = false;
            }
            HasConnectionTestResult = true;
        }

        [RelayCommand]
        public async Task BrowseExportDirectory() {
            var files = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
                Title = "Choose DAT export directory",
                AllowMultiple = false,
                SuggestedStartLocation = await _window.StorageProvider.TryGetFolderFromPathAsync(_settings.App.ProjectsDirectory)
            });

            if (files.Count > 0) {
                var localPath = files[0].TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(localPath)) {
                    ExportDirectory = localPath;
                }
            }
        }

        [RelayCommand]
        public async Task Export() {
            if (!Validate()) return;

            Task? progressDialogTask = null;

            try {
                if (!OverwriteFiles) {
                    foreach (var datFile in datFiles) {
                        var filePath = Path.Combine(ExportDirectory, datFile);
                        if (File.Exists(filePath)) {
                            DirectoryErrorMessage = $"File {datFile} already exists. Check 'Overwrite existing DAT files' to replace.";
                            HasDirectoryError = true;
                            return;
                        }
                    }
                }

                // Persist world database settings to project before export
                var aceSettings = BuildAceDbSettings();
                _project.AceDb = aceSettings;
                _project.Save();

                InstanceRepositionService.RepositionResult? repoResult = null;

                if (EnableReposition) {
                    _project.OnExportReposition = async (ctx) => {
                        repoResult = await _repositionService.RunAsync(aceSettings, ctx);
                    };
                }
                else {
                    _project.OnExportReposition = null;
                }

                // When AceDb is configured, export dungeon instance placements (generators/items/portals) to SQL
                _project.OnExportDungeonInstances = (exportDir, dungeonsWithPlacements) => {
                    if (dungeonsWithPlacements.Count == 0) return;
                    var allRecords = new List<LandblockInstanceRecord>();
                    foreach (var doc in dungeonsWithPlacements)
                        allRecords.AddRange(AceDbConnector.ToLandblockInstanceRecords(doc.LandblockKey, doc.InstancePlacements));
                    if (allRecords.Count == 0) return;
                    string sql = AceDbConnector.GenerateInsertSqlBatch(allRecords, aceSettings.Database);
                    string sqlPath = Path.Combine(exportDir, "dungeon_instances.sql");
                    File.WriteAllText(sqlPath, sql);
                    if (aceSettings.ApplyDirectly) {
                        try {
                            using var connector = new AceDbConnector(aceSettings);
                            connector.ExecuteSqlAsync(sql).GetAwaiter().GetResult();
                        }
                        catch (Exception ex) {
                            Console.WriteLine($"[Export] Dungeon instances ApplyDirectly failed: {ex.Message}");
                        }
                    }
                };

                var exportStatusText = new TextBlock {
                    Text = "Starting export...",
                    FontSize = 13,
                    Foreground = Avalonia.Media.Brushes.White,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                };

                var exportProgressDialog = new StackPanel {
                    Margin = new Avalonia.Thickness(24),
                    Spacing = 12,
                    Width = 380,
                    Children = {
                        new TextBlock { Text = "Exporting DATs", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold },
                        exportStatusText,
                        new Avalonia.Controls.ProgressBar { IsIndeterminate = true, Height = 4 }
                    }
                };

                progressDialogTask = DialogHost.Show(exportProgressDialog, "ExportDialogHost");

                await Task.Run(() => _project.ExportDats(ExportDirectory, PortalIteration, status => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => exportStatusText.Text = status);
                }));

                await CloseDialogHostSafeAsync(progressDialogTask);
                progressDialogTask = null;

                var successMsg = "DAT files exported successfully!";
                if (repoResult != null) {
                    if (repoResult.Error != null) {
                        successMsg += $"\n\nReposition warning: {repoResult.Error}";
                    }
                    else if (repoResult.InstancesUpdated > 0) {
                        successMsg += $"\n\nRepositioned {repoResult.InstancesUpdated} of {repoResult.InstancesChecked} instances across {repoResult.LandblocksProcessed} landblocks.";
                        if (repoResult.SqlFilePath != null) {
                            successMsg += $"\nSQL file: {repoResult.SqlFilePath}";
                        }
                        if (repoResult.AppliedDirectly) {
                            successMsg += "\nChanges applied directly to database.";
                        }
                    }
                    else {
                        successMsg += $"\n\nNo instances needed repositioning ({repoResult.InstancesChecked} checked).";
                    }
                }

                // Report dungeon instance export if we had placements
                var dungeonsWithPlacements = new List<DungeonDocument>();
                foreach (var (_, doc) in _project.DocumentManager.ActiveDocs) {
                    if (doc is DungeonDocument dng && dng.InstancePlacements.Count > 0)
                        dungeonsWithPlacements.Add(dng);
                }
                if (dungeonsWithPlacements.Count > 0) {
                    int totalPlacements = dungeonsWithPlacements.Sum(d => d.InstancePlacements.Count);
                    successMsg += $"\n\nDungeon instances: wrote dungeon_instances.sql ({totalPlacements} generator/item/portal placement(s) across {dungeonsWithPlacements.Count} dungeon(s)).";
                    if (aceSettings.ApplyDirectly)
                        successMsg += " Applied to database.";
                }

                // Outdoor instance placements (from Terrain editor)
                if (_project.OutdoorInstancePlacements != null && _project.OutdoorInstancePlacements.Count > 0) {
                    var outdoorRecords = AceDbConnector.ToLandblockInstanceRecordsFromOutdoor(_project.OutdoorInstancePlacements);
                    string outdoorSql = AceDbConnector.GenerateInsertSqlBatch(outdoorRecords, aceSettings.Database);
                    string outdoorPath = Path.Combine(ExportDirectory, "landblock_instances.sql");
                    File.WriteAllText(outdoorPath, outdoorSql);
                    if (aceSettings.ApplyDirectly) {
                        try {
                            using var connector = new AceDbConnector(aceSettings);
                            connector.ExecuteSqlAsync(outdoorSql).GetAwaiter().GetResult();
                        }
                        catch (Exception ex) {
                            Console.WriteLine($"[Export] Outdoor instances ApplyDirectly failed: {ex.Message}");
                        }
                    }
                    successMsg += $"\n\nOutdoor instances: wrote landblock_instances.sql ({_project.OutdoorInstancePlacements.Count} placement(s)).";
                    if (aceSettings.ApplyDirectly)
                        successMsg += " Applied to database.";
                }

                await DialogHost.Show(new StackPanel {
                    Margin = new Avalonia.Thickness(10),
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = successMsg, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 400 },
                        new Button
                        {
                            Content = "OK",
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            Command = new RelayCommand(() => DialogHost.Close("ExportDialogHost"))
                        }
                    }
                }, "ExportDialogHost");

                _window.Close();
            }
            catch (Exception ex) {
                Console.WriteLine($"[Export] Export failed: {ex}");
                await CloseDialogHostSafeAsync(progressDialogTask);

                DirectoryErrorMessage = $"Export failed: {ex.Message}";
                HasDirectoryError = true;

                try {
                    await DialogHost.Show(new StackPanel {
                        Margin = new Avalonia.Thickness(10),
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = $"Export failed: {ex.Message}" },
                            new Button
                            {
                                Content = "OK",
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                                Command = new RelayCommand(() => DialogHost.Close("ExportDialogHost"))
                            }
                        }
                    }, "ExportDialogHost");
                }
                catch {
                    // Error already visible via HasDirectoryError
                }
            }
            finally {
                _project.OnExportReposition = null;
                _project.OnExportDungeonInstances = null;
            }
        }

        private static async Task CloseDialogHostSafeAsync(Task? dialogTask) {
            bool closed = false;
            try {
                DialogHost.Close("ExportDialogHost");
                closed = true;
            }
            catch { }

            if (closed && dialogTask != null) {
                try { await dialogTask; } catch { }
            }
        }

        private bool Validate() {
            if (_isValidating) return false;
            _isValidating = true;

            try {
                HasDirectoryError = false;
                HasIterationError = false;
                DirectoryErrorMessage = string.Empty;
                IterationErrorMessage = string.Empty;

                if (string.IsNullOrWhiteSpace(ExportDirectory)) {
                    DirectoryErrorMessage = "Export directory is required.";
                    HasDirectoryError = true;
                }
                else if (!Directory.Exists(ExportDirectory)) {
                    DirectoryErrorMessage = "Selected directory does not exist.";
                    HasDirectoryError = true;
                }

                if (PortalIteration <= 0) {
                    IterationErrorMessage = "Portal iteration must be greater than 0.";
                    HasIterationError = true;
                }

                CanExport = !HasDirectoryError && !HasIterationError;
                return CanExport;
            }
            finally {
                _isValidating = false;
            }
        }
    }
}
