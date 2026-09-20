using System;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class DeleteStaticObjectCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly int _objectIndex;
        private DungeonStabData? _savedObject;

        public string Description => "Delete Object";

        public DeleteStaticObjectCommand(ushort cellNum, int objectIndex) {
            _cellNum = cellNum;
            _objectIndex = objectIndex;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell == null || _objectIndex >= cell.StaticObjects.Count) return;
            _savedObject = new DungeonStabData {
                Id = cell.StaticObjects[_objectIndex].Id,
                Origin = cell.StaticObjects[_objectIndex].Origin,
                Orientation = cell.StaticObjects[_objectIndex].Orientation
            };
            cell.StaticObjects.RemoveAt(_objectIndex);
        }

        public void Undo(DungeonDocument document) {
            if (_savedObject == null) return;
            var cell = document.GetCell(_cellNum);
            if (cell == null) return;
            cell.StaticObjects.Insert(Math.Min(_objectIndex, cell.StaticObjects.Count), _savedObject);
        }
    }
}
