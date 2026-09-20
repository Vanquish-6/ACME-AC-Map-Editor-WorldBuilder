using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// World-completion pass: fills <c>client_cell_1.dat</c> with LandBlock / LandBlockInfo / EnvCell
/// records that exist in an optional extra world-data folder (manifest + payload chunks) but are
/// missing from the converted client output. Spawn and ownership tails are stripped; those belong
/// to the world database, never to client DATs.
/// </summary>
public static class LegacyDatServerWorldCompletion {
    public sealed class Result {
        public int LandBlocksAdded { get; internal set; }
        public int LandBlockInfosAdded { get; internal set; }
        public int EnvCellsAdded { get; internal set; }
        public int PortalTablesAdded { get; internal set; }
        public int DecodeFailures { get; internal set; }
        public int WriteFailures { get; internal set; }
        public int MissingPortalEnvironments { get; internal set; }
        public int MissingPortalSurfaces { get; internal set; }
        public int MissingPortalModels { get; internal set; }
        public List<string> FailureSamples { get; } = [];

        public int TotalAdded => LandBlocksAdded + LandBlockInfosAdded + EnvCellsAdded + PortalTablesAdded;
    }

    /// <summary>
    /// Portal record types safe to import from extra world data. GfxObj / Environment records are
    /// excluded — they are collision/physics variants without drawing data and must come from the
    /// client portal catalog instead.
    /// </summary>
    private static readonly Dictionary<string, Func<byte[], uint, IDatRecord?>> PortalImportTypes =
        new(StringComparer.Ordinal) {
            ["PalSetOrPaletteSet"] = (payload, id) => ParsePortalObject<PalSet>(payload, id),
            ["ClothingOrClothingTable"] = (payload, id) => ParsePortalObject<ClothingTable>(payload, id),
        };

    private sealed record PendingRecord(uint Id, string TypeGuess, long Offset, int Size);

    /// <summary>
    /// Validates that <paramref name="directory"/> points at a usable extra world-data folder
    /// (manifest + chunks) and normalizes it to the export root.
    /// </summary>
    public static bool TryResolveSource(string? directory, out string exportRoot) {
        exportRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(directory)) {
            return false;
        }

