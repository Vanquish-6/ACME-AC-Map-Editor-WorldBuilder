using System.Numerics;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib.MonsterBuilder;

public readonly record struct MeshStats(
    int VertexCount,
    int TriangleCount,
    int DegenerateTriangles,
    Vector3 Min,
    Vector3 Max) {
    public Vector3 Size => Max - Min;
}

/// <summary>
/// Edits a GfxObj in its own local coordinates and copies those edits into the
/// fields the native DAT writer serializes. Setup placement frames are not applied.
/// </summary>
public static class GfxObjMesh {
    public static MeshStats Stats(GfxObj gfx) {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var vertex in gfx.VertexArray.Vertices.Values) {
            if (!IsFinite(vertex.Origin))
                continue;
            min = Vector3.Min(min, vertex.Origin);
            max = Vector3.Max(max, vertex.Origin);
            any = true;
        }
        if (!any) {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }

        int triangles = 0;
        int degenerate = 0;
        foreach (var poly in gfx.Polygons.Values) {
            if (poly.VertexIds.Count < 3)
                continue;
            int fans = poly.VertexIds.Count - 2;
            triangles += fans;
            if (IsDegenerate(gfx, poly))
                degenerate += fans;
        }
        return new MeshStats(gfx.VertexArray.Vertices.Count, triangles, degenerate, min, max);
    }

    public static (Vector3 Center, float Radius) BoundingSphere(GfxObj gfx) {
        var stats = Stats(gfx);
        var center = (stats.Min + stats.Max) * 0.5f;
        float radius = 0.01f;
        foreach (var vertex in gfx.VertexArray.Vertices.Values)
            radius = MathF.Max(radius, Vector3.Distance(center, vertex.Origin));
        return (center, radius);
    }

    public static void TransformVertices(GfxObj gfx, Matrix4x4 matrix, bool rotateNormals) {
        Matrix4x4.Invert(matrix, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);
        foreach (var vertex in gfx.VertexArray.Vertices.Values) {
            vertex.Origin = Vector3.Transform(vertex.Origin, matrix);
            if (rotateNormals && vertex.Normal.LengthSquared() > 1e-12f) {
                var normal = Vector3.TransformNormal(vertex.Normal, rotateNormals ? normalMatrix : matrix);
                float length = normal.Length();
                vertex.Normal = length > 1e-8f ? normal / length : vertex.Normal;
            }
        }
        Commit(gfx, topologyChanged: false);
    }

    public static void Translate(GfxObj gfx, Vector3 delta) =>
        TransformVertices(gfx, Matrix4x4.CreateTranslation(delta), rotateNormals: false);

    public static void Rotate(GfxObj gfx, Quaternion rotation) =>
        TransformVertices(gfx, Matrix4x4.CreateFromQuaternion(rotation), rotateNormals: true);

    public static void ScaleAboutOrigin(GfxObj gfx, Vector3 scale) {
        TransformVertices(gfx, Matrix4x4.CreateScale(scale), rotateNormals: false);
        RecomputeNormals(gfx);
        Commit(gfx, topologyChanged: false);
    }

    public static void ScaleUniform(GfxObj gfx, float scale) =>
        ScaleAboutOrigin(gfx, new Vector3(scale));

    public static void MirrorLocal(GfxObj gfx, int axis) {
        foreach (var vertex in gfx.VertexArray.Vertices.Values) {
            var position = vertex.Origin;
            var normal = vertex.Normal;
            if (axis == 0) { position.X = -position.X; normal.X = -normal.X; }
            else if (axis == 1) { position.Y = -position.Y; normal.Y = -normal.Y; }
            else { position.Z = -position.Z; normal.Z = -normal.Z; }
            vertex.Origin = position;
            vertex.Normal = normal;
        }
        ReverseWinding(gfx);
        Commit(gfx, topologyChanged: false);
    }

    public static void ScaleAlong(GfxObj gfx, Vector3 axis, float factor) {
        float length = axis.Length();
        if (length < 1e-6f || !float.IsFinite(factor))
            return;
        axis /= length;
        foreach (var vertex in gfx.VertexArray.Vertices.Values) {
            float along = Vector3.Dot(vertex.Origin, axis);
            var perpendicular = vertex.Origin - axis * along;
            vertex.Origin = perpendicular + axis * (along * factor);
        }
        RecomputeNormals(gfx);
        Commit(gfx, topologyChanged: false);
    }

    public static void ScalePerpendicular(GfxObj gfx, Vector3 axis, float factor) {
        float length = axis.Length();
        if (length < 1e-6f || !float.IsFinite(factor))
            return;
        axis /= length;
        foreach (var vertex in gfx.VertexArray.Vertices.Values) {
            float along = Vector3.Dot(vertex.Origin, axis);
            var perpendicular = vertex.Origin - axis * along;
            vertex.Origin = axis * along + perpendicular * factor;
        }
        RecomputeNormals(gfx);
        Commit(gfx, topologyChanged: false);
    }

    public static void Taper(GfxObj gfx, Vector3 axis, float endScale) {
        float length = axis.Length();
        if (length < 1e-6f)
            return;
        axis /= length;
        var stats = Stats(gfx);
        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (var vertex in gfx.VertexArray.Vertices.Values) {
            float along = Vector3.Dot(vertex.Origin, axis);
            min = MathF.Min(min, along);
            max = MathF.Max(max, along);
        }
        float span = MathF.Max(max - min, 1e-6f);
        foreach (var vertex in gfx.VertexArray.Vertices.Values) {
            float along = Vector3.Dot(vertex.Origin, axis);
            float t = (along - min) / span;
            float factor = 1f + (endScale - 1f) * t;
            var perpendicular = vertex.Origin - axis * along;
            vertex.Origin = axis * along + perpendicular * factor;
        }
        _ = stats;
        RecomputeNormals(gfx);
        Commit(gfx, topologyChanged: false);
    }

    public static void Bulge(GfxObj gfx, Vector3 axis, float amount) {
        float length = axis.Length();
        if (length < 1e-6f)
            return;
        axis /= length;
        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (var vertex in gfx.VertexArray.Vertices.Values) {
            float along = Vector3.Dot(vertex.Origin, axis);
            min = MathF.Min(min, along);
            max = MathF.Max(max, along);
        }
        float span = MathF.Max(max - min, 1e-6f);
        foreach (var vertex in gfx.VertexArray.Vertices.Values) {
            float along = Vector3.Dot(vertex.Origin, axis);
            float t = (along - min) / span;
            float factor = 1f + amount * MathF.Sin(t * MathF.PI);
            var perpendicular = vertex.Origin - axis * along;
            vertex.Origin = axis * along + perpendicular * factor;
        }
        RecomputeNormals(gfx);
        Commit(gfx, topologyChanged: false);
    }

    public static Vector3 LongestAxis(GfxObj gfx) {
        var size = Stats(gfx).Size;
        if (size.X >= size.Y && size.X >= size.Z) return Vector3.UnitX;
        if (size.Y >= size.Z) return Vector3.UnitY;
        return Vector3.UnitZ;
    }

    public static void ReplaceTriangles(
        GfxObj gfx,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<Vector2> uvs,
        IReadOnlyList<int> surfaceIndices,
        IReadOnlyList<uint> surfaces,
        bool deduplicate) {

        var corners = new List<(Vector3 P, Vector3 N, Vector2 Uv, int Surface)>(positions.Count);
        for (int i = 0; i < positions.Count; i++) {
            var normal = i < normals.Count ? normals[i] : Vector3.UnitZ;
            if (normal.LengthSquared() < 1e-12f)
                normal = Vector3.UnitZ;
            else
                normal = Vector3.Normalize(normal);
            var uv = i < uvs.Count ? uvs[i] : Vector2.Zero;
            int surface = i < surfaceIndices.Count ? surfaceIndices[i] : 0;
            corners.Add((positions[i], normal, uv, surface));
        }

        var verts = new Dictionary<ushort, SWVertex>();
        var polys = new Dictionary<ushort, Polygon>();
        if (deduplicate)
            BuildDeduplicated(corners, verts, polys);
        else
            BuildSoup(corners, verts, polys);

        gfx.Surfaces = surfaces.Count > 0 ? surfaces.ToList() : gfx.Surfaces.Count > 0 ? gfx.Surfaces : [0];
        gfx.VertexArray = new VertexArray {
            VertexType = VertexType.CSWVertexType,
            Vertices = verts,
        };
        gfx.Polygons = polys;
        gfx.VertexType = (int)VertexType.CSWVertexType;
        var (center, _) = BoundingSphere(gfx);
        gfx.SortCenter = center;
        Commit(gfx, topologyChanged: true);
    }

    public static void Deduplicate(GfxObj gfx) {
        var corners = new List<(Vector3 P, Vector3 N, Vector2 Uv, int Surface)>();
        var oldVerts = gfx.VertexArray.Vertices;
        foreach (var poly in gfx.Polygons.OrderBy(kv => kv.Key)) {
            for (int i = 0; i < poly.Value.VertexIds.Count; i++) {
                ushort id = poly.Value.VertexIds[i];
                if (!oldVerts.TryGetValue(id, out var vertex))
                    continue;
                var uv = vertex.UVs.Count > 0
                    ? new Vector2(vertex.UVs[Math.Min(UvIndex(poly.Value, i), vertex.UVs.Count - 1)].U,
                        vertex.UVs[Math.Min(UvIndex(poly.Value, i), vertex.UVs.Count - 1)].V)
                    : Vector2.Zero;
                corners.Add((vertex.Origin, vertex.Normal, uv, poly.Value.PosSurface));
            }
        }
        // Deduplicate walks existing corners, but triangles must stay grouped.
        // Rebuild from the current triangle list instead.
        var triCorners = new List<(Vector3 P, Vector3 N, Vector2 Uv, int Surface)>();
        foreach (var poly in gfx.Polygons.OrderBy(kv => kv.Key)) {
            if (poly.Value.VertexIds.Count < 3)
                continue;
            var gathered = new List<(Vector3 P, Vector3 N, Vector2 Uv, int Surface)>();
            bool ok = true;
            for (int i = 0; i < poly.Value.VertexIds.Count; i++) {
                if (!oldVerts.TryGetValue(poly.Value.VertexIds[i], out var vertex)) {
                    ok = false;
                    break;
                }
                int uvIndex = UvIndex(poly.Value, i);
                if (uvIndex >= vertex.UVs.Count) uvIndex = 0;
                var uv = vertex.UVs.Count > 0
                    ? new Vector2(vertex.UVs[uvIndex].U, vertex.UVs[uvIndex].V)
                    : Vector2.Zero;
                gathered.Add((vertex.Origin, vertex.Normal, uv, poly.Value.PosSurface));
            }
            if (!ok || gathered.Count < 3)
                continue;
            for (int fan = 2; fan < gathered.Count; fan++) {
                triCorners.Add(gathered[0]);
                triCorners.Add(gathered[fan - 1]);
                triCorners.Add(gathered[fan]);
            }
        }
        _ = corners;
        var verts = new Dictionary<ushort, SWVertex>();
        var polys = new Dictionary<ushort, Polygon>();
        BuildDeduplicated(triCorners, verts, polys);
        gfx.VertexArray.Vertices = verts;
        gfx.Polygons = polys;
        Commit(gfx, topologyChanged: true);
    }

    /// <summary>
    /// Replaces <paramref name="destination"/> geometry with a copy of <paramref name="source"/>.
    /// The destination id and Setup attachment are left alone.
    /// </summary>
    public static void CopyFrom(GfxObj destination, GfxObj source) {
        destination.Surfaces = source.Surfaces.ToList();
        destination.VertexType = source.VertexType;
        destination.VertexArray = new VertexArray {
            VertexType = source.VertexArray?.VertexType ?? VertexType.CSWVertexType,
            Vertices = source.VertexArray?.Vertices.ToDictionary(
                pair => pair.Key,
                pair => new SWVertex {
                    Origin = pair.Value.Origin,
                    Normal = pair.Value.Normal,
                    UVs = pair.Value.UVs.Select(uv => new Vec2Duv { U = uv.U, V = uv.V }).ToList(),
                }) ?? [],
        };
        destination.Polygons = source.Polygons.ToDictionary(pair => pair.Key, pair => ClonePolygon(pair.Value));
        var (center, _) = BoundingSphere(destination);
        destination.SortCenter = center;
        Commit(destination, topologyChanged: true);
    }

    public static void ApplySurface(GfxObj gfx, uint surfaceDid, IEnumerable<ushort>? polygonIds) {
        if (!gfx.Surfaces.Contains(surfaceDid))
            gfx.Surfaces.Add(surfaceDid);
        short index = (short)gfx.Surfaces.IndexOf(surfaceDid);
        var selected = polygonIds?.ToHashSet();
        foreach (var entry in gfx.Polygons) {
            if (selected != null && !selected.Contains(entry.Key))
                continue;
            entry.Value.PosSurface = index;
        }
        Commit(gfx, topologyChanged: false);
    }

    public static void AddPrimitive(GfxObj gfx, PrimitiveKind kind, PrimitiveArgs args) {
        var mesh = PrimitiveFactory.Create(kind, args);
        if (gfx.Surfaces.Count == 0)
            gfx.Surfaces.Add(args.SurfaceDid == 0 ? 0x08000001u : args.SurfaceDid);
        int surfaceIndex = 0;
        if (args.SurfaceDid != 0) {
            if (!gfx.Surfaces.Contains(args.SurfaceDid))
                gfx.Surfaces.Add(args.SurfaceDid);
            surfaceIndex = gfx.Surfaces.IndexOf(args.SurfaceDid);
        }

        ushort nextVertex = gfx.VertexArray.Vertices.Count == 0
            ? (ushort)0
            : (ushort)(gfx.VertexArray.Vertices.Keys.Max() + 1);
        var idMap = new Dictionary<int, ushort>();
        for (int i = 0; i < mesh.Positions.Count; i++) {
            if (nextVertex == ushort.MaxValue)
                throw new InvalidOperationException("GfxObj vertex ids are full.");
            gfx.VertexArray.Vertices[nextVertex] = new SWVertex {
                Origin = mesh.Positions[i],
                Normal = mesh.Normals[i],
                UVs = [new Vec2Duv { U = mesh.Uvs[i].X, V = mesh.Uvs[i].Y }],
            };
            idMap[i] = nextVertex;
            nextVertex++;
        }

        ushort nextPoly = gfx.Polygons.Count == 0
            ? (ushort)0
            : (ushort)(gfx.Polygons.Keys.Max() + 1);
        for (int i = 0; i < mesh.Indices.Count; i += 3) {
            gfx.Polygons[nextPoly++] = Triangle(
                idMap[mesh.Indices[i]],
                idMap[mesh.Indices[i + 1]],
                idMap[mesh.Indices[i + 2]],
                (short)surfaceIndex);
        }
        var (center, _) = BoundingSphere(gfx);
        gfx.SortCenter = center;
        Commit(gfx, topologyChanged: true);
    }

    public static void CopyMirroredFrom(
        GfxObj source,
        Matrix4x4 sourceLocalToSetup,
        GfxObj destination,
        Matrix4x4 destinationLocalToSetup) {

        if (!Matrix4x4.Invert(destinationLocalToSetup, out var setupToDest))
            throw new InvalidOperationException("The destination part transform cannot be inverted.");

        var mirrored = new List<(Vector3 P, Vector3 N, Vector2 Uv, int Surface)>();
        foreach (var poly in source.Polygons.OrderBy(kv => kv.Key)) {
            if (poly.Value.VertexIds.Count < 3)
                continue;
            var gathered = new List<(Vector3 P, Vector3 N, Vector2 Uv, int Surface)>();
            bool ok = true;
            for (int i = 0; i < poly.Value.VertexIds.Count; i++) {
                if (!source.VertexArray.Vertices.TryGetValue(poly.Value.VertexIds[i], out var vertex)) {
                    ok = false;
                    break;
                }
                var setupPoint = Vector3.Transform(vertex.Origin, sourceLocalToSetup);
                setupPoint.X = -setupPoint.X;
                var local = Vector3.Transform(setupPoint, setupToDest);
                var setupNormal = Vector3.TransformNormal(vertex.Normal, sourceLocalToSetup);
                setupNormal.X = -setupNormal.X;
                var localNormal = Vector3.TransformNormal(setupNormal, setupToDest);
                float normalLength = localNormal.Length();
                if (normalLength > 1e-8f)
                    localNormal /= normalLength;
                int uvIndex = UvIndex(poly.Value, i);
                var uv = vertex.UVs.Count > 0
                    ? new Vector2(vertex.UVs[Math.Min(uvIndex, vertex.UVs.Count - 1)].U,
                        vertex.UVs[Math.Min(uvIndex, vertex.UVs.Count - 1)].V)
                    : Vector2.Zero;
                int surface = poly.Value.PosSurface;
                gathered.Add((local, localNormal, uv, surface));
            }
            if (!ok || gathered.Count < 3)
                continue;
            gathered.Reverse();
            for (int fan = 2; fan < gathered.Count; fan++) {
                mirrored.Add(gathered[0]);
                mirrored.Add(gathered[fan - 1]);
                mirrored.Add(gathered[fan]);
            }
        }

        var surfaces = source.Surfaces.ToList();
        var verts = new Dictionary<ushort, SWVertex>();
        var polys = new Dictionary<ushort, Polygon>();
        BuildSoup(mirrored, verts, polys);
        destination.Surfaces = surfaces;
        destination.VertexArray = new VertexArray {
            VertexType = VertexType.CSWVertexType,
            Vertices = verts,
        };
        destination.Polygons = polys;
        destination.VertexType = (int)VertexType.CSWVertexType;
        var (center, _) = BoundingSphere(destination);
        destination.SortCenter = center;
        Commit(destination, topologyChanged: true);
    }

    public static void RecomputeNormals(GfxObj gfx) {
        var sums = gfx.VertexArray.Vertices.Keys.ToDictionary(id => id, _ => Vector3.Zero);
        foreach (var poly in gfx.Polygons.Values) {
            if (poly.VertexIds.Count < 3)
                continue;
            if (!TryFaceNormal(gfx, poly, out var normal))
                continue;
            foreach (ushort id in poly.VertexIds) {
                if (sums.ContainsKey(id))
                    sums[id] += normal;
            }
        }
        foreach (var entry in gfx.VertexArray.Vertices) {
            var sum = sums[entry.Key];
            float length = sum.Length();
            if (length > 1e-8f)
                entry.Value.Normal = sum / length;
        }
    }

    /// <summary>
    /// Copies <see cref="GfxObj.VertexArray"/> and <see cref="GfxObj.Polygons"/> into the
    /// MessagePack fields <see cref="DatNativeRecords.Pack{T}"/> writes.
    /// Topology changes also rebuild drawing and physics BSP bytes. Vertex edits keep
    /// the source BSP bytes so an unmodified clone stays equivalent.
    /// </summary>
    public static void Commit(GfxObj gfx, bool topologyChanged) {
        gfx.VertexArray ??= new VertexArray();
        gfx.Polygons ??= new Dictionary<ushort, Polygon>();
        gfx.VertexType = (int)gfx.VertexArray.VertexType;

        var vertexOrder = PreserveOrder(gfx.VertexEntryOrder, gfx.VertexArray.Vertices.Keys);
        gfx.VertexEntryOrder = vertexOrder;
        gfx.Vertices = gfx.VertexArray.Vertices.ToDictionary(
            kv => kv.Key,
            kv => DatFrames.From(kv.Value.Origin));
        gfx.RenderVertices = gfx.VertexArray.Vertices.ToDictionary(
            kv => kv.Key,
            kv => new GfxVertex {
                OriginArray = DatFrames.From(kv.Value.Origin),
                NormalArray = DatFrames.From(kv.Value.Normal),
                Uvs = kv.Value.UVs.Select(uv => new[] { uv.U, uv.V }).ToList(),
            });

        var polygonOrder = PreserveOrder(
            gfx.DrawingPolygonEntryOrder.Select(id => (ushort)id),
            gfx.Polygons.Keys);
        gfx.DrawingPolygonEntryOrder = polygonOrder.Select(id => (uint)id).ToList();
        gfx.DrawingPolygons = gfx.Polygons.ToDictionary(kv => (uint)kv.Key, kv => kv.Value);

        if (gfx.Polygons.Count > 0)
            gfx.Flags |= (uint)GfxObjFlags.HasDrawing;

        if (!topologyChanged)
            return;

        var (center, radius) = BoundingSphere(gfx);
        gfx.SortCenter = center;
        var polyIds = polygonOrder.ToList();
        gfx.DrawingBspBytes = RetailBspBytes.DrawingPolygonNode(polyIds, center, radius);

        bool hadPhysics = gfx.Flags.HasFlag(GfxObjFlags.HasPhysics) || gfx.PhysicsPolygons.Count > 0 || gfx.Physics != null;
        if (!hadPhysics)
            return;

        gfx.Physics ??= new GfxObjPhysics();
        gfx.PhysicsPolygons = gfx.Polygons.ToDictionary(kv => kv.Key, kv => ClonePolygon(kv.Value));
        gfx.Physics.RawPolygonEntryOrder = polyIds.Select(id => (uint)id).ToList();
        gfx.Physics.RawPolygonsEntries = gfx.PhysicsPolygons
            .Select(kv => new MapEntry<uint, Polygon> { Key = kv.Key, Value = kv.Value })
            .ToList();
        gfx.Physics.PhysicsBspBytes = RetailBspBytes.PhysicsLeaf(polyIds, center, radius);
        gfx.Flags |= (uint)GfxObjFlags.HasPhysics;
    }

    public static Polygon Triangle(ushort a, ushort b, ushort c, short surface) => new() {
        VertexIds = [a, b, c],
        PosSurface = surface,
        NegSurface = -1,
        PosUVIndices = [0, 0, 0],
        NegUVIndices = [0, 0, 0],
        Stippling = StipplingType.Positive,
        SidesType = (int)CullMode.Clockwise,
    };

    static void BuildSoup(
        List<(Vector3 P, Vector3 N, Vector2 Uv, int Surface)> corners,
        Dictionary<ushort, SWVertex> verts,
        Dictionary<ushort, Polygon> polys) {

        ushort vertexId = 0;
        ushort polyId = 0;
        for (int i = 0; i + 2 < corners.Count; i += 3) {
            ushort a = AddVertex(verts, ref vertexId, corners[i]);
            ushort b = AddVertex(verts, ref vertexId, corners[i + 1]);
            ushort c = AddVertex(verts, ref vertexId, corners[i + 2]);
            polys[polyId++] = Triangle(a, b, c, (short)corners[i].Surface);
        }
    }

    static void BuildDeduplicated(
        List<(Vector3 P, Vector3 N, Vector2 Uv, int Surface)> corners,
        Dictionary<ushort, SWVertex> verts,
        Dictionary<ushort, Polygon> polys) {

        var lookup = new Dictionary<(int, int, int, int, int, int, int), ushort>();
        ushort next = 0;
        ushort PolyVertex((Vector3 P, Vector3 N, Vector2 Uv, int Surface) corner) {
            var key = (
                Quantize(corner.P.X), Quantize(corner.P.Y), Quantize(corner.P.Z),
                Quantize(corner.N.X), Quantize(corner.N.Y), Quantize(corner.N.Z),
                Quantize(corner.Uv.X) ^ Quantize(corner.Uv.Y));
            if (lookup.TryGetValue(key, out ushort existing))
                return existing;
            ushort id = AddVertex(verts, ref next, corner);
            lookup[key] = id;
            return id;
        }

        ushort polyId = 0;
        for (int i = 0; i + 2 < corners.Count; i += 3) {
            polys[polyId++] = Triangle(
                PolyVertex(corners[i]),
                PolyVertex(corners[i + 1]),
                PolyVertex(corners[i + 2]),
                (short)corners[i].Surface);
        }
    }

    static ushort AddVertex(
        Dictionary<ushort, SWVertex> verts,
        ref ushort next,
        (Vector3 P, Vector3 N, Vector2 Uv, int Surface) corner) {

        if (next == ushort.MaxValue)
            throw new InvalidOperationException("GfxObj vertex ids are full.");
        ushort id = next++;
        verts[id] = new SWVertex {
            Origin = corner.P,
            Normal = corner.N,
            UVs = [new Vec2Duv { U = corner.Uv.X, V = corner.Uv.Y }],
        };
        return id;
    }

    static int Quantize(float value) => (int)MathF.Round(value * 100000f);

    static List<ushort> PreserveOrder(IEnumerable<ushort>? current, IEnumerable<ushort> keys) {
        var set = keys.ToHashSet();
        var order = new List<ushort>();
        if (current != null) {
            foreach (ushort key in current) {
                if (set.Remove(key))
                    order.Add(key);
            }
        }
        foreach (ushort key in set.OrderBy(id => id))
            order.Add(key);
        return order;
    }

    static void ReverseWinding(GfxObj gfx) {
        foreach (var poly in gfx.Polygons.Values) {
            poly.VertexIds.Reverse();
            poly.PosUVIndices?.Reverse();
            poly.NegUVIndices?.Reverse();
        }
    }

    static int UvIndex(Polygon poly, int corner) {
        if (poly.PosUVIndices != null && corner < poly.PosUVIndices.Count)
            return poly.PosUVIndices[corner];
        return 0;
    }

    static bool IsDegenerate(GfxObj gfx, Polygon poly) {
        if (!TryFaceNormal(gfx, poly, out var normal))
            return true;
        return normal.LengthSquared() < 1e-12f;
    }

    static bool TryFaceNormal(GfxObj gfx, Polygon poly, out Vector3 normal) {
        normal = Vector3.Zero;
        if (poly.VertexIds.Count < 3)
            return false;
        if (!gfx.VertexArray.Vertices.TryGetValue(poly.VertexIds[0], out var v0)
            || !gfx.VertexArray.Vertices.TryGetValue(poly.VertexIds[1], out var v1)
            || !gfx.VertexArray.Vertices.TryGetValue(poly.VertexIds[2], out var v2))
            return false;
        normal = Vector3.Cross(v1.Origin - v0.Origin, v2.Origin - v0.Origin);
        return true;
    }

    static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    static Polygon ClonePolygon(Polygon source) => new() {
        Stippling = source.Stippling,
        SidesType = source.SidesType,
        PosSurface = source.PosSurface,
        NegSurface = source.NegSurface,
        VertexIds = source.VertexIds.ToList(),
        PosUVIndices = source.PosUVIndices?.ToList() ?? [],
        NegUVIndices = source.NegUVIndices?.ToList() ?? [],
    };
}

