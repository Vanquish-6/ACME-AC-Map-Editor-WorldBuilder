using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class SetObjectOrientationCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly int _objectIndex;
        private readonly Quaternion _oldOrientation;
        private readonly Quaternion _newOrientation;

        public string Description => "Set Object Orientation";

        public SetObjectOrientationCommand(ushort cellNum, int objectIndex,
            Quaternion oldOrientation, Quaternion newOrientation) {
            _cellNum = cellNum;
            _objectIndex = objectIndex;
            _oldOrientation = oldOrientation;
            _newOrientation = newOrientation;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Orientation = _newOrientation;
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Orientation = _oldOrientation;
        }
    }
}
