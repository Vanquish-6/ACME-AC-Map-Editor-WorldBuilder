using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// Shared doorway picking for Rooms and Connect. Hits the door polygon first,
    /// then falls back to a generous centroid radius so top-down still works.
    /// </summary>
    public static class DungeonPortalPicker {

        public readonly record struct Hit(ushort CellNum, ushort PolyId);

        public static Hit? Pick(DungeonEditingContext ctx, MouseState mouse) {
            var ray = ctx.ComputeRay(mouse);
            if (ray == null || ctx.Scene == null) return null;
            return Pick(ctx.Scene.OpenPortalIndicators, ray.Value.origin, ray.Value.direction);
        }

        public static Hit? Pick(IReadOnlyList<OpenPortalIndicator> doors, Vector3 origin, Vector3 dir) {
            if (doors == null || doors.Count == 0) return null;

            float bestScore = float.MaxValue;
            int best = -1;

            for (int i = 0; i < doors.Count; i++) {
                var door = doors[i];
                if (door.WorldVertices == null || door.WorldVertices.Length < 3) continue;

                if (RayHitsDoor(origin, dir, door, out float t) && t > 0.05f) {
                    if (t < bestScore) {
                        bestScore = t;
                        best = i;
                    }
                    continue;
                }

                var to = door.Centroid - origin;
                float along = Vector3.Dot(to, dir);
                if (along < 0.05f) continue;
                float perp = (origin + dir * along - door.Centroid).Length();
                if (perp > 10f) continue;
                float fallback = 1000f + perp + along * 0.02f;
                if (fallback < bestScore) {
                    bestScore = fallback;
                    best = i;
                }
            }

            return best < 0 ? null : new Hit(doors[best].CellNum, doors[best].PolyId);
        }

        private static bool RayHitsDoor(Vector3 origin, Vector3 dir, OpenPortalIndicator door, out float t) {
            t = float.MaxValue;
            bool hit = RayHitsFan(origin, dir, door.WorldVertices, ref t);

            var n = door.Normal.LengthSquared() > 1e-8f ? Vector3.Normalize(door.Normal) : Vector3.UnitZ;
            var offset = n * 0.45f;
            var shifted = new Vector3[door.WorldVertices.Length];
            for (int i = 0; i < door.WorldVertices.Length; i++)
                shifted[i] = door.WorldVertices[i] + offset;
            hit |= RayHitsFan(origin, dir, shifted, ref t);

            for (int i = 0; i < door.WorldVertices.Length; i++)
                shifted[i] = door.WorldVertices[i] - offset;
            hit |= RayHitsFan(origin, dir, shifted, ref t);

            return hit;
        }

        private static bool RayHitsFan(Vector3 origin, Vector3 dir, Vector3[] verts, ref float bestT) {
            bool any = false;
            for (int i = 1; i < verts.Length - 1; i++) {
                if (RayIntersectsTriangle(origin, dir, verts[0], verts[i], verts[i + 1], out float t)
                    && t > 0.05f && t < bestT) {
                    bestT = t;
                    any = true;
                }
            }
            return any;
        }

        private static bool RayIntersectsTriangle(Vector3 origin, Vector3 dir,
            Vector3 v0, Vector3 v1, Vector3 v2, out float t) {
            t = 0f;
            const float epsilon = 1e-6f;
            var e1 = v1 - v0;
            var e2 = v2 - v0;
            var h = Vector3.Cross(dir, e2);
            float a = Vector3.Dot(e1, h);
            if (a > -epsilon && a < epsilon) return false;
            float f = 1f / a;
            var s = origin - v0;
            float u = f * Vector3.Dot(s, h);
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            float v = f * Vector3.Dot(dir, q);
            if (v < 0f || u + v > 1f) return false;
            t = f * Vector3.Dot(e2, q);
            return t > epsilon;
        }
    }
}