        string root = ResolveExportRoot(directory);
        if (File.Exists(Path.Combine(root, "scell_1", "manifest.ndjson"))
            && Directory.Exists(Path.Combine(root, "scell_1", "chunks"))) {
            exportRoot = root;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Runs the completion pass. The caller is responsible for running
    /// <see cref="LegacyDatClientExportFixer.CompleteClientExport"/> afterwards (header sync + journals),
    /// as with every other bulk write pass.
    /// </summary>
    public static Result Complete(
        string serverDatExportDirectory,
        string exportDirectory,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverDatExportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);

        if (!TryResolveSource(serverDatExportDirectory, out string exportRoot)) {
            throw new DirectoryNotFoundException(
                $"World data folder could not be used: {serverDatExportDirectory}");
        }

        string scellDirectory = Path.Combine(exportRoot, "scell_1");
        string sportalDirectory = Path.Combine(exportRoot, "sportal_1");

        bool hasPortalSource = File.Exists(Path.Combine(sportalDirectory, "manifest.ndjson"))
            && Directory.Exists(Path.Combine(sportalDirectory, "chunks"));

        var result = new Result();

        onProgress?.Invoke("World completion: reading converted catalogs...");
        (var existingCellIds, var existingPortalIds) = ReadExistingIds(exportDirectory);
        onProgress?.Invoke(
            $"World completion: export has {existingCellIds.Count:N0} cell / {existingPortalIds.Count:N0} portal entries.");

        onProgress?.Invoke("World completion: scanning manifests for missing records...");
        var pendingCellByChunk = CollectMissingRecords(
            Path.Combine(scellDirectory, "manifest.ndjson"),
            existingCellIds,
            static type => type is "EnvCell" or "LandBlockInfo" or "LandBlock");
        var pendingPortalByChunk = hasPortalSource
            ? CollectMissingRecords(
                Path.Combine(sportalDirectory, "manifest.ndjson"),
                existingPortalIds,
                PortalImportTypes.ContainsKey)
            : [];

        int pendingCellCount = pendingCellByChunk.Sum(kvp => kvp.Value.Count);
        int pendingPortalCount = pendingPortalByChunk.Sum(kvp => kvp.Value.Count);
        if (pendingCellCount == 0 && pendingPortalCount == 0) {
            onProgress?.Invoke("World completion: nothing missing; export already covers the world.");
            return result;
        }

        onProgress?.Invoke(
            $"World completion: {pendingCellCount:N0} cell + {pendingPortalCount:N0} portal record(s) to import.");

        // §8.2: clear the stale journal and zero the free list before Chorizite writes, or the
        // writer recycles retail free-block chains and corrupts payload offsets.
        var filesToPatch = new List<string>();
        if (pendingCellCount > 0) {
            filesToPatch.Add("client_cell_1.dat");
        }

        if (pendingPortalCount > 0) {
            filesToPatch.Add("client_portal.dat");
        }

        LegacyDatClientExportFixer.PatchCatalogFilesForWriting(exportDirectory, filesToPatch.ToArray());

        var referencedEnvironments = new HashSet<uint>();
        var referencedSurfaces = new HashSet<ushort>();
        var referencedModels = new HashSet<uint>();

        using (var writer = new DefaultDatReaderWriter(
            exportDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand)) {
            int cellIteration = writer.GetIteration(DatArchive.Cell);
            int processed = 0;

            foreach (var (chunkName, records) in pendingCellByChunk.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)) {
                string chunkPath = Path.Combine(scellDirectory, "chunks", chunkName);
                using var chunk = File.OpenRead(chunkPath);
                foreach (var record in records.OrderBy(r => r.Offset)) {
                    byte[] payload = new byte[record.Size];
                    chunk.Seek(record.Offset, SeekOrigin.Begin);
                    chunk.ReadExactly(payload, 0, payload.Length);

                    bool ok = record.TypeGuess switch {
                        "EnvCell" => TryImportEnvCell(
                            writer, cellIteration, record.Id, payload, result,
                            referencedEnvironments, referencedSurfaces, referencedModels),
                        "LandBlockInfo" => TryImportLandBlockInfo(
                            writer, cellIteration, record.Id, payload, result, referencedModels),
                        "LandBlock" => TryImportLandBlock(writer, cellIteration, record.Id, payload, result),
                        _ => true,
                    };

                    if (!ok && result.FailureSamples.Count < 12) {
                        result.FailureSamples.Add($"0x{record.Id:X8} ({record.TypeGuess})");
                    }

                    processed++;
                    if (processed % 20000 == 0) {
                        onProgress?.Invoke(
                            $"World completion: {processed:N0}/{pendingCellCount:N0} cell records imported "
                            + $"(EnvCell {result.EnvCellsAdded:N0}, LBI {result.LandBlockInfosAdded:N0}).");
                    }
                }
            }

            if (pendingPortalCount > 0) {
                int portalIteration = writer.GetIteration(DatArchive.Portal);
                foreach (var (chunkName, records) in pendingPortalByChunk.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)) {
                    string chunkPath = Path.Combine(sportalDirectory, "chunks", chunkName);
                    using var chunk = File.OpenRead(chunkPath);
                    foreach (var record in records.OrderBy(r => r.Offset)) {
                        byte[] payload = new byte[record.Size];
                        chunk.Seek(record.Offset, SeekOrigin.Begin);
                        chunk.ReadExactly(payload, 0, payload.Length);

                        bool ok = TryImportPortalTable(writer, portalIteration, record, payload, result);
                        if (!ok && result.FailureSamples.Count < 12) {
                            result.FailureSamples.Add($"0x{record.Id:X8} ({record.TypeGuess})");
                        }
                    }
                }
            }

