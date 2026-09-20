using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// When <c>ACME_LEGACY_CONVERT_BISECT=1</c>, copies <c>client_portal.dat</c> at conversion checkpoints
/// into <c>{output}/_bisect/{stage}/client_portal.dat</c> for corruption bisect.
/// When <c>ACME_LEGACY_REGION_TRACE=1</c>, logs the converted Region 0x13000000 terrain tiling at each
/// checkpoint (DM grassland desc[1] = 1/3, retail seed = 2/4) so we can see exactly which stage reverts
/// the DM Region to the retail seed. Diagnostic only.
/// </summary>
public static class LegacyDatConversionBisect {
    public static bool Enabled =>
        string.Equals(
            System.Environment.GetEnvironmentVariable("ACME_LEGACY_CONVERT_BISECT"),
            "1",
            StringComparison.Ordinal);

    public static bool RegionTraceEnabled =>
        string.Equals(
            System.Environment.GetEnvironmentVariable("ACME_LEGACY_REGION_TRACE"),
            "1",
            StringComparison.Ordinal);

    public static void SnapshotPortal(string outputDirectory, string stageName, Action<string>? onProgress = null) {
        if (RegionTraceEnabled) {
            TraceRegion(outputDirectory, stageName, onProgress);
        }

        if (!Enabled) {
            return;
        }

        string source = Path.Combine(outputDirectory, "client_portal.dat");
        if (!File.Exists(source)) {
            return;
        }

        string stageDir = Path.Combine(outputDirectory, "_bisect", Sanitize(stageName));
        Directory.CreateDirectory(stageDir);
        string dest = Path.Combine(stageDir, "client_portal.dat");
        File.Copy(source, dest, overwrite: true);

        foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
            string companion = Path.Combine(outputDirectory, datFile);
            if (File.Exists(companion)) {
                File.Copy(companion, Path.Combine(stageDir, datFile), overwrite: true);
            }
        }

        onProgress?.Invoke($"Bisect snapshot: {stageName} -> _bisect/{Sanitize(stageName)}/");
    }

    static void TraceRegion(string outputDirectory, string stageName, Action<string>? onProgress) {
        string portal = Path.Combine(outputDirectory, "client_portal.dat");
        if (!File.Exists(portal)) {
            onProgress?.Invoke($"[REGION-TRACE] {stageName}: (no client_portal.dat yet)");
            return;
        }

        try {
            using var rw = new DefaultDatReaderWriter(
                outputDirectory,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            if (rw.TryGet<Region>(0x13000000, out var region) && region != null) {
                var descs = region.TerrainInfo.LandSurfaces.TexMerge.TerrainDesc.ToList();
                // desc[1]=Grassland, desc[4]=Marsh are the clearest DM-vs-seed discriminators.
                string Sig(int i) => i < descs.Count
                    ? $"{descs[i].TerrainTex.TexTiling}/{descs[i].TerrainTex.DetailTexTiling}/0x{descs[i].TerrainTex.DetailTextureId:X8}"
                    : "-";
                onProgress?.Invoke(
                    $"[REGION-TRACE] {stageName}: descs={descs.Count} d1(grass)={Sig(1)} d4(marsh)={Sig(4)} "
                    + $"(DM=1/3, seed=2/4)");
            }
            else {
                onProgress?.Invoke($"[REGION-TRACE] {stageName}: Region 0x13000000 NOT readable");
            }

            TraceCharGen(rw, stageName, onProgress);
        }
        catch (Exception ex) {
            onProgress?.Invoke($"[REGION-TRACE] {stageName}: ERR {ex.GetType().Name}: {ex.Message}");
        }
    }

    static void TraceCharGen(DefaultDatReaderWriter rw, string stageName, Action<string>? onProgress) {
        try {
            if (rw.TryGet<CharGen>(0x0E000002, out var charGen) && charGen != null) {
                string first = charGen.StartingAreas.Count > 0
                    ? $"'{charGen.StartingAreas[0].Name}'/0x{charGen.StartingAreas[0].Locations.FirstOrDefault()?.CellId ?? 0:X8}"
                    : "(none)";
                onProgress?.Invoke(
                    $"[CHARGEN-TRACE] {stageName}: heritage={charGen.HeritageGroups.Count} areas={charGen.StartingAreas.Count} first={first} "
                    + "(DM=8/19 'Holtburg South'/0xA9B00014, retail=13/5 'Holtburg'/0x860201AD)");
            }
            else {
                onProgress?.Invoke($"[CHARGEN-TRACE] {stageName}: CharGen 0x0E000002 NOT readable");
            }
        }
        catch (Exception ex) {
            onProgress?.Invoke($"[CHARGEN-TRACE] {stageName}: ERR {ex.GetType().Name}: {ex.Message}");
        }
    }

    static string Sanitize(string stageName) =>
        stageName.Replace(' ', '-').Replace('/', '-');
}
