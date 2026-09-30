using System.Diagnostics.CodeAnalysis;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Writes a legacy <c>cell.dat</c> / <c>portal.dat</c> folder by replacing existing records.
/// The native library only allocates new sectors in retail <c>client_*.dat</c> files, so a
/// record that is missing or a different size is left unchanged and reported in <see cref="Failures"/>.
/// </summary>
public sealed class LegacyDatSameSizeWriter : IDatReaderWriter {
    readonly Dictionary<DatArchive, LegacyDatDatabase> _databases = new();
    readonly List<string> _failures = new();
    int _suspendFailures;

    public DatProjectMode Mode => DatProjectMode.LegacyPreTod;
    public bool CanWrite => true;
    public string CacheNamespace { get; }
    public string DatDirectory { get; }
    public DatCatalogTree Tree => new DatArchiveSession(this, DatArchive.Portal).Tree;
    public DatIterationInfo Iteration => new DatArchiveSession(this, DatArchive.Portal).Iteration;
    public IDatReaderWriter Dats => this;
    public IReadOnlyList<string> Failures => _failures;

    public LegacyDatSameSizeWriter(string datDirectory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(datDirectory);
        DatDirectory = Path.GetFullPath(datDirectory);
        CacheNamespace = DatCacheNamespace.FromDatDirectory(DatDirectory, Mode);
        try {
            Open(DatArchive.Cell, "cell.dat", isCellDatabase: true);
            Open(DatArchive.Portal, "portal.dat", isCellDatabase: false);
            string language = Path.Combine(DatDirectory, "language.dat");
            if (File.Exists(language)) {
                Open(DatArchive.Local, "language.dat", isCellDatabase: false);
            }
        }
        catch {
            Dispose();
            throw;
        }
    }

    public void SuspendFailures() => _suspendFailures++;

    public void ResumeFailures() => _suspendFailures = Math.Max(0, _suspendFailures - 1);

    public bool ContainsFile(DatArchive archive, uint id) =>
        _databases.TryGetValue(archive, out var database) && database.FileIds.Contains(id);

    public uint[] ListFileIds(DatArchive archive) =>
        _databases.TryGetValue(archive, out var database)
            ? database.FileIds.ToArray()
            : Array.Empty<uint>();

    public bool TryGetFileBytes(DatArchive archive, uint id, out byte[]? bytes) {
        bytes = null;
        return _databases.TryGetValue(archive, out var database) && database.TryReadFileBytes(id, out bytes);
    }

    public bool TryWriteFileBytes(DatArchive archive, uint id, byte[] bytes, int iteration = 0) {
        _ = iteration;
        if (!_databases.TryGetValue(archive, out var database)) {
            Fail($"0x{id:X8} cannot be written. This legacy export has no {ArchiveFileName(archive)}.");
            return false;
        }

        if (!database.TryOverwriteSameSize(id, bytes)) {
            Fail($"0x{id:X8} was not replaced in {ArchiveFileName(archive)}. Legacy export only replaces an existing record of the same size ({bytes.Length} bytes).");
            return false;
        }

        return true;
    }

    public int GetIteration(DatArchive archive) =>
        _databases.TryGetValue(archive, out var database) ? database.Iteration : 0;

    public void SetIteration(DatArchive archive, int iteration) {
        _ = archive;
        _ = iteration;
    }

    public void ResetTree(DatArchive archive) {
        _ = archive;
    }

    public LandBlock[] ReadLandblocks() {
        var blocks = new List<LandBlock>();
        foreach (uint id in ListFileIds(DatArchive.Cell)) {
            if ((id & 0xFFFF) == 0xFFFF && TryGetLandblock(id, out var block)) {
                blocks.Add(block);
            }
        }

        return blocks.ToArray();
    }

    public void Flush() {
    }

    public bool TryGetLandblock(uint id, out LandBlock file) {
        file = null!;
        if (!TryGetFileBytes(DatArchive.Cell, id, out byte[]? bytes) || bytes == null) {
            return false;
        }

        return LegacyDatDecoders.TryDecodeLandBlock(bytes, LegacyDatVersion.DarkMajesty, 0, out file!) && file != null;
    }

    public bool TrySaveLandblock(LandBlock file, int iteration = 0) {
        _ = iteration;
        if (!TryGetFileBytes(DatArchive.Cell, file.Id, out byte[]? original) || original == null) {
            Fail($"LandBlock 0x{file.Id:X8} is not in cell.dat. Legacy export cannot add a new landblock.");
            return false;
        }

        if (!LegacyLandBlockPatch.TryApply(original, file, out byte[]? patched) || patched == null) {
            Fail($"LandBlock 0x{file.Id:X8} could not be updated in place.");
            return false;
        }

        return TryWriteFileBytes(DatArchive.Cell, file.Id, patched);
    }

