using Acme.Dat;
using System.Diagnostics.CodeAnalysis;

namespace WorldBuilder.Shared.Lib {
    public class DefaultDatReaderWriter : IDatReaderWriter {
        private readonly object _lock = new();
        private readonly AethDatNative _native;
        private readonly bool _canWrite;
        private readonly FileCachingStrategy _fileCaching;
        private readonly Dictionary<DatArchive, uint[]> _idCache = new();
        private readonly Dictionary<(Type Type, uint Id), object> _recordCache = new();
        private readonly LinkedList<(Type Type, uint Id)> _envLruOrder = new();
        private readonly Dictionary<(Type Type, uint Id), LinkedListNode<(Type Type, uint Id)>> _envLruNodes = new();
        private readonly DatArchive _primaryArchive;

        /// <summary>
        /// Environments are large (every CellStruct). Catalog scans must not pin thousands of them.
        /// </summary>
        internal const int MaxCachedEnvironments = 96;

        public DatProjectMode Mode { get; }
        public bool CanWrite => _canWrite;
        public string CacheNamespace { get; }
        public string DatDirectory { get; }
        public DatArchive PrimaryArchive => _primaryArchive;
        public DatCatalogTree Tree => new DatArchiveSession(this, _primaryArchive).Tree;
        public DatIterationInfo Iteration => new DatArchiveSession(this, _primaryArchive).Iteration;

        public DefaultDatReaderWriter(string datPath, DatAccessType accessType)
            : this(datPath, accessType, FileCachingStrategy.OnDemand, IndexCachingStrategy.OnDemand) {
        }

        public DefaultDatReaderWriter(string datPath, DatAccessType accessType,
            FileCachingStrategy fileCaching, IndexCachingStrategy indexCaching)
            : this(NormalizePath(datPath, out var archive), accessType, fileCaching, indexCaching, archive) {
        }

        public DefaultDatReaderWriter(string datPath, DatAccessType accessType, DatArchive primaryArchive)
            : this(datPath, accessType, FileCachingStrategy.OnDemand, IndexCachingStrategy.OnDemand, primaryArchive) {
        }

        protected DefaultDatReaderWriter(
            string datPath,
            DatAccessType accessType,
            FileCachingStrategy fileCaching,
            IndexCachingStrategy indexCaching,
            DatArchive primaryArchive) {
            _fileCaching = fileCaching;
            _ = indexCaching;
            _primaryArchive = primaryArchive;
            if (accessType == DatAccessType.ReadWrite && Directory.Exists(datPath)) {
                SyncRetailClientDatHeaderSizes(datPath);
            }

            DatDirectory = Path.GetFullPath(datPath);
            _canWrite = accessType == DatAccessType.ReadWrite;
            Mode = DetectMode(datPath);
            CacheNamespace = DatCacheNamespace.FromDatDirectory(datPath, Mode);
            _native = new AethDatNative(DatDirectory, _canWrite);
        }

        private static string NormalizePath(string datPath, out DatArchive archive) {
            archive = Directory.Exists(datPath)
                ? DatArchive.Portal
                : DatDirectoryLayout.ArchiveFromPath(datPath);
            if (File.Exists(datPath) && !Directory.Exists(datPath)) {
                return DatDirectoryLayout.Isolate(datPath, DatDirectoryLayout.RetailFileName(archive));
            }

            return datPath;
        }

        public static DefaultDatReaderWriter CreateEmpty(string datPath) {
            var native = AethDatNative.Create(datPath);
            native.Dispose();
            return new DefaultDatReaderWriter(datPath, DatAccessType.ReadWrite);
        }

        public bool ContainsFile(DatArchive archive, uint id) {
            lock (_lock) return _native.ContainsFile(archive, id);
        }

        public uint[] ListFileIds(DatArchive archive) {
            lock (_lock) {
                if (!_idCache.TryGetValue(archive, out var ids)) {
                    ids = _native.ListFileIds(archive);
                    _idCache[archive] = ids;
                }
                return ids;
            }
        }

        public bool TryGetFileBytes(DatArchive archive, uint id, out byte[]? bytes) {
            lock (_lock) return _native.TryGetFileBytes(archive, id, out bytes);
        }

        public bool TryGetFileBytes(DatArchive archive, uint id, out byte[]? bytes, bool autoDecompress) {
            _ = autoDecompress;
            return TryGetFileBytes(archive, id, out bytes);
        }

        public bool TryGetFileBytes(uint id, out byte[]? bytes, bool autoDecompress = false) =>
            ((IDatReaderWriter)this).TryGetFileBytes(id, out bytes, autoDecompress);

        public bool TryWriteFileBytes(DatArchive archive, uint id, byte[] bytes, int iteration = 0) {
            lock (_lock) {
                if (!_canWrite) return false;
                _native.WriteFileBytes(archive, id, bytes, iteration);
                _idCache.Remove(archive);
                InvalidateRecordCache(id);
                return true;
            }
        }

