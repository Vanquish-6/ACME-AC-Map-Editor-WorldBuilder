using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using DatCharGen = Acme.Dat.CharGen;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WorldBuilder.Editors.Dungeon;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.CharGen {
    public partial class CharGenEditorViewModel : ViewModelBase {
        private IDatReaderWriter? _dats;
        private Project? _project;
        private PortalDatDocument? _portalDoc;
        private DatCharGen? _charGen;
        private const uint CharGenId = 0x0E000002;

        [ObservableProperty] private string _statusText = "No CharGen loaded";
        [ObservableProperty] private ObservableCollection<HeritageListItem> _heritageGroups = new();
        [ObservableProperty] private HeritageListItem? _selectedHeritage;
        [ObservableProperty] private HeritageDetailViewModel? _selectedDetail;
        [ObservableProperty] private ObservableCollection<StartingAreaViewModel> _startingAreas = new();

        public DungeonObjectBrowserViewModel? ObjectBrowser { get; private set; }
        public WorldBuilderSettings Settings { get; }

        public CharGenEditorViewModel(WorldBuilderSettings settings) {
            Settings = settings;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _portalDoc = project.DocumentManager.GetOrCreateDocumentAsync<PortalDatDocument>(PortalDatDocument.DocumentId).Result;
            LoadCharGen();
        }

        internal void InitObjectBrowser(Func<Landscape.ThumbnailRenderService?> getThumbnailService) {
            if (_dats == null || ObjectBrowser != null) return;
            ObjectBrowser = new DungeonObjectBrowserViewModel(_dats, getThumbnailService);
            ObjectBrowser.ActivateForDisplay();
            OnPropertyChanged(nameof(ObjectBrowser));
        }

        private void LoadCharGen() {
            if (_dats == null) return;

            if (_portalDoc != null && _portalDoc.TryGetEntry<DatCharGen>(CharGenId, out var docTable) && docTable != null) {
                _charGen = docTable;
            }
            else if (!_dats.TryGet<DatCharGen>(CharGenId, out var datTable)) {
                StatusText = "Failed to load CharGen from DAT";
                return;
            }
            else {
                _charGen = datTable;
            }

            RefreshHeritageList();
            RefreshStartingAreas();
            StatusText = $"Loaded CharGen: {_charGen.HeritageGroups.Count} heritages, {_charGen.StartingAreas.Count} starting areas";
        }

        private void RefreshHeritageList() {
            if (_charGen == null) return;
            var items = _charGen.HeritageGroups
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new HeritageListItem(kvp.Key, kvp.Value))
                .ToList();
            HeritageGroups = new ObservableCollection<HeritageListItem>(items);
        }

        private void RefreshStartingAreas() {
            if (_charGen == null) return;
            var areas = _charGen.StartingAreas
                .Select(a => new StartingAreaViewModel(a))
                .ToList();
            StartingAreas = new ObservableCollection<StartingAreaViewModel>(areas);
        }

        partial void OnSelectedHeritageChanged(HeritageListItem? value) {
            if (value != null && _charGen != null && _dats != null &&
                _charGen.HeritageGroups.TryGetValue(value.Id, out var group)) {
                try {
                    SelectedDetail = new HeritageDetailViewModel(value.Id, group, _charGen.HeritageGroups, _dats);
                }
                catch (Exception ex) {
                    SelectedDetail = null;
                    StatusText = $"Error loading heritage: {ex.Message}";
                }
            }
            else {
                SelectedDetail = null;
            }
        }

        [RelayCommand]
        private void Save() {
            if (_charGen == null || _portalDoc == null) return;

            if (SelectedDetail != null && _charGen.HeritageGroups.TryGetValue(SelectedDetail.HeritageId, out var group)) {
                SelectedDetail.ApplyTo(group);
            }

            foreach (var areaVm in StartingAreas) {
                areaVm.ApplyTo();
            }

            _portalDoc.SetEntry(CharGenId, _charGen);

            if (SelectedDetail != null) {
                int idx = -1;
                for (int i = 0; i < HeritageGroups.Count; i++) {
                    if (HeritageGroups[i].Id == SelectedDetail.HeritageId) { idx = i; break; }
                }
                if (idx >= 0 && _charGen.HeritageGroups.TryGetValue(SelectedDetail.HeritageId, out var updated)) {
                    HeritageGroups[idx] = new HeritageListItem(SelectedDetail.HeritageId, updated);
                }
            }

            StatusText = "Saved CharGen to project. Use File > Export to write DATs.";
        }

        [RelayCommand]
        private void AddHeritage() {
            if (_charGen == null || _dats == null) return;

            uint nextId = 1;
            if (_charGen.HeritageGroups.Count > 0)
                nextId = _charGen.HeritageGroups.Keys.Max() + 1;

            var newGroup = new HeritageGroupCG {
                Name = "New Heritage",
                AttributeCredits = 330,
                SkillCredits = 52
            };

            _charGen.HeritageGroups[nextId] = newGroup;
            RefreshHeritageList();
            SelectedHeritage = HeritageGroups.FirstOrDefault(h => h.Id == nextId);
            StatusText = $"Added new heritage #{nextId}. Remember to Save.";
        }

        [RelayCommand]
        private void RemoveHeritage() {
            if (SelectedDetail == null || _charGen == null || _portalDoc == null) return;

            var id = SelectedDetail.HeritageId;
            if (!_charGen.HeritageGroups.Remove(id)) return;

            _portalDoc.SetEntry(CharGenId, _charGen);

            SelectedDetail = null;
            RefreshHeritageList();
            StatusText = $"Deleted heritage #{id}. Use File > Export to write DATs.";
        }

        [RelayCommand]
        private void AddStartingArea() {
            if (_charGen == null) return;

            var area = new StartingArea { Name = "New Area" };
            _charGen.StartingAreas.Add(area);
            RefreshStartingAreas();
            StatusText = $"Added new starting area. Remember to Save.";
        }

        [RelayCommand]
        private void RemoveStartingArea(StartingAreaViewModel? areaVm) {
            if (areaVm == null || _charGen == null || _portalDoc == null) return;

            var backing = areaVm.BackingArea;
            if (!_charGen.StartingAreas.Remove(backing)) return;

            _portalDoc.SetEntry(CharGenId, _charGen);

            RefreshStartingAreas();
            StatusText = "Removed starting area. Use File > Export to write DATs.";
        }
    }

    public class HeritageListItem {
        public uint Id { get; }
        public string Name { get; }
        public string IdHex { get; }
        public uint AttributeCredits { get; }
        public uint SkillCredits { get; }

        public HeritageListItem(uint id, HeritageGroupCG group) {
            Id = id;
            try {
                Name = group.Name?.ToString() ?? $"Heritage {id}";
                AttributeCredits = group.AttributeCredits;
                SkillCredits = group.SkillCredits;
            }
            catch {
                Name = $"Heritage {id}";
            }
            IdHex = $"0x{id:X2}";
        }

        public override string ToString() => $"{IdHex} - {Name}";
    }

    public partial class IconPickerItem : ObservableObject {
        public uint Id { get; }
        public string IdHex { get; }
        [ObservableProperty] private WriteableBitmap? _bitmap;

        public IconPickerItem(uint id) {
            Id = id;
            IdHex = $"0x{id:X8}";
        }
    }

    public partial class HeritageDetailViewModel : ObservableObject {
        public uint HeritageId { get; }

        [ObservableProperty] private string _name = "";
        [ObservableProperty] private uint _iconId;
        [ObservableProperty] private WriteableBitmap? _iconBitmap;
        [ObservableProperty] private uint _attributeCredits;
        [ObservableProperty] private uint _skillCredits;
        [ObservableProperty] private uint _setupId;
        [ObservableProperty] private uint _environmentSetupId;

        [ObservableProperty] private string _setupInfo = "";
        [ObservableProperty] private string _envSetupInfo = "";

        [ObservableProperty] private ObservableCollection<IconPickerItem> _availableIcons = new();
        [ObservableProperty] private bool _isIconPickerOpen;

        private readonly IDatReaderWriter? _dats;

        partial void OnIconIdChanged(uint value) {
            if (_dats != null) {
                var localDats = _dats;
                Task.Run(() => {
                    var bmp = DatIconLoader.LoadIcon(localDats, value, 48);
                    Dispatcher.UIThread.Post(() => IconBitmap = bmp);
                });
            }
        }

        partial void OnSetupIdChanged(uint value) {
            LoadSetupInfo(value, isEnv: false);
        }

        partial void OnEnvironmentSetupIdChanged(uint value) {
            LoadSetupInfo(value, isEnv: true);
        }

        public HeritageDetailViewModel(uint id, HeritageGroupCG group,
            Dictionary<uint, HeritageGroupCG> allGroups, IDatReaderWriter dats) {
            _dats = dats;
            HeritageId = id;
            try {
                Name = group.Name?.ToString() ?? "";
                IconId = group.IconId;
                AttributeCredits = group.AttributeCredits;
                SkillCredits = group.SkillCredits;
                SetupId = group.SetupId;
                EnvironmentSetupId = group.EnvironmentSetupId;
            }
            catch {
                Name = $"Heritage {id}";
            }

            try {
                LoadIconAsync(IconId);
                BuildAvailableIcons(allGroups);
            }
            catch { }
        }

        private void LoadSetupInfo(uint id, bool isEnv) {
            if (id == 0) {
                if (isEnv) EnvSetupInfo = "Not set";
                else SetupInfo = "Not set";
                return;
            }

            var localDats = _dats;
            Task.Run(() => {
                string info = BuildSetupInfoString(id, localDats);
                Dispatcher.UIThread.Post(() => {
                    if (isEnv) EnvSetupInfo = info;
                    else SetupInfo = info;
                });
            });
        }

        private static string BuildSetupInfoString(uint id, IDatReaderWriter? dats) {
            bool isSetup = (id & 0xFF000000) == 0x02000000;
            if (dats == null) return $"0x{id:X8}";

            try {
                if (isSetup && dats.TryGet<Setup>(id, out var setup)) {
                    int partCount = setup.Parts?.Count ?? 0;
                    int placementCount = setup.PlacementFrames?.Count ?? 0;
                    return $"Setup: {partCount} part(s), {placementCount} placement(s)";
                }
                else if (!isSetup) {
                    return "GfxObj (single mesh)";
                }
            }
            catch { }
            return "Not found in DAT";
        }

        private void LoadIconAsync(uint iconId) {
            if (iconId == 0 || _dats == null) return;
            var localDats = _dats;
            Task.Run(() => {
                var bmp = DatIconLoader.LoadIcon(localDats, iconId, 48);
                Dispatcher.UIThread.Post(() => IconBitmap = bmp);
            });
        }

        private void BuildAvailableIcons(Dictionary<uint, HeritageGroupCG> allGroups) {
            var snapshot = allGroups.Values.ToArray();
            var uniqueIconIds = snapshot
                .Select(g => g.IconId)
                .Where(id => id != 0)
                .Distinct()
                .ToList();
            uniqueIconIds.Sort();

            if (_dats != null) {
                var localDats = _dats;
                Task.Run(() => {
                    var items = new List<IconPickerItem>();
                    foreach (var iconId in uniqueIconIds) {
                        var item = new IconPickerItem(iconId);
                        item.Bitmap = DatIconLoader.LoadIcon(localDats, iconId, 32);
                        if (item.Bitmap != null)
                            items.Add(item);
                    }
                    Dispatcher.UIThread.Post(() => {
                        AvailableIcons = new ObservableCollection<IconPickerItem>(items);
                    });
                });
            }
        }

        [RelayCommand]
        private void PickIcon(IconPickerItem? item) {
            if (item == null) return;
            IconId = item.Id;
            IsIconPickerOpen = false;
        }

        [RelayCommand]
        private void ToggleIconPicker() {
            IsIconPickerOpen = !IsIconPickerOpen;
        }

        public void ApplyTo(HeritageGroupCG group) {
            group.Name = Name;
            group.IconId = IconId;
            group.AttributeCredits = AttributeCredits;
            group.SkillCredits = SkillCredits;
            group.SetupId = SetupId;
            group.EnvironmentSetupId = EnvironmentSetupId;
        }
    }

    public partial class StartingAreaViewModel : ObservableObject {
        private readonly StartingArea _area;

        [ObservableProperty] private string _name;
        [ObservableProperty] private ObservableCollection<LocationViewModel> _locations = new();
        public int LocationCount => _area.Locations.Count;
        public StartingArea BackingArea => _area;

        public StartingAreaViewModel(StartingArea area) {
            _area = area;
            try { _name = area.Name?.ToString() ?? "(unnamed)"; }
            catch { _name = "(unnamed)"; }

            foreach (var loc in area.Locations) {
                Locations.Add(new LocationViewModel(loc));
            }
        }

        [RelayCommand]
        private void AddLocation() {
            var pos = new Position {
                CellId = 0,
                Frame = new Frame {
                    Origin = System.Numerics.Vector3.Zero,
                    Orientation = System.Numerics.Quaternion.Identity
                }
            };
            _area.Locations.Add(pos);
            Locations.Add(new LocationViewModel(pos));
            OnPropertyChanged(nameof(LocationCount));
        }

        [RelayCommand]
        private void RemoveLocation(LocationViewModel? loc) {
            if (loc == null) return;
            _area.Locations.Remove(loc.BackingPosition);
            Locations.Remove(loc);
            OnPropertyChanged(nameof(LocationCount));
        }

        public void ApplyTo() {
            _area.Name = Name;
            foreach (var loc in Locations) {
                loc.ApplyTo();
            }
        }
    }

    public partial class LocationViewModel : ObservableObject {
        private readonly Position _pos;

        [ObservableProperty] private string _cellId;
        [ObservableProperty] private string _x;
        [ObservableProperty] private string _y;
        [ObservableProperty] private string _z;

        public Position BackingPosition => _pos;

        public LocationViewModel(Position pos) {
            _pos = pos;
            _cellId = $"0x{pos.CellId:X8}";
            _x = pos.Frame.Origin.X.ToString("F2");
            _y = pos.Frame.Origin.Y.ToString("F2");
            _z = pos.Frame.Origin.Z.ToString("F2");
        }

        public void ApplyTo() {
            if (uint.TryParse(_cellId.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var cid))
                _pos.CellId = cid;
            if (float.TryParse(_x, out var fx)) _pos.Frame.Origin = new System.Numerics.Vector3(fx, _pos.Frame.Origin.Y, _pos.Frame.Origin.Z);
            if (float.TryParse(_y, out var fy)) _pos.Frame.Origin = new System.Numerics.Vector3(_pos.Frame.Origin.X, fy, _pos.Frame.Origin.Z);
            if (float.TryParse(_z, out var fz)) _pos.Frame.Origin = new System.Numerics.Vector3(_pos.Frame.Origin.X, _pos.Frame.Origin.Y, fz);
        }
    }
}
