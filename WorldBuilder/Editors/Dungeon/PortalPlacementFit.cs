using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// Scores doorway snaps so a piece does not overlap existing rooms or sit
    /// in front of other open doors.
    /// </summary>
    public static class PortalPlacementFit {
        public const float AabbShrink = 0.4f;
        public const float ProbeDistance = 4.5f;

        public readonly record struct WorldCellPose(Vector3 Origin, Quaternion Rotation, ushort EnvId, ushort CellStruct);

        public readonly record struct ExistingRoom(ushort CellNum, Vector3 Origin, RoomAABB WorldAabb);

        public readonly record struct OpenDoorProbe(ushort CellNum, ushort PolyId, Vector3 Centroid, Vector3 Normal);

        public static List<WorldCellPose> PrefabWorldCells(
            DungeonPrefab prefab, int connectIndex, Vector3 connectOrigin, Quaternion connectRot) {

            var result = new List<WorldCellPose>(prefab.Cells.Count);
            if (connectIndex < 0 || connectIndex >= prefab.Cells.Count) return result;

            var connectPC = prefab.Cells[connectIndex];
            var connectOffset = new Vector3(connectPC.OffsetX, connectPC.OffsetY, connectPC.OffsetZ);
            var connectRelRot = Quaternion.Normalize(new Quaternion(connectPC.RotX, connectPC.RotY, connectPC.RotZ, connectPC.RotW));
            Quaternion invRelRot = connectRelRot.LengthSquared() > 0.01f ? Quaternion.Inverse(connectRelRot) : Quaternion.Identity;
            var worldBaseRot = Quaternion.Normalize(connectRot * invRelRot);
            var worldBaseOrigin = connectOrigin - Vector3.Transform(connectOffset, worldBaseRot);

            for (int i = 0; i < prefab.Cells.Count; i++) {
                var pc = prefab.Cells[i];
                if (i == connectIndex) {
                    result.Add(new WorldCellPose(connectOrigin, connectRot, pc.EnvId, pc.CellStruct));
                    continue;
                }
                var offset = new Vector3(pc.OffsetX, pc.OffsetY, pc.OffsetZ);
                var relRot = Quaternion.Normalize(new Quaternion(pc.RotX, pc.RotY, pc.RotZ, pc.RotW));
                var worldOrigin = worldBaseOrigin + Vector3.Transform(offset, worldBaseRot);
                var worldRot = Quaternion.Normalize(worldBaseRot * relRot);
                result.Add(new WorldCellPose(worldOrigin, worldRot, pc.EnvId, pc.CellStruct));
            }
            return result;
        }

        public static List<ExistingRoom> CollectExisting(
            DungeonDocument doc, PortalGeometryCache? geo) {
            var list = new List<ExistingRoom>(doc.Cells.Count);
            foreach (var dc in doc.Cells) {
                RoomAABB aabb;
                var local = geo?.GetAABB(dc.EnvironmentId, dc.CellStructure);
                if (local.HasValue)
                    aabb = local.Value.ToWorldSpace(dc.Origin, dc.Orientation);
                else
                    aabb = new RoomAABB {
                        Min = dc.Origin - new Vector3(5f),
                        Max = dc.Origin + new Vector3(5f)
                    };
                list.Add(new ExistingRoom(dc.CellNumber, dc.Origin, aabb));
            }
            return list;
        }

        public static List<RoomAABB> CandidateAabbs(
            IReadOnlyList<WorldCellPose> poses, PortalGeometryCache? geo) {
            var list = new List<RoomAABB>(poses.Count);
            foreach (var pose in poses) {
                var local = geo?.GetAABB(pose.EnvId, pose.CellStruct);
                if (local.HasValue)
                    list.Add(local.Value.ToWorldSpace(pose.Origin, pose.Rotation));
                else
                    list.Add(new RoomAABB {
                        Min = pose.Origin - new Vector3(5f),
                        Max = pose.Origin + new Vector3(5f)
                    });
            }
            return list;
        }

        public static bool OverlapsExisting(
            IReadOnlyList<RoomAABB> candidateAabbs,
            IReadOnlyList<ExistingRoom> existing,
            ushort attachCellNum) {
            foreach (var cand in candidateAabbs) {
                foreach (var room in existing) {
                    if (room.CellNum == attachCellNum) continue;
                    if (cand.Intersects(room.WorldAabb, AabbShrink))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// How many other open doorways would have this piece sitting in the walkway in front of them.
        /// </summary>
        public static int CountBlockedOpenDoors(
            IReadOnlyList<RoomAABB> candidateAabbs,
            IReadOnlyList<OpenDoorProbe> otherDoors) {
            int n = 0;
            foreach (var door in otherDoors) {
                var probe = door.Centroid + Vector3.Normalize(door.Normal) * ProbeDistance;
                foreach (var aabb in candidateAabbs) {
                    if (!aabb.ContainsPoint(probe, inflate: 0.2f)) continue;
                    n++;
                    break;
                }
            }
            return n;
        }

        /// <summary>
        /// Remaining doors on the new piece that immediately face into existing rooms.
        /// </summary>
        public static int CountBornBlockedExits(
            DungeonPrefab? prefab,
            int attachFaceIndex,
            IReadOnlyList<PrefabOpenFace> faces,
            Vector3 connectOrigin,
            Quaternion connectRot,
            IReadOnlyList<ExistingRoom> existing,
            ushort attachCellNum) {
            int n = 0;
            for (int i = 0; i < faces.Count; i++) {
                if (i == attachFaceIndex) continue;
                var face = faces[i];
                Vector3 localNormal = new Vector3(face.NormalX, face.NormalY, face.NormalZ);
                if (prefab != null && face.CellIndex >= 0 && face.CellIndex < prefab.Cells.Count) {
                    var pc = prefab.Cells[face.CellIndex];
                    var cellOffset = new Vector3(pc.OffsetX, pc.OffsetY, pc.OffsetZ);
                    var cellRel = Quaternion.Normalize(new Quaternion(pc.RotX, pc.RotY, pc.RotZ, pc.RotW));
                    localNormal = Vector3.Transform(localNormal, cellRel);
                    if (localNormal.LengthSquared() < 0.01f)
                        localNormal = new Vector3(face.NormalX, face.NormalY, face.NormalZ);
                    var worldC = connectOrigin + Vector3.Transform(cellOffset, connectRot);
                    var worldN = Vector3.Normalize(Vector3.Transform(localNormal, connectRot));
                    var probe = worldC + worldN * ProbeDistance;
                    if (existing.Any(r => r.CellNum != attachCellNum && r.WorldAabb.ContainsPoint(probe)))
                        n++;
                }
                else {
                    if (localNormal.LengthSquared() < 0.01f) continue;
                    var worldN = Vector3.Normalize(Vector3.Transform(localNormal, connectRot));
                    var probe = connectOrigin + worldN * ProbeDistance;
                    if (existing.Any(r => r.CellNum != attachCellNum && r.WorldAabb.ContainsPoint(probe)))
                        n++;
                }
            }
            return n;
        }

        public static float GrowthScore(Vector3 newOrigin, Vector3 dungeonCenter) =>
            (newOrigin - dungeonCenter).Length();
    }
}
