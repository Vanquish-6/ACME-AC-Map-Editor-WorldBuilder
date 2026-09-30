using System.Globalization;
using System.Numerics;

namespace WorldBuilder.Shared.Lib.MonsterBuilder;

public sealed class ObjPartMesh {
    public List<Vector3> Positions { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<Vector2> Uvs { get; } = [];
    public List<int> SurfaceIndices { get; } = [];
    public List<uint> Surfaces { get; } = [];
    /// <summary>Index into <see cref="GroupNames"/> per corner. -1 when the face was outside any <c>g</c>/<c>o</c>.</summary>
    public List<int> GroupIndices { get; } = [];
    public List<string> GroupNames { get; } = [];
    /// <summary>Index into <see cref="MaterialNames"/> per corner. -1 before the first <c>usemtl</c>.</summary>
    public List<int> MaterialIndices { get; } = [];
    public List<string> MaterialNames { get; } = [];
    /// <summary><c>mtllib</c> file names as written in the OBJ.</summary>
    public List<string> MaterialLibraries { get; } = [];
    public int SourceTriangles { get; set; }
    public bool Deduplicated { get; set; }
    public bool HadNormals { get; set; }
    public bool HadUvs { get; set; }

    public int TriangleCount => Positions.Count / 3;

    public (Vector3 Min, Vector3 Max) Bounds() {
        if (Positions.Count == 0)
            return (Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in Positions) {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }
}

/// <summary>
/// Which axis points up in the OBJ file. Asheron's Call is Z up with Y forward.
/// Blender, Maya, and most downloaded models export Y up.
/// </summary>
public enum ObjUpAxis {
    ZUp,
    YUp,
}

/// <summary>
/// Wavefront OBJ reader for one body part. Faces are triangulated. Vertices stay
/// as triangle soup unless <paramref name="deduplicate"/> is set. Index forms
/// <c>v</c>, <c>v/vt</c>, <c>v//vn</c>, and <c>v/vt/vn</c> are accepted.
/// </summary>
public static class ObjPartImport {
    public static bool TryParse(string text, bool deduplicate, out ObjPartMesh? mesh, out string? error) =>
        TryParse(text, deduplicate, partIndex: null, out mesh, out error);

    public static bool TryParse(string text, bool deduplicate, int? partIndex, out ObjPartMesh? mesh, out string? error) {
        mesh = null;
        error = null;
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var corners = new List<(Vector3 P, Vector3 N, Vector2 Uv, uint Surface, int Group, int Material)>();
        var groupNames = new List<string>();
        var materialNames = new List<string>();
        var libraries = new List<string>();
        uint currentSurface = 0;
        bool useSurface = false;
        int currentGroup = -1;
        int currentMaterial = -1;
        bool groupFiltered = false;
        bool acceptGroup = true;
        int sourceTriangles = 0;
        bool usedNormals = false;
        bool usedUvs = false;

        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line.StartsWith("g ", StringComparison.Ordinal) || line.StartsWith("o ", StringComparison.Ordinal)
                || line == "g" || line == "o") {
                var name = line.Length > 2 ? line[2..].Trim() : "";
                currentGroup = groupNames.IndexOf(name);
                if (currentGroup < 0) {
                    currentGroup = groupNames.Count;
                    groupNames.Add(name);
                }
                if (partIndex.HasValue && name.Contains("_part", StringComparison.Ordinal)) {
                    groupFiltered = true;
                    acceptGroup = GroupMatchesPart(name, partIndex.Value);
                }
                continue;
            }
            if (!acceptGroup && groupFiltered && line.StartsWith("f ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("mtllib ", StringComparison.Ordinal)) {
                var library = line[7..].Trim();
                if (library.Length > 0 && !libraries.Contains(library))
                    libraries.Add(library);
                continue;
            }
            if (line.StartsWith("usemtl", StringComparison.Ordinal)) {
                var material = line.Length > 6 ? line[6..].Trim() : "";
                currentMaterial = materialNames.IndexOf(material);
                if (currentMaterial < 0) {
                    currentMaterial = materialNames.Count;
                    materialNames.Add(material);
                }
                if (TryParseSurface(material, out uint surface)) {
                    currentSurface = surface;
                    useSurface = true;
                }
                continue;
            }
            if (line.StartsWith("v ", StringComparison.Ordinal)) {
                if (!TryVec3(line, out var position, out error))
                    return false;
                positions.Add(position);
                continue;
            }
            if (line.StartsWith("vn ", StringComparison.Ordinal)) {
                if (!TryVec3(line, out var normal, out error))
                    return false;
                normals.Add(normal);
                continue;
            }
            if (line.StartsWith("vt ", StringComparison.Ordinal)) {
                if (!TryVec2(line, out var uv, out error))
                    return false;
                uvs.Add(uv);
                continue;
            }
            if (!line.StartsWith("f ", StringComparison.Ordinal))
                continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var face = new List<(int V, int Vt, int Vn)>();
            for (int i = 1; i < tokens.Length; i++) {
                if (!TryFaceIndex(tokens[i], positions.Count, uvs.Count, normals.Count, out var corner, out error))
                    return false;
                face.Add(corner);
            }
            if (face.Count < 3) {
                error = "OBJ face has fewer than 3 vertices.";
                return false;
            }
            var faceNormal = FaceNormal(positions, face);
            for (int fan = 2; fan < face.Count; fan++) {
                AddCorner(face[0], faceNormal);
                AddCorner(face[fan - 1], faceNormal);
                AddCorner(face[fan], faceNormal);
                sourceTriangles++;
            }
        }

        if (corners.Count == 0) {
            error = "OBJ has no faces.";
            return false;
        }

        if (!usedNormals)
            SmoothNormals(corners);

        if (deduplicate)
            corners = Deduplicate(corners);

        var surfaces = new List<uint>();
        var surfaceLookup = new Dictionary<uint, int>();
        mesh = new ObjPartMesh {
            SourceTriangles = sourceTriangles,
            Deduplicated = deduplicate,
            HadNormals = usedNormals,
            HadUvs = usedUvs,
        };
        mesh.GroupNames.AddRange(groupNames);
        mesh.MaterialNames.AddRange(materialNames);
        mesh.MaterialLibraries.AddRange(libraries);
        foreach (var corner in corners) {
            mesh.Positions.Add(corner.P);
            mesh.Normals.Add(corner.N);
            mesh.Uvs.Add(corner.Uv);
            mesh.GroupIndices.Add(corner.Group);
            mesh.MaterialIndices.Add(corner.Material);
            uint surface = useSurface ? corner.Surface : 0;
            if (!surfaceLookup.TryGetValue(surface, out int index)) {
                index = surfaces.Count;
                surfaces.Add(surface);
                surfaceLookup[surface] = index;
            }
            mesh.SurfaceIndices.Add(index);
        }
        if (useSurface)
            mesh.Surfaces.AddRange(surfaces);
        return true;

        void AddCorner((int V, int Vt, int Vn) corner, Vector3 faceNormal) {
            var position = positions[corner.V];
            Vector3 normal;
            if (corner.Vn >= 0 && corner.Vn < normals.Count) {
                normal = normals[corner.Vn];
                usedNormals = true;
            }
            else {
                normal = faceNormal;
            }
            var uv = Vector2.Zero;
            if (corner.Vt >= 0 && corner.Vt < uvs.Count) {
                uv = uvs[corner.Vt];
                usedUvs = true;
            }
            corners.Add((position, normal, uv, currentSurface, currentGroup, currentMaterial));
        }
    }

    /// <summary>
    /// Cheap pass that reports the <c>mtllib</c> files and <c>usemtl</c> names in an OBJ
    /// so textures can be resolved before the full parse.
    /// </summary>
    public static (List<string> Libraries, List<string> Materials) ScanMaterials(string text) {
        var libraries = new List<string>();
        var materials = new List<string>();
        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();
            if (line.StartsWith("mtllib ", StringComparison.Ordinal)) {
                var library = line[7..].Trim();
                if (library.Length > 0 && !libraries.Contains(library))
                    libraries.Add(library);
            }
            else if (line.StartsWith("usemtl", StringComparison.Ordinal)) {
                var material = line.Length > 6 ? line[6..].Trim() : "";
                if (!materials.Contains(material))
                    materials.Add(material);
            }
        }
        return (libraries, materials);
    }