public enum PrimitiveKind {
    Cube,
    Box,
    Cylinder,
    Cone,
    Sphere,
    Spike,
    Horn,
}

public sealed class PrimitiveArgs {
    public int Segments { get; init; } = 6;
    public float Radius { get; init; } = 0.06f;
    public float Length { get; init; } = 0.25f;
    public Vector3 Position { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Scale { get; init; } = Vector3.One;
    public uint SurfaceDid { get; init; }
    public float Width { get; init; } = 0.1f;
    public float Height { get; init; } = 0.1f;
    public float Depth { get; init; } = 0.1f;
}

public static class PrimitiveFactory {
    public sealed record Mesh(List<Vector3> Positions, List<Vector3> Normals, List<Vector2> Uvs, List<int> Indices);

    public static Mesh Create(PrimitiveKind kind, PrimitiveArgs args) {
        int segments = Math.Clamp(args.Segments, 3, 16);
        Mesh mesh = kind switch {
            PrimitiveKind.Cube => Box(1f, 1f, 1f),
            PrimitiveKind.Box => Box(args.Width, args.Depth, args.Height),
            PrimitiveKind.Cylinder => Cylinder(args.Radius, args.Length, segments),
            PrimitiveKind.Cone or PrimitiveKind.Spike or PrimitiveKind.Horn => Cone(args.Radius, args.Length, segments),
            PrimitiveKind.Sphere => Sphere(args.Radius, segments, Math.Max(3, segments / 2)),
            _ => Box(args.Width, args.Depth, args.Height),
        };
        var matrix = Matrix4x4.CreateScale(args.Scale)
            * Matrix4x4.CreateFromQuaternion(args.Rotation)
            * Matrix4x4.CreateTranslation(args.Position);
        for (int i = 0; i < mesh.Positions.Count; i++) {
            mesh.Positions[i] = Vector3.Transform(mesh.Positions[i], matrix);
            var normal = Vector3.TransformNormal(mesh.Normals[i], matrix);
            float length = normal.Length();
            mesh.Normals[i] = length > 1e-8f ? normal / length : Vector3.UnitZ;
        }
        return mesh;
    }

