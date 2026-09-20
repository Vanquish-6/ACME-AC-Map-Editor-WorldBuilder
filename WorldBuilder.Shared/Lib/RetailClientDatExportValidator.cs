using Acme.Dat;
using System.Buffers.Binary;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Validates a retail-format export folder against <see cref="RetailClientDatSpec"/>.
/// </summary>
public static class RetailClientDatExportValidator {
    const int HeaderOffset = 0x140;
    const int ExpectedMagic = 0x5442;
    const uint RegionId = 0x13000000u;
    static readonly byte[] EmptyTransactionPrefix = [0x00, 0x50, 0x4C, 0x00];

    public sealed class Options {
        public LegacyDatExportPolicy? ExportPolicy { get; init; }
        public string? RetailSeedDirectory { get; init; }
        public bool RequireOverlayPortalRoot { get; init; }
        public bool RequireRetailSpellTableIds { get; init; } = true;
        public bool ValidateCharGenShell { get; init; } = true;
        public bool ValidateLocalConnectLayout { get; init; } = true;
        /// <summary>When set with a Full DM policy, portal client-metadata IDs must match retail seed bytes.</summary>
        public bool RequireRetailPortalClientMetadata { get; init; } = true;
    }

    public static RetailClientDatValidationReport Validate(
        string exportDirectory,
        Options? options = null) {
        options ??= new Options();
        var errors = new List<string>();
        var warnings = new List<string>();
        var info = new List<string>();

        exportDirectory = Path.GetFullPath(exportDirectory);
        if (!Directory.Exists(exportDirectory)) {
            errors.Add($"Export directory does not exist: {exportDirectory}");
            return BuildReport(errors, warnings, info);
        }

        RetailClientDatSpec spec;
        try {
            spec = RetailClientDatSpec.LoadEmbedded();
        }
        catch (Exception ex) {
            errors.Add($"Could not load retail_client_dat_spec.json: {ex.Message}");
            return BuildReport(errors, warnings, info);
        }

        info.Add($"Spec v{spec.Version}: {spec.Title}");

        foreach (string datFile in spec.RetailFiles) {
            if (!File.Exists(Path.Combine(exportDirectory, datFile))) {
                errors.Add($"Missing required file: {datFile}");
            }
        }

        if (errors.Count > 0) {
            return BuildReport(errors, warnings, info);
        }

        if (options.ExportPolicy != null) {
            foreach (string line in LegacyDatClientExportValidator.Validate(exportDirectory, options.ExportPolicy)) {
                errors.Add($"[client-export] {line}");
            }
        }

        foreach (string datFile in new[] { "client_cell_1.dat", "client_portal.dat", "client_local_English.dat" }) {
            ValidateBTreeReadCompatibility(exportDirectory, datFile, errors);
        }

        ValidateHeaders(exportDirectory, spec, options, errors, warnings, info);
        ValidateTransactionJournals(exportDirectory, errors, warnings);
        ValidateDuplicatePortalIds(exportDirectory, errors, info);
        ValidateSpellTables(exportDirectory, spec, options, errors, warnings, info);

        if (options.RetailSeedDirectory != null
            && options.ExportPolicy != null
            && options.RequireRetailPortalClientMetadata) {
            ValidatePortalClientMetadata(exportDirectory, options.RetailSeedDirectory, options.ExportPolicy, errors, info);
        }

        try {
            using var reader = new DefaultDatReaderWriter(
                exportDirectory,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);

            ValidateRegion(reader, errors);
            ValidateCharGen(reader, options, errors, warnings);
            ValidateLocalUi(reader, spec, options, errors);
            ValidateCatalogPastEof(exportDirectory, reader, errors, warnings);
        }
        catch (Exception ex) {
            errors.Add($"Could not open export DATs: {ex.Message}");
        }

        return BuildReport(errors, warnings, info);
    }

