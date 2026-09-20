using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// Analyzes prefab topology, height, chamber sizes, and open-face directions
    /// to produce descriptive, searchable names like "Sewer Hallway",
    /// "Cave Corner Stairway", "Crypt T-Junction with Chamber".
    /// </summary>
    public static class PrefabNamer {

        public static void NameAll(List<DungeonPrefab> prefabs, List<CatalogRoom> catalog) {
            var boundsLookup = catalog.ToDictionary(
                c => (c.EnvId, c.CellStruct),
                c => c.BoundsWidth * c.BoundsDepth);

            foreach (var p in prefabs) {
                Name(p, boundsLookup);
                ComputeOpenFaceDirections(p);
            }
        }

        public static void Name(DungeonPrefab p, Dictionary<(ushort, ushort), float> boundsLookup) {
            string style = InferStyle(p.SourceDungeonName);
            p.Style = style;

            if (p.Cells.Count >= 8 && !string.IsNullOrEmpty(p.SourceDungeonName)) {
                p.DisplayName = $"{p.SourceDungeonName} ({p.Cells.Count} cells)";
                p.Category = "Full Dungeon";
                p.Tags = new List<string> { style.ToLowerInvariant(), "dungeon", "full",
                    $"{p.Cells.Count}-cell", p.SourceDungeonName.ToLowerInvariant() };
                return;
            }

            var internalDegree = new int[p.Cells.Count];
            foreach (var ip in p.InternalPortals) {
                internalDegree[ip.CellIndexA]++;
                internalDegree[ip.CellIndexB]++;
            }
            int maxDegree = internalDegree.Length > 0 ? internalDegree.Max() : 0;
            bool hasJunction = maxDegree >= 3;

            int openCount = p.OpenFaces.Count;
            string shape = ClassifyShape(p, internalDegree, hasJunction, openCount);

            string heightTag = "";
            if (p.Cells.Count >= 2) {
                float minZ = p.Cells.Min(c => c.OffsetZ);
                float maxZ = p.Cells.Max(c => c.OffsetZ);
                float zRange = maxZ - minZ;
                if (zRange > 5f) heightTag = "Descent";
                else if (zRange > 1f) heightTag = "Stairway";
            }

            bool hasChamber = false;
            if (boundsLookup.Count > 0 && p.Cells.Count >= 2) {
                var areas = p.Cells
                    .Select(c => boundsLookup.GetValueOrDefault((c.EnvId, c.CellStruct), 0f))
                    .ToList();
                float avg = areas.Average();
                hasChamber = avg > 0 && areas.Any(a => a > avg * 2f);
            }

            string funcName = shape;
            if (!string.IsNullOrEmpty(heightTag) && shape != "Hub" && shape != "Crossroads") {
                funcName = shape switch {
                    "Hallway" or "Passage" => $"{heightTag}",
                    "Corner" or "L-Turn" => $"Corner {heightTag}",
                    _ => $"{shape} {heightTag}"
                };
            }
            if (hasChamber && shape != "Chamber" && shape != "Alcove") {
                funcName += " with Chamber";
            }

            p.DisplayName = $"{style} {funcName}";
            p.Category = shape switch {
                "Hallway" or "Passage" or "Stairway" or "Descent" => "Hallway",
                "Corner" or "L-Turn" => "Corner",
                "T-Junction" or "Branch" => "T-Junction",
                "Hub" or "Crossroads" => "Hub",
                "Dead-End Passage" or "Dead-End Chamber" or "Alcove" => "Dead End",
                "Chamber" => "Chamber",
                _ => "Other"
            };

            p.Tags = new List<string> {
                style.ToLowerInvariant(), funcName.ToLowerInvariant(), p.Category.ToLowerInvariant(),
                $"{p.Cells.Count}-cell", $"{openCount}-exit"
            };
            if (hasChamber) p.Tags.Add("chamber");
            if (!string.IsNullOrEmpty(heightTag)) p.Tags.Add(heightTag.ToLowerInvariant());
            if (!string.IsNullOrEmpty(p.SourceDungeonName))
                p.Tags.Add(p.SourceDungeonName.ToLowerInvariant());
            if (p.HasNoRoof) p.Tags.Add("no-roof");
            else if (p.HasPartialRoof) p.Tags.Add("partial-roof");
            else p.Tags.Add("roofed");
        }

        private static string ClassifyShape(DungeonPrefab p, int[] internalDegree, bool hasJunction, int openCount) {
            if (hasJunction) {
                int maxDeg = internalDegree.Max();
                if (maxDeg >= 4 || openCount >= 4) return "Crossroads";
                if (openCount >= 3) return "T-Junction";
                return "Branch";
            }

            if (openCount == 2 && p.OpenFaces.Count == 2) {
                var n1 = new Vector3(p.OpenFaces[0].NormalX, p.OpenFaces[0].NormalY, p.OpenFaces[0].NormalZ);
                var n2 = new Vector3(p.OpenFaces[1].NormalX, p.OpenFaces[1].NormalY, p.OpenFaces[1].NormalZ);
                float len1 = n1.Length(), len2 = n2.Length();
                if (len1 > 0.01f && len2 > 0.01f) {
                    float dot = Vector3.Dot(n1 / len1, n2 / len2);
                    if (dot < -0.5f) return "Hallway";
                    if (dot > 0.5f) return "Passage";
                    return "Corner";
                }
                return "Hallway";
            }

            if (openCount == 1) {
                bool anyLargeCell = p.Cells.Any(c => c.PortalCount <= 1);
                return anyLargeCell ? "Alcove" : "Dead-End Passage";
            }

            if (openCount == 0) return "Chamber";
            if (openCount >= 4) return "Crossroads";
            if (openCount == 3) return "T-Junction";

            return "Passage";
        }

        /// <summary>
        /// Compute cardinal direction labels for each open face in a prefab,
        /// taking cell rotation into account so labels are in world space.
        /// </summary>
        public static void ComputeOpenFaceDirections(DungeonPrefab p) {
            p.OpenFaceDirections.Clear();
            foreach (var of in p.OpenFaces) {
                var localNormal = new Vector3(of.NormalX, of.NormalY, of.NormalZ);
                if (of.CellIndex < p.Cells.Count) {
                    var cell = p.Cells[of.CellIndex];
                    var rot = new Quaternion(cell.RotX, cell.RotY, cell.RotZ, cell.RotW);
                    if (rot.LengthSquared() > 0.01f) {
                        rot = Quaternion.Normalize(rot);
                        localNormal = Vector3.Transform(localNormal, rot);
                    }
                }
                p.OpenFaceDirections.Add(DungeonPrefab.ClassifyDirection(localNormal.X, localNormal.Y, localNormal.Z));
            }
        }

        public static string InferStyle(string dungeonName) {
            if (string.IsNullOrEmpty(dungeonName)) return "Stone";
            var lower = dungeonName.ToLowerInvariant();
            if (lower.Contains("sewer")) return "Sewer";
            if (lower.Contains("cave") || lower.Contains("grotto")) return "Cave";
            if (lower.Contains("crypt") || lower.Contains("tomb") || lower.Contains("catacomb")) return "Crypt";
            if (lower.Contains("mine") || lower.Contains("quarry")) return "Mine";
            if (lower.Contains("ruin")) return "Ruins";
            if (lower.Contains("fortress") || lower.Contains("citadel") || lower.Contains("castle")) return "Fortress";
            if (lower.Contains("tower")) return "Tower";
            if (lower.Contains("swamp") || lower.Contains("marsh")) return "Swamp";
            if (lower.Contains("hive") || lower.Contains("nest") || lower.Contains("olthoi")) return "Hive";
            if (lower.Contains("lair") || lower.Contains("den")) return "Lair";
            if (lower.Contains("temple") || lower.Contains("shrine")) return "Temple";
            if (lower.Contains("dungeon")) return "Dungeon";
            return "Stone";
        }
    }
}
