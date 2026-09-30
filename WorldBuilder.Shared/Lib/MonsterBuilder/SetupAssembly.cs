using System.Numerics;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib.MonsterBuilder;

/// <summary>
/// Setup pose used by the Dark Majesty client.
/// <c>CPartArray::UpdateParts</c> (VA 0x00446200) combines each part's placement
/// frame with the object frame. It does not walk <c>parent_index</c>.
/// <c>CPartArray::InitParts</c> (VA 0x004455E0) copies <c>default_scale</c> onto
/// <c>CPhysicsPart::gfxobj_scale</c>, which scales the mesh in GfxObj local space.
/// </summary>
public static class SetupAssembly {
    public const uint HasParent = 0x0001;
    public const uint HasDefaultScale = 0x0002;

    public static AnimationFrame? RestingFrame(Setup setup) {
        if (setup.PlacementFrames.TryGetValue(Placement.Resting, out var resting))
            return resting;
        if (setup.PlacementFrames.TryGetValue(Placement.Default, out var def))
            return def;
        foreach (var entry in setup.PlacementFrames)
            return entry.Value;
        return null;
    }

    public static Placement PreviewPose(Setup setup) {
        if (setup.PlacementFrames.ContainsKey(Placement.Resting))
            return Placement.Resting;
        if (setup.PlacementFrames.ContainsKey(Placement.Default))
            return Placement.Default;
        foreach (var entry in setup.PlacementFrames)
            return entry.Key;
        return Placement.Resting;
    }

    public static bool TryGetPartFrame(Setup setup, int partIndex, out Frame frame) {
        var placement = RestingFrame(setup);
        if (placement?.Frames != null && partIndex >= 0 && partIndex < placement.Frames.Count) {
            frame = placement.Frames[partIndex];
            return true;
        }

        frame = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };
        return false;
    }

    public static Vector3 DefaultScale(Setup setup, int partIndex) {
        if ((setup.Flags & HasDefaultScale) == 0)
            return Vector3.One;
        if (partIndex < 0 || partIndex >= setup.DefaultScale.Count)
            return Vector3.One;
        var raw = setup.DefaultScale[partIndex];
        if (raw == null || raw.Length < 3)
            return Vector3.One;
        return new Vector3(raw[0], raw[1], raw[2]);
    }

    /// <summary>
    /// Maps GfxObj local coordinates into setup/object space for the static pose.
    /// Scale, then the placement rotation, then the placement translation.
    /// </summary>
    public static Matrix4x4 LocalToSetup(Setup setup, int partIndex) {
        TryGetPartFrame(setup, partIndex, out var frame);
        var scale = DefaultScale(setup, partIndex);
        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(frame.Orientation)
            * Matrix4x4.CreateTranslation(frame.Origin);
    }

    public static bool TryInvert(Matrix4x4 matrix, out Matrix4x4 inverse) =>
        Matrix4x4.Invert(matrix, out inverse);

    public static bool HasParentLink(Setup setup, int partIndex, out int parent) {
        parent = -1;
        if ((setup.Flags & HasParent) == 0)
            return false;
        if (partIndex < 0 || partIndex >= setup.ParentIndex.Count)
            return false;
        uint raw = setup.ParentIndex[partIndex];
        if (raw == uint.MaxValue || raw >= (uint)setup.Parts.Count)
            return false;
        parent = (int)raw;
        return true;
    }

    public static int Depth(Setup setup, int partIndex) {
        int depth = 0;
        int guard = setup.Parts.Count + 1;
        int current = partIndex;
        var seen = new HashSet<int>();
        while (HasParentLink(setup, current, out int parent) && seen.Add(current) && guard-- > 0) {
            depth++;
            current = parent;
        }
        return depth;
    }

    public static Vector3 SetupUpAxisInLocal(Setup setup, int partIndex) =>
        AxisInLocal(setup, partIndex, Vector3.UnitZ);

    public static Vector3 SetupWidthAxisInLocal(Setup setup, int partIndex) =>
        AxisInLocal(setup, partIndex, Vector3.UnitX);

    public static Vector3 AxisInLocal(Setup setup, int partIndex, Vector3 setupAxis) {
        TryGetPartFrame(setup, partIndex, out var frame);
        var local = Vector3.Transform(setupAxis, Quaternion.Inverse(frame.Orientation));
        float length = local.Length();
        return length > 1e-6f ? local / length : Vector3.UnitZ;
    }

    public static (Vector3 Min, Vector3 Max) Bounds(Setup setup, Func<uint, GfxObj?> getGfx) {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        for (int i = 0; i < setup.Parts.Count; i++) {
            var gfx = getGfx(setup.Parts[i]);
            if (gfx?.VertexArray.Vertices == null || gfx.VertexArray.Vertices.Count == 0)
                continue;
            var matrix = LocalToSetup(setup, i);
            foreach (var vertex in gfx.VertexArray.Vertices.Values) {
                var point = Vector3.Transform(vertex.Origin, matrix);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
                any = true;
            }
        }
        return any ? (min, max) : (new Vector3(-0.5f), new Vector3(0.5f));
    }
}
