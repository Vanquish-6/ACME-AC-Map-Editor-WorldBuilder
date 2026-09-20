using System;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class RotateCellCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly Quaternion _rotation;
        private readonly Quaternion _inverseRotation;

        public string Description => "Rotate room";

        public RotateCellCommand(ushort cellNum, float degrees, Vector3? axis = null) {
            _cellNum = cellNum;
            var ax = axis ?? Vector3.UnitZ;
            _rotation = Quaternion.CreateFromAxisAngle(ax, degrees * MathF.PI / 180f);
            _inverseRotation = Quaternion.CreateFromAxisAngle(ax, -degrees * MathF.PI / 180f);
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null) cell.Orientation = Quaternion.Normalize(_rotation * cell.Orientation);
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null) cell.Orientation = Quaternion.Normalize(_inverseRotation * cell.Orientation);
        }
    }
}