    /// <summary>
    /// Assigns Asheron's Call surfaces from OBJ material names. Corners whose material is in
    /// <paramref name="materialToSurface"/> take that surface. Other corners keep a
    /// <c>surface_0x…</c> surface when the file named one, otherwise fall back to
    /// <paramref name="defaultSurface"/>, otherwise to the first mapped surface.
    /// Returns false when nothing could be assigned and the mesh is left as parsed.
    /// </summary>
    public static bool ApplyMaterialSurfaces(ObjPartMesh mesh, IReadOnlyDictionary<string, uint>? materialToSurface, uint? defaultSurface) {
        if ((materialToSurface == null || materialToSurface.Count == 0) && !defaultSurface.HasValue)
            return false;
        var perCorner = new uint?[mesh.Positions.Count];
        uint? firstMapped = null;
        bool any = false;
        for (int i = 0; i < mesh.Positions.Count; i++) {
            int material = i < mesh.MaterialIndices.Count ? mesh.MaterialIndices[i] : -1;
            if (material >= 0 && materialToSurface != null
                && materialToSurface.TryGetValue(mesh.MaterialNames[material], out uint mapped)) {
                perCorner[i] = mapped;
                firstMapped ??= mapped;
                any = true;
                continue;
            }
            if (mesh.Surfaces.Count > 0 && i < mesh.SurfaceIndices.Count) {
                perCorner[i] = mesh.Surfaces[mesh.SurfaceIndices[i]];
                any = true;
                continue;
            }
            if (defaultSurface.HasValue) {
                perCorner[i] = defaultSurface.Value;
                any = true;
            }
        }
        if (!any)
            return false;
        uint fallback = defaultSurface ?? firstMapped ?? 0x08000001u;
        var surfaces = new List<uint>();
        var lookup = new Dictionary<uint, int>();
        mesh.SurfaceIndices.Clear();
        mesh.Surfaces.Clear();
        for (int i = 0; i < perCorner.Length; i++) {
            uint surface = perCorner[i] ?? fallback;
            if (!lookup.TryGetValue(surface, out int slot)) {
                slot = surfaces.Count;
                surfaces.Add(surface);
                lookup[surface] = slot;
            }
            mesh.SurfaceIndices.Add(slot);
        }
        mesh.Surfaces.AddRange(surfaces);
        return true;
    }

