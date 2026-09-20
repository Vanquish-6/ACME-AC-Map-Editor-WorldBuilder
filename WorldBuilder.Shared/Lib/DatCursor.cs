using System.Buffers.Binary;
using System.Numerics;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Conversion and test fixture cursor. DAT record grammar for editor saves belongs in the native library.
/// </summary>
public sealed class DatBinReader {
    private readonly byte[] _data;
    private int _pos;

    public DatBinReader(byte[] data) : this(data.AsMemory()) { }

    public DatBinReader(ReadOnlyMemory<byte> data) {
        _data = data.ToArray();
    }

    public DatBinReader(ReadOnlyMemory<byte> data, object? unpackContext) : this(data) { _ = unpackContext; }

    public int Offset { get => _pos; set => _pos = value; }
    public int Length => _data.Length;
    public int Remaining => _data.Length - _pos;

    public byte ReadByte() => _data[_pos++];
    public sbyte ReadSByte() => (sbyte)ReadByte();
    public ushort ReadUInt16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos)); _pos += 2; return v; }
    public short ReadInt16() { var v = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(_pos)); _pos += 2; return v; }
    public uint ReadUInt32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos)); _pos += 4; return v; }
    public int ReadInt32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos)); _pos += 4; return v; }
    public float ReadSingle() { var v = BitConverter.ToSingle(_data, _pos); _pos += 4; return v; }
    public double ReadDouble() { var v = BitConverter.ToDouble(_data, _pos); _pos += 8; return v; }
    public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());
    public Quaternion ReadQuaternion() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
    public byte[] ReadBytes(int count) {
        var slice = _data.AsSpan(_pos, count).ToArray();
        _pos += count;
        return slice;
    }
    public byte[] ReadRemaining() => ReadBytes(Remaining);
    public void Align(int n) {
        int rem = _pos % n;
        if (rem != 0) _pos += n - rem;
    }
    public T ReadItem<T>() where T : new() => new();
}

public sealed class DatBinWriter {
    private byte[] _buffer;
    public int Offset { get; private set; }

    public DatBinWriter(byte[] buffer) { _buffer = buffer; }
    public DatBinWriter(Memory<byte> buffer) { _buffer = buffer.ToArray(); }

    public void WriteByte(byte v) { Ensure(1); _buffer[Offset++] = v; }
    public void WriteSByte(sbyte v) => WriteByte((byte)v);
    public void WriteUInt16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(Offset), v); Offset += 2; }
    public void WriteInt16(short v) { Ensure(2); BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(Offset), v); Offset += 2; }
    public void WriteUInt32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(Offset), v); Offset += 4; }
    public void WriteInt32(int v) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(Offset), v); Offset += 4; }
    public void WriteSingle(float v) { Ensure(4); BitConverter.TryWriteBytes(_buffer.AsSpan(Offset), v); Offset += 4; }
    public void WriteDouble(double v) { Ensure(8); BitConverter.TryWriteBytes(_buffer.AsSpan(Offset), v); Offset += 8; }
    public void WriteVector3(Vector3 v) { WriteSingle(v.X); WriteSingle(v.Y); WriteSingle(v.Z); }
    public void WriteQuaternion(Quaternion v) { WriteSingle(v.X); WriteSingle(v.Y); WriteSingle(v.Z); WriteSingle(v.W); }

    public void WriteBytes(ReadOnlySpan<byte> bytes) {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(Offset));
        Offset += bytes.Length;
    }

    public void WriteBytes(byte[] bytes, int count) => WriteBytes(bytes.AsSpan(0, count));

    public void WriteItem<T>(T item) {
        switch (item) {
            case Frame frame:
                WriteVector3(frame.Origin);
                WriteQuaternion(frame.Orientation);
                break;
            case Stab stab:
                WriteUInt32(stab.Id);
                WriteItem(stab.Frame);
                break;
            case CellPortal portal:
                WriteUInt16(portal.Flags);
                WriteUInt16(portal.PolygonId);
                WriteUInt16(portal.OtherCellId);
                WriteUInt16(portal.OtherPortalId);
                break;
            case BuildingInfo building:
                WriteUInt32(building.ModelId);
                WriteItem(building.Frame);
                WriteUInt32(building.NumLeaves);
                WriteUInt32((uint)building.Portals.Count);
                foreach (var portal in building.Portals) {
                    WriteUInt16(portal.Flags);
                    WriteUInt16(portal.OtherCellId);
                    WriteUInt16(portal.OtherPortalId);
                    WriteUInt16((ushort)portal.StabList.Count);
                    foreach (ushort cell in portal.StabList) {
                        WriteUInt16(cell);
                    }
                    Align(4);
                }
                break;
            case AC1LegacyPStringBase<byte>:
            case AC1LegacyPStringBase<char>:
                WriteUInt16(0);
                Align(4);
                break;
            default:
                break;
        }
    }

    public void Align(int n) {
        int rem = Offset % n;
        if (rem != 0) {
            int pad = n - rem;
            Ensure(pad);
            Offset += pad;
        }
    }

    private void Ensure(int extra) {
        int needed = Offset + extra;
        if (needed <= _buffer.Length) {
            return;
        }

        int size = Math.Max(_buffer.Length * 2, needed);
        Array.Resize(ref _buffer, size);
    }
}

public interface IPackable {
    void Pack(DatBinWriter writer);
}

public interface IUnpackable {
    void Unpack(DatBinReader reader);
}

internal sealed class AC1LegacyPStringBase<T> {
    public string Value { get; set; } = "";
}

internal static class PartsMask {
    public const uint HasSkyInfo = 0x00000001;
    public const uint HasSceneInfo = 0x00000004;
}

internal static class DatIdQueries {
    public static IReadOnlyList<uint> GetLandBlockInfoIds(this IDatReaderWriter dats) =>
        dats.GetAllIdsOfType<LandBlockInfo>().ToList();

    public static IReadOnlyList<uint> GetEnvCellIds(this IDatReaderWriter dats) =>
        dats.GetAllIdsOfType<EnvCell>().ToList();
}
