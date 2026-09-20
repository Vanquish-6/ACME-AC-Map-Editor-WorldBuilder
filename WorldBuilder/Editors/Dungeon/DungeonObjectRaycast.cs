using System;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public static class DungeonObjectRaycast {
        public struct DungeonObjectHit {
            public bool Hit;
            public ushort CellNumber;
            public int ObjectIndex;
            public uint ObjectId;
            public float Distance;
            public Vector3 HitPosition;
            public Vector3 WorldOrigin;

            /// <summary>True when the hit is a DungeonInstancePlacement (weenie), not a cell static object.</summary>
            public bool IsInstancePlacement;
            /// <summary>Index into DungeonDocument.InstancePlacements when IsInstancePlacement is true.</summary>
            public int InstancePlacementIndex;
        }

        /// <summary>
        /// Raycast against all static objects and instance placements (weenies) in the dungeon document.
        /// Objects are stored in landblock-local coordinates; we transform to world space
        /// using the same offset the scene uses for rendering.
        /// </summary>
        public static DungeonObjectHit Raycast(
            Vector3 rayOrigin, Vector3 rayDir,
            DungeonDocument document, DungeonScene scene) {

            var result = new DungeonObjectHit { Hit = false, Distance = float.MaxValue };
            if (document == null || scene == null) return result;

            uint lbId = document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);
            const float dungeonZBump = -50f;

            foreach (var cell in document.Cells) {
                for (int i = 0; i < cell.StaticObjects.Count; i++) {
                    var stab = cell.StaticObjects[i];
                    bool isSetup = (stab.Id & 0xFF000000) == 0x02000000;
                    var bounds = scene.GetObjectBounds(stab.Id, isSetup);
                    if (bounds == null) continue;

                    var (localMin, localMax) = bounds.Value;

                    // Transform object origin from landblock-local to world space
                    var worldOrigin = stab.Origin + lbOffset;
                    worldOrigin.Z += dungeonZBump;

                    var worldTransform =
                        Matrix4x4.CreateScale(stab.Scale)
                        * Matrix4x4.CreateFromQuaternion(stab.Orientation)
                        * Matrix4x4.CreateTranslation(worldOrigin);

                    var worldMin = Vector3.Transform(localMin, worldTransform);
                    var worldMax = Vector3.Transform(localMax, worldTransform);

                    var aabbMin = Vector3.Min(worldMin, worldMax);
                    var aabbMax = Vector3.Max(worldMin, worldMax);

                    if (RayIntersectsAABB(rayOrigin, rayDir, aabbMin, aabbMax, out float dist)) {
                        if (dist < result.Distance) {
                            result = new DungeonObjectHit {
                                Hit = true,
                                CellNumber = cell.CellNumber,
                                ObjectIndex = i,
                                ObjectId = stab.Id,
                                Distance = dist,
                                HitPosition = rayOrigin + rayDir * dist,
                                WorldOrigin = worldOrigin,
                                IsInstancePlacement = false,
                                InstancePlacementIndex = -1
                            };
                        }
                    }
                }
            }

            // Second pass: instance placements (weenies) use their setup model's bounds
            for (int i = 0; i < document.InstancePlacements.Count; i++) {
                var placement = document.InstancePlacements[i];
                if (!scene.TryGetWeenieSetupId(placement.WeenieClassId, out var setupId) || setupId == 0)
                    continue;

                bool isSetup = (setupId & 0xFF000000) == 0x02000000;
                var bounds = scene.GetObjectBounds(setupId, isSetup);
                if (bounds == null) continue;

                var (localMin, localMax) = bounds.Value;

                var worldOrigin = placement.Origin + lbOffset;
                worldOrigin.Z += dungeonZBump;

                var worldTransform =
                    Matrix4x4.CreateFromQuaternion(placement.Orientation)
                    * Matrix4x4.CreateTranslation(worldOrigin);

                var worldMin = Vector3.Transform(localMin, worldTransform);
                var worldMax = Vector3.Transform(localMax, worldTransform);

                var aabbMin = Vector3.Min(worldMin, worldMax);
                var aabbMax = Vector3.Max(worldMin, worldMax);

                if (RayIntersectsAABB(rayOrigin, rayDir, aabbMin, aabbMax, out float dist)) {
                    if (dist < result.Distance) {
                        result = new DungeonObjectHit {
                            Hit = true,
                            IsInstancePlacement = true,
                            InstancePlacementIndex = i,
                            ObjectId = setupId,
                            Distance = dist,
                            HitPosition = rayOrigin + rayDir * dist,
                            WorldOrigin = worldOrigin
                        };
                    }
                }
            }

            return result;
        }

        private static bool RayIntersectsAABB(Vector3 rayOrigin, Vector3 rayDir, Vector3 aabbMin, Vector3 aabbMax, out float distance) {
            distance = 0f;
            float tMin = float.MinValue;
            float tMax = float.MaxValue;

            for (int i = 0; i < 3; i++) {
                float origin = i == 0 ? rayOrigin.X : i == 1 ? rayOrigin.Y : rayOrigin.Z;
                float dir = i == 0 ? rayDir.X : i == 1 ? rayDir.Y : rayDir.Z;
                float min = i == 0 ? aabbMin.X : i == 1 ? aabbMin.Y : aabbMin.Z;
                float max = i == 0 ? aabbMax.X : i == 1 ? aabbMax.Y : aabbMax.Z;

                if (Math.Abs(dir) < 1e-8f) {
                    if (origin < min || origin > max) return false;
                }
                else {
                    float t1 = (min - origin) / dir;
                    float t2 = (max - origin) / dir;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    tMin = Math.Max(tMin, t1);
                    tMax = Math.Min(tMax, t2);
                    if (tMin > tMax) return false;
                }
            }

            distance = tMin >= 0 ? tMin : tMax;
            return distance >= 0;
        }
    }
}
