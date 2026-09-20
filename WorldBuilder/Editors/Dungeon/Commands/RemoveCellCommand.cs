using System.Collections.Generic;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class RemoveCellCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private DungeonCellData? _savedCell;
        private List<(ushort otherCellNum, DungeonCellPortalData portal)> _savedOtherPortals = new();

        public string Description => "Delete Cell";

        public RemoveCellCommand(ushort cellNum) {
            _cellNum = cellNum;
        }

        public void Execute(DungeonDocument document) {
            var src = document.GetCell(_cellNum);
            if (src == null) return;

            _savedCell = CloneCell(src);

            _savedOtherPortals.Clear();
            foreach (var other in document.Cells) {
                if (other.CellNumber == _cellNum) continue;
                foreach (var cp in other.CellPortals) {
                    if (cp.OtherCellId == _cellNum) {
                        _savedOtherPortals.Add((other.CellNumber, new DungeonCellPortalData {
                            OtherCellId = cp.OtherCellId,
                            PolygonId = cp.PolygonId,
                            OtherPortalId = cp.OtherPortalId,
                            Flags = cp.Flags
                        }));
                    }
                }
            }

            document.RemoveCell(_cellNum);
        }

        public void Undo(DungeonDocument document) {
            if (_savedCell == null) return;

            var restored = CloneCell(_savedCell);
            document.Cells.Add(restored);

            foreach (var (otherCellNum, portal) in _savedOtherPortals) {
                var otherCell = document.GetCell(otherCellNum);
                otherCell?.CellPortals.Add(portal);
            }
        }

        private static DungeonCellData CloneCell(DungeonCellData src) => new DungeonCellData {
            CellNumber = src.CellNumber,
            EnvironmentId = src.EnvironmentId,
            CellStructure = src.CellStructure,
            Origin = src.Origin,
            Orientation = src.Orientation,
            Flags = src.Flags,
            Surfaces = new List<ushort>(src.Surfaces),
            CellPortals = new List<DungeonCellPortalData>(src.CellPortals),
            VisibleCells = new List<ushort>(src.VisibleCells),
        };
    }
}
