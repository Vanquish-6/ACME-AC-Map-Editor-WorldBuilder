using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class AddInstancePlacementCommand : IDungeonCommand {
        private readonly uint _weenieClassId;
        private readonly ushort _cellNum;
        private readonly Vector3 _origin;
        private readonly Quaternion _orientation;

        public string Description => "Place Weenie";

        public AddInstancePlacementCommand(uint weenieClassId, ushort cellNum, Vector3 origin, Quaternion orientation) {
            _weenieClassId = weenieClassId;
            _cellNum = cellNum;
            _origin = origin;
            _orientation = orientation;
        }

        public void Execute(DungeonDocument document) {
            document.InstancePlacements.Add(new DungeonInstancePlacement {
                WeenieClassId = _weenieClassId,
                CellNumber = _cellNum,
                Origin = _origin,
                Orientation = _orientation,
            });
        }

        public void Undo(DungeonDocument document) {
            if (document.InstancePlacements.Count == 0) return;
            var last = document.InstancePlacements[^1];
            if (last.WeenieClassId == _weenieClassId && last.CellNumber == _cellNum)
                document.InstancePlacements.RemoveAt(document.InstancePlacements.Count - 1);
        }
    }
}
