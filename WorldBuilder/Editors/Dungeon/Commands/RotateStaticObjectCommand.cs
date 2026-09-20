using System;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class RotateStaticObjectCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly int _objectIndex;
        private readonly Quaternion _rotation;
        private readonly Quaternion _inverseRotation;

        public string Description => "Rotate Object";

        public RotateStaticObjectCommand(ushort cellNum, int objectIndex, float degrees) {
            _cellNum = cellNum;
            _objectIndex = objectIndex;
            _rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);
            _inverseRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -degrees * MathF.PI / 180f);
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Orientation = Quaternion.Normalize(_rotation * cell.StaticObjects[_objectIndex].Orientation);
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Orientation = Quaternion.Normalize(_inverseRotation * cell.StaticObjects[_objectIndex].Orientation);
        }
    }
}