    public bool TryGet<T>(uint id, [MaybeNullWhen(false)] out T file) where T : class, IDatRecord, new() {
        file = default;
        if (typeof(T) == typeof(Region)) {
            uint actualId = LegacyDatDecoders.ResolveRegionFileId(id);
            if (TryDecodeRecord(actualId, out file) && file is Region region) {
                region.Id = id;
                return true;
            }

            file = (T)(object)LegacyDatDecoders.CreateFallbackRegion(id);
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

        if (typeof(T) == typeof(EnvCell)
            && !TryDecodeRecord(id, out file)
            && TryGetFileBytes(DatArchive.Cell, id, out byte[]? cellBytes)
            && cellBytes != null
            && LegacyDatDecoders.TryDecodeLegacyEnvCell(cellBytes, out EnvCell? legacyCell)
            && legacyCell != null) {
            file = (T)(object)legacyCell;
            return true;
        }

        return TryDecodeRecord(id, out file);
    }

    public bool TrySave<T>(T file, int? iteration = 0) where T : class, IDatRecord, new() {
        _ = iteration;
        if (file is LandBlock landBlock) {
            return TrySaveLandblock(landBlock);
        }

        if (!DatRecordTable.TryGet(typeof(T), out var info)) {
            Fail($"{typeof(T).Name} cannot be written into a legacy DAT.");
            return false;
        }

        uint id = GetRecordId(file);
        if (!TryGetFileBytes(info.Archive, id, out byte[]? original) || original == null) {
            Fail($"{typeof(T).Name} 0x{id:X8} is not in {ArchiveFileName(info.Archive)}. Legacy export cannot add a new record.");
            return false;
        }

        byte[] encoded;
        try {
            if (CodecRoundTrips<T>(info.Kind, original)) {
                encoded = AethDatNative.EncodeStatic((uint)info.Kind, file);
            }
            else if (file is EnvCell envCell
                && LegacyDatDecoders.TryDecodeLegacyEnvCell(original, out _)
                && LegacyEnvCellRoundTrips(original)) {
                encoded = LegacyDatDecoders.EncodeLegacyEnvCell(envCell);
            }
            else {
                Fail($"{typeof(T).Name} 0x{id:X8} is stored in legacy format and was left unchanged.");
                return false;
            }
        }
        catch (Exception ex) {
            Fail($"{typeof(T).Name} 0x{id:X8} could not be encoded: {ex.Message}");
            return false;
        }

        if (encoded.Length != original.Length) {
            Fail($"{typeof(T).Name} 0x{id:X8} would change from {original.Length} bytes to {encoded.Length}. Legacy export keeps each record's original size.");
            return false;
        }

        return TryWriteFileBytes(info.Archive, id, encoded);
    }

    public IEnumerable<uint> GetAllIdsOfType<T>() where T : class, IDatRecord, new() {
        if (!DatRecordTable.TryGet(typeof(T), out var info)) {
            return Array.Empty<uint>();
        }

        return DatRecordTable.FilterIds(typeof(T), ListFileIds(info.Archive));
    }

    public void Dispose() {
        foreach (var database in _databases.Values) {
            database.Dispose();
        }

        _databases.Clear();
    }

    void Open(DatArchive archive, string fileName, bool isCellDatabase) {
        string path = Path.Combine(DatDirectory, fileName);
        if (!File.Exists(path)) {
            throw new FileNotFoundException($"Legacy DAT export requires {fileName}.", path);
        }

        _databases[archive] = new LegacyDatDatabase(path, isCellDatabase, writable: true);
    }

    bool TryDecodeRecord<T>(uint id, [MaybeNullWhen(false)] out T file) where T : class, IDatRecord, new() {
        file = default;
        if (!DatRecordTable.TryGet(typeof(T), out var info)) {
            return false;
        }

        uint[] ids = typeof(T) == typeof(Region) && id == 0x13000000u ? new[] { id, 0x130F0000u } : new[] { id };
        foreach (uint tryId in ids) {
            if (!TryGetFileBytes(info.Archive, tryId, out byte[]? bytes) || bytes == null) {
                continue;
            }

            try {
                file = AethDatNative.DecodeStatic<T>((uint)info.Kind, bytes);
                return file != null;
            }
            catch {
                file = default;
            }
        }

        return false;
    }

    static bool CodecRoundTrips<T>(DatKind kind, byte[] original) where T : class, IDatRecord, new() {
        try {
            T decoded = AethDatNative.DecodeStatic<T>((uint)kind, original);
            byte[] encoded = AethDatNative.EncodeStatic((uint)kind, decoded);
            return encoded.AsSpan().SequenceEqual(original);
        }
        catch {
            return false;
        }
    }

    static bool LegacyEnvCellRoundTrips(byte[] original) {
        if (!LegacyDatDecoders.TryDecodeLegacyEnvCell(original, out EnvCell? cell) || cell == null) {
            return false;
        }

        try {
            return LegacyDatDecoders.EncodeLegacyEnvCell(cell).AsSpan().SequenceEqual(original);
        }
        catch {
            return false;
        }
    }

    void Fail(string message) {
        if (_suspendFailures == 0) {
            _failures.Add(message);
        }
    }

    static string ArchiveFileName(DatArchive archive) => archive switch {
        DatArchive.Cell => "cell.dat",
        DatArchive.Local => "language.dat",
        DatArchive.Highres => "client_highres.dat",
        _ => "portal.dat",
    };

    static uint GetRecordId(IDatRecord file) => file switch {
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
}

static class LegacyLandBlockPatch {
    const int HeaderSize = 8;
    const int TerrainBytes = 81 * 2;
    const int HeightBytes = 81;

    public static bool TryApply(byte[] original, LandBlock block, out byte[]? patched) {
        patched = null;
        int minimum = HeaderSize + TerrainBytes + HeightBytes;
        if (original.Length < minimum || block.Terrain.Length < 81 || block.Height.Length < 81) {
            return false;
        }

        uint fileId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(original);
        if (fileId != block.Id) {
            return false;
        }

        patched = (byte[])original.Clone();
        for (int i = 0; i < 81; i++) {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                patched.AsSpan(HeaderSize + (i * 2)),
                block.Terrain[i]);
        }

        block.Height.AsSpan(0, HeightBytes).CopyTo(patched.AsSpan(HeaderSize + TerrainBytes));
        return true;
    }
}
