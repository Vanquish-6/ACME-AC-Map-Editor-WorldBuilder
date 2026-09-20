using System.Reflection;
using System.Text.Json;

namespace WorldBuilder.Shared.Lib;

public static class LegacyDatDmLayoutSpecNames {
    public const string CharGenWizard = "char_gen_wizard";
    public const string InGamePanels = "in_game_panels";
}

public sealed class LegacyDatDmLayoutSpec {
    public int Version { get; init; } = 1;
    public string? Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<LegacyDatDmLayoutSpecLayout> Layouts { get; init; } = Array.Empty<LegacyDatDmLayoutSpecLayout>();
    public IReadOnlyList<LegacyDatDmLayoutBlockedFeature> BlockedFeatures { get; init; } = Array.Empty<LegacyDatDmLayoutBlockedFeature>();
    public IReadOnlyList<LegacyDatDmLayoutOperation> Operations { get; init; } = Array.Empty<LegacyDatDmLayoutOperation>();

    public static LegacyDatDmLayoutSpec LoadEmbeddedSpec(string specName) => specName switch {
        LegacyDatDmLayoutSpecNames.CharGenWizard => LoadEmbedded("WorldBuilder.Shared.Data.dm_chargen_layout_spec.json"),
        LegacyDatDmLayoutSpecNames.InGamePanels => LoadEmbedded("WorldBuilder.Shared.Data.dm_ingame_layout_spec.json"),
        _ => throw new ArgumentOutOfRangeException(nameof(specName), specName, "Unknown DM layout spec name."),
    };

    private static LegacyDatDmLayoutSpec LoadEmbedded(string resourceName) {
        var assembly = typeof(LegacyDatDmLayoutSpec).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) {
            throw new InvalidOperationException($"Missing embedded resource {resourceName}.");
        }

        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();
        var spec = JsonSerializer.Deserialize<LegacyDatDmLayoutSpec>(json, JsonOptions);
        if (spec == null || spec.Layouts.Count == 0) {
            throw new InvalidOperationException($"DM layout spec is empty or invalid ({resourceName}).");
        }

        return spec;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class LegacyDatDmLayoutSpecLayout {
    public string LayoutId { get; init; } = string.Empty;
    public string? Module { get; init; }
    public string? ClientField { get; init; }
    public string Role { get; init; } = string.Empty;
    public string? SourceElementId { get; init; }
    public int? HardcodedProgressState { get; init; }
    public string? Notes { get; init; }

    public uint LayoutIdValue => LegacyDatDmLayoutSpecParsing.ParseHex(LayoutId);
}

public sealed class LegacyDatDmLayoutBlockedFeature {
    public string Area { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string? Source { get; init; }
}

public sealed class LegacyDatDmLayoutOperation {
    public string Type { get; init; } = string.Empty;
    public string LayoutId { get; init; } = string.Empty;
    public string? ElementId { get; init; }
    public uint? X { get; init; }
    public uint? Y { get; init; }
    public uint? Width { get; init; }
    public uint? Height { get; init; }
    public uint? ReadOrder { get; init; }
    public string? BaseLayoutId { get; init; }
    public string? BaseElementId { get; init; }
    public bool Disabled { get; init; }
    public string? Notes { get; init; }

    public uint LayoutIdValue => LegacyDatDmLayoutSpecParsing.ParseHex(LayoutId);
    public uint ElementIdValue => LegacyDatDmLayoutSpecParsing.ParseHex(ElementId ?? throw new InvalidOperationException("Layout operation requires an element id."));
    public uint? BaseLayoutIdValue => string.IsNullOrWhiteSpace(BaseLayoutId) ? null : LegacyDatDmLayoutSpecParsing.ParseHex(BaseLayoutId);
    public uint? BaseElementIdValue => string.IsNullOrWhiteSpace(BaseElementId) ? null : LegacyDatDmLayoutSpecParsing.ParseHex(BaseElementId);
}

internal static class LegacyDatDmLayoutSpecParsing {
    internal static uint ParseHex(string value) {
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
            value = value[2..];
        }

        return uint.Parse(value, System.Globalization.NumberStyles.HexNumber);
    }
}
