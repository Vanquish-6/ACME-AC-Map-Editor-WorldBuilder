using Acme.Dat;
using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Catalog view over one archive in an <see cref="IDatReaderWriter"/>.
/// Bytes and codecs stay in the native DAT library; this only names Portal/Cell/Local/Highres.
/// </summary>
public sealed class DatArchiveSession : IDatReaderWriter {
    private readonly IDatReaderWriter _inner;

    public DatArchiveSession(IDatReaderWriter inner, DatArchive archive) {
        _inner = inner;
        Archive = archive;
        Tree = new DatCatalogTree(this);
        Iteration = new DatIterationInfo(this);
    }

    public DatArchive Archive { get; }
    public DatCatalogTree Tree { get; }
    public DatIterationInfo Iteration { get; }
    public int CurrentIteration => _inner.GetIteration(Archive);

    public DatProjectMode Mode => _inner.Mode;
    public bool CanWrite => _inner.CanWrite;
    public string CacheNamespace => _inner.CacheNamespace;
    public string DatDirectory => _inner.DatDirectory;

    public bool ContainsFile(DatArchive archive, uint id) => _inner.ContainsFile(archive, id);
    public uint[] ListFileIds(DatArchive archive) => _inner.ListFileIds(archive);
    public bool TryGetFileBytes(DatArchive archive, uint id, out byte[]? bytes) =>
        _inner.TryGetFileBytes(archive, id, out bytes);
    public bool TryWriteFileBytes(DatArchive archive, uint id, byte[] bytes, int iteration = 0) =>
        _inner.TryWriteFileBytes(archive, id, bytes, iteration);
    public int GetIteration(DatArchive archive) => _inner.GetIteration(archive);
    public void SetIteration(DatArchive archive, int iteration) => _inner.SetIteration(archive, iteration);
    public void ResetTree(DatArchive archive) => _inner.ResetTree(archive);
    public LandBlock[] ReadLandblocks() => _inner.ReadLandblocks();
    public void Flush() => _inner.Flush();
    public bool TryGetLandblock(uint id, out LandBlock file) => _inner.TryGetLandblock(id, out file);
    public bool TrySaveLandblock(LandBlock file, int iteration = 0) => _inner.TrySaveLandblock(file, iteration);
    public bool TryGet<T>(uint id, [MaybeNullWhen(false)] out T file) where T : class, IDatRecord, new() =>
        _inner.TryGet(id, out file);
    public bool TrySave<T>(T file, int? iteration = 0) where T : class, IDatRecord, new() =>
        _inner.TrySave(file, iteration);
    public IEnumerable<uint> GetAllIdsOfType<T>() where T : class, IDatRecord, new() =>
        _inner.GetAllIdsOfType<T>();
    public IDatReaderWriter Dats => this;

    public bool HasFile(uint id) => _inner.ContainsFile(Archive, id);

    public bool TryGetFileBytes(uint id, out byte[]? bytes, bool autoDecompress = false) {
        _ = autoDecompress;
        return _inner.TryGetFileBytes(Archive, id, out bytes);
    }

    public bool TryWriteFileBytes(uint id, byte[] bytes, int length, int iteration) {
        _ = length;
        return _inner.TryWriteFileBytes(Archive, id, bytes, iteration);
    }

    public bool TryWriteFileBytes(uint id, byte[] bytes, int length, DatBTreeFile? template) {
        int iteration = template?.Iteration ?? CurrentIteration;
        return TryWriteFileBytes(id, bytes, length, iteration);
    }

    public bool TryWriteFile<T>(T file, int iteration) where T : class, IDatRecord, new() =>
        _inner.TrySave(file, iteration);

    public bool TryWriteFile<T>(T file, DatBTreeFile? template) where T : class, IDatRecord, new() =>
        _inner.TrySave(file, template?.Iteration ?? CurrentIteration);

    public void Dispose() { }
}

public sealed class DatIterationInfo {
    private readonly DatArchiveSession _session;
    public DatIterationInfo(DatArchiveSession session) => _session = session;
    public int CurrentIteration => _session.CurrentIteration;
}

/// <summary>Id-only catalog row. Native writes do not expose btree offset/flags.</summary>
public sealed class DatBTreeFile {
    public uint Id { get; set; }
    public int Iteration { get; set; }
    public int Version { get => Iteration; set => Iteration = value; }
    public uint Offset { get; set; }
    public uint Size { get; set; }
}

public sealed class DatCatalogTree : IEnumerable<DatBTreeFile> {
    private readonly DatArchiveSession _session;

    public DatCatalogTree(DatArchiveSession session) => _session = session;

    public bool HasFile(uint id) => _session.HasFile(id);

    public bool TryGetFile(uint id, out DatBTreeFile file) {
        if (!_session.HasFile(id)) {
            file = null!;
            return false;
        }

        file = new DatBTreeFile { Id = id, Iteration = _session.CurrentIteration };
        return true;
    }

    public bool TryDelete(uint id, out DatBTreeFile file) {
        file = new DatBTreeFile { Id = id, Iteration = _session.CurrentIteration };
        if (!_session.HasFile(id)) {
            return false;
        }

        LegacyDatCatalogEntryReplacer.RemoveExistingEntry(_session, _session.Archive, id);
        return true;
    }

    public IEnumerator<DatBTreeFile> GetEnumerator() {
        int iteration = _session.CurrentIteration;
        foreach (uint id in _session.ListFileIds(_session.Archive)) {
            yield return new DatBTreeFile { Id = id, Iteration = iteration };
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class DatArchiveViews {
    public static DatArchiveSession Portal(this IDatReaderWriter dats) => new(dats, DatArchive.Portal);
    public static DatArchiveSession Cell(this IDatReaderWriter dats) => new(dats, DatArchive.Cell);
    public static DatArchiveSession Local(this IDatReaderWriter dats) => new(dats, DatArchive.Local);
    public static DatArchiveSession HighRes(this IDatReaderWriter dats) => new(dats, DatArchive.Highres);

    public static DatArchiveSession Catalog(this IDatReaderWriter dats) =>
        dats as DatArchiveSession ?? dats.Portal();

    public static DatArchive ArchiveFromDatFileName(string datFileName) => DatCatalog.ArchiveFromFileName(datFileName);
}

internal static class DatFlagExtensions {
    public static bool HasFlag(this int value, EmitterType flag) => (value & (int)flag) != 0;
    public static bool HasFlag(this ushort value, PortalFlags flag) => (value & (ushort)flag) != 0;
}
