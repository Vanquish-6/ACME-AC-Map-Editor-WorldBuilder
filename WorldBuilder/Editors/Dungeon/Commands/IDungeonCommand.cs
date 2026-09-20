using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public interface IDungeonCommand {
        void Execute(DungeonDocument document);
        void Undo(DungeonDocument document);
        string Description { get; }
    }
}
