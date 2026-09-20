using System.Collections.Generic;
using System.Linq;
using WorldBuilder.Lib.History;

namespace WorldBuilder.Editors.Landscape.Commands {
    /// <summary>
    /// Wraps multiple commands as a single undoable operation.
    /// </summary>
    public class CompoundCommand : ICommand {
        private readonly List<ICommand> _commands;

        public string Description { get; }
        public bool CanExecute => _commands.All(c => c.CanExecute);
        public bool CanUndo => _commands.All(c => c.CanUndo);
        public List<string> AffectedDocumentIds => _commands.SelectMany(c => c.AffectedDocumentIds).Distinct().ToList();

        public CompoundCommand(string description, List<ICommand> commands) {
            Description = description;
            _commands = commands;
        }

        public CompoundCommand(string description, params ICommand[] commands) {
            Description = description;
            _commands = commands.ToList();
        }

        public bool Execute() {
            var executed = new List<ICommand>();
            foreach (var cmd in _commands) {
                if (!cmd.Execute()) {
                    // Rollback already-executed commands on failure
                    for (int i = executed.Count - 1; i >= 0; i--) {
                        executed[i].Undo();
                    }
                    return false;
                }
                executed.Add(cmd);
            }
            return true;
        }

        public bool Undo() {
            // Undo in reverse order
            for (int i = _commands.Count - 1; i >= 0; i--) {
                if (!_commands[i].Undo()) return false;
            }
            return true;
        }
    }
}
