namespace WorldBuilder.Shared.Lib {
    /// <summary>
    /// Maps AC client runtime <c>GetDBOType</c> cache indices to Chorizite <see cref="DBObjType"/> file types.
    /// These are not the same namespace — confusing them breaks StringTable resolution and cache lookups.
    /// </summary>
    public static class ClientDbObjTypeMap {
        public readonly record struct Entry(string Name, int ClientCacheType, int? ChoriziteFileType, string? Notes);

        public static IReadOnlyList<Entry> All { get; } = new Entry[] {
            new("Iteration", 1, 1, ""),
            new("GfxObj", 2, 2, ""),
            new("Setup", 3, 3, ""),
            new("Animation", 4, 4, ""),
            new("Palette", 10, 5, "Client cache 10, file type 5"),
            new("SurfaceTexture", 11, 6, ""),
            new("RenderSurface", 12, 7, ""),
            new("Surface", 13, 8, ""),
            new("GfxObjDegradeInfo", 26, 19, ""),
            new("Scene", 27, 20, ""),
            new("RenderTexture", 30, 23, ""),
            new("RenderMaterial", 31, 24, ""),
            new("LanguageString", 41, 32, ""),
            new("PhysicsScript", 43, 34, ""),
            new("PhysicsScriptTable", 44, 35, ""),
            new("MasterProperty", 45, 36, ""),
            new("StringTable", 37, 52, "StringInfo validates table DID as cache type 37"),
            new("RenderMesh", 67, null, "Runtime-only in client"),
            new("LandBlock", 48, 48, "Aligned"),
            new("LandBlockInfo", 49, 49, "Aligned"),
            new("EnvCell", 50, 50, "Aligned"),
        };

        public static int? TryGetChoriziteFileType(int clientCacheType) {
            foreach (var e in All) {
                if (e.ClientCacheType == clientCacheType)
                    return e.ChoriziteFileType;
            }
            return null;
        }

        public static int? TryGetClientCacheType(int choriziteFileType) {
            foreach (var e in All) {
                if (e.ChoriziteFileType == choriziteFileType)
                    return e.ClientCacheType;
            }
            return null;
        }
    }
}
