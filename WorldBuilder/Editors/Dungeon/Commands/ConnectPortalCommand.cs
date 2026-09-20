using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class ConnectPortalCommand : IDungeonCommand {
        private readonly ushort _cellNumA;
        private readonly ushort _polyIdA;
        private readonly ushort _cellNumB;
        private readonly ushort _polyIdB;

        public string Description => "Connect Portal";

        public ConnectPortalCommand(ushort cellNumA, ushort polyIdA, ushort cellNumB, ushort polyIdB) {
            _cellNumA = cellNumA;
            _polyIdA = polyIdA;
            _cellNumB = cellNumB;
            _polyIdB = polyIdB;
        }

        public void Execute(DungeonDocument document) {
            document.ConnectPortals(_cellNumA, _polyIdA, _cellNumB, _polyIdB);
        }

        public void Undo(DungeonDocument document) {
            var cellA = document.GetCell(_cellNumA);
            var cellB = document.GetCell(_cellNumB);
            cellA?.CellPortals.RemoveAll(cp => cp.OtherCellId == _cellNumB && cp.PolygonId == _polyIdA);
            cellB?.CellPortals.RemoveAll(cp => cp.OtherCellId == _cellNumA && cp.PolygonId == _polyIdB);
            document.MarkDirty();
        }
    }
}
