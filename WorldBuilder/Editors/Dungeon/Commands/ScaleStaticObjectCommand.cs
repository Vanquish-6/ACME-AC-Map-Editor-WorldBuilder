using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class ScaleStaticObjectCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly int _objectIndex;
        private readonly Vector3 _oldScale;
        private readonly Vector3 _newScale;

        public string Description => "Scale Object";

        public ScaleStaticObjectCommand(ushort cellNum, int objectIndex,
            Vector3 oldScale, Vector3 newScale) {
            _cellNum = cellNum;
            _objectIndex = objectIndex;
            _oldScale = oldScale;
            _newScale = newScale;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Scale = _newScale;
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Scale = _oldScale;
        }
    }
}
