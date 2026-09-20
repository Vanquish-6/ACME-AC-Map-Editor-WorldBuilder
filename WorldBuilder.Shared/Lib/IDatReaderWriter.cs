using Acme.Dat;

namespace WorldBuilder.Shared.Lib {
    public interface IDatReaderWriter : IDisposable {
        DatProjectMode Mode { get; }
        bool CanWrite { get; }
        string CacheNamespace { get; }
        string DatDirectory { get; }

        bool ContainsFile(DatArchive archive, uint id);
        uint[] ListFileIds(DatArchive archive);
        bool TryGetFileBytes(DatArchive archive, uint id, out byte[]? bytes);
        bool TryWriteFileBytes(DatArchive archive, uint id, byte[] bytes, int iteration = 0);
        int GetIteration(DatArchive archive);
        void SetIteration(DatArchive archive, int iteration);
        void ResetTree(DatArchive archive);
        LandBlock[] ReadLandblocks();
        void Flush();

        bool TryGetLandblock(uint id, out LandBlock file);
        bool TrySaveLandblock(LandBlock file, int iteration = 0);
        bool TryGet<T>(uint id, out T file) where T : class, IDatRecord, new();
        bool TrySave<T>(T file, int? iteration = 0) where T : class, IDatRecord, new();
        IEnumerable<uint> GetAllIdsOfType<T>() where T : class, IDatRecord, new();

        /// <summary>Former DatCollection; this reader itself.</summary>
        IDatReaderWriter Dats { get; }

        DatCatalogTree Tree { get; }
        DatIterationInfo Iteration { get; }

        bool TryGetFileBytes(DatArchive archive, uint id, out byte[]? bytes, bool autoDecompress) {
            _ = autoDecompress;
            return TryGetFileBytes(archive, id, out bytes);
        }

        bool TryGetFileBytes(uint id, out byte[]? bytes, bool autoDecompress = false) {
            _ = autoDecompress;
            foreach (DatArchive archive in new[] { DatArchive.Portal, DatArchive.Cell, DatArchive.Local, DatArchive.Highres }) {
                if (TryGetFileBytes(archive, id, out bytes) && bytes != null) {
                    return true;
                }
            }

            bytes = null;
            return false;
        }

        bool TryWriteFileBytes(DatArchive archive, uint id, byte[] bytes, int length, int iteration) {
            if (length < bytes.Length) {
                bytes = bytes.AsSpan(0, Math.Max(0, length)).ToArray();
            }

            return TryWriteFileBytes(archive, id, bytes, iteration);
        }

        bool TryWriteFileBytes(uint id, byte[] bytes, int length, int iteration) {
            DatArchive archive = (id >> 24) switch {
                0x23 => DatArchive.Local,
                0x01 or 0x02 or 0x08 or 0x05 or 0x06 or 0x0D or 0x0E or 0x32 => DatArchive.Portal,
                _ when (id & 0xFFFF) is 0xFFFF or 0xFFFE => DatArchive.Cell,
                _ when (id & 0xFF00) != 0 && (id & 0xFFFF) < 0xFFFE => DatArchive.Cell,
                _ => DatArchive.Portal,
            };
            return TryWriteFileBytes(archive, id, bytes, length, iteration);
        }
    }
}
