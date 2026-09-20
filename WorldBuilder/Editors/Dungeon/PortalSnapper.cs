using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace WorldBuilder.Editors.Dungeon {
    /// <summary>
    /// Computes the world transform for a new cell so that one of its portal
    /// polygons aligns flush with a portal polygon on an existing cell.
    /// </summary>
    public static class PortalSnapper {

        public struct PortalGeometry {
            public Vector3 Centroid;
            public Vector3 Normal;
            public List<Vector3> Vertices;
        }

        /// <summary>
        /// Extract portal polygon geometry (centroid + normal) from a CellStruct in local space.
        /// </summary>
        public static PortalGeometry? GetPortalGeometry(CellStruct cellStruct, ushort polygonId) {
            if (!cellStruct.Polygons.TryGetValue(polygonId, out var poly))
                return null;
            if (poly.VertexIds.Count < 3) return null;

            var verts = new List<Vector3>();
            foreach (var vid in poly.VertexIds) {
                if (cellStruct.VertexArray.Vertices.TryGetValue((ushort)vid, out var vtx))
                    verts.Add(vtx.Origin);
            }
            if (verts.Count < 3) return null;

            var centroid = Vector3.Zero;
            foreach (var v in verts) centroid += v;
            centroid /= verts.Count;

            var edge1 = verts[1] - verts[0];
            var edge2 = verts[2] - verts[0];
            var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));

            return new PortalGeometry {
                Centroid = centroid,
                Normal = normal,
                Vertices = verts
            };
        }

        /// <summary>
        /// Find all portal polygon IDs in a CellStruct using the structural Portals list
        /// (corresponds to CCellStruct::portals in the AC client).
        /// </summary>
        /// <remarks>
        /// In the AC client, CCellStruct::portals[] stores CPolygon* pointers resolved
        /// from indices during UnPack. DatReaderWriter's CellStruct.Portals stores
        /// ushort values that may be either polygon IDs (if already resolved) or raw
        /// indices into the polygon array. We handle both cases.
        /// </remarks>
        public static List<ushort> GetPortalPolygonIds(CellStruct cellStruct) {
            if (cellStruct.Portals == null || cellStruct.Portals.Count == 0)
                return new List<ushort>();

            // Pre-build a sorted key list for index-based lookup so we don't
            // depend on Dictionary insertion order.
            ushort[]? sortedPolyKeys = null;

            var result = new List<ushort>(cellStruct.Portals.Count);
            var seen = new HashSet<ushort>();
            foreach (var portalRef in cellStruct.Portals) {
                ushort resolvedId = portalRef;

                if (!cellStruct.Polygons.ContainsKey(resolvedId)) {
                    // portalRef is a raw index into the polygon array.
                    // Build sorted key list on first use.
                    if (sortedPolyKeys == null) {
                        sortedPolyKeys = cellStruct.Polygons.Keys.OrderBy(k => k).ToArray();
                    }
                    int idx = portalRef;
                    if (idx >= 0 && idx < sortedPolyKeys.Length) {
                        resolvedId = sortedPolyKeys[idx];
                    } else {
                        continue;
                    }
                }

                if (seen.Add(resolvedId))
                    result.Add(resolvedId);
            }

            return result;
        }

        /// <summary>
        /// Compute the world transform (origin + orientation) for a new cell so that
        /// its sourcePortal aligns with the targetPortal on an existing cell.
        ///
        /// The portals will face each other (normals opposite), centers aligned,
        /// and edge directions matched so the openings overlap properly.
        /// </summary>
        /// <param name="targetCentroidWorld">Target portal centroid in world space.</param>
        /// <param name="targetNormalWorld">Target portal normal in world space (pointing outward from existing cell).</param>
        /// <param name="sourceGeometryLocal">Source portal geometry in local space of the new cell.</param>
        /// <param name="targetGeometryWorld">Optional target portal geometry in world space for twist alignment.</param>
        /// <returns>Origin and orientation for the new cell in world space.</returns>
        public static (Vector3 origin, Quaternion orientation) ComputeSnapTransform(
            Vector3 targetCentroidWorld,
            Vector3 targetNormalWorld,
            PortalGeometry sourceGeometryLocal,
            PortalGeometry? targetGeometryWorld = null) {

            var desiredSourceNormalWorld = -targetNormalWorld;

            // Step 1: Align normals so the portals face each other
            var normalRotation = RotationBetween(sourceGeometryLocal.Normal, desiredSourceNormalWorld);

            // Step 2: Align the "up" edges of the two portals to eliminate twist.
            // Compute an up-like vector perpendicular to the normal from each portal's
            // vertex span. Without this, portals can be rotated 90/180 degrees.
            var rotation = normalRotation;
            if (targetGeometryWorld != null && targetGeometryWorld.Value.Vertices.Count >= 2 &&
                sourceGeometryLocal.Vertices.Count >= 2) {

                var targetUp = ComputePortalUp(targetGeometryWorld.Value.Vertices, targetNormalWorld);
                var sourceUp = ComputePortalUp(sourceGeometryLocal.Vertices, sourceGeometryLocal.Normal);
                var rotatedSourceUp = Vector3.Transform(sourceUp, normalRotation);

                // The source's up (after normal alignment) should match the target's up.
                // The portals face each other, so one is mirrored — we want the up vectors
                // to be parallel (same direction) for proper alignment.
                if (targetUp.LengthSquared() > 0.01f && rotatedSourceUp.LengthSquared() > 0.01f) {
                    targetUp = Vector3.Normalize(targetUp);
                    rotatedSourceUp = Vector3.Normalize(rotatedSourceUp);
                    var twistRotation = RotationBetween(rotatedSourceUp, targetUp);
                    rotation = Quaternion.Normalize(twistRotation * normalRotation);
                }
            }

            var rotatedSourceCentroid = Vector3.Transform(sourceGeometryLocal.Centroid, rotation);
            var origin = targetCentroidWorld - rotatedSourceCentroid;

            return (origin, rotation);
        }

        /// <summary>
        /// Place a cell so its source portal sits flush on the target doorway:
        /// centroids coincide, wall doors stay level on the 90° yaw grid, and the
        /// openings face each other. Stops pitch/roll drift that leaves a lip in the
        /// opening (black void in walk view, uneven frame in the client).
        /// </summary>
        public static (Vector3 origin, Quaternion orientation) ComputeFlushSnap(
            Vector3 targetCentroidWorld,
            Vector3 targetNormalWorld,
            PortalGeometry sourceLocal,
            Quaternion? orientationHint = null) {

            var targetN = targetNormalWorld.LengthSquared() > 1e-8f
                ? Vector3.Normalize(targetNormalWorld)
                : Vector3.UnitY;
            bool wall = MathF.Abs(targetN.Z) < 0.55f;

            Quaternion rot;
            if (wall) {
                var flatTarget = new Vector3(targetN.X, targetN.Y, 0f);
                if (flatTarget.LengthSquared() < 1e-8f)
                    flatTarget = Vector3.UnitY;
                else
                    flatTarget = Vector3.Normalize(flatTarget);

                if (orientationHint.HasValue) {
                    var hinted = ConstrainToYaw(orientationHint.Value);
                    var hintedN = sourceLocal.Normal.LengthSquared() > 1e-8f
                        ? Vector3.Normalize(Vector3.Transform(sourceLocal.Normal, hinted))
                        : Vector3.UnitY;
                    rot = Vector3.Dot(hintedN, flatTarget) < -0.35f
                        ? hinted
                        : SnapYawToOppose(sourceLocal.Normal, flatTarget, orientationHint);
                }
                else {
                    rot = SnapYawToOppose(sourceLocal.Normal, flatTarget, null);
                }
            }
            else if (orientationHint.HasValue) {
                rot = Quaternion.Normalize(orientationHint.Value);
            }
            else {
                (_, rot) = ComputeSnapTransform(targetCentroidWorld, targetN, sourceLocal);
            }

            var origin = targetCentroidWorld - Vector3.Transform(sourceLocal.Centroid, rot);
            return (origin, rot);
        }

        /// <summary>
        /// Choose the 0/90/180/270 yaw whose source portal normal most opposes the target.
        /// </summary>
        public static Quaternion SnapYawToOppose(
            Vector3 sourceNormalLocal,
            Vector3 targetNormalWorld,
            Quaternion? hint = null) {

            int hinted = 0;
            if (hint.HasValue) {
                var yawOnly = ConstrainToYaw(hint.Value);
                float angle = 2f * MathF.Atan2(yawOnly.Z, yawOnly.W);
                hinted = ((int)MathF.Round(angle / (MathF.PI * 0.5f))) & 3;
            }

            var targetN = targetNormalWorld.LengthSquared() > 1e-8f
                ? Vector3.Normalize(targetNormalWorld)
                : Vector3.UnitY;
            Quaternion best = YawQuarter(hinted);
            float bestDot = float.MaxValue;
            for (int i = 0; i < 4; i++) {
                var candidate = YawQuarter(i);
                var rotated = sourceNormalLocal.LengthSquared() > 1e-8f
                    ? Vector3.Normalize(Vector3.Transform(sourceNormalLocal, candidate))
                    : Vector3.UnitY;
                float dot = Vector3.Dot(rotated, targetN);
                if (i == hinted)
                    dot -= 0.0001f;
                if (dot < bestDot) {
                    bestDot = dot;
                    best = candidate;
                }
            }
            return best;
        }

        /// <summary>World-space gap between two portal centroids after each cell's transform.</summary>
        public static float CentroidGap(
            PortalGeometry aLocal, Vector3 aOrigin, Quaternion aRot,
            PortalGeometry bLocal, Vector3 bOrigin, Quaternion bRot) {
            var (ca, _) = TransformPortalToWorld(aLocal, aOrigin, aRot);
            var (cb, _) = TransformPortalToWorld(bLocal, bOrigin, bRot);
            return Vector3.Distance(ca, cb);
        }

        /// <summary>
        /// Compute a consistent "up" direction from a portal polygon's vertices.
        /// 
        /// The AC client's portal geometry defines a polygon where edges form the
        /// doorframe. We find the edge most aligned with world-Z (the vertical edge
        /// of the doorway) and use its direction as "up". For floor/ceiling portals
        /// where no edge has a Z component, we fall back to cross-product with world axes.
        /// 
        /// This replaces the previous Z-span approach which picked the wrong direction
        /// when vertices at min/max Z weren't on the same edge (e.g. diamond-shaped portals).
        /// </summary>
        private static Vector3 ComputePortalUp(List<Vector3> vertices, Vector3 normal) {
            if (vertices.Count < 2) return Vector3.UnitZ;

            var n = Vector3.Normalize(normal);

            // Find the polygon edge most aligned with world-Z (vertical).
            // For a standard wall portal, the vertical edges of the doorframe
            // define the "up" direction. Using the edge rather than vertex-span
            // ensures we get a direction that follows actual geometry.
            Vector3 bestEdge = Vector3.Zero;
            float bestZAlignment = -1f;
            for (int i = 0; i < vertices.Count; i++) {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertices.Count];
                var edge = b - a;
                float edgeLen = edge.Length();
                if (edgeLen < 0.01f) continue;

                var edgeDir = edge / edgeLen;
                float zAlign = MathF.Abs(edgeDir.Z);
                if (zAlign > bestZAlignment) {
                    bestZAlignment = zAlign;
                    // Ensure consistent direction: always point upward (+Z)
                    bestEdge = edgeDir.Z >= 0 ? edge : -edge;
                }
            }

            Vector3 up;
            if (bestZAlignment > 0.1f) {
                up = bestEdge;
            } else {
                // Flat portal (floor/ceiling): no edge has significant Z.
                // Use cross product with a world axis for a consistent reference.
                up = Vector3.Cross(n, Vector3.UnitX);
                if (up.LengthSquared() < 0.01f)
                    up = Vector3.Cross(n, Vector3.UnitY);
            }

            // Project out normal component to stay in the portal plane
            up -= Vector3.Dot(up, n) * n;
            return up.LengthSquared() > 0.001f ? Vector3.Normalize(up) : Vector3.UnitZ;
        }

        /// <summary>
        /// Transform a portal's local-space geometry to world space given a cell's transform.
        /// </summary>
        public static (Vector3 centroid, Vector3 normal) TransformPortalToWorld(
            PortalGeometry localGeometry, Vector3 cellOrigin, Quaternion cellOrientation) {

            var worldCentroid = Vector3.Transform(localGeometry.Centroid, cellOrientation) + cellOrigin;
            var worldNormal = Vector3.Normalize(Vector3.Transform(localGeometry.Normal, cellOrientation));
            return (worldCentroid, worldNormal);
        }

        /// <summary>Portal geometry with vertices in world space, for twist alignment.</summary>
        public static PortalGeometry TransformGeometryToWorld(
            PortalGeometry local, Vector3 cellOrigin, Quaternion cellOrientation) {
            var verts = new List<Vector3>(local.Vertices.Count);
            foreach (var v in local.Vertices)
                verts.Add(Vector3.Transform(v, cellOrientation) + cellOrigin);
            var (c, n) = TransformPortalToWorld(local, cellOrigin, cellOrientation);
            return new PortalGeometry { Centroid = c, Normal = n, Vertices = verts };
        }

        /// <summary>
        /// Twist a snap result around the target portal normal so the user can try 90° turns
        /// without breaking the doorway alignment.
        /// </summary>
        public static (Vector3 origin, Quaternion orientation) ApplyPortalTwist(
            Vector3 targetCentroidWorld, Vector3 targetNormalWorld,
            Vector3 sourceCentroidLocal, Quaternion orientation, int quarterTurns) {
            int q = ((quarterTurns % 4) + 4) % 4;
            if (q == 0) {
                var rotated = Vector3.Transform(sourceCentroidLocal, orientation);
                return (targetCentroidWorld - rotated, orientation);
            }
            var twist = Quaternion.CreateFromAxisAngle(Vector3.Normalize(targetNormalWorld), q * (MathF.PI * 0.5f));
            var newOri = Quaternion.Normalize(twist * orientation);
            var newOrigin = targetCentroidWorld - Vector3.Transform(sourceCentroidLocal, newOri);
            return (newOrigin, newOri);
        }

        /// <summary>
        /// Pick the source portal that best aligns with the target (normals opposite).
        /// Returns the portal ID whose normal is closest to -targetNormalWorld.
        /// </summary>
        public static ushort? PickBestSourcePortal(CellStruct sourceCellStruct, Vector3 targetNormalWorld) {
            var portalIds = GetPortalPolygonIds(sourceCellStruct);
            if (portalIds.Count == 0) return null;
            if (portalIds.Count == 1) return portalIds[0];

            var desiredSourceNormal = -Vector3.Normalize(targetNormalWorld);
            ushort? best = null;
            float bestDot = float.MinValue;

            foreach (var pid in portalIds) {
                var geom = GetPortalGeometry(sourceCellStruct, pid);
                if (geom == null) continue;
                float dot = Vector3.Dot(Vector3.Normalize(geom.Value.Normal), desiredSourceNormal);
                if (dot > bestDot) {
                    bestDot = dot;
                    best = pid;
                }
            }
            return best ?? portalIds[0];
        }

        public static Quaternion ConstrainToYaw(Quaternion q) {
            float len = MathF.Sqrt(q.Z * q.Z + q.W * q.W);
            if (len < 0.001f) return Quaternion.Identity;
            return new Quaternion(0, 0, q.Z / len, q.W / len);
        }

        public static Quaternion YawQuarter(int quarter) {
            int q = ((quarter % 4) + 4) % 4;
            return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, q * (MathF.PI * 0.5f));
        }

        /// <summary>
        /// Compute the shortest rotation quaternion that rotates vector 'from' to vector 'to'.
        /// </summary>
        private static Quaternion RotationBetween(Vector3 from, Vector3 to) {
            from = Vector3.Normalize(from);
            to = Vector3.Normalize(to);

            float dot = Vector3.Dot(from, to);

            if (dot > 0.999999f) {
                return Quaternion.Identity;
            }

            if (dot < -0.999999f) {
                // 180-degree rotation: pick an arbitrary perpendicular axis
                var perp = Math.Abs(from.X) < 0.9f
                    ? Vector3.Cross(Vector3.UnitX, from)
                    : Vector3.Cross(Vector3.UnitY, from);
                perp = Vector3.Normalize(perp);
                return Quaternion.CreateFromAxisAngle(perp, MathF.PI);
            }

            var axis = Vector3.Cross(from, to);
            float w = 1.0f + dot;
            return Quaternion.Normalize(new Quaternion(axis.X, axis.Y, axis.Z, w));
        }
    }
}
