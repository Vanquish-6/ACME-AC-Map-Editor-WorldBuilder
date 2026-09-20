using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class AddStaticObjectCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly uint _objectId;
        private readonly Vector3 _origin;
        private readonly Quaternion _orientation;
        private int _insertedIndex = -1;

        public string Description => "Place Object";

        public AddStaticObjectCommand(ushort cellNum, uint objectId, Vector3 origin, Quaternion orientation) {
            _cellNum = cellNum;
            _objectId = objectId;
            _origin = origin;
            _orientation = orientation;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell == null) return;
            // 0x00000000 is not a valid DID and 0x01xxxxxx is a GfxObj (not a Setup) — only
            // Setup DIDs (high byte 0x02) are valid stab references. Placing a GfxObj directly
            // would cause TryGet<Setup> to throw on every export and the client may not render it.
            if (_objectId == 0) return;
            if ((_objectId >> 24) != 0x02) return;
            cell.StaticObjects.Add(new DungeonStabData {
                Id = _objectId,
                Origin = _origin,
                Orientation = _orientation
            });
            _insertedIndex = cell.StaticObjects.Count - 1;
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell == null) return;
            if (_insertedIndex >= 0 && _insertedIndex < cell.StaticObjects.Count
                && cell.StaticObjects[_insertedIndex].Id == _objectId) {
                cell.StaticObjects.RemoveAt(_insertedIndex);
            }
            else {
                // Fallback: remove the last stab with matching ID
                for (int i = cell.StaticObjects.Count - 1; i >= 0; i--) {
                    if (cell.StaticObjects[i].Id == _objectId) {
                        cell.StaticObjects.RemoveAt(i);
                        break;
                    }
                }
            }
        }
    }
}
