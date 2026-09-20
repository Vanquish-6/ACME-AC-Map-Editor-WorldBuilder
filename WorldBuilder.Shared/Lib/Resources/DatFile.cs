using Acme.Dat;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Shared.Lib.Resources {
    public class DatFile<T> : IDatResource<T> where T : class, IDatRecord, new() {
        private readonly IDatReaderWriter _datReader;

        public DatFile(uint id, IDatReaderWriter datReader, ResourceManager resourceManager) : base(id, resourceManager) {
            _datReader = datReader;
        }

        protected override Task<bool> LoadInternal() {
            if (_datReader.TryGet(Id, out T file)) {
                File = file;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public override void Dispose() {
            base.Dispose();
        }
    }
}