        public bool TryWriteFileBytes(DatArchive archive, uint id, byte[] bytes, int length, int iteration) =>
            ((IDatReaderWriter)this).TryWriteFileBytes(archive, id, bytes, length, iteration);

        public bool TryWriteFileBytes(uint id, byte[] bytes, int length, int iteration) =>
            ((IDatReaderWriter)this).TryWriteFileBytes(id, bytes, length, iteration);

        public int GetIteration(DatArchive archive) {
            lock (_lock) return _native.GetIteration(archive);
        }

        public void SetIteration(DatArchive archive, int iteration) {
            lock (_lock) {
                if (!_canWrite) return;
                _native.SetIteration(archive, iteration);
            }
        }

        public LandBlock[] ReadLandblocks() {
            lock (_lock) return _native.ReadLandblocks();
        }

        public void ResetTree(DatArchive archive) {
            lock (_lock) {
                if (!_canWrite) return;
                _native.ResetTree(archive);
                _idCache.Remove(archive);
                InvalidateRecordCache();
            }
        }

        public void Flush() {
            lock (_lock) _native.Flush();
        }

        public bool TryGetLandblock(uint id, out LandBlock file) {
            lock (_lock) {
                file = null!;
                if (!_native.TryGetFileBytes(DatArchive.Cell, id, out byte[]? bytes) || bytes == null) return false;
                file = AethDatNative.DecodeLandblock(bytes);
                return true;
            }
        }

        public bool TrySaveLandblock(LandBlock file, int iteration = 0) {
            lock (_lock) {
                if (!_canWrite) return false;
                byte[] bytes = AethDatNative.EncodeLandblock(file);
                _native.WriteFileBytes(DatArchive.Cell, file.Id, bytes, iteration);
                _idCache.Remove(DatArchive.Cell);
                return true;
            }
        }

        public bool TryGet<T>(uint id, [MaybeNullWhen(false)] out T file) where T : class, IDatRecord, new() {
            file = default;
            if (!DatRecordTable.TryGet(typeof(T), out var info)) {
                throw new NotImplementedException($"DefaultDatReaderWriter does not currently support {typeof(T)}");
            }

            var cacheKey = (typeof(T), id);
            byte[]? bytes = null;

            lock (_lock) {
                if (_fileCaching == FileCachingStrategy.OnDemand
                    && IsCachedRecordType(typeof(T))
                    && _recordCache.TryGetValue(cacheKey, out var cached)) {
                    TouchEnvironmentLru(cacheKey);
                    file = (T)cached;
                    return true;
                }

                var archives = ArchivesFor(info);
                var idsToTry = typeof(T) == typeof(Region) && id == 0x13000000u
                    ? new[] { id, 0x130F0000u }
                    : new[] { id };
                foreach (uint tryId in idsToTry) {
                    foreach (var archive in archives) {
                        if (_native.TryGetFileBytes(archive, tryId, out byte[]? found) && found != null) {
                            bytes = found;
                            break;
                        }
                    }
                    if (bytes != null) break;
                }
                if (bytes == null) return false;
            }

            T decoded;
            try {
                decoded = AethDatNative.DecodeStatic<T>((uint)info.Kind, bytes);
                AssignId(decoded, id);
            }
            catch {
                return false;
            }

            lock (_lock) {
                if (_fileCaching == FileCachingStrategy.OnDemand && IsCachedRecordType(typeof(T))) {
                    if (_recordCache.TryGetValue(cacheKey, out var raced)) {
                        TouchEnvironmentLru(cacheKey);
                        file = (T)raced;
                        return true;
                    }
                    InsertRecordCache(cacheKey, decoded);
                }
                file = decoded;
                return true;
            }
        }

