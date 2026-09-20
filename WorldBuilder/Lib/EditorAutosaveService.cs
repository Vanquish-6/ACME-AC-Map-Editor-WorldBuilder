using System;
using System.IO;

namespace WorldBuilder.Lib {
    /// <summary>
    /// Lightweight session lock + timestamped sidecar so a crashed editor can
    /// tell the user their last ForceSave is the recovery point.
    /// </summary>
    public static class EditorAutosaveService {
        public const string SessionLockName = ".acme-session-lock";

        public static string GetAutosaveDirectory(string projectDirectory) {
            var dir = Path.Combine(projectDirectory, ".autosave");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static bool HasUncleanShutdown(string projectDirectory) {
            try {
                return File.Exists(Path.Combine(projectDirectory, SessionLockName));
            }
            catch {
                return false;
            }
        }

        public static void BeginSession(string projectDirectory) {
            try {
                Directory.CreateDirectory(projectDirectory);
                File.WriteAllText(Path.Combine(projectDirectory, SessionLockName), DateTime.UtcNow.ToString("o"));
            }
            catch {
                // Ignore lock write failures — editing still works.
            }
        }

        public static void EndSession(string projectDirectory) {
            try {
                var path = Path.Combine(projectDirectory, SessionLockName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public static void Touch(string projectDirectory, string key) {
            try {
                var dir = GetAutosaveDirectory(projectDirectory);
                File.WriteAllText(Path.Combine(dir, key + ".stamp"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }
        }

        public static string? ReadStamp(string projectDirectory, string key) {
            try {
                var path = Path.Combine(GetAutosaveDirectory(projectDirectory), key + ".stamp");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch {
                return null;
            }
        }
    }
}
