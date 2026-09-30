using System.Text.Json.Serialization;

namespace WorldBuilder.Shared.Lib.MonsterBuilder;

/// <summary>
/// ACME editor metadata. This is not a game DAT record.
/// </summary>
public sealed class MonsterProjectModel {
    [JsonPropertyName("format")] public int Format { get; set; } = 1;
    [JsonPropertyName("source_setup")] public string SourceSetup { get; set; } = "";
    [JsonPropertyName("working_setup")] public string? WorkingSetup { get; set; }
    [JsonPropertyName("cloned")] public bool Cloned { get; set; }
    [JsonPropertyName("parts")] public List<MonsterPartModel> Parts { get; set; } = [];
    [JsonPropertyName("setup_record")] public string? SetupRecord { get; set; }
    [JsonPropertyName("gfx_records")] public Dictionary<string, string> GfxRecords { get; set; } = new();
    [JsonPropertyName("textures")] public List<MonsterTextureModel> Textures { get; set; } = [];
    /// <summary>Surface, SurfaceTexture, and RenderSurface records created by this project, keyed by hex id.</summary>
    [JsonPropertyName("texture_records")] public Dictionary<string, string> TextureRecords { get; set; } = new();
}

public sealed class MonsterTextureModel {
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("surface")] public string Surface { get; set; } = "";
    [JsonPropertyName("surface_texture")] public string? SurfaceTexture { get; set; }
    [JsonPropertyName("render_surface")] public string? RenderSurface { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("solid_color")] public uint? SolidColor { get; set; }
}

public sealed class MonsterPartModel {
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("source_gfxobj")] public string SourceGfxObj { get; set; } = "";
    [JsonPropertyName("working_gfxobj")] public string? WorkingGfxObj { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("mirror_partner")] public int? MirrorPartner { get; set; }
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }
}
