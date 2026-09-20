using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace WorldBuilder.Shared.Lib {

    /// <summary>
    /// Experimental: single Wavefront OBJ (triangulated mesh) → one <see cref="GfxObj"/> + one-part <see cref="Setup"/>.
    /// Polygons use one existing portal <see cref="Surface"/> DID (retail texture/material).
    /// Blender-style OBJ (no <c>vt</c>, <c>f v//vn</c>) is normalized on import: corners are flattened to parallel arrays
    /// and missing UVs get a planar projection from mesh bounds so the portal vertex format is valid.
    /// </summary>
    public static class ObjSingleMeshImporter {

        public sealed record ImportResult(uint GfxObjId, uint SetupId, int TriangleCount, int VertexCount);

        /// <summary>Next custom DID in the 0x01FFxxxx / 0x02FFxxxx high sub-range (same strategy as <see cref="Services.CustomTextureStore.AllocateGid"/>).</summary>
        public static uint AllocateNextId(uint rangeBase, IEnumerable<uint> existingIds) {
            uint customBase = rangeBase | 0x00FF0000;
            uint maxExisting = existingIds
                .Where(id => id >= customBase)
                .DefaultIfEmpty(customBase)
                .Max();
            return maxExisting >= customBase ? maxExisting + 1 : customBase + 1;
        }

        public static bool TryBuild(string objText, uint surfaceDid, uint gfxObjId, uint setupId,
            out GfxObj? gfxObj, out Setup? setup, out string? error) {

            gfxObj = null;
            setup = null;
            error = null;

            if (!TryParseObj(objText, out var positions, out var normals, out var uvs, out var faces,
                    out var faceSurfaceDids, out var hadAnyVt, out var parseErr)) {
                error = parseErr;
                return false;
            }

            if (faces.Count == 0) {
                error = "OBJ has no faces.";
                return false;
            }

            FlattenFaceCornersToParallelArrays(positions, normals, uvs, faces, out var flatPos, out var flatNorm, out var flatUv, out var flatFaces);
            ApplyPlanarUvIfNeeded(flatPos, flatUv, hadAnyVt);

            // Propagate per-face surface DIDs through the flattening (faces are 1:1).
            // If usemtl was present, faceSurfaceDids[i] holds the DID for original face i.
            var flatSurfaceDids = new List<uint>(flatFaces.Count);
            for (int i = 0; i < flatFaces.Count; i++)
                flatSurfaceDids.Add(i < faceSurfaceDids.Count ? faceSurfaceDids[i] : surfaceDid);

            try {
                gfxObj = BuildGfxObj(gfxObjId, surfaceDid, flatPos, flatNorm, flatUv, flatFaces, flatSurfaceDids);
                var (sphereOrigin, sphereRadius) = ComputeBoundingSphere(flatPos);
                setup = BuildSinglePartSetup(setupId, gfxObjId, sphereOrigin, sphereRadius);
                return true;
            }
            catch (Exception ex) {
                error = ex.Message;
                gfxObj = null;
                setup = null;
                return false;
            }
        }

        static Setup BuildSinglePartSetup(uint setupId, uint gfxObjId, Vector3 sphereOrigin, float sphereRadius) {
            var af = new AnimationFrame(1) {
                Frames = new List<Frame> {
                    new() { Origin = Vector3.Zero, Orientation = Quaternion.Identity }
                }
            };

            var setup = new Setup {
                Id = setupId,
                NumParts = 1,
                Parts = new List<uint> { gfxObjId },
                PlacementFrames = new Dictionary<Placement, AnimationFrame> {
                    [Placement.Default] = af,
                    [Placement.Resting] = CloneAnimationFrame(af),
                },
                SortingSphere = new Sphere { Origin = sphereOrigin, Radius = sphereRadius },
                SelectionSphere = new Sphere { Origin = sphereOrigin, Radius = sphereRadius },
            };
            return setup;
        }

        static AnimationFrame CloneAnimationFrame(AnimationFrame src) {
            var dst = new AnimationFrame(1) {
                Frames = new List<Frame>()
            };
            foreach (var fr in src.Frames)
                dst.Frames.Add(new Frame { Origin = fr.Origin, Orientation = fr.Orientation });
            return dst;
        }

        static GfxObj BuildGfxObj(uint gfxObjId, uint fallbackSurfaceDid,
            List<Vector3> pos, List<Vector3> norm, List<Vector2> uv,
            List<(int i0, int i1, int i2)> triFaces,
            List<uint>? perFaceSurfaceDids = null) {

            // Build an ordered surface list from the per-face DIDs (or use the single fallback).
            var surfaceList = new List<uint>();
            var surfaceIndexMap = new Dictionary<uint, int>();
            if (perFaceSurfaceDids != null) {
                foreach (var did in perFaceSurfaceDids) {
                    if (!surfaceIndexMap.ContainsKey(did)) {
                        surfaceIndexMap[did] = surfaceList.Count;
                        surfaceList.Add(did);
                    }
                }
            }
            if (surfaceList.Count == 0) {
                surfaceList.Add(fallbackSurfaceDid);
                surfaceIndexMap[fallbackSurfaceDid] = 0;
            }

            var gfx = new GfxObj {
                Id = gfxObjId,
                Surfaces = surfaceList,
            };

            var verts = new Dictionary<ushort, SWVertex>();
            ushort nextId = 0;

            ushort GetOrAddVertex(Vector3 p, Vector3 n, Vector2 t) {
                foreach (var (id, v) in verts) {
                    if (Vector3.DistanceSquared(v.Origin, p) < 1e-12f
                        && Vector3.DistanceSquared(v.Normal, n) < 1e-12f
                        && v.UVs.Count > 0
                        && MathF.Abs(v.UVs[0].U - t.X) < 1e-6f
                        && MathF.Abs(v.UVs[0].V - t.Y) < 1e-6f) {
                        return id;
                    }
                }
                var nv = new SWVertex {
                    Origin = p,
                    Normal = Vector3.Normalize(n),
                    UVs = new List<Vec2Duv> { new() { U = t.X, V = t.Y } }
                };
                if (nextId == ushort.MaxValue)
                    throw new InvalidOperationException("Too many unique vertices (ushort overflow).");
                verts[nextId] = nv;
                return nextId++;
            }

            var polys = new Dictionary<ushort, Polygon>();
            ushort polyKey = 0;

            for (int fi = 0; fi < triFaces.Count; fi++) {
                var (ia, ib, ic) = triFaces[fi];
                var pa = pos[ia];
                var pb = pos[ib];
                var pc = pos[ic];
                var na = ia < norm.Count ? norm[ia] : ComputeFlatNormal(pa, pb, pc);
                var nb = ib < norm.Count ? norm[ib] : na;
                var nc = ic < norm.Count ? norm[ic] : na;
                var ua = ia < uv.Count ? uv[ia] : Vector2.Zero;
                var ub = ib < uv.Count ? uv[ib] : Vector2.Zero;
                var uc = ic < uv.Count ? uv[ic] : Vector2.Zero;

                ushort ida = GetOrAddVertex(pa, na, ua);
                ushort idb = GetOrAddVertex(pb, nb, ub);
                ushort idc = GetOrAddVertex(pc, nc, uc);

                uint faceDid = (perFaceSurfaceDids != null && fi < perFaceSurfaceDids.Count)
                    ? perFaceSurfaceDids[fi]
                    : fallbackSurfaceDid;
                int surfIdx = surfaceIndexMap[faceDid];

                var poly = new Polygon {
                    VertexIds = new List<ushort> { ida, idb, idc },
                    PosSurface = (short)surfIdx,
                    NegSurface = -1,
                    PosUVIndices = new List<byte> { 0, 0, 0 },
                    Stippling = StipplingType.Positive,
                    SidesType = (int)CullMode.Clockwise,
                };
                polys[polyKey++] = poly;
            }

            gfx.VertexArray = new VertexArray {
                VertexType = VertexType.CSWVertexType,
                Vertices = verts,
            };
            gfx.Polygons = polys;
            gfx.SortCenter = Vector3.Zero;

            BspGenerator.Build(gfx);

            return gfx;
        }

        /// <summary>
        /// Computes a tight bounding sphere origin (AABB center) and radius for the given vertex positions.
        /// Used to set <see cref="Setup.SortingSphere"/> and <see cref="Setup.SelectionSphere"/>.
        /// </summary>
        static (Vector3 origin, float radius) ComputeBoundingSphere(IReadOnlyList<Vector3> positions) {
            if (positions.Count == 0) return (Vector3.Zero, 1f);
            var min = positions[0];
            var max = positions[0];
            for (int i = 1; i < positions.Count; i++) {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }
            var center = (min + max) * 0.5f;
            float radius = 0f;
            foreach (var p in positions)
                radius = MathF.Max(radius, Vector3.Distance(center, p));
            return (center, MathF.Max(radius, 0.01f));
        }

        static Vector3 ComputeFlatNormal(Vector3 a, Vector3 b, Vector3 c) {
            var e1 = b - a;
            var e2 = c - a;
            var n = Vector3.Cross(e1, e2);
            var len = n.Length();
            return len > 1e-8f ? n / len : Vector3.UnitY;
        }

        static bool TryParseObj(string text,
            out List<Vector3> positions,
            out List<Vector3> normals,
            out List<Vector2> texCoords,
            out List<(int, int, int)> triangles,
            out List<uint> faceSurfaceDids,
            out bool hadAnyVt,
            out string? error) {

            positions = new List<Vector3>();
            normals = new List<Vector3>();
            texCoords = new List<Vector2>();
            triangles = new List<(int, int, int)>();
            faceSurfaceDids = new List<uint>();
            hadAnyVt = false;
            error = null;

            uint currentSurfaceDid = 0;
            bool hasUsemtl = false;

            var inv = CultureInfo.InvariantCulture;
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null) {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line.StartsWith("usemtl ", StringComparison.Ordinal)) {
                    var mtlName = line.Substring(7).Trim();
                    if (TryParseSurfaceDidFromMtl(mtlName, out var did)) {
                        currentSurfaceDid = did;
                        hasUsemtl = true;
                    }
                    continue;
                }

                if (line.StartsWith("v ", StringComparison.Ordinal)) {
                    var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 4) continue;
                    positions.Add(new Vector3(
                        float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv)));
                }
                else if (line.StartsWith("vn ", StringComparison.Ordinal)) {
                    var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 4) continue;
                    normals.Add(Vector3.Normalize(new Vector3(
                        float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv))));
                }
                else if (line.StartsWith("vt ", StringComparison.Ordinal)) {
                    var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 3) continue;
                    hadAnyVt = true;
                    texCoords.Add(new Vector2(float.Parse(p[1], inv), float.Parse(p[2], inv)));
                }
                else if (line.StartsWith("f ", StringComparison.Ordinal)) {
                    var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 4) continue;

                    var corners = new List<(int vi, int vti, int vni)>();
                    for (int i = 1; i < p.Length; i++) {
                        var part = p[i].Split('/');
                        int vi = ParseObjIndex(part[0], positions.Count);
                        int vti = part.Length > 1 && part[1].Length > 0 ? ParseObjIndex(part[1], texCoords.Count) : -1;
                        int vni = part.Length > 2 && part[2].Length > 0 ? ParseObjIndex(part[2], normals.Count) : -1;
                        corners.Add((vi, vti, vni));
                    }

                    if (corners.Count < 3) continue;

                    var expandedPos = new List<Vector3>();
                    var expandedNorm = new List<Vector3>();
                    var expandedUv = new List<Vector2>();

                    foreach (var (vi, vti, vni) in corners) {
                        if (vi < 0 || vi >= positions.Count) {
                            error = "Face references invalid vertex index.";
                            return false;
                        }
                        expandedPos.Add(positions[vi]);
                        expandedNorm.Add(vni >= 0 && vni < normals.Count ? normals[vni] : Vector3.UnitY);
                        expandedUv.Add(vti >= 0 && vti < texCoords.Count ? texCoords[vti] : Vector2.Zero);
                    }

                    for (int k = 2; k < expandedPos.Count; k++) {
                        int baseIdx = positions.Count;
                        positions.Add(expandedPos[0]);
                        positions.Add(expandedPos[k - 1]);
                        positions.Add(expandedPos[k]);
                        normals.Add(expandedNorm[0]);
                        normals.Add(expandedNorm[k - 1]);
                        normals.Add(expandedNorm[k]);
                        texCoords.Add(expandedUv[0]);
                        texCoords.Add(expandedUv[k - 1]);
                        texCoords.Add(expandedUv[k]);
                        triangles.Add((baseIdx, baseIdx + 1, baseIdx + 2));
                        if (hasUsemtl)
                            faceSurfaceDids.Add(currentSurfaceDid);
                    }
                }
            }

            if (!hasUsemtl)
                faceSurfaceDids.Clear();

            if (positions.Count == 0) {
                error = "OBJ has no vertices.";
                return false;
            }

            return true;
        }

        /// <summary>Parses surface DID from material names like "surface_0x08000001".</summary>
        static bool TryParseSurfaceDidFromMtl(string mtlName, out uint did) {
            did = 0;
            const string prefix = "surface_0x";
            if (mtlName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(mtlName.AsSpan(prefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out did);
            const string prefixNohex = "surface_";
            if (mtlName.StartsWith(prefixNohex, StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(mtlName.AsSpan(prefixNohex.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out did);
            return false;
        }

        /// <summary>
        /// Face indices refer into mixed-length OBJ buffers (e.g. many <c>v</c> but no <c>vt</c>).
        /// Copy each referenced corner into parallel lists so position/normal/UV share one index space.
        /// </summary>
        static void FlattenFaceCornersToParallelArrays(
            IReadOnlyList<Vector3> positions,
            IReadOnlyList<Vector3> normals,
            IReadOnlyList<Vector2> texCoords,
            IReadOnlyList<(int a, int b, int c)> faces,
            out List<Vector3> outPos,
            out List<Vector3> outNorm,
            out List<Vector2> outUv,
            out List<(int, int, int)> outFaces) {

            int triCount = faces.Count;
            var pos = new List<Vector3>(triCount * 3);
            var norm = new List<Vector3>(triCount * 3);
            var uv = new List<Vector2>(triCount * 3);
            var facesOut = new List<(int, int, int)>(triCount);
            int next = 0;

            foreach (var (ia, ib, ic) in faces) {
                void AddCorner(int i) {
                    pos.Add(positions[i]);
                    if (i < normals.Count)
                        norm.Add(normals[i]);
                    else
                        norm.Add(Vector3.UnitY);
                    if (i < texCoords.Count)
                        uv.Add(texCoords[i]);
                    else
                        uv.Add(Vector2.Zero);
                }

                AddCorner(ia);
                AddCorner(ib);
                AddCorner(ic);
                facesOut.Add((next, next + 1, next + 2));
                next += 3;
            }

            outPos = pos;
            outNorm = norm;
            outUv = uv;
            outFaces = facesOut;
        }

        /// <summary>
        /// DCC exports often omit <c>vt</c>; portal CSW vertices need UVs. Uses axis-aligned planar mapping on the two largest AABB axes.
        /// </summary>
        static void ApplyPlanarUvIfNeeded(IReadOnlyList<Vector3> positions, IList<Vector2> texCoords, bool fileHadVtLines) {
            if (positions.Count == 0 || texCoords.Count != positions.Count)
                return;

            bool allZero = true;
            for (int i = 0; i < texCoords.Count; i++) {
                if (MathF.Abs(texCoords[i].X) > 1e-8f || MathF.Abs(texCoords[i].Y) > 1e-8f) {
                    allZero = false;
                    break;
                }
            }

            if (fileHadVtLines && !allZero)
                return;

            Vector3 min = positions[0], max = positions[0];
            for (int i = 1; i < positions.Count; i++) {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }

            Vector3 size = max - min;
            var axes = new[] {
                (0, size.X),
                (1, size.Y),
                (2, size.Z),
            };
            Array.Sort(axes, (x, y) => y.Item2.CompareTo(x.Item2));
            int axisU = axes[0].Item1;
            int axisV = axes[1].Item1;
            float spanU = MathF.Max(axes[0].Item2, 1e-8f);
            float spanV = MathF.Max(axes[1].Item2, 1e-8f);
            float minU = GetAxisComponent(min, axisU);
            float minV = GetAxisComponent(min, axisV);

            for (int i = 0; i < positions.Count; i++) {
                float u = (GetAxisComponent(positions[i], axisU) - minU) / spanU;
                float v = (GetAxisComponent(positions[i], axisV) - minV) / spanV;
                texCoords[i] = new Vector2(u, v);
            }
        }

        static float GetAxisComponent(Vector3 p, int axis) =>
            axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

        static int ParseObjIndex(string s, int count) {
            if (string.IsNullOrEmpty(s)) return -1;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                return -1;
            if (idx < 0)
                return count + idx;
            return idx - 1;
        }

    }
}
