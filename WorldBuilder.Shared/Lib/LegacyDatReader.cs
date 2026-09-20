using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib {
    internal enum LegacyDatVersion {
        Unknown = 0,
        Tod = 1,
        DarkMajesty = 2,
    }

    internal readonly record struct LegacyDatFileEntry(uint Id, uint Offset, uint Size);

    internal sealed class LegacyDatDatabase : IDisposable {
        private const uint TodHeaderOffset = 0x140;
        private const uint AcdmHeaderOffset = 0x12C;
        private readonly FileStream _stream;
        private readonly object _streamLock = new();
        private readonly Dictionary<uint, LegacyDatFileEntry> _entries = new();

        public string FilePath { get; }
        public bool IsCellDatabase { get; }
        public LegacyDatVersion Version { get; }
        public int Iteration { get; }
        public uint BlockSize { get; }
        public uint RootOffset { get; }
        public IEnumerable<uint> FileIds => _entries.Keys;

        public LegacyDatDatabase(string filePath, bool isCellDatabase) {
            if (!File.Exists(filePath)) {
                throw new FileNotFoundException($"Legacy DAT file not found: {filePath}", filePath);
            }

            FilePath = filePath;
            IsCellDatabase = isCellDatabase;
            _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            using var reader = new BinaryReader(_stream, System.Text.Encoding.Default, leaveOpen: true);
            if (TryReadTodHeader(reader, out var todBlockSize, out var todRoot)) {
                Version = LegacyDatVersion.Tod;
                BlockSize = todBlockSize;
                RootOffset = todRoot;
                Iteration = 0;
            }
            else if (TryReadAcdmHeader(reader, out var dmBlockSize, out var dmRoot, out var dmIteration)) {
                Version = LegacyDatVersion.DarkMajesty;
                BlockSize = dmBlockSize;
                RootOffset = dmRoot;
                Iteration = dmIteration;
            }
            else {
                throw new InvalidDataException($"Unsupported DAT header in '{filePath}'.");
            }

            if (BlockSize == 0 || RootOffset == 0) {
                throw new InvalidDataException($"Invalid DAT metadata in '{filePath}'.");
            }

            var visited = new HashSet<uint>();
            ReadDirectory(RootOffset, visited);
        }

        public bool TryReadFileBytes(uint id, [MaybeNullWhen(false)] out byte[] bytes) {
            if (!_entries.TryGetValue(id, out var entry)) {
                bytes = null;
                return false;
            }

            lock (_streamLock) {
                bytes = ReadEntryBytes(entry.Offset, entry.Size);
                return true;
            }
        }

        private static bool TryReadTodHeader(BinaryReader reader, out uint blockSize, out uint rootOffset) {
            blockSize = 0;
            rootOffset = 0;
            reader.BaseStream.Seek(TodHeaderOffset, SeekOrigin.Begin);
            uint fileType = reader.ReadUInt32();
            if (fileType != 0x5442) {
                return false;
            }

            blockSize = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            rootOffset = reader.ReadUInt32();
            return true;
        }

        private static bool TryReadAcdmHeader(BinaryReader reader, out uint blockSize, out uint rootOffset, out int iteration) {
            blockSize = 0;
            rootOffset = 0;
            iteration = 0;
            reader.BaseStream.Seek(AcdmHeaderOffset, SeekOrigin.Begin);
            uint fileType = reader.ReadUInt32();
            if (fileType != 0x5442) {
                return false;
            }

            blockSize = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            iteration = reader.ReadInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            rootOffset = reader.ReadUInt32();
            return true;
        }

        private void ReadDirectory(uint sectorOffset, HashSet<uint> visited) {
            if (sectorOffset == 0 || sectorOffset == 0xCDCDCDCD || !visited.Add(sectorOffset)) {
                return;
            }

            byte[] headerBytes;
            lock (_streamLock) {
                headerBytes = ReadEntryBytes(sectorOffset, GetDirectoryObjectSize());
            }

            using var ms = new MemoryStream(headerBytes, writable: false);
            using var reader = new BinaryReader(ms);

            int branchSize = GetBranchSize();
            var branches = new uint[branchSize];
            for (int i = 0; i < branchSize; i++) {
                branches[i] = reader.ReadUInt32();
            }

            uint entryCount = reader.ReadUInt32();
            var entries = new List<LegacyDatFileEntry>((int)entryCount);
            for (int i = 0; i < entryCount; i++) {
                entries.Add(ReadFileEntry(reader));
            }

            foreach (var entry in entries) {
                _entries[entry.Id] = entry;
            }

            if (Version == LegacyDatVersion.DarkMajesty && Iteration <= 8) {
                for (int i = 0; i < branches.Length; i++) {
                    if (branches[i] != 0 && branches[i] != sectorOffset && branches[i] != 0xCDCDCDCD) {
                        ReadDirectory(branches[i], visited);
                    }
                }
                return;
            }

            if (branches[0] == 0) {
                return;
            }

            for (int i = 0; i < entryCount + 1 && i < branches.Length; i++) {
                if (branches[i] != 0) {
                    ReadDirectory(branches[i], visited);
                }
            }
        }

        private LegacyDatFileEntry ReadFileEntry(BinaryReader reader) {
            if (Version == LegacyDatVersion.Tod) {
                _ = reader.ReadUInt32();
                uint id = reader.ReadUInt32();
                uint offset = reader.ReadUInt32();
                uint size = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                return new LegacyDatFileEntry(id, offset, size);
            }

            uint dmId = reader.ReadUInt32();
            uint dmOffset = reader.ReadUInt32();
            uint dmSize = reader.ReadUInt32();
            return new LegacyDatFileEntry(dmId, dmOffset, dmSize);
        }

        private uint GetDirectoryObjectSize() {
            if (Version == LegacyDatVersion.DarkMajesty && Iteration <= 8) {
                return 0x400;
            }

            uint branchSize = (uint)GetBranchSize();
            const uint fileEntrySize = sizeof(uint) * 6;
            return (sizeof(uint) * branchSize) + sizeof(uint) + (fileEntrySize * (branchSize - 1));
        }

        private int GetBranchSize() {
            if (Version == LegacyDatVersion.DarkMajesty && Iteration <= 8) {
                return 0x25;
            }

            return 0x3E;
        }

        private byte[] ReadEntryBytes(uint offset, uint size) {
            byte[] buffer = new byte[size];
            _stream.Seek(offset, SeekOrigin.Begin);

            uint nextOffset = ReadNextSectorOffset(_stream);
            int written = 0;
            uint remaining = size;

            while (remaining > 0) {
                if (nextOffset == 0) {
                    int finalCount = (int)remaining;
                    _stream.ReadExactly(buffer.AsSpan(written, finalCount));
                    break;
                }

                int chunkSize = (int)Math.Min(BlockSize - 4, remaining);
                _stream.ReadExactly(buffer.AsSpan(written, chunkSize));
                written += chunkSize;
                remaining -= (uint)chunkSize;

                if (remaining == 0) {
                    break;
                }

                _stream.Seek(nextOffset, SeekOrigin.Begin);
                nextOffset = ReadNextSectorOffset(_stream);
            }

            return buffer;
        }

        private static uint ReadNextSectorOffset(Stream stream) {
            Span<byte> nextOffsetBytes = stackalloc byte[4];
            stream.ReadExactly(nextOffsetBytes);
            return BitConverter.ToUInt32(nextOffsetBytes);
        }

        public void Dispose() {
            _stream.Dispose();
        }
    }

    internal static class LegacyDatDecoders {
        private const uint LegacyRegionAlias = 0x13000000;
        private const uint LegacyRegionFileId = 0x130F0000;
        private const uint SyntheticRenderSurfacePrefix = 0xF6000000;
        private const int LegacyMapSize = 254;
        private const int LegacyLandblockLength = 192;
        private const int LegacyVerticesPerCell = 9;

        public static uint ResolveRegionFileId(uint requestedId) =>
            requestedId == LegacyRegionAlias ? LegacyRegionFileId : requestedId;

        public static uint CreateSyntheticRenderSurfaceId(uint surfaceTextureId) =>
            SyntheticRenderSurfacePrefix | (surfaceTextureId & 0x00FFFFFFu);

        public static bool TryResolveSyntheticRenderSurfaceId(uint syntheticRenderSurfaceId, out uint surfaceTextureId) {
            if ((syntheticRenderSurfaceId & 0xFF000000u) == SyntheticRenderSurfacePrefix) {
                surfaceTextureId = 0x05000000u | (syntheticRenderSurfaceId & 0x00FFFFFFu);
                return true;
            }

            surfaceTextureId = 0;
            return false;
        }

        public static Region CreateFallbackRegion(uint id, uint terrainTextureId = 0) {
            var landHeightTable = new float[256];
            for (int i = 0; i < landHeightTable.Length; i++) {
                landHeightTable[i] = i * 2f;
            }

            return new Region {
                Id = id,
                RegionNumber = 1,
                Version = 0,
                Name = "Legacy",
                LandDefs = new LandDefs {
                    NumBlockLength = LegacyMapSize,
                    NumBlockWidth = LegacyMapSize,
                    SquareLength = 24f,
                    LblockLength = LegacyLandblockLength,
                    VertexPerCell = LegacyVerticesPerCell,
                    MaxObjHeight = 512f,
                    SkyHeight = 2048f,
                    RoadWidth = 5f,
                    LandHeightTable = landHeightTable,
                },
                GameTime = new GameTime(),
                TerrainTypes = [
                    new TerrainType { SceneTypeIndices = [] }
                ],
                LandSurf = new LandSurf {
                    Type = 0,
                    TexMerge = new TexMerge {
                        BaseTexSize = 1,
                        CornerTerrainMaps = [],
                        SideTerrainMaps = [],
                        RoadMaps = [],
                        TerrainDesc = [
                            new TMTerrainDesc {
                                TerrainType = TerrainTextureType.Grassland,
                                TerrainTex = new TerrainTex {
                                    TextureId = terrainTextureId,
                                    TexTiling = 1,
                                    DetailTexTiling = 1,
                                }
                            }
                        ]
                    }
                },
            };
        }

        public static bool TryDecode<T>(byte[] bytes, [MaybeNullWhen(false)] out T record)
            where T : class, IDatRecord, new() {
            record = null;
            if (!DatRecordTable.TryGet(typeof(T), out var info)) {
                return false;
            }

            try {
                record = AethDatNative.DecodeStatic<T>((uint)info.Kind, bytes);
                return record != null;
            }
            catch {
                record = null;
                return false;
            }
        }

        public static bool TryDecodeRegion(byte[] bytes, LegacyDatVersion version, uint fallbackTerrainTextureId, [MaybeNullWhen(false)] out Region region) {
            if (TryDecode(bytes, out region) && region != null) {
                return true;
            }

            if (version == LegacyDatVersion.DarkMajesty) {
                region = CreateFallbackRegion(0x13000000, fallbackTerrainTextureId);
                return true;
            }

            region = null;
            return false;
        }

        public static bool TryDecodeGfxObj(byte[] bytes, LegacyDatVersion version, [MaybeNullWhen(false)] out GfxObj gfxObj) =>
            TryDecode(bytes, out gfxObj);

        public static bool TryDecodeEnvironment(byte[] bytes, LegacyDatVersion version, [MaybeNullWhen(false)] out Acme.Dat.Environment environment) =>
            TryDecode(bytes, out environment);

        public static bool TryDecodeSurface(byte[] bytes, LegacyDatVersion version, [MaybeNullWhen(false)] out Surface surface) =>
            TryDecode(bytes, out surface);

        public static bool TryDecodeLandBlock(byte[] bytes, LegacyDatVersion version, int iteration, [MaybeNullWhen(false)] out LandBlock landBlock) =>
            TryDecode(bytes, out landBlock);

        public static bool TryDecodeLandBlockInfo(byte[] bytes, LegacyDatVersion version, [MaybeNullWhen(false)] out LandBlockInfo landBlockInfo) =>
            TryDecode(bytes, out landBlockInfo);

        public static bool TryDecodeEnvCell(byte[] bytes, LegacyDatVersion version, int iteration, [MaybeNullWhen(false)] out EnvCell envCell) =>
            TryDecode(bytes, out envCell);

        public static bool TryDecodeSurfaceTexture(
            byte[] bytes,
            LegacyDatVersion version,
            uint syntheticRenderSurfaceId,
            [MaybeNullWhen(false)] out SurfaceTexture surfaceTexture,
            [MaybeNullWhen(false)] out RenderSurface renderSurface) {
            _ = version;
            surfaceTexture = null;
            renderSurface = null;
            if (bytes.Length < 20) {
                return false;
            }

            var reader = new DatBinReader(bytes);
            uint id = reader.ReadUInt32();
            uint format = reader.ReadUInt32();
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            if (width <= 0 || height <= 0) {
                return false;
            }

            byte[] source;
            uint paletteId = 0;
            if (format == 2) {
                var indices = new List<byte>();
                while (reader.Remaining > 4) {
                    indices.Add(reader.ReadByte());
                }
                source = new byte[indices.Count * 2];
                for (int i = 0; i < indices.Count; i++) {
                    source[i * 2] = indices[i];
                }
                paletteId = reader.Remaining >= 4 ? reader.ReadUInt32() : 0;
                renderSurface = new RenderSurface {
                    Id = syntheticRenderSurfaceId,
                    Width = width,
                    Height = height,
                    Format = (uint)PixelFormat.PFID_INDEX16,
                    SourceData = source,
                    DefaultPaletteId = paletteId,
                };
            }
            else {
                source = reader.ReadRemaining();
                if (source.Length >= 4) {
                    paletteId = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(source.Length - 4));
                    source = source[..^4];
                }
                renderSurface = new RenderSurface {
                    Id = syntheticRenderSurfaceId,
                    Width = width,
                    Height = height,
                    Format = format,
                    SourceData = source,
                    DefaultPaletteId = paletteId,
                };
            }

            surfaceTexture = new SurfaceTexture {
                Id = id,
                Textures = [syntheticRenderSurfaceId],
            };
            return true;
        }

        public static bool TryDecodeDirectRenderSurface(
            byte[] bytes,
            LegacyDatVersion version,
            [MaybeNullWhen(false)] out RenderSurface renderSurface) {
            _ = version;
            renderSurface = null;
            if (bytes.Length < 12) {
                return false;
            }

            var reader = new DatBinReader(bytes);
            uint id = reader.ReadUInt32();
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            byte[] rgb = reader.ReadRemaining();
            var bgra = new byte[Math.Max(0, width) * Math.Max(0, height) * 4];
            for (int i = 0, o = 0; i + 2 < rgb.Length && o + 3 < bgra.Length; i += 3, o += 4) {
                bgra[o] = rgb[i + 2];
                bgra[o + 1] = rgb[i + 1];
                bgra[o + 2] = rgb[i];
                bgra[o + 3] = 0xFF;
            }

            renderSurface = new RenderSurface {
                Id = id,
                Width = width,
                Height = height,
                Format = (uint)PixelFormat.PFID_A8R8G8B8,
                SourceData = bgra,
            };
            return true;
        }

        public static bool CollectGfxObjReferences(byte[] bytes, LegacyDatVersion version, Action<uint> trackSurface) {
            if (!TryDecodeGfxObj(bytes, version, out var gfx) || gfx == null) {
                return false;
            }

            foreach (uint surface in gfx.Surfaces) {
                if (surface != 0) {
                    trackSurface(surface);
                }
            }

            return true;
        }

        public static bool CollectSetupReferences(byte[] bytes, Action<uint> trackPart) {
            if (!TryDecode<Setup>(bytes, out var setup) || setup == null) {
                return false;
            }

            foreach (uint part in setup.Parts) {
                if (part != 0) {
                    trackPart(part);
                }
            }

            return true;
        }

        public static bool CollectLandBlockInfoReferences(byte[] bytes, LegacyDatVersion version, Action<uint> trackPortalObjectId) {
            if (!TryDecodeLandBlockInfo(bytes, version, out var info) || info == null) {
                return false;
            }

            foreach (var obj in info.Objects) {
                if (obj.Id != 0) {
                    trackPortalObjectId(obj.Id);
                }
            }

            foreach (var building in info.Buildings) {
                if (building.ModelId != 0) {
                    trackPortalObjectId(building.ModelId);
                }
            }

            return true;
        }

        public static bool CollectEnvCellReferences(
            byte[] bytes,
            LegacyDatVersion version,
            int iteration,
            Action<ushort> trackSurface,
            Action<ushort> trackEnvironmentId,
            Action<uint> trackPortalObjectId) {
            if (!TryDecodeEnvCell(bytes, version, iteration, out var cell) || cell == null) {
                return false;
            }

            foreach (uint surface in cell.Surfaces) {
                trackSurface((ushort)(surface & 0xFFFF));
            }

            if (cell.EnvironmentId != 0) {
                trackEnvironmentId((ushort)(cell.EnvironmentId & 0xFFFF));
            }

            foreach (var obj in cell.StaticObjects) {
                if (obj.Id != 0) {
                    trackPortalObjectId(obj.Id);
                }
            }

            return true;
        }
    }

    public sealed class LegacyDatReader : IDatReaderWriter {
        private readonly DefaultDatReaderWriter _inner;

        public DatProjectMode Mode => DatProjectMode.LegacyPreTod;
        public bool CanWrite => false;
        public string CacheNamespace { get; }
        public string DatDirectory => _inner.DatDirectory;
        public DatCatalogTree Tree => _inner.Tree;
        public DatIterationInfo Iteration => _inner.Iteration;

        public static bool IsSyntheticRenderSurfaceId(uint renderSurfaceId) =>
            LegacyDatDecoders.TryResolveSyntheticRenderSurfaceId(renderSurfaceId, out _);

        public LegacyDatReader(string datDirectory) {
            CacheNamespace = DatCacheNamespace.FromDatDirectory(datDirectory, Mode);
            _inner = new DefaultDatReaderWriter(datDirectory, DatAccessType.Read);
        }

        public bool ContainsFile(DatArchive archive, uint id) => _inner.ContainsFile(archive, id);
        public uint[] ListFileIds(DatArchive archive) => _inner.ListFileIds(archive);
        public bool TryGetFileBytes(DatArchive archive, uint id, out byte[]? bytes) =>
            _inner.TryGetFileBytes(archive, id, out bytes);
        public bool TryWriteFileBytes(DatArchive archive, uint id, byte[] bytes, int iteration = 0) => false;
        public int GetIteration(DatArchive archive) => _inner.GetIteration(archive);
        public void SetIteration(DatArchive archive, int iteration) { }
        public void ResetTree(DatArchive archive) { }
        public LandBlock[] ReadLandblocks() => _inner.ReadLandblocks();
        public void Flush() { }
        public bool TryGetLandblock(uint id, out LandBlock file) => _inner.TryGetLandblock(id, out file);
        public bool TrySaveLandblock(LandBlock file, int iteration = 0) => false;
        public bool TrySave<T>(T file, int? iteration = 0) where T : class, IDatRecord, new() => false;

        public bool TryGet<T>(uint id, [MaybeNullWhen(false)] out T file) where T : class, IDatRecord, new() {
            if (typeof(T) == typeof(Region)) {
                uint actualId = LegacyDatDecoders.ResolveRegionFileId(id);
                if (_inner.TryGet(actualId, out file) && file != null) {
                    if (file is Region region) {
                        region.Id = id;
                    }
                    return true;
                }

                file = (T)(object)LegacyDatDecoders.CreateFallbackRegion(id);
                return true;
            }

            if (typeof(T) == typeof(RenderSurface)
                && LegacyDatDecoders.TryResolveSyntheticRenderSurfaceId(id, out uint textureId)
                && _inner.TryGet(textureId, out SurfaceTexture? texture)
                && texture != null
                && texture.Textures.Count > 0
                && _inner.TryGet(texture.Textures[0], out file)) {
                return true;
            }

            return _inner.TryGet(id, out file);
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : class, IDatRecord, new() {
            if (typeof(T) == typeof(RenderSurface) && Mode == DatProjectMode.LegacyPreTod) {
                return _inner.GetAllIdsOfType<SurfaceTexture>()
                    .Select(LegacyDatDecoders.CreateSyntheticRenderSurfaceId)
                    .Concat(_inner.GetAllIdsOfType<RenderSurface>())
                    .Distinct()
                    .OrderBy(id => id);
            }

            if (typeof(T) == typeof(Region)) {
                var ids = _inner.GetAllIdsOfType<Region>().ToList();
                if (_inner.ContainsFile(DatArchive.Portal, 0x130F0000u) && !ids.Contains(0x13000000u)) {
                    return new[] { 0x13000000u };
                }
                return ids;
            }

            return _inner.GetAllIdsOfType<T>();
        }

        public IDatReaderWriter Dats => this;

        public IReadOnlyList<uint> GetLandBlockInfoIds() => DatIdQueries.GetLandBlockInfoIds(this);
        public IReadOnlyList<uint> GetEnvCellIds() => DatIdQueries.GetEnvCellIds(this);

        public bool TryReadPortalAsciiString(uint id, out string text) {
            text = string.Empty;
            if (!TryGetFileBytes(DatArchive.Portal, id, out var bytes) || bytes == null) {
                return false;
            }

            text = LegacyDatDmPortalStringReader.ExtractAscii(bytes);
            return text.Length > 0;
        }

        public IReadOnlyDictionary<uint, string> ReadPortalAsciiStrings(byte typeHighByte) {
            var results = new Dictionary<uint, string>();
            foreach (uint id in ListFileIds(DatArchive.Portal).Where(id => (id >> 24) == typeHighByte).OrderBy(id => id)) {
                if (TryReadPortalAsciiString(id, out string text)) {
                    results[id] = text;
                }
            }
            return results;
        }

        public HashSet<uint> GetAllCellFileIds() {
            var ids = new HashSet<uint>();
            foreach (uint id in GetAllIdsOfType<LandBlock>()) ids.Add(id);
            foreach (uint id in this.GetLandBlockInfoIds()) ids.Add(id);
            foreach (uint id in this.GetEnvCellIds()) ids.Add(id);
            return ids;
        }

        public bool TryCollectLandBlockInfoReferences(uint id, Action<uint> trackPortalObjectId) {
            if (!TryGetFileBytes(DatArchive.Cell, id, out var bytes) || bytes == null) {
                return false;
            }

            return LegacyDatDecoders.CollectLandBlockInfoReferences(bytes, LegacyDatVersion.DarkMajesty, trackPortalObjectId);
        }

        public bool TryCollectSetupReferences(uint id, Action<uint> trackPart) {
            if (!TryGetFileBytes(DatArchive.Portal, id, out var bytes) || bytes == null) {
                return false;
            }

            return LegacyDatDecoders.CollectSetupReferences(bytes, trackPart);
        }

        public bool TryReadPortalRawBytes(uint id, [MaybeNullWhen(false)] out byte[] bytes) =>
            TryGetFileBytes(DatArchive.Portal, id, out bytes);

        public bool TryCollectEnvCellReferences(
            uint id,
            Action<ushort> trackSurface,
            Action<ushort> trackEnvironmentId,
            Action<uint> trackPortalObjectId) {
            if (!TryGetFileBytes(DatArchive.Cell, id, out var bytes) || bytes == null) {
                return false;
            }

            return LegacyDatDecoders.CollectEnvCellReferences(
                bytes, LegacyDatVersion.DarkMajesty, 0, trackSurface, trackEnvironmentId, trackPortalObjectId);
        }

        public bool TryCollectGfxObjReferences(uint id, Action<uint> trackSurface) {
            if (!TryGetFileBytes(DatArchive.Portal, id, out var bytes) || bytes == null) {
                return false;
            }

            return LegacyDatDecoders.CollectGfxObjReferences(bytes, LegacyDatVersion.DarkMajesty, trackSurface);
        }

        public bool TryCollectSurfaceReferences(uint id, Action<uint> trackSurfaceTexture, Action<uint> trackPalette) {
            if (!TryGet<Surface>(id, out var surface) || surface == null) {
                return false;
            }

            if (surface.OrigTextureId != 0) {
                trackSurfaceTexture(surface.OrigTextureId);
            }

            if (surface.OrigPaletteId != 0) {
                trackPalette(surface.OrigPaletteId);
            }

            return true;
        }

        public void Dispose() => _inner.Dispose();
    }
}
