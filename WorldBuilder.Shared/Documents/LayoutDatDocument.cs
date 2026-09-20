using Acme.Dat;
using MemoryPack;
using Microsoft.Extensions.Logging;

namespace WorldBuilder.Shared.Documents {

    [MemoryPackable]
    public partial class LayoutDatEntry {
        public string TypeName = "";
        public byte[] Data = Array.Empty<byte>();
    }

    [MemoryPackable]
    public partial class LayoutDatData {
        public Dictionary<uint, LayoutDatEntry> Entries = new();
    }

    public partial class LayoutDatDocument : BaseDocument {
        public override string Type => nameof(LayoutDatDocument);
        public const string DocumentId = "ui_layouts";

        private LayoutDatData _data = new();
        private readonly Dictionary<uint, object> _objectCache = new();

        public LayoutDatDocument(ILogger logger) : base(logger) { }

        public int EntryCount => _data.Entries.Count;
        public bool HasStoredLayout(uint layoutId) => _data.Entries.ContainsKey(layoutId);

        public void SetLayout(uint layoutId, LayoutDesc layout) {
            layout.Id = layoutId;
            _objectCache[layoutId] = layout;
            try {
                _data.Entries[layoutId] = new LayoutDatEntry {
                    TypeName = nameof(LayoutDesc),
                    Data = AethDatNative.EncodeStatic((uint)DatKind.LayoutDesc, layout),
                };
            }
            catch (Exception ex) {
                _logger.LogError(ex, "[LayoutDatDoc] Failed to pack layout 0x{Id:X8}", layoutId);
                _data.Entries[layoutId] = new LayoutDatEntry { TypeName = nameof(LayoutDesc), Data = [] };
            }

            MarkDirty();
            OnUpdate(new BaseDocumentEvent());
        }

        public bool TryGetLayout(uint layoutId, out LayoutDesc? layout) {
            if (_objectCache.TryGetValue(layoutId, out var cached) && cached is LayoutDesc typed) {
                layout = typed;
                return true;
            }

            if (_data.Entries.TryGetValue(layoutId, out var entry) && entry.Data.Length > 0) {
                try {
                    layout = AethDatNative.DecodeStatic<LayoutDesc>((uint)DatKind.LayoutDesc, entry.Data);
                    layout.Id = layoutId;
                    _objectCache[layoutId] = layout;
                    return true;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "[LayoutDatDoc] Failed to unpack layout 0x{Id:X8}", layoutId);
                }
            }

            layout = default;
            return false;
        }

        public void RemoveLayout(uint layoutId) {
            _data.Entries.Remove(layoutId);
            _objectCache.Remove(layoutId);
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
                _data = MemoryPackSerializer.Deserialize<LayoutDatData>(projection) ?? new();
                _objectCache.Clear();
                return true;
            }
            catch (MemoryPackSerializationException) {
                _logger.LogWarning("[LayoutDatDoc] Cache schema mismatch; resetting layout overrides");
                _data = new();
                _objectCache.Clear();
                return true;
            }
        }

        protected override Task<bool> SaveToDatsInternal(IDatReaderWriter datwriter, int iteration = 0) {
            SyncCacheToData();
            foreach (var (layoutId, entry) in _data.Entries) {
                bool saved = false;
                if (_objectCache.TryGetValue(layoutId, out var cachedObj) && cachedObj is LayoutDesc live) {
                    live.Id = layoutId;
                    saved = datwriter.TrySave(live, iteration);
                }

                if (!saved && entry.Data.Length > 0) {
                    try {
                        var obj = AethDatNative.DecodeStatic<LayoutDesc>((uint)DatKind.LayoutDesc, entry.Data);
                        obj.Id = layoutId;
                        saved = datwriter.TrySave(obj, iteration);
                    }
                    catch (Exception ex) {
                        _logger.LogError(ex, "[LayoutDatDoc] Failed to export layout 0x{Id:X8}", layoutId);
                    }
                }

                if (saved) {
                    _logger.LogInformation("[LayoutDatDoc] Exported layout 0x{Id:X8}", layoutId);
                }
            }

            ClearDirty();
            return Task.FromResult(true);
        }

        private void SyncCacheToData() {
            foreach (var (layoutId, obj) in _objectCache) {
                if (obj is not LayoutDesc live) continue;
                if (!_data.Entries.TryGetValue(layoutId, out var entry)) continue;
                try {
                    live.Id = layoutId;
                    entry.Data = AethDatNative.EncodeStatic((uint)DatKind.LayoutDesc, live);
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "[LayoutDatDoc] Re-pack failed for 0x{Id:X8}", layoutId);
                }
            }
        }
    }
}
