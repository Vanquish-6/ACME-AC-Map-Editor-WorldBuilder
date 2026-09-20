using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class NudgeCellCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly Vector3 _offset;

        public string Description => "Move room";

        public NudgeCellCommand(ushort cellNum, Vector3 offset) {
            _cellNum = cellNum;
            _offset = offset;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null) cell.Origin += _offset;
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null) cell.Origin -= _offset;
        }
    }
}