    static Mesh Box(float width, float depth, float height) {
        float x = width * 0.5f;
        float y = depth * 0.5f;
        float z = height * 0.5f;
        Vector3[] corners = [
            new(-x, -y, -z), new(x, -y, -z), new(x, y, -z), new(-x, y, -z),
            new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z),
        ];
        int[][] faces = [
            [4, 5, 6, 7],
            [1, 0, 3, 2],
            [0, 1, 5, 4],
            [3, 7, 6, 2],
            [0, 4, 7, 3],
            [1, 2, 6, 5],
        ];
        return Quads(corners, faces);
    }

    static Mesh Quads(Vector3[] corners, int[][] faces) {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        foreach (var face in faces) {
            var normal = Vector3.Normalize(Vector3.Cross(
                corners[face[1]] - corners[face[0]],
                corners[face[2]] - corners[face[0]]));
            int start = positions.Count;
            Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            for (int i = 0; i < 4; i++) {
                positions.Add(corners[face[i]]);
                normals.Add(normal);
                uvs.Add(uv[i]);
            }
            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
        return new Mesh(positions, normals, uvs, indices);
    }

    static Mesh Cylinder(float radius, float length, int segments) {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        float half = length * 0.5f;
        for (int i = 0; i < segments; i++) {
            float a0 = MathF.Tau * i / segments;
            float a1 = MathF.Tau * (i + 1) / segments;
            var n0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0);
            var n1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0);
            int start = positions.Count;
            Add(positions, normals, uvs, n0 * radius + new Vector3(0, 0, -half), n0, new Vector2(i / (float)segments, 0));
            Add(positions, normals, uvs, n1 * radius + new Vector3(0, 0, -half), n1, new Vector2((i + 1) / (float)segments, 0));
            Add(positions, normals, uvs, n1 * radius + new Vector3(0, 0, half), n1, new Vector2((i + 1) / (float)segments, 1));
            Add(positions, normals, uvs, n0 * radius + new Vector3(0, 0, half), n0, new Vector2(i / (float)segments, 1));
            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
        Cap(positions, normals, uvs, indices, radius, half, segments, true);
        Cap(positions, normals, uvs, indices, radius, -half, segments, false);
        return new Mesh(positions, normals, uvs, indices);
    }

