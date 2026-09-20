using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Skill {
    public partial class SkillEditorViewModel : ViewModelBase {
        private IDatReaderWriter? _dats;
        private Project? _project;
        private PortalDatDocument? _portalDoc;
        private SkillTable? _skillTable;
        private Dictionary<SkillId, SkillBase>? _allSkills;
        private const uint SkillTableId = 0x0E000004;

        [ObservableProperty] private string _statusText = "No skills loaded";
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private SkillCategory? _filterCategory;
        [ObservableProperty] private ObservableCollection<SkillListItem> _skills = new();
        [ObservableProperty] private SkillListItem? _selectedSkill;
        [ObservableProperty] private SkillDetailViewModel? _selectedDetail;
        [ObservableProperty] private int _totalSkillCount;
        [ObservableProperty] private int _filteredSkillCount;

        public IReadOnlyList<SkillCategory?> CategoryOptions { get; } = new List<SkillCategory?> {
            null, SkillCategory.Combat, SkillCategory.Magic, SkillCategory.Other, SkillCategory.Undefined,
        };

        public WorldBuilderSettings Settings { get; }

        public SkillEditorViewModel(WorldBuilderSettings settings) {
            Settings = settings;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _portalDoc = project.DocumentManager.GetOrCreateDocumentAsync<PortalDatDocument>(PortalDatDocument.DocumentId).Result;
            LoadSkills();
        }

        private void LoadSkills() {
            if (_dats == null) return;

            if (_portalDoc != null && _portalDoc.TryGetEntry<SkillTable>(SkillTableId, out var docTable) && docTable != null) {
                _skillTable = docTable;
            }
            else if (!_dats.TryGet<SkillTable>(SkillTableId, out var datTable)) {
                StatusText = "Failed to load SkillTable from DAT";
                return;
            }
            else {
                _skillTable = datTable;
            }

            _allSkills = _skillTable.Skills;
            TotalSkillCount = _allSkills.Count;

            ApplyFilter();
            StatusText = $"Loaded {TotalSkillCount} skills";
        }

        partial void OnSearchTextChanged(string value) => ApplyFilter();
        partial void OnFilterCategoryChanged(SkillCategory? value) => ApplyFilter();

        partial void OnSelectedSkillChanged(SkillListItem? value) {
            if (value != null && _allSkills != null && _allSkills.TryGetValue(value.Id, out var skill) && _dats != null) {
                try {
                    SelectedDetail = new SkillDetailViewModel(value.Id, skill, _allSkills, _dats);
                }
                catch (Exception ex) {
                    Console.WriteLine($"[SkillEditor] Error creating detail: {ex}");
                    SelectedDetail = null;
                    StatusText = $"Error loading skill: {ex.Message}";
                }
            }
            else {
                SelectedDetail = null;
            }
        }

        private void ApplyFilter() {
            if (_allSkills == null) return;

            var query = SearchText?.Trim() ?? "";

            var filtered = _allSkills
                .Where(kvp => {
                    if (!string.IsNullOrEmpty(query) &&
                        !(kvp.Value.Name?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (FilterCategory.HasValue && kvp.Value.Category != FilterCategory.Value) return false;
                    return true;
                })
                .OrderBy(kvp => kvp.Value.Name?.ToString() ?? "")
                .Select(kvp => new SkillListItem(kvp.Key, kvp.Value))
                .ToList();

            Skills = new ObservableCollection<SkillListItem>(filtered);
            FilteredSkillCount = filtered.Count;
        }

        [RelayCommand]
        private void ClearFilters() {
            SearchText = "";
            FilterCategory = null;
        }

        [RelayCommand]
        private void AddSkill() {
            if (_skillTable == null || _allSkills == null) return;

            int nextId = 1;
            if (_allSkills.Count > 0)
                nextId = _allSkills.Keys.Max(k => (int)k) + 1;

            var newSkill = new SkillBase {
                Name = $"New Skill {nextId}",
                Description = "",
                Category = SkillCategory.Other,
                Formula = new SkillFormula()
            };

            var skillId = (SkillId)nextId;
            _allSkills[skillId] = newSkill;
            TotalSkillCount = _allSkills.Count;
            ApplyFilter();

            SelectedSkill = Skills.FirstOrDefault(s => s.Id == skillId);
            StatusText = $"Added new skill: {skillId} (ID {nextId}). Remember to Save.";
        }

        [RelayCommand]
        private void DeleteSkill() {
            if (SelectedDetail == null || _skillTable == null || _portalDoc == null || _allSkills == null) return;

            var id = SelectedDetail.SkillId;
            if (!_allSkills.Remove(id)) return;

            _portalDoc.SetEntry(SkillTableId, _skillTable);

            SelectedDetail = null;
            TotalSkillCount = _allSkills.Count;
            ApplyFilter();
            StatusText = $"Deleted skill: {id}. Use File > Export to write DATs.";
        }

        [RelayCommand]
        private void SaveSkill() {
            if (SelectedDetail == null || _skillTable == null || _portalDoc == null || _allSkills == null) return;

            var detail = SelectedDetail;
            var id = detail.SkillId;

            if (!_allSkills.TryGetValue(id, out var skill)) return;

            detail.ApplyTo(skill);

            _portalDoc.SetEntry(SkillTableId, _skillTable);

            int idx = -1;
            for (int i = 0; i < Skills.Count; i++) {
                if (Skills[i].Id == id) { idx = i; break; }
            }
            if (idx >= 0) {
                Skills[idx] = new SkillListItem(id, skill);
            }

            StatusText = $"Saved skill: {skill.Name} to project. Use File > Export to write DATs.";
        }
    }

    public class SkillListItem {
        public SkillId Id { get; }
        public string Name { get; }
        public string IdHex { get; }
        public SkillCategory Category { get; }
        public int TrainedCost { get; }
        public int SpecializedCost { get; }

        public SkillListItem(SkillId id, SkillBase skill) {
            Id = id;
            Name = skill.Name?.ToString() ?? "";
            IdHex = $"0x{(int)id:X2}";
            Category = skill.Category;
            TrainedCost = skill.TrainedCost;
            SpecializedCost = skill.SpecializedCost;
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

    public partial class SkillDetailViewModel : ObservableObject {
        public SkillId SkillId { get; }

        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _description = "";
        [ObservableProperty] private uint _iconId;
        [ObservableProperty] private WriteableBitmap? _iconBitmap;
        [ObservableProperty] private int _trainedCost;
        [ObservableProperty] private int _specializedCost;
        [ObservableProperty] private SkillCategory _category;
        [ObservableProperty] private bool _chargenUse;
        [ObservableProperty] private uint _minLevel;
        [ObservableProperty] private double _upperBound;
        [ObservableProperty] private double _lowerBound;
        [ObservableProperty] private double _learnMod;

        [ObservableProperty] private int _formulaDivisor;
        [ObservableProperty] private AttributeId _formulaAttribute1;
        [ObservableProperty] private AttributeId _formulaAttribute2;
        [ObservableProperty] private bool _formulaUseFormula;
        [ObservableProperty] private bool _formulaHasSecondAttribute;
        [ObservableProperty] private int _formulaUnknown;

        [ObservableProperty] private ObservableCollection<IconPickerItem> _availableIcons = new();
        [ObservableProperty] private bool _isIconPickerOpen;

        public IReadOnlyList<SkillCategory> AllCategories { get; } = Enum.GetValues<SkillCategory>();
        public IReadOnlyList<AttributeId> AllAttributes { get; } = Enum.GetValues<AttributeId>();

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

        public SkillDetailViewModel(SkillId id, SkillBase skill, Dictionary<SkillId, SkillBase> allSkills, IDatReaderWriter dats) {
            _dats = dats;
            SkillId = id;
            Name = skill.Name?.ToString() ?? "";
            Description = skill.Description?.ToString() ?? "";
            IconId = skill.IconId;
            TrainedCost = skill.TrainedCost;
            SpecializedCost = skill.SpecializedCost;
            Category = skill.Category;
            ChargenUse = skill.ChargenUse;
            MinLevel = skill.MinLevel;
            UpperBound = skill.UpperBound;
            LowerBound = skill.LowerBound;
            LearnMod = skill.LearnMod;

            if (skill.Formula != null) {
                FormulaDivisor = skill.Formula.Divisor;
                FormulaAttribute1 = skill.Formula.Attribute1;
                FormulaAttribute2 = skill.Formula.Attribute2;
                FormulaUseFormula = skill.Formula.Attribute1Multiplier > 0;
                FormulaHasSecondAttribute = skill.Formula.Attribute2Multiplier > 0;
                FormulaUnknown = skill.Formula.AdditiveBonus;
            }

            try {
                LoadIconAsync(skill.IconId);
                BuildAvailableIcons(allSkills);
            }
            catch (Exception ex) {
                Console.WriteLine($"[SkillEditor] Icon load error: {ex.Message}");
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

        private void BuildAvailableIcons(Dictionary<SkillId, SkillBase> allSkills) {
            var snapshot = allSkills.Values.ToArray();
            var uniqueIconIds = snapshot
                .Select(s => s.IconId)
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

        public void ApplyTo(SkillBase skill) {
            skill.Name = Name;
            skill.Description = Description;
            skill.IconId = IconId;
            skill.TrainedCost = TrainedCost;
            skill.SpecializedCost = SpecializedCost;
            skill.Category = Category;
            skill.ChargenUse = ChargenUse;
            skill.MinLevel = MinLevel;
            skill.UpperBound = UpperBound;
            skill.LowerBound = LowerBound;
            skill.LearnMod = LearnMod;

            skill.Formula ??= new SkillFormula();
            skill.Formula.Divisor = FormulaDivisor;
            skill.Formula.Attribute1 = FormulaAttribute1;
            skill.Formula.Attribute2 = FormulaAttribute2;
            skill.Formula.Attribute1Multiplier = FormulaUseFormula ? 1 : 0;
            skill.Formula.Attribute2Multiplier = FormulaHasSecondAttribute ? 1 : 0;
            skill.Formula.AdditiveBonus = FormulaUnknown;
        }
    }
}
