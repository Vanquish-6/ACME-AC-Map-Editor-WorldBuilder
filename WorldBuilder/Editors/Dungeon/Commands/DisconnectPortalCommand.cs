using System;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class DisconnectPortalCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly int _portalIndex;
        private DungeonCellPortalData? _savedPortal;
        private DungeonCellPortalData? _savedBackPortal;
        private ushort _otherCellNum;

        public string Description => "Disconnect Portal";

        public DisconnectPortalCommand(ushort cellNum, int portalIndex) {
            _cellNum = cellNum;
            _portalIndex = portalIndex;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell == null || _portalIndex >= cell.CellPortals.Count) return;

            var portal = cell.CellPortals[_portalIndex];
            _savedPortal = new DungeonCellPortalData {
                OtherCellId = portal.OtherCellId,
                PolygonId = portal.PolygonId,
                OtherPortalId = portal.OtherPortalId,
                Flags = portal.Flags
            };
            _otherCellNum = portal.OtherCellId;

            cell.CellPortals.RemoveAt(_portalIndex);

            var otherCell = document.GetCell(_otherCellNum);
            if (otherCell != null) {
                var backIdx = otherCell.CellPortals.FindIndex(cp => cp.OtherCellId == _cellNum);
                if (backIdx >= 0) {
                    var bp = otherCell.CellPortals[backIdx];
                    _savedBackPortal = new DungeonCellPortalData {
                        OtherCellId = bp.OtherCellId,
                        PolygonId = bp.PolygonId,
                        OtherPortalId = bp.OtherPortalId,
                        Flags = bp.Flags
                    };
                    otherCell.CellPortals.RemoveAt(backIdx);
                }
            }

            document.MarkDirty();
        }

        public void Undo(DungeonDocument document) {
            if (_savedPortal == null) return;

            var cell = document.GetCell(_cellNum);
            if (cell != null) {
                cell.CellPortals.Insert(Math.Min(_portalIndex, cell.CellPortals.Count), _savedPortal);
            }

            if (_savedBackPortal != null) {
                var otherCell = document.GetCell(_otherCellNum);
                otherCell?.CellPortals.Add(_savedBackPortal);
            }

            document.MarkDirty();
        }
    }
}
