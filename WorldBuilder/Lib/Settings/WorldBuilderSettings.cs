using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldBuilder.Lib.Settings {
    public partial class WorldBuilderSettings : ObservableObject {
        private readonly ILogger<WorldBuilderSettings>? _log;

        [JsonIgnore]
        public string AppDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ACME WorldBuilder");

        [JsonIgnore]
        public string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

        private AppSettings _app = new();
        public AppSettings App {
            get => _app;
            set => SetProperty(ref _app, value);
        }

        private LandscapeEditorSettings _landscape = new();
        public LandscapeEditorSettings Landscape {
            get => _landscape;
            set => SetProperty(ref _landscape, value);
        }

        private DungeonEditorSettings _dungeon = new();
        public DungeonEditorSettings Dungeon {
            get => _dungeon;
            set => SetProperty(ref _dungeon, value);
        }

        private InputSettings _input = new();
        public InputSettings Input {
            get => _input;
            set => SetProperty(ref _input, value);
        }

        private AceDbConnectionSettings _aceDbConnection = new();
        public AceDbConnectionSettings AceDbConnection {
            get => _aceDbConnection;
            set => SetProperty(ref _aceDbConnection, value);
        }

        public WorldBuilderSettings() { }

        public WorldBuilderSettings(ILogger<WorldBuilderSettings> log) {
            _log = log;

            if (!Directory.Exists(AppDataDirectory)) {
                Directory.CreateDirectory(AppDataDirectory);
            }

            TryLoad();
        }

        private void TryLoad() {
            if (File.Exists(SettingsFilePath)) {
                try {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<WorldBuilderSettings>(json, SourceGenerationContext.Default.WorldBuilderSettings);
                    if (settings != null) {
                        foreach (var property in settings.GetType().GetProperties()) {
                            if (property.CanWrite) {
                                property.SetValue(this, property.GetValue(settings));
                            }
                        }
                    }
                }
                catch (Exception ex) {
                    _log?.LogError(ex, "Failed to load settings");
                }
            }
        }

        public void Save() {
            var tmpFile = Path.GetTempFileName();
            try {
                SanitizeFloats(this);
                var json = JsonSerializer.Serialize(this, SourceGenerationContext.Default.WorldBuilderSettings)
                    ?? throw new Exception("Failed to serialize settings to json");
                File.WriteAllText(tmpFile, json);
                File.Move(tmpFile, SettingsFilePath, true);
            }
            catch(Exception ex) {
                _log?.LogError(ex, "Failed to save settings");
            }
            finally {
                File.Delete(tmpFile);
            }
        }

        private static void SanitizeFloats(object obj) {
            if (obj == null) return;
            try {
                foreach (var prop in obj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) {
                    if (!prop.CanRead || !prop.CanWrite) continue;
                    if (prop.GetIndexParameters().Length > 0) continue;
                    try {
                        if (prop.PropertyType == typeof(float)) {
                            var val = (float)prop.GetValue(obj)!;
                            if (!float.IsFinite(val)) prop.SetValue(obj, 0f);
                        }
                        else if (prop.PropertyType == typeof(double)) {
                            var val = (double)prop.GetValue(obj)!;
                            if (!double.IsFinite(val)) prop.SetValue(obj, 0.0);
                        }
                        else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string)
                            && prop.PropertyType.Namespace?.StartsWith("WorldBuilder") == true) {
                            SanitizeFloats(prop.GetValue(obj)!);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}