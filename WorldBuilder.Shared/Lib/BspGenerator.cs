using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace WorldBuilder.Shared.Lib {

    /// <summary>
    /// Builds <see cref="PhysicsBSPTree"/> and <see cref="DrawingBSPTree"/> from a <see cref="GfxObj"/>'s
    /// collision/render polygon sets so that the AC client can use the object for collision and rendering.
    ///
    /// Algorithm
    /// ---------
    /// Standard polygon-plane BSP construction (no polygon splitting — spanning polygons go to
    /// whichever half-space their centroid falls into):
    ///   1. Choose the best splitting plane from the polygon set (score = |front - back|).
    ///   2. Classify each polygon as FRONT, BACK, or COPLANAR by vertex-majority vote.
    ///   3. Recurse on front and back sets; create leaf nodes when depth / size limit reached.
    ///
    /// Physics BSP
    /// -----------
    ///   Inner nodes: Type = BPIN, SplittingPlane, BoundingSphere — no polygon list.
    ///   Leaf nodes:  Type = Leaf, LeafIndex, Solid, BoundingSphere, Polygons.
    ///   The negative half-space of every splitting plane is considered solid; positive is open.
    ///   PhysicsBSP is built from <see cref="GfxObj.PhysicsPolygons"/> when available and
    ///   falls back to <see cref="GfxObj.Polygons"/> only if legacy data does not provide a
    ///   separate collision mesh.
    ///
    /// Drawing BSP
    /// -----------
    ///   Inner nodes: Type = BPIN, SplittingPlane, BoundingSphere, Polygons (coplanar at this node).
    ///   Leaf nodes:  Type = Leaf, LeafIndex only — no geometry at leaves.
    /// </summary>
    public static class BspGenerator {

        const float PlaneEpsilon = 0.0002f;
        const int MaxDepth = 24;

        // ──────────────────────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Generates physics and drawing BSP trees for <paramref name="gfxObj"/>, populates
        /// <see cref="GfxObj.PhysicsPolygons"/>, <see cref="GfxObj.PhysicsBSP"/>,
        /// <see cref="GfxObj.DrawingBSP"/>, and updates <see cref="GfxObj.Flags"/>.
        /// </summary>
        public static void Build(GfxObj gfxObj) {
            ResetBspState(gfxObj);

            if (gfxObj.VertexArray?.Vertices is not { Count: > 0 } verts)
                return;

            var physicsPolys = SelectPhysicsPolygons(gfxObj);
            if (physicsPolys.Count > 0) {
                gfxObj.PhysicsPolygons = new Dictionary<ushort, Polygon>(physicsPolys);
                gfxObj.PhysicsBSP = new PhysicsBSPTree {
                    Root = BuildPhysicsRoot(gfxObj.PhysicsPolygons, verts),
                };
                gfxObj.Flags |= (uint)GfxObjFlags.HasPhysics;
            }

            if (gfxObj.Polygons is { Count: > 0 }) {
                gfxObj.DrawingBSP = new DrawingBSPTree {
                    Root = BuildDrawingRoot(gfxObj.Polygons, verts),
                };
                gfxObj.Flags |= (uint)GfxObjFlags.HasDrawing;
            }
        }

        /// <summary>
        /// Builds conservative retail-safe BSP roots for legacy-converted GfxObjs.
        /// Physics is emitted as a minimal retail-safe BPIN root with two leaf
        /// children. Both children are present so traversal never recurses into a null
        /// branch from the root during placement/collision checks. Drawing uses a
        /// BPOL root containing all render polygons, which round-trips cleanly
        /// through DatReaderWriter.
        /// </summary>
        public static void BuildLegacyRetailSafe(GfxObj gfxObj) {
            ResetBspState(gfxObj);

            if (gfxObj.VertexArray?.Vertices is not { Count: > 0 } verts)
                return;

            var physicsPolys = SelectPhysicsPolygons(gfxObj);
            if (physicsPolys.Count > 0) {
                gfxObj.PhysicsPolygons = new Dictionary<ushort, Polygon>(physicsPolys);
                gfxObj.PhysicsBSP = new PhysicsBSPTree {
                    Root = BuildLegacyPhysicsRootForExport(gfxObj.PhysicsPolygons, verts),
                };
                gfxObj.Flags |= (uint)GfxObjFlags.HasPhysics;
            }

            if (gfxObj.Polygons is { Count: > 0 }) {
                var keys = gfxObj.Polygons.Keys.OrderBy(id => id).ToList();
                gfxObj.DrawingBSP = new DrawingBSPTree {
                    Root = DrawingPolygonNode(keys, gfxObj.Polygons, verts),
                };
                gfxObj.Flags |= (uint)GfxObjFlags.HasDrawing;
            }
        }

        /// <summary>
        /// Builds retail-safe BSP trees for legacy Environment cell structures.
        /// The legacy reader already decodes the authoritative vertex/polygon/portal payloads,
        /// so we rebuild trees from that geometry instead of emitting placeholder roots.
        /// </summary>
        public static void BuildLegacyRetailSafe(CellStruct cellStruct) {
            ResetBspState(cellStruct);

            cellStruct.VertexArray ??= new VertexArray {
                Vertices = new Dictionary<ushort, SWVertex>(),
            };
            cellStruct.Polygons ??= new Dictionary<ushort, Polygon>();
            cellStruct.Portals ??= new List<ushort>();
            cellStruct.PhysicsPolygons ??= new Dictionary<ushort, Polygon>();

            var verts = cellStruct.VertexArray.Vertices ?? new Dictionary<ushort, SWVertex>();
            cellStruct.VertexArray.Vertices = verts;

            var drawPolys = cellStruct.Polygons;
            var physicsPolys = SelectPhysicsPolygons(cellStruct, drawPolys);
            if (cellStruct.PhysicsPolygons.Count == 0 && physicsPolys.Count > 0) {
                cellStruct.PhysicsPolygons = new Dictionary<ushort, Polygon>(physicsPolys);
            }

            cellStruct.CellBSP = new CellBSPTree {
                Root = BuildLegacyCellRootForExport(drawPolys, verts),
            };

            cellStruct.DrawingBSP = new DrawingBSPTree {
                Root = BuildLegacyDrawingRootForExport(drawPolys, verts, BuildPortalRefs(cellStruct, drawPolys)),
            };

            cellStruct.PhysicsBSP = new PhysicsBSPTree {
                Root = BuildLegacyPhysicsRootForExport(physicsPolys, verts),
            };
        }

        private static void ResetBspState(GfxObj gfxObj) {
            gfxObj.Flags &= ~((uint)GfxObjFlags.HasPhysics | (uint)GfxObjFlags.HasDrawing);
            gfxObj.PhysicsBSP = null!;
            gfxObj.DrawingBSP = null!;
        }

        private static void ResetBspState(CellStruct cellStruct) {
            cellStruct.CellBSP = null!;
            cellStruct.PhysicsBSP = null!;
            cellStruct.DrawingBSP = null!;
        }

        private static Dictionary<ushort, Polygon> SelectPhysicsPolygons(GfxObj gfxObj) {
            if (gfxObj.PhysicsPolygons is { Count: > 0 }) {
                return gfxObj.PhysicsPolygons;
            }

            return gfxObj.Polygons ?? new Dictionary<ushort, Polygon>();
        }

        private static Dictionary<ushort, Polygon> SelectPhysicsPolygons(
            CellStruct cellStruct,
            Dictionary<ushort, Polygon> drawPolys) {
            if (cellStruct.PhysicsPolygons is { Count: > 0 }) {
                return cellStruct.PhysicsPolygons;
            }

            return drawPolys;
        }

        internal static PhysicsBSPNode BuildLegacyPhysicsRootForExport(
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts) {
            var keys = polys.Keys.OrderBy(id => id).ToList();
            var planes = BuildPlanes(polys, verts);
            var splitPlane = planes.Values.FirstOrDefault();
            if (splitPlane.Normal.LengthSquared() < 1e-12f) {
                splitPlane = new Plane(Vector3.UnitZ, 0f);
            }

            return new PhysicsBSPNode {
                Type = BSPNodeType.BPIN,
                SplittingPlane = splitPlane,
                LeafIndex = -1,
                BoundingSphere = BoundingSphere(keys, polys, verts),
                Polygons = new List<ushort>(),
                // Duplicate the conservative collision leaf on both sides so
                // root traversal can never hit a null branch and still sees the
                // legacy collision mesh regardless of split-side classification.
                PosNode = PhysicsLeaf(keys, polys, verts, idx: 0, solid: false),
                NegNode = PhysicsLeaf(keys, polys, verts, idx: 1, solid: false),
            };
        }

        internal static CellBSPNode BuildLegacyCellRootForExport(
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts) {
            var planes = BuildPlanes(polys, verts);
            var splitPlane = planes.Values.FirstOrDefault();
            if (splitPlane.Normal.LengthSquared() < 1e-12f) {
                return CellLeaf(0);
            }

            return new CellBSPNode {
                Type = BSPNodeType.BPIN,
                SplittingPlane = splitPlane,
                LeafIndex = -1,
                PosNode = CellLeaf(0),
                NegNode = CellLeaf(1),
            };
        }

        private static CellBSPNode BuildCellRoot(
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts) {
            var keys = polys.Keys.OrderBy(id => id).ToList();
            var planes = BuildPlanes(polys, verts);
            if (keys.Count == 0 || planes.Count == 0) {
                return CellLeaf(0);
            }

            int leafIdx = 0;
            return BuildCell(keys, planes, polys, verts, ref leafIdx, 0);
        }

        private static CellBSPNode CellLeaf(int idx) => new() {
            Type = BSPNodeType.Leaf,
            SplittingPlane = default,
            LeafIndex = idx,
        };

        internal static DrawingBSPNode BuildLegacyDrawingRootForExport(
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts,
            List<PortalRef> portals) {
            var keys = polys.Keys.OrderBy(id => id).ToList();
            if (portals.Count == 0) {
                return DrawingPolygonNode(keys, polys, verts);
            }

            return new DrawingBSPNode {
                Type = BSPNodeType.Portal,
                SplittingPlane = SelectLegacyDrawingPortalSplitPlane(portals, polys, verts),
                LeafIndex = -1,
                BoundingSphere = BoundingSphere(keys, polys, verts),
                Polygons = new List<ushort>(keys),
                Portals = new List<PortalRef>(portals),
                PosNode = DrawingLeaf(0),
                NegNode = DrawingLeaf(1),
            };
        }

        internal static List<PortalRef> BuildPortalRefs(
            CellStruct cellStruct,
            Dictionary<ushort, Polygon> polys) {
            var refs = new List<PortalRef>();
            if (cellStruct.Portals == null || cellStruct.Portals.Count == 0) {
                return refs;
            }

            ushort[] sortedPolyKeys = polys.Keys.OrderBy(id => id).ToArray();
            for (ushort portalIndex = 0; portalIndex < cellStruct.Portals.Count; portalIndex++) {
                ushort polyId = cellStruct.Portals[portalIndex];
                if (!polys.ContainsKey(polyId) && polyId < sortedPolyKeys.Length) {
                    polyId = sortedPolyKeys[polyId];
                }

                if (!polys.ContainsKey(polyId)) {
                    continue;
                }

                refs.Add(new PortalRef {
                    PolyId = polyId,
                    PortalIndex = portalIndex,
                });
            }

            return refs;
        }

        private static DrawingBSPNode DrawingLeaf(int idx) => new() {
            Type = BSPNodeType.Leaf,
            LeafIndex = idx,
            Polygons = new List<ushort>(),
            Portals = new List<PortalRef>(),
        };

        private static Plane SelectLegacyDrawingPortalSplitPlane(
            List<PortalRef> portals,
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts) {
            var planes = BuildPlanes(polys, verts);
            foreach (var portal in portals) {
                if (planes.TryGetValue(portal.PolyId, out var portalPlane)
                    && portalPlane.Normal.LengthSquared() >= 1e-12f) {
                    return portalPlane;
                }
            }

            var splitPlane = planes.Values.FirstOrDefault();
            if (splitPlane.Normal.LengthSquared() < 1e-12f) {
                splitPlane = new Plane(Vector3.UnitZ, 0f);
            }

            return splitPlane;
        }

        private static PhysicsBSPNode BuildPhysicsRoot(
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts) {
            var keys = polys.Keys.OrderBy(id => id).ToList();
            var planes = BuildPlanes(polys, verts);
            if (keys.Count == 0 || planes.Count == 0) {
                return PhysicsLeaf(keys, polys, verts, idx: 0, solid: false);
            }

            int leafIdx = 0;
            return BuildPhysics(keys, planes, polys, verts, ref leafIdx, 0);
        }

        private static DrawingBSPNode BuildDrawingRoot(
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts) {
            var keys = polys.Keys.OrderBy(id => id).ToList();
            var planes = BuildPlanes(polys, verts);
            if (keys.Count == 0 || planes.Count == 0) {
                return DrawingPolygonNode(keys, polys, verts);
            }

            int leafIdx = 0;
            return BuildDrawing(keys, planes, polys, verts, ref leafIdx, 0);
        }

        private static Dictionary<ushort, Plane> BuildPlanes(
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts) {
            var planes = new Dictionary<ushort, Plane>();
            foreach (var (key, poly) in polys) {
                if (TryComputePolyPlane(poly, verts, out var plane)) {
                    planes[key] = plane;
                }
            }

            return planes;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Physics BSP
        // ──────────────────────────────────────────────────────────────────────

        static PhysicsBSPNode BuildPhysics(
            List<ushort> keys,
            Dictionary<ushort, Plane> planes,
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts,
            ref int leafIdx,
            int depth) {

            if (keys.Count == 0 || depth >= MaxDepth)
                return PhysicsLeaf(keys, polys, verts, leafIdx++, solid: false);

            int si = ChooseSplitter(keys, planes, polys, verts);
            if (si < 0)
                return PhysicsLeaf(keys, polys, verts, leafIdx++, solid: false);

            Plane splitPlane = planes[keys[si]];

            var front = new List<ushort>();
            var back  = new List<ushort>();
            foreach (var k in keys) {
                switch (Classify(k, polys[k], splitPlane, verts)) {
                    case Side.Front:    front.Add(k); break;
                    default:            back.Add(k);  break; // coplanar → solid side
                }
            }

            // Guard: if all polygons land on the same side we have a degenerate split → leaf
            if (front.Count == 0 || back.Count == 0)
                return PhysicsLeaf(keys, polys, verts, leafIdx++, solid: false);

            return new PhysicsBSPNode {
                Type           = BSPNodeType.BPIN,
                SplittingPlane = splitPlane,
                LeafIndex      = -1,
                BoundingSphere = BoundingSphere(keys, polys, verts),
                Polygons       = new List<ushort>(),
                PosNode        = BuildPhysics(front, planes, polys, verts, ref leafIdx, depth + 1),
                NegNode        = BuildPhysics(back,  planes, polys, verts, ref leafIdx, depth + 1),
            };
        }

        static PhysicsBSPNode PhysicsLeaf(
            List<ushort> keys,
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts,
            int idx,
            bool solid) {
            var node = new PhysicsBSPNode {
                Type           = BSPNodeType.Leaf,
                SplittingPlane = default,
                LeafIndex      = idx,
                BoundingSphere = BoundingSphere(keys, polys, verts),
                Polygons       = new List<ushort>(keys),
            };
            SetSolid(node, solid ? 1 : 0);
            return node;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Cell BSP
        // ──────────────────────────────────────────────────────────────────────

        static CellBSPNode BuildCell(
            List<ushort> keys,
            Dictionary<ushort, Plane> planes,
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts,
            ref int leafIdx,
            int depth) {

            if (keys.Count == 0 || depth >= MaxDepth)
                return CellLeaf(leafIdx++);

            int si = ChooseSplitter(keys, planes, polys, verts);
            if (si < 0)
                return CellLeaf(leafIdx++);

            Plane splitPlane = planes[keys[si]];

            var front = new List<ushort>();
            var back = new List<ushort>();
            foreach (var k in keys) {
                switch (Classify(k, polys[k], splitPlane, verts)) {
                    case Side.Front: front.Add(k); break;
                    default: back.Add(k); break;
                }
            }

            if (front.Count == 0 || back.Count == 0)
                return CellLeaf(leafIdx++);

            return new CellBSPNode {
                Type = BSPNodeType.BPIN,
                SplittingPlane = splitPlane,
                LeafIndex = -1,
                PosNode = BuildCell(front, planes, polys, verts, ref leafIdx, depth + 1),
                NegNode = BuildCell(back, planes, polys, verts, ref leafIdx, depth + 1),
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        // Drawing BSP
        // ──────────────────────────────────────────────────────────────────────

        static DrawingBSPNode BuildDrawing(
            List<ushort> keys,
            Dictionary<ushort, Plane> planes,
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts,
            ref int leafIdx,
            int depth) {

            if (keys.Count == 0 || depth >= MaxDepth)
                return DrawingPolygonNode(keys, polys, verts);

            int si = ChooseSplitter(keys, planes, polys, verts);
            if (si < 0)
                return DrawingPolygonNode(keys, polys, verts);

            Plane splitPlane = planes[keys[si]];

            var front    = new List<ushort>();
            var back     = new List<ushort>();
            var coplanar = new List<ushort>();
            foreach (var k in keys) {
                switch (Classify(k, polys[k], splitPlane, verts)) {
                    case Side.Front:    front.Add(k);    break;
                    case Side.Coplanar: coplanar.Add(k); break;
                    default:            back.Add(k);     break;
                }
            }

            if (front.Count == 0 || back.Count == 0)
                return DrawingPolygonNode(keys, polys, verts);

            return new DrawingBSPNode {
                Type           = BSPNodeType.BPIN,
                SplittingPlane = splitPlane,
                LeafIndex      = -1,
                BoundingSphere = BoundingSphere(keys, polys, verts),
                Polygons       = coplanar,
                Portals        = new List<PortalRef>(),
                PosNode        = BuildDrawing(front, planes, polys, verts, ref leafIdx, depth + 1),
                NegNode        = BuildDrawing(back,  planes, polys, verts, ref leafIdx, depth + 1),
            };
        }

        internal static DrawingBSPNode DrawingPolygonNode(
            List<ushort> keys,
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts,
            List<PortalRef>? portals = null) => new DrawingBSPNode {
            Type           = BSPNodeType.BPOL,
            SplittingPlane = default,
            LeafIndex      = -1,
            BoundingSphere = BoundingSphere(keys, polys, verts),
            Polygons       = new List<ushort>(keys),
            Portals        = portals ?? new List<PortalRef>(),
        };

        // ──────────────────────────────────────────────────────────────────────
        // Splitter selection — minimise |front − back| over a candidate sample
        // ──────────────────────────────────────────────────────────────────────

        enum Side { Front, Back, Coplanar }

        static int ChooseSplitter(
            List<ushort> keys,
            Dictionary<ushort, Plane> planes,
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts) {

            int best = int.MaxValue;
            int bestIdx = -1;
            int step = Math.Max(1, keys.Count / 16);

            for (int i = 0; i < keys.Count; i += step) {
                if (!planes.TryGetValue(keys[i], out var splitPlane)) {
                    continue;
                }
                int f = 0, b = 0;
                foreach (var k in keys) {
                    switch (Classify(k, polys[k], splitPlane, verts)) {
                        case Side.Front: f++; break;
                        case Side.Back:  b++; break;
                    }
                }
                int score = Math.Abs(f - b);
                if (score < best && f > 0 && b > 0) { best = score; bestIdx = i; }
            }
            return bestIdx;
        }

        // Classifies polygon `poly` relative to `splitPlane` by vertex majority.
        static Side Classify(ushort key, Polygon poly, Plane splitPlane, Dictionary<ushort, SWVertex> verts) {
            int f = 0, b = 0;
            foreach (var rawId in poly.VertexIds) {
                if (rawId < 0) continue;
                if (!verts.TryGetValue((ushort)rawId, out var v)) continue;
                float d = Vector3.Dot(splitPlane.Normal, v.Origin) + splitPlane.D;
                if (d > PlaneEpsilon) f++;
                else if (d < -PlaneEpsilon) b++;
            }
            if (f > 0 && b == 0) return Side.Front;
            if (b > 0 && f == 0) return Side.Back;
            if (f == 0 && b == 0) return Side.Coplanar;
            // Spanning polygon: assign to whichever side has more vertices
            return f >= b ? Side.Front : Side.Back;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Geometry helpers
        // ──────────────────────────────────────────────────────────────────────

        static bool TryComputePolyPlane(Polygon poly, Dictionary<ushort, SWVertex> verts, out Plane plane) {
            plane = default;
            Vector3? v0 = null, v1 = null, v2 = null;
            foreach (var raw in poly.VertexIds) {
                if (raw < 0) continue;
                if (!verts.TryGetValue((ushort)raw, out var sv)) continue;
                if (v0 == null)      { v0 = sv.Origin; continue; }
                else if (v1 == null) { v1 = sv.Origin; continue; }
                else                 { v2 = sv.Origin; break; }
            }
            if (v0 == null || v1 == null || v2 == null) return false;
            var n = Vector3.Cross(v1.Value - v0.Value, v2.Value - v0.Value);
            if (n.LengthSquared() < 1e-12f) return false;
            n = Vector3.Normalize(n);
            plane = new Plane(n, -Vector3.Dot(n, v0.Value));
            return true;
        }

        static Sphere BoundingSphere(
            List<ushort> keys,
            Dictionary<ushort, Polygon> polys,
            Dictionary<ushort, SWVertex> verts) {

            if (keys.Count == 0)
                return new Sphere { Origin = Vector3.Zero, Radius = 0.01f };

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            bool any = false;

            foreach (var k in keys) {
                if (!polys.TryGetValue(k, out var poly)) continue;
                foreach (var raw in poly.VertexIds) {
                    if (raw < 0) continue;
                    if (!verts.TryGetValue((ushort)raw, out var v)) continue;
                    min = Vector3.Min(min, v.Origin);
                    max = Vector3.Max(max, v.Origin);
                    any = true;
                }
            }

            if (!any) return new Sphere { Origin = Vector3.Zero, Radius = 0.01f };
            var center = (min + max) * 0.5f;
            float r = 0f;
            foreach (var k in keys) {
                if (!polys.TryGetValue(k, out var poly)) continue;
                foreach (var raw in poly.VertexIds) {
                    if (raw < 0) continue;
                    if (!verts.TryGetValue((ushort)raw, out var v)) continue;
                    r = MathF.Max(r, Vector3.Distance(center, v.Origin));
                }
            }
            return new Sphere { Origin = center, Radius = MathF.Max(r, 0.01f) };
        }

        // ──────────────────────────────────────────────────────────────────────
        // Solid flag — property is read-only; set backing field via reflection
        // ──────────────────────────────────────────────────────────────────────

        static readonly FieldInfo? _solidField =
            typeof(PhysicsBSPNode).GetField("_solid",               BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(PhysicsBSPNode).GetField("solid",             BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(PhysicsBSPNode).GetField("<Solid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

        static void SetSolid(PhysicsBSPNode node, int value) =>
            _solidField?.SetValue(node, value);

        internal static void SetPhysicsSolid(PhysicsBSPNode node, int value) => SetSolid(node, value);
    }
}