    static void Cap(List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, List<int> indices,
        float radius, float z, int segments, bool up) {
        int center = positions.Count;
        var normal = up ? Vector3.UnitZ : -Vector3.UnitZ;
        Add(positions, normals, uvs, new Vector3(0, 0, z), normal, new Vector2(0.5f, 0.5f));
        int ring = positions.Count;
        for (int i = 0; i < segments; i++) {
            float a = MathF.Tau * i / segments;
            Add(positions, normals, uvs,
                new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, z),
                normal,
                new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a)));
        }
        for (int i = 0; i < segments; i++) {
            int a = ring + i;
            int b = ring + (i + 1) % segments;
            if (up) indices.AddRange([center, a, b]);
            else indices.AddRange([center, b, a]);
        }
    }

    static Mesh Cone(float radius, float length, int segments) {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        var tip = new Vector3(0, 0, length);
        for (int i = 0; i < segments; i++) {
            float a0 = MathF.Tau * i / segments;
            float a1 = MathF.Tau * (i + 1) / segments;
            var p0 = new Vector3(MathF.Cos(a0) * radius, MathF.Sin(a0) * radius, 0);
            var p1 = new Vector3(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius, 0);
            var normal = Vector3.Normalize(Vector3.Cross(p0 - tip, p1 - tip));
            int start = positions.Count;
            Add(positions, normals, uvs, tip, normal, new Vector2(0.5f, 1));
            Add(positions, normals, uvs, p0, normal, new Vector2(i / (float)segments, 0));
            Add(positions, normals, uvs, p1, normal, new Vector2((i + 1) / (float)segments, 0));
            indices.AddRange([start, start + 1, start + 2]);
        }
        Cap(positions, normals, uvs, indices, radius, 0, segments, false);
        return new Mesh(positions, normals, uvs, indices);
    }

    static Mesh Sphere(float radius, int slices, int stacks) {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        for (int stack = 0; stack < stacks; stack++) {
            float v0 = stack / (float)stacks;
            float v1 = (stack + 1) / (float)stacks;
            float p0 = MathF.PI * v0;
            float p1 = MathF.PI * v1;
            for (int slice = 0; slice < slices; slice++) {
                float u0 = slice / (float)slices;
                float u1 = (slice + 1) / (float)slices;
                var a = SpherePoint(radius, p0, MathF.Tau * u0);
                var b = SpherePoint(radius, p0, MathF.Tau * u1);
                var c = SpherePoint(radius, p1, MathF.Tau * u1);
                var d = SpherePoint(radius, p1, MathF.Tau * u0);
                int start = positions.Count;
                Add(positions, normals, uvs, a, Vector3.Normalize(a), new Vector2(u0, v0));
                Add(positions, normals, uvs, b, Vector3.Normalize(b), new Vector2(u1, v0));
                Add(positions, normals, uvs, c, Vector3.Normalize(c), new Vector2(u1, v1));
                Add(positions, normals, uvs, d, Vector3.Normalize(d), new Vector2(u0, v1));
                if (stack > 0)
                    indices.AddRange([start, start + 1, start + 2]);
                if (stack < stacks - 1)
                    indices.AddRange([start, start + 2, start + 3]);
            }
        }
        return new Mesh(positions, normals, uvs, indices);
    }

    static Vector3 SpherePoint(float radius, float polar, float azimuth) =>
        new(
            radius * MathF.Sin(polar) * MathF.Cos(azimuth),
            radius * MathF.Sin(polar) * MathF.Sin(azimuth),
            radius * MathF.Cos(polar));

    static void Add(List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, Vector3 position, Vector3 normal, Vector2 uv) {
        positions.Add(position);
        normals.Add(normal);
        uvs.Add(uv);
    }
}
