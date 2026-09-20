using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MessagePack;

namespace Acme.Dat;

/// <summary>Archive selection in the native DAT ABI. These values are ABI constants.</summary>
public enum DatArchive { Portal = 0, Cell = 1, Local = 2, Highres = 3 }

/// <summary>
/// Owns a native archive collection. DAT storage and codecs belong to the native library;
/// this class only transfers bytes. Dispose waits for active calls, and every
/// native allocation is freed by the same DLL that allocated it.
/// </summary>
public sealed class AethDatNative : IDisposable {
    private readonly object _gate = new();
    private readonly DatHandle _handle;
    private static readonly MessagePackSerializerOptions MsgPack =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public AethDatNative(string directory, bool writable = false) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        CheckVersion();
        _handle = Native.Open(Path.GetFullPath(directory), writable ? 1 : 0);
        if (_handle.IsInvalid) {
            _handle.Dispose();
            throw Error();
        }
    }

    private AethDatNative(DatHandle handle) {
        _handle = handle;
    }

    public static AethDatNative Create(string directory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        CheckVersion();
        var handle = Native.Create(Path.GetFullPath(directory));
        if (handle.IsInvalid) {
            handle.Dispose();
            throw Error();
        }
        return new AethDatNative(handle);
    }

    public bool ContainsFile(DatArchive archive, uint id) {
        lock (_gate) {
            CheckOpen();
            int result = Native.Contains(_handle, archive, id);
            if (result < 0) throw Error();
            return result != 0;
        }
    }

    public uint[] ListFileIds(DatArchive archive) {
        lock (_gate) {
            CheckOpen();
            if (Native.List(_handle, archive, out var ptr, out uint count) != 0) throw Error();
            try {
                var ids = new uint[checked((int)count)];
                if (count != 0) {
                    unsafe { new ReadOnlySpan<uint>((void*)ptr, ids.Length).CopyTo(ids); }
                }
                return ids;
            }
            finally { Native.FreeIds(ptr, count); }
        }
    }

    public bool TryGetFileBytes(DatArchive archive, uint id, out byte[]? bytes) {
        lock (_gate) {
            CheckOpen();
            int status = Native.Read(_handle, archive, id, out var ptr, out uint length);
            if (status == 1) { bytes = null; return false; }
            if (status != 0) throw Error();
            try {
                bytes = new byte[checked((int)length)];
                if (length != 0) Marshal.Copy(ptr, bytes, 0, bytes.Length);
                return true;
            }
            finally { Native.Free(ptr, length); }
        }
    }

    public void WriteFileBytes(DatArchive archive, uint id, byte[] bytes, int iteration = 0) {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate) {
            CheckOpen();
            if (Native.Write(_handle, archive, id, bytes, checked((uint)bytes.Length), iteration) != 0) throw Error();
        }
    }

    public LandBlock[] ReadLandblocks() {
        lock (_gate) {
            CheckOpen();
            if (Native.ReadLandblocks(_handle, out var ptr, out uint length) != 0) throw Error();
            try {
                int stride = 256;
                int count = (int)length / stride;
                var blocks = new LandBlock[count];
                if (count == 0) return blocks;
                unsafe {
                    var span = new ReadOnlySpan<byte>((void*)ptr, (int)length);
                    for (int i = 0; i < count; i++) {
                        var slice = span.Slice(i * stride, stride);
                        uint id = BitConverter.ToUInt32(slice);
                        uint hasObjects = BitConverter.ToUInt32(slice.Slice(4));
                        var terrain = new ushort[81];
                        MemoryMarshal.Cast<byte, ushort>(slice.Slice(8, 162)).CopyTo(terrain);
                        var height = slice.Slice(8 + 162, 81).ToArray();
                        blocks[i] = new LandBlock {
                            Id = id,
                            HasObjects = hasObjects != 0,
                            Terrain = terrain,
                            Height = height,
                            Padding = slice[8 + 162 + 81],
                        };
                    }
                }
                return blocks;
            }
            finally { Native.Free(ptr, length); }
        }
    }

    public int GetIteration(DatArchive archive) {
        if (!TryGetFileBytes(archive, DatRecordTable.IterationId, out var bytes) || bytes is not { Length: >= 4 }) {
            return 0;
        }
        return BitConverter.ToInt32(bytes, 0);
    }

    public void SetIteration(DatArchive archive, int iteration) {
        var bytes = BitConverter.GetBytes(iteration);
        WriteFileBytes(archive, DatRecordTable.IterationId, bytes, iteration);
    }

    public void Flush() {
        lock (_gate) {
            CheckOpen();
            if (Native.Flush(_handle) != 0) throw Error();
        }
    }

    public void ResetTree(DatArchive archive) {
        lock (_gate) {
            CheckOpen();
            if (Native.ResetTree(_handle, archive) != 0) throw Error();
        }
    }

    public T Decode<T>(uint kind, byte[] datBytes) where T : class, IDatRecord =>
        DecodeStatic<T>(kind, datBytes);

    public byte[] Encode<T>(uint kind, T record) where T : class, IDatRecord =>
        EncodeStatic(kind, record);

    public static LandBlock DecodeLandblock(byte[] datBytes) => DecodeStatic<LandBlock>(1, datBytes);

    public static byte[] EncodeLandblock(LandBlock record) => EncodeStatic(1, record);

    public static T DecodeStatic<T>(uint kind, byte[] datBytes) where T : class, IDatRecord {
        var record = MessagePackSerializer.Deserialize<T>(Transcode(kind, datBytes, false), MsgPack);
        record.AfterDeserialize();
        return record;
    }

    public static byte[] EncodeStatic<T>(uint kind, T record) where T : class, IDatRecord {
        record.BeforeSerialize();
        return Transcode(kind, MessagePackSerializer.Serialize(record), true);
    }

    public void Dispose() { lock (_gate) if (!_handle.IsInvalid && !_handle.IsClosed) _handle.Dispose(); }
    private void CheckOpen() => ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
    private static IOException Error() => new(Marshal.PtrToStringUTF8(Native.LastError()) ?? "native DAT library failed");
    private static void CheckVersion() {
        if (Native.Version() != 2) throw new NotSupportedException("Unsupported native DAT ABI; install the matching native library.");
    }

    private static byte[] Transcode(uint kind, byte[] bytes, bool encode) {
        ArgumentNullException.ThrowIfNull(bytes);
        CheckVersion();
        int status = encode
            ? Native.Encode(kind, bytes, checked((uint)bytes.Length), out var ptr, out uint length)
            : Native.Decode(kind, bytes, checked((uint)bytes.Length), out ptr, out length);
        if (status != 0) throw Error();
        try {
            byte[] output = new byte[checked((int)length)];
            if (length != 0) Marshal.Copy(ptr, output, 0, output.Length);
            return output;
        }
        finally { Native.Free(ptr, length); }
    }

    private sealed class DatHandle : SafeHandleZeroOrMinusOneIsInvalid {
        public DatHandle() : base(true) { }
        protected override bool ReleaseHandle() { Native.Close(handle); return true; }
    }

    private static class Native {
        private const string Dll = "acme_dat";
        [DllImport(Dll, EntryPoint = "aeth_dat_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint Version();
        [DllImport(Dll, EntryPoint = "aeth_dat_decode", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Decode(uint kind, byte[] data, uint length, out IntPtr output, out uint outputLength);
        [DllImport(Dll, EntryPoint = "aeth_dat_encode", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Encode(uint kind, byte[] data, uint length, out IntPtr output, out uint outputLength);
        [DllImport(Dll, EntryPoint = "aeth_dat_open", CallingConvention = CallingConvention.Cdecl)]
        internal static extern DatHandle Open([MarshalAs(UnmanagedType.LPUTF8Str)] string directory, int writable);
        [DllImport(Dll, EntryPoint = "aeth_dat_create_directory", CallingConvention = CallingConvention.Cdecl)]
        internal static extern DatHandle Create([MarshalAs(UnmanagedType.LPUTF8Str)] string directory);
        [DllImport(Dll, EntryPoint = "aeth_dat_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Close(IntPtr handle);
        [DllImport(Dll, EntryPoint = "aeth_dat_last_error", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr LastError();
        [DllImport(Dll, EntryPoint = "aeth_dat_contains", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Contains(DatHandle handle, DatArchive archive, uint id);
        [DllImport(Dll, EntryPoint = "aeth_dat_list", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int List(DatHandle handle, DatArchive archive, out IntPtr ids, out uint count);
        [DllImport(Dll, EntryPoint = "aeth_dat_read", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Read(DatHandle handle, DatArchive archive, uint id, out IntPtr bytes, out uint length);
        [DllImport(Dll, EntryPoint = "aeth_dat_write", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Write(DatHandle handle, DatArchive archive, uint id, byte[] bytes, uint length, int iteration);
        [DllImport(Dll, EntryPoint = "aeth_dat_read_landblocks", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ReadLandblocks(DatHandle handle, out IntPtr bytes, out uint length);
        [DllImport(Dll, EntryPoint = "aeth_dat_flush", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Flush(DatHandle handle);
        [DllImport(Dll, EntryPoint = "aeth_dat_reset_tree", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ResetTree(DatHandle handle, DatArchive archive);
        [DllImport(Dll, EntryPoint = "aeth_dat_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Free(IntPtr bytes, uint length);
        [DllImport(Dll, EntryPoint = "aeth_dat_free_u32", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FreeIds(IntPtr ids, uint count);
    }
}
