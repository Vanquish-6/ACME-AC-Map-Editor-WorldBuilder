using System.Numerics;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib.MonsterBuilder;

public enum MonsterIssueSeverity {
    Warning,
    Fatal,
}

public sealed record MonsterIssue(MonsterIssueSeverity Severity, string Message);

public sealed class MonsterValidationReport {
    public List<MonsterIssue> Issues { get; } = [];
    public bool HasFatal => Issues.Any(issue => issue.Severity == MonsterIssueSeverity.Fatal);
    public IEnumerable<MonsterIssue> Warnings => Issues.Where(issue => issue.Severity == MonsterIssueSeverity.Warning);
    public IEnumerable<MonsterIssue> Fatals => Issues.Where(issue => issue.Severity == MonsterIssueSeverity.Fatal);

    public void Warn(string message) => Issues.Add(new MonsterIssue(MonsterIssueSeverity.Warning, message));
    public void Fatal(string message) => Issues.Add(new MonsterIssue(MonsterIssueSeverity.Fatal, message));
}

public static class MonsterValidation {
    public static MonsterValidationReport Check(
        Setup setup,
        IReadOnlyDictionary<uint, GfxObj> gfxById,
        Func<uint, bool>? surfaceExists,
        IReadOnlySet<uint>? protectedIds,
        IReadOnlySet<uint>? existingDatIds) {

        var report = new MonsterValidationReport();
        if (setup.Parts.Count == 0)
            report.Fatal("Setup has no parts.");
        if (setup.NumParts != (uint)setup.Parts.Count)
            report.Warn($"Setup num_parts is {setup.NumParts} and the part list has {setup.Parts.Count}. Publish writes the part-list length.");

        if ((setup.Flags & SetupAssembly.HasParent) != 0 && setup.ParentIndex.Count != setup.Parts.Count)
            report.Fatal("Setup parent list length does not match the part list.");
        if ((setup.Flags & SetupAssembly.HasDefaultScale) != 0 && setup.DefaultScale.Count != setup.Parts.Count)
            report.Fatal("Setup default scale list length does not match the part list.");

        var seen = new HashSet<uint>();
        for (int i = 0; i < setup.Parts.Count; i++) {
            uint id = setup.Parts[i];
            string name = $"Part {i}";
            if (!seen.Add(id))
                report.Warn($"{name} repeats GfxObj 0x{id:X8}. The parts share one mesh.");
            if (protectedIds != null && protectedIds.Contains(id))
                report.Fatal($"{name} still uses source GfxObj 0x{id:X8}. Clone before publishing.");
            if (existingDatIds != null && existingDatIds.Contains(id) && (protectedIds == null || !protectedIds.Contains(id)))
                report.Warn($"{name} GfxObj 0x{id:X8} already exists and publish will replace that record.");
            if (!gfxById.TryGetValue(id, out var gfx)) {
                report.Fatal($"{name} references GfxObj 0x{id:X8}, which is not in the working set.");
                continue;
            }
            CheckGfx(report, name, gfx, surfaceExists);
        }

        if (protectedIds != null && protectedIds.Contains(setup.Id))
            report.Fatal($"Working Setup 0x{setup.Id:X8} is still the source id. Clone before publishing.");
        else if (existingDatIds != null && existingDatIds.Contains(setup.Id))
            report.Warn($"Setup 0x{setup.Id:X8} already exists and publish will replace that record.");

        foreach (var placement in setup.PlacementFrames) {
            if (placement.Value.Frames.Count != setup.Parts.Count)
                report.Warn($"Placement 0x{(uint)placement.Key:X} has {placement.Value.Frames.Count} frames for {setup.Parts.Count} parts.");
            if (placement.Value.HookData.Length < 4)
                report.Fatal($"Placement 0x{(uint)placement.Key:X} hook data is shorter than 4 bytes.");
        }

        // Retail Setups (for example 0x02000034) ship with parent cycles and no root; the client
        // never walks parent_index when posing parts, so this cannot block publishing.
        if (Cycle(setup))
            report.Warn("Setup parent links contain a cycle. Retail data does this too; the client ignores parent_index when posing.");

        var (min, max) = SetupAssembly.Bounds(setup, id => gfxById.TryGetValue(id, out var gfx) ? gfx : null);
        var size = max - min;
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || !float.IsFinite(size.Z))
            report.Fatal("Assembled bounds are not finite.");
        else if (size.X > 50f || size.Y > 50f || size.Z > 50f)
            report.Warn($"Assembled bounds are {size.X:0.###} x {size.Y:0.###} x {size.Z:0.###}. That is large for an Asheron's Call creature.");

        return report;
    }

    static void CheckGfx(MonsterValidationReport report, string name, GfxObj gfx, Func<uint, bool>? surfaceExists) {
        var verts = gfx.VertexArray?.Vertices ?? [];
        if (verts.Count == 0)
            report.Fatal($"{name} has no vertices.");
        foreach (var vertex in verts.Values) {
            if (!Finite(vertex.Origin) || !Finite(vertex.Normal))
                report.Fatal($"{name} contains a NaN or infinite vertex.");
        }

        int degenerate = 0;
        foreach (var poly in gfx.Polygons) {
            var polygon = poly.Value;
            if (polygon.VertexIds.Count < 3) {
                report.Fatal($"{name} polygon {poly.Key} has fewer than 3 vertices.");
                continue;
            }
            for (int i = 0; i < polygon.VertexIds.Count; i++) {
                if (!verts.TryGetValue(polygon.VertexIds[i], out var vertex)) {
                    report.Fatal($"{name} polygon {poly.Key} references vertex {polygon.VertexIds[i]}, but the mesh has {verts.Count} vertices.");
                    continue;
                }
                if (polygon.PosUVIndices != null && i < polygon.PosUVIndices.Count) {
                    int uv = polygon.PosUVIndices[i];
                    if (vertex.UVs.Count == 0 || uv < 0 || uv >= vertex.UVs.Count)
                        report.Fatal($"{name} polygon {poly.Key} UV index {uv} is outside vertex {polygon.VertexIds[i]}.");
                }
            }
            if (polygon.PosSurface < 0 || polygon.PosSurface >= gfx.Surfaces.Count)
                report.Fatal($"{name} polygon {poly.Key} surface index {polygon.PosSurface} is outside the surface list.");
            if (IsDegenerate(verts, polygon))
                degenerate++;
        }
        if (degenerate > 0)
            report.Warn($"{name} contains {degenerate} degenerate triangles.");

        if (surfaceExists != null) {
            foreach (uint surface in gfx.Surfaces) {
                if (!surfaceExists(surface))
                    report.Warn($"{name} surface 0x{surface:X8} was not found in the portal DAT.");
            }
        }
    }

    static bool Cycle(Setup setup) {
        if ((setup.Flags & SetupAssembly.HasParent) == 0)
            return false;
        for (int start = 0; start < setup.Parts.Count; start++) {
            var seen = new HashSet<int>();
            int current = start;
            while (SetupAssembly.HasParentLink(setup, current, out int parent)) {
                if (!seen.Add(current))
                    return true;
                current = parent;
            }
        }
        return false;
    }

    static bool IsDegenerate(Dictionary<ushort, SWVertex> verts, Polygon poly) {
        if (!verts.TryGetValue(poly.VertexIds[0], out var a)
            || !verts.TryGetValue(poly.VertexIds[1], out var b)
            || !verts.TryGetValue(poly.VertexIds[2], out var c))
            return false;
        return Vector3.Cross(b.Origin - a.Origin, c.Origin - a.Origin).LengthSquared() < 1e-12f;
    }

    static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