    /// <summary>
    /// OBJ and Blender put V = 0 at the bottom of the image; Asheron's Call (Direct3D) puts it
    /// at the top. Flip so a DCC texture lands the right way up.
    /// </summary>
    public static void FlipV(ObjPartMesh mesh) {
        for (int i = 0; i < mesh.Uvs.Count; i++)
            mesh.Uvs[i] = new Vector2(mesh.Uvs[i].X, 1f - mesh.Uvs[i].Y);
    }

    /// <summary>
    /// Files without <c>vn</c> get one normal per face, which makes every corner unique and
    /// looks faceted. Average the face normals at each shared position so organic models
    /// shade smoothly and welding can share vertices.
    /// </summary>
    static void SmoothNormals(List<(Vector3 P, Vector3 N, Vector2 Uv, uint Surface, int Group, int Material)> corners) {
        var sums = new Dictionary<(int, int, int), Vector3>();
        foreach (var corner in corners) {
            var key = Key(corner.P);
            sums[key] = sums.GetValueOrDefault(key) + corner.N;
        }
        for (int i = 0; i < corners.Count; i++) {
            var corner = corners[i];
            var sum = sums[Key(corner.P)];
            float length = sum.Length();
            if (length > 1e-6f)
                corners[i] = corner with { N = sum / length };
        }

        static (int, int, int) Key(Vector3 p) =>
            ((int)MathF.Round(p.X * 10000f), (int)MathF.Round(p.Y * 10000f), (int)MathF.Round(p.Z * 10000f));
    }

    static Vector3 FaceNormal(List<Vector3> positions, List<(int V, int Vt, int Vn)> face) {
        // Newell's method handles concave and non-planar polygons better than one cross product.
        var normal = Vector3.Zero;
        for (int i = 0; i < face.Count; i++) {
            var a = positions[face[i].V];
            var b = positions[face[(i + 1) % face.Count].V];
            normal.X += (a.Y - b.Y) * (a.Z + b.Z);
            normal.Y += (a.Z - b.Z) * (a.X + b.X);
            normal.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        float length = normal.Length();
        return length > 1e-12f ? normal / length : Vector3.UnitZ;
    }

    /// <summary>
    /// Rewrites a Y-up file (Blender, Maya, most downloads) into Asheron's Call Z-up.
    /// File X stays X, file Y becomes Z, and file -Z becomes Y so the model still faces forward.
    /// </summary>
    public static void ConvertUpAxis(ObjPartMesh mesh, ObjUpAxis upAxis) {
        if (upAxis == ObjUpAxis.ZUp)
            return;
        for (int i = 0; i < mesh.Positions.Count; i++) {
            mesh.Positions[i] = YUpToZUp(mesh.Positions[i]);
            mesh.Normals[i] = YUpToZUp(mesh.Normals[i]);
        }
    }

    static Vector3 YUpToZUp(Vector3 v) => new(v.X, -v.Z, v.Y);

    /// <summary>
    /// Guesses the file's up axis. ACME exports (header comment or <c>_partN</c> groups,
    /// which Blender keeps as object names on a round trip) are Z up; anything else is
    /// treated as a Y-up DCC export.
    /// </summary>
    public static ObjUpAxis DetectUpAxis(string text) {
        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line[0] == '#') {
                if (line.Contains("WorldBuilder", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("AC Setup", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("AC GfxObj", StringComparison.OrdinalIgnoreCase))
                    return ObjUpAxis.ZUp;
                continue;
            }
            if ((line.StartsWith("g ", StringComparison.Ordinal) || line.StartsWith("o ", StringComparison.Ordinal))
                && TryPartIndexFromMarker(line[2..], out _))
                return ObjUpAxis.ZUp;
        }
        return ObjUpAxis.YUp;
    }

    /// <summary>Applies a uniform scale, a yaw about Z, and a translation to every corner, in that order.</summary>
    public static void TransformInPlace(ObjPartMesh mesh, float scale, float yawDegrees, Vector3 translation) {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawDegrees * (MathF.PI / 180f));
        var matrix = Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(translation);
        var normalMatrix = Matrix4x4.CreateFromQuaternion(rotation);
        for (int i = 0; i < mesh.Positions.Count; i++) {
            mesh.Positions[i] = Vector3.Transform(mesh.Positions[i], matrix);
            var normal = Vector3.TransformNormal(mesh.Normals[i], normalMatrix);
            float length = normal.Length();
            mesh.Normals[i] = length > 1e-8f ? normal / length : Vector3.UnitZ;
        }
    }

