using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class PasteCellsCommand : IDungeonCommand {
        private readonly List<DungeonCellData> _cellTemplates;
        private readonly Vector3 _offset;
        private readonly List<ushort> _createdCellNums = new();

        public string Description => $"Paste {_cellTemplates.Count} room(s)";

        public PasteCellsCommand(List<DungeonCellData> cellTemplates, Vector3 offset) {
            _cellTemplates = cellTemplates;
            _offset = offset;
        }

        public void Execute(DungeonDocument document) {
            _createdCellNums.Clear();

            var cellMap = new Dictionary<ushort, ushort>();
            foreach (var template in _cellTemplates) {
                var newNum = document.AllocateCellNumber();
                cellMap[template.CellNumber] = newNum;
                _createdCellNums.Add(newNum);
            }

            for (int i = 0; i < _cellTemplates.Count; i++) {
                var src = _cellTemplates[i];
                var newCell = new DungeonCellData {
                    CellNumber = _createdCellNums[i],
                    EnvironmentId = src.EnvironmentId,
                    CellStructure = src.CellStructure,
                    Origin = src.Origin + _offset,
                    Orientation = src.Orientation,
                    Flags = src.Flags,
                    RestrictionObj = src.RestrictionObj,
                };
                newCell.Surfaces.AddRange(src.Surfaces);

                foreach (var cp in src.CellPortals) {
                    if (cellMap.TryGetValue(cp.OtherCellId, out var mappedOther)) {
                        newCell.CellPortals.Add(new DungeonCellPortalData {
                            OtherCellId = mappedOther,
                            PolygonId = cp.PolygonId,
                            OtherPortalId = cp.OtherPortalId,
                            Flags = cp.Flags
                        });
                    }
                }

                foreach (var vc in src.VisibleCells) {
                    if (cellMap.TryGetValue(vc, out var mappedVc))
                        newCell.VisibleCells.Add(mappedVc);
                }

                foreach (var stab in src.StaticObjects) {
                    newCell.StaticObjects.Add(new DungeonStabData {
                        Id = stab.Id,
                        Origin = stab.Origin,
                        Orientation = stab.Orientation
                    });
                }

                document.Cells.Add(newCell);
            }
            document.MarkDirty();
        }

        public void Undo(DungeonDocument document) {
            foreach (var cellNum in _createdCellNums) {
                document.RemoveCell(cellNum);
            }
        }

        public IReadOnlyList<ushort> CreatedCellNums => _createdCellNums;
    }
}
