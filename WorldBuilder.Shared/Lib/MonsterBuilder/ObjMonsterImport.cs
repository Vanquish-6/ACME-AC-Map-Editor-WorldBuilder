using System.Globalization;
using System.Numerics;
using System.Text;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib.MonsterBuilder;

public sealed class ObjMonsterImportOptions {
    public ObjUpAxis UpAxis { get; init; } = ObjUpAxis.ZUp;
    /// <summary>Scale the model to the creature's height, centre it, and stand it on the creature's floor.</summary>
    public bool FitToCreature { get; init; } = true;
    /// <summary>Extra rotation about Z, applied before fitting. Use 180 when the model faces backwards.</summary>
    public float YawDegrees { get; init; }
    /// <summary>Parts that receive no triangles are collapsed to an invisible placeholder instead of keeping the old mesh.</summary>
    public bool ClearUnmatchedParts { get; init; } = true;
    public bool Deduplicate { get; init; } = true;
    /// <summary>Map OBJ groups by name (<c>_part3</c>, <c>head</c>, a part label) before falling back to position.</summary>
    public bool UseGroupNames { get; init; } = true;
    /// <summary>Flip texture V for OBJs whose UVs have V = 0 at the bottom (Blender, Maya).</summary>
    public bool FlipV { get; init; }
    /// <summary>OBJ material name → Asheron's Call Surface id.</summary>
    public IReadOnlyDictionary<string, uint>? MaterialSurfaces { get; init; }
    /// <summary>Surface for faces with no mapped material and no <c>surface_0x…</c> name.</summary>
    public uint? DefaultSurface { get; init; }
}

public sealed class ObjMonsterImportResult {
    public int Triangles { get; set; }
    public float Scale { get; set; } = 1f;
    public Dictionary<int, int> TrianglesPerPart { get; } = [];
    public List<int> ClearedParts { get; } = [];
    public List<int> KeptParts { get; } = [];
    public List<string> NamedGroups { get; } = [];
    public List<string> PositionedGroups { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool UsedPositionMapping { get; set; }

    public string Summary() {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Imported {Triangles} triangles onto {TrianglesPerPart.Count} parts");
        if (MathF.Abs(Scale - 1f) > 1e-4f)
            text.Append(CultureInfo.InvariantCulture, $", scaled x{Scale:0.###}");
        text.Append('.');
        if (NamedGroups.Count > 0)
            text.Append(CultureInfo.InvariantCulture, $" {NamedGroups.Count} groups matched parts by name.");
        if (UsedPositionMapping)
            text.Append(" Remaining triangles went to the part whose original mesh was closest.");
        if (ClearedParts.Count > 0)
            text.Append(CultureInfo.InvariantCulture, $" {ClearedParts.Count} parts received nothing and were emptied.");
        if (KeptParts.Count > 0)
            text.Append(CultureInfo.InvariantCulture, $" {KeptParts.Count} parts received nothing and kept their old mesh.");
        foreach (var warning in Warnings)
            text.Append(' ').Append(warning);
        return text.ToString();
    }
}

/// <summary>
/// Splits one assembled OBJ across the parts of an existing Setup so a model made
/// outside the game animates with that Setup's MotionTable. Each triangle is placed on
/// a part, converted into that part's GfxObj local space, and written with
/// <see cref="GfxObjMesh.ReplaceTriangles"/>.
/// </summary>
public static class ObjMonsterImport {
    const float PlaceholderSize = 0.002f;

    public sealed class Plan {
        public required ObjPartMesh Mesh { get; init; }
        /// <summary>Part index per triangle, or -1 when nothing could take it.</summary>
        public required int[] PartPerTriangle { get; init; }
        public required ObjMonsterImportResult Result { get; init; }
    }

