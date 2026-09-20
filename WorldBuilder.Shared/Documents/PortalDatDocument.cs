using Acme.Dat;
using MemoryPack;
using Microsoft.Extensions.Logging;

namespace WorldBuilder.Shared.Documents {

    [MemoryPackable]
    public partial class PortalDatData {
        public Dictionary<uint, PortalDatEntry> Entries = new();
    }

    [MemoryPackable]
    public partial class PortalDatEntry {
        public string TypeName = "";
        public byte[] Data = Array.Empty<byte>();
    }

    public partial class PortalDatDocument : BaseDocument {
        public override string Type => nameof(PortalDatDocument);
        public const string DocumentId = "portal_tables";

        private PortalDatData _data = new();
        private readonly Dictionary<uint, object> _objectCache = new();
        private readonly HashSet<uint> _unpackFailures = new();

        public PortalDatDocument(ILogger logger) : base(logger) { }

        public bool HasEntry(uint fileId) =>
            _objectCache.ContainsKey(fileId) || _data.Entries.ContainsKey(fileId);

        public int EntryCount => _data.Entries.Count;
        public IEnumerable<uint> GetEntryIds() => _data.Entries.Keys;

        public void SetEntry<T>(uint fileId, T obj) where T : class, IDatRecord, new() {
            _objectCache[fileId] = obj;
            try {
                _data.Entries[fileId] = new PortalDatEntry {
                    TypeName = typeof(T).Name,
                    Data = Encode(obj),
                };
            }
            catch (Exception ex) {
                _logger.LogError(ex, "[PortalDatDoc] Failed to pack entry 0x{FileId:X8}", fileId);
                _data.Entries[fileId] = new PortalDatEntry { TypeName = typeof(T).Name, Data = [] };
            }

            MarkDirty();
            OnUpdate(new BaseDocumentEvent());
        }

        public bool TryGetEntry<T>(uint fileId, out T? obj) where T : class, IDatRecord, new() {
            if (_objectCache.TryGetValue(fileId, out var cached) && cached is T typed) {
                obj = typed;
                return true;
            }

            if (_unpackFailures.Contains(fileId)) {
                obj = default;
                return false;
            }

            if (_data.Entries.TryGetValue(fileId, out var entry) && entry.Data.Length > 0) {
                try {
                    obj = Decode<T>(entry.Data);
                    _objectCache[fileId] = obj;
                    return true;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "[PortalDatDoc] Failed to unpack entry 0x{FileId:X8}", fileId);
                    _unpackFailures.Add(fileId);
                }
            }

            obj = default;
            return false;
        }

        public void RemoveEntry(uint fileId) {
            _data.Entries.Remove(fileId);
            _objectCache.Remove(fileId);
            _unpackFailures.Remove(fileId);
            MarkDirty();
            OnUpdate(new BaseDocumentEvent());
        }

        protected override Task<bool> InitInternal(IDatReaderWriter datreader, DocumentManager documentManager) {
            ClearDirty();
            return Task.FromResult(true);
        }

        protected override byte[] SaveToProjectionInternal() {
            SyncCacheToData();
            return MemoryPackSerializer.Serialize(_data);
        }

        protected override bool LoadFromProjectionInternal(byte[] projection) {
            try {
                _data = MemoryPackSerializer.Deserialize<PortalDatData>(projection) ?? new();
                _objectCache.Clear();
                _unpackFailures.Clear();
                return true;
            }
            catch (MemoryPackSerializationException) {
                _logger.LogWarning("[PortalDatDoc] Project cache has incompatible format, resetting");
                _data = new();
                _objectCache.Clear();
                _unpackFailures.Clear();
                return true;
            }
        }

