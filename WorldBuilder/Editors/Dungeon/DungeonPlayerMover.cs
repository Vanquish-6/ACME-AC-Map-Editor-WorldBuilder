using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// First-person walk: stay on the floor inside a cell, slide on walls, pass through open doorways.
    /// </summary>
    public static class DungeonPlayerMover {
        public const float EyeHeight = 1.85f;
        public const float Radius = 0.38f;
        public const float WalkSpeed = 7f;
        public const float RunSpeed = 14f;

        public static bool TrySpawn(
            EnvCellManager ecm,
            IReadOnlyList<LoadedEnvCell> cells,
            LoadedEnvCell? preferred,
            out Vector3 eye) {

            eye = default;
            if (cells == null || cells.Count == 0) return false;

            if (preferred != null && TrySpawnInCell(ecm, preferred, out eye))
                return true;

            foreach (var cell in cells) {
                if (preferred != null && ReferenceEquals(cell, preferred)) continue;
                if (TrySpawnInCell(ecm, cell, out eye))
                    return true;
            }

            return false;
        }

        private static bool TrySpawnInCell(EnvCellManager ecm, LoadedEnvCell cell, out Vector3 eye) {
            eye = default;
            if (cell.LocalBoundsMax.X <= cell.LocalBoundsMin.X)
                return false;

            var min = cell.LocalBoundsMin;
            var max = cell.LocalBoundsMax;
            float height = max.Z - min.Z;
            float localEyeZ = min.Z + EyeHeight;
            if (height > 0.8f)
                localEyeZ = Math.Clamp(localEyeZ, min.Z + 0.5f, max.Z - 0.35f);
            var localEye = new Vector3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, localEyeZ);
            var interior = Vector3.Transform(localEye, cell.WorldTransform);

            // Snap down onto a real floor only if that hit is the bottom of THIS cell.
            // Roof triangles also face up; a ray from the editor fly cam would land on them.
            var probe = interior;
            var hit = ecm.RaycastSurface(probe, -Vector3.UnitZ);
            if (hit.Hit && hit.HitNormal.Z > 0.35f && hit.Distance < height + 1f && hit.Distance < 8f) {
                var localHit = Vector3.Transform(hit.HitPosition, cell.InverseWorldTransform);
                float fromFloor = localHit.Z - min.Z;
                if (fromFloor >= -0.35f && (height < 0.5f || fromFloor <= height * 0.4f)
                    && EnvCellManager.PointInCell(hit.HitPosition + new Vector3(0f, 0f, 0.2f), cell)) {
                    var snapped = hit.HitPosition + new Vector3(0f, 0f, EyeHeight);
                    if (EnvCellManager.PointInCell(snapped, cell)) {
                        eye = snapped;
                        return true;
                    }
                }
            }

            if (EnvCellManager.PointInCell(interior, cell)) {
                eye = interior;
                return true;
            }

            var originEye = cell.WorldPosition + new Vector3(0f, 0f, EyeHeight);
            if (EnvCellManager.PointInCell(originEye, cell)) {
                eye = originEye;
                return true;
            }

            return false;
        }

        public static Vector3 Step(
            EnvCellManager ecm,
            Vector3 eye,
            Vector3 wishHorizontal,
            float deltaTime,
            bool running) {

            wishHorizontal.Z = 0f;
            if (wishHorizontal.LengthSquared() > 1e-8f)
                wishHorizontal = Vector3.Normalize(wishHorizontal);

            float speed = running ? RunSpeed : WalkSpeed;
            var step = wishHorizontal * speed * MathF.Max(deltaTime, 0f);

            var hip = eye - new Vector3(0f, 0f, EyeHeight * 0.45f);
            for (int i = 0; i < 2; i++) {
                float dist = step.Length();
                if (dist < 1e-4f) break;
                var dir = step / dist;
                var hit = ecm.RaycastSurface(hip, dir);
                if (!hit.Hit || hit.Distance > dist + Radius) break;
                if (MathF.Abs(hit.HitNormal.Z) > 0.72f) break;
                var n = hit.HitNormal;
                n.Z = 0f;
                if (n.LengthSquared() < 1e-8f) {
                    step = Vector3.Zero;
                    break;
                }
                n = Vector3.Normalize(n);
                step -= n * Vector3.Dot(step, n);
            }

            var proposed = new Vector3(eye.X + step.X, eye.Y + step.Y, eye.Z);
            var interiorCell = ecm.FindCameraCell(eye);
            // Stay inside the dungeon. The editor fly cam sits on the roof; do not
            // keep walking if we are not in a cell.
            if (interiorCell == null)
                return eye;
            var floorProbe = proposed + new Vector3(0f, 0f, 0.6f);
            if (!TryFindFloor(ecm, floorProbe, EyeHeight + 2.5f, interiorCell, out var floorPos))
                return eye;

            proposed.Z = floorPos.Z + EyeHeight;

            var ceiling = ecm.RaycastSurface(proposed - new Vector3(0f, 0f, 0.15f), Vector3.UnitZ);
            if (ceiling.Hit && ceiling.Distance < 0.25f)
                return eye;

            return proposed;
        }

        public static Vector3 FlattenLook(PerspectiveCamera camera) {
            var forward = camera.Front;
            forward.Z = 0f;
            if (forward.LengthSquared() < 1e-6f) {
                float yaw = MathHelper.DegreesToRadians(camera.Yaw);
                forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
            }
            return Vector3.Normalize(forward);
        }

        public static Vector3 FlattenRight(PerspectiveCamera camera, Vector3 forward) {
            var right = camera.Right;
            right.Z = 0f;
            if (right.LengthSquared() < 1e-6f)
                right = new Vector3(forward.Y, -forward.X, 0f);
            return Vector3.Normalize(right);
        }

        private static bool TryFindFloor(
            EnvCellManager ecm, Vector3 from, float maxDrop, LoadedEnvCell? stayInside, out Vector3 floor) {
            floor = default;
            var hit = ecm.RaycastSurface(from, -Vector3.UnitZ);
            if (hit.Hit && hit.Distance <= maxDrop && hit.HitNormal.Z > 0.35f) {
                var test = hit.HitPosition + new Vector3(0f, 0f, 0.15f);
                if (stayInside != null) {
                    var local = Vector3.Transform(hit.HitPosition, stayInside.InverseWorldTransform);
                    float zSpan = stayInside.LocalBoundsMax.Z - stayInside.LocalBoundsMin.Z;
                    float fromFloor = local.Z - stayInside.LocalBoundsMin.Z;
                    if (zSpan > 0.8f && fromFloor > zSpan * 0.45f)
                        return false;
                    if (!EnvCellManager.PointInCell(test, stayInside)
                        && ecm.FindCameraCell(hit.HitPosition + new Vector3(0f, 0f, 0.4f)) == null)
                        return false;
                }
                else if (ecm.FindCameraCell(hit.HitPosition + new Vector3(0f, 0f, 0.4f)) == null) {
                    return false;
                }
                floor = hit.HitPosition;
                return true;
            }
            floor = default;
            return false;
        }
    }
}
