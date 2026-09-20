using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Acme.Dat;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    public class CellSelectionChangedArgs {
        public LoadedEnvCell? PrimaryCell { get; init; }
        public IReadOnlyList<LoadedEnvCell> AllCells { get; init; } = Array.Empty<LoadedEnvCell>();
        public int Count => AllCells.Count;
        public bool HasSelection => PrimaryCell != null;
    }

    public class ObjectSelectionChangedArgs {
        public ushort CellNum { get; init; }
        public int ObjectIndex { get; init; } = -1;
        public DungeonStabData? Stab { get; init; }
        public bool HasSelection => ObjectIndex >= 0;
    }

    public class InstancePlacementSelectionChangedArgs {
        public int Index { get; init; } = -1;
        public DungeonInstancePlacement? Placement { get; init; }
        public bool HasSelection => Index >= 0;
    }

    /// <summary>
    /// Manages cell and object selection state for the dungeon editor.
    /// Fires events when selection changes so the ViewModel can update its bound properties.
    /// </summary>
    public class DungeonSelectionManager {
        private readonly DungeonEditingContext _ctx;

        private LoadedEnvCell? _selectedCell;
        private readonly List<LoadedEnvCell> _selectedCells = new();
        private ushort _selectedObjCellNum;
        private int _selectedObjIndex = -1;
        private int _selectedInstancePlacementIndex = -1;

        public LoadedEnvCell? SelectedCell => _selectedCell;
        public IReadOnlyList<LoadedEnvCell> SelectedCells => _selectedCells;
        public ushort SelectedObjCellNum => _selectedObjCellNum;
        public int SelectedObjIndex => _selectedObjIndex;
        public bool HasSelectedCell => _selectedCell != null;
        public bool HasSelectedObject => _selectedObjIndex >= 0;
        public int SelectedInstancePlacementIndex => _selectedInstancePlacementIndex;
        public bool HasSelectedInstancePlacement => _selectedInstancePlacementIndex >= 0;

        public event Action<CellSelectionChangedArgs>? CellSelectionChanged;
        public event Action? CellDeselected;
        public event Action<ObjectSelectionChangedArgs>? ObjectSelectionChanged;
        public event Action? ObjectDeselected;
        public event Action<InstancePlacementSelectionChangedArgs>? InstancePlacementSelectionChanged;
        public event Action? InstancePlacementDeselected;

        public DungeonSelectionManager(DungeonEditingContext ctx) {
            _ctx = ctx;
        }

        public void SelectCell(LoadedEnvCell cell) {
            _selectedCells.Clear();
            _selectedCells.Add(cell);
            _selectedCell = cell;
            DeselectInstancePlacement(silent: true);
            SyncToContext();
            SyncSceneSelection();
            CellSelectionChanged?.Invoke(new CellSelectionChangedArgs {
                PrimaryCell = cell,
                AllCells = _selectedCells.ToList()
            });
        }

        public void ToggleCellInSelection(LoadedEnvCell cell) {
            var idx = _selectedCells.FindIndex(c => c.CellId == cell.CellId);
            if (idx >= 0) {
                _selectedCells.RemoveAt(idx);
            }
            else {
                _selectedCells.Add(cell);
            }
            if (_selectedCells.Count == 0) {
                DeselectCell();
                return;
            }
            _selectedCell = _selectedCells[0];
            SyncToContext();
            SyncSceneSelection();
            CellSelectionChanged?.Invoke(new CellSelectionChangedArgs {
                PrimaryCell = _selectedCell,
                AllCells = _selectedCells.ToList()
            });
        }

        public void DeselectCell() {
            _selectedCells.Clear();
            _selectedCell = null;
            SyncToContext();
            if (_ctx.Scene != null) _ctx.Scene.SelectedCells = null;
            CellDeselected?.Invoke();
        }

        public void SelectObject(ushort cellNum, int objectIndex, DungeonStabData stab) {
            DeselectCell();
            DeselectObject();
            DeselectInstancePlacement(silent: true);

            _selectedObjCellNum = cellNum;
            _selectedObjIndex = objectIndex;
            SyncToContext();

            UpdateObjectSelectionHighlight(stab);
            ObjectSelectionChanged?.Invoke(new ObjectSelectionChangedArgs {
                CellNum = cellNum,
                ObjectIndex = objectIndex,
                Stab = stab
            });
        }

        public void DeselectObject() {
            _selectedObjCellNum = 0;
            _selectedObjIndex = -1;
            SyncToContext();
            if (_ctx.Scene != null) {
                _ctx.Scene.SelectedObjectBounds = null;
                _ctx.Scene.SelectedObjectPosition = null;
            }
            ObjectDeselected?.Invoke();
        }

        public void SelectInstancePlacement(int index, DungeonInstancePlacement placement) {
            DeselectCell();
            DeselectObject();
            _selectedInstancePlacementIndex = index;
            SyncToContext();
            UpdateInstancePlacementSelectionHighlight(placement);
            InstancePlacementSelectionChanged?.Invoke(new InstancePlacementSelectionChangedArgs {
                Index = index,
                Placement = placement
            });
        }

        public void DeselectInstancePlacement(bool silent = false) {
            if (_selectedInstancePlacementIndex < 0) return;
            _selectedInstancePlacementIndex = -1;
            SyncToContext();
            if (_ctx.Scene != null) {
                _ctx.Scene.SelectedObjectBounds = null;
                _ctx.Scene.SelectedObjectPosition = null;
            }
            if (!silent) InstancePlacementDeselected?.Invoke();
        }

        private void UpdateInstancePlacementSelectionHighlight(DungeonInstancePlacement placement) {
            if (_ctx.Scene == null || _ctx.Document == null) return;
            if (!_ctx.Scene.TryGetWeenieSetupId(placement.WeenieClassId, out var setupId) || setupId == 0) {
                _ctx.Scene.SelectedObjectBounds = null;
                return;
            }

            bool isSetup = (setupId & 0xFF000000) == 0x02000000;
            var bounds = _ctx.Scene.GetObjectBounds(setupId, isSetup);
            if (bounds == null) { _ctx.Scene.SelectedObjectBounds = null; return; }

            uint lbId = _ctx.Document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;

            var worldOrigin = placement.Origin + new Vector3(blockX * 192f, blockY * 192f, -50f);

            var worldTransform = Matrix4x4.CreateFromQuaternion(placement.Orientation)
                * Matrix4x4.CreateTranslation(worldOrigin);

            var worldMin = Vector3.Transform(bounds.Value.Min, worldTransform);
            var worldMax = Vector3.Transform(bounds.Value.Max, worldTransform);

            _ctx.Scene.SelectedObjectBounds = (Vector3.Min(worldMin, worldMax), Vector3.Max(worldMin, worldMax));
            _ctx.Scene.SelectedObjectPosition = worldOrigin;
            _ctx.Scene.SelectedObjectOrientation = placement.Orientation;
        }

        public void UpdateObjectSelectionHighlight(DungeonStabData? stab = null) {
            if (_ctx.Scene == null || _ctx.Document == null) return;

            if (stab == null) {
                var cell = _ctx.Document.GetCell(_selectedObjCellNum);
                if (cell == null || _selectedObjIndex >= cell.StaticObjects.Count) {
                    _ctx.Scene.SelectedObjectBounds = null;
                    return;
                }
                stab = cell.StaticObjects[_selectedObjIndex];
            }

            bool isSetup = (stab.Id & 0xFF000000) == 0x02000000;
            var bounds = _ctx.Scene.GetObjectBounds(stab.Id, isSetup);
            if (bounds == null) { _ctx.Scene.SelectedObjectBounds = null; return; }

            uint lbId = _ctx.Document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);

            var worldOrigin = stab.Origin + lbOffset;
            worldOrigin.Z += -50f;

            var worldTransform = Matrix4x4.CreateScale(stab.Scale)
                * Matrix4x4.CreateFromQuaternion(stab.Orientation)
                * Matrix4x4.CreateTranslation(worldOrigin);

            var worldMin = Vector3.Transform(bounds.Value.Min, worldTransform);
            var worldMax = Vector3.Transform(bounds.Value.Max, worldTransform);

            _ctx.Scene.SelectedObjectBounds = (Vector3.Min(worldMin, worldMax), Vector3.Max(worldMin, worldMax));
            _ctx.Scene.SelectedObjectPosition = worldOrigin;
            _ctx.Scene.SelectedObjectOrientation = stab.Orientation;
        }

        /// <summary>
        /// Sync selection state from the editing context (after tool changes).
        /// Returns the current state so the ViewModel can update its properties.
        /// </summary>
        public (CellSelectionChangedArgs? cell, ObjectSelectionChangedArgs? obj, InstancePlacementSelectionChangedArgs? placement) SyncFromContext() {
            _selectedCell = _ctx.SelectedCell;
            _selectedCells.Clear();
            _selectedCells.AddRange(_ctx.SelectedCells);
            _selectedObjCellNum = _ctx.SelectedObjCellNum;
            _selectedObjIndex = _ctx.SelectedObjIndex;
            _selectedInstancePlacementIndex = _ctx.SelectedInstancePlacementIndex;

            CellSelectionChangedArgs? cellArgs = null;
            ObjectSelectionChangedArgs? objArgs = null;
            InstancePlacementSelectionChangedArgs? placementArgs = null;

            if (_selectedCell != null) {
                SyncSceneSelection();
                cellArgs = new CellSelectionChangedArgs {
                    PrimaryCell = _selectedCell,
                    AllCells = _selectedCells.ToList()
                };
            }

            if (_selectedObjIndex >= 0) {
                var cell = _ctx.Document?.GetCell(_selectedObjCellNum);
                if (cell != null && _selectedObjIndex < cell.StaticObjects.Count) {
                    var stab = cell.StaticObjects[_selectedObjIndex];
                    UpdateObjectSelectionHighlight(stab);
                    objArgs = new ObjectSelectionChangedArgs {
                        CellNum = _selectedObjCellNum,
                        ObjectIndex = _selectedObjIndex,
                        Stab = stab
                    };
                }
            }
            else if (_selectedInstancePlacementIndex >= 0 && _ctx.Document != null &&
                     _selectedInstancePlacementIndex < _ctx.Document.InstancePlacements.Count) {
                var placement = _ctx.Document.InstancePlacements[_selectedInstancePlacementIndex];
                UpdateInstancePlacementSelectionHighlight(placement);
                placementArgs = new InstancePlacementSelectionChangedArgs {
                    Index = _selectedInstancePlacementIndex,
                    Placement = placement
                };
            }
            else {
                if (_ctx.Scene != null) {
                    _ctx.Scene.SelectedObjectBounds = null;
                    _ctx.Scene.SelectedObjectPosition = null;
                }
            }

            return (cellArgs, objArgs, placementArgs);
        }

        public string BuildCellInfoString(LoadedEnvCell cell, bool includeStatics, RoomPaletteViewModel? palette, DungeonDocument? document) {
            int portalCount = cell.Portals?.Count ?? 0;
            int connectedPortals = 0;
            if (cell.Portals != null) {
                foreach (var p in cell.Portals) {
                    if (p.OtherCellId != 0 && p.OtherCellId != 0xFFFF) connectedPortals++;
                }
            }

            var roomName = palette?.GetRoomDisplayName(cell.EnvironmentId, (ushort)cell.GpuKey.CellStructure);
            var roomLabel = !string.IsNullOrEmpty(roomName) ? roomName : $"Room 0x{cell.CellId:X8}";

            var sb = new System.Text.StringBuilder();
            sb.Append($"{roomLabel}\n");
            sb.Append($"Doorways: {connectedPortals}/{portalCount} connected  |  Textures: {cell.SurfaceCount}");
            if (includeStatics && cell.Portals != null) {
                var connected = cell.Portals
                    .Where(p => p.OtherCellId != 0 && p.OtherCellId != 0xFFFF)
                    .Select(p => $"0x{p.OtherCellId:X4}")
                    .Distinct()
                    .ToList();
                if (connected.Count > 0) {
                    sb.Append($"\nConnects to: {string.Join(", ", connected)}");
                }
            }
            if (includeStatics) {
                int staticCount = 0;
                var dc = document?.GetCell((ushort)(cell.CellId & 0xFFFF));
                if (dc != null) staticCount = dc.StaticObjects.Count;
                sb.Append($"\nStatics: {staticCount}");
            }
            return sb.ToString();
        }

        public static string ComputeCellTeleportLine(LoadedEnvCell cell, DungeonDocument? document, IDatReaderWriter? dats = null) {
            var dc = document?.GetCell((ushort)(cell.CellId & 0xFFFF));
            if (dc == null) return $"0x{cell.CellId:X8}";

            var pos = ComputeFloorCentroid(dc, dats);
            return $"0x{cell.CellId:X8} [{pos.X:F6} {pos.Y:F6} {pos.Z:F6}] 1.000000 0.000000 0.000000 0.000000";
        }

        /// <summary>
        /// Compute a world-space position at the center of the cell floor.
        /// Uses the cell geometry vertices to find the centroid at floor level.
        /// </summary>
        private static Vector3 ComputeFloorCentroid(DungeonCellData dc, IDatReaderWriter? dats) {
            if (dats == null) return dc.Origin;

            uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return dc.Origin;
            if (!env.Cells.TryGetValue(dc.CellStructure, out var cs)) return dc.Origin;
            if (cs.VertexArray?.Vertices == null || cs.VertexArray.Vertices.Count == 0) return dc.Origin;

            var rot = dc.Orientation;
            if (rot.LengthSquared() < 0.01f) rot = Quaternion.Identity;
            rot = Quaternion.Normalize(rot);

            float minZ = float.MaxValue;
            var centroid = Vector3.Zero;
            int count = 0;
            foreach (var vtx in cs.VertexArray.Vertices.Values) {
                var worldPos = dc.Origin + Vector3.Transform(vtx.Origin, rot);
                centroid += worldPos;
                if (worldPos.Z < minZ) minZ = worldPos.Z;
                count++;
            }

            if (count == 0) return dc.Origin;
            centroid /= count;
            centroid.Z = minZ + 0.005f;
            return centroid;
        }

        public static IReadOnlyList<(Vector3 From, Vector3 To)> ComputeConnectionLines(
            DungeonDocument? document, IDatReaderWriter? dats) {
            var result = new List<(Vector3 From, Vector3 To)>();
            if (document == null || dats == null || document.Cells.Count == 0) return result;

            int totalPortals = document.Cells.Sum(c => c.CellPortals.Count);
            if (totalPortals == 0) {
                Console.WriteLine($"[ConnectionLines] No portal data in document ({document.Cells.Count} cells, 0 portals)");
                return result;
            }

            uint lbId = document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);
            const float dungeonZBump = -50f;

            var drawn = new HashSet<(ushort, ushort)>();

            foreach (var cell in document.Cells) {
                foreach (var cp in cell.CellPortals) {
                    if (cp.OtherCellId == 0 || cp.OtherCellId == 0xFFFF) continue;

                    var otherCell = document.GetCell(cp.OtherCellId);
                    if (otherCell == null) continue;

                    var key = (Math.Min(cell.CellNumber, cp.OtherCellId), Math.Max(cell.CellNumber, cp.OtherCellId));
                    if (drawn.Contains(key)) continue;
                    drawn.Add(key);

                    uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
                    if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env) ||
                        !env.Cells.TryGetValue(cell.CellStructure, out var cellStruct)) continue;

                    var geomA = PortalSnapper.GetPortalGeometry(cellStruct, cp.PolygonId);
                    if (geomA == null) continue;

                    var worldOriginA = cell.Origin + lbOffset + new Vector3(0, 0, dungeonZBump);
                    var (centroidA, _) = PortalSnapper.TransformPortalToWorld(geomA.Value, worldOriginA, cell.Orientation);

                    uint otherEnvFileId = (uint)(otherCell.EnvironmentId | 0x0D000000);
                    if (!dats.TryGet<Acme.Dat.Environment>(otherEnvFileId, out var otherEnv) ||
                        !otherEnv.Cells.TryGetValue(otherCell.CellStructure, out var otherCellStruct)) continue;

                    var backPortal = otherCell.CellPortals.FirstOrDefault(p => p.OtherCellId == cell.CellNumber);
                    if (backPortal == null) continue;

                    var geomB = PortalSnapper.GetPortalGeometry(otherCellStruct, backPortal.PolygonId);
                    if (geomB == null) continue;

                    var worldOriginB = otherCell.Origin + lbOffset + new Vector3(0, 0, dungeonZBump);
                    var (centroidB, _) = PortalSnapper.TransformPortalToWorld(geomB.Value, worldOriginB, otherCell.Orientation);

                    result.Add((centroidA, centroidB));
                }
            }

            if (result.Count == 0 && totalPortals > 0) {
                int backPortalMissing = 0, geomMissing = 0;
                foreach (var cell in document.Cells) {
                    foreach (var cp in cell.CellPortals) {
                        if (cp.OtherCellId == 0 || cp.OtherCellId == 0xFFFF) continue;
                        var otherCell = document.GetCell(cp.OtherCellId);
                        if (otherCell == null) continue;
                        var back = otherCell.CellPortals.FirstOrDefault(p => p.OtherCellId == cell.CellNumber);
                        if (back == null) { backPortalMissing++; continue; }
                        uint ef = (uint)(cell.EnvironmentId | 0x0D000000);
                        if (!dats.TryGet<Acme.Dat.Environment>(ef, out var env) ||
                            !env.Cells.TryGetValue(cell.CellStructure, out var cs)) { geomMissing++; continue; }
                        var geom = PortalSnapper.GetPortalGeometry(cs, cp.PolygonId);
                        if (geom == null) geomMissing++;
                    }
                }
                Console.WriteLine($"[ConnectionLines] 0 lines despite {totalPortals} portals — backPortalMissing={backPortalMissing}, geomMissing={geomMissing}");
            }
            else if (result.Count > 0) {
                Console.WriteLine($"[ConnectionLines] Computed {result.Count} connection lines from {totalPortals} portals");
            }

            return result;
        }

        /// <summary>
        /// Returns the set of cell numbers that are connected via portals to any of
        /// the currently selected cells (excluding the selected cells themselves).
        /// </summary>
        public HashSet<ushort> GetConnectedNeighborCellNums() {
            var result = new HashSet<ushort>();
            if (_ctx.Document == null) return result;
            var selectedNums = new HashSet<ushort>(_selectedCells.Select(c => (ushort)(c.CellId & 0xFFFF)));
            foreach (var num in selectedNums) {
                var dc = _ctx.Document.GetCell(num);
                if (dc == null) continue;
                foreach (var cp in dc.CellPortals) {
                    if (cp.OtherCellId != 0 && cp.OtherCellId != 0xFFFF && !selectedNums.Contains(cp.OtherCellId))
                        result.Add(cp.OtherCellId);
                }
            }
            return result;
        }

        /// <summary>
        /// Compute connection lines that touch any of the currently selected cells.
        /// Returns portal centroid pairs in world-space, matching ConnectionLines format.
        /// </summary>
        public IReadOnlyList<(Vector3 From, Vector3 To)> ComputeSelectedConnectionLines(
            DungeonDocument? document, IDatReaderWriter? dats) {
            var result = new List<(Vector3 From, Vector3 To)>();
            if (document == null || dats == null || _selectedCells.Count == 0) return result;

            var selectedNums = new HashSet<ushort>(_selectedCells.Select(c => (ushort)(c.CellId & 0xFFFF)));

            uint lbId = document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);
            const float dungeonZBump = -50f;

            var drawn = new HashSet<(ushort, ushort)>();

            foreach (var cell in document.Cells) {
                if (!selectedNums.Contains(cell.CellNumber)) continue;
                foreach (var cp in cell.CellPortals) {
                    if (cp.OtherCellId == 0 || cp.OtherCellId == 0xFFFF) continue;
                    var otherCell = document.GetCell(cp.OtherCellId);
                    if (otherCell == null) continue;

                    var key = (Math.Min(cell.CellNumber, cp.OtherCellId), Math.Max(cell.CellNumber, cp.OtherCellId));
                    if (drawn.Contains(key)) continue;
                    drawn.Add(key);

                    uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
                    if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env) ||
                        !env.Cells.TryGetValue(cell.CellStructure, out var cellStruct)) continue;

                    var geomA = PortalSnapper.GetPortalGeometry(cellStruct, cp.PolygonId);
                    if (geomA == null) continue;

                    var worldOriginA = cell.Origin + lbOffset + new Vector3(0, 0, dungeonZBump);
                    var (centroidA, _) = PortalSnapper.TransformPortalToWorld(geomA.Value, worldOriginA, cell.Orientation);

                    uint otherEnvFileId = (uint)(otherCell.EnvironmentId | 0x0D000000);
                    if (!dats.TryGet<Acme.Dat.Environment>(otherEnvFileId, out var otherEnv) ||
                        !otherEnv.Cells.TryGetValue(otherCell.CellStructure, out var otherCellStruct)) continue;

                    var backPortal = otherCell.CellPortals.FirstOrDefault(p => p.OtherCellId == cell.CellNumber);
                    if (backPortal == null) continue;

                    var geomB = PortalSnapper.GetPortalGeometry(otherCellStruct, backPortal.PolygonId);
                    if (geomB == null) continue;

                    var worldOriginB = otherCell.Origin + lbOffset + new Vector3(0, 0, dungeonZBump);
                    var (centroidB, _) = PortalSnapper.TransformPortalToWorld(geomB.Value, worldOriginB, otherCell.Orientation);

                    result.Add((centroidA, centroidB));
                }
            }
            return result;
        }

        private void SyncSceneSelection() {
            if (_ctx.Scene != null)
                _ctx.Scene.SelectedCells = _selectedCells.ToList();
        }

        private void SyncToContext() {
            _ctx.SelectedCell = _selectedCell;
            _ctx.SelectedCells.Clear();
            _ctx.SelectedCells.AddRange(_selectedCells);
            _ctx.SelectedObjCellNum = _selectedObjCellNum;
            _ctx.SelectedObjIndex = _selectedObjIndex;
            _ctx.SelectedInstancePlacementIndex = _selectedInstancePlacementIndex;
        }
    }
}
