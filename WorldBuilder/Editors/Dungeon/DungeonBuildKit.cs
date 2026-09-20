using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// A small set of 1-cell rooms that share a doorway size and have proven
    /// connections to each other in retail dungeons.
    /// </summary>
    public sealed class DungeonBuildKit {
        public string Style { get; init; } = "";
        public string CategoryName { get; init; } = "";
        public string Hint { get; init; } = "";
        public float DoorWidth { get; init; }
        public float DoorHeight { get; init; }
        public int Score { get; init; }
        public List<DungeonPrefab> Pieces { get; init; } = new();
    }

    /// <summary>
    /// Builds connectable starter kits from the dungeon knowledge base.
    /// Prefab extraction only stores 2+ cell chunks, so the kit synthesizes
    /// 1-cell bricks from catalog rooms whose adjacency edges actually exist.
    /// </summary>
    public static class DungeonBuildKitBuilder {
        private static readonly string[] PreferredStyles = { "Sewer", "Cave", "Crypt" };

        private static readonly Dictionary<string, int> RoleQuota = new(StringComparer.OrdinalIgnoreCase) {
            ["Hallway"] = 2,
            ["Corner"] = 2,
            ["T-Junction"] = 1,
            ["Hub"] = 1,
            ["Dead End"] = 2,
            ["Chamber"] = 0,
            ["Stair"] = 0
        };

        private static readonly Dictionary<string, int> RoleSort = new(StringComparer.OrdinalIgnoreCase) {
            ["Hallway"] = 0,
            ["Corner"] = 1,
            ["T-Junction"] = 2,
            ["Hub"] = 3,
            ["Dead End"] = 4,
            ["Chamber"] = 5,
            ["Stair"] = 6
        };

        public static List<DungeonBuildKit> BuildAll(DungeonKnowledgeBase kb, IDatReaderWriter dats) {
            var kits = new List<DungeonBuildKit>();
            if (kb?.Catalog == null || kb.Catalog.Count == 0 || kb.Edges == null || dats == null)
                return kits;

            var catalog = kb.Catalog
                .GroupBy(r => (r.EnvId, r.CellStruct))
                .ToDictionary(g => g.Key, g => g.First());

            var candidates = catalog.Values.Where(IsCandidateRoom).ToList();
            if (candidates.Count == 0) return kits;

            var adj = BuildAdjacency(kb.Edges, candidates);
            var surfaces = BuildSurfaceLookup(kb.Prefabs);

            var styles = PreferredStyles
                .Where(s => candidates.Any(r => r.Style.Equals(s, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var style in styles) {
                var kit = TryBuildStyleKit(style, candidates, catalog, adj, surfaces, dats);
                if (kit != null)
                    kits.Add(kit);
            }

            return kits.OrderByDescending(k => k.Score).ToList();
        }

        private static bool IsCandidateRoom(CatalogRoom r) {
            int portals = r.VerifiedPortalCount > 0 ? r.VerifiedPortalCount : r.PortalCount;
            if (portals < 1 || portals > 4) return false;
            if (r.UsageCount < 15) return false;
            if (r.OutsidePortalRate > 0.15f) return false;
            if (r.RestrictionRate > 0.35f) return false;
            return TryDoorKey(r, out _);
        }

        private static bool TryDoorKey(CatalogRoom r, out (float w, float h) key) {
            key = default;
            var dims = r.PortalDimensions?
                .Where(d => d.Width >= 1.5f && d.Height >= 1.5f)
                .ToList();
            if (dims == null || dims.Count == 0) return false;
            var widths = dims.Select(d => d.Width).OrderBy(v => v).ToList();
            var heights = dims.Select(d => d.Height).OrderBy(v => v).ToList();
            key = (RoundDoor(widths[widths.Count / 2]), RoundDoor(heights[heights.Count / 2]));
            return key.w >= 2f && key.h >= 2f;
        }

        private static float RoundDoor(float v) => MathF.Round(v * 2f) / 2f;

        private static Dictionary<(ushort env, ushort cs), Dictionary<(ushort env, ushort cs), int>> BuildAdjacency(
            List<AdjacencyEdge> edges, List<CatalogRoom> candidates) {
            var keys = candidates.Select(r => (r.EnvId, r.CellStruct)).ToHashSet();
            var adj = new Dictionary<(ushort, ushort), Dictionary<(ushort, ushort), int>>();

            void Add((ushort, ushort) a, (ushort, ushort) b, int count) {
                if (a == b) return;
                if (!adj.TryGetValue(a, out var map)) {
                    map = new Dictionary<(ushort, ushort), int>();
                    adj[a] = map;
                }
                map.TryGetValue(b, out int existing);
                map[b] = existing + count;
            }

            foreach (var e in edges) {
                var a = (e.EnvIdA, e.CellStructA);
                var b = (e.EnvIdB, e.CellStructB);
                if (!keys.Contains(a) || !keys.Contains(b)) continue;
                int count = Math.Max(1, e.Count);
                Add(a, b, count);
                Add(b, a, count);
            }
            return adj;
        }

        private static Dictionary<(ushort env, ushort cs), List<ushort>> BuildSurfaceLookup(List<DungeonPrefab>? prefabs) {
            var lookup = new Dictionary<(ushort, ushort), List<ushort>>();
            if (prefabs == null) return lookup;
            foreach (var prefab in prefabs) {
                foreach (var cell in prefab.Cells) {
                    var key = (cell.EnvId, cell.CellStruct);
                    if (cell.Surfaces.Count == 0 || lookup.ContainsKey(key)) continue;
                    lookup[key] = new List<ushort>(cell.Surfaces);
                }
            }
            return lookup;
        }

        private static DungeonBuildKit? TryBuildStyleKit(
            string style,
            List<CatalogRoom> allCandidates,
            Dictionary<(ushort, ushort), CatalogRoom> catalog,
            Dictionary<(ushort env, ushort cs), Dictionary<(ushort env, ushort cs), int>> adj,
            Dictionary<(ushort env, ushort cs), List<ushort>> surfaces,
            IDatReaderWriter dats) {

            var styleRooms = allCandidates
                .Where(r => r.Style.Equals(style, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (styleRooms.Count < 8) return null;

            var doorGroups = styleRooms
                .Select(r => {
                    TryDoorKey(r, out var key);
                    return (room: r, key);
                })
                .GroupBy(x => x.key)
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var group in doorGroups) {
                var door = group.Key;
                var rooms = group.Select(x => x.room).ToList();
                var keys = rooms.Select(r => (r.EnvId, r.CellStruct)).ToHashSet();
                var component = LargestComponent(keys, adj, minCount: 3);
                if (component.Count < 8) continue;

                var ranked = component
                    .Select(k => catalog[k])
                    .OrderByDescending(r => r.UsageCount)
                    .Take(32)
                    .ToList();

                var built = new List<DungeonPrefab>();
                foreach (var room in ranked) {
                    var prefab = TryCreateCellPrefab(room, dats, surfaces);
                    if (prefab == null) continue;
                    built.Add(prefab);
                }
                if (built.Count < 8) continue;

                var selected = SelectConnectedSet(built, adj);
                if (selected.Count < 6) continue;
                if (!HasCoreRoles(selected)) continue;

                AnnotateLinks(selected, adj);
                DisambiguateNames(selected);

                var usage = selected.Sum(p => p.UsageCount);
                var hint = "Click a hallway to start. Yellow doorway only lists rooms that used that door in retail.";

                int score = ScoreKit(selected, usage);
                Console.WriteLine($"[BuildKit] {style}: {selected.Count} rooms, door {door.w:0.#}x{door.h:0.#}, score={score}, usage={usage}");

                return new DungeonBuildKit {
                    Style = style,
                    CategoryName = style,
                    Hint = hint,
                    DoorWidth = door.w,
                    DoorHeight = door.h,
                    Score = score,
                    Pieces = selected
                };
            }

            return null;
        }

        private static HashSet<(ushort env, ushort cs)> LargestComponent(
            HashSet<(ushort env, ushort cs)> nodes,
            Dictionary<(ushort env, ushort cs), Dictionary<(ushort env, ushort cs), int>> adj,
            int minCount) {

            var remaining = new HashSet<(ushort, ushort)>(nodes);
            HashSet<(ushort, ushort)> best = new();

            while (remaining.Count > 0) {
                var start = remaining.First();
                var stack = new Stack<(ushort, ushort)>();
                var comp = new HashSet<(ushort, ushort)>();
                stack.Push(start);
                remaining.Remove(start);
                while (stack.Count > 0) {
                    var u = stack.Pop();
                    comp.Add(u);
                    if (!adj.TryGetValue(u, out var nbrs)) continue;
                    foreach (var (v, count) in nbrs) {
                        if (count < minCount || !remaining.Contains(v)) continue;
                        remaining.Remove(v);
                        stack.Push(v);
                    }
                }
                if (comp.Count > best.Count)
                    best = comp;
            }
            return best;
        }

        private static List<DungeonPrefab> SelectConnectedSet(
            List<DungeonPrefab> built,
            Dictionary<(ushort env, ushort cs), Dictionary<(ushort env, ushort cs), int>> adj) {

            var byKey = built.ToDictionary(p => RoomKey(p));
            var seed = built
                .Where(p => p.KitRole == "Hallway")
                .OrderByDescending(p => p.UsageCount)
                .FirstOrDefault()
                ?? built.Where(p => p.OpenFaces.Count == 2)
                    .OrderByDescending(p => p.UsageCount)
                    .FirstOrDefault()
                ?? built.OrderByDescending(p => p.UsageCount).First();

            var selected = new List<DungeonPrefab> { seed };
            var selectedKeys = new HashSet<(ushort, ushort)> { RoomKey(seed) };
            var roleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
                [seed.KitRole] = 1
            };
            var remaining = built.Where(p => !selectedKeys.Contains(RoomKey(p))).ToList();

            while (selected.Count < 8 && remaining.Count > 0) {
                DungeonPrefab? best = null;
                float bestScore = float.MinValue;
                foreach (var cand in remaining) {
                    int links = LinkWeight(RoomKey(cand), selectedKeys, adj);
                    if (links < 3) continue;
                    string role = cand.KitRole;
                    int have = roleCounts.GetValueOrDefault(role);
                    int want = RoleQuota.GetValueOrDefault(role, 1);
                    if (have >= want) continue;
                    int envDupes = selected.Count(p => p.Cells[0].EnvId == cand.Cells[0].EnvId);
                    if (envDupes >= 3) continue;
                    float score = links
                        + (have == 0 ? 80f : 20f)
                        + Math.Min(cand.UsageCount, 4000) / 80f
                        - envDupes * 8f;
                    if (score > bestScore) {
                        bestScore = score;
                        best = cand;
                    }
                }
                if (best == null) break;
                selected.Add(best);
                selectedKeys.Add(RoomKey(best));
                roleCounts[best.KitRole] = roleCounts.GetValueOrDefault(best.KitRole) + 1;
                remaining.Remove(best);
            }

            // Fill remaining slots with strongly linked leftovers if we are still short.
            remaining = remaining
                .OrderByDescending(p => LinkWeight(RoomKey(p), selectedKeys, adj))
                .ThenByDescending(p => p.UsageCount)
                .ToList();
            foreach (var cand in remaining) {
                if (selected.Count >= 8) break;
                if (LinkWeight(RoomKey(cand), selectedKeys, adj) < 5) continue;
                string role = cand.KitRole;
                if (roleCounts.GetValueOrDefault(role) >= RoleQuota.GetValueOrDefault(role, 1) + 1)
                    continue;
                selected.Add(cand);
                selectedKeys.Add(RoomKey(cand));
                roleCounts[role] = roleCounts.GetValueOrDefault(role) + 1;
            }

            return selected
                .OrderBy(p => RoleSort.GetValueOrDefault(p.KitRole, 9))
                .ThenByDescending(p => p.UsageCount)
                .ToList();
        }

        private static bool HasCoreRoles(List<DungeonPrefab> pieces) {
            bool hasConnector = pieces.Any(p => p.KitRole is "Hallway" or "Corner");
            bool hasCap = pieces.Any(p => p.KitRole is "Dead End" or "Chamber");
            return hasConnector && hasCap;
        }

        private static int ScoreKit(List<DungeonPrefab> pieces, int usage) {
            int score = pieces.Count * 50;
            if (pieces.Any(p => p.KitRole == "Hallway")) score += 10000;
            if (pieces.Any(p => p.KitRole == "Corner")) score += 8000;
            if (pieces.Any(p => p.KitRole == "T-Junction")) score += 8000;
            if (pieces.Any(p => p.KitRole == "Dead End")) score += 8000;
            if (pieces.Any(p => p.KitRole == "Hub")) score += 3000;
            if (pieces.Any(p => p.KitRole == "Chamber")) score += 2000;
            score += Math.Min(usage, 120000) / 20;
            return score;
        }

        private static void AnnotateLinks(
            List<DungeonPrefab> pieces,
            Dictionary<(ushort env, ushort cs), Dictionary<(ushort env, ushort cs), int>> adj) {
            var keys = pieces.Select(RoomKey).ToHashSet();
            foreach (var p in pieces) {
                var key = RoomKey(p);
                int links = 0;
                if (adj.TryGetValue(key, out var nbrs)) {
                    foreach (var (v, count) in nbrs) {
                        if (v != key && keys.Contains(v) && count >= 3)
                            links++;
                    }
                }
                p.KitLinkCount = links;
            }
        }

        private static void DisambiguateNames(List<DungeonPrefab> pieces) {
            var groups = pieces.GroupBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups) {
                if (group.Count() < 2) continue;
                foreach (var p in group) {
                    var dirs = FriendlyDirections(p.OpenFaceDirections);
                    if (!string.IsNullOrEmpty(dirs))
                        p.DisplayName = $"{group.Key} · {dirs}";
                }
            }
        }

        public static string ShortRoleName(string role) => role switch {
            "T-Junction" => "T-junction",
            "Dead End" => "Dead end",
            "Chamber" => "Room",
            "Hub" => "Crossroads",
            "Stair" => "Stairs",
            _ => role
        };

        public static string FriendlyDirections(IReadOnlyList<string> dirs) {
            if (dirs == null || dirs.Count == 0) return "";
            return string.Join(", ", dirs.Select(d => d switch {
                "N" => "North",
                "S" => "South",
                "E" => "East",
                "W" => "West",
                _ => d
            }));
        }

        private static int LinkWeight(
            (ushort env, ushort cs) key,
            HashSet<(ushort, ushort)> selected,
            Dictionary<(ushort env, ushort cs), Dictionary<(ushort env, ushort cs), int>> adj) {
            if (!adj.TryGetValue(key, out var nbrs)) return 0;
            int sum = 0;
            foreach (var s in selected) {
                if (nbrs.TryGetValue(s, out int c))
                    sum += c;
            }
            return sum;
        }

        private static (ushort env, ushort cs) RoomKey(DungeonPrefab p) =>
            (p.Cells[0].EnvId, p.Cells[0].CellStruct);

        public static DungeonPrefab? TryCreateCellPrefab(
            CatalogRoom room,
            IDatReaderWriter dats,
            Dictionary<(ushort env, ushort cs), List<ushort>>? surfaces = null) {
            uint envFileId = (uint)(room.EnvId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return null;
            if (!env.Cells.TryGetValue(room.CellStruct, out var cs)) return null;

            bool hasCeiling = CellHasCeiling(cs);
            if (!hasCeiling) return null;

            var portalIds = room.PortalPolyIds != null && room.PortalPolyIds.Count > 0
                ? room.PortalPolyIds
                : PortalSnapper.GetPortalPolygonIds(cs);
            if (portalIds.Count == 0) return null;

            var prefab = new DungeonPrefab {
                Signature = $"kitcell_{room.EnvId:X4}_{room.CellStruct}",
                SourceDungeonName = room.SourceDungeons.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "",
                UsageCount = room.UsageCount,
                Style = room.Style,
                HasFullRoof = true,
                HasPartialRoof = false,
                HasNoRoof = false
            };

            List<ushort> cellSurfaces;
            if (surfaces != null && surfaces.TryGetValue((room.EnvId, room.CellStruct), out var fromPrefab) && fromPrefab.Count > 0)
                cellSurfaces = new List<ushort>(fromPrefab);
            else
                cellSurfaces = room.SampleSurfaces != null ? new List<ushort>(room.SampleSurfaces) : new List<ushort>();

            prefab.Cells.Add(new PrefabCell {
                LocalIndex = 0,
                EnvId = room.EnvId,
                CellStruct = room.CellStruct,
                PortalCount = portalIds.Count,
                OffsetX = 0, OffsetY = 0, OffsetZ = 0,
                RotX = 0, RotY = 0, RotZ = 0, RotW = 1,
                Surfaces = cellSurfaces,
                HasCeiling = true
            });

            foreach (var polyId in portalIds) {
                float nx = 0, ny = 0, nz = 0;
                var geom = PortalSnapper.GetPortalGeometry(cs, polyId);
                if (geom != null) {
                    nx = geom.Value.Normal.X;
                    ny = geom.Value.Normal.Y;
                    nz = geom.Value.Normal.Z;
                }
                prefab.OpenFaces.Add(new PrefabOpenFace {
                    CellIndex = 0,
                    PolyId = polyId,
                    EnvId = room.EnvId,
                    CellStruct = room.CellStruct,
                    NormalX = nx, NormalY = ny, NormalZ = nz
                });
            }

            PrefabNamer.ComputeOpenFaceDirections(prefab);
            string role = ClassifyKitRole(prefab, room);
            if (string.IsNullOrEmpty(role)) return null;

            prefab.KitRole = role;
            prefab.Category = role switch {
                "Stair" => "Hallway",
                "Hub" => "Hub",
                _ => role
            };
            prefab.DisplayName = ShortRoleName(role);
            prefab.Tags = new List<string> {
                "starter", "kit", room.Style.ToLowerInvariant(), role.ToLowerInvariant(), "1-cell", "roofed"
            };
            return prefab;
        }

        private static string ClassifyKitRole(DungeonPrefab prefab, CatalogRoom room) {
            var faces = prefab.OpenFaces;
            var horizontal = faces.Where(f => MathF.Abs(f.NormalZ) < 0.55f).ToList();
            var vertical = faces.Count - horizontal.Count;
            float area = room.BoundsWidth * room.BoundsDepth;

            if (vertical > 0 && horizontal.Count <= 1)
                return "Stair";

            if (horizontal.Count == 1)
                return area >= 80f ? "Chamber" : "Dead End";

            if (horizontal.Count == 2) {
                var n1 = Vector3.Normalize(new Vector3(horizontal[0].NormalX, horizontal[0].NormalY, 0));
                var n2 = Vector3.Normalize(new Vector3(horizontal[1].NormalX, horizontal[1].NormalY, 0));
                if (n1.LengthSquared() < 0.01f || n2.LengthSquared() < 0.01f)
                    return "Hallway";
                float dot = Vector3.Dot(n1, n2);
                return dot < -0.45f ? "Hallway" : "Corner";
            }

            if (horizontal.Count == 3) return "T-Junction";
            if (horizontal.Count >= 4) return "Hub";
            return "";
        }

        private static bool CellHasCeiling(CellStruct cs) {
            if (cs.VertexArray?.Vertices == null || cs.Polygons == null) return true;
            var portalIds = cs.Portals != null ? new HashSet<ushort>(PortalSnapper.GetPortalPolygonIds(cs)) : new HashSet<ushort>();

            foreach (var kvp in cs.Polygons) {
                if (portalIds.Contains(kvp.Key)) continue;
                var poly = kvp.Value;
                if (poly.VertexIds.Count < 3) continue;
                var verts = cs.VertexArray.Vertices;
                if (!verts.TryGetValue((ushort)poly.VertexIds[0], out var v0) ||
                    !verts.TryGetValue((ushort)poly.VertexIds[1], out var v1) ||
                    !verts.TryGetValue((ushort)poly.VertexIds[2], out var v2)) continue;
                var normal = Vector3.Normalize(Vector3.Cross(v1.Origin - v0.Origin, v2.Origin - v0.Origin));
                if (normal.Z < -0.7f) return true;
            }
            return false;
        }
    }
}
