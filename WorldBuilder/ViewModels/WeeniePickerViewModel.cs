using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib.AceDb;

namespace WorldBuilder.ViewModels {
    /// <summary>
    /// Display item for weenie picker list.
    /// </summary>
    public partial class WeeniePickerItem : ObservableObject {
        public uint ClassId { get; }
        public string Name { get; }
        /// <summary>Setup DID for 3D model preview. 0 = no visual available.</summary>
        public uint SetupId { get; }
        public string DisplayText => $"{Name} ({ClassId})";

        public WeeniePickerItem(uint classId, string name, uint setupId) {
            ClassId = classId;
            Name = name ?? "";
            SetupId = setupId;
        }
    }

    /// <summary>
    /// ViewModel for browsing weenies from the world database (search + list + selection).
    /// Used by Dungeon Instance Placements and Terrain instance panels.
    /// </summary>
    public partial class WeeniePickerViewModel : ViewModelBase {
        private readonly WorldBuilderSettings _settings;

        [ObservableProperty]
        private string _searchText = "";

        [ObservableProperty]
        private WeeniePickerItem? _selectedWeenie;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusMessage = "";

        public ObservableCollection<WeeniePickerItem> Weenies { get; } = new();

        /// <summary>Fired when user selects a weenie (full picker item with ClassId and SetupId).</summary>
        public event EventHandler<WeeniePickerItem>? WeenieSelected;

        /// <summary>Fired after weenies are loaded, with the full list for bulk cache population.</summary>
        public event EventHandler<IReadOnlyList<WeeniePickerItem>>? WeeniesLoaded;

        public WeeniePickerViewModel(WorldBuilderSettings settings) {
            _settings = settings;
        }

        [RelayCommand]
        private async Task LoadWeeniesAsync() {
            if (_settings?.AceDbConnection == null) {
                StatusMessage = "Configure the world database in Settings first.";
                return;
            }

            IsLoading = true;
            StatusMessage = "Loading...";
            Weenies.Clear();
            SelectedWeenie = null;

            try {
                var aceSettings = _settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
                var list = await connector.GetWeenieNamesAsync(search, limit: 500);

                foreach (var e in list)
                    Weenies.Add(new WeeniePickerItem(e.ClassId, e.Name, e.SetupId));

                StatusMessage = list.Count == 0
                    ? (search != null ? "No weenies match the search." : "No weenies found. Check DB connection and that weenie_properties_string exists.")
                    : $"{Weenies.Count} weenie(s) loaded.";

                WeeniesLoaded?.Invoke(this, (IReadOnlyList<WeeniePickerItem>)Weenies);
            }
            catch (Exception ex) {
                StatusMessage = "Error: " + ex.Message;
            }
            finally {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private async Task TestConnectionAsync() {
            if (_settings?.AceDbConnection == null) {
                StatusMessage = "Configure the world database in Settings first.";
                return;
            }

            IsLoading = true;
            StatusMessage = "Testing...";
            try {
                var aceSettings = _settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var err = await connector.TestConnectionAsync();
                StatusMessage = err == null ? "Connection OK." : "Failed: " + err;
            }
            catch (Exception ex) {
                StatusMessage = "Error: " + ex.Message;
            }
            finally {
                IsLoading = false;
            }
        }

        partial void OnSelectedWeenieChanged(WeeniePickerItem? value) {
            if (value != null)
                WeenieSelected?.Invoke(this, value);
        }
    }
}
