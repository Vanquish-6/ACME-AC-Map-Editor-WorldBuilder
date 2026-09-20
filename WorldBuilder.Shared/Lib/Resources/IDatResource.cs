using Acme.Dat;

namespace WorldBuilder.Shared.Lib.Resources {
    public abstract class IDatResource<T> : IModelResource where T : class, IDatRecord, new() {
        public T? File { get; protected set; }

        public IDatResource(uint id, ResourceManager resourceManager) : base(id, resourceManager) {
        }
    }
}
