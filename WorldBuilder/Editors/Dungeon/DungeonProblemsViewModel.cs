using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using WorldBuilder.Shared.Documents;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon {
    public partial class DungeonProblemItem : ViewModelBase {
        public DungeonDocument.ValidationResult Result { get; }

        public string SeverityLabel => Result.Severity switch {
            DungeonDocument.ValidationSeverity.Error => "Error",
            DungeonDocument.ValidationSeverity.Warning => "Warning",
            _ => "Info"
        };

        public string Message => Result.Message;
        public string Location => Result.LocationLabel ?? (Result.CellNumber is ushort n ? $"Room 0x{n:X4}" : "");
        public string Explanation => Result.Explanation ?? "";
        public bool HasExplanation => !string.IsNullOrEmpty(Result.Explanation);
        public IBrush SeverityBrush => Result.Severity switch {
            DungeonDocument.ValidationSeverity.Error => new SolidColorBrush(Color.Parse("#ff8888")),
            DungeonDocument.ValidationSeverity.Warning => new SolidColorBrush(Color.Parse("#e0c060")),
            _ => new SolidColorBrush(Color.Parse("#88cc88"))
        };

        public DungeonProblemItem(DungeonDocument.ValidationResult result) {
            Result = result;
        }
    }

    public partial class DungeonProblemsViewModel : ViewModelBase {
        private readonly DungeonEditorViewModel _editor;

        [ObservableProperty] private ObservableCollection<DungeonProblemItem> _items = new();
        [ObservableProperty] private DungeonProblemItem? _selectedItem;
        [ObservableProperty] private string _summary = "No problems.";

        public DungeonProblemsViewModel(DungeonEditorViewModel editor) {
            _editor = editor;
        }

        public void Refresh() {
            var results = _editor.GetCurrentDocument()?.ValidateComprehensive()
                          ?? new List<DungeonDocument.ValidationResult>();
            AppendMisalignedDoorways(results);
            var list = new ObservableCollection<DungeonProblemItem>();
            foreach (var r in results)
                list.Add(new DungeonProblemItem(r));
            Items = list;

            int errors = results.FindAll(r => r.Severity == DungeonDocument.ValidationSeverity.Error).Count;
            int warnings = results.FindAll(r => r.Severity == DungeonDocument.ValidationSeverity.Warning).Count;
            Summary = errors == 0 && warnings == 0
                ? "No problems."
                : $"{errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")} — click a row to select it in the scene.";
        }

        partial void OnSelectedItemChanged(DungeonProblemItem? value) {
            if (value != null)
                _editor.GoToProblem(value.Result);
        }

        [RelayCommand]
        private void Recheck() {
            _editor.ValidateDungeonCommand.Execute(null);
            Refresh();
        }

        /// <summary>
        /// Connected doorways whose portal polygons don't sit on the same plane/center
        /// look like a black void in walk view and an uneven frame in-game.
        /// </summary>
        private void AppendMisalignedDoorways(List<DungeonDocument.ValidationResult> results) {
            var doc = _editor.GetCurrentDocument();
            var dats = _editor.EditingContext.Dats;
            if (doc == null || dats == null || doc.Cells.Count == 0) return;

            const float gapWarn = 0.35f;
            var seen = new HashSet<(ushort A, ushort B)>();

            foreach (var cell in doc.Cells) {
                foreach (var portal in cell.CellPortals) {
                    if (portal.OtherCellId == 0 || portal.OtherCellId == 0xFFFF) continue;
                    var other = doc.GetCell(portal.OtherCellId);
                    if (other == null) continue;

                    ushort a = cell.CellNumber;
                    ushort b = portal.OtherCellId;
                    var pair = a < b ? (a, b) : (b, a);
                    if (!seen.Add(pair)) continue;

                    var back = other.CellPortals.Find(p => p.OtherCellId == cell.CellNumber);
                    ushort otherPoly = back != null ? back.PolygonId : portal.OtherPortalId;
                    if (!TryPortalGeom(dats, cell, portal.PolygonId, out var geomA)) continue;
                    if (!TryPortalGeom(dats, other, otherPoly, out var geomB)) continue;

                    float gap = PortalSnapper.CentroidGap(
                        geomA, cell.Origin, cell.Orientation,
                        geomB, other.Origin, other.Orientation);
                    if (gap < gapWarn) continue;

                    results.Add(new DungeonDocument.ValidationResult(
                        DungeonDocument.ValidationSeverity.Warning,
                        "These doorways don't line up",
                        cell.CellNumber,
                        portal.PolygonId,
                        $"The opening between room 0x{a:X4} and 0x{b:X4} is {gap:F2} units off. " +
                        "That makes a black void in walk view until you step through. Delete the later room and place it again so the frames sit flush.",
                        $"Room 0x{a:X4} ↔ 0x{b:X4}"));
                }
            }
        }

        private static bool TryPortalGeom(
            Shared.Lib.IDatReaderWriter dats,
            DungeonCellData cell,
            ushort polyId,
            out PortalSnapper.PortalGeometry geom) {

            geom = default;
            uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env) || env == null) return false;
            if (!env.Cells.TryGetValue(cell.CellStructure, out var cs)) return false;
            var found = PortalSnapper.GetPortalGeometry(cs, polyId);
            if (found == null) return false;
            geom = found.Value;
            return true;
        }
    }
}
