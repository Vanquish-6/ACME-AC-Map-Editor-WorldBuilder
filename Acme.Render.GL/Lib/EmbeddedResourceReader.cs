using System.Reflection;

namespace Acme.Render.GL.Lib {
    internal static class EmbeddedResourceReader {
        internal static string GetEmbeddedResource(string filename) {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Acme.Render.GL." + filename;

            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found in {assembly.GetName().Name}");
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
