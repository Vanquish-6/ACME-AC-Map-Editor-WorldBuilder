using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Explicit merge policy for legacy (DM) to retail-format conversion.
/// Presets map to the former <see cref="LegacyDatExportMode"/> values for backward compatibility.
/// </summary>
public sealed record LegacyDatExportPolicy {
    public LegacyDatExportPreset? Preset { get; init; }
    public LegacyDatWorldSource World { get; init; } = LegacyDatWorldSource.DmOnly;
    public LegacyDatPortalCatalogPolicy Portal { get; init; } = LegacyDatPortalCatalogPolicy.WorldClosure;
    public LegacyDatClientShellPolicy Shell { get; init; } = new();
    public LegacyDatRetailGapFillPolicy GapFill { get; init; } = LegacyDatRetailGapFillPolicy.ShellReserve;
    public LegacyDatPortalDeployPolicy Deploy { get; init; } = LegacyDatPortalDeployPolicy.SingleConvertedCatalog;

    public static LegacyDatExportPolicy FromPreset(LegacyDatExportPreset preset) => preset switch {
        // Full DM: everything DM (world, art, textures, CharGen, appearance tables) plus the
        // DM UI replacement (DM portal 0x31 / CLIENT.EXE wording + layout-shape patches on the
        // retail LayoutDesc skeleton, per docs/DM_UI_DECOMPILE.md). Shared login/connect shell
        // art stays retail because the DM source dats do not ship those surface IDs (§8.7.1).
        LegacyDatExportPreset.FullDm => new LegacyDatExportPolicy {
            Preset = LegacyDatExportPreset.FullDm,
            World = LegacyDatWorldSource.DmOnly,
            Portal = LegacyDatPortalCatalogPolicy.FullDmCatalog,
            Shell = new LegacyDatClientShellPolicy {
                Ui = LegacyDatContentSource.DmPortalStrings,
                CharGen = LegacyDatContentSource.Dm,
                AppearanceTables = LegacyDatContentSource.Dm,
                SpellTables = LegacyDatContentSource.GapFillRetail,
                ConnectionLoginUiShell = true,
                PatchConnectionScreenStrings = true,
                PatchInGameScreenStrings = true,
                PatchCharGenLayoutShape = true,
                PatchInGameLayoutShape = true,
            },
            GapFill = LegacyDatRetailGapFillPolicy.FillMissingOnly,
            // Client boot requires the retail portal B-tree root; overlay the converted DM
            // catalog onto the retail seed in-place.
            Deploy = LegacyDatPortalDeployPolicy.OverlayOnRetailSeed,
        },
        // Half DM: DM world / textures / GfxObjs / Setups / cells, but the retail UI shell,
        // retail CharGen, retail appearance and retail spell/skill/vital tables.
        LegacyDatExportPreset.HalfDm => new LegacyDatExportPolicy {
            Preset = LegacyDatExportPreset.HalfDm,
            World = LegacyDatWorldSource.DmOnly,
            Portal = LegacyDatPortalCatalogPolicy.FullDmCatalog,
            Shell = new LegacyDatClientShellPolicy {
                Ui = LegacyDatContentSource.Retail,
                CharGen = LegacyDatContentSource.Retail,
                AppearanceTables = LegacyDatContentSource.Retail,
                SpellTables = LegacyDatContentSource.Retail,
                ConnectionLoginUiShell = true,
            },
            GapFill = LegacyDatRetailGapFillPolicy.FillMissingOnly,
            Deploy = LegacyDatPortalDeployPolicy.OverlayOnRetailSeed,
        },
        // Legacy preset names (old manifests / saved projects) normalize onto the two
        // supported modes. Exports whose manifest carries [export_policy_json] keep their
        // exact axes via TryParseManifestJson and never hit this mapping.
        LegacyDatExportPreset.DmFull or LegacyDatExportPreset.FullDmFaithful =>
            FromPreset(LegacyDatExportPreset.FullDm),
        LegacyDatExportPreset.DmRetailShell or LegacyDatExportPreset.SlimMerge
            or LegacyDatExportPreset.FullMerge or LegacyDatExportPreset.RetailWorldDmArt =>
            FromPreset(LegacyDatExportPreset.HalfDm),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
    };

