using System.Collections.Generic;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public class DungeonCompositeCommand : IDungeonCommand {
        private readonly List<IDungeonCommand> _commands = new();
        public string Description { get; }

        public DungeonCompositeCommand(string description) { Description = description; }

        public void Add(IDungeonCommand cmd) => _commands.Add(cmd);

        public void Execute(DungeonDocument document) {
            foreach (var cmd in _commands) cmd.Execute(document);
        }

        public void Undo(DungeonDocument document) {
            for (int i = _commands.Count - 1; i >= 0; i--) _commands[i].Undo(document);
        }
    }
}