    /// <summary>
    /// Parses and positions the OBJ, then decides which part each triangle belongs to.
    /// Nothing on the session is changed. <paramref name="labels"/> and <paramref name="roles"/>
    /// map normalised names to part indices for group-name matching.
    /// </summary>
    public static bool TryPlan(
        string objText,
        Setup setup,
        Func<int, GfxObj?> gfxForPart,
        IReadOnlyDictionary<string, int>? labels,
        IReadOnlyDictionary<string, int>? roles,
        ObjMonsterImportOptions options,
        out Plan? plan,
        out string? error) {

        plan = null;
        if (!ObjPartImport.TryParse(objText, deduplicate: false, partIndex: null, out var mesh, out error) || mesh == null)
            return false;
        if (mesh.TriangleCount == 0) {
            error = "OBJ has no faces.";
            return false;
        }
        if (setup.Parts.Count == 0) {
            error = "The Setup has no parts.";
            return false;
        }

        var result = new ObjMonsterImportResult { Triangles = mesh.TriangleCount };
        ObjPartImport.ConvertUpAxis(mesh, options.UpAxis);
        if (options.FlipV)
            ObjPartImport.FlipV(mesh);
        ObjPartImport.ApplyMaterialSurfaces(mesh, options.MaterialSurfaces, options.DefaultSurface);

        var (creatureMin, creatureMax) = SetupAssembly.Bounds(setup, id => GfxById(setup, gfxForPart, id));
        if (options.FitToCreature)
            result.Scale = ObjPartImport.FitToBounds(mesh, creatureMin, creatureMax, options.YawDegrees);
        else if (MathF.Abs(options.YawDegrees) > 1e-4f)
            ObjPartImport.TransformInPlace(mesh, 1f, options.YawDegrees, Vector3.Zero);

        var partPerTriangle = new int[mesh.TriangleCount];
        Array.Fill(partPerTriangle, -1);

        // 1. Group names.
        var groupToPart = new int[mesh.GroupNames.Count];
        Array.Fill(groupToPart, -1);
        if (options.UseGroupNames) {
            for (int g = 0; g < mesh.GroupNames.Count; g++) {
                if (ObjPartImport.TryResolveGroup(mesh.GroupNames[g], setup.Parts.Count, labels, roles, out int part))
                    groupToPart[g] = part;
            }
        }
        bool anyUnresolved = false;
        var usedGroups = new HashSet<int>();
        for (int t = 0; t < mesh.TriangleCount; t++) {
            int group = mesh.GroupIndices[t * 3];
            if (group >= 0)
                usedGroups.Add(group);
            if (group >= 0 && groupToPart[group] >= 0)
                partPerTriangle[t] = groupToPart[group];
            else
                anyUnresolved = true;
        }
        foreach (int g in usedGroups.OrderBy(g => g)) {
            if (groupToPart[g] >= 0)
                result.NamedGroups.Add(mesh.GroupNames[g]);
            else
                result.PositionedGroups.Add(mesh.GroupNames[g]);
        }

        // 2. Nearest original surface for anything left.
        if (anyUnresolved) {
            float creatureHeight = MathF.Max(creatureMax.Z - creatureMin.Z, 1e-3f);
            var reference = BuildReference(setup, gfxForPart, creatureHeight);
            if (reference.Count == 0) {
                error = "No part has geometry to match against, and the OBJ groups are not named after parts.";
                return false;
            }
            result.UsedPositionMapping = true;
            for (int t = 0; t < mesh.TriangleCount; t++) {
                if (partPerTriangle[t] >= 0)
                    continue;
                var centroid = (mesh.Positions[t * 3] + mesh.Positions[t * 3 + 1] + mesh.Positions[t * 3 + 2]) / 3f;
                partPerTriangle[t] = Nearest(reference, centroid);
            }
        }

        foreach (int part in partPerTriangle) {
            if (part >= 0)
                result.TrianglesPerPart[part] = result.TrianglesPerPart.GetValueOrDefault(part) + 1;
        }

        plan = new Plan { Mesh = mesh, PartPerTriangle = partPerTriangle, Result = result };
        return true;
    }

