using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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

        public LegacyDatDatabase(string filePath, bool isCellDatabase, bool writable = false) {
            if (!File.Exists(filePath)) {
                throw new FileNotFoundException($"Legacy DAT file not found: {filePath}", filePath);
            }

            FilePath = filePath;
            IsCellDatabase = isCellDatabase;
            _stream = new FileStream(
                filePath,
                FileMode.Open,
                writable ? FileAccess.ReadWrite : FileAccess.Read,
                FileShare.ReadWrite);

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

        /// <summary>
        /// Overwrites an existing file when <paramref name="bytes"/> is the same length.
        /// Sector links stay untouched, so the B-tree does not need to be rebuilt.
        /// </summary>
        public bool TryOverwriteSameSize(uint id, ReadOnlySpan<byte> bytes) {
            if (!_stream.CanWrite || !_entries.TryGetValue(id, out var entry) || entry.Size != bytes.Length) {
                return false;
            }

            lock (_streamLock) {
                WriteEntryBytes(entry.Offset, bytes);
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

        private void WriteEntryBytes(uint offset, ReadOnlySpan<byte> data) {
            _stream.Seek(offset, SeekOrigin.Begin);
            uint nextOffset = ReadNextSectorOffset(_stream);
            int written = 0;
            uint remaining = (uint)data.Length;

            while (remaining > 0) {
                if (nextOffset == 0) {
                    _stream.Write(data.Slice(written, (int)remaining));
                    break;
                }

                int chunkSize = (int)Math.Min(BlockSize - 4, remaining);
                _stream.Write(data.Slice(written, chunkSize));
                written += chunkSize;
                remaining -= (uint)chunkSize;

                if (remaining == 0) {
                    break;
                }

                _stream.Seek(nextOffset, SeekOrigin.Begin);
                nextOffset = ReadNextSectorOffset(_stream);
            }

            _stream.Flush();
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

        public static bool TryDecodeGfxObj(byte[] bytes, LegacyDatVersion version, [MaybeNullWhen(false)] out GfxObj gfxObj) {
            if (version == LegacyDatVersion.DarkMajesty
                && TryDecodeLegacyGfxObj(bytes, out gfxObj)
                && gfxObj != null) {
                return true;
            }

            return TryDecode(bytes, out gfxObj);
        }

        /// <summary>
        /// Reads the fixed header of a pre-ToD Setup. The native codec can decode
        /// complete Setup records when their optional sections are present, but
        /// legacy reference walks also encounter compact records containing only
        /// the part list.
        /// </summary>
        public static bool TryDecodeLegacySetup(
            byte[] bytes,
            [MaybeNullWhen(false)] out Setup setup) {
            setup = null;
            try {
                var reader = new DatBinReader(bytes);
                uint id = reader.ReadUInt32();
                if ((id >> 24) != 0x02) {
                    return false;
                }

                uint flags = reader.ReadUInt32();
                uint partCount = ReadCount(reader, bytes, 0x10000);
                var parts = new List<uint>(checked((int)partCount));
                for (uint i = 0; i < partCount; i++) {
                    parts.Add(reader.ReadUInt32());
                }

                setup = new Setup {
                    Id = id,
                    Flags = flags,
                    NumParts = partCount,
                    Parts = parts,
                };
                return true;
            }
            catch {
                setup = null;
                return false;
            }
        }

        /// <summary>
        /// Reads the pre-ToD GfxObj layout. The retail native codec understands the
        /// MessagePack interchange shape, but not every Dark Majesty polygon/BSP
        /// payload, so legacy objects need to be unpacked before the renderer sees
        /// their vertex and polygon maps.
        /// </summary>
        private static bool TryDecodeLegacyGfxObj(
            byte[] bytes,
            [MaybeNullWhen(false)] out GfxObj gfxObj) {
            gfxObj = null;
            try {
                var reader = new DatBinReader(bytes);
                uint id = reader.ReadUInt32();
                if ((id >> 24) != 0x01) {
                    return false;
                }

                uint flags = reader.ReadUInt32();
                uint surfaceCount = ReadCount(reader, bytes, 0x10000);
                var surfaces = new List<uint>(checked((int)surfaceCount));
                for (uint i = 0; i < surfaceCount; i++) {
                    surfaces.Add(reader.ReadUInt32());
                }

                var vertexType = (VertexType)reader.ReadInt32();
                uint vertexCount = ReadCount(reader, bytes, 0x100000);
                var vertices = new Dictionary<ushort, SWVertex>(checked((int)vertexCount));
                var vertexOrder = new List<ushort>(checked((int)vertexCount));
                for (uint i = 0; i < vertexCount; i++) {
                    ushort vertexId = reader.ReadUInt16();
                    ushort uvCount = reader.ReadUInt16();
                    var vertex = new SWVertex {
                        Origin = reader.ReadVector3(),
                        Normal = reader.ReadVector3(),
                        UVs = new List<Vec2Duv>(uvCount),
                    };
                    for (int uv = 0; uv < uvCount; uv++) {
                        vertex.UVs.Add(new Vec2Duv {
                            U = reader.ReadSingle(),
                            V = reader.ReadSingle(),
                        });
                    }

                    vertices[vertexId] = vertex;
                    vertexOrder.Add(vertexId);
                }

                var physicsPolygons = new Dictionary<ushort, Polygon>();
                var physicsOrder = new List<uint>();
                PhysicsBSPNode? physicsRoot = null;
                if ((flags & (uint)GfxObjFlags.HasPhysics) != 0) {
                    ReadLegacyPolygonMap(reader, physicsPolygons, physicsOrder);
                    if (reader.Remaining < 4) {
                        return false;
                    }

                    physicsRoot = LegacyBspReader.ReadPhysicsNode(reader);
                }

                var sortCenter = reader.ReadVector3();
                var drawingPolygons = new Dictionary<ushort, Polygon>();
                var drawingOrder = new List<uint>();
                DrawingBSPNode? drawingRoot = null;
                if ((flags & (uint)GfxObjFlags.HasDrawing) != 0) {
                    ReadLegacyPolygonMap(reader, drawingPolygons, drawingOrder);
                    if (reader.Remaining < 4) {
                        return false;
                    }

                    drawingRoot = LegacyBspReader.ReadDrawingNode(reader);
                }

                uint? degradeDid = null;
                if ((flags & (uint)GfxObjFlags.HasDIDDegrade) != 0 && reader.Remaining >= 4) {
                    degradeDid = reader.ReadUInt32();
                }

                var gfx = new GfxObj {
                    Id = id,
                    Flags = flags,
                    Surfaces = surfaces,
                    VertexType = (int)vertexType,
                    VertexEntryOrder = vertexOrder,
                    Vertices = vertices.ToDictionary(
                        pair => pair.Key,
                        pair => DatFrames.From(pair.Value.Origin)),
                    RenderVertices = vertices.ToDictionary(
                        pair => pair.Key,
                        pair => new GfxVertex {
                            OriginArray = DatFrames.From(pair.Value.Origin),
                            NormalArray = DatFrames.From(pair.Value.Normal),
                            Uvs = pair.Value.UVs
                                .Select(uv => new[] { uv.U, uv.V })
                                .ToList(),
                        }),
                    VertexArray = new VertexArray {
                        VertexType = vertexType,
                        Vertices = vertices,
                    },
                    PhysicsPolygons = physicsPolygons,
                    Polygons = drawingPolygons,
                    DrawingPolygonEntryOrder = drawingOrder,
                    DrawingPolygons = drawingPolygons.ToDictionary(
                        pair => (uint)pair.Key,
                        pair => pair.Value),
                    SortCenter = sortCenter,
                    PhysicsBSP = physicsRoot == null ? null : new PhysicsBSPTree { Root = physicsRoot },
                    DrawingBSP = drawingRoot == null ? null : new DrawingBSPTree { Root = drawingRoot },
                    DegradeDid = degradeDid,
                    TrailingData = reader.Remaining > 0 ? reader.ReadRemaining() : [],
                };

                if (physicsRoot != null) {
                    gfx.Physics = new GfxObjPhysics {
                        RawPolygonEntryOrder = physicsOrder,
                        RawPolygonsEntries = physicsPolygons
                            .Select(pair => new MapEntry<uint, Polygon> {
                                Key = pair.Key,
                                Value = pair.Value,
                            })
                            .ToList(),
                    };
                }

                // The editor-facing BSP nodes are not MessagePack fields. Keep a
                // retail-safe byte representation alongside them so a decoded
                // legacy object can also pass through the normal pack/unpack path.
                LegacyBspRetailNormalizer.NormalizeForRetail(gfx);
                var (center, radius) = Bounds(gfx);
                if (gfx.DrawingBSP?.Root != null) {
                    gfx.DrawingBspBytes = EncodeDrawingBsp(gfx.DrawingBSP.Root, center, radius);
                }

                if (gfx.Physics is { } physics && gfx.PhysicsBSP?.Root != null) {
                    physics.PhysicsBspBytes = EncodePhysicsBsp(gfx.PhysicsBSP.Root, center, radius);
                }

                gfxObj = gfx;
                return true;
            }
            catch {
                gfxObj = null;
                return false;
            }
        }

        private static void ReadLegacyPolygonMap(
            DatBinReader reader,
            Dictionary<ushort, Polygon> polygons,
            List<uint> order) {
            uint polygonCount = ReadCount(reader, reader.Length, 0x100000);
            for (uint i = 0; i < polygonCount; i++) {
                ushort id = reader.ReadUInt16();
                int vertexCount = reader.ReadByte();
                if (vertexCount < 3 || vertexCount > ushort.MaxValue) {
                    throw new InvalidDataException("Legacy polygon has an invalid vertex count.");
                }

                var stippling = (StipplingType)reader.ReadByte();
                int sidesType = reader.ReadInt32();
                var polygon = new Polygon {
                    Stippling = stippling,
                    SidesType = sidesType,
                    PosSurface = reader.ReadInt16(),
                    NegSurface = reader.ReadInt16(),
                    VertexIds = new List<ushort>(vertexCount),
                };
                for (int vertex = 0; vertex < vertexCount; vertex++) {
                    polygon.VertexIds.Add((ushort)reader.ReadInt16());
                }

                if (!stippling.HasFlag(StipplingType.NoPos)) {
                    for (int vertex = 0; vertex < vertexCount; vertex++) {
                        polygon.PosUVIndices.Add(reader.ReadByte());
                    }
                }

                if (sidesType == (int)CullMode.Clockwise
                    && !stippling.HasFlag(StipplingType.NoNeg)) {
                    for (int vertex = 0; vertex < vertexCount; vertex++) {
                        polygon.NegUVIndices.Add(reader.ReadByte());
                    }
                }

                if (sidesType == (int)CullMode.None) {
                    polygon.NegSurface = polygon.PosSurface;
                    polygon.NegUVIndices = polygon.PosUVIndices.ToList();
                }

                reader.Align(4);
                polygons[id] = polygon;
                order.Add(id);
            }
        }

        private static uint ReadCount(DatBinReader reader, byte[] bytes, uint max) {
            uint count = reader.ReadUInt32();
            if (count > max || count > (uint)Math.Max(0, bytes.Length - reader.Offset)) {
                throw new InvalidDataException("Legacy DAT collection count is invalid.");
            }

            return count;
        }

        private static uint ReadCount(DatBinReader reader, int length, uint max) {
            uint count = reader.ReadUInt32();
            if (count > max || count > (uint)Math.Max(0, length - reader.Offset)) {
                throw new InvalidDataException("Legacy DAT collection count is invalid.");
            }

            return count;
        }

        private static (Vector3 Center, float Radius) Bounds(GfxObj gfx) {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            bool any = false;
            foreach (var vertex in gfx.VertexArray.Vertices.Values) {
                min = Vector3.Min(min, vertex.Origin);
                max = Vector3.Max(max, vertex.Origin);
                any = true;
            }

            if (!any) {
                return (Vector3.Zero, 0.01f);
            }

            var center = (min + max) * 0.5f;
            float radius = 0.01f;
            foreach (var vertex in gfx.VertexArray.Vertices.Values) {
                radius = MathF.Max(radius, Vector3.Distance(center, vertex.Origin));
            }

            return (center, radius);
        }

        private static byte[] EncodePhysicsBsp(PhysicsBSPNode root, Vector3 fallbackCenter, float fallbackRadius) {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WritePhysicsNode(writer, root, fallbackCenter, fallbackRadius);
            writer.Flush();
            return stream.ToArray();
        }

        private static void WritePhysicsNode(
            BinaryWriter writer,
            PhysicsBSPNode node,
            Vector3 fallbackCenter,
            float fallbackRadius) {
            writer.Write((uint)node.Type);
            if (node.Type == BSPNodeType.Leaf) {
                writer.Write(node.LeafIndex);
                writer.Write(node.Solid);
                WriteSphere(writer, node.BoundingSphere, fallbackCenter, fallbackRadius);
                writer.Write(node.Polygons.Count);
                foreach (ushort polygon in node.Polygons) {
                    writer.Write(polygon);
                }

                Align(writer);
                return;
            }

            WritePlane(writer, node.SplittingPlane);
            WritePhysicsChildren(writer, node, fallbackCenter, fallbackRadius);
            WriteSphere(writer, node.BoundingSphere, fallbackCenter, fallbackRadius);
        }

        private static void WritePhysicsChildren(
            BinaryWriter writer,
            PhysicsBSPNode node,
            Vector3 fallbackCenter,
            float fallbackRadius) {
            switch (node.Type) {
                case BSPNodeType.BPnn:
                case BSPNodeType.BPIn:
                    WritePhysicsNode(writer, node.PosNode ?? EmptyPhysicsLeaf(), fallbackCenter, fallbackRadius);
                    break;
                case BSPNodeType.BpIN:
                case BSPNodeType.BpnN:
                    WritePhysicsNode(writer, node.NegNode ?? EmptyPhysicsLeaf(), fallbackCenter, fallbackRadius);
                    break;
                default:
                    WritePhysicsNode(writer, node.PosNode ?? EmptyPhysicsLeaf(), fallbackCenter, fallbackRadius);
                    WritePhysicsNode(writer, node.NegNode ?? EmptyPhysicsLeaf(), fallbackCenter, fallbackRadius);
                    break;
            }
        }

        private static byte[] EncodeDrawingBsp(DrawingBSPNode root, Vector3 fallbackCenter, float fallbackRadius) {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteDrawingNode(writer, root, fallbackCenter, fallbackRadius);
            writer.Flush();
            return stream.ToArray();
        }

        private static void WriteDrawingNode(
            BinaryWriter writer,
            DrawingBSPNode node,
            Vector3 fallbackCenter,
            float fallbackRadius) {
            writer.Write((uint)node.Type);
            if (node.Type == BSPNodeType.Leaf) {
                writer.Write(node.LeafIndex);
                Align(writer);
                return;
            }

            WritePlane(writer, node.SplittingPlane);
            if (node.Type == BSPNodeType.Portal) {
                WriteDrawingNode(writer, node.PosNode ?? EmptyDrawingLeaf(), fallbackCenter, fallbackRadius);
                WriteDrawingNode(writer, node.NegNode ?? EmptyDrawingLeaf(), fallbackCenter, fallbackRadius);
                WriteSphere(writer, node.BoundingSphere, fallbackCenter, fallbackRadius);
                WriteDrawingLists(writer, node);
                Align(writer);
                return;
            }

            switch (node.Type) {
                case BSPNodeType.BPnn:
                case BSPNodeType.BPIn:
                    WriteDrawingNode(writer, node.PosNode ?? EmptyDrawingLeaf(), fallbackCenter, fallbackRadius);
                    break;
                case BSPNodeType.BpIN:
                case BSPNodeType.BpnN:
                    WriteDrawingNode(writer, node.NegNode ?? EmptyDrawingLeaf(), fallbackCenter, fallbackRadius);
                    break;
                default:
                    WriteDrawingNode(writer, node.PosNode ?? EmptyDrawingLeaf(), fallbackCenter, fallbackRadius);
                    WriteDrawingNode(writer, node.NegNode ?? EmptyDrawingLeaf(), fallbackCenter, fallbackRadius);
                    break;
            }

            WriteSphere(writer, node.BoundingSphere, fallbackCenter, fallbackRadius);
            writer.Write(node.Polygons.Count);
            foreach (ushort polygon in node.Polygons) {
                writer.Write(polygon);
            }

            Align(writer);
        }

        private static void WriteDrawingLists(BinaryWriter writer, DrawingBSPNode node) {
            writer.Write(node.Polygons.Count);
            writer.Write(node.Portals.Count);
            foreach (ushort polygon in node.Polygons) {
                writer.Write(polygon);
            }

            foreach (var portal in node.Portals) {
                writer.Write((short)portal.PolyId);
                writer.Write((short)portal.PortalIndex);
            }
        }

        private static void WritePlane(BinaryWriter writer, Plane plane) {
            writer.Write(plane.Normal.X);
            writer.Write(plane.Normal.Y);
            writer.Write(plane.Normal.Z);
            writer.Write(plane.D);
        }

        private static void WriteSphere(
            BinaryWriter writer,
            SphereBounds? sphere,
            Vector3 fallbackCenter,
            float fallbackRadius) {
            var center = sphere?.Origin ?? fallbackCenter;
            float radius = sphere?.Radius ?? fallbackRadius;
            writer.Write(center.X);
            writer.Write(center.Y);
            writer.Write(center.Z);
            writer.Write(MathF.Max(radius, 0.01f));
        }

        private static PhysicsBSPNode EmptyPhysicsLeaf() => new() {
            Type = BSPNodeType.Leaf,
            LeafIndex = 0,
            BoundingSphere = new SphereBounds(),
        };

        private static DrawingBSPNode EmptyDrawingLeaf() => new() {
            Type = BSPNodeType.Leaf,
            LeafIndex = 0,
        };

        private static void Align(BinaryWriter writer) {
            int padding = (4 - ((int)writer.BaseStream.Position & 3)) & 3;
            for (int i = 0; i < padding; i++) {
                writer.Write((byte)0);
            }
        }

        public static bool TryDecodeEnvironment(byte[] bytes, LegacyDatVersion version, [MaybeNullWhen(false)] out Acme.Dat.Environment environment) =>
            TryDecode(bytes, out environment);

        public static bool TryDecodeSurface(byte[] bytes, LegacyDatVersion version, [MaybeNullWhen(false)] out Surface surface) =>
            TryDecodeLegacySurface(bytes, out surface) || TryDecode(bytes, out surface);

        /// <summary>
        /// Legacy portal surfaces store their file id in the payload. The retail decoder treats that id as
        /// <see cref="Surface.Type"/>, so texture ids come out as 0 and the viewer then looks in highres.
        /// </summary>
        public static bool TryDecodeLegacySurface(byte[] bytes, [MaybeNullWhen(false)] out Surface surface) {
            surface = null;
            if (bytes.Length < 24) {
                return false;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if ((id >> 24) != 0x08) {
                return false;
            }

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            bool textured = (type & (uint)(SurfaceType.Base1Image | SurfaceType.Base1ClipMap)) != 0;
            if (textured && bytes.Length < 28) {
                return false;
            }

            surface = new Surface { Id = id, Type = (SurfaceType)type };
            int offset;
            if (textured) {
                surface.OrigTextureId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
                surface.OrigPaletteId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
                offset = 16;
            }
            else {
                surface.ColorValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
                offset = 12;
            }

            if (bytes.Length < offset + 12) {
                return false;
            }

            surface.Translucency = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset));
            surface.Luminosity = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 4));
            surface.Diffuse = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 8));
            return true;
        }

        public static bool TryDecodeLegacyPalette(byte[] bytes, [MaybeNullWhen(false)] out Palette palette) {
            palette = null;
            if (bytes.Length < 8) {
                return false;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if ((id >> 24) != 0x04) {
                return false;
            }

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            if (count == 0 || count > 2048 || bytes.Length < 8 + count * 4) {
                return false;
            }

            var colors = new List<uint>((int)count);
            for (int i = 0; i < count; i++) {
                colors.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8 + i * 4)));
            }

            palette = new Palette { Id = id, Colors = colors };
            return true;
        }

        public static bool TryDecodeLandBlock(byte[] bytes, LegacyDatVersion version, int iteration, [MaybeNullWhen(false)] out LandBlock landBlock) =>
            TryDecode(bytes, out landBlock);

        public static bool TryDecodeLandBlockInfo(byte[] bytes, LegacyDatVersion version, [MaybeNullWhen(false)] out LandBlockInfo landBlockInfo) =>
            TryDecode(bytes, out landBlockInfo);

        public static bool TryDecodeEnvCell(byte[] bytes, LegacyDatVersion version, int iteration, [MaybeNullWhen(false)] out EnvCell envCell) =>
            TryDecode(bytes, out envCell) || TryDecodeLegacyEnvCell(bytes, out envCell);

        /// <summary>
        /// 2005 client <c>CEnvCell::UnPack</c>: flags, cell id, then counts. Retail records lead with the id twice.
        /// </summary>
        public static bool TryDecodeLegacyEnvCell(byte[] bytes, [MaybeNullWhen(false)] out EnvCell envCell) {
            envCell = null;
            if (bytes.Length < 16) {
                return false;
            }

            int offset = 0;
            if (!TryU32(bytes, ref offset, out uint flags) || !TryU32(bytes, ref offset, out uint id)) {
                return false;
            }

            if ((id & 0xFFFF) is 0xFFFF or 0xFFFE) {
                return false;
            }

            if (!TryU8(bytes, ref offset, out byte surfaceCount)
                || !TryU8(bytes, ref offset, out byte portalCount)
                || !TryU16(bytes, ref offset, out ushort visibleCount)) {
                return false;
            }

            var surfaces = new List<uint>(surfaceCount);
            for (int i = 0; i < surfaceCount; i++) {
                if (!TryU16(bytes, ref offset, out ushort surfaceId)) {
                    return false;
                }

                surfaces.Add(surfaceId);
            }

            if (!Align4(bytes, ref offset)
                || !TryU16(bytes, ref offset, out ushort environmentId)
                || !TryU16(bytes, ref offset, out ushort cellStructure)
                || !TryFrame(bytes, ref offset, out float[] origin, out float[] quat)) {
                return false;
            }

            var portals = new List<CellPortal>(portalCount);
            for (int i = 0; i < portalCount; i++) {
                if (!TryU16(bytes, ref offset, out ushort portalFlags)
                    || !TryU16(bytes, ref offset, out ushort polygonId)
                    || !TryU16(bytes, ref offset, out ushort otherCellId)
                    || !TryU16(bytes, ref offset, out ushort otherPortalId)) {
                    return false;
                }

                portals.Add(new CellPortal {
                    Flags = portalFlags,
                    PolygonId = polygonId,
                    OtherCellId = otherCellId,
                    OtherPortalId = otherPortalId,
                });
            }

            if (!Align4(bytes, ref offset)) {
                return false;
            }

            var visible = new List<ushort>(visibleCount);
            for (int i = 0; i < visibleCount; i++) {
                if (!TryU16(bytes, ref offset, out ushort visibleId)) {
                    return false;
                }

                visible.Add(visibleId);
            }

            if (!Align4(bytes, ref offset)) {
                return false;
            }

            var statics = new List<Stab>();
            if ((flags & 2) != 0) {
                if (!TryU32(bytes, ref offset, out uint staticCount)) {
                    return false;
                }

                for (uint i = 0; i < staticCount; i++) {
                    if (!TryU32(bytes, ref offset, out uint staticId)
                        || !TryFrame(bytes, ref offset, out float[] staticOrigin, out float[] staticQuat)) {
                        return false;
                    }

                    statics.Add(new Stab {
                        Id = staticId,
                        OriginArray = staticOrigin,
                        AnglesArray = staticQuat,
                    });
                }
            }

            if (!Align4(bytes, ref offset)) {
                return false;
            }

            uint? restriction = null;
            if ((flags & 8) != 0) {
                if (!TryU32(bytes, ref offset, out uint restrictionId)) {
                    return false;
                }

                restriction = restrictionId;
            }

            envCell = new EnvCell {
                Id = id,
                Flags = flags,
                Surfaces = surfaces,
                EnvironmentId = environmentId,
                CellStructure = cellStructure,
                PositionOriginArray = origin,
                PositionQuatArray = quat,
                CellPortals = portals,
                VisibleCells = visible,
                StaticObjects = statics,
                RestrictionObjNullable = restriction,
                IdEcho = id,
                TrailingData = offset < bytes.Length ? bytes[offset..] : [],
            };
            return true;
        }

        /// <summary>
        /// Inverse of <see cref="TryDecodeLegacyEnvCell"/>. Pad bytes are zero.
        /// </summary>
        public static byte[] EncodeLegacyEnvCell(EnvCell cell) {
            ArgumentNullException.ThrowIfNull(cell);
            if (cell.Surfaces.Count > byte.MaxValue || cell.CellPortals.Count > byte.MaxValue) {
                throw new InvalidDataException($"EnvCell 0x{cell.Id:X8} has more surfaces or portals than a legacy record can store.");
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(cell.Flags);
            writer.Write(cell.Id);
            writer.Write((byte)cell.Surfaces.Count);
            writer.Write((byte)cell.CellPortals.Count);
            writer.Write((ushort)cell.VisibleCells.Count);
            foreach (uint surface in cell.Surfaces) {
                writer.Write((ushort)surface);
            }

            Pad4(stream);
            writer.Write((ushort)cell.EnvironmentId);
            writer.Write(cell.CellStructure);
            WriteLegacyFrame(writer, cell.PositionOriginArray, cell.PositionQuatArray);
            foreach (CellPortal portal in cell.CellPortals) {
                writer.Write(portal.Flags);
                writer.Write(portal.PolygonId);
                writer.Write(portal.OtherCellId);
                writer.Write(portal.OtherPortalId);
            }

            Pad4(stream);
            foreach (ushort visible in cell.VisibleCells) {
                writer.Write(visible);
            }

            Pad4(stream);
            if ((cell.Flags & 2) != 0) {
                writer.Write((uint)cell.StaticObjects.Count);
                foreach (Stab stab in cell.StaticObjects) {
                    writer.Write(stab.Id);
                    WriteLegacyFrame(writer, stab.OriginArray, stab.AnglesArray);
                }
            }

            Pad4(stream);
            if ((cell.Flags & 8) != 0) {
                writer.Write(cell.RestrictionObjNullable ?? 0);
            }

            if (cell.TrailingData is { Length: > 0 }) {
                writer.Write(cell.TrailingData);
            }

            writer.Flush();
            return stream.ToArray();
        }

        static void Pad4(MemoryStream stream) {
            int pad = (4 - ((int)stream.Position & 3)) & 3;
            for (int i = 0; i < pad; i++) {
                stream.WriteByte(0);
            }
        }

        static void WriteLegacyFrame(BinaryWriter writer, float[]? origin, float[]? quat) {
            float x = origin is { Length: > 0 } ? origin[0] : 0f;
            float y = origin is { Length: > 1 } ? origin[1] : 0f;
            float z = origin is { Length: > 2 } ? origin[2] : 0f;
            float qx = quat is { Length: > 0 } ? quat[0] : 0f;
            float qy = quat is { Length: > 1 } ? quat[1] : 0f;
            float qz = quat is { Length: > 2 } ? quat[2] : 0f;
            float qw = quat is { Length: > 3 } ? quat[3] : 1f;
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
            writer.Write(qw);
            writer.Write(qx);
            writer.Write(qy);
            writer.Write(qz);
        }

        static bool Align4(byte[] bytes, ref int offset) {
            int pad = (4 - (offset & 3)) & 3;
            if (offset + pad > bytes.Length) {
                return false;
            }

            offset += pad;
            return true;
        }

        static bool TryFrame(byte[] bytes, ref int offset, out float[] origin, out float[] quat) {
            origin = [];
            quat = [];
            if (!TryF32(bytes, ref offset, out float x)
                || !TryF32(bytes, ref offset, out float y)
                || !TryF32(bytes, ref offset, out float z)
                || !TryF32(bytes, ref offset, out float w)
                || !TryF32(bytes, ref offset, out float qx)
                || !TryF32(bytes, ref offset, out float qy)
                || !TryF32(bytes, ref offset, out float qz)) {
                return false;
            }

            origin = [x, y, z];
            quat = [qx, qy, qz, w];
            return true;
        }

        static bool TryU8(byte[] bytes, ref int offset, out byte value) {
            if (offset >= bytes.Length) {
                value = 0;
                return false;
            }

            value = bytes[offset++];
            return true;
        }

        static bool TryU16(byte[] bytes, ref int offset, out ushort value) {
            if (offset + 2 > bytes.Length) {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
            offset += 2;
            return true;
        }

        static bool TryU32(byte[] bytes, ref int offset, out uint value) {
            if (offset + 4 > bytes.Length) {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            offset += 4;
            return true;
        }

        static bool TryF32(byte[] bytes, ref int offset, out float value) {
            if (!TryU32(bytes, ref offset, out uint bits)) {
                value = 0;
                return false;
            }

            value = BitConverter.Int32BitsToSingle((int)bits);
            return true;
        }

        public static bool TryDecodeSurfaceTexture(
            byte[] bytes,
            LegacyDatVersion version,
            uint syntheticRenderSurfaceId,
            [MaybeNullWhen(false)] out SurfaceTexture surfaceTexture,
            [MaybeNullWhen(false)] out RenderSurface renderSurface) {
            _ = version;
            surfaceTexture = null;
            renderSurface = null;
            if (bytes.Length < 16) {
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

            byte[] payload = reader.ReadRemaining();
            int pixelCount = width * height;
            if (format == 2) {
                if (payload.Length < 4) {
                    return false;
                }

                int indexCount = payload.Length - 4;
                var source = new byte[indexCount * 2];
                for (int i = 0; i < indexCount; i++) {
                    source[i * 2] = payload[i];
                }

                renderSurface = new RenderSurface {
                    Id = syntheticRenderSurfaceId,
                    Width = width,
                    Height = height,
                    Format = (uint)PixelFormat.PFID_INDEX16,
                    SourceData = source,
                    DefaultPaletteId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(indexCount)),
                };
            }
            else if (!TryExpandLegacyImage(format, payload, pixelCount, out PixelFormat pixelFormat, out byte[]? expanded)
                || expanded == null) {
                return false;
            }
            else {
                renderSurface = new RenderSurface {
                    Id = syntheticRenderSurfaceId,
                    Width = width,
                    Height = height,
                    Format = (uint)pixelFormat,
                    SourceData = expanded,
                };
            }

            surfaceTexture = new SurfaceTexture {
                Id = id,
                Textures = [syntheticRenderSurfaceId],
            };
            return true;
        }

        /// <summary>
        /// DM ImgTex codes, matching the client unpacker. Format 10 is three full planes
        /// (red, then green, then blue), not interleaved RGB.
        /// </summary>
        static bool TryExpandLegacyImage(uint format, byte[] payload, int pixelCount, out PixelFormat pixelFormat, [MaybeNullWhen(false)] out byte[] expanded) {
            pixelFormat = PixelFormat.PFID_UNKNOWN;
            expanded = null;
            switch (format) {
                case 4: {
                    if (payload.Length < pixelCount * 2) {
                        return false;
                    }

                    expanded = new byte[pixelCount * 4];
                    for (int i = 0; i < pixelCount; i++) {
                        ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(i * 2));
                        expanded[i * 4 + 0] = (byte)((packed & 0x0F) * 17);
                        expanded[i * 4 + 1] = (byte)(((packed >> 4) & 0x0F) * 17);
                        expanded[i * 4 + 2] = (byte)(((packed >> 8) & 0x0F) * 17);
                        expanded[i * 4 + 3] = (byte)(((packed >> 12) & 0x0F) * 17);
                    }

                    pixelFormat = PixelFormat.PFID_A8R8G8B8;
                    return true;
                }
                case 5 or 0x15: {
                    if (payload.Length < pixelCount * 4) {
                        return false;
                    }

                    expanded = payload;
                    pixelFormat = PixelFormat.PFID_A8R8G8B8;
                    return true;
                }
                case 7: {
                    if (payload.Length < pixelCount * 2) {
                        return false;
                    }

                    expanded = new byte[pixelCount * 4];
                    for (int i = 0; i < pixelCount; i++) {
                        ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(i * 2));
                        expanded[i * 4 + 0] = (byte)((packed & 0x1F) * 255 / 31);
                        expanded[i * 4 + 1] = (byte)(((packed >> 5) & 0x3F) * 255 / 63);
                        expanded[i * 4 + 2] = (byte)(((packed >> 11) & 0x1F) * 255 / 31);
                        expanded[i * 4 + 3] = 255;
                    }

                    pixelFormat = PixelFormat.PFID_A8R8G8B8;
                    return true;
                }
                case 8 or 0x14: {
                    if (payload.Length < pixelCount * 3) {
                        return false;
                    }

                    expanded = payload;
                    pixelFormat = PixelFormat.PFID_R8G8B8;
                    return true;
                }
                case 10: {
                    if (payload.Length < pixelCount * 3) {
                        return false;
                    }

                    expanded = new byte[pixelCount * 3];
                    for (int i = 0; i < pixelCount; i++) {
                        expanded[i * 3 + 0] = payload[i];
                        expanded[i * 3 + 1] = payload[pixelCount + i];
                        expanded[i * 3 + 2] = payload[pixelCount * 2 + i];
                    }

                    pixelFormat = PixelFormat.PFID_R8G8B8;
                    return true;
                }
                case 11 or 0x1C: {
                    if (payload.Length < pixelCount) {
                        return false;
                    }

                    expanded = payload;
                    pixelFormat = PixelFormat.PFID_A8;
                    return true;
                }
                default:
                    return false;
            }
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
            if (!TryDecodeLegacySetup(bytes, out var setup)
                && !TryDecode(bytes, out setup)) {
                return false;
            }

            if (setup == null) {
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

            if (typeof(T) == typeof(GfxObj)
                && TryGetFileBytes(DatArchive.Portal, id, out byte[]? gfxBytes)
                && gfxBytes != null
                && LegacyDatDecoders.TryDecodeGfxObj(
                    gfxBytes,
                    LegacyDatVersion.DarkMajesty,
                    out GfxObj? legacyGfx)
                && legacyGfx != null) {
                file = (T)(object)legacyGfx;
                return true;
            }

            if (typeof(T) == typeof(Setup)
                && _inner.TryGet(id, out file)
                && file != null) {
                return true;
            }

            if (typeof(T) == typeof(Setup)
                && TryGetFileBytes(DatArchive.Portal, id, out byte[]? setupBytes)
                && setupBytes != null
                && LegacyDatDecoders.TryDecodeLegacySetup(setupBytes, out Setup? legacySetup)
                && legacySetup != null) {
                file = (T)(object)legacySetup;
                return true;
            }

            if (typeof(T) == typeof(Surface)
                && TryGetFileBytes(DatArchive.Portal, id, out byte[]? surfaceBytes)
                && surfaceBytes != null
                && LegacyDatDecoders.TryDecodeLegacySurface(surfaceBytes, out Surface? legacySurface)
                && legacySurface != null) {
                file = (T)(object)legacySurface;
                return true;
            }

            if (typeof(T) == typeof(Palette)
                && TryGetFileBytes(DatArchive.Portal, id, out byte[]? paletteBytes)
                && paletteBytes != null
                && LegacyDatDecoders.TryDecodeLegacyPalette(paletteBytes, out Palette? legacyPalette)
                && legacyPalette != null) {
                file = (T)(object)legacyPalette;
                return true;
            }

            if (typeof(T) == typeof(SurfaceTexture)
                && TryReadLegacySurfaceTexture(id, out SurfaceTexture? legacyTexture, out _)
                && legacyTexture != null) {
                file = (T)(object)legacyTexture;
                return true;
            }

            if (typeof(T) == typeof(RenderSurface)
                && TryGetFileBytes(DatArchive.Portal, id, out byte[]? directRenderBytes)
                && directRenderBytes != null
                && LegacyDatDecoders.TryDecodeDirectRenderSurface(
                    directRenderBytes,
                    LegacyDatVersion.DarkMajesty,
                    out RenderSurface? directRender)
                && directRender != null) {
                file = (T)(object)directRender;
                return true;
            }

            if (typeof(T) == typeof(RenderSurface)
                && LegacyDatDecoders.TryResolveSyntheticRenderSurfaceId(id, out uint textureId)
                && TryReadLegacySurfaceTexture(textureId, out _, out RenderSurface? legacyRender)
                && legacyRender != null) {
                file = (T)(object)legacyRender;
                return true;
            }

            if (typeof(T) == typeof(CharGen)
                && TryGetFileBytes(DatArchive.Portal, id, out byte[]? charGenBytes)
                && charGenBytes != null
                && LegacyDatCharGenMapper.TryDecodeLegacyCharGen(charGenBytes, out CharGen? legacyCharGen)
                && legacyCharGen != null) {
                file = (T)(object)legacyCharGen;
                return true;
            }

            if (typeof(T) == typeof(EnvCell)
                && !_inner.TryGet(id, out file)
                && TryGetFileBytes(DatArchive.Cell, id, out byte[]? cellBytes)
                && cellBytes != null
                && LegacyDatDecoders.TryDecodeLegacyEnvCell(cellBytes, out EnvCell? legacyCell)
                && legacyCell != null) {
                file = (T)(object)legacyCell;
                return true;
            }

            return _inner.TryGet(id, out file);
        }

        bool TryReadLegacySurfaceTexture(uint textureId, out SurfaceTexture? surfaceTexture, out RenderSurface? renderSurface) {
            surfaceTexture = null;
            renderSurface = null;
            if (!TryGetFileBytes(DatArchive.Portal, textureId, out byte[]? bytes) || bytes == null) {
                return false;
            }

            return LegacyDatDecoders.TryDecodeSurfaceTexture(
                bytes,
                LegacyDatVersion.DarkMajesty,
                LegacyDatDecoders.CreateSyntheticRenderSurfaceId(textureId),
                out surfaceTexture,
                out renderSurface);
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
