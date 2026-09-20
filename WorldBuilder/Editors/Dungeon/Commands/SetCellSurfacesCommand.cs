using System.Collections.Generic;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class SetCellSurfacesCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly List<ushort> _newSurfaces;
        private readonly List<ushort> _oldSurfaces;

        public string Description => "Change Surfaces";

        public SetCellSurfacesCommand(ushort cellNum, List<ushort> oldSurfaces, List<ushort> newSurfaces) {
            _cellNum = cellNum;
            _oldSurfaces = new List<ushort>(oldSurfaces);
            _newSurfaces = new List<ushort>(newSurfaces);
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell == null) return;
            cell.Surfaces.Clear();
            cell.Surfaces.AddRange(_newSurfaces);
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell == null) return;
            cell.Surfaces.Clear();
            cell.Surfaces.AddRange(_oldSurfaces);
        }
    }
}