    /// <summary>
    /// Builds the replacement GfxObjs for a plan. Returns copies so a failure leaves
    /// the session untouched; the caller swaps them in.
    /// </summary>
    public static bool TryBuild(
        Plan plan,
        Setup setup,
        Func<int, GfxObj?> gfxForPart,
        ObjMonsterImportOptions options,
        out Dictionary<uint, GfxObj> replacements,
        out string? error) {

        replacements = [];
        error = null;
        var mesh = plan.Mesh;
        var result = plan.Result;

        var trianglesByPart = new Dictionary<int, List<int>>();
        for (int t = 0; t < plan.PartPerTriangle.Length; t++) {
            int part = plan.PartPerTriangle[t];
            if (part < 0)
                continue;
            if (!trianglesByPart.TryGetValue(part, out var list))
                trianglesByPart[part] = list = [];
            list.Add(t);
        }

        // Parts that share one GfxObj cannot take different meshes; the clone step splits them.
        var sharers = setup.Parts
            .Select((id, index) => (id, index))
            .GroupBy(pair => pair.id)
            .Where(group => group.Count() > 1)
            .ToList();
        if (sharers.Count > 0) {
            var first = sharers[0];
            error = $"Parts {string.Join(", ", first.Select(pair => pair.index))} share GfxObj 0x{first.Key:X8}. Clone the monster first so every part has its own GfxObj.";
            return false;
        }

        for (int part = 0; part < setup.Parts.Count; part++) {
            uint gfxId = setup.Parts[part];
            var source = gfxForPart(part);
            if (source == null) {
                result.Warnings.Add($"Part {part} has no GfxObj and was skipped.");
                continue;
            }

            bool hasTriangles = trianglesByPart.TryGetValue(part, out var triangles) && triangles.Count > 0;
            if (!hasTriangles) {
                if (options.ClearUnmatchedParts) {
                    var cleared = DatNativeRecords.CloneMessagePack(source);
                    WritePlaceholder(cleared);
                    replacements[gfxId] = cleared;
                    result.ClearedParts.Add(part);
                }
                else {
                    result.KeptParts.Add(part);
                }
                continue;
            }

            var localToSetup = SetupAssembly.LocalToSetup(setup, part);
            if (!Matrix4x4.Invert(localToSetup, out var setupToLocal)) {
                error = $"Part {part} has a placement transform that cannot be inverted.";
                return false;
            }

            var positions = new List<Vector3>(triangles!.Count * 3);
            var normals = new List<Vector3>(triangles.Count * 3);
            var uvs = new List<Vector2>(triangles.Count * 3);
            var surfaceIndices = new List<int>(triangles.Count * 3);
            var surfaces = new List<uint>();
            var surfaceSlot = new Dictionary<int, int>();
            bool objHasSurfaces = mesh.Surfaces.Count > 0;

            foreach (int t in triangles) {
                for (int c = t * 3; c < t * 3 + 3; c++) {
                    positions.Add(Vector3.Transform(mesh.Positions[c], setupToLocal));
                    var normal = Vector3.TransformNormal(mesh.Normals[c], setupToLocal);
                    float length = normal.Length();
                    normals.Add(length > 1e-8f ? normal / length : Vector3.UnitZ);
                    uvs.Add(mesh.Uvs[c]);
                    int slot = 0;
                    if (objHasSurfaces) {
                        int global = mesh.SurfaceIndices[c];
                        if (!surfaceSlot.TryGetValue(global, out slot)) {
                            slot = surfaces.Count;
                            surfaces.Add(mesh.Surfaces[global]);
                            surfaceSlot[global] = slot;
                        }
                    }
                    surfaceIndices.Add(slot);
                }
            }
            if (!objHasSurfaces)
                surfaces = source.Surfaces.Count > 0 ? [source.Surfaces[0]] : [0x08000001u];

            bool deduplicate = options.Deduplicate || positions.Count > ushort.MaxValue;
            var target = DatNativeRecords.CloneMessagePack(source);
            try {
                GfxObjMesh.ReplaceTriangles(target, positions, normals, uvs, surfaceIndices, surfaces, deduplicate);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("vertex ids", StringComparison.OrdinalIgnoreCase)) {
                error = $"Part {part} would need more than {ushort.MaxValue} vertices ({triangles.Count} triangles). Decimate the model or split it across more parts.";
                return false;
            }
            if (!mesh.HadUvs)
                result.Warnings.Add("The OBJ has no texture coordinates; surfaces will look flat until UVs are added.");
            replacements[gfxId] = target;
        }

        if (result.Warnings.Count > 1)
            DedupeWarnings(result.Warnings);
        return true;
    }

    /// <summary>Grows the Setup sorting and selection spheres so they still enclose the assembled model.</summary>
    public static void GrowSpheres(Setup setup, Func<uint, GfxObj?> getGfx) {
        var (min, max) = SetupAssembly.Bounds(setup, getGfx);
        var center = (min + max) * 0.5f;
        float radius = MathF.Max((max - min).Length() * 0.5f, 0.05f);
        Grow(setup.SortingSphere, center, radius);
        Grow(setup.SelectionSphere, center, radius);

        static void Grow(Sphere sphere, Vector3 center, float radius) {
            if (sphere.Radius <= 0f) {
                sphere.Origin = center;
                sphere.Radius = radius;
                return;
            }
            float needed = Vector3.Distance(sphere.Origin, center) + radius;
            if (needed > sphere.Radius)
                sphere.Radius = needed;
        }
    }

    static void WritePlaceholder(GfxObj gfx) {
        var surfaces = gfx.Surfaces.Count > 0 ? [gfx.Surfaces[0]] : new List<uint> { 0x08000001u };
        GfxObjMesh.ReplaceTriangles(
            gfx,
            [Vector3.Zero, new Vector3(PlaceholderSize, 0, 0), new Vector3(0, PlaceholderSize, 0)],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [Vector2.Zero, new Vector2(1, 0), new Vector2(0, 1)],
            [0, 0, 0],
            surfaces,
            deduplicate: false);
    }

    sealed class PartReference {
        public int Part;
        /// <summary>Setup-space triangles, three corners each.</summary>
        public Vector3[] Triangles = [];
        public Vector3 Center;
        public float Radius;
    }

