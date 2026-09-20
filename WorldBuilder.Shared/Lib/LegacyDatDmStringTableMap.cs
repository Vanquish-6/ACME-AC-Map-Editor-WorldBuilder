using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldBuilder.Shared.Lib;

public static class LegacyDatDmStringTableMapNames {
    public const string CharGenPortal = "char_gen_portal";
    public const string Connection = "connection";
    public const string InGameExe = "in_game_exe";
    public const string PreGameExe = "pre_game_exe";
}

public sealed class LegacyDatDmStringTableMap {
    public int Version { get; init; } = 1;
    public string? Name { get; init; }
    public string DefaultTableId { get; init; } = "0x23000002";
    public IReadOnlyList<LegacyDatDmStringTableMapEntry> Entries { get; init; } = Array.Empty<LegacyDatDmStringTableMapEntry>();

    public static LegacyDatDmStringTableMap LoadEmbeddedCharGenMap() =>
        LoadEmbedded("WorldBuilder.Shared.Data.dm_0x31_to_retail_string_map.json");

    public static LegacyDatDmStringTableMap LoadEmbeddedConnectionMap() =>
        LoadEmbedded("WorldBuilder.Shared.Data.dm_connection_to_retail_string_map.json");

    public static LegacyDatDmStringTableMap LoadEmbeddedMap(string mapName) => mapName switch {
        LegacyDatDmStringTableMapNames.CharGenPortal => LoadEmbeddedCharGenMap(),
        LegacyDatDmStringTableMapNames.Connection => LoadEmbeddedConnectionMap(),
        LegacyDatDmStringTableMapNames.InGameExe => LoadEmbedded("WorldBuilder.Shared.Data.dm_ingame_to_retail_string_map.json"),
        LegacyDatDmStringTableMapNames.PreGameExe => LoadEmbedded("WorldBuilder.Shared.Data.dm_exe_to_retail_string_map.json"),
        _ => throw new ArgumentOutOfRangeException(nameof(mapName), mapName, "Unknown DM string table map name."),
    };

    private static LegacyDatDmStringTableMap LoadEmbedded(string resourceName) {
        var assembly = typeof(LegacyDatDmStringTableMap).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) {
            throw new InvalidOperationException($"Missing embedded resource {resourceName}.");
        }

        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();
        var map = JsonSerializer.Deserialize<LegacyDatDmStringTableMap>(json, JsonOptions);
        if (map == null || map.Entries.Count == 0) {
            throw new InvalidOperationException($"DM string table map is empty or invalid ({resourceName}).");
        }

        return map;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class LegacyDatDmStringTableMapEntry {
    public string DmId { get; init; } = string.Empty;
    public string? Screen { get; init; }
    public string? RetailTableId { get; init; }
    public string? RetailStringId { get; init; }
    public uint? RetailKey { get; init; }
    public string? SearchContains { get; init; }
    public bool Skip { get; init; }
    public bool Gap { get; init; }
    public string? Notes { get; init; }
    public string? DmText { get; init; }

    [JsonIgnore]
    public uint DmIdValue => ParseHex(DmId);

    [JsonIgnore]
    public uint RetailTableIdValue => ParseHex(RetailTableId ?? "0x23000002");

    private static uint ParseHex(string value) {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
            value = value[2..];
        }

        return uint.Parse(value, System.Globalization.NumberStyles.HexNumber);
    }
}