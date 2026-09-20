using MessagePack;

namespace Acme.Dat;

public static class DatNativeRecords {
    public static byte[] Pack<T>(T record) where T : class, IDatRecord, new() {
        if (!DatRecordTable.TryGet(typeof(T), out var info)) {
            throw new NotSupportedException($"No native DAT pack kind for {typeof(T).Name}.");
        }

        return AethDatNative.EncodeStatic((uint)info.Kind, record);
    }

    public static T Unpack<T>(byte[] bytes) where T : class, IDatRecord, new() {
        if (!DatRecordTable.TryGet(typeof(T), out var info)) {
            throw new NotSupportedException($"No native DAT unpack kind for {typeof(T).Name}.");
        }

        return AethDatNative.DecodeStatic<T>((uint)info.Kind, bytes);
    }

    public static bool TryUnpack<T>(byte[] bytes, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T record)
        where T : class, IDatRecord, new() {
        try {
            record = Unpack<T>(bytes);
            return true;
        }
        catch {
            record = null;
            return false;
        }
    }

    public static T CloneMessagePack<T>(T record) where T : class, IDatRecord {
        record.BeforeSerialize();
        var copy = MessagePackSerializer.Deserialize<T>(MessagePackSerializer.Serialize(record))!;
        copy.AfterDeserialize();
        return copy;
    }
}
