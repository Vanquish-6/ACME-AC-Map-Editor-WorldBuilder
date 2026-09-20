using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class SetCellOrientationCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly Quaternion _newOrientation;
        private Quaternion _oldOrientation;

        public string Description => "Set Rotation";

        public SetCellOrientationCommand(ushort cellNum, Quaternion oldOrientation, Quaternion newOrientation) {
            _cellNum = cellNum;
            _oldOrientation = oldOrientation;
            _newOrientation = newOrientation;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null) cell.Orientation = _newOrientation;
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null) cell.Orientation = _oldOrientation;
        }
    }
}