    /// <summary>
    /// Collects each part's current surface in setup space. Attach-point dummies (one flat
    /// triangle a few centimetres across, used by humanoid Setups for equipment) and cleared
    /// placeholders are left out so they do not pull body triangles onto themselves.
    /// </summary>
    static List<PartReference> BuildReference(Setup setup, Func<int, GfxObj?> gfxForPart, float creatureHeight) {
        var references = new List<PartReference>();
        float minimumDiagonal = MathF.Max(creatureHeight * 0.05f, PlaceholderSize * 4f);
        var statsPerPart = new MeshStats?[setup.Parts.Count];
        bool anyRealMesh = false;
        for (int part = 0; part < setup.Parts.Count; part++) {
            var gfx = gfxForPart(part);
            if (gfx?.VertexArray?.Vertices == null || gfx.VertexArray.Vertices.Count == 0)
                continue;
            statsPerPart[part] = GfxObjMesh.Stats(gfx);
            anyRealMesh |= statsPerPart[part]!.Value.TriangleCount > 2;
        }
        for (int part = 0; part < setup.Parts.Count; part++) {
            if (statsPerPart[part] is not MeshStats stats)
                continue;
            var gfx = gfxForPart(part)!;
            if (stats.Size.Length() < minimumDiagonal)
                continue;
            if (anyRealMesh && stats.TriangleCount <= 2)
                continue;
            var matrix = SetupAssembly.LocalToSetup(setup, part);
            var triangles = new List<Vector3>(stats.TriangleCount * 3);
            foreach (var poly in gfx.Polygons.Values) {
                if (poly.VertexIds.Count < 3)
                    continue;
                if (!gfx.VertexArray.Vertices.TryGetValue(poly.VertexIds[0], out var v0))
                    continue;
                var p0 = Vector3.Transform(v0.Origin, matrix);
                for (int fan = 2; fan < poly.VertexIds.Count; fan++) {
                    if (!gfx.VertexArray.Vertices.TryGetValue(poly.VertexIds[fan - 1], out var v1)
                        || !gfx.VertexArray.Vertices.TryGetValue(poly.VertexIds[fan], out var v2))
                        continue;
                    triangles.Add(p0);
                    triangles.Add(Vector3.Transform(v1.Origin, matrix));
                    triangles.Add(Vector3.Transform(v2.Origin, matrix));
                }
            }
            if (triangles.Count == 0)
                continue;
            var center = Vector3.Zero;
            foreach (var p in triangles) center += p;
            center /= triangles.Count;
            float radius = 0f;
            foreach (var p in triangles) radius = MathF.Max(radius, Vector3.Distance(center, p));
            references.Add(new PartReference { Part = part, Triangles = triangles.ToArray(), Center = center, Radius = radius });
        }
        return references;
    }

    static int Nearest(List<PartReference> references, Vector3 point) {
        int best = references[0].Part;
        float bestDistance = float.MaxValue;
        foreach (var reference in references) {
            // Lower bound: the surface is at least (distance to centre - radius) away.
            float lower = Vector3.Distance(point, reference.Center) - reference.Radius;
            if (lower > 0f && lower * lower >= bestDistance)
                continue;
            var tris = reference.Triangles;
            for (int i = 0; i + 2 < tris.Length; i += 3) {
                float distance = DistanceSquaredToTriangle(point, tris[i], tris[i + 1], tris[i + 2]);
                if (distance < bestDistance) {
                    bestDistance = distance;
                    best = reference.Part;
                }
            }
        }
        return best;
    }

    /// <summary>Closest point on a triangle (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    static float DistanceSquaredToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f)
            return Vector3.DistanceSquared(p, a);

        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3)
            return Vector3.DistanceSquared(p, b);

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) {
            float v = d1 / (d1 - d3);
            return Vector3.DistanceSquared(p, a + ab * v);
        }

        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6)
            return Vector3.DistanceSquared(p, c);

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) {
            float w = d2 / (d2 - d6);
            return Vector3.DistanceSquared(p, a + ac * w);
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return Vector3.DistanceSquared(p, b + (c - b) * w);
        }

        float denominator = 1f / (va + vb + vc);
        float vv = vb * denominator;
        float ww = vc * denominator;
        return Vector3.DistanceSquared(p, a + ab * vv + ac * ww);
    }

    static GfxObj? GfxById(Setup setup, Func<int, GfxObj?> gfxForPart, uint id) {
        int index = setup.Parts.IndexOf(id);
        return index >= 0 ? gfxForPart(index) : null;
    }

    static void DedupeWarnings(List<string> warnings) {
        var seen = new HashSet<string>();
        warnings.RemoveAll(w => !seen.Add(w));
    }
}