    static void ValidateBTreeReadCompatibility(string exportDirectory, string datFile, List<string> errors) {
        string path = Path.Combine(exportDirectory, datFile);
        var issues = DatExportFixer.InspectReadCompatibility(path);
        if (!issues.HasIssues) {
            return;
        }

        var parts = new List<string>();
        if (issues.NodesWithInvalidFileCount > 0) {
            parts.Add($"{issues.NodesWithInvalidFileCount} invalid file count node(s)");
        }

        if (issues.NodesWithBranch0Sentinel > 0) {
            parts.Add($"{issues.NodesWithBranch0Sentinel} branch[0]=0xCDCDCDCD node(s)");
        }

        if (issues.ActiveBranchSentinels > 0) {
            parts.Add($"{issues.ActiveBranchSentinels} active 0xCDCDCDCD branch slot(s)");
        }

        if (issues.ActiveBranchInvalidOffsets > 0) {
            parts.Add($"{issues.ActiveBranchInvalidOffsets} branch slot(s) past EOF");
        }

        errors.Add($"{datFile}: B-tree read-compatibility — {string.Join(", ", parts)}");
    }

    static void ValidateHeaders(
        string exportDirectory,
        RetailClientDatSpec spec,
        Options options,
        List<string> errors,
        List<string> warnings,
        List<string> info) {
        var fingerprints = spec.ReferenceSeed?.Fingerprints;
        foreach (string datFile in new[] { "client_portal.dat", "client_cell_1.dat", "client_local_English.dat" }) {
            string path = Path.Combine(exportDirectory, datFile);
            if (!TryReadHeader(path, out int magic, out int blockSize, out int root)) {
                warnings.Add($"{datFile}: could not read header @0x140");
                continue;
            }

            if (magic != ExpectedMagic) {
                warnings.Add($"{datFile}: header magic 0x{magic:X4} (expected 0x{ExpectedMagic:X4})");
            }

            info.Add($"{datFile}: block={blockSize} root=0x{root:X}");

            if (fingerprints != null
                && fingerprints.TryGetValue(datFile, out RetailClientDatFileFingerprint? fingerprint)
                && !string.IsNullOrWhiteSpace(fingerprint.BtreeRoot)) {
                uint expectedRoot = RetailClientDatSpec.ParseHexId(fingerprint.BtreeRoot);
                if (datFile == "client_portal.dat" && root != (int)expectedRoot) {
                    string message =
                        $"{datFile}: portal B-tree root 0x{root:X} differs from reference seed {fingerprint.BtreeRoot} (overlay exports should match)";
                    if (options.RequireOverlayPortalRoot) {
                        errors.Add(message);
                    }
                    else {
                        warnings.Add(message);
                    }
                }
            }
        }
    }

    static void ValidateTransactionJournals(
        string exportDirectory,
        List<string> errors,
        List<string> warnings) {
        foreach (string datFile in specRetailFiles) {
            string path = Path.Combine(exportDirectory, datFile);
            if (!File.Exists(path)) {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x100 + EmptyTransactionPrefix.Length) {
                errors.Add($"{datFile}: file too small for transaction journal @0x100");
                continue;
            }

            if (!bytes.AsSpan(0x100, EmptyTransactionPrefix.Length).SequenceEqual(EmptyTransactionPrefix)) {
                string message =
                    $"{datFile}: transaction journal @0x100 is not native no-op (0x00504C00); BTree__LoadTree / replay may crash client init";
                if (datFile is "client_portal.dat" or "client_cell_1.dat" or "client_local_English.dat") {
                    errors.Add(message);
                }
                else {
                    warnings.Add(message);
                }
            }
        }
    }

    static readonly string[] specRetailFiles = [
        "client_cell_1.dat",
        "client_portal.dat",
        "client_highres.dat",
        "client_local_English.dat",
    ];