    /// <summary>
    /// Compatibility shim for the legacy tri-mode enum (old manifests' <c>export_mode=</c> line and
    /// mode-only callers). Preserves the historical axis semantics so old exports repair/validate
    /// the way they were produced; new exports use the two presets.
    /// </summary>
    public static LegacyDatExportPolicy FromLegacyMode(LegacyDatExportMode mode) => mode switch {
        LegacyDatExportMode.FullMerge => new LegacyDatExportPolicy {
            World = LegacyDatWorldSource.RetailDerethPlusDm,
            Portal = LegacyDatPortalCatalogPolicy.FullRetailPlusDmOverlay,
            Shell = new LegacyDatClientShellPolicy {
                Ui = LegacyDatContentSource.Retail,
                CharGen = LegacyDatContentSource.Retail,
                AppearanceTables = LegacyDatContentSource.Retail,
                SpellTables = LegacyDatContentSource.Retail,
                ConnectionLoginUiShell = true,
            },
            GapFill = LegacyDatRetailGapFillPolicy.Off,
            Deploy = LegacyDatPortalDeployPolicy.OverlayOnRetailSeed,
        },
        LegacyDatExportMode.SlimMerge => new LegacyDatExportPolicy {
            World = LegacyDatWorldSource.DmOnly,
            Portal = LegacyDatPortalCatalogPolicy.WorldClosure,
            Shell = new LegacyDatClientShellPolicy {
                Ui = LegacyDatContentSource.DmPortalStrings,
                CharGen = LegacyDatContentSource.Retail,
                AppearanceTables = LegacyDatContentSource.Dm,
                SpellTables = LegacyDatContentSource.Retail,
                ConnectionLoginUiShell = true,
                PatchConnectionScreenStrings = true,
                PatchInGameScreenStrings = true,
                PatchCharGenLayoutShape = true,
                PatchInGameLayoutShape = true,
            },
            GapFill = LegacyDatRetailGapFillPolicy.ShellReserve,
            Deploy = LegacyDatPortalDeployPolicy.OverlayOnRetailSeed,
        },
        LegacyDatExportMode.FullDm => FromPreset(LegacyDatExportPreset.FullDm),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public LegacyDatExportMode ToLegacyMode() {
        if (Preset is LegacyDatExportPreset.FullDm or LegacyDatExportPreset.HalfDm
            or LegacyDatExportPreset.DmFull or LegacyDatExportPreset.FullDmFaithful) {
            return LegacyDatExportMode.FullDm;
        }

        if (Preset is LegacyDatExportPreset.DmRetailShell or LegacyDatExportPreset.SlimMerge) {
            return LegacyDatExportMode.SlimMerge;
        }

        if (Preset is LegacyDatExportPreset.FullMerge or LegacyDatExportPreset.RetailWorldDmArt) {
            return LegacyDatExportMode.FullMerge;
        }

        if (World == LegacyDatWorldSource.RetailDerethPlusDm
            && Portal == LegacyDatPortalCatalogPolicy.FullRetailPlusDmOverlay) {
            return LegacyDatExportMode.FullMerge;
        }

        if (World == LegacyDatWorldSource.DmOnly
            && Portal == LegacyDatPortalCatalogPolicy.FullDmCatalog) {
            return LegacyDatExportMode.FullDm;
        }

        return LegacyDatExportMode.SlimMerge;
    }

    public string GetDisplayName() => GetPresetDisplayName(Preset) ?? "custom policy";

    public static string? GetPresetDisplayName(LegacyDatExportPreset? preset) => preset switch {
        LegacyDatExportPreset.FullDm => "Full DM",
        LegacyDatExportPreset.HalfDm => "Half DM",
        // Legacy aliases (old manifests only).
        LegacyDatExportPreset.DmFull => "Full DM",
        LegacyDatExportPreset.FullDmFaithful => "Full DM",
        LegacyDatExportPreset.DmRetailShell => "Half DM",
        LegacyDatExportPreset.SlimMerge => "Half DM",
        LegacyDatExportPreset.FullMerge => "Half DM",
        LegacyDatExportPreset.RetailWorldDmArt => "Half DM",
        null => null,
        _ => preset.ToString(),
    };

    public string ToManifestJson() => JsonSerializer.Serialize(this, PolicyJsonOptions);

    public static LegacyDatExportPolicy? TryParseManifestJson(string? json) {
        if (string.IsNullOrWhiteSpace(json)) {
            return null;
        }

        try {
            return JsonSerializer.Deserialize<LegacyDatExportPolicy>(json, PolicyJsonOptions);
        }
        catch {
            return null;
        }
    }

    public static readonly JsonSerializerOptions PolicyJsonOptions = new() {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// The two supported conversion modes. The remaining values are legacy aliases kept only so
/// old manifests/projects still parse; <see cref="LegacyDatExportPolicy.FromPreset"/> normalizes
/// them onto <see cref="FullDm"/> or <see cref="HalfDm"/>.
/// </summary>
public enum LegacyDatExportPreset {
    /// <summary>Everything DM (world, art, CharGen, appearance) + DM UI replacing the retail UI.</summary>
    FullDm,
    /// <summary>DM world/textures/objs/setups/cells, but the retail UI/CharGen/table shell.</summary>
    HalfDm,
    // Legacy aliases — do not use for new exports.
    DmRetailShell,
    DmFull,
    FullMerge,
    SlimMerge,
    FullDmFaithful,
    RetailWorldDmArt,
}

public enum LegacyDatWorldSource {
    DmOnly,
    RetailDerethPlusDm,
}

public enum LegacyDatPortalCatalogPolicy {
    FullDmCatalog,
    WorldClosure,
    FullRetailPlusDmOverlay,
}

public sealed record LegacyDatClientShellPolicy {
    public LegacyDatContentSource Ui { get; init; } = LegacyDatContentSource.Retail;
    public LegacyDatContentSource CharGen { get; init; } = LegacyDatContentSource.Retail;
    public LegacyDatContentSource AppearanceTables { get; init; } = LegacyDatContentSource.Dm;
    public LegacyDatContentSource SpellTables { get; init; } = LegacyDatContentSource.Retail;
    /// <summary>
    /// Keep retail portal assets for login/connect/disconnect layouts (for example <c>0x21000001</c>).
    /// Current legacy DM sources do not ship retail shared shell surface IDs, so these bytes must remain
    /// retail unless a later asset-remap/import path is added.
    /// </summary>
    public bool ConnectionLoginUiShell { get; init; }
    /// <summary>
    /// Patch connection/login/disconnect <see cref="StringTable"/> rows with DM CLIENT.EXE wording.
    /// </summary>
    public bool PatchConnectionScreenStrings { get; init; }
    /// <summary>
    /// Patch in-game <see cref="StringTable"/> rows with DM CLIENT.EXE wording.
    /// </summary>
    public bool PatchInGameScreenStrings { get; init; }
    /// <summary>
    /// Apply repeatable retail-layout char-gen shell overrides after finalize and keep the patched local DAT.
    /// </summary>
    public bool PatchCharGenLayoutShape { get; init; }
    /// <summary>
    /// Apply repeatable retail-layout in-game panel overrides after finalize and keep the patched local DAT.
    /// </summary>
    public bool PatchInGameLayoutShape { get; init; }
}

public enum LegacyDatContentSource {
    Dm,
    Retail,
    /// <summary>Retail <c>LayoutDesc</c> shell with DM portal <c>0x31</c> text patched into <c>StringTable</c>.</summary>
    DmPortalStrings,
    GapFillRetail,
}

public enum LegacyDatRetailGapFillPolicy {
    Off,
    FillMissingOnly,
    ShellReserve,
}

public enum LegacyDatPortalDeployPolicy {
    SingleConvertedCatalog,
    OverlayOnRetailSeed,
}

public enum LegacyDatMergeWinner {
    DmConvert,
    RetailCopy,
    Skip,
}
