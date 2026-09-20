using System.Text;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Patches retail <see cref="StringTable"/> rows in <c>client_local_English.dat</c>
/// with DM portal <c>0x31</c> char-create text (retail layouts unchanged).
/// </summary>
public static class LegacyDatDmStringTableExporter {
    public const uint DefaultCharGenStringTableId = 0x23000002;

    public sealed record ScreenCoverage(
        string Screen,
        int Total,
        int Patched,
        int Skipped,
        int Gap,
        int Failed);

    public sealed record PatchReport(
        int Patched,
        int Skipped,
        int UnmappedDmBlobs,
        IReadOnlyList<string> Lines,
        IReadOnlyList<ScreenCoverage>? Coverage = null);

    public static int ApplyCharGenPortalStrings(
        LegacyDatReader legacySource,
        DefaultDatReaderWriter outputWriter,
        IList<string>? warnings = null) =>
        ApplyCharGenPortalStrings(legacySource, outputWriter, warnings, reportPath: null).Patched;

    public static PatchReport ApplyCharGenPortalStrings(
        LegacyDatReader legacySource,
        DefaultDatReaderWriter outputWriter,
        IList<string>? warnings,
        string? reportPath) {
        ArgumentNullException.ThrowIfNull(legacySource);
        ArgumentNullException.ThrowIfNull(outputWriter);

        var reportLines = new List<string>();
        if (outputWriter.Local() == null) {
            warnings?.Add("DM UI strings: output has no local dat; StringTable patch skipped.");
            return new PatchReport(0, 0, 0, reportLines);
        }

        IReadOnlyDictionary<uint, string> dmStrings = LegacyDatDmPortalStringReader.ReadAll(legacySource);
        if (dmStrings.Count == 0) {
            warnings?.Add("DM UI strings: no portal 0x31 strings found in legacy source.");
            return new PatchReport(0, 0, 0, reportLines);
        }

        LegacyDatDmStringTableMap map = LegacyDatDmStringTableMap.LoadEmbeddedCharGenMap();
        var tableCache = new Dictionary<uint, StringTable>();
        var mappedDmIds = new HashSet<uint>();
        int patched = 0;
        int skipped = 0;

        reportLines.Add("DM char-create StringTable patch report");
        reportLines.Add($"DM portal 0x31 blobs in source: {dmStrings.Count}");
        reportLines.Add($"Curated map entries: {map.Entries.Count}");
        reportLines.Add(string.Empty);

        foreach (var entry in map.Entries) {
            if (!string.IsNullOrWhiteSpace(entry.DmId)) {
                mappedDmIds.Add(entry.DmIdValue);
            }

            if (entry.Skip) {
                skipped++;
                string note = string.IsNullOrWhiteSpace(entry.Notes) ? "skipped by map" : entry.Notes;
                reportLines.Add($"SKIP {entry.DmId}: {note}");
                continue;
            }

            if (!dmStrings.TryGetValue(entry.DmIdValue, out string? dmText) || string.IsNullOrWhiteSpace(dmText)) {
                warnings?.Add($"DM UI strings: missing portal text for {entry.DmId}.");
                skipped++;
                reportLines.Add($"MISSING {entry.DmId}: no DM portal text");
                continue;
            }

            uint tableId = entry.RetailTableIdValue;
            if (!tableCache.TryGetValue(tableId, out StringTable? table)) {
                if (!outputWriter.TryGet(tableId, out table) || table?.Strings == null) {
                    warnings?.Add($"DM UI strings: retail StringTable 0x{tableId:X8} missing in output local dat.");
                    skipped++;
                    reportLines.Add($"FAIL {entry.DmId}: table 0x{tableId:X8} missing");
                    continue;
                }

                tableCache[tableId] = table;
            }

            if (!TryResolveRetailKey(table!, entry, dmText, out uint retailKey, out string resolveMethod)) {
                warnings?.Add($"DM UI strings: could not resolve retail key for {entry.DmId}.");
                skipped++;
                reportLines.Add($"FAIL {entry.DmId}: no retail row ({Truncate(dmText, 60)})");
                continue;
            }

            if (!table!.Strings.TryGetValue(retailKey, out StringTableString? row) || row == null) {
                warnings?.Add($"DM UI strings: StringTable 0x{tableId:X8} has no key {retailKey} for {entry.DmId}.");
                skipped++;
                reportLines.Add($"FAIL {entry.DmId}: key {retailKey} missing in 0x{tableId:X8}");
                continue;
            }

            string? before = FormatRow(row);
            ApplyDmTextToRow(row, dmText);
            string? after = FormatRow(row);
            patched++;
            reportLines.Add(
                $"OK   {entry.DmId} -> 0x{tableId:X8}:{retailKey} ({resolveMethod})");
            reportLines.Add($"     before: {Truncate(before, 100)}");
            reportLines.Add($"     after:  {Truncate(after, 100)}");
        }

        foreach (var (dmId, dmText) in dmStrings.OrderBy(static pair => pair.Key)) {
            if (mappedDmIds.Contains(dmId)) {
                continue;
            }

            if (dmId is 0x31000020 or 0x31000022) {
                reportLines.Add($"SKIP 0x{dmId:X8}: credits / legal (not char-gen UI)");
                continue;
            }

            uint tableId = DefaultCharGenStringTableId;
            if (!tableCache.TryGetValue(tableId, out StringTable? table)) {
                if (!outputWriter.TryGet(tableId, out table) || table?.Strings == null) {
                    continue;
                }

                tableCache[tableId] = table;
            }

            var autoEntry = new LegacyDatDmStringTableMapEntry { DmId = $"0x{dmId:X8}" };
            if (!TryResolveRetailKey(table!, autoEntry, dmText, out uint retailKey, out string resolveMethod)) {
                reportLines.Add($"UNMAPPED 0x{dmId:X8}: {Truncate(dmText, 80)}");
                continue;
            }

            if (!table!.Strings.TryGetValue(retailKey, out StringTableString? row) || row == null) {
                reportLines.Add($"UNMAPPED 0x{dmId:X8}: resolved key {retailKey} but row missing");
                continue;
            }

            ApplyDmTextToRow(row, dmText);
            patched++;
            reportLines.Add($"AUTO 0x{dmId:X8} -> 0x{tableId:X8}:{retailKey} ({resolveMethod})");
        }

        foreach (var (tableId, table) in tableCache) {
            table.Id = tableId;
            if (!outputWriter.TrySave(table)) {
                warnings?.Add($"DM UI strings: failed to write StringTable 0x{tableId:X8}.");
            }
        }

        int unmapped = reportLines.Count(static line => line.StartsWith("UNMAPPED", StringComparison.Ordinal));
        if (patched == 0) {
            warnings?.Add("DM UI strings: no StringTable rows were patched.");
        }

        reportLines.Add(string.Empty);
        reportLines.Add($"Patched: {patched}, skipped/failed: {skipped}, unmapped DM blobs: {unmapped}");
        reportLines.Add("Note: retail LayoutDesc screens are unchanged; only help-panel text in client_local_English.dat is replaced.");

        if (!string.IsNullOrWhiteSpace(reportPath)) {
            File.WriteAllLines(reportPath, reportLines, Encoding.UTF8);
        }

        return new PatchReport(patched, skipped, unmapped, reportLines);
    }

