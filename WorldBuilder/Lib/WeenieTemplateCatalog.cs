using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Lib {
    /// <summary>
    /// Loads built-in and user-supplied weenie starter templates (JSON).
    /// Later sources override earlier entries with the same <c>id</c> (case-insensitive).
    /// </summary>
    public static class WeenieTemplateCatalog {
        const string EmbeddedResourceName = "WorldBuilder.Data.WeenieTemplates.json";

        public static IReadOnlyList<WeenieTemplateDefinition> Load(WorldBuilderSettings settings, Project? project) {
            var map = new Dictionary<string, WeenieTemplateDefinition>(StringComparer.OrdinalIgnoreCase);

            void AddMany(IEnumerable<WeenieTemplateDefinition> items) {
                foreach (var t in items) {
                    if (string.IsNullOrWhiteSpace(t.Id)) continue;
                    map[t.Id] = t;
                }
            }

            AddMany(LoadEmbeddedBundle());

            var diskBundle = Path.Combine(AppContext.BaseDirectory, "Data", "WeenieTemplates.json");
            if (File.Exists(diskBundle)) {
                try {
                    AddMany(WeenieTemplateJson.ParseBundle(File.ReadAllText(diskBundle)));
                }
                catch {
                    // ignore invalid user file
                }
            }

            AddMany(LoadJsonFilesInDirectory(Path.Combine(settings.AppDataDirectory, "weenie-templates")));
            if (project != null)
                AddMany(LoadJsonFilesInDirectory(Path.Combine(project.ProjectDirectory, "weenie-templates")));

            return map.Values.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static IReadOnlyList<WeenieTemplateDefinition> LoadEmbeddedBundle() {
            var asm = Assembly.GetExecutingAssembly();
            Stream? stream = asm.GetManifestResourceStream(EmbeddedResourceName);
            stream ??= OpenEmbeddedFallback(asm);
            if (stream == null) return Array.Empty<WeenieTemplateDefinition>();

            using (stream) {
                using var tr = new StreamReader(stream);
                return WeenieTemplateJson.ParseBundle(tr.ReadToEnd());
            }
        }

        static Stream? OpenEmbeddedFallback(Assembly asm) {
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("WeenieTemplates.json", StringComparison.OrdinalIgnoreCase));
            return name == null ? null : asm.GetManifestResourceStream(name);
        }

        static IReadOnlyList<WeenieTemplateDefinition> LoadJsonFilesInDirectory(string directory) {
            if (!Directory.Exists(directory)) return Array.Empty<WeenieTemplateDefinition>();
            var list = new List<WeenieTemplateDefinition>();
            foreach (var path in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)) {
                try {
                    list.AddRange(WeenieTemplateJson.ParseBundle(File.ReadAllText(path)));
                }
                catch {
                    // skip bad file
                }
            }
            return list;
        }
    }
}
