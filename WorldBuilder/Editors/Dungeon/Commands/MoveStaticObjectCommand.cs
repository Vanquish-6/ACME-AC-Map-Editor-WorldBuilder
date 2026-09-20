using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class MoveStaticObjectCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly int _objectIndex;
        private readonly Vector3 _delta;

        public string Description => "Move Object";

        public MoveStaticObjectCommand(ushort cellNum, int objectIndex, Vector3 delta) {
            _cellNum = cellNum;
            _objectIndex = objectIndex;
            _delta = delta;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Origin += _delta;
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Origin -= _delta;
        }
    }
}