    public static PatchReport ApplyConnectionScreenStrings(
        DefaultDatReaderWriter outputWriter,
        IList<string>? warnings,
        string? reportPath) =>
        ApplyScreenStrings(
            outputWriter,
            warnings,
            reportPath,
            LegacyDatDmStringTableMapNames.Connection);

    public static PatchReport ApplyInGameScreenStrings(
        DefaultDatReaderWriter outputWriter,
        IList<string>? warnings,
        string? reportPath) =>
        ApplyScreenStrings(
            outputWriter,
            warnings,
            reportPath,
            LegacyDatDmStringTableMapNames.InGameExe);

    public static PatchReport ApplyScreenStrings(
        DefaultDatReaderWriter outputWriter,
        IList<string>? warnings,
        string? reportPath,
        string mapName,
        LegacyDatReader? legacySource = null) {
        ArgumentNullException.ThrowIfNull(outputWriter);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);

        var reportLines = new List<string> {
            string.Empty,
            $"DM UI screen StringTable patch report ({mapName})",
        };

        if (outputWriter.Local() == null) {
            warnings?.Add($"DM UI screen strings ({mapName}): output has no local dat; patch skipped.");
            return new PatchReport(0, 0, 0, reportLines, Array.Empty<ScreenCoverage>());
        }

        LegacyDatDmStringTableMap map = LegacyDatDmStringTableMap.LoadEmbeddedMap(mapName);
        var tableCache = new Dictionary<uint, StringTable>();
        var coverage = new Dictionary<string, CoverageAccumulator>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<uint, string>? dmPortalStrings = null;
        int patched = 0;
        int skipped = 0;

