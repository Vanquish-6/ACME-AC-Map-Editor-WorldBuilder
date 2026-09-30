using System.Buffers.Binary;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Dark Majesty portal images such as the connection background and intro stills:
/// file id, width, height, then tightly packed RGB.
/// </summary>
public static class LegacyDirectRenderSurface {
    public const int HeaderSize = 12;

    public static bool TryParse(ReadOnlySpan<byte> file, out uint id, out int width, out int height) {
        id = 0;
        width = 0;
        height = 0;
        if (file.Length < HeaderSize) {
            return false;
        }

        id = BinaryPrimitives.ReadUInt32LittleEndian(file);
        width = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(4));
        height = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(8));
        if (width <= 0 || height <= 0) {
            return false;
        }

        long rgbBytes = (long)width * height * 3;
        return rgbBytes <= int.MaxValue - HeaderSize && file.Length == HeaderSize + rgbBytes;
    }

    public static byte[] Encode(uint id, int width, int height, ReadOnlySpan<byte> rgb) {
        if (width <= 0 || height <= 0) {
            throw new ArgumentOutOfRangeException(nameof(width), "Legacy image size must be positive.");
        }

        int expected = checked(width * height * 3);
        if (rgb.Length != expected) {
            throw new ArgumentException($"Expected {expected} RGB bytes for {width}x{height}.", nameof(rgb));
        }

        byte[] file = new byte[HeaderSize + expected];
        BinaryPrimitives.WriteUInt32LittleEndian(file, id);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), height);
        rgb.CopyTo(file.AsSpan(HeaderSize));
        return file;
    }
}

/// <summary>
/// Writes same-size file replacements into a legacy <c>portal.dat</c>.
/// </summary>
public static class LegacyPortalImageWriter {
    public static bool TryRead(string portalPath, uint id, out byte[]? bytes) {
        using var database = new LegacyDatDatabase(portalPath, isCellDatabase: false);
        return database.TryReadFileBytes(id, out bytes);
    }

    public static void Apply(string portalPath, IReadOnlyDictionary<uint, byte[]> replacements) {
        ArgumentException.ThrowIfNullOrWhiteSpace(portalPath);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0) {
            return;
        }

        using var database = new LegacyDatDatabase(portalPath, isCellDatabase: false, writable: true);
        foreach (var (id, bytes) in replacements) {
            if (!database.TryOverwriteSameSize(id, bytes)) {
                throw new InvalidDataException(
                    $"portal.dat has no same-size record 0x{id:X8} ({bytes.Length} bytes).");
            }
        }
    }
}