    /// <summary>
    /// Scales the mesh uniformly so its height matches <paramref name="targetMin"/>..<paramref name="targetMax"/>,
    /// centres it on X and Y, and puts its lowest point on the target floor.
    /// </summary>
    public static float FitToBounds(ObjPartMesh mesh, Vector3 targetMin, Vector3 targetMax, float yawDegrees) {
        if (mesh.Positions.Count == 0)
            return 1f;
        // Yaw first so the fitted bounds describe the oriented model.
        if (MathF.Abs(yawDegrees) > 1e-4f)
            TransformInPlace(mesh, 1f, yawDegrees, Vector3.Zero);
        var (min, max) = mesh.Bounds();
        var size = max - min;
        var targetSize = targetMax - targetMin;
        float sourceHeight = MathF.Max(size.Z, 1e-6f);
        float targetHeight = targetSize.Z > 1e-4f ? targetSize.Z : MathF.Max(targetSize.X, MathF.Max(targetSize.Y, 1f));
        float scale = targetHeight / sourceHeight;
        var sourceCenter = (min + max) * 0.5f;
        var targetCenter = (targetMin + targetMax) * 0.5f;
        var translation = new Vector3(
            targetCenter.X - sourceCenter.X * scale,
            targetCenter.Y - sourceCenter.Y * scale,
            targetMin.Z - min.Z * scale);
        TransformInPlace(mesh, scale, 0f, translation);
        return scale;
    }

    /// <summary>
    /// Scales the mesh uniformly so its longest dimension matches the target box's longest
    /// dimension, then moves its centre onto the target centre. Used for single parts,
    /// whose long axis is rarely Z.
    /// </summary>
    public static float FitCentered(ObjPartMesh mesh, Vector3 targetMin, Vector3 targetMax) {
        if (mesh.Positions.Count == 0)
            return 1f;
        var (min, max) = mesh.Bounds();
        var size = max - min;
        var targetSize = targetMax - targetMin;
        float sourceMax = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        float targetMaxDim = MathF.Max(targetSize.X, MathF.Max(targetSize.Y, targetSize.Z));
        if (sourceMax < 1e-6f || targetMaxDim < 1e-6f)
            return 1f;
        float scale = targetMaxDim / sourceMax;
        var sourceCenter = (min + max) * 0.5f;
        var targetCenter = (targetMin + targetMax) * 0.5f;
        TransformInPlace(mesh, scale, 0f, targetCenter - sourceCenter * scale);
        return scale;
    }

    public static void ConvertAssembledToLocal(ObjPartMesh mesh, Matrix4x4 localToSetup) {
        if (!Matrix4x4.Invert(localToSetup, out var setupToLocal))
            throw new InvalidOperationException("The part placement transform cannot be inverted.");
        for (int i = 0; i < mesh.Positions.Count; i++) {
            mesh.Positions[i] = Vector3.Transform(mesh.Positions[i], setupToLocal);
            var normal = Vector3.TransformNormal(mesh.Normals[i], setupToLocal);
            float length = normal.Length();
            mesh.Normals[i] = length > 1e-8f ? normal / length : Vector3.UnitZ;
        }
    }