        foreach (var entry in map.Entries) {
            string screen = string.IsNullOrWhiteSpace(entry.Screen) ? mapName : entry.Screen.Trim();
            if (!coverage.TryGetValue(screen, out CoverageAccumulator? screenCoverage)) {
                screenCoverage = new CoverageAccumulator();
                coverage[screen] = screenCoverage;
            }

            screenCoverage.Total++;
            string label = BuildEntryLabel(entry);

            if (entry.Gap) {
                skipped++;
                screenCoverage.Gap++;
                reportLines.Add($"GAP  [{screen}] {label}: {entry.Notes ?? "no DM source string confirmed"}");
                continue;
            }

            if (entry.Skip) {
                skipped++;
                screenCoverage.Skipped++;
                reportLines.Add($"SKIP [{screen}] {label}: {entry.Notes ?? "skipped by map"}");
                continue;
            }

            string? dmText = entry.DmText;
            if (string.IsNullOrWhiteSpace(dmText) && !string.IsNullOrWhiteSpace(entry.DmId)) {
                if (legacySource == null) {
                    warnings?.Add($"DM UI screen strings ({mapName}): entry {label} requires legacy source text.");
                    skipped++;
                    screenCoverage.Failed++;
                    reportLines.Add($"FAIL [{screen}] {label}: legacy source text required");
                    continue;
                }

                dmPortalStrings ??= LegacyDatDmPortalStringReader.ReadAll(legacySource);
                if (!dmPortalStrings.TryGetValue(entry.DmIdValue, out dmText) || string.IsNullOrWhiteSpace(dmText)) {
                    warnings?.Add($"DM UI screen strings ({mapName}): missing DM source text for {label}.");
                    skipped++;
                    screenCoverage.Failed++;
                    reportLines.Add($"FAIL [{screen}] {label}: missing DM source text");
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(dmText)) {
                warnings?.Add($"DM UI screen strings ({mapName}): missing dmText for {label}.");
                skipped++;
                screenCoverage.Failed++;
                reportLines.Add($"FAIL [{screen}] {label}: no dmText in map");
                continue;
            }

            uint tableId = entry.RetailTableIdValue;
            if (!tableCache.TryGetValue(tableId, out StringTable? table)) {
                if (!outputWriter.TryGet(tableId, out table) || table?.Strings == null) {
                    warnings?.Add($"DM UI screen strings ({mapName}): StringTable 0x{tableId:X8} missing.");
                    skipped++;
                    screenCoverage.Failed++;
                    reportLines.Add($"FAIL [{screen}] {label}: table 0x{tableId:X8} missing");
                    continue;
                }

                tableCache[tableId] = table;
            }

            if (!TryResolveRetailKey(table!, entry, dmText, out uint retailKey, out string resolveMethod)) {
                warnings?.Add($"DM UI screen strings ({mapName}): could not resolve retail key for {label}.");
                skipped++;
                screenCoverage.Failed++;
                reportLines.Add($"FAIL [{screen}] {label}: no retail row");
                continue;
            }

            if (!table!.Strings.TryGetValue(retailKey, out StringTableString? row) || row == null) {
                warnings?.Add($"DM UI screen strings ({mapName}): no row {retailKey} in 0x{tableId:X8} for {label}.");
                skipped++;
                screenCoverage.Failed++;
                reportLines.Add($"FAIL [{screen}] {label}: key {retailKey} missing");
                continue;
            }

            string? before = FormatRow(row);
            ApplyDmTextToRow(row, dmText);
            patched++;
            screenCoverage.Patched++;
            reportLines.Add($"OK   [{screen}] {label} -> 0x{tableId:X8}:{retailKey} ({resolveMethod})");
            reportLines.Add($"     before: {Truncate(before, 100)}");
            reportLines.Add($"     after:  {Truncate(FormatRow(row), 100)}");
        }

        foreach (var (tableId, table) in tableCache) {
            table.Id = tableId;
            if (!outputWriter.TrySave(table)) {
                warnings?.Add($"DM UI screen strings ({mapName}): failed to write StringTable 0x{tableId:X8}.");
            }
        }

        var coverageRows = coverage
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Value.ToCoverage(pair.Key))
            .ToArray();

        reportLines.Add(string.Empty);
        reportLines.Add($"Screen map '{mapName}' patched: {patched}, skipped/failed/gap: {skipped}");
        reportLines.Add("Coverage by screen:");
        foreach (ScreenCoverage row in coverageRows) {
            double percent = row.Total == 0 ? 0 : row.Patched * 100.0 / row.Total;
            reportLines.Add(
                $"  {row.Screen}: patched {row.Patched}/{row.Total} ({percent:0.#}%), skipped {row.Skipped}, gap {row.Gap}, failed {row.Failed}");
        }

        if (!string.IsNullOrWhiteSpace(reportPath)) {
            File.AppendAllLines(reportPath, reportLines, Encoding.UTF8);
        }

        if (patched == 0) {
            warnings?.Add($"DM UI screen strings ({mapName}): no StringTable rows were patched.");
        }