    static void ValidatePortalClientMetadata(
        string exportDirectory,
        string retailSeedDirectory,
        LegacyDatExportPolicy policy,
        List<string> errors,
        List<string> info) {
        if (policy.Portal != LegacyDatPortalCatalogPolicy.FullDmCatalog) {
            return;
        }

        using var seed = new DefaultDatReaderWriter(
            retailSeedDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);
        using var output = new DefaultDatReaderWriter(
            exportDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        var metadataIds = LegacyDatFullDmPortalKeeper.CollectRetailPortalClientMetadataIds(seed);
        int missing = 0;
        int notRetailBytes = 0;
        int unreadable = 0;

        foreach (uint id in metadataIds) {
            if (!output.ContainsFile(DatArchive.Portal, id)) {
                missing++;
                if (missing <= 8) {
                    errors.Add($"client_portal.dat: missing client-metadata 0x{id:X8} (portal_init / GetByEnum)");
                }

                continue;
            }

            if (!seed.TryGetFileBytes(DatArchive.Portal, id, out byte[]? retailBytes) || retailBytes == null) {
                continue;
            }

            if (!output.TryGetFileBytes(DatArchive.Portal, id, out byte[]? exportBytes) || exportBytes == null) {
                unreadable++;
                if (unreadable <= 8) {
                    errors.Add($"client_portal.dat: client-metadata 0x{id:X8} payload not readable");
                }

                continue;
            }

            if (!retailBytes.AsSpan().SequenceEqual(exportBytes)) {
                notRetailBytes++;
                if (notRetailBytes <= 8) {
                    errors.Add(
                        $"client_portal.dat: client-metadata 0x{id:X8} is not retail bytes (DM payload at enum-map ID crashes init)");
                }
            }
        }

        info.Add(
            $"client_portal.dat: client-metadata ids={metadataIds.Count:N0} missing={missing:N0} non_retail={notRetailBytes:N0} unreadable={unreadable:N0}");

        if (missing > 8) {
            errors.Add($"client_portal.dat: +{missing - 8} more missing client-metadata id(s)");
        }

        if (notRetailBytes > 8) {
            errors.Add($"client_portal.dat: +{notRetailBytes - 8} more non-retail client-metadata payload(s)");
        }
    }

    static void ValidateDuplicatePortalIds(string exportDirectory, List<string> errors, List<string> info) {
        using var reader = new DefaultDatReaderWriter(
            exportDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        var duplicateGroups = reader.Portal().Catalog().Tree
            .GroupBy(file => file.Id)
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ToList();

        int portalEntries = reader.ListFileIds(DatArchive.Portal).Count();
        info.Add($"client_portal.dat: catalog entries={portalEntries:N0}");

        if (duplicateGroups.Count == 0) {
            info.Add("client_portal.dat: duplicate catalog IDs=0");
            return;
        }

        errors.Add($"client_portal.dat: {duplicateGroups.Count:N0} duplicate catalog id(s) (spec requires exactly one entry per ID)");
        foreach (var group in duplicateGroups.Take(8)) {
            errors.Add($"  duplicate 0x{group.Key:X8} count={group.Count()}");
        }

        if (duplicateGroups.Count > 8) {
            errors.Add($"  ... +{duplicateGroups.Count - 8} more duplicate id(s)");
        }
    }

    static void ValidateSpellTables(
        string exportDirectory,
        RetailClientDatSpec spec,
        Options options,
        List<string> errors,
        List<string> warnings,
        List<string> info) {
        using var reader = new DefaultDatReaderWriter(
            exportDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        var spellIds = reader.Portal().Catalog().Tree
            .Where(file => (file.Id >> 24) == 0x0E)
            .Select(file => file.Id)
            .ToList();

        int unique = spellIds.Distinct().Count();
        info.Add($"client_portal.dat: 0x0E table entries={spellIds.Count} unique={unique}");

        if (spellIds.Count != unique) {
            errors.Add("client_portal.dat: duplicate 0x0E spell/skill/vital/CharGen table catalog entries");
        }

        if (!options.RequireRetailSpellTableIds || spec.IdBands?.PortalSpellTables == null) {
            return;
        }

        var expected = spec.IdBands.PortalSpellTables
            .Select(RetailClientDatSpec.ParseHexId)
            .ToHashSet();

        foreach (uint id in expected) {
            if (!reader.ContainsFile(DatArchive.Portal, id)) {
                warnings.Add($"client_portal.dat: missing retail spell-table id 0x{id:X8}");
            }
        }

        foreach (uint id in spellIds.Distinct()) {
            if (!expected.Contains(id)) {
                warnings.Add($"client_portal.dat: unexpected 0x0E table id 0x{id:X8} (not in retail seed spell-table set)");
            }
        }

        if (spec.ReferenceSeed?.Fingerprints.TryGetValue("client_portal.dat", out var fp) == true
            && fp.SpellTables0x0E is int expectedCount
            && unique != expectedCount) {
            warnings.Add($"client_portal.dat: unique 0x0E tables={unique} (reference seed={expectedCount})");
        }
    }

    static void ValidateRegion(DefaultDatReaderWriter reader, List<string> errors) {
        if (!reader.TryGet<Region>(RegionId, out _)) {
            errors.Add($"client_portal.dat: Region 0x{RegionId:X8} is not readable (portal_init / enter_world)");
        }
    }

    static void ValidateCharGen(
        DefaultDatReaderWriter reader,
        Options options,
        List<string> errors,
        List<string> warnings) {
        uint charGenId = LegacyDatRetailClientShellCollector.CharGenId;
        if (!reader.TryGet(charGenId, out CharGen? charGen) || charGen == null) {
            errors.Add($"client_portal.dat: CharGen 0x{charGenId:X8} is not readable (char_create)");
            return;
        }

        if (charGen.HeritageGroups.Count == 0) {
            warnings.Add("CharGen: no heritage groups");
        }

        if (charGen.StartingAreas.Count == 0) {
            string message = "CharGen: no starting areas (client crashes at character select / login).";
            if (options.ExportPolicy != null
                && LegacyDatMergeRuleResolver.ShouldRequireCharGenStartingAreas(options.ExportPolicy)) {
                errors.Add(message);
            }
            else {
                warnings.Add(message);
            }
        }

        if (!options.ValidateCharGenShell) {
            return;
        }

        int missingSetup = 0;
        int unreadableSetup = 0;
        foreach (var (_, group) in charGen.HeritageGroups) {
            foreach (uint setupId in new[] { group.SetupId, group.EnvironmentSetupId }) {
                if (setupId == 0) {
                    continue;
                }

                if (!reader.ContainsFile(DatArchive.Portal, setupId)) {
                    missingSetup++;
                    continue;
                }

                if (!reader.TryGet<Setup>(setupId, out Setup? setup) || setup == null) {
                    unreadableSetup++;
                }
            }
        }

        if (missingSetup > 0) {
            errors.Add($"CharGen shell: {missingSetup} heritage Setup/EnvironmentSetup id(s) missing from portal");
        }

        if (unreadableSetup > 0) {
            errors.Add($"CharGen shell: {unreadableSetup} heritage Setup id(s) not readable");
        }

        ValidateCharGenSpawnReadiness(reader, charGen, errors, warnings);
    }

    static void ValidateCharGenSpawnReadiness(
        DefaultDatReaderWriter reader,
        CharGen charGen,
        List<string> errors,
        List<string> warnings) {
        int plausible = 0;
        int missingLandBlock = 0;
        int missingOutdoorLbi = 0;
        int missingDungeonLbi = 0;
        int missingDungeonEnvCell = 0;

        foreach (var area in charGen.StartingAreas) {
            foreach (var location in area.Locations) {
                if (!LegacyDatCharGenMapper.IsPlausibleSpawnCellId(location.CellId)) {
                    continue;
                }

                plausible++;

                uint landBlockId = LegacyDatCharGenMapper.LandBlockFileIdForCell(location.CellId);
                uint lbiId = LegacyDatCharGenMapper.LandBlockInfoFileIdForCell(location.CellId);
                if (!reader.ContainsFile(DatArchive.Cell, landBlockId)
                    && !reader.TryGet<LandBlock>(landBlockId, out _)) {
                    missingLandBlock++;
                }

                ushort cellLow = (ushort)(location.CellId & 0xFFFF);
                bool isDungeonSpawn = cellLow is not (>= 0x0001 and <= 0x0040) and not (0xFFFE or 0xFFFF);

                if (!reader.ContainsFile(DatArchive.Cell, lbiId)
                    && !reader.TryGet<LandBlockInfo>(lbiId, out _)) {
                    // LandBlockInfo is optional for outdoor landblocks (only present when the block
                    // carries statics/buildings); DM wilderness spawn points legitimately lack it.
                    // Dungeon spawns need the LBI for the interior cell table.
                    if (isDungeonSpawn) {
                        missingDungeonLbi++;
                    }
                    else {
                        missingOutdoorLbi++;
                    }
                }

                if (isDungeonSpawn
                    && !reader.ContainsFile(DatArchive.Cell, location.CellId)
                    && !reader.TryGet<EnvCell>(location.CellId, out _)) {
                    missingDungeonEnvCell++;
                }
            }
        }

        if (plausible == 0 && charGen.StartingAreas.Count > 0) {
            warnings.Add("CharGen spawns: no plausible starting-location cell ids were found.");
            return;
        }

        if (missingLandBlock > 0) {
            errors.Add($"CharGen spawns: {missingLandBlock} starting-location landblock(s) missing from client_cell_1.dat (enter_world)");
        }

        if (missingDungeonLbi > 0) {
            errors.Add($"CharGen spawns: {missingDungeonLbi} dungeon starting-location LandBlockInfo object(s) missing from client_cell_1.dat (enter_world)");
        }

        if (missingOutdoorLbi > 0) {
            warnings.Add($"CharGen spawns: {missingOutdoorLbi} outdoor starting-location LandBlockInfo object(s) absent from client_cell_1.dat (optional for blocks without statics).");
        }

        if (missingDungeonEnvCell > 0) {
            warnings.Add($"CharGen spawns: {missingDungeonEnvCell} dungeon starting EnvCell object(s) are not directly readable from client_cell_1.dat.");
        }
    }

    static void ValidateLocalUi(
        DefaultDatReaderWriter reader,
        RetailClientDatSpec spec,
        Options options,
        List<string> errors) {
        if (!options.ValidateLocalConnectLayout) {
            return;
        }

        uint connectLayoutId = LegacyDatClientExportValidator.ConnectLayoutId;
        if (!reader.TryGet<LayoutDesc>(connectLayoutId, out _)) {
            errors.Add($"client_local_English.dat: connect layout 0x{connectLayoutId:X8} is not readable (local_init)");
        }

        if (spec.IdBands?.LocalUi != null
            && spec.IdBands.LocalUi.TryGetValue("0x21000039", out _)
            && !reader.TryGet<LayoutDesc>(0x21000039, out _)) {
            errors.Add("client_local_English.dat: char create layout 0x21000039 is not readable (char_create UI)");
        }
    }

    static void ValidateCatalogPastEof(
        string exportDirectory,
        DefaultDatReaderWriter reader,
        List<string> errors,
        List<string> warnings) {
        ValidateDatabasePastEof(
            reader.Portal(),
            Path.Combine(exportDirectory, "client_portal.dat"),
            errors);
        ValidateDatabasePastEof(
            reader,
            Path.Combine(exportDirectory, "client_cell_1.dat"),
            errors);
        ValidateDatabasePastEof(
            reader,
            Path.Combine(exportDirectory, "client_local_English.dat"),
            errors);
    }

    static void ValidateDatabasePastEof(
        IDatReaderWriter database,
        string path,
        List<string> target) {
        if (!File.Exists(path)) {
            return;
        }

        long length = new FileInfo(path).Length;
        int pastEof = 0;
        int readFail = 0;
        foreach (var file in database.Catalog().Tree) {
            if (file.Offset <= 0 || file.Offset + file.Size > length) {
                pastEof++;
            }

            if (!database.Catalog().TryGetFileBytes(file.Id, out _, false)) {
                readFail++;
            }
        }

        if (pastEof > 0) {
            target.Add($"{Path.GetFileName(path)}: {pastEof:N0} catalog entr(y/ies) point past EOF");
        }

        if (readFail > 0) {
            target.Add($"{Path.GetFileName(path)}: {readFail:N0} catalog entr(y/ies) failed payload read");
        }
    }

    static bool TryReadHeader(string path, out int magic, out int blockSize, out int root) {
        magic = 0;
        blockSize = 0;
        root = 0;
        if (!File.Exists(path)) {
            return false;
        }

        using var fs = File.OpenRead(path);
        if (fs.Length < HeaderOffset + 36) {
            return false;
        }

        var buf = new byte[36];
        fs.Position = HeaderOffset;
        fs.ReadExactly(buf);
        magic = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        blockSize = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
        root = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(32, 4));
        return true;
    }

    static RetailClientDatValidationReport BuildReport(
        List<string> errors,
        List<string> warnings,
        List<string> info) =>
        new() {
            Errors = errors,
            Warnings = warnings,
            Info = info,
        };
}
