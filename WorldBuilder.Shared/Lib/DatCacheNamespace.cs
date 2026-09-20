using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WorldBuilder.Shared.Lib {
    public static class DatCacheNamespace {
        public static string FromDatDirectory(string datDirectory, DatProjectMode mode) {
            string fullPath = Path.GetFullPath(datDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();

            string input = $"{mode}:{fullPath}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return $"{mode.ToString().ToLowerInvariant()}-{Convert.ToHexString(hash.AsSpan(0, 8))}";
        }

        public static string FromProjectFile(string projectFilePath) {
            string fullPath = Path.GetFullPath(projectFilePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath));
            return $"project-{Convert.ToHexString(hash.AsSpan(0, 8))}";
        }
    }
}