        public bool TrySave<T>(T file, int? iteration = 0) where T : class, IDatRecord, new() {
            lock (_lock) {
                if (!_canWrite) return false;
                if (!DatRecordTable.TryGet(typeof(T), out var info)) {
                    throw new NotImplementedException($"DefaultDatReaderWriter does not currently support {typeof(T)}");
                }
                uint id = GetRecordId(file);
                byte[] bytes = _native.Encode((uint)info.Kind, file);
                _native.WriteFileBytes(info.Archive, id, bytes, iteration ?? 0);
                _idCache.Remove(info.Archive);
                if (IsCachedRecordType(typeof(T))) {
                    InsertRecordCache((typeof(T), id), file);
                }
                else {
                    InvalidateRecordCache(id);
                }
                return true;
            }
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : class, IDatRecord, new() {
            if (!DatRecordTable.TryGet(typeof(T), out var info)) {
                return Array.Empty<uint>();
            }
            var ids = new List<uint>();
            foreach (var archive in ArchivesFor(info)) {
                ids.AddRange(DatRecordTable.FilterIds(typeof(T), ListFileIds(archive)));
            }
            return ids;
        }

        public IDatReaderWriter Dats => this;

        public void Dispose() {
            lock (_lock) {
                ClearRecordCache();
                _idCache.Clear();
            }
            _native.Dispose();
        }

        private static bool IsCachedRecordType(Type type) =>
            type == typeof(Acme.Dat.Environment)
            || type == typeof(Surface)
            || type == typeof(SurfaceTexture)
            || type == typeof(RenderSurface)
            || type == typeof(Palette)
            || type == typeof(GfxObj)
            || type == typeof(Setup)
            || type == typeof(Scene);

        private void InsertRecordCache((Type Type, uint Id) key, object value) {
            _recordCache[key] = value;
            if (key.Type != typeof(Acme.Dat.Environment)) return;

            if (_envLruNodes.TryGetValue(key, out var existing)) {
                _envLruOrder.Remove(existing);
                _envLruOrder.AddFirst(existing);
                return;
            }

            while (_envLruOrder.Count >= MaxCachedEnvironments) {
                var last = _envLruOrder.Last!;
                _envLruOrder.RemoveLast();
                _envLruNodes.Remove(last.Value);
                _recordCache.Remove(last.Value);
            }

            _envLruNodes[key] = _envLruOrder.AddFirst(key);
        }

        private void TouchEnvironmentLru((Type Type, uint Id) key) {
            if (key.Type != typeof(Acme.Dat.Environment)) return;
            if (!_envLruNodes.TryGetValue(key, out var node)) return;
            _envLruOrder.Remove(node);
            _envLruOrder.AddFirst(node);
        }

        private void RemoveEnvironmentLru((Type Type, uint Id) key) {
            if (!_envLruNodes.Remove(key, out var node)) return;
            _envLruOrder.Remove(node);
        }

        private void ClearRecordCache() {
            _recordCache.Clear();
            _envLruOrder.Clear();
            _envLruNodes.Clear();
        }

        private void InvalidateRecordCache(uint? id = null) {
            if (id == null) {
                ClearRecordCache();
                return;
            }

            List<(Type Type, uint Id)>? remove = null;
            foreach (var key in _recordCache.Keys) {
                if (key.Id == id.Value) {
                    remove ??= new List<(Type Type, uint Id)>();
                    remove.Add(key);
                }
            }
            if (remove == null) return;
            foreach (var key in remove) {
                _recordCache.Remove(key);
                RemoveEnvironmentLru(key);
            }
        }

        private static DatArchive[] ArchivesFor(DatRecordInfo info) {
            if (info.Kind is DatKind.RenderTexture or DatKind.RenderSurface or DatKind.Palette
                or DatKind.SurfaceTexture) {
                return [info.Archive, DatArchive.Highres];
            }
            return [info.Archive];
        }

        private static void AssignId(IDatRecord file, uint id) {
            switch (file) {
                case Surface s: s.Id = id; break;
                case ExperienceTable xp: xp.Id = id; break;
                case SkillTable skills: skills.Id = id; break;
                case VitalTable vitals: vitals.Id = id; break;
                case CharGen chargen: chargen.Id = id; break;
                case Region region: region.Id = id; break;
                case SpellTable: break;
                case SpellComponentTable: break;
            }
        }

        private static uint GetRecordId(IDatRecord file) => file switch {
            LandBlock lb => lb.Id,
            LandBlockInfo lbi => lbi.Id,
            EnvCell cell => cell.Id,
            Setup setup => setup.Id,
            Scene scene => scene.Id,
            Palette pal => pal.Id,
            SurfaceTexture st => st.Id,
            RenderTexture rt => rt.Id,
            RenderSurface rs => rs.Id,
            ParticleEmitter pe => pe.Id,
            LayoutDesc layout => layout.Id,
            StringTable strings => strings.Id,
            ClothingTable clothing => clothing.Id,
            PalSet palSet => palSet.Id,
            GfxObj gfx => gfx.Id,
            Acme.Dat.Environment env => env.Id,
            Region region => region.Id,
            Surface s => s.Id,
            ExperienceTable => 0x0E000018,
            SkillTable => 0x0E000004,
            VitalTable => 0x0E000003,
            CharGen => 0x0E000002,
            SpellTable => 0x0E00000E,
            SpellComponentTable => 0x0E00000F,
            _ => throw new NotImplementedException($"No Id on {file.GetType().Name}"),
        };

        private static DatProjectMode DetectMode(string datPath) {
            if (File.Exists(Path.Combine(datPath, "client_portal.dat"))) return DatProjectMode.Retail;
            if (File.Exists(Path.Combine(datPath, "portal.dat"))) return DatProjectMode.LegacyPreTod;
            return DatProjectMode.Retail;
        }

        private static void SyncRetailClientDatHeaderSizes(string datPath) {
            foreach (string datFile in new[] {
                "client_cell_1.dat",
                "client_portal.dat",
                "client_local_English.dat",
                "client_highres.dat",
            }) {
                string path = Path.Combine(datPath, datFile);
                if (!File.Exists(path)) {
                    continue;
                }

                LegacyDatClientExportFixer.SyncHeaderFileSize(path);
            }
        }
    }
}
