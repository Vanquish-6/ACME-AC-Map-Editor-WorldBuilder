using System.Text;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Reads DM UI text blobs (portal type <c>0x31</c>) from a legacy source.
/// </summary>
public static class LegacyDatDmPortalStringReader {
    public const uint PortalStringTypePrefix = 0x31000000;

    public static IReadOnlyDictionary<uint, string> ReadAll(LegacyDatReader legacySource) {
        ArgumentNullException.ThrowIfNull(legacySource);
        return legacySource.ReadPortalAsciiStrings(0x31);
    }

    public static bool TryRead(LegacyDatReader legacySource, uint id, out string text) {
        ArgumentNullException.ThrowIfNull(legacySource);
        return legacySource.TryReadPortalAsciiString(id, out text);
    }

    internal static string ExtractAscii(byte[] bytes) {
        var paragraphs = new List<string>();
        var current = new StringBuilder();

        void Flush() {
            if (current.Length < 4) {
                current.Clear();
                return;
            }

            paragraphs.Add(current.ToString());
            current.Clear();
        }

        foreach (byte value in bytes) {
            if (value is >= 32 and <= 126 or 9) {
                current.Append((char)value);
                continue;
            }

            if (value is 10 or 13) {
                if (current.Length > 0) {
                    current.Append(' ');
                }

                continue;
            }

            Flush();
        }

        Flush();
        return string.Join("\n\n", paragraphs).Trim();
    }
}