        protected override Task<bool> SaveToDatsInternal(IDatReaderWriter datwriter, int iteration = 0) {
            SyncCacheToData();
            foreach (var (fileId, entry) in _data.Entries) {
                bool saved = false;
                if (_objectCache.TryGetValue(fileId, out var cachedObj)) {
                    saved = TrySaveTyped(datwriter, cachedObj, iteration);
                }

                if (!saved && entry.Data.Length > 0) {
                    saved = TrySaveFromBytes(datwriter, entry, fileId, iteration);
                }

                if (saved) {
                    _logger.LogInformation("[PortalDatDoc] Exported 0x{FileId:X8} ({Type})", fileId, entry.TypeName);
                }
                else {
                    _logger.LogError("[PortalDatDoc] Failed to export 0x{FileId:X8} ({Type})", fileId, entry.TypeName);
                }
            }

            return Task.FromResult(true);
        }

        private void SyncCacheToData() {
            foreach (var (fileId, obj) in _objectCache) {
                if (!_data.Entries.TryGetValue(fileId, out var entry)) continue;
                try {
                    if (obj is IDatRecord record) {
                        entry.Data = Encode(record);
                    }
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "[PortalDatDoc] Failed to re-pack entry 0x{FileId:X8}", fileId);
                }
            }
        }

        private static bool TrySaveTyped(IDatReaderWriter writer, object obj, int iteration) => obj switch {
            SpellTable t => writer.TrySave(t, iteration),
            SpellComponentTable t => writer.TrySave(t, iteration),
            VitalTable t => writer.TrySave(t, iteration),
            SkillTable t => writer.TrySave(t, iteration),
            ExperienceTable t => writer.TrySave(t, iteration),
            CharGen t => writer.TrySave(t, iteration),
            GfxObj t => writer.TrySave(t, iteration),
            Setup t => writer.TrySave(t, iteration),
            RenderSurface t => writer.TrySave(t, iteration),
            _ => false
        };

        private bool TrySaveFromBytes(IDatReaderWriter writer, PortalDatEntry entry, uint fileId, int iteration) {
            try {
                return entry.TypeName switch {
                    nameof(SpellTable) => UnpackAndSave<SpellTable>(writer, entry.Data, iteration),
                    nameof(SpellComponentTable) => UnpackAndSave<SpellComponentTable>(writer, entry.Data, iteration),
                    nameof(VitalTable) => UnpackAndSave<VitalTable>(writer, entry.Data, iteration),
                    nameof(SkillTable) => UnpackAndSave<SkillTable>(writer, entry.Data, iteration),
                    nameof(ExperienceTable) => UnpackAndSave<ExperienceTable>(writer, entry.Data, iteration),
                    nameof(CharGen) => UnpackAndSave<CharGen>(writer, entry.Data, iteration),
                    nameof(GfxObj) => UnpackAndSave<GfxObj>(writer, entry.Data, iteration),
                    nameof(Setup) => UnpackAndSave<Setup>(writer, entry.Data, iteration),
                    nameof(RenderSurface) => UnpackAndSave<RenderSurface>(writer, entry.Data, iteration),
                    _ => false
                };
            }
            catch (Exception ex) {
                _logger.LogError(ex, "[PortalDatDoc] Failed to unpack-and-save {Type}", entry.TypeName);
                _unpackFailures.Add(fileId);
                return false;
            }
        }

        private static bool UnpackAndSave<T>(IDatReaderWriter writer, byte[] data, int iteration)
            where T : class, IDatRecord, new() =>
            writer.TrySave(Decode<T>(data), iteration);

        static byte[] Encode<T>(T record) where T : class, IDatRecord {
            if (!DatRecordTable.TryGet(typeof(T), out var info) && record is IDatRecord) {
                DatRecordTable.TryGet(record.GetType(), out info);
            }
            if (!DatRecordTable.TryGet(record.GetType(), out var kindInfo)) {
                throw new NotSupportedException(record.GetType().Name);
            }

            return AethDatNative.EncodeStatic((uint)kindInfo.Kind, record);
        }

        static T Decode<T>(byte[] data) where T : class, IDatRecord, new() {
            if (!DatRecordTable.TryGet(typeof(T), out var info)) {
                throw new NotSupportedException(typeof(T).Name);
            }

            return AethDatNative.DecodeStatic<T>((uint)info.Kind, data);
        }
    }
}
