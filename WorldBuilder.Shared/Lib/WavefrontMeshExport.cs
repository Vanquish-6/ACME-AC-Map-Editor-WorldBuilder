using Acme.Dat;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace WorldBuilder.Shared.Lib {

    /// <summary>
    /// Wavefront OBJ export for portal <see cref="GfxObj"/> / <see cref="Setup"/> geometry.
    /// Export to OBJ for inspection and editing in Blender; rebuilding <see cref="GfxObj"/> for DAT is a separate step.
    /// </summary>
    public static class WavefrontMeshExport {

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static AnimationFrame? GetDefaultPlacementFrame(Setup setup) {
            if (setup.PlacementFrames.TryGetValue(Placement.Resting, out var resting))
                return resting;
            if (setup.PlacementFrames.TryGetValue(Placement.Default, out var def))
                return def;
            foreach (var kvp in setup.PlacementFrames)
                return kvp.Value;
            return null;
        }

        /// <summary>
        /// Writes one GfxObj: all renderable faces (positive and negative surfaces), fan-triangulated.
        /// Skips <see cref="StipplingType.NoPos"/> (portal openings). Negative-face polygons
        /// are emitted with reversed vertex order so normals point correctly in DCC tools.
        /// Coordinates are AC / DAT space (same as WorldBuilder GL preview); rotate in DCC if needed.
        /// </summary>
        public static void WriteGfxObj(GfxObj gfx, uint gfxObjId, TextWriter w) {
            w.WriteLine("# AC GfxObj → Wavefront OBJ (WorldBuilder)");
            w.WriteLine($"# GfxObj 0x{gfxObjId:X8}");
            w.WriteLine("o gfxobj");

            TriangulateFaces(gfx, Matrix4x4.Identity, out var positions, out var normals, out var uvs, out var surfaceRanges);
            WriteVerticesAndFaces(w, positions, normals, uvs, 1, surfaceRanges);
        }

        /// <summary>
        /// Each Setup part becomes an OBJ <c>g</c> group; vertices use the default placement frame (Resting → Default → first).
        /// </summary>
        public static bool TryWriteSetup(IDatReaderWriter dats, uint setupId, TextWriter w, out string? error) {
            error = null;
            if (!dats.TryGet<Setup>(setupId, out var setup) || setup == null) {
                error = "Setup not found in DATs.";
                return false;
            }

            w.WriteLine("# AC Setup → Wavefront OBJ (WorldBuilder)");
            w.WriteLine($"# Setup 0x{setupId:X8}");
            var placementFrame = GetDefaultPlacementFrame(setup);

            int nextObjIndex = 1;
            for (int pi = 0; pi < setup.Parts.Count; pi++) {
                uint partId = setup.Parts[pi];
                if (!dats.TryGet<GfxObj>(partId, out var gfx) || gfx == null) {
                    w.WriteLine($"# skip part {pi}: GfxObj 0x{partId:X8} missing");
                    continue;
                }

                var transform = Matrix4x4.Identity;
                if (placementFrame?.Frames != null && pi < placementFrame.Frames.Count) {
                    var fr = placementFrame.Frames[pi];
                    transform = Matrix4x4.CreateFromQuaternion(fr.Orientation)
                        * Matrix4x4.CreateTranslation(fr.Origin);
                }

                w.WriteLine($"g setup_{setupId:X8}_part{pi}_0x{partId:X8}");
                TriangulateFaces(gfx, transform, out var positions, out var normals, out var uvs, out var surfaceRanges);
                nextObjIndex = WriteVerticesAndFaces(w, positions, normals, uvs, nextObjIndex, surfaceRanges);
            }

            return true;
        }

        /// <summary>
        /// Triangulates all renderable faces from a GfxObj, grouped by surface DID.
        /// Skips <see cref="StipplingType.NoPos"/> (portal openings); all other polygons
        /// export using PosSurface / PosUVIndices. This matches the renderer behaviour.
        /// </summary>
        static void TriangulateFaces(GfxObj gfx, Matrix4x4 transform,
            out List<Vector3> positions, out List<Vector3> normals, out List<Vector2> uvs) {
            TriangulateFaces(gfx, transform, out positions, out normals, out uvs, out _);
        }

        static void TriangulateFaces(GfxObj gfx, Matrix4x4 transform,
            out List<Vector3> positions, out List<Vector3> normals, out List<Vector2> uvs,
            out List<(uint SurfaceDid, int TriStart, int TriCount)> surfaceRanges) {

            positions = new List<Vector3>();
            normals = new List<Vector3>();
            uvs = new List<Vector2>();
            surfaceRanges = new List<(uint, int, int)>();

            var rot = transform;
            rot.M41 = rot.M42 = rot.M43 = 0f;
            rot.M44 = 1f;

            var polysBySurface = new Dictionary<uint, List<Polygon>>();
            foreach (var poly in gfx.Polygons.Values) {
                if (poly.VertexIds.Count < 3) continue;
                if (poly.Stippling == StipplingType.NoPos) continue;
                if (poly.PosSurface < 0 || poly.PosSurface >= gfx.Surfaces.Count) continue;

                uint surfaceDid = gfx.Surfaces[poly.PosSurface];
                if (!polysBySurface.TryGetValue(surfaceDid, out var list)) {
                    list = new List<Polygon>();
                    polysBySurface[surfaceDid] = list;
                }
                list.Add(poly);
            }

            foreach (var (surfaceDid, polys) in polysBySurface) {
                int triStart = positions.Count / 3;
                foreach (var poly in polys)
                    EmitFace(poly, gfx, transform, rot, reversed: false, useNegUVs: false, positions, normals, uvs);
                int triCount = positions.Count / 3 - triStart;
                if (triCount > 0)
                    surfaceRanges.Add((surfaceDid, triStart, triCount));
            }
        }

        static void EmitFace(Polygon poly, GfxObj gfx, Matrix4x4 transform, Matrix4x4 rot,
            bool reversed, bool useNegUVs,
            List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs) {

            var corners = new List<(Vector3 p, Vector3 n, Vector2 uv)>();
            for (int i = 0; i < poly.VertexIds.Count; i++) {
                ushort rawVert = poly.VertexIds[i];
                if (rawVert < 0) { corners.Clear(); break; }

                ushort vertId = (ushort)rawVert;
                ushort uvIdx = 0;
                var uvList = useNegUVs ? poly.NegUVIndices : poly.PosUVIndices;
                if (uvList != null && i < uvList.Count)
                    uvIdx = uvList[i];

                if (!gfx.VertexArray.Vertices.TryGetValue(vertId, out var vertex)) { corners.Clear(); break; }
                if (uvIdx >= vertex.UVs.Count) uvIdx = 0;

                var uv = vertex.UVs.Count > 0
                    ? new Vector2(vertex.UVs[uvIdx].U, vertex.UVs[uvIdx].V)
                    : Vector2.Zero;

                var localN = vertex.Normal;
                float len = localN.Length();
                if (len > 1e-6f) localN /= len;
                if (reversed) localN = -localN;

                corners.Add((Vector3.Transform(vertex.Origin, transform), Vector3.TransformNormal(localN, rot), uv));
            }

            if (corners.Count < 3) return;

            if (reversed) corners.Reverse();

            for (int i = 2; i < corners.Count; i++) {
                positions.Add(corners[0].p); normals.Add(corners[0].n); uvs.Add(corners[0].uv);
                positions.Add(corners[i - 1].p); normals.Add(corners[i - 1].n); uvs.Add(corners[i - 1].uv);
                positions.Add(corners[i].p); normals.Add(corners[i].n); uvs.Add(corners[i].uv);
            }
        }

        /// <returns>Next 1-based OBJ index after this block.</returns>
        static int WriteVerticesAndFaces(TextWriter w,
            List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, int indexBase,
            List<(uint SurfaceDid, int TriStart, int TriCount)>? surfaceRanges = null) {

            foreach (var p in positions)
                w.WriteLine(string.Create(Inv, $"v {p.X} {p.Y} {p.Z}"));
            foreach (var n in normals)
                w.WriteLine(string.Create(Inv, $"vn {n.X} {n.Y} {n.Z}"));
            foreach (var uv in uvs)
                w.WriteLine(string.Create(Inv, $"vt {uv.X} {uv.Y}"));

            if (surfaceRanges != null && surfaceRanges.Count > 0) {
                foreach (var (surfaceDid, triStart, triCount) in surfaceRanges) {
                    w.WriteLine($"usemtl surface_0x{surfaceDid:X8}");
                    for (int t = 0; t < triCount; t++) {
                        int vi = (triStart + t) * 3;
                        int a = indexBase + vi, b = indexBase + vi + 1, c = indexBase + vi + 2;
                        w.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                    }
                }
            }
            else {
                for (int i = 0; i < positions.Count; i += 3) {
                    int a = indexBase + i, b = indexBase + i + 1, c = indexBase + i + 2;
                    w.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                }
            }

            return indexBase + positions.Count;
        }
    }
}
