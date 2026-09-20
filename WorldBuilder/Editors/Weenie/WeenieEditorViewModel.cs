using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Shared.Models;
using WorldBuilder.Editors.Weenie.Views;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Weenie {

    public partial class WeenieIntRow : ObservableObject {
        public ushort PropertyType { get; }
        public string Label => AceWeeniePropertyLabels.Int(PropertyType);
        public string RowDescription => $"{Label} (type {PropertyType})";
        [ObservableProperty] private string _valueText;

        public WeenieIntRow(ushort propertyType, int value) {
            PropertyType = propertyType;
            _valueText = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public partial class WeenieInt64Row : ObservableObject {
        public ushort PropertyType { get; }
        public string Label => AceWeeniePropertyLabels.Int64(PropertyType);
        public string RowDescription => $"{Label} (type {PropertyType})";
        [ObservableProperty] private string _valueText;

        public WeenieInt64Row(ushort propertyType, long value) {
            PropertyType = propertyType;
            _valueText = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public partial class WeenieBoolRow : ObservableObject {
        public ushort PropertyType { get; }
        public string Label => AceWeeniePropertyLabels.Bool(PropertyType);
        public string RowDescription => $"{Label} (type {PropertyType})";
        [ObservableProperty] private bool _value;

        public WeenieBoolRow(ushort propertyType, bool value) {
            PropertyType = propertyType;
            _value = value;
        }
    }

    public partial class WeenieFloatRow : ObservableObject {
        public ushort PropertyType { get; }
        public string Label => AceWeeniePropertyLabels.Float(PropertyType);
        public string RowDescription => $"{Label} (type {PropertyType})";
        [ObservableProperty] private string _valueText;

        public WeenieFloatRow(ushort propertyType, double value) {
            PropertyType = propertyType;
            _valueText = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public partial class WeenieStringRow : ObservableObject {
        public ushort PropertyType { get; }
        public string Label => AceWeeniePropertyLabels.String(PropertyType);
        public string RowDescription => $"{Label} (type {PropertyType})";
        [ObservableProperty] private string _valueText;

        public WeenieStringRow(ushort propertyType, string value) {
            PropertyType = propertyType;
            _valueText = value ?? "";
        }
    }

    public partial class WeenieDidRow : ObservableObject {
        public ushort PropertyType { get; }
        public string Label => AceWeeniePropertyLabels.DataId(PropertyType);
        public string RowDescription => $"{Label} (type {PropertyType})";
        [ObservableProperty] private string _valueText;

        public WeenieDidRow(ushort propertyType, uint value) {
            PropertyType = propertyType;
            _valueText = "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
        }
    }

    public partial class WeenieIidRow : ObservableObject {
        public ushort PropertyType { get; }
        public string Label => AceWeeniePropertyLabels.InstanceId(PropertyType);
        public string RowDescription => $"{Label} (type {PropertyType})";
        [ObservableProperty] private string _valueText;

        public WeenieIidRow(ushort propertyType, ulong value) {
            PropertyType = propertyType;
            _valueText = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Dropdown item for the "Add property" ComboBox in each property tab.</summary>
    public sealed class PropertyTypeOption {
        public ushort Type { get; }
        public string Name { get; }
        public PropertyTypeOption(ushort type, string name) { Type = type; Name = name; }
        public override string ToString() => $"{Name} ({Type})";
    }

    /// <summary>Picker row for JSON starter templates (built-in + user folders).</summary>
    public sealed class WeenieTemplatePickerItem {
        public WeenieTemplateDefinition Definition { get; }
        public string Title => Definition.Title;
        public string Subtitle => string.IsNullOrEmpty(Definition.Description) ? Definition.Id : Definition.Description!;

        public WeenieTemplatePickerItem(WeenieTemplateDefinition definition) => Definition = definition;
    }

    public partial class WeenieListEntryVm : ObservableObject {
        public uint ClassId { get; }
        public string Name { get; }
        public uint SetupId { get; }
        public string Subtitle => $"WCID {ClassId}  •  Setup 0x{SetupId:X8}";

        public WeenieListEntryVm(uint classId, string name, uint setupId) {
            ClassId = classId;
            Name = name;
            SetupId = setupId;
        }
    }

    /// <summary>Weenie editor: browse weenies from MySQL, edit scalar properties, 3D Setup + icon preview.</summary>
    public partial class WeenieEditorViewModel : ViewModelBase {
        private Project? _project;
        private IDatReaderWriter? _dats;

        public WorldBuilderSettings Settings { get; }

        public WeenieEditorViewModel(WorldBuilderSettings settings) {
            Settings = settings;
        }

        [ObservableProperty] private string _statusText = "Configure the world database in Settings, then search weenies.";
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private ObservableCollection<WeenieListEntryVm> _weenies = new();
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWeenieDetail))]
        private WeenieListEntryVm? _selectedWeenie;
        /// <summary>Raw <c>weenie.type</c> value.</summary>
        [ObservableProperty] private string _weenieTypeText = "1";
        [ObservableProperty] private string _weenieTypeHint = "";
        [ObservableProperty] private uint _previewSetupDid;
        [ObservableProperty] private WriteableBitmap? _iconBitmap;
        [ObservableProperty] private string _complexSummary = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SaveButtonText))]
        [NotifyPropertyChangedFor(nameof(HasWeenieDetail))]
        private bool _isCreatingNew;

        [ObservableProperty] private string _newClassName = "";

        public string SaveButtonText => IsCreatingNew ? "Create in DB" : "Save scalars";
        public bool HasWeenieDetail => SelectedWeenie != null || IsCreatingNew;

        [ObservableProperty] private ObservableCollection<WeenieIntRow> _intRows = new();
        [ObservableProperty] private ObservableCollection<WeenieInt64Row> _int64Rows = new();
        [ObservableProperty] private ObservableCollection<WeenieBoolRow> _boolRows = new();
        [ObservableProperty] private ObservableCollection<WeenieFloatRow> _floatRows = new();
        [ObservableProperty] private ObservableCollection<WeenieStringRow> _stringRows = new();
        [ObservableProperty] private ObservableCollection<WeenieDidRow> _didRows = new();
        [ObservableProperty] private ObservableCollection<WeenieIidRow> _iidRows = new();

        [ObservableProperty] private ObservableCollection<WeenieTemplatePickerItem> _weenieTemplates = new();
        [ObservableProperty] private WeenieTemplatePickerItem? _selectedTemplate;

        // "Add property" picker lists (static, built from property enums)
        public static IReadOnlyList<PropertyTypeOption> AllIntTypes { get; } = BuildOptions<AcePropertyInt>();
        public static IReadOnlyList<PropertyTypeOption> AllInt64Types { get; } = BuildOptions<AcePropertyInt64>();
        public static IReadOnlyList<PropertyTypeOption> AllBoolTypes { get; } = BuildOptions<AcePropertyBool>();
        public static IReadOnlyList<PropertyTypeOption> AllFloatTypes { get; } = BuildOptions<AcePropertyFloat>();
        public static IReadOnlyList<PropertyTypeOption> AllStringTypes { get; } = BuildOptions<AcePropertyString>();
        public static IReadOnlyList<PropertyTypeOption> AllDidTypes { get; } = BuildOptions<AcePropertyDataId>();
        public static IReadOnlyList<PropertyTypeOption> AllIidTypes { get; } = BuildOptions<AcePropertyInstanceId>();

        static IReadOnlyList<PropertyTypeOption> BuildOptions<TEnum>() where TEnum : struct, Enum =>
            Enum.GetValues<TEnum>()
                .Select(v => new PropertyTypeOption(Convert.ToUInt16(v), v.ToString()))
                .Where(p => p.Type != 0)
                .OrderBy(p => p.Name)
                .ToList();

        // Selected "add" item per tab
        [ObservableProperty] private PropertyTypeOption? _selectedNewInt;
        [ObservableProperty] private PropertyTypeOption? _selectedNewInt64;
        [ObservableProperty] private PropertyTypeOption? _selectedNewBool;
        [ObservableProperty] private PropertyTypeOption? _selectedNewFloat;
        [ObservableProperty] private PropertyTypeOption? _selectedNewString;
        [ObservableProperty] private PropertyTypeOption? _selectedNewDid;
        [ObservableProperty] private PropertyTypeOption? _selectedNewIid;

        [RelayCommand] private void AddIntProperty() { AddRow(SelectedNewInt, IntRows, t => new WeenieIntRow(t, 0)); SelectedNewInt = null; }
        [RelayCommand] private void AddInt64Property() { AddRow(SelectedNewInt64, Int64Rows, t => new WeenieInt64Row(t, 0)); SelectedNewInt64 = null; }
        [RelayCommand] private void AddBoolProperty() { AddRow(SelectedNewBool, BoolRows, t => new WeenieBoolRow(t, false)); SelectedNewBool = null; }
        [RelayCommand] private void AddFloatProperty() { AddRow(SelectedNewFloat, FloatRows, t => new WeenieFloatRow(t, 0.0)); SelectedNewFloat = null; }
        [RelayCommand] private void AddStringProperty() { AddRow(SelectedNewString, StringRows, t => new WeenieStringRow(t, "")); SelectedNewString = null; }
        [RelayCommand] private void AddDidProperty() { AddRow(SelectedNewDid, DidRows, t => new WeenieDidRow(t, 0)); SelectedNewDid = null; }
        [RelayCommand] private void AddIidProperty() { AddRow(SelectedNewIid, IidRows, t => new WeenieIidRow(t, 0)); SelectedNewIid = null; }

        void AddRow<TRow>(PropertyTypeOption? pick, ObservableCollection<TRow> rows, Func<ushort, TRow> factory)
            where TRow : ObservableObject {
            if (pick == null) { StatusText = "Pick a property type from the dropdown first."; return; }
            var prop = typeof(TRow).GetProperty("PropertyType");
            if (rows.Any(r => (ushort)prop!.GetValue(r)! == pick.Type)) {
                StatusText = $"{pick.Name} ({pick.Type}) already exists.";
                return;
            }
            rows.Add(factory(pick.Type));
        }

        [RelayCommand] private void RemoveIntRow(WeenieIntRow? r) { if (r != null) IntRows.Remove(r); }
        [RelayCommand] private void RemoveInt64Row(WeenieInt64Row? r) { if (r != null) Int64Rows.Remove(r); }
        [RelayCommand] private void RemoveBoolRow(WeenieBoolRow? r) { if (r != null) BoolRows.Remove(r); }
        [RelayCommand] private void RemoveFloatRow(WeenieFloatRow? r) { if (r != null) FloatRows.Remove(r); }
        [RelayCommand] private void RemoveStringRow(WeenieStringRow? r) { if (r != null) StringRows.Remove(r); }
        [RelayCommand] private void RemoveDidRow(WeenieDidRow? r) { if (r != null) DidRows.Remove(r); }
        [RelayCommand] private void RemoveIidRow(WeenieIidRow? r) { if (r != null) IidRows.Remove(r); }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DatReaderWriter;
            ReloadWeenieTemplates();
        }

        void ReloadWeenieTemplates() {
            WeenieTemplates.Clear();
            SelectedTemplate = null;
            var list = WeenieTemplateCatalog.Load(Settings, _project);
            foreach (var t in list)
                WeenieTemplates.Add(new WeenieTemplatePickerItem(t));
        }

        [RelayCommand]
        private void CreateNew() {
            SelectedWeenie = null;
            ClearDetail();
            IsCreatingNew = true;
            NewClassName = "";

            if (SelectedTemplate != null) {
                ApplyTemplate(SelectedTemplate.Definition, replaceScalars: true);
                StatusText = $"New weenie from template \u201c{SelectedTemplate.Title}\u201d. Enter a class name and save.";
            }
            else {
                StatusText = "New weenie. Enter a class name, configure properties (or pick a template), then save.";
            }
        }

        [RelayCommand]
        private void CancelNew() {
            IsCreatingNew = false;
            NewClassName = "";
            ClearDetail();
            StatusText = "Cancelled new weenie creation.";
        }

        [RelayCommand]
        private void ApplyTemplateMerge() {
            if ((SelectedWeenie == null && !IsCreatingNew) || SelectedTemplate == null) {
                StatusText = "Select a weenie (or create new) and a template.";
                return;
            }
            ApplyTemplate(SelectedTemplate.Definition, replaceScalars: false);
            var mergeTarget = IsCreatingNew ? "new weenie" : $"WCID {SelectedWeenie!.ClassId}";
            StatusText = $"Merged template \u201c{SelectedTemplate.Title}\u201d into {mergeTarget} (unsaved).";
        }

        [RelayCommand]
        private void ApplyTemplateReplaceScalars() {
            if ((SelectedWeenie == null && !IsCreatingNew) || SelectedTemplate == null) {
                StatusText = "Select a weenie (or create new) and a template.";
                return;
            }
            ApplyTemplate(SelectedTemplate.Definition, replaceScalars: true);
            var replaceTarget = IsCreatingNew ? "new weenie" : $"WCID {SelectedWeenie!.ClassId}";
            StatusText = $"Replaced scalar properties with \u201c{SelectedTemplate.Title}\u201d on {replaceTarget} (unsaved).";
        }

        void ApplyTemplate(WeenieTemplateDefinition t, bool replaceScalars) {
            if (replaceScalars) {
                IntRows.Clear();
                Int64Rows.Clear();
                BoolRows.Clear();
                FloatRows.Clear();
                StringRows.Clear();
                DidRows.Clear();
                IidRows.Clear();
                WeenieTypeText = t.WeenieType.ToString(CultureInfo.InvariantCulture);
            }

            foreach (var (type, value) in t.Ints)
                UpsertIntRow(type, value);
            foreach (var (type, value) in t.Int64s)
                UpsertInt64Row(type, value);
            foreach (var (type, value) in t.Bools)
                UpsertBoolRow(type, value);
            foreach (var (type, value) in t.Floats)
                UpsertFloatRow(type, value);
            foreach (var (type, value) in t.Strings)
                UpsertStringRow(type, value);
            foreach (var (type, value) in t.DataIds)
                UpsertDidRow(type, value);
            foreach (var (type, value) in t.InstanceIds)
                UpsertIidRow(type, value);

            if (replaceScalars) {
                if (Enum.IsDefined(typeof(AceWeenieType), t.WeenieType))
                    WeenieTypeHint = AceWeeniePropertyLabels.WeenieType(t.WeenieType);
                else
                    WeenieTypeHint = "Custom / unknown type id";
            }

            RefreshPreviewFromScalarRows();
        }

        void UpsertIntRow(ushort type, int value) {
            var row = IntRows.FirstOrDefault(r => r.PropertyType == type);
            if (row != null)
                row.ValueText = value.ToString(CultureInfo.InvariantCulture);
            else
                IntRows.Add(new WeenieIntRow(type, value));
        }

        void UpsertInt64Row(ushort type, long value) {
            var row = Int64Rows.FirstOrDefault(r => r.PropertyType == type);
            if (row != null)
                row.ValueText = value.ToString(CultureInfo.InvariantCulture);
            else
                Int64Rows.Add(new WeenieInt64Row(type, value));
        }

        void UpsertBoolRow(ushort type, bool value) {
            var row = BoolRows.FirstOrDefault(r => r.PropertyType == type);
            if (row != null)
                row.Value = value;
            else
                BoolRows.Add(new WeenieBoolRow(type, value));
        }

        void UpsertFloatRow(ushort type, double value) {
            var row = FloatRows.FirstOrDefault(r => r.PropertyType == type);
            if (row != null)
                row.ValueText = value.ToString(CultureInfo.InvariantCulture);
            else
                FloatRows.Add(new WeenieFloatRow(type, value));
        }

        void UpsertStringRow(ushort type, string value) {
            var row = StringRows.FirstOrDefault(r => r.PropertyType == type);
            if (row != null)
                row.ValueText = value;
            else
                StringRows.Add(new WeenieStringRow(type, value));
        }

        void UpsertDidRow(ushort type, uint value) {
            var row = DidRows.FirstOrDefault(r => r.PropertyType == type);
            if (row != null)
                row.ValueText = "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
            else
                DidRows.Add(new WeenieDidRow(type, value));
        }

        void UpsertIidRow(ushort type, ulong value) {
            var row = IidRows.FirstOrDefault(r => r.PropertyType == type);
            if (row != null)
                row.ValueText = value.ToString(CultureInfo.InvariantCulture);
            else
                IidRows.Add(new WeenieIidRow(type, value));
        }

        void RefreshPreviewFromScalarRows() {
            var setupRow = DidRows.FirstOrDefault(r => r.PropertyType == (ushort)AcePropertyDataId.Setup);
            if (setupRow != null && TryParseUInt(setupRow.ValueText, out var setupId))
                PreviewSetupDid = setupId;
            else
                PreviewSetupDid = 0;

            var iconRow = DidRows.FirstOrDefault(r => r.PropertyType == (ushort)AcePropertyDataId.Icon);
            if (iconRow != null && TryParseUInt(iconRow.ValueText, out var iconId))
                LoadIcon(iconId);
            else
                IconBitmap = null;
        }

        [RelayCommand]
        private async Task SearchWeeniesAsync() {
            if (Settings?.AceDbConnection == null) {
                StatusText = "Configure the world database in Settings first.";
                return;
            }

            IsBusy = true;
            StatusText = "Loading weenie list…";
            Weenies.Clear();
            SelectedWeenie = null;
            ClearDetail();

            try {
                var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
                var list = await connector.GetWeenieNamesAsync(search, limit: 2500);

                foreach (var e in list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    Weenies.Add(new WeenieListEntryVm(e.ClassId, e.Name, e.SetupId));

                StatusText = list.Count == 0
                    ? "No weenies matched."
                    : $"{list.Count} weenies. Select one to edit scalar properties.";
            }
            catch (Exception ex) {
                StatusText = "Error: " + ex.Message;
            }
            finally {
                IsBusy = false;
            }
        }

        partial void OnSelectedWeenieChanged(WeenieListEntryVm? value) {
            if (value != null)
                IsCreatingNew = false;
            _ = LoadDetailAsync(value);
        }

        async Task LoadDetailAsync(WeenieListEntryVm? entry) {
            ClearDetail();
            if (entry == null) return;
            if (Settings?.AceDbConnection == null) return;

            StatusText = $"Loading WCID {entry.ClassId}…";

            try {
                var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var snap = await connector.LoadWeenieSnapshotAsync(entry.ClassId);
                if (snap == null) {
                    StatusText = $"No weenie row for WCID {entry.ClassId}.";
                    return;
                }

                ApplySnapshotToUi(snap);
                StatusText = $"Editing WCID {entry.ClassId} — {entry.Name}";
            }
            catch (Exception ex) {
                StatusText = "Load failed: " + ex.Message;
            }
        }

        void ApplySnapshotToUi(AceWeenieSnapshot snap) {
            WeenieTypeText = snap.WeenieType.ToString(CultureInfo.InvariantCulture);
            if (Enum.IsDefined(typeof(AceWeenieType), snap.WeenieType))
                WeenieTypeHint = AceWeeniePropertyLabels.WeenieType(snap.WeenieType);
            else
                WeenieTypeHint = "Custom / unknown type id";

            PreviewSetupDid = snap.SetupDid;
            LoadIcon(snap.IconDid);

            IntRows.Clear();
            foreach (var r in snap.Ints.OrderBy(x => x.Type))
                IntRows.Add(new WeenieIntRow(r.Type, r.Value));

            Int64Rows.Clear();
            foreach (var r in snap.Int64s.OrderBy(x => x.Type))
                Int64Rows.Add(new WeenieInt64Row(r.Type, r.Value));

            BoolRows.Clear();
            foreach (var r in snap.Bools.OrderBy(x => x.Type))
                BoolRows.Add(new WeenieBoolRow(r.Type, r.Value));

            FloatRows.Clear();
            foreach (var r in snap.Floats.OrderBy(x => x.Type))
                FloatRows.Add(new WeenieFloatRow(r.Type, r.Value));

            StringRows.Clear();
            foreach (var r in snap.Strings.OrderBy(x => x.Type))
                StringRows.Add(new WeenieStringRow(r.Type, r.Value));

            DidRows.Clear();
            foreach (var r in snap.DataIds.OrderBy(x => x.Type))
                DidRows.Add(new WeenieDidRow(r.Type, r.Value));

            IidRows.Clear();
            foreach (var r in snap.InstanceIds.OrderBy(x => x.Type))
                IidRows.Add(new WeenieIidRow(r.Type, r.Value));

            ComplexSummary =
                $"Spell book: {snap.SpellBookCount}  •  Create list: {snap.CreateListCount}  •  Emotes: {snap.EmoteCount}  •  Book pages: {snap.BookCount}\n" +
                $"Positions: {snap.PositionCount}  •  Attributes: {snap.AttributeCount}  •  Vitals: {snap.Attribute2ndCount}  •  Skills: {snap.SkillCount}";
        }

        void LoadIcon(uint iconDid) {
            IconBitmap = null;
            if (iconDid == 0 || _dats == null) return;
            var localDats = _dats;
            var id = iconDid;
            _ = Task.Run(() => {
                var bmp = DatIconLoader.LoadIcon(localDats, id, 64);
                Dispatcher.UIThread.Post(() => IconBitmap = bmp);
            });
        }

        void ClearDetail() {
            WeenieTypeText = "1";
            WeenieTypeHint = "";
            PreviewSetupDid = 0;
            IconBitmap = null;
            ComplexSummary = "";
            IntRows.Clear();
            Int64Rows.Clear();
            BoolRows.Clear();
            FloatRows.Clear();
            StringRows.Clear();
            DidRows.Clear();
            IidRows.Clear();
        }

        [RelayCommand]
        private async Task SaveAsync() {
            if (IsCreatingNew) {
                await InsertNewWeenieAsync();
                return;
            }

            if (SelectedWeenie == null) {
                StatusText = "Select a weenie first.";
                return;
            }
            if (Settings?.AceDbConnection == null) {
                StatusText = "Configure the world database in Settings first.";
                return;
            }

            if (!TryBuildSnapshot(SelectedWeenie.ClassId, out var snap, out var err)) {
                StatusText = err;
                return;
            }

            IsBusy = true;
            StatusText = "Saving\u2026";
            try {
                var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var ok = await connector.SaveWeenieScalarsAsync(snap);
                StatusText = ok
                    ? $"Saved WCID {SelectedWeenie.ClassId} (scalar tables only)."
                    : "Save failed (check DB permissions and that the weenie exists).";
            }
            catch (Exception ex) {
                StatusText = "Save error: " + ex.Message;
            }
            finally {
                IsBusy = false;
            }
        }

        async Task InsertNewWeenieAsync() {
            if (string.IsNullOrWhiteSpace(NewClassName)) {
                StatusText = "Enter a class name for the new weenie.";
                return;
            }
            if (Settings?.AceDbConnection == null) {
                StatusText = "Configure the world database in Settings first.";
                return;
            }
            if (!TryBuildSnapshot(0, out var snap, out var err)) {
                StatusText = err;
                return;
            }

            IsBusy = true;
            StatusText = "Creating new weenie\u2026";
            try {
                var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var newId = await connector.InsertWeenieAsync(NewClassName.Trim(), snap);
                if (newId == 0) {
                    StatusText = "Failed to create weenie (check DB permissions).";
                    return;
                }

                IsCreatingNew = false;
                NewClassName = "";
                StatusText = $"Created WCID {newId}. Refreshing list\u2026";

                await SearchWeeniesAsync();
                var created = Weenies.FirstOrDefault(w => w.ClassId == newId);
                if (created != null)
                    SelectedWeenie = created;
            }
            catch (Exception ex) {
                StatusText = "Create error: " + ex.Message;
            }
            finally {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RevertAsync() {
            if (SelectedWeenie != null)
                await LoadDetailAsync(SelectedWeenie);
        }

        [RelayCommand]
        private Task BrowseDidAsync(WeenieDidRow? row) {
            // TODO: WeenieDidPickerWindow / WeenieDidPickerViewModel not yet implemented
            StatusText = "DID picker not yet available. Edit the hex value directly.";
            return Task.CompletedTask;
        }

        bool TryBuildSnapshot(uint classId, out AceWeenieSnapshot snap, out string error) {
            snap = new AceWeenieSnapshot { ClassId = classId };
            error = "";
            if (!uint.TryParse(WeenieTypeText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wt)) {
                error = "Weenie type must be a valid unsigned integer.";
                return false;
            }
            snap.WeenieType = wt;

            foreach (var row in IntRows) {
                if (!int.TryParse(row.ValueText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) {
                    error = $"Invalid int value for {row.Label} (type {row.PropertyType}).";
                    return false;
                }
                snap.Ints.Add(new AceWeenieRowInt { Type = row.PropertyType, Value = v });
            }

            foreach (var row in Int64Rows) {
                if (!long.TryParse(row.ValueText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) {
                    error = $"Invalid int64 for {row.Label}.";
                    return false;
                }
                snap.Int64s.Add(new AceWeenieRowInt64 { Type = row.PropertyType, Value = v });
            }

            foreach (var row in BoolRows)
                snap.Bools.Add(new AceWeenieRowBool { Type = row.PropertyType, Value = row.Value });

            foreach (var row in FloatRows) {
                if (!double.TryParse(row.ValueText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) {
                    error = $"Invalid float for {row.Label}.";
                    return false;
                }
                snap.Floats.Add(new AceWeenieRowFloat { Type = row.PropertyType, Value = v });
            }

            foreach (var row in StringRows)
                snap.Strings.Add(new AceWeenieRowString { Type = row.PropertyType, Value = row.ValueText ?? "" });

            foreach (var row in DidRows) {
                if (!TryParseUInt(row.ValueText, out var v)) {
                    error = $"Invalid DID for {row.Label} (use decimal or 0x hex).";
                    return false;
                }
                snap.DataIds.Add(new AceWeenieRowDid { Type = row.PropertyType, Value = v });
            }

            foreach (var row in IidRows) {
                if (!ulong.TryParse(row.ValueText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) {
                    error = $"Invalid IID for {row.Label}.";
                    return false;
                }
                snap.InstanceIds.Add(new AceWeenieRowIid { Type = row.PropertyType, Value = v });
            }

            return true;
        }

        static bool TryParseUInt(string s, out uint v) {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }
    }
}