    static List<(Vector3 P, Vector3 N, Vector2 Uv, uint Surface, int Group, int Material)> Deduplicate(
        List<(Vector3 P, Vector3 N, Vector2 Uv, uint Surface, int Group, int Material)> corners) {
        // Welding is explicit. The returned list is still expanded per triangle so the
        // GfxObj builder can choose shared indices from identical corners.
        return corners;
    }

    static bool GroupMatchesPart(string group, int partIndex) =>
        TryPartIndexFromMarker(group, out int index) ? index == partIndex : true;

    /// <summary>Reads N from names like <c>setup_02000034_part7_0x0100ABCD</c>.</summary>
    public static bool TryPartIndexFromMarker(string group, out int partIndex) {
        partIndex = -1;
        const string marker = "_part";
        int at = group.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return false;
        int start = at + marker.Length;
        int end = start;
        while (end < group.Length && char.IsDigit(group[end]))
            end++;
        return end > start
            && int.TryParse(group[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out partIndex);
    }

    /// <summary>
    /// Resolves an OBJ group name to a Setup part. Accepts the ACME export marker
    /// (<c>_part3</c>), plain <c>part3</c> / <c>part_3</c> / <c>3</c>, an editor label,
    /// or a role name such as <c>head</c> or <c>Upper Arm L</c>.
    /// </summary>
    public static bool TryResolveGroup(
        string group,
        int partCount,
        IReadOnlyDictionary<string, int>? labels,
        IReadOnlyDictionary<string, int>? roles,
        out int partIndex) {
        partIndex = -1;
        var name = group.Trim();
        if (name.Length == 0)
            return false;
        if (TryPartIndexFromMarker(name, out int marked) && marked >= 0 && marked < partCount) {
            partIndex = marked;
            return true;
        }
        var lowered = name.ToLowerInvariant();
        var plain = lowered;
        if (plain.StartsWith("part", StringComparison.Ordinal))
            plain = plain[4..].TrimStart('_', '-', ' ');
        if (int.TryParse(plain, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= 0 && parsed < partCount) {
            partIndex = parsed;
            return true;
        }
        // Blender appends ".001"-style suffixes to duplicated names.
        int dot = lowered.LastIndexOf('.');
        var stem = dot > 0 && lowered.Length - dot <= 4 && lowered[(dot + 1)..].All(char.IsDigit) ? lowered[..dot] : lowered;
        var key = Normalize(stem);
        if (labels != null && labels.TryGetValue(key, out partIndex))
            return true;
        if (roles != null && roles.TryGetValue(key, out partIndex))
            return true;
        partIndex = -1;
        return false;
    }

    public static string Normalize(string text) {
        var chars = text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    static bool TryFaceIndex(string token, int positionCount, int uvCount, int normalCount,
        out (int V, int Vt, int Vn) corner, out string? error) {
        corner = default;
        error = null;
        var parts = token.Split('/');
        if (!TryResolve(parts[0], positionCount, out int vertex)) {
            error = $"OBJ face vertex index '{parts[0]}' is outside 1..{positionCount}.";
            return false;
        }
        int vt = -1;
        int vn = -1;
        if (parts.Length > 1 && parts[1].Length > 0) {
            if (!TryResolve(parts[1], uvCount, out vt)) {
                error = $"OBJ texture index '{parts[1]}' is outside 1..{uvCount}.";
                return false;
            }
        }
        if (parts.Length > 2 && parts[2].Length > 0) {
            if (!TryResolve(parts[2], normalCount, out vn)) {
                error = $"OBJ normal index '{parts[2]}' is outside 1..{normalCount}.";
                return false;
            }
        }
        corner = (vertex, vt, vn);
        return true;
    }

    static bool TryResolve(string text, int count, out int index) {
        index = -1;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw) || raw == 0)
            return false;
        index = raw > 0 ? raw - 1 : count + raw;
        return index >= 0 && index < count;
    }

    static bool TryVec3(string line, out Vector3 value, out string? error) {
        value = default;
        error = null;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) {
            error = $"Could not parse OBJ vector '{line}'.";
            return false;
        }
        value = new Vector3(x, y, z);
        return true;
    }

    static bool TryVec2(string line, out Vector2 value, out string? error) {
        value = default;
        error = null;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) {
            error = $"Could not parse OBJ texture coordinate '{line}'.";
            return false;
        }
        value = new Vector2(x, y);
        return true;
    }

    static bool TryParseSurface(string name, out uint surface) {
        surface = 0;
        const string prefixed = "surface_0x";
        if (name.StartsWith(prefixed, StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(name.AsSpan(prefixed.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out surface);
        const string plain = "surface_";
        if (name.StartsWith(plain, StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(name.AsSpan(plain.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out surface);
        return false;
    }
}
