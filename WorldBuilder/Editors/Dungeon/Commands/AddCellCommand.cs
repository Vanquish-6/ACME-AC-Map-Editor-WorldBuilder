using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class AddCellCommand : IDungeonCommand {
        private readonly ushort _environmentId;
        private readonly ushort _cellStructure;
        private readonly Vector3 _origin;
        private readonly Quaternion _orientation;
        private readonly List<ushort> _surfaces;
        private readonly ushort? _connectToCellNum;
        private readonly ushort _connectToPolyId;
        private readonly ushort _sourcePolyId;
        private ushort _createdCellNum;

        public ushort CreatedCellNum => _createdCellNum;

        public string Description => "Place room";

        public AddCellCommand(ushort envId, ushort cellStruct, Vector3 origin, Quaternion orientation,
            List<ushort> surfaces, ushort? connectToCellNum = null, ushort connectToPolyId = 0, ushort sourcePolyId = 0) {
            _environmentId = envId;
            _cellStructure = cellStruct;
            _origin = origin;
            _orientation = orientation;
            _surfaces = new List<ushort>(surfaces);
            _connectToCellNum = connectToCellNum;
            _connectToPolyId = connectToPolyId;
            _sourcePolyId = sourcePolyId;
        }

        public void Execute(DungeonDocument document) {
            _createdCellNum = document.AddCell(_environmentId, _cellStructure, _origin, _orientation, _surfaces);
            if (_connectToCellNum.HasValue) {
                document.ConnectPortals(_connectToCellNum.Value, _connectToPolyId, _createdCellNum, _sourcePolyId);
            }
        }

        public void Undo(DungeonDocument document) {
            document.RemoveCell(_createdCellNum);
        }
    }
}
