using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Machine-readable retail client DAT contract loaded from
/// <c>WorldBuilder.Shared/Data/retail_client_dat_spec.json</c>.
/// </summary>
public sealed class RetailClientDatSpec {
    public int Version { get; init; }
    public string Title { get; init; } = "";

    [JsonPropertyName("retail_files")]
    public IReadOnlyList<string> RetailFiles { get; init; } = Array.Empty<string>();

    [JsonPropertyName("reference_seed")]
    public RetailClientDatReferenceSeed? ReferenceSeed { get; init; }

    public RetailClientDatContainerSpec? Container { get; init; }

    [JsonPropertyName("id_bands")]
    public RetailClientDatIdBands? IdBands { get; init; }

    public static RetailClientDatSpec LoadEmbedded() {
        var assembly = typeof(RetailClientDatSpec).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream == null) {
            throw new InvalidOperationException($"Missing embedded resource {EmbeddedResourceName}.");
        }

        using var reader = new StreamReader(stream);
        return LoadFromJson(reader.ReadToEnd());
    }

    public static RetailClientDatSpec LoadFromFile(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadFromJson(File.ReadAllText(path));
    }

    const string EmbeddedResourceName = "WorldBuilder.Shared.Data.retail_client_dat_spec.json";

    static RetailClientDatSpec LoadFromJson(string json) {
        var spec = JsonSerializer.Deserialize<RetailClientDatSpec>(json, JsonOptions)
            ?? throw new InvalidOperationException("retail_client_dat_spec.json deserialized to null.");

        if (spec.RetailFiles.Count == 0) {
            throw new InvalidOperationException("retail_client_dat_spec.json is missing retail_files.");
        }

        return spec;
    }

    public static uint ParseHexId(string value) {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
            value = value[2..];
        }

        return Convert.ToUInt32(value, 16);
    }

    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class RetailClientDatReferenceSeed {
    public string Label { get; init; } = "";
    public Dictionary<string, RetailClientDatFileFingerprint> Fingerprints { get; init; } = new();
}

public sealed class RetailClientDatFileFingerprint {
    public string Magic { get; init; } = "";

    [JsonPropertyName("block_size")]
    public int BlockSize { get; init; }

    [JsonPropertyName("btree_root")]
    public string BtreeRoot { get; init; } = "";

    [JsonPropertyName("catalog_entries")]
    public int? CatalogEntries { get; init; }

    [JsonPropertyName("spell_tables_0x0E")]
    public int? SpellTables0x0E { get; init; }

    [JsonPropertyName("iter_field")]
    public int? IterField { get; init; }
}

public sealed class RetailClientDatContainerSpec {
    public string HeaderOffset { get; init; } = "0x140";
    public string Magic { get; init; } = "0x5442";
    public string TransactionJournalOffset { get; init; } = "0x100";
    public string IterationObjectId { get; init; } = "0xFFFF0001";
}

public sealed class RetailClientDatIdBands {
    [JsonPropertyName("portal_spell_skill_vital_tables_0x0E_retail_seed")]
    public IReadOnlyList<string> PortalSpellTables { get; init; } = Array.Empty<string>();

    public Dictionary<string, string>? PortalGlobals { get; init; }
    public Dictionary<string, string>? LocalUi { get; init; }
}

public sealed class RetailClientDatValidationReport {
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Info { get; init; } = Array.Empty<string>();

    public bool IsValid => Errors.Count == 0;

    public void WriteToConsole(TextWriter? writer = null) {
        writer ??= Console.Out;
        foreach (string line in Info) {
            writer.WriteLine($"INFO: {line}");
        }

        foreach (string line in Warnings) {
            writer.WriteLine($"WARN: {line}");
        }

        foreach (string line in Errors) {
            writer.WriteLine($"FAIL: {line}");
        }

        writer.WriteLine(IsValid ? "VALID" : $"INVALID ({Errors.Count} error(s), {Warnings.Count} warning(s))");
    }
}
