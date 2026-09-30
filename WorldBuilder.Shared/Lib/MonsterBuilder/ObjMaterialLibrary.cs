using System.Globalization;

namespace WorldBuilder.Shared.Lib.MonsterBuilder;

public sealed class ObjMaterial {
    public string Name { get; init; } = "";
    /// <summary><c>map_Kd</c> path as written in the file, relative to the MTL's folder.</summary>
    public string? DiffuseMap { get; set; }
    /// <summary><c>Kd</c> as packed ARGB, or null when the material has no colour.</summary>
    public uint? DiffuseColor { get; set; }
    /// <summary><c>d</c> (1 = opaque) folded into the alpha of <see cref="DiffuseColor"/> when present.</summary>
    public float Opacity { get; set; } = 1f;
}

/// <summary>
/// Reads the parts of a Wavefront MTL file that Asheron's Call can express: a diffuse
/// texture (<c>map_Kd</c>) or a flat diffuse colour (<c>Kd</c>).
/// </summary>
public static class ObjMaterialLibrary {
    static readonly HashSet<string> OptionValues = new(StringComparer.OrdinalIgnoreCase) {
        "on", "off", "r", "g", "b", "m", "l", "z", "sphere", "cube_top", "cube_bottom", "cube_front", "cube_back", "cube_left", "cube_right",
    };

    public static Dictionary<string, ObjMaterial> Parse(string text) {
        var materials = new Dictionary<string, ObjMaterial>(StringComparer.Ordinal);
        ObjMaterial? current = null;
        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line.StartsWith("newmtl", StringComparison.OrdinalIgnoreCase)) {
                var name = line.Length > 6 ? line[6..].Trim() : "";
                current = new ObjMaterial { Name = name };
                materials[name] = current;
                continue;
            }
            if (current == null)
                continue;
            if (line.StartsWith("map_Kd", StringComparison.OrdinalIgnoreCase)) {
                current.DiffuseMap = FileArgument(line[6..]);
            }
            else if (line.StartsWith("map_Ka", StringComparison.OrdinalIgnoreCase) && current.DiffuseMap == null) {
                current.DiffuseMap = FileArgument(line[6..]);
            }
            else if (line.StartsWith("Kd ", StringComparison.Ordinal) || line.StartsWith("Kd\t", StringComparison.Ordinal)) {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                    && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
                    && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float b)) {
                    current.DiffuseColor = Pack(current.Opacity, r, g, b);
                }
            }
            else if (line.StartsWith("d ", StringComparison.Ordinal) || line.StartsWith("d\t", StringComparison.Ordinal)) {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && float.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out float d)) {
                    current.Opacity = Math.Clamp(d, 0f, 1f);
                    if (current.DiffuseColor is uint colour)
                        current.DiffuseColor = (colour & 0x00FFFFFF) | ((uint)MathF.Round(current.Opacity * 255f) << 24);
                }
            }
        }
        return materials;
    }

    /// <summary>Strips <c>-s 1 1 1</c>-style options and returns the remaining file name (spaces kept).</summary>
    static string? FileArgument(string rest) {
        var tokens = rest.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        // Options look like "-s 1 1 1" or "-clamp on": skip the flag and any numeric/keyword values after it.
        while (i < tokens.Length && tokens[i].StartsWith('-') && tokens[i].Length > 1 && !char.IsDigit(tokens[i][1])) {
            i++;
            while (i < tokens.Length
                && (float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _) || OptionValues.Contains(tokens[i])))
                i++;
        }
        if (i >= tokens.Length)
            return null;
        var file = string.Join(' ', tokens.Skip(i)).Trim().Trim('"');
        return file.Length == 0 ? null : file.Replace('\\', '/');
    }

    static uint Pack(float alpha, float r, float g, float b) =>
        ((uint)MathF.Round(Math.Clamp(alpha, 0f, 1f) * 255f) << 24)
        | ((uint)MathF.Round(Math.Clamp(r, 0f, 1f) * 255f) << 16)
        | ((uint)MathF.Round(Math.Clamp(g, 0f, 1f) * 255f) << 8)
        | (uint)MathF.Round(Math.Clamp(b, 0f, 1f) * 255f);
}
