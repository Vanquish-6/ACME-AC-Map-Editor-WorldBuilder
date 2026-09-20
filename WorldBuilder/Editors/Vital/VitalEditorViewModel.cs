using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using System;
using System.Collections.Generic;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Vital {
    public partial class VitalEditorViewModel : ViewModelBase {
        private IDatReaderWriter? _dats;
        private Project? _project;
        private PortalDatDocument? _portalDoc;
        private VitalTable? _vitalTable;
        private const uint VitalTableId = 0x0E000003;

        [ObservableProperty] private string _statusText = "No vital table loaded";

        [ObservableProperty] private SkillFormulaViewModel? _healthFormula;
        [ObservableProperty] private SkillFormulaViewModel? _staminaFormula;
        [ObservableProperty] private SkillFormulaViewModel? _manaFormula;

        public WorldBuilderSettings Settings { get; }

        public VitalEditorViewModel(WorldBuilderSettings settings) {
            Settings = settings;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _portalDoc = project.DocumentManager.GetOrCreateDocumentAsync<PortalDatDocument>(PortalDatDocument.DocumentId).Result;
            LoadVitalTable();
        }

        private void LoadVitalTable() {
            if (_dats == null) return;

            if (_portalDoc != null && _portalDoc.TryGetEntry<VitalTable>(VitalTableId, out var docTable) && docTable != null) {
                _vitalTable = docTable;
            }
            else if (!_dats.TryGet<VitalTable>(VitalTableId, out var datTable)) {
                StatusText = "Failed to load VitalTable from DAT";
                return;
            }
            else {
                _vitalTable = datTable;
            }

            HealthFormula = new SkillFormulaViewModel("Health", _vitalTable.Health);
            StaminaFormula = new SkillFormulaViewModel("Stamina", _vitalTable.Stamina);
            ManaFormula = new SkillFormulaViewModel("Mana", _vitalTable.Mana);

            StatusText = "Loaded VitalTable (0x0E000003)";
        }

        [RelayCommand]
        private void Save() {
            if (_vitalTable == null || _portalDoc == null) return;

            HealthFormula?.ApplyTo(_vitalTable.Health);
            StaminaFormula?.ApplyTo(_vitalTable.Stamina);
            ManaFormula?.ApplyTo(_vitalTable.Mana);

            _portalDoc.SetEntry(VitalTableId, _vitalTable);
            StatusText = "Saved VitalTable to project. Use File > Export to write DATs.";
        }
    }

    public partial class SkillFormulaViewModel : ObservableObject {
        public string VitalName { get; }

        [ObservableProperty] private int _unknown;
        [ObservableProperty] private bool _hasSecondAttribute;
        [ObservableProperty] private bool _useFormula;
        [ObservableProperty] private int _divisor;
        [ObservableProperty] private AttributeId _attribute1;
        [ObservableProperty] private AttributeId _attribute2;

        public IReadOnlyList<AttributeId> AllAttributes { get; } = Enum.GetValues<AttributeId>();

        public string FormulaDisplay => HasSecondAttribute
            ? $"({Attribute1} + {Attribute2}) / {Divisor}"
            : $"{Attribute1} / {Divisor}";

        partial void OnHasSecondAttributeChanged(bool value) => OnPropertyChanged(nameof(FormulaDisplay));
        partial void OnAttribute1Changed(AttributeId value) => OnPropertyChanged(nameof(FormulaDisplay));
        partial void OnAttribute2Changed(AttributeId value) => OnPropertyChanged(nameof(FormulaDisplay));
        partial void OnDivisorChanged(int value) => OnPropertyChanged(nameof(FormulaDisplay));

        public SkillFormulaViewModel(string vitalName, SkillFormula formula) {
            VitalName = vitalName;
            Load(formula.Divisor, formula.Attribute1, formula.Attribute2, formula.Attribute1Multiplier > 0, formula.Attribute2Multiplier > 0, formula.AdditiveBonus);
        }

        public SkillFormulaViewModel(string vitalName, VitalFormula formula) {
            VitalName = vitalName;
            Load((int)formula.Divisor, formula.Attribute1, formula.Attribute2, formula.Attribute1Multiplier > 0, formula.Attribute2Multiplier > 0, (int)formula.AdditiveBonus);
        }

        private void Load(int divisor, AttributeId attr1, AttributeId attr2, bool useFormula, bool hasSecond, int unknown) {
            Divisor = divisor;
            Attribute1 = attr1;
            Attribute2 = attr2;
            UseFormula = useFormula;
            HasSecondAttribute = hasSecond;
            Unknown = unknown;
        }

        public void ApplyTo(SkillFormula formula) {
            formula.Divisor = Divisor;
            formula.Attribute1 = Attribute1;
            formula.Attribute2 = Attribute2;
            formula.Attribute1Multiplier = UseFormula ? 1 : 0;
            formula.Attribute2Multiplier = HasSecondAttribute ? 1 : 0;
            formula.AdditiveBonus = Unknown;
        }

        public void ApplyTo(VitalFormula formula) {
            formula.Divisor = (uint)Divisor;
            formula.Attribute1 = Attribute1;
            formula.Attribute2 = Attribute2;
            formula.Attribute1Multiplier = UseFormula ? 1u : 0;
            formula.Attribute2Multiplier = HasSecondAttribute ? 1u : 0;
            formula.AdditiveBonus = (uint)Unknown;
        }
    }
}