        return new PatchReport(patched, skipped, 0, reportLines, coverageRows);
    }

    private static bool TryResolveRetailKey(
        StringTable table,
        LegacyDatDmStringTableMapEntry entry,
        string dmText,
        out uint retailKey,
        out string resolveMethod) {
        if (!string.IsNullOrWhiteSpace(entry.RetailStringId)) {
            retailKey = AcClientStringHash.Compute(entry.RetailStringId);
            resolveMethod = entry.RetailStringId;
            return true;
        }

        if (entry.RetailKey is uint explicitKey) {
            retailKey = explicitKey;
            resolveMethod = $"retailKey {explicitKey}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.SearchContains)) {
            string needle = entry.SearchContains;
            foreach (var (key, row) in table.Strings) {
                string? existing = FormatRow(row);
                if (existing != null && existing.Contains(needle, StringComparison.OrdinalIgnoreCase)) {
                    retailKey = key;
                    resolveMethod = $"searchContains";
                    return true;
                }
            }
        }

        string snippet = dmText.Length <= 48 ? dmText : dmText[..48];
        foreach (var (key, row) in table.Strings) {
            string? existing = FormatRow(row);
            if (existing != null && existing.Contains(snippet, StringComparison.OrdinalIgnoreCase)) {
                retailKey = key;
                resolveMethod = "fuzzy snippet";
                return true;
            }
        }

        retailKey = 0;
        resolveMethod = string.Empty;
        return false;
    }

    internal static void ApplyDmTextToRowForTesting(StringTableString row, string dmText) =>
        ApplyDmTextToRow(row, dmText);

    private static void ApplyDmTextToRow(StringTableString row, string dmText) {
        if (row.Strings == null || row.Strings.Count == 0) {
            return;
        }

        IReadOnlyList<string> lines = SplitDmTextIntoLines(dmText);
        if (TryApplyDmTextPreservingTemplateSlots(row, lines)) {
            return;
        }

        for (int i = 0; i < row.Strings.Count; i++) {
            if (row.Strings[i] == null) {
                continue;
            }

            row.Strings[i] = i < lines.Count ? lines[i] : string.Empty;
        }
    }

    private static bool TryApplyDmTextPreservingTemplateSlots(StringTableString row, IReadOnlyList<string> lines) {
        if (row.Strings == null || row.Strings.Count == 0) {
            return false;
        }

        var nonEmptyIndexes = new List<int>(row.Strings.Count);
        bool hasEmptySlot = false;
        for (int i = 0; i < row.Strings.Count; i++) {
            string? value = row.Strings[i];
            if (string.IsNullOrEmpty(value)) {
                hasEmptySlot = true;
                continue;
            }

            nonEmptyIndexes.Add(i);
        }

        if (!hasEmptySlot || nonEmptyIndexes.Count == 0 || lines.Count > nonEmptyIndexes.Count) {
            return false;
        }

        int lineIndex = 0;
        foreach (int slotIndex in nonEmptyIndexes) {
            row.Strings[slotIndex] = lineIndex < lines.Count ? lines[lineIndex++] : string.Empty;
        }

        return true;
    }

    private static IReadOnlyList<string> SplitDmTextIntoLines(string dmText) {
        string normalized = dmText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        if (blocks.Length > 1) {
            return blocks;
        }

        return normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    private static string? FormatRow(StringTableString row) {
        if (row.Strings == null || row.Strings.Count == 0) {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var line in row.Strings) {
            if (string.IsNullOrWhiteSpace(line)) {
                continue;
            }

            if (sb.Length > 0) {
                sb.Append('\n');
            }

            sb.Append(line);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string Truncate(string? value, int max) {
        if (string.IsNullOrWhiteSpace(value)) {
            return "(empty)";
        }

        string flat = value.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
    private static string BuildEntryLabel(LegacyDatDmStringTableMapEntry entry) {
        if (!string.IsNullOrWhiteSpace(entry.DmId)) {
            return entry.DmId;
        }

        if (entry.RetailKey is uint key) {
            return $"retail:{key}";
        }

        if (!string.IsNullOrWhiteSpace(entry.SearchContains)) {
            return entry.SearchContains;
        }

        if (!string.IsNullOrWhiteSpace(entry.DmText)) {
            return Truncate(entry.DmText, 40);
        }

        return "entry";
    }

    private sealed class CoverageAccumulator {
        public int Total;
        public int Patched;
        public int Skipped;
        public int Gap;
        public int Failed;

        public ScreenCoverage ToCoverage(string screen) =>
            new(screen, Total, Patched, Skipped, Gap, Failed);
    }
}