            // Closure audit (informational): every imported cell ref should resolve in the
            // converted portal catalog. Misses indicate portal-side gaps, not bad cell data.
            var portalTree = writer.ListFileIds(DatArchive.Portal);
            result.MissingPortalEnvironments = referencedEnvironments
                .Count(envId => envId != 0 && !portalTree.HasFile(LegacyDatPortalFileIds.Environment(envId)));
            result.MissingPortalSurfaces = referencedSurfaces
                .Count(surfaceId => !portalTree.HasFile(LegacyDatPortalFileIds.Surface(surfaceId)));
            result.MissingPortalModels = referencedModels
                .Count(modelId => !portalTree.HasFile(modelId));
        }

        onProgress?.Invoke(
            $"World completion: added {result.EnvCellsAdded:N0} EnvCell(s), "
            + $"{result.LandBlockInfosAdded:N0} LandBlockInfo(s), {result.LandBlocksAdded:N0} LandBlock(s), "
            + $"{result.PortalTablesAdded:N0} portal table(s); "
            + $"{result.DecodeFailures} decode failure(s), {result.WriteFailures} write failure(s).");
        if (result.MissingPortalEnvironments > 0 || result.MissingPortalSurfaces > 0 || result.MissingPortalModels > 0) {
            onProgress?.Invoke(
                $"World completion closure: missing portal refs — environments {result.MissingPortalEnvironments}, "
                + $"surfaces {result.MissingPortalSurfaces}, models {result.MissingPortalModels}.");
        }

        return result;
    }

    private static string ResolveExportRoot(string serverDatExportDirectory) {
        string full = Path.GetFullPath(serverDatExportDirectory);
        // Accept a nested manifest folder itself for convenience.
        if (File.Exists(Path.Combine(full, "manifest.ndjson"))) {
            return Path.GetDirectoryName(full) ?? full;
        }

        return full;
    }

    private static (HashSet<uint> CellIds, HashSet<uint> PortalIds) ReadExistingIds(string exportDirectory) {
        using var reader = new DefaultDatReaderWriter(
            exportDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);
        var cellIds = new HashSet<uint>();
        foreach (var file in reader.Cell().Catalog().Tree) {
            cellIds.Add(file.Id);
        }

        var portalIds = new HashSet<uint>();
        foreach (var file in reader.ListFileIds(DatArchive.Portal)) {
            portalIds.Add(file);
        }

        return (cellIds, portalIds);
    }

    private static Dictionary<string, List<PendingRecord>> CollectMissingRecords(
        string manifestPath,
        HashSet<uint> existingIds,
        Func<string, bool> includeType) {
        var pendingByChunk = new Dictionary<string, List<PendingRecord>>(StringComparer.Ordinal);
        using var manifest = new StreamReader(manifestPath);
        while (manifest.ReadLine() is { } line) {
            if (line.Length == 0) {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("Status").GetString() != "ok") {
                continue;
            }

            string typeGuess = root.GetProperty("TypeGuess").GetString() ?? string.Empty;
            if (!includeType(typeGuess)) {
                continue;
            }

            uint id = root.GetProperty("Id").GetUInt32();
            if (existingIds.Contains(id)) {
                continue;
            }

            string chunkName = root.GetProperty("PayloadChunk").GetString() ?? string.Empty;
            var record = new PendingRecord(
                id,
                typeGuess,
                root.GetProperty("PayloadOffset").GetInt64(),
                root.GetProperty("PayloadSize").GetInt32());
            if (!pendingByChunk.TryGetValue(chunkName, out var list)) {
                list = [];
                pendingByChunk[chunkName] = list;
            }

            list.Add(record);
        }

        return pendingByChunk;
    }

    /// <summary>
    /// Parses a server portal payload with the standard client grammar. Returns null when the
    /// payload does not parse or does not match the expected id.
    /// </summary>
    private static IDatRecord? ParsePortalObject<T>(byte[] payload, uint id)
        where T : class, Acme.Dat.IDatRecord, new() {
        try {
            if (!DatNativeRecords.TryUnpack<T>(payload, out var obj) || obj == null) {
                return null;
            }

            uint unpackedId = obj switch {
                Setup setup => setup.Id,
                GfxObj gfx => gfx.Id,
                Scene scene => scene.Id,
                PalSet palSet => palSet.Id,
                ClothingTable clothing => clothing.Id,
                SpellTable => 0x0E00000E,
                SpellComponentTable => 0x0E00000F,
                SkillTable => 0x0E000004,
                VitalTable => 0x0E000003,
                ExperienceTable => 0x0E000018,
                CharGen => 0x0E000002,
                Region region => region.Id,
                _ => 0,
            };
            return unpackedId == id ? obj : null;
        }
        catch {
            return null;
        }
    }

    private static bool TryImportPortalTable(
        DefaultDatReaderWriter writer,
        int iteration,
        PendingRecord record,
        byte[] payload,
        Result result) {
        if (!PortalImportTypes.TryGetValue(record.TypeGuess, out var parse)) {
            return true;
        }

        var obj = parse(payload, record.Id);
        if (obj == null) {
            result.DecodeFailures++;
            return false;
        }

        var written = obj switch {
            PalSet pal => writer.Portal().TryWriteFile(pal, iteration),
            ClothingTable clothing => writer.Portal().TryWriteFile(clothing, iteration),
            _ => false,
        };
        if (!written) {
            result.WriteFailures++;
            return false;
        }

        result.PortalTablesAdded++;
        return true;
    }

    // ---- server-grammar decoders (PRE_TOD + PHATSDK_USE_EXTENDED_CELL_DATA) ----

    private static bool TryImportEnvCell(
        DefaultDatReaderWriter writer,
        int iteration,
        uint id,
        byte[] payload,
        Result result,
        HashSet<uint> referencedEnvironments,
        HashSet<ushort> referencedSurfaces,
        HashSet<uint> referencedModels) {
        // Server layout: u32 flags, u32 id, u8 numSurfaces, u8 numPortals, u16 numStabs,
        // surfaces u16[] align4, u16 envId, u16 structIndex, Frame(28B),
        // portals { u16 flags, u16 polyIdx, u16 otherCell, i16 otherPortal },
        // stabs u16[] align4, flags&2 statics { u32 did, Frame },
        // flags&4 dynamic spawns (server-only), flags&8 restriction iid (server-only).
        var reader = new SpanReader(payload);
        try {
            uint flags = reader.U32();
            uint fileId = reader.U32();
            if ((flags & ~0xFu) != 0 || fileId != id) {
                result.DecodeFailures++;
                return false;
            }

            byte numSurfaces = reader.U8();
            byte numPortals = reader.U8();
            ushort numStabs = reader.U16();

            var envCell = new EnvCell {
                Id = id,
                // Keep SeenOutside + HasStaticObjs; drop server-only dynamic-spawn (4) and
                // restriction (8) bits — instance ids from the 2005 shard are meaningless on a
                // fresh emulator world and restriction entries would lock interiors.
                Flags = flags & 0x3u,
            };

            for (int i = 0; i < numSurfaces; i++) {
                ushort surface = reader.U16();
                envCell.Surfaces.Add(surface);
                referencedSurfaces.Add(surface);
            }

            reader.Align4();
            envCell.EnvironmentId = reader.U16();
            envCell.CellStructure = reader.U16();
            referencedEnvironments.Add(envCell.EnvironmentId);
            envCell.Position = reader.Frame();

            for (int i = 0; i < numPortals; i++) {
                envCell.CellPortals.Add(new CellPortal {
                    Flags = reader.U16(),
                    PolygonId = reader.U16(),
                    OtherCellId = reader.U16(),
                    OtherPortalId = reader.U16(),
                });
            }

            for (int i = 0; i < numStabs; i++) {
                envCell.VisibleCells.Add(reader.U16());
            }

            reader.Align4();

            if ((flags & 2) != 0) {
                uint staticCount = reader.U32();
                if (staticCount > 4096) {
                    result.DecodeFailures++;
                    return false;
                }

                for (int i = 0; i < staticCount; i++) {
                    var stab = new Stab { Id = reader.U32(), Frame = reader.Frame() };
                    envCell.StaticObjects.Add(stab);
                    referencedModels.Add(stab.Id);
                }
            }

            if ((flags & 4) != 0) {
                uint dynamicCount = reader.U32();
                if (dynamicCount > 4096) {
                    result.DecodeFailures++;
                    return false;
                }

                reader.Skip((int)dynamicCount * 40); // u32 wcid + Position(32B) + u32 iid
            }

            if ((flags & 8) != 0) {
                reader.Skip(4); // restriction iid
            }

            if (!reader.AtEnd) {
                result.DecodeFailures++;
                return false;
            }

            if (!writer.TrySave(envCell, iteration)) {
                result.WriteFailures++;
                return false;
            }

            result.EnvCellsAdded++;
            return true;
        }
        catch (ArgumentOutOfRangeException) {
            result.DecodeFailures++;
            return false;
        }
    }

    private static bool TryImportLandBlockInfo(
        DefaultDatReaderWriter writer,
        int iteration,
        uint id,
        byte[] payload,
        Result result,
        HashSet<uint> referencedModels) {
        // Server layout: u32 id, objects { u32 did, Frame }, u32 buildingInfo (LOWORD count,
        // HIWORD LBIPackMask), buildings { u32 did, Frame, u32 numLeaves, u32 numPortals,
        // portals { u16 flags, u16 cell, u16 otherPortal, u16 numStabs, stabs u16[], align4 } },
        // align4, u32 version, weenies/links (server-only), u32 numCells + cell ids,
        // mask&1 restriction, mask&2 ownership (server-only instance data).
        var reader = new SpanReader(payload);
        try {
            uint fileId = reader.U32();
            if (fileId != id) {
                result.DecodeFailures++;
                return false;
            }

            uint numObjects = reader.U32();
            if (numObjects > 4096) {
                result.DecodeFailures++;
                return false;
            }

            var lbi = new LandBlockInfo { Id = id };
            for (int i = 0; i < numObjects; i++) {
                var stab = new Stab { Id = reader.U32(), Frame = reader.Frame() };
                lbi.Objects.Add(stab);
                referencedModels.Add(stab.Id);
            }

            uint buildingInfo = reader.U32();
            ushort buildingCount = (ushort)(buildingInfo & 0xFFFF);
            ushort packMask = (ushort)(buildingInfo >> 16);
            if (buildingCount > 256 || packMask > 0xF) {
                result.DecodeFailures++;
                return false;
            }

            for (int i = 0; i < buildingCount; i++) {
                var building = new BuildingInfo {
                    ModelId = reader.U32(),
                    Frame = reader.Frame(),
                    NumLeaves = reader.U32(),
                };
                referencedModels.Add(building.ModelId);

                uint numPortals = reader.U32();
                if (numPortals > 1024) {
                    result.DecodeFailures++;
                    return false;
                }

                for (int j = 0; j < numPortals; j++) {
                    var portal = new BuildingPortal {
                        Flags = reader.U16(),
                        OtherCellId = reader.U16(),
                        OtherPortalId = reader.U16(),
                    };
                    ushort stabCount = reader.U16();
                    for (int k = 0; k < stabCount; k++) {
                        portal.StabList.Add(reader.U16());
                    }

                    reader.Align4();
                    building.Portals.Add(portal);
                }

                lbi.Buildings.Add(building);
            }

            reader.Align4();
            uint version = reader.U32();
            if (version > 100) {
                result.DecodeFailures++;
                return false;
            }

            uint numWeenies = reader.U32();
            if (numWeenies > 4096) {
                result.DecodeFailures++;
                return false;
            }

            reader.Skip((int)numWeenies * 40); // u32 wcid + Position(32B) + u32 iid

            uint numWeenieLinks = reader.U32();
            if (numWeenieLinks > 16384) {
                result.DecodeFailures++;
                return false;
            }

            reader.Skip((int)numWeenieLinks * 8);

            uint numCells = reader.U32();
            if (numCells > 4096) {
                result.DecodeFailures++;
                return false;
            }

            lbi.NumCells = numCells;
            reader.Skip((int)numCells * 4); // client grammar derives cell ids from NumCells

            // Server restriction/ownership tails carry live housing instance ids; client
            // RestrictionTable stays empty so interiors are not locked against a fresh world DB.
            if ((packMask & 1) != 0) {
                ushort count = reader.U16();
                reader.U16();
                reader.Skip(count * 8);
            }

            if ((packMask & 2) != 0) {
                ushort count = reader.U16();
                reader.U16();
                for (int i = 0; i < count; i++) {
                    reader.Skip(4);
                    uint owned = reader.U32();
                    if (owned > 4096) {
                        result.DecodeFailures++;
                        return false;
                    }

                    reader.Skip((int)owned * 4);
                }
            }

            if (!reader.AtEnd) {
                result.DecodeFailures++;
                return false;
            }

            if (!writer.TrySave(lbi, iteration)) {
                result.WriteFailures++;
                return false;
            }

            result.LandBlockInfosAdded++;
            return true;
        }
        catch (ArgumentOutOfRangeException) {
            result.DecodeFailures++;
            return false;
        }
    }

    private static bool TryImportLandBlock(
        DefaultDatReaderWriter writer,
        int iteration,
        uint id,
        byte[] payload,
        Result result) {
        // Server LandBlock uses the retail-exact client grammar:
        // u32 id, u32 hasObjects, 81 x u16 terrain, 81 x u8 height, pad to 4.
        var reader = new SpanReader(payload);
        try {
            uint fileId = reader.U32();
            if (fileId != id) {
                result.DecodeFailures++;
                return false;
            }

            var landBlock = new LandBlock {
                Id = id,
                HasObjects = reader.U32() != 0,
            };
            for (int i = 0; i < 81; i++) {
                landBlock.Terrain[i] = reader.U16();
            }

            for (int i = 0; i < 81; i++) {
                landBlock.Height[i] = reader.U8();
            }

            if (!writer.TrySave(landBlock, iteration)) {
                result.WriteFailures++;
                return false;
            }

            result.LandBlocksAdded++;
            return true;
        }
        catch (ArgumentOutOfRangeException) {
            result.DecodeFailures++;
            return false;
        }
    }

    private struct SpanReader {
        private readonly byte[] _payload;
        private int _pos;

        public SpanReader(byte[] payload) {
            _payload = payload;
            _pos = 0;
        }

        public readonly bool AtEnd => _pos == _payload.Length;

        public void Align4() => _pos = (_pos + 3) & ~3;

        public void Skip(int count) {
            if (count < 0 || _pos + count > _payload.Length) {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _pos += count;
        }

        public byte U8() {
            if (_pos + 1 > _payload.Length) {
                throw new ArgumentOutOfRangeException(nameof(_pos));
            }

            return _payload[_pos++];
        }

        public ushort U16() {
            if (_pos + 2 > _payload.Length) {
                throw new ArgumentOutOfRangeException(nameof(_pos));
            }

            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_payload.AsSpan(_pos));
            _pos += 2;
            return value;
        }

        public uint U32() {
            if (_pos + 4 > _payload.Length) {
                throw new ArgumentOutOfRangeException(nameof(_pos));
            }

            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_payload.AsSpan(_pos));
            _pos += 4;
            return value;
        }

        public float F32() {
            if (_pos + 4 > _payload.Length) {
                throw new ArgumentOutOfRangeException(nameof(_pos));
            }

            float value = BinaryPrimitives.ReadSingleLittleEndian(_payload.AsSpan(_pos));
            _pos += 4;
            return value;
        }

        public Frame Frame() {
            var origin = new Vector3(F32(), F32(), F32());
            // Server frames store W first; System.Numerics ctor takes (x, y, z, w).
            float w = F32();
            float x = F32();
            float y = F32();
            float z = F32();
            return new Frame { Origin = origin, Orientation = new Quaternion(x, y, z, w) };
        }
    }
}
