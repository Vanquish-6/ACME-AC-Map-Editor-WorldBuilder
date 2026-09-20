using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Spell {
    public partial class SpellEditorViewModel : ViewModelBase {
        private IDatReaderWriter? _dats;
        private Project? _project;
        private PortalDatDocument? _portalDoc;
        private SpellDbDocument? _spellDbDoc;
        private SpellTable? _spellTable;
        private Dictionary<uint, SpellBase>? _allSpells;
        private Dictionary<uint, SpellRecord> _dbSpellCache = new();
        private SpellComponentTable? _componentTable;
        private const uint SpellTableId = 0x0E00000E;
        private const int SpellPageSize = 500;
        private SpellDetailViewModel? _activeDetail;
        private Action? _toastAction;
        private CancellationTokenSource? _toastCts;
        private uint? _lastCreatedSpellId;
        private readonly Stack<SpellUndoAction> _undoStack = new();
        private SpellBase? _baselineSpell;
        private bool _isSelectionGuardActive;
        private SpellListItem? _pendingSpellSelection;

        [ObservableProperty] private string _statusText = "No spells loaded";
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private MagicSchool? _filterSchool;
        [ObservableProperty] private SpellType? _filterSpellType;
        [ObservableProperty] private ObservableCollection<SpellListItem> _spells = new();
        [ObservableProperty] private SpellListItem? _selectedSpell;
        [ObservableProperty] private SpellDetailViewModel? _selectedDetail;
        [ObservableProperty] private int _totalSpellCount;
        [ObservableProperty] private int _filteredSpellCount;
        [ObservableProperty] private bool _saveToDb;
        [ObservableProperty] private int _visibleLimit = SpellPageSize;
        [ObservableProperty] private bool _hasMoreResults;
        [ObservableProperty] private SpellPresetOption _selectedPresetOption;
        [ObservableProperty] private bool _newestFirst = true;
        [ObservableProperty] private string _filterResultText = "";
        [ObservableProperty] private bool _showOnboarding = true;
        [ObservableProperty] private string _toastMessage = "";
        [ObservableProperty] private string _toastActionLabel = "";
        [ObservableProperty] private bool _hasToastAction;
        [ObservableProperty] private bool _isToastVisible;
        [ObservableProperty] private string _validationMessage = "";
        [ObservableProperty] private bool _hasBlockingIssues;
        [ObservableProperty] private bool _canSave = true;
        [ObservableProperty] private bool _canGoToLastCreatedSpell;
        [ObservableProperty] private string _presetPreviewText = "";
        [ObservableProperty] private bool _hasUnsavedChanges;
        [ObservableProperty] private ObservableCollection<string> _changedFields = new();
        [ObservableProperty] private bool _hasUndoAction;

        public IReadOnlyList<MagicSchool?> SchoolOptions { get; } = new List<MagicSchool?> {
            null, MagicSchool.WarMagic, MagicSchool.LifeMagic,
            MagicSchool.ItemEnchantment, MagicSchool.CreatureEnchantment, MagicSchool.VoidMagic,
        };

        public IReadOnlyList<SpellType?> SpellTypeOptions { get; } = new List<SpellType?> {
            null, SpellType.Enchantment, SpellType.Projectile, SpellType.Boost,
            SpellType.Transfer, SpellType.PortalLink, SpellType.PortalRecall,
            SpellType.PortalSummon, SpellType.PortalSending, SpellType.Dispel,
            SpellType.LifeProjectile, SpellType.FellowBoost, SpellType.FellowEnchantment,
            SpellType.FellowPortalSending, SpellType.FellowDispel, SpellType.EnchantmentProjectile,
        };
        public IReadOnlyList<SpellPresetOption> NewSpellPresetOptions { get; } = new List<SpellPresetOption> {
            new(SpellPresetKind.Blank, "Blank", "Creates an empty shell with only a generated name."),
            new(SpellPresetKind.ProjectileStarter, "Basic Bolt", "War projectile starter with sensible mana/range defaults."),
            new(SpellPresetKind.EnchantmentStarter, "Basic Buff", "Item enchantment starter with default duration and tuning."),
            new(SpellPresetKind.PortalStarter, "Basic Portal", "Portal summon starter with basic lifetime and mana values."),
        };

        public WorldBuilderSettings Settings { get; }

        public SpellEditorViewModel(WorldBuilderSettings settings) {
            Settings = settings;
            _selectedPresetOption = NewSpellPresetOptions[0];
            PresetPreviewText = _selectedPresetOption.Preview;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _portalDoc = project.DocumentManager.GetOrCreateDocumentAsync<PortalDatDocument>(PortalDatDocument.DocumentId).Result;
            _spellDbDoc = project.DocumentManager.GetOrCreateDocumentAsync<SpellDbDocument>(SpellDbDocument.DocumentId).Result;
            LoadSpells();
        }

        private void LoadSpells() {
            if (_dats == null) return;

            if (_portalDoc != null && _portalDoc.TryGetEntry<SpellTable>(SpellTableId, out var docTable) && docTable != null) {
                _spellTable = docTable;
            }
            else if (!_dats.TryGet<SpellTable>(SpellTableId, out var datTable)) {
                StatusText = "Failed to load SpellTable from DAT";
                return;
            }
            else {
                _spellTable = datTable;
            }

            _allSpells = _spellTable.Spells;
            TotalSpellCount = _allSpells.Count;

            _dats.TryGet<SpellComponentTable>(0x0E00000F, out var compTable);
            _componentTable = compTable;

            ApplyFilter();
            StatusText = $"Loaded {TotalSpellCount} spells, {_componentTable?.Components.Count ?? 0} components";
        }

        partial void OnSearchTextChanged(string value) {
            VisibleLimit = SpellPageSize;
            ApplyFilter();
        }
        partial void OnFilterSchoolChanged(MagicSchool? value) {
            VisibleLimit = SpellPageSize;
            ApplyFilter();
        }
        partial void OnFilterSpellTypeChanged(SpellType? value) {
            VisibleLimit = SpellPageSize;
            ApplyFilter();
        }
        partial void OnNewestFirstChanged(bool value) => ApplyFilter();
        partial void OnSelectedPresetOptionChanged(SpellPresetOption value) => PresetPreviewText = value.Preview;
        partial void OnSaveToDbChanged(bool value) {
            if (value) {
                ShowToast("Save to DB is enabled. Saving can overwrite spell rows in the world database.");
            }
        }
        partial void OnSelectedSpellChanged(SpellListItem? value) {
            if (!_isSelectionGuardActive && value != null && _activeDetail != null && HasUnsavedChanges && value.Id != _activeDetail.SpellId) {
                _pendingSpellSelection = value;
                _isSelectionGuardActive = true;
                SelectedSpell = Spells.FirstOrDefault(s => s.Id == _activeDetail.SpellId);
                _isSelectionGuardActive = false;
                ShowToast("You have unsaved changes on this spell.", "Discard and switch", DiscardAndSwitchSelection);
                return;
            }

            if (_activeDetail != null) {
                _activeDetail.PropertyChanged -= SelectedDetailOnPropertyChanged;
            }

            if (value != null && _allSpells != null && _allSpells.TryGetValue(value.Id, out var spell) && _dats != null) {
                LoadDetail(value.Id, spell);
                UpdateValidationStatus();
            }
            else {
                _activeDetail = null;
                SelectedDetail = null;
                ValidationMessage = "";
                HasBlockingIssues = false;
                HasUnsavedChanges = false;
                ChangedFields = new ObservableCollection<string>();
            }
        }

        private async Task LoadDbSpellAsync(SpellDetailViewModel detail, uint spellId) {
            if (Settings?.AceDbConnection == null)
                return;

            try {
                var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var currentId = detail.SpellId;

                var dbSpell = await connector.GetSpellAsync(spellId);

                if (dbSpell != null && currentId == detail.SpellId) {
                    _dbSpellCache[spellId] = dbSpell;
                    detail.LoadFromDb(dbSpell);
                }
            }
            catch {
            }
        }

        private void ApplyFilter() {
            if (_allSpells == null) return;

            var query = SearchText?.Trim() ?? "";
            uint searchId = 0;
            bool hasIdSearch = query.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(query.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out searchId);

            var filteredQuery = _allSpells
                .Where(kvp => {
                    if (hasIdSearch) return kvp.Key == searchId;
                    if (!string.IsNullOrEmpty(query) &&
                        !(kvp.Value.Name?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (FilterSchool.HasValue && kvp.Value.School != FilterSchool.Value) return false;
                    if (FilterSpellType.HasValue && kvp.Value.MetaSpellType != FilterSpellType.Value) return false;
                    return true;
                });

            var sorted = NewestFirst
                ? filteredQuery.OrderByDescending(kvp => kvp.Key)
                : filteredQuery.OrderBy(kvp => kvp.Value.Name?.ToString() ?? "").ThenBy(kvp => kvp.Key);

            var filtered = sorted
                .Select(kvp => new SpellListItem(kvp.Key, kvp.Value))
                .ToList();

            HasMoreResults = filtered.Count > VisibleLimit;
            Spells = new ObservableCollection<SpellListItem>(filtered.Take(VisibleLimit));
            FilteredSpellCount = filtered.Count;
            FilterResultText = $"Showing {Spells.Count} of {FilteredSpellCount} filtered ({TotalSpellCount} total)";
        }

        [RelayCommand]
        private void ClearFilters() {
            SearchText = "";
            FilterSchool = null;
            FilterSpellType = null;
            NewestFirst = true;
        }

        [RelayCommand]
        private void LoadMoreSpells() {
            if (!HasMoreResults) return;
            VisibleLimit += SpellPageSize;
            ApplyFilter();
            ShowToast($"Loaded more spells ({Spells.Count}/{FilteredSpellCount}).");
        }

        [RelayCommand]
        private void DismissOnboarding() {
            ShowOnboarding = false;
        }

        [RelayCommand]
        private void AddSpell() {
            if (_spellTable == null || _portalDoc == null || _allSpells == null) return;

            var nextId = GetNextSpellId();
            var selectedPreset = SelectedPresetOption?.Kind ?? SpellPresetKind.Blank;

            var newSpell = new SpellBase { Name = $"New Spell {nextId}", Description = "" };
            ApplyPreset(newSpell, selectedPreset);
            _allSpells[nextId] = newSpell;
            MarkSpellTableDirty();
            PushUndo(new SpellUndoAction(SpellUndoActionKind.RemoveCreated, nextId, null));

            TotalSpellCount = _allSpells.Count;
            _lastCreatedSpellId = nextId;
            CanGoToLastCreatedSpell = true;
            FocusSpellById(nextId);

            StatusText = $"Added spell 0x{nextId:X4}. Search was set to that ID so it is easy to find; export writes DATs.";
            ShowToast($"Created spell 0x{nextId:X4}.", "Go to spell", GoToLastCreatedSpell);
        }

        [RelayCommand]
        private void CopySpell() {
            if (SelectedDetail == null || _spellTable == null || _portalDoc == null || _allSpells == null) return;

            var nextId = GetNextSpellId();
            var copy = new SpellBase();
            SelectedDetail.ApplyTo(copy);

            if (SelectedDetail.DbSpell != null && _spellDbDoc != null) {
                var clone = CloneDbSpellRecord(SelectedDetail.DbSpell, nextId);
                _dbSpellCache[nextId] = clone;
                _spellDbDoc.Set(nextId, clone);
            }

            var sourceName = SelectedDetail.Name?.Trim() ?? "";
            copy.Name = string.IsNullOrWhiteSpace(sourceName)
                ? $"New Spell {nextId}"
                : $"{sourceName} (Copy)";

            _allSpells[nextId] = copy;
            MarkSpellTableDirty();
            PushUndo(new SpellUndoAction(SpellUndoActionKind.RemoveCreated, nextId, null));

            TotalSpellCount = _allSpells.Count;
            _lastCreatedSpellId = nextId;
            CanGoToLastCreatedSpell = true;
            FocusSpellById(nextId);

            StatusText = $"Copied spell to 0x{nextId:X4} and marked project data dirty. Export writes DATs.";
            ShowToast($"Copied to 0x{nextId:X4}.", "Go to spell", GoToLastCreatedSpell);
        }

        [RelayCommand]
        private void ApplyPresetToSelected() {
            if (SelectedDetail == null || _dats == null || _allSpells == null) return;
            var spell = new SpellBase();
            SelectedDetail.ApplyTo(spell);
            var selectedPreset = SelectedPresetOption?.Kind ?? SpellPresetKind.Blank;
            ApplyPreset(spell, selectedPreset);
            LoadDetail(SelectedDetail.SpellId, spell);
            StatusText = $"Applied {SelectedPresetOption?.Name ?? "preset"} to selected spell. Save when ready.";
            ShowToast($"Applied {SelectedPresetOption?.Name ?? "preset"}.");
        }

        [RelayCommand]
        private void DeleteSpell() {
            if (SelectedDetail == null || _spellTable == null || _portalDoc == null || _allSpells == null) return;

            var id = SelectedDetail.SpellId;
            if (!_allSpells.TryGetValue(id, out var existing)) return;
            var deletedSnapshot = CloneSpell(existing);
            if (!_allSpells.Remove(id)) return;

            MarkSpellTableDirty();
            PushUndo(new SpellUndoAction(SpellUndoActionKind.RestoreDeleted, id, deletedSnapshot));

            SelectedDetail = null;
            TotalSpellCount = _allSpells.Count;
            ApplyFilter();
            StatusText = $"Deleted spell #{id}. Use File > Export to write DATs.";
            ShowToast($"Deleted spell 0x{id:X4}.");
        }

        [RelayCommand]
        private async Task SaveSpell() {
            if (SelectedDetail == null || _spellTable == null || _portalDoc == null || _allSpells == null) return;
            UpdateValidationStatus();
            if (HasBlockingIssues) {
                ShowToast("Fix validation issues before saving.");
                return;
            }

            var detail = SelectedDetail;
            var id = detail.SpellId;

            if (!_allSpells.TryGetValue(id, out var spell)) return;

            detail.ApplyTo(spell);

            MarkSpellTableDirty();
            _baselineSpell = CloneSpell(spell);
            RefreshDirtyState();

            var existing = Spells.FirstOrDefault(s => s.Id == id);
            if (existing != null) {
                var index = Spells.IndexOf(existing);
                Spells[index] = new SpellListItem(id, spell);
            }

            if (_spellDbDoc != null) {
                var db = detail.DbSpell ?? new SpellRecord { Id = id };

                detail.ApplyDbTo(db);

                _spellDbDoc.Set(id, db);

                detail.DbSpell = db;
                _dbSpellCache.Remove(id);

                if (SaveToDb && Settings?.AceDbConnection != null) {
                    try {
                        var aceSettings = Settings.AceDbConnection.ToAceDbSettings();
                        using var connector = new AceDbConnector(aceSettings);
                        var success = await connector.SaveSpellAsync(db);

                        if (!success) {
                            StatusText = $"Saved locally, but DB save failed for spell #{id}";
                            return;
                        }
                    }
                    catch {
                        StatusText = $"Saved locally, but failed to save to DB for spell #{id}";
                        return;
                    }
                }
            }

            StatusText = $"Saved spell #{id}: {spell.Name} to project. Use File > Export to write DATs.";
            ShowToast($"Saved spell 0x{id:X4}. Export when ready.");
        }

        [RelayCommand]
        private void GoToLastCreatedSpell() {
            if (_lastCreatedSpellId == null) return;
            FocusSpellById(_lastCreatedSpellId.Value);
        }

        [RelayCommand]
        private void DismissToast() {
            IsToastVisible = false;
            _toastAction = null;
            ToastActionLabel = "";
            HasToastAction = false;
        }

        [RelayCommand]
        private void ExecuteToastAction() {
            _toastAction?.Invoke();
            DismissToast();
        }

        [RelayCommand]
        private void DiscardCurrentEdits() {
            if (SelectedSpell == null || _allSpells == null || !_allSpells.TryGetValue(SelectedSpell.Id, out var sourceSpell)) return;
            LoadDetail(SelectedSpell.Id, sourceSpell);
            ShowToast("Discarded unsaved changes.");
        }

        [RelayCommand]
        private void UndoLastAction() {
            if (_undoStack.Count == 0 || _allSpells == null) return;

            var action = _undoStack.Pop();
            HasUndoAction = _undoStack.Count > 0;

            switch (action.Kind) {
                case SpellUndoActionKind.RemoveCreated:
                    if (_allSpells.Remove(action.SpellId)) {
                        MarkSpellTableDirty();
                        TotalSpellCount = _allSpells.Count;
                        ApplyFilter();
                        ShowToast($"Undid create/copy for 0x{action.SpellId:X4}.");
                    }
                    break;
                case SpellUndoActionKind.RestoreDeleted:
                    if (action.Snapshot != null) {
                        _allSpells[action.SpellId] = CloneSpell(action.Snapshot);
                        MarkSpellTableDirty();
                        TotalSpellCount = _allSpells.Count;
                        FocusSpellById(action.SpellId);
                        ShowToast($"Restored deleted spell 0x{action.SpellId:X4}.");
                    }
                    break;
            }
        }

        private uint GetNextSpellId() {
            if (_allSpells == null || _allSpells.Count == 0) return 1;
            return _allSpells.Keys.Max() + 1;
        }

        private void FocusSpellById(uint id) {
            SearchText = $"0x{id:X4}";
            FilterSchool = null;
            FilterSpellType = null;
            ApplyFilter();
            SelectedSpell = Spells.FirstOrDefault(s => s.Id == id);
        }

        private void MarkSpellTableDirty() {
            if (_portalDoc == null || _spellTable == null) return;
            _portalDoc.SetEntry(SpellTableId, _spellTable);
        }

        private void LoadDetail(uint spellId, SpellBase spell) {
            if (_dats == null || _allSpells == null) return;
            if (_activeDetail != null) {
                _activeDetail.PropertyChanged -= SelectedDetailOnPropertyChanged;
            }
            var detail = new SpellDetailViewModel(spellId, spell, _componentTable, _allSpells, _dats);
            _activeDetail = detail;
            _activeDetail.PropertyChanged += SelectedDetailOnPropertyChanged;
            SelectedDetail = detail;

            if (_dbSpellCache.TryGetValue(spellId, out var spellCache)) {
                detail.LoadFromDb(spellCache);
            }
            else if (_spellDbDoc != null && _spellDbDoc.TryGet(spellId, out var localDb) && localDb != null) {
                detail.LoadFromDb(localDb);
            }
            else {
                _ = LoadDbSpellAsync(detail, spellId);
            }

            _baselineSpell = CloneSpell(spell);
            RefreshDirtyState();
        }

        private static void ApplyPreset(SpellBase spell, SpellPresetKind preset) {
            switch (preset) {
                case SpellPresetKind.ProjectileStarter:
                    spell.School = MagicSchool.WarMagic;
                    spell.MetaSpellType = SpellType.Projectile;
                    spell.Power = 100;
                    spell.BaseMana = 20;
                    spell.BaseRangeConstant = 10f;
                    spell.BaseRangeMod = 0.35f;
                    spell.Description = "Starter projectile preset.";
                    break;
                case SpellPresetKind.EnchantmentStarter:
                    spell.School = MagicSchool.ItemEnchantment;
                    spell.MetaSpellType = SpellType.Enchantment;
                    spell.Power = 120;
                    spell.BaseMana = 25;
                    spell.Duration = 90;
                    spell.DegradeModifier = 0f;
                    spell.Description = "Starter enchantment preset.";
                    break;
                case SpellPresetKind.PortalStarter:
                    spell.School = MagicSchool.LifeMagic;
                    spell.MetaSpellType = SpellType.PortalSummon;
                    spell.BaseMana = 60;
                    spell.PortalLifetime = 60;
                    spell.BaseRangeConstant = 0f;
                    spell.BaseRangeMod = 0f;
                    spell.Description = "Starter portal preset.";
                    break;
                case SpellPresetKind.Blank:
                default:
                    break;
            }
        }

        private void SelectedDetailOnPropertyChanged(object? sender, PropertyChangedEventArgs e) {
            UpdateValidationStatus();
            RefreshDirtyState();
        }

        private void UpdateValidationStatus() {
            if (SelectedDetail == null) {
                ValidationMessage = "";
                HasBlockingIssues = false;
                CanSave = true;
                return;
            }

            var issues = new List<string>();
            if (string.IsNullOrWhiteSpace(SelectedDetail.Name)) {
                issues.Add("Name is required.");
            }

            if (SelectedDetail.BaseMana > 5000) {
                issues.Add("Base Mana is unusually high (over 5000).");
            }

            if (SelectedDetail.ComponentSlots.Count > 8) {
                issues.Add("A spell can have at most 8 components.");
            }

            HasBlockingIssues = issues.Any(i => i.Contains("required", StringComparison.OrdinalIgnoreCase) || i.Contains("at most", StringComparison.OrdinalIgnoreCase));
            ValidationMessage = issues.Count == 0 ? "Looks good." : string.Join(" ", issues);
            CanSave = !HasBlockingIssues;
        }

        private void RefreshDirtyState() {
            if (SelectedDetail == null || _baselineSpell == null) {
                HasUnsavedChanges = false;
                ChangedFields = new ObservableCollection<string>();
                return;
            }

            var draft = new SpellBase();
            SelectedDetail.ApplyTo(draft);
            var changes = GetChangedFields(_baselineSpell, draft);
            ChangedFields = new ObservableCollection<string>(changes);
            HasUnsavedChanges = changes.Count > 0;
        }

        private static List<string> GetChangedFields(SpellBase baseline, SpellBase draft) {
            var changes = new List<string>();
            static bool Diff<T>(T a, T b) where T : notnull => !EqualityComparer<T>.Default.Equals(a, b);

            if ((baseline.Name?.ToString() ?? "") != (draft.Name?.ToString() ?? "")) changes.Add("Name");
            if ((baseline.Description?.ToString() ?? "") != (draft.Description?.ToString() ?? "")) changes.Add("Description");
            if (Diff(baseline.School, draft.School)) changes.Add("School");
            if (Diff(baseline.MetaSpellType, draft.MetaSpellType)) changes.Add("Spell Type");
            if (Diff(baseline.BaseMana, draft.BaseMana)) changes.Add("Base Mana");
            if (Diff(baseline.Power, draft.Power)) changes.Add("Power");
            if (Diff(baseline.Icon, draft.Icon)) changes.Add("Icon");
            if (Diff(baseline.Duration, draft.Duration)) changes.Add("Duration");
            if (Diff(baseline.PortalLifetime, draft.PortalLifetime)) changes.Add("Portal Lifetime");
            if (Diff(baseline.ManaMod, draft.ManaMod)) changes.Add("Mana Mod");
            if (Diff(baseline.DisplayOrder, draft.DisplayOrder)) changes.Add("Display Order");

            var baseComponents = baseline.Components ?? new List<uint>();
            var draftComponents = draft.Components ?? new List<uint>();
            if (!baseComponents.SequenceEqual(draftComponents)) {
                changes.Add("Components");
            }

            return changes;
        }

        private static SpellBase CloneSpell(SpellBase source) {
            return new SpellBase {
                Name = source.Name?.ToString() ?? "",
                Description = source.Description?.ToString() ?? "",
                School = source.School,
                MetaSpellType = source.MetaSpellType,
                Category = source.Category,
                Icon = source.Icon,
                BaseMana = source.BaseMana,
                Power = source.Power,
                BaseRangeConstant = source.BaseRangeConstant,
                BaseRangeMod = source.BaseRangeMod,
                SpellEconomyMod = source.SpellEconomyMod,
                FormulaVersion = source.FormulaVersion,
                ComponentLoss = source.ComponentLoss,
                Bitfield = source.Bitfield,
                MetaSpellId = source.MetaSpellId,
                Duration = source.Duration,
                DegradeModifier = source.DegradeModifier,
                DegradeLimit = source.DegradeLimit,
                PortalLifetime = source.PortalLifetime,
                CasterEffect = source.CasterEffect,
                TargetEffect = source.TargetEffect,
                FizzleEffect = source.FizzleEffect,
                RecoveryInterval = source.RecoveryInterval,
                RecoveryAmount = source.RecoveryAmount,
                DisplayOrder = source.DisplayOrder,
                NonComponentTargetType = source.NonComponentTargetType,
                ManaMod = source.ManaMod,
                Components = (source.Components ?? new List<uint>()).ToList()
            };
        }

        private static SpellRecord CloneDbSpellRecord(SpellRecord source, uint nextId) {
            return new SpellRecord {
                Id = nextId,
                Name = source.Name,
                StatModType = source.StatModType,
                StatModKey = source.StatModKey,
                StatModVal = source.StatModVal,
                EType = source.EType,
                BaseIntensity = source.BaseIntensity,
                Variance = source.Variance,
                Wcid = source.Wcid,
                NumProjectiles = source.NumProjectiles,
                NumProjectilesVariance = source.NumProjectilesVariance,
                SpreadAngle = source.SpreadAngle,
                VerticalAngle = source.VerticalAngle,
                DefaultLaunchAngle = source.DefaultLaunchAngle,
                NonTracking = source.NonTracking,
                CreateOffsetOriginX = source.CreateOffsetOriginX,
                CreateOffsetOriginY = source.CreateOffsetOriginY,
                CreateOffsetOriginZ = source.CreateOffsetOriginZ,
                PaddingOriginX = source.PaddingOriginX,
                PaddingOriginY = source.PaddingOriginY,
                PaddingOriginZ = source.PaddingOriginZ,
                DimsOriginX = source.DimsOriginX,
                DimsOriginY = source.DimsOriginY,
                DimsOriginZ = source.DimsOriginZ,
                PeturbationOriginX = source.PeturbationOriginX,
                PeturbationOriginY = source.PeturbationOriginY,
                PeturbationOriginZ = source.PeturbationOriginZ,
                ImbuedEffect = source.ImbuedEffect,
                SlayerCreatureType = source.SlayerCreatureType,
                SlayerDamageBonus = source.SlayerDamageBonus,
                CritFreq = source.CritFreq,
                CritMultiplier = source.CritMultiplier,
                IgnoreMagicResist = source.IgnoreMagicResist,
                ElementalModifier = source.ElementalModifier,
                DrainPercentage = source.DrainPercentage,
                DamageRatio = source.DamageRatio,
                DamageType = source.DamageType,
                Boost = source.Boost,
                BoostVariance = source.BoostVariance,
                Source = source.Source,
                Destination = source.Destination,
                Proportion = source.Proportion,
                LossPercent = source.LossPercent,
                SourceLoss = source.SourceLoss,
                TransferCap = source.TransferCap,
                MaxBoostAllowed = source.MaxBoostAllowed,
                TransferBitfield = source.TransferBitfield,
                Index = source.Index,
                Link = source.Link,
                PositionObjCellId = source.PositionObjCellId,
                PositionOriginX = source.PositionOriginX,
                PositionOriginY = source.PositionOriginY,
                PositionOriginZ = source.PositionOriginZ,
                PositionAnglesW = source.PositionAnglesW,
                PositionAnglesX = source.PositionAnglesX,
                PositionAnglesY = source.PositionAnglesY,
                PositionAnglesZ = source.PositionAnglesZ,
                MinPower = source.MinPower,
                MaxPower = source.MaxPower,
                PowerVariance = source.PowerVariance,
                DispelSchool = source.DispelSchool,
                Align = source.Align,
                Number = source.Number,
                NumberVariance = source.NumberVariance,
                DotDuration = source.DotDuration,
                LastModified = source.LastModified
            };
        }

        private void DiscardAndSwitchSelection() {
            if (_pendingSpellSelection == null) return;
            var target = _pendingSpellSelection;
            _pendingSpellSelection = null;
            DiscardCurrentEdits();
            _isSelectionGuardActive = true;
            SelectedSpell = target;
            _isSelectionGuardActive = false;
        }

        private void PushUndo(SpellUndoAction action) {
            _undoStack.Push(action);
            HasUndoAction = true;
        }

        private async void ShowToast(string message, string actionLabel = "", Action? action = null) {
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            var token = _toastCts.Token;

            ToastMessage = message;
            ToastActionLabel = actionLabel;
            HasToastAction = !string.IsNullOrWhiteSpace(actionLabel) && action != null;
            _toastAction = action;
            IsToastVisible = true;

            try {
                await Task.Delay(5000, token);
                if (!token.IsCancellationRequested) {
                    IsToastVisible = false;
                    _toastAction = null;
                    ToastActionLabel = "";
                    HasToastAction = false;
                }
            }
            catch (TaskCanceledException) {
            }
        }
    }

    public enum SpellPresetKind {
        Blank,
        ProjectileStarter,
        EnchantmentStarter,
        PortalStarter
    }

    internal enum SpellUndoActionKind {
        RemoveCreated,
        RestoreDeleted
    }

    internal sealed class SpellUndoAction {
        public SpellUndoActionKind Kind { get; }
        public uint SpellId { get; }
        public SpellBase? Snapshot { get; }

        public SpellUndoAction(SpellUndoActionKind kind, uint spellId, SpellBase? snapshot) {
            Kind = kind;
            SpellId = spellId;
            Snapshot = snapshot;
        }
    }

    public sealed class SpellPresetOption {
        public SpellPresetKind Kind { get; }
        public string Name { get; }
        public string Preview { get; }

        public SpellPresetOption(SpellPresetKind kind, string name, string preview) {
            Kind = kind;
            Name = name;
            Preview = preview;
        }

        public override string ToString() => Name;
    }

    public class SpellListItem {
        public uint Id { get; }
        public string Name { get; }
        public string IdHex { get; }
        public MagicSchool School { get; }
        public SpellType MetaSpellType { get; }
        public uint Power { get; }
        public uint BaseMana { get; }

        public SpellListItem(uint id, SpellBase spell) {
            Id = id;
            Name = spell.Name?.ToString() ?? "";
            IdHex = $"0x{id:X4}";
            School = spell.School;
            MetaSpellType = spell.MetaSpellType;
            Power = spell.Power;
            BaseMana = spell.BaseMana;
        }

        public override string ToString() => $"{IdHex} - {Name}";
    }

    /// <summary>
    /// Selectable component entry for the component picker dropdown.
    /// </summary>
    public partial class ComponentPickerItem : ObservableObject {
        public uint Id { get; }
        public string Name { get; }
        public string TypeName { get; }
        public string DisplayLabel { get; }

        [ObservableProperty] private WriteableBitmap? _icon;

        public ComponentPickerItem(uint id, SpellComponentBase comp) {
            Id = id;
            Name = comp.Name?.ToString() ?? $"#{id}";
            TypeName = comp.Type.ToString();
            DisplayLabel = $"{Name} ({TypeName})";
        }

        public override string ToString() => DisplayLabel;
    }

    /// <summary>
    /// A component slot (1-8) that holds a selected component from the picker.
    /// </summary>
    public partial class SpellComponentSlot : ObservableObject {
        public int SlotIndex { get; }
        public string SlotLabel { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasComponent))]
        private ComponentPickerItem? _selectedComponent;

        public bool HasComponent => SelectedComponent != null;

        public SpellComponentSlot(int slotIndex) {
            SlotIndex = slotIndex;
            SlotLabel = $"#{slotIndex + 1}";
        }
    }

    /// <summary>
    /// Selectable icon for the icon picker grid.
    /// </summary>
    public partial class IconPickerItem : ObservableObject {
        public uint Id { get; }
        public string IdHex { get; }

        [ObservableProperty] private WriteableBitmap? _bitmap;

        public IconPickerItem(uint id) {
            Id = id;
            IdHex = $"0x{id:X8}";
        }
    }

    public partial class SpellDetailViewModel : ObservableObject {
        public uint SpellId { get; }

        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _description = "";
        [ObservableProperty] private MagicSchool _school;
        [ObservableProperty] private SpellType _metaSpellType;
        [ObservableProperty] private SpellCategory _category;
        [ObservableProperty] private uint _icon;
        [ObservableProperty] private WriteableBitmap? _iconBitmap;
        [ObservableProperty] private uint _baseMana;
        [ObservableProperty] private uint _power;
        [ObservableProperty] private float _baseRangeConstant;
        [ObservableProperty] private float _baseRangeMod;
        [ObservableProperty] private float _spellEconomyMod;
        [ObservableProperty] private uint _formulaVersion;
        [ObservableProperty] private float _componentLoss;
        [ObservableProperty] private uint _bitfield;
        [ObservableProperty] private uint _metaSpellId;
        [ObservableProperty] private double _duration;
        [ObservableProperty] private float _degradeModifier;
        [ObservableProperty] private float _degradeLimit;
        [ObservableProperty] private double _portalLifetime;
        [ObservableProperty] private PlayScript _casterEffect;
        [ObservableProperty] private PlayScript _targetEffect;
        [ObservableProperty] private PlayScript _fizzleEffect;
        [ObservableProperty] private double _recoveryInterval;
        [ObservableProperty] private float _recoveryAmount;
        [ObservableProperty] private uint _displayOrder;
        [ObservableProperty] private uint _nonComponentTargetType;
        [ObservableProperty] private uint _manaMod;
        [ObservableProperty] private SpellRecord? _dbSpell;

        [ObservableProperty] private ObservableCollection<SpellComponentSlot> _componentSlots = new();
        [ObservableProperty] private ObservableCollection<IconPickerItem> _availableIcons = new();
        [ObservableProperty] private bool _isIconPickerOpen;


        // Spell table props
        [ObservableProperty] private string? _dbName;

        [ObservableProperty] private uint? _dbStatModType;
        [ObservableProperty] private uint? _dbStatModKey;
        [ObservableProperty] private float? _dbStatModVal;

        [ObservableProperty] private uint? _dbEType;
        [ObservableProperty] private int? _dbBaseIntensity;
        [ObservableProperty] private int? _dbVariance;

        [ObservableProperty] private uint? _dbWcid;

        [ObservableProperty] private int? _dbNumProjectiles;
        [ObservableProperty] private int? _dbNumProjectilesVariance;
        [ObservableProperty] private float? _dbSpreadAngle;
        [ObservableProperty] private float? _dbVerticalAngle;
        [ObservableProperty] private float? _dbDefaultLaunchAngle;

        [ObservableProperty] private bool? _dbNonTracking;

        [ObservableProperty] private float? _dbCreateOffsetOriginX;
        [ObservableProperty] private float? _dbCreateOffsetOriginY;
        [ObservableProperty] private float? _dbCreateOffsetOriginZ;

        [ObservableProperty] private float? _dbPaddingOriginX;
        [ObservableProperty] private float? _dbPaddingOriginY;
        [ObservableProperty] private float? _dbPaddingOriginZ;

        [ObservableProperty] private float? _dbDimsOriginX;
        [ObservableProperty] private float? _dbDimsOriginY;
        [ObservableProperty] private float? _dbDimsOriginZ;

        [ObservableProperty] private float? _dbPeturbationOriginX;
        [ObservableProperty] private float? _dbPeturbationOriginY;
        [ObservableProperty] private float? _dbPeturbationOriginZ;

        [ObservableProperty] private uint? _dbImbuedEffect;

        [ObservableProperty] private int? _dbSlayerCreatureType;
        [ObservableProperty] private float? _dbSlayerDamageBonus;

        [ObservableProperty] private double? _dbCritFreq;
        [ObservableProperty] private double? _dbCritMultiplier;

        [ObservableProperty] private int? _dbIgnoreMagicResist;
        [ObservableProperty] private double? _dbElementalModifier;

        [ObservableProperty] private float? _dbDrainPercentage;
        [ObservableProperty] private float? _dbDamageRatio;

        [ObservableProperty] private int? _dbDamageType;

        [ObservableProperty] private int? _dbBoost;
        [ObservableProperty] private int? _dbBoostVariance;

        [ObservableProperty] private int? _dbSource;
        [ObservableProperty] private int? _dbDestination;

        [ObservableProperty] private float? _dbProportion;
        [ObservableProperty] private float? _dbLossPercent;

        [ObservableProperty] private int? _dbSourceLoss;
        [ObservableProperty] private int? _dbTransferCap;
        [ObservableProperty] private int? _dbMaxBoostAllowed;

        [ObservableProperty] private uint? _dbTransferBitfield;

        [ObservableProperty] private int? _dbIndex;
        [ObservableProperty] private int? _dbLink;

        [ObservableProperty] private uint? _dbPositionObjCellId;

        [ObservableProperty] private float? _dbPositionOriginX;
        [ObservableProperty] private float? _dbPositionOriginY;
        [ObservableProperty] private float? _dbPositionOriginZ;

        [ObservableProperty] private float? _dbPositionAnglesW;
        [ObservableProperty] private float? _dbPositionAnglesX;
        [ObservableProperty] private float? _dbPositionAnglesY;
        [ObservableProperty] private float? _dbPositionAnglesZ;

        [ObservableProperty] private int? _dbMinPower;
        [ObservableProperty] private int? _dbMaxPower;
        [ObservableProperty] private float? _dbPowerVariance;

        [ObservableProperty] private int? _dbDispelSchool;

        [ObservableProperty] private int? _dbAlign;
        [ObservableProperty] private int? _dbNumber;
        [ObservableProperty] private float? _dbNumberVariance;

        [ObservableProperty] private double? _dbDotDuration;

        public void LoadFromDb(SpellRecord db) {
            if (db == null)
                return;

            DbSpell = db;

            DbName = db.Name;

            DbStatModType = db.StatModType;
            DbStatModKey = db.StatModKey;
            DbStatModVal = db.StatModVal;

            DbEType = db.EType;
            DbBaseIntensity = db.BaseIntensity;
            DbVariance = db.Variance;

            DbWcid = db.Wcid;

            DbNumProjectiles = db.NumProjectiles;
            DbNumProjectilesVariance = db.NumProjectilesVariance;
            DbSpreadAngle = db.SpreadAngle;
            DbVerticalAngle = db.VerticalAngle;
            DbDefaultLaunchAngle = db.DefaultLaunchAngle;

            DbNonTracking = db.NonTracking;

            DbCreateOffsetOriginX = db.CreateOffsetOriginX;
            DbCreateOffsetOriginY = db.CreateOffsetOriginY;
            DbCreateOffsetOriginZ = db.CreateOffsetOriginZ;

            DbPaddingOriginX = db.PaddingOriginX;
            DbPaddingOriginY = db.PaddingOriginY;
            DbPaddingOriginZ = db.PaddingOriginZ;

            DbDimsOriginX = db.DimsOriginX;
            DbDimsOriginY = db.DimsOriginY;
            DbDimsOriginZ = db.DimsOriginZ;

            DbPeturbationOriginX = db.PeturbationOriginX;
            DbPeturbationOriginY = db.PeturbationOriginY;
            DbPeturbationOriginZ = db.PeturbationOriginZ;

            DbImbuedEffect = db.ImbuedEffect;

            DbSlayerCreatureType = db.SlayerCreatureType;
            DbSlayerDamageBonus = db.SlayerDamageBonus;

            DbCritFreq = db.CritFreq;
            DbCritMultiplier = db.CritMultiplier;

            DbIgnoreMagicResist = db.IgnoreMagicResist;
            DbElementalModifier = db.ElementalModifier;

            DbDrainPercentage = db.DrainPercentage;
            DbDamageRatio = db.DamageRatio;

            DbDamageType = db.DamageType;

            DbBoost = db.Boost;
            DbBoostVariance = db.BoostVariance;

            DbSource = db.Source;
            DbDestination = db.Destination;

            DbProportion = db.Proportion;
            DbLossPercent = db.LossPercent;

            DbSourceLoss = db.SourceLoss;
            DbTransferCap = db.TransferCap;
            DbMaxBoostAllowed = db.MaxBoostAllowed;

            DbTransferBitfield = db.TransferBitfield;

            DbIndex = db.Index;
            DbLink = db.Link;

            DbPositionObjCellId = db.PositionObjCellId;

            DbPositionOriginX = db.PositionOriginX;
            DbPositionOriginY = db.PositionOriginY;
            DbPositionOriginZ = db.PositionOriginZ;

            DbPositionAnglesW = db.PositionAnglesW;
            DbPositionAnglesX = db.PositionAnglesX;
            DbPositionAnglesY = db.PositionAnglesY;
            DbPositionAnglesZ = db.PositionAnglesZ;

            DbMinPower = db.MinPower;
            DbMaxPower = db.MaxPower;
            DbPowerVariance = db.PowerVariance;

            DbDispelSchool = db.DispelSchool;

            DbAlign = db.Align;
            DbNumber = db.Number;
            DbNumberVariance = db.NumberVariance;

            DbDotDuration = db.DotDuration;
        }

        public void ApplyDbTo(SpellRecord db) {
            if (db == null)
                return;

            db.Name = DbName;

            db.StatModType = DbStatModType;
            db.StatModKey = DbStatModKey;
            db.StatModVal = DbStatModVal;

            db.EType = DbEType;
            db.BaseIntensity = DbBaseIntensity;
            db.Variance = DbVariance;

            db.Wcid = DbWcid;

            db.NumProjectiles = DbNumProjectiles;
            db.NumProjectilesVariance = DbNumProjectilesVariance;
            db.SpreadAngle = DbSpreadAngle;
            db.VerticalAngle = DbVerticalAngle;
            db.DefaultLaunchAngle = DbDefaultLaunchAngle;

            db.NonTracking = DbNonTracking;

            db.CreateOffsetOriginX = DbCreateOffsetOriginX;
            db.CreateOffsetOriginY = DbCreateOffsetOriginY;
            db.CreateOffsetOriginZ = DbCreateOffsetOriginZ;

            db.PaddingOriginX = DbPaddingOriginX;
            db.PaddingOriginY = DbPaddingOriginY;
            db.PaddingOriginZ = DbPaddingOriginZ;

            db.DimsOriginX = DbDimsOriginX;
            db.DimsOriginY = DbDimsOriginY;
            db.DimsOriginZ = DbDimsOriginZ;

            db.PeturbationOriginX = DbPeturbationOriginX;
            db.PeturbationOriginY = DbPeturbationOriginY;
            db.PeturbationOriginZ = DbPeturbationOriginZ;

            db.ImbuedEffect = DbImbuedEffect;

            db.SlayerCreatureType = DbSlayerCreatureType;
            db.SlayerDamageBonus = DbSlayerDamageBonus;

            db.CritFreq = DbCritFreq;
            db.CritMultiplier = DbCritMultiplier;

            db.IgnoreMagicResist = DbIgnoreMagicResist;
            db.ElementalModifier = DbElementalModifier;

            db.DrainPercentage = DbDrainPercentage;
            db.DamageRatio = DbDamageRatio;

            db.DamageType = DbDamageType;

            db.Boost = DbBoost;
            db.BoostVariance = DbBoostVariance;

            db.Source = DbSource;
            db.Destination = DbDestination;

            db.Proportion = DbProportion;
            db.LossPercent = DbLossPercent;

            db.SourceLoss = DbSourceLoss;
            db.TransferCap = DbTransferCap;
            db.MaxBoostAllowed = DbMaxBoostAllowed;

            db.TransferBitfield = DbTransferBitfield;

            db.Index = DbIndex;
            db.Link = DbLink;

            db.PositionObjCellId = DbPositionObjCellId;

            db.PositionOriginX = DbPositionOriginX;
            db.PositionOriginY = DbPositionOriginY;
            db.PositionOriginZ = DbPositionOriginZ;

            db.PositionAnglesW = DbPositionAnglesW;
            db.PositionAnglesX = DbPositionAnglesX;
            db.PositionAnglesY = DbPositionAnglesY;
            db.PositionAnglesZ = DbPositionAnglesZ;

            db.MinPower = DbMinPower;
            db.MaxPower = DbMaxPower;
            db.PowerVariance = DbPowerVariance;

            db.DispelSchool = DbDispelSchool;

            db.Align = DbAlign;
            db.Number = DbNumber;
            db.NumberVariance = DbNumberVariance;

            db.DotDuration = DbDotDuration;
        }

        public List<ComponentPickerItem> AllComponents { get; private set; } = new();

        public bool IsEnchantment => MetaSpellType == SpellType.Enchantment || MetaSpellType == SpellType.FellowEnchantment;
        public bool IsPortalSummon => MetaSpellType == SpellType.PortalSummon;
        public bool CanAddComponent => ComponentSlots.Count < 8;

        partial void OnMetaSpellTypeChanged(SpellType value) {
            OnPropertyChanged(nameof(IsEnchantment));
            OnPropertyChanged(nameof(IsPortalSummon));
        }

        partial void OnIconChanged(uint value) {
            if (_dats != null) {
                var localDats = _dats;
                Task.Run(() => {
                    var bmp = DatIconLoader.LoadIcon(localDats, value, 48);
                    Dispatcher.UIThread.Post(() => IconBitmap = bmp);
                });
            }
        }

        public IReadOnlyList<MagicSchool> AllSchools { get; } = Enum.GetValues<MagicSchool>();
        public IReadOnlyList<SpellType> AllSpellTypes { get; } = Enum.GetValues<SpellType>();
        public IReadOnlyList<PlayScript> AllPlayScripts { get; } = Enum.GetValues<PlayScript>();

        public ObservableCollection<FlagItem> BitfieldFlags { get; } = new();
        public ObservableCollection<FlagItem> TargetTypeFlags { get; } = new();

        public string BitfieldDisplay => BitfieldFlags.Any(f => f.IsChecked)
            ? string.Join(", ", BitfieldFlags.Where(f => f.IsChecked).Select(f => f.Name))
            : "(none)";

        public string TargetTypeDisplay => TargetTypeFlags.Any(f => f.IsChecked)
            ? string.Join(", ", TargetTypeFlags.Where(f => f.IsChecked).Select(f => f.Name))
            : "(none)";

        private readonly SpellComponentTable? _componentTable;
        private readonly IDatReaderWriter? _dats;
        private uint _extraBitfieldBits;
        private uint _extraTargetTypeBits;

        public SpellDetailViewModel(uint id, SpellBase spell, SpellComponentTable? componentTable,
            Dictionary<uint, SpellBase> allSpells, IDatReaderWriter dats) {
            _componentTable = componentTable;
            _dats = dats;

            SpellId = id;
            Name = spell.Name?.ToString() ?? "";
            Description = spell.Description?.ToString() ?? "";
            School = spell.School;
            MetaSpellType = spell.MetaSpellType;
            Category = spell.Category;
            Icon = spell.Icon;
            BaseMana = spell.BaseMana;
            Power = spell.Power;
            BaseRangeConstant = spell.BaseRangeConstant;
            BaseRangeMod = spell.BaseRangeMod;
            SpellEconomyMod = spell.SpellEconomyMod;
            FormulaVersion = spell.FormulaVersion;
            ComponentLoss = spell.ComponentLoss;
            Bitfield = (uint)spell.Bitfield;
            MetaSpellId = spell.MetaSpellId;
            Duration = spell.Duration;
            DegradeModifier = spell.DegradeModifier;
            DegradeLimit = spell.DegradeLimit;
            PortalLifetime = spell.PortalLifetime;
            CasterEffect = spell.CasterEffect;
            TargetEffect = spell.TargetEffect;
            FizzleEffect = spell.FizzleEffect;
            RecoveryInterval = spell.RecoveryInterval;
            RecoveryAmount = spell.RecoveryAmount;
            DisplayOrder = spell.DisplayOrder;
            NonComponentTargetType = (uint)spell.NonComponentTargetType;
            ManaMod = spell.ManaMod;

            InitBitfieldFlags(Bitfield);
            InitTargetTypeFlags(NonComponentTargetType);

            BuildAllComponents();
            BuildComponentSlots(spell.Components);
            BuildAvailableIcons(allSpells);
            LoadIconAsync(spell.Icon);
        }

        private void BuildAllComponents() {
            if (_componentTable == null) return;
            AllComponents = _componentTable.Components
                .OrderBy(kvp => kvp.Value.Name?.ToString() ?? "")
                .Select(kvp => new ComponentPickerItem(kvp.Key, kvp.Value))
                .ToList();

            if (_dats != null) {
                var localDats = _dats;
                foreach (var item in AllComponents) {
                    var comp = _componentTable.Components[item.Id];
                    if (comp.Icon == 0) continue;
                    var localItem = item;
                    var localIconId = comp.Icon;
                    Task.Run(() => {
                        var bmp = DatIconLoader.LoadIcon(localDats, localIconId, 20);
                        Dispatcher.UIThread.Post(() => localItem.Icon = bmp);
                    });
                }
            }
        }

        private void BuildComponentSlots(List<uint> componentIds) {
            ComponentSlots.Clear();
            for (int i = 0; i < componentIds.Count; i++) {
                var slot = new SpellComponentSlot(i);
                var match = AllComponents.FirstOrDefault(c => c.Id == componentIds[i]);
                slot.SelectedComponent = match;
                ComponentSlots.Add(slot);
            }
            OnPropertyChanged(nameof(CanAddComponent));
        }

        private void BuildAvailableIcons(Dictionary<uint, SpellBase> allSpells) {
            var snapshot = allSpells.Values.ToArray();
            var uniqueIconIds = snapshot
                .Select(s => s.Icon)
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

        private void LoadIconAsync(uint iconId) {
            if (iconId == 0 || _dats == null) return;
            var localDats = _dats;
            Task.Run(() => {
                var bmp = DatIconLoader.LoadIcon(localDats, iconId, 48);
                Dispatcher.UIThread.Post(() => IconBitmap = bmp);
            });
        }

        [RelayCommand]
        private void PickIcon(IconPickerItem? item) {
            if (item == null) return;
            Icon = item.Id;
            IsIconPickerOpen = false;
        }

        [RelayCommand]
        private void ToggleIconPicker() {
            IsIconPickerOpen = !IsIconPickerOpen;
        }

        [RelayCommand]
        private void AddComponent() {
            if (ComponentSlots.Count >= 8) return;
            var slot = new SpellComponentSlot(ComponentSlots.Count);
            ComponentSlots.Add(slot);
            OnPropertyChanged(nameof(CanAddComponent));
        }

        [RelayCommand]
        private void RemoveComponent(SpellComponentSlot? slot) {
            if (slot == null) return;
            ComponentSlots.Remove(slot);
            for (int i = 0; i < ComponentSlots.Count; i++) {
                // Re-index isn't needed since SlotIndex is readonly, but labels update via the collection
            }
            OnPropertyChanged(nameof(CanAddComponent));
        }

        [RelayCommand]
        private void MoveComponentUp(SpellComponentSlot? slot) {
            if (slot == null) return;
            int idx = ComponentSlots.IndexOf(slot);
            if (idx <= 0) return;
            ComponentSlots.Move(idx, idx - 1);
        }

        [RelayCommand]
        private void MoveComponentDown(SpellComponentSlot? slot) {
            if (slot == null) return;
            int idx = ComponentSlots.IndexOf(slot);
            if (idx < 0 || idx >= ComponentSlots.Count - 1) return;
            ComponentSlots.Move(idx, idx + 1);
        }

        private void InitBitfieldFlags(uint bitfield) {
            uint knownBits = 0;
            foreach (var flag in Enum.GetValues<SpellIndex>()) {
                var val = (uint)flag;
                if (val == 0 || (val & (val - 1)) != 0) continue;
                knownBits |= val;
                var item = new FlagItem(flag.ToString(), val, (bitfield & val) != 0);
                item.PropertyChanged += (_, e) => {
                    if (e.PropertyName == nameof(FlagItem.IsChecked)) UpdateBitfieldFromFlags();
                };
                BitfieldFlags.Add(item);
            }
            _extraBitfieldBits = bitfield & ~knownBits;
        }

        private void UpdateBitfieldFromFlags() {
            uint val = _extraBitfieldBits;
            foreach (var f in BitfieldFlags)
                if (f.IsChecked) val |= f.Value;
            Bitfield = val;
            OnPropertyChanged(nameof(BitfieldDisplay));
        }

        private void InitTargetTypeFlags(uint targetType) {
            uint knownBits = 0;
            foreach (var flag in Enum.GetValues<ItemType>()) {
                var val = (uint)flag;
                if (val == 0 || (val & (val - 1)) != 0) continue;
                knownBits |= val;
                var item = new FlagItem(flag.ToString(), val, (targetType & val) != 0);
                item.PropertyChanged += (_, e) => {
                    if (e.PropertyName == nameof(FlagItem.IsChecked)) UpdateTargetTypeFromFlags();
                };
                TargetTypeFlags.Add(item);
            }
            _extraTargetTypeBits = targetType & ~knownBits;
        }

        private void UpdateTargetTypeFromFlags() {
            uint val = _extraTargetTypeBits;
            foreach (var f in TargetTypeFlags)
                if (f.IsChecked) val |= f.Value;
            NonComponentTargetType = val;
            OnPropertyChanged(nameof(TargetTypeDisplay));
        }

        public void ApplyTo(SpellBase spell) {
            spell.Name = Name;
            spell.Description = Description;
            spell.School = School;
            spell.MetaSpellType = MetaSpellType;
            spell.Category = Category;
            spell.Icon = Icon;
            spell.BaseMana = BaseMana;
            spell.Power = Power;
            spell.BaseRangeConstant = BaseRangeConstant;
            spell.BaseRangeMod = BaseRangeMod;
            spell.SpellEconomyMod = SpellEconomyMod;
            spell.FormulaVersion = FormulaVersion;
            spell.ComponentLoss = ComponentLoss;
            spell.Bitfield = Bitfield;
            spell.MetaSpellId = MetaSpellId;
            spell.Duration = Duration;
            spell.DegradeModifier = DegradeModifier;
            spell.DegradeLimit = DegradeLimit;
            spell.PortalLifetime = PortalLifetime;
            spell.CasterEffect = CasterEffect;
            spell.TargetEffect = TargetEffect;
            spell.FizzleEffect = FizzleEffect;
            spell.RecoveryInterval = RecoveryInterval;
            spell.RecoveryAmount = RecoveryAmount;
            spell.DisplayOrder = DisplayOrder;
            spell.NonComponentTargetType = (uint)NonComponentTargetType;
            spell.ManaMod = ManaMod;

            spell.Components = ComponentSlots
                .Where(s => s.SelectedComponent != null)
                .Select(s => s.SelectedComponent!.Id)
                .ToList();
        }
    }

    public partial class FlagItem : ObservableObject {
        public string Name { get; }
        public uint Value { get; }

        [ObservableProperty] private bool _isChecked;

        public FlagItem(string name, uint value, bool isChecked) {
            Name = name;
            Value = value;
            _isChecked = isChecked;
        }
    }
}
