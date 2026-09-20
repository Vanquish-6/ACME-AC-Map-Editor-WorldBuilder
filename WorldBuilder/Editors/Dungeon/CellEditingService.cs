using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Acme.Dat;
using Acme.Render.GL.Lib;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// Encapsulates cell mutation operations: nudge, rotate, delete, surfaces,
    /// portals, copy/paste. Operates on DungeonEditingContext and Selection.
    /// </summary>
    public class CellEditingService {
        private readonly DungeonEditingContext _ctx;
        private readonly DungeonSelectionManager _selection;
        private readonly Func<IDatReaderWriter?> _getDats;
        private readonly Func<RoomPaletteViewModel?> _getPalette;

        private List<DungeonCellData>? _cellClipboard;

        public CellEditingService(
            DungeonEditingContext ctx,
            DungeonSelectionManager selection,
            Func<IDatReaderWriter?> getDats,
            Func<RoomPaletteViewModel?> getPalette) {
            _ctx = ctx;
            _selection = selection;
            _getDats = getDats;
            _getPalette = getPalette;
        }

        public (int removed, int remaining)? DeleteSelectedCells() {
            if (_selection.SelectedCells.Count == 0 || _ctx.Document == null) return null;
            var toRemove = _selection.SelectedCells.Select(c => (ushort)(c.CellId & 0xFFFF)).ToList();
            _selection.DeselectCell();
            foreach (var cellNum in toRemove)
                _ctx.CommandHistory.Execute(new RemoveCellCommand(cellNum), _ctx.Document);
            return (toRemove.Count, _ctx.Document.Cells.Count);
        }

        public void RotateSelectedCell(float degrees, Vector3 axis) {
            if (_selection.SelectedCell == null || _ctx.Document == null) return;
            var cellNum = (ushort)(_selection.SelectedCell.CellId & 0xFFFF);
            _ctx.CommandHistory.Execute(new RotateCellCommand(cellNum, degrees, axis), _ctx.Document);
        }

        public void NudgeSelectedCell(Vector3 offset) {
            if (_ctx.Document == null) return;
            var nums = _selection.SelectedCells.Select(c => (ushort)(c.CellId & 0xFFFF)).Distinct().ToList();
            if (nums.Count == 0 && _selection.SelectedCell != null)
                nums.Add((ushort)(_selection.SelectedCell.CellId & 0xFFFF));
            if (nums.Count == 0) return;
            if (nums.Count == 1) {
                _ctx.CommandHistory.Execute(new NudgeCellCommand(nums[0], offset), _ctx.Document);
                return;
            }
            var composite = new DungeonCompositeCommand("Move rooms");
            foreach (var n in nums)
                composite.Add(new NudgeCellCommand(n, offset));
            _ctx.CommandHistory.Execute(composite, _ctx.Document);
        }

        public string? ApplyPosition(string posX, string posY, string posZ) {
            if (_selection.SelectedCell == null || _ctx.Document == null) return null;
            if (!float.TryParse(posX, out var x) || !float.TryParse(posY, out var y) || !float.TryParse(posZ, out var z))
                return "Invalid position values";
            var cellNum = (ushort)(_selection.SelectedCell.CellId & 0xFFFF);
            var dc = _ctx.Document.GetCell(cellNum);
            if (dc == null) return null;
            var delta = new Vector3(x, y, z) - dc.Origin;
            if (delta.LengthSquared() < 0.001f) return null;
            _ctx.CommandHistory.Execute(new NudgeCellCommand(cellNum, delta), _ctx.Document);
            return $"Room {cellNum:X4} moved to ({x:F1}, {y:F1}, {z:F1})";
        }

        public string? ApplyRotation(string rotX, string rotY, string rotZ) {
            if (_selection.SelectedCell == null || _ctx.Document == null) return null;
            if (!float.TryParse(rotX, out var rx) || !float.TryParse(rotY, out var ry) || !float.TryParse(rotZ, out var rz))
                return "Invalid rotation values";
            var cellNum = (ushort)(_selection.SelectedCell.CellId & 0xFFFF);
            var dc = _ctx.Document.GetCell(cellNum);
            if (dc == null) return null;
            var newOrientation = EulerToQuat(new Vector3(rx, ry, rz));
            _ctx.CommandHistory.Execute(
                new SetCellOrientationCommand(cellNum, dc.Orientation, newOrientation),
                _ctx.Document);
            return $"Room {cellNum:X4} rotation set to ({rx:F1}, {ry:F1}, {rz:F1})";
        }

        public string? ApplySurfaces(string surfaceText) {
            if (_selection.SelectedCells.Count == 0 || _ctx.Document == null) return null;

            var newSurfaces = new List<ushort>();
            foreach (var part in surfaceText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                var hex = part.TrimStart('0', 'x', 'X');
                if (string.IsNullOrEmpty(hex)) hex = "0";
                if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var surfId))
                    newSurfaces.Add(surfId);
            }
            if (newSurfaces.Count == 0) return null;

            var composite = new DungeonCompositeCommand("Change Surfaces");
            int count = 0;
            foreach (var cell in _selection.SelectedCells) {
                var cellNum = (ushort)(cell.CellId & 0xFFFF);
                var dc = _ctx.Document.GetCell(cellNum);
                if (dc == null) continue;
                composite.Add(new SetCellSurfacesCommand(cellNum, dc.Surfaces, newSurfaces));
                count++;
            }
            if (count == 0) return null;
            _ctx.CommandHistory.Execute(composite, _ctx.Document);
            return $"Updated textures on {count} room(s)";
        }

        /// <returns>Status message, plus the updated surfaces string and the refreshed DungeonCellData for UI update.</returns>
        public (string status, string? surfaceText, DungeonCellData? primaryDc) ApplySurfaceFromBrowser(ushort surfaceId, int slotIndex) {
            if (_selection.SelectedCells.Count == 0 || _ctx.Document == null)
                return ("Select a room first, then pick a texture", null, null);

            var composite = new DungeonCompositeCommand("Apply Surface");
            int count = 0;
            foreach (var cell in _selection.SelectedCells) {
                var cellNum = (ushort)(cell.CellId & 0xFFFF);
                var dc = _ctx.Document.GetCell(cellNum);
                if (dc == null) continue;

                var newSurfaces = new List<ushort>(dc.Surfaces);
                if (slotIndex >= 0 && slotIndex < newSurfaces.Count) {
                    newSurfaces[slotIndex] = surfaceId;
                } else {
                    for (int i = 0; i < newSurfaces.Count; i++)
                        newSurfaces[i] = surfaceId;
                    if (newSurfaces.Count == 0)
                        newSurfaces.Add(surfaceId);
                }
                composite.Add(new SetCellSurfacesCommand(cellNum, dc.Surfaces, newSurfaces));
                count++;
            }

            if (count > 0)
                _ctx.CommandHistory.Execute(composite, _ctx.Document);

            var primaryDc = _ctx.Document.GetCell((ushort)(_selection.SelectedCell!.CellId & 0xFFFF));
            string? surfText = primaryDc != null ? string.Join(", ", primaryDc.Surfaces.Select(s => s.ToString("X4"))) : null;

            var status = slotIndex >= 0
                ? $"Texture {slotIndex + 1}: applied 0x{surfaceId:X4} to {count} room(s)"
                : $"Applied texture 0x{surfaceId:X4} to all textures on {count} room(s)";
            return (status, surfText, primaryDc);
        }

        public void DisconnectLastPortal() {
            if (_selection.SelectedCell == null || _ctx.Document == null) return;
            var cellNum = (ushort)(_selection.SelectedCell.CellId & 0xFFFF);
            var dc = _ctx.Document.GetCell(cellNum);
            if (dc == null || dc.CellPortals.Count == 0) return;
            _ctx.CommandHistory.Execute(new DisconnectPortalCommand(cellNum, dc.CellPortals.Count - 1), _ctx.Document);
        }

        public void DisconnectPortalAt(int index) {
            if (_selection.SelectedCell == null || _ctx.Document == null) return;
            var cellNum = (ushort)(_selection.SelectedCell.CellId & 0xFFFF);
            var dc = _ctx.Document.GetCell(cellNum);
            if (dc == null || index < 0 || index >= dc.CellPortals.Count) return;
            _ctx.CommandHistory.Execute(new DisconnectPortalCommand(cellNum, index), _ctx.Document);
        }

        public List<PortalListEntry> BuildPortalList() {
            var list = new List<PortalListEntry>();
            var dats = _getDats();
            var palette = _getPalette();
            if (_selection.SelectedCell == null || _ctx.Document == null || dats == null) return list;

            var cellNum = (ushort)(_selection.SelectedCell.CellId & 0xFFFF);
            var dc = _ctx.Document.GetCell(cellNum);
            if (dc == null) return list;

            uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
            var allPortalPolyIds = new List<ushort>();
            if (dats.TryGet<Acme.Dat.Environment>(envFileId, out var env) &&
                env.Cells.TryGetValue(dc.CellStructure, out var cellStruct)) {
                allPortalPolyIds = PortalSnapper.GetPortalPolygonIds(cellStruct);
            }

            var connectedPolyIds = new HashSet<ushort>(dc.CellPortals.Select(cp => cp.PolygonId));
            int idx = 0;
            foreach (var cp in dc.CellPortals) {
                string? connectedName = null;
                var otherDc = _ctx.Document.GetCell(cp.OtherCellId);
                if (otherDc != null) {
                    connectedName = palette?.GetRoomDisplayName(
                        (uint)(otherDc.EnvironmentId | 0x0D000000), otherDc.CellStructure);
                }
                list.Add(new PortalListEntry(idx, cp.PolygonId, cp.OtherCellId, isConnected: true, connectedName,
                    ownerCellNum: cellNum, ownerEnvId: dc.EnvironmentId, ownerCellStruct: dc.CellStructure));
                idx++;
            }

            foreach (var polyId in allPortalPolyIds) {
                if (!connectedPolyIds.Contains(polyId)) {
                    int compatCount = 0;
                    if (_ctx.PortalIndex != null) {
                        var compat = _ctx.PortalIndex.GetCompatible(dc.EnvironmentId, dc.CellStructure, polyId);
                        compatCount = compat.Count;
                    }
                    list.Add(new PortalListEntry(-1, polyId, 0, isConnected: false,
                        ownerCellNum: cellNum, ownerEnvId: dc.EnvironmentId, ownerCellStruct: dc.CellStructure,
                        compatibleCount: compatCount));
                }
            }
            return list;
        }

        public List<CellSurfaceSlot> BuildSurfaceSlots(DungeonCellData dc) {
            var dats = _getDats();
            var slots = new List<CellSurfaceSlot>(dc.Surfaces.Count);
            for (int i = 0; i < dc.Surfaces.Count; i++) {
                var surfId = dc.Surfaces[i];
                var fullId = (uint)(surfId | 0x08000000);
                var slot = new CellSurfaceSlot(i, surfId, $"Texture {i + 1} (0x{surfId:X4})");

                var localSlot = slot;
                var localDats = dats;
                if (localDats != null) {
                    System.Threading.Tasks.Task.Run(() => {
                        var thumb = SurfaceBrowserViewModel.CreateBitmap(
                            GenerateSmallSurfaceThumb(localDats, fullId), 32, 32);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => localSlot.Thumbnail = thumb);
                    });
                }
                slots.Add(slot);
            }
            return slots;
        }

        public string? CopySelectedCells() {
            if (_selection.SelectedCells.Count == 0 || _ctx.Document == null) {
                Console.WriteLine($"[Dungeon] Copy: nothing to copy (selected={_selection.SelectedCells.Count}, doc={(_ctx.Document != null ? "yes" : "null")})");
                return null;
            }

            _cellClipboard = new List<DungeonCellData>();
            foreach (var cell in _selection.SelectedCells) {
                var cellNum = (ushort)(cell.CellId & 0xFFFF);
                var dc = _ctx.Document.GetCell(cellNum);
                if (dc == null) continue;
                _cellClipboard.Add(DeepCloneCell(dc));
            }
            Console.WriteLine($"[Dungeon] Copied {_cellClipboard.Count} cell(s) to clipboard");
            return $"Copied {_cellClipboard.Count} room(s)";
        }

        /// <returns>Status message and list of created cell numbers, or null if nothing to paste.</returns>
        public (string status, IReadOnlyList<ushort> createdNums)? PasteCells() {
            Console.WriteLine($"[Dungeon] Paste: clipboard={_cellClipboard?.Count ?? 0}, doc={(_ctx.Document != null ? "yes" : "null")}");
            if (_cellClipboard == null || _cellClipboard.Count == 0 || _ctx.Document == null) return null;

            var offset = new Vector3(10f, 0f, 0f);
            var cmd = new PasteCellsCommand(_cellClipboard, offset);
            _ctx.CommandHistory.Execute(cmd, _ctx.Document);
            return ($"Pasted {_cellClipboard.Count} room(s)", cmd.CreatedCellNums);
        }

        public List<(string Label, Action Action, bool IsEnabled)> GetCellContextMenuItems(
            Vector3 rayOrigin, Vector3 rayDir,
            Action deleteAction, Action favoriteAction, Action? saveAsPrefabAction = null) {
            var items = new List<(string Label, Action Action, bool IsEnabled)>();
            var dats = _getDats();
            var palette = _getPalette();
            if (_ctx.Scene?.EnvCellManager == null || _ctx.Document == null || dats == null) return items;

            var hit = _ctx.Scene.EnvCellManager.Raycast(rayOrigin, rayDir);
            if (!hit.Hit) return items;

            var hitCell = hit.Cell;
            var cellNum = (ushort)(hitCell.CellId & 0xFFFF);
            var dc = _ctx.Document.GetCell(cellNum);
            if (dc == null) return items;

            _selection.SelectCell(hitCell);

            uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return items;
            if (!env.Cells.TryGetValue(dc.CellStructure, out var cellStruct)) return items;

            var allPortalPolyIds = PortalSnapper.GetPortalPolygonIds(cellStruct);
            var connectedPolyIds = new HashSet<ushort>(dc.CellPortals.Select(cp => cp.PolygonId));

            var roomName = palette?.GetRoomDisplayName(hitCell.EnvironmentId, (ushort)hitCell.GpuKey.CellStructure);
            var roomLabel = !string.IsNullOrEmpty(roomName) ? roomName : $"Room 0x{cellNum:X4}";
            items.Add(($"— {roomLabel} —", () => { }, false));

            for (int i = 0; i < dc.CellPortals.Count; i++) {
                var cp = dc.CellPortals[i];
                int portalIdx = i;
                var otherDc = _ctx.Document.GetCell(cp.OtherCellId);
                string targetLabel;
                if (otherDc != null) {
                    var otherName = palette?.GetRoomDisplayName(
                        (uint)(otherDc.EnvironmentId | 0x0D000000), otherDc.CellStructure);
                    targetLabel = !string.IsNullOrEmpty(otherName) ? otherName : $"Room 0x{cp.OtherCellId:X4}";
                } else {
                    targetLabel = $"Room 0x{cp.OtherCellId:X4}";
                }
                items.Add(($"Disconnect Doorway {i + 1} → {targetLabel}",
                    () => DisconnectPortalAt(portalIdx), true));
            }

            int openCount = allPortalPolyIds.Count(p => !connectedPolyIds.Contains(p));
            if (openCount > 0) {
                items.Add(($"{openCount} open doorway{(openCount != 1 ? "s" : "")}", () => { }, false));
            }

            items.Add(("Delete Room", deleteAction, true));
            items.Add(("Favorite This Room", favoriteAction, true));
            if (saveAsPrefabAction != null) {
                var selCount = _selection.SelectedCells.Count;
                var label = selCount > 1
                    ? $"Save {selCount} Rooms as Favorite Piece"
                    : "Save as Favorite Piece";
                items.Add((label, saveAsPrefabAction, selCount > 0));
            }
            return items;
        }

        public static Vector3 QuatToEuler(Quaternion q) {
            float sinr = 2f * (q.W * q.X + q.Y * q.Z);
            float cosr = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float rx = MathF.Atan2(sinr, cosr) * 180f / MathF.PI;

            float sinp = 2f * (q.W * q.Y - q.Z * q.X);
            float ry = MathF.Abs(sinp) >= 1f
                ? MathF.CopySign(90f, sinp)
                : MathF.Asin(sinp) * 180f / MathF.PI;

            float siny = 2f * (q.W * q.Z + q.X * q.Y);
            float cosy = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float rz = MathF.Atan2(siny, cosy) * 180f / MathF.PI;

            return new Vector3(rx, ry, rz);
        }

        public static Quaternion EulerToQuat(Vector3 eulerDeg) {
            float rx = eulerDeg.X * MathF.PI / 180f;
            float ry = eulerDeg.Y * MathF.PI / 180f;
            float rz = eulerDeg.Z * MathF.PI / 180f;
            return Quaternion.Normalize(
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rz) *
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, ry) *
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, rx));
        }

        public static byte[] GenerateSmallSurfaceThumb(IDatReaderWriter dats, uint surfaceId) {
            const int sz = 32;
            try {
                if (!dats.TryGet<Surface>(surfaceId, out var surface))
                    return CreateFallbackThumb(sz);
                if (surface.Type.HasFlag(SurfaceType.Base1Solid))
                    return TextureHelpers.CreateSolidColorTexture(surface.ColorValue, sz, sz);
                if (!dats.TryGet<SurfaceTexture>(surface.OrigTextureId, out var surfTex) ||
                    surfTex.Textures?.Any() != true)
                    return CreateFallbackThumb(sz);
                var rsId = surfTex.Textures.Last();
                if (!dats.TryGet<RenderSurface>(rsId, out var rs))
                    return CreateFallbackThumb(sz);
                if (rs.FormatEnum == PixelFormat.PFID_A8R8G8B8 && rs.SourceData.Length >= sz * sz * 4) {
                    var downsampled = SurfaceBrowserViewModel.DownsampleNearest(rs.SourceData, rs.Width, rs.Height, sz, sz);
                    return DatIconLoader.SwizzleBgraToRgba(downsampled, sz * sz);
                }
            }
            catch { }
            return CreateFallbackThumb(sz);
        }

        public static byte[] CreateFallbackThumb(int sz) {
            var data = new byte[sz * sz * 4];
            for (int i = 0; i < data.Length; i += 4) {
                data[i] = 60; data[i + 1] = 40; data[i + 2] = 80; data[i + 3] = 255;
            }
            return data;
        }

        public static DungeonCellData DeepCloneCell(DungeonCellData src) {
            var clone = new DungeonCellData {
                CellNumber = src.CellNumber,
                EnvironmentId = src.EnvironmentId,
                CellStructure = src.CellStructure,
                Origin = src.Origin,
                Orientation = src.Orientation,
                Flags = src.Flags,
                RestrictionObj = src.RestrictionObj,
            };
            clone.Surfaces.AddRange(src.Surfaces);
            foreach (var cp in src.CellPortals) {
                clone.CellPortals.Add(new DungeonCellPortalData {
                    OtherCellId = cp.OtherCellId,
                    PolygonId = cp.PolygonId,
                    OtherPortalId = cp.OtherPortalId,
                    Flags = cp.Flags
                });
            }
            clone.VisibleCells.AddRange(src.VisibleCells);
            foreach (var stab in src.StaticObjects) {
                clone.StaticObjects.Add(new DungeonStabData {
                    Id = stab.Id,
                    Origin = stab.Origin,
                    Orientation = stab.Orientation
                });
            }
            return clone;
        }

        /// <summary>
        /// Extracts the currently selected cells into a DungeonPrefab that can be
        /// saved as a custom/favorite piece for reuse in other dungeons.
        /// </summary>
        public DungeonPrefab? ExtractSelectionAsPrefab(string? displayName = null) {
            if (_selection.SelectedCells.Count == 0 || _ctx.Document == null) return null;

            var dats = _getDats();
            var cells = new List<DungeonCellData>();
            var cellNumSet = new HashSet<ushort>();

            foreach (var selected in _selection.SelectedCells) {
                var cellNum = (ushort)(selected.CellId & 0xFFFF);
                var dc = _ctx.Document.GetCell(cellNum);
                if (dc == null) continue;
                cells.Add(dc);
                cellNumSet.Add(cellNum);
            }

            if (cells.Count == 0) return null;

            var first = cells[0];
            var originPos = first.Origin;
            var originRot = first.Orientation;
            if (originRot.LengthSquared() < 0.01f) originRot = Quaternion.Identity;
            originRot = Quaternion.Normalize(originRot);
            var invRot = Quaternion.Conjugate(originRot);

            var prefab = new DungeonPrefab {
                SourceLandblock = _ctx.Document.LandblockKey,
                SourceDungeonName = "Custom",
                UsageCount = 1,
                DisplayName = displayName ?? $"Custom Piece ({cells.Count} cell{(cells.Count != 1 ? "s" : "")})",
                Category = "Custom",
                Style = "Custom",
            };

            var cellIndexMap = new Dictionary<ushort, int>();
            for (int i = 0; i < cells.Count; i++) {
                var dc = cells[i];
                cellIndexMap[dc.CellNumber] = i;

                var relPos = Vector3.Transform(dc.Origin - originPos, invRot);
                var relRot = Quaternion.Normalize(invRot * Quaternion.Normalize(
                    dc.Orientation.LengthSquared() > 0.01f ? dc.Orientation : Quaternion.Identity));

                prefab.Cells.Add(new PrefabCell {
                    LocalIndex = i,
                    EnvId = dc.EnvironmentId,
                    CellStruct = dc.CellStructure,
                    PortalCount = dc.CellPortals.Count,
                    OffsetX = relPos.X, OffsetY = relPos.Y, OffsetZ = relPos.Z,
                    RotX = relRot.X, RotY = relRot.Y, RotZ = relRot.Z, RotW = relRot.W,
                    Surfaces = new List<ushort>(dc.Surfaces),
                });
            }

            foreach (var dc in cells) {
                int myIdx = cellIndexMap[dc.CellNumber];
                foreach (var cp in dc.CellPortals) {
                    if (cellNumSet.Contains(cp.OtherCellId) && cellIndexMap.TryGetValue(cp.OtherCellId, out int otherIdx)) {
                        if (myIdx < otherIdx) {
                            prefab.InternalPortals.Add(new PrefabPortal {
                                CellIndexA = myIdx, PolyIdA = cp.PolygonId,
                                CellIndexB = otherIdx, PolyIdB = cp.OtherPortalId,
                            });
                        }
                    }
                    else {
                        float nx = 0, ny = 0, nz = 0;
                        if (dats != null) {
                            uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
                            if (dats.TryGet<Acme.Dat.Environment>(envFileId, out var env) &&
                                env.Cells.TryGetValue(dc.CellStructure, out var cs)) {
                                var geom = PortalSnapper.GetPortalGeometry(cs, cp.PolygonId);
                                if (geom != null) {
                                    var cellRot = Quaternion.Normalize(new Quaternion(
                                        prefab.Cells[myIdx].RotX, prefab.Cells[myIdx].RotY,
                                        prefab.Cells[myIdx].RotZ, prefab.Cells[myIdx].RotW));
                                    var worldNormal = Vector3.Transform(geom.Value.Normal, cellRot);
                                    nx = worldNormal.X; ny = worldNormal.Y; nz = worldNormal.Z;
                                }
                            }
                        }

                        prefab.OpenFaces.Add(new PrefabOpenFace {
                            CellIndex = myIdx, PolyId = cp.PolygonId,
                            EnvId = dc.EnvironmentId,
                            CellStruct = dc.CellStructure,
                            NormalX = nx, NormalY = ny, NormalZ = nz,
                        });
                    }
                }

                // Also check for portal polygon IDs that aren't connected to anything
                if (dats != null) {
                    uint envFileId2 = (uint)(dc.EnvironmentId | 0x0D000000);
                    if (dats.TryGet<Acme.Dat.Environment>(envFileId2, out var env2) &&
                        env2.Cells.TryGetValue(dc.CellStructure, out var cs2)) {
                        var allPortalPolyIds = PortalSnapper.GetPortalPolygonIds(cs2);
                        var connectedPolyIds = new HashSet<ushort>(dc.CellPortals.Select(c => c.PolygonId));
                        foreach (var polyId in allPortalPolyIds) {
                            if (connectedPolyIds.Contains(polyId)) continue;
                            float nx = 0, ny = 0, nz = 0;
                            var geom = PortalSnapper.GetPortalGeometry(cs2, polyId);
                            if (geom != null) {
                                var cellRot = Quaternion.Normalize(new Quaternion(
                                    prefab.Cells[myIdx].RotX, prefab.Cells[myIdx].RotY,
                                    prefab.Cells[myIdx].RotZ, prefab.Cells[myIdx].RotW));
                                var worldNormal = Vector3.Transform(geom.Value.Normal, cellRot);
                                nx = worldNormal.X; ny = worldNormal.Y; nz = worldNormal.Z;
                            }
                            prefab.OpenFaces.Add(new PrefabOpenFace {
                                CellIndex = myIdx, PolyId = polyId,
                                EnvId = dc.EnvironmentId,
                                CellStruct = dc.CellStructure,
                                NormalX = nx, NormalY = ny, NormalZ = nz,
                            });
                        }
                    }
                }
            }

            foreach (var of in prefab.OpenFaces) {
                prefab.OpenFaceDirections.Add(DungeonPrefab.ClassifyDirection(of.NormalX, of.NormalY, of.NormalZ));
            }

            var timestamp = DateTime.UtcNow.Ticks;
            prefab.Signature = $"custom_{timestamp}_{string.Join("|",
                prefab.Cells.OrderBy(c => c.EnvId).ThenBy(c => c.CellStruct)
                    .Select(c => $"{c.EnvId:X4}_{c.CellStruct}"))}";

            Console.WriteLine($"[Dungeon] Extracted custom prefab: {prefab.DisplayName} ({prefab.Cells.Count} cells, {prefab.OpenFaces.Count} open faces)");
            return prefab;
        }

        /// <summary>
        /// Extracts all cells in the document into a single DungeonPrefab.
        /// Does not require any selection — uses the full document.
        /// </summary>
        public DungeonPrefab? ExtractDocumentAsPrefab(string? displayName = null) {
            if (_ctx.Document == null || _ctx.Document.Cells.Count == 0) return null;

            var dats = _getDats();
            var cells = _ctx.Document.Cells.ToList();
            var cellNumSet = new HashSet<ushort>(cells.Select(c => c.CellNumber));

            var first = cells[0];
            var originPos = first.Origin;
            var originRot = first.Orientation;
            if (originRot.LengthSquared() < 0.01f) originRot = Quaternion.Identity;
            originRot = Quaternion.Normalize(originRot);
            var invRot = Quaternion.Conjugate(originRot);

            var dungeonName = displayName;
            if (string.IsNullOrEmpty(dungeonName)) {
                var lbKey = (ushort)_ctx.Document.LandblockKey;
                dungeonName = WorldBuilder.Lib.LocationDatabase.Dungeons
                    .FirstOrDefault(d => d.LandblockId == lbKey)?.Name?.Trim();
            }

            var prefab = new DungeonPrefab {
                SourceLandblock = _ctx.Document.LandblockKey,
                SourceDungeonName = dungeonName ?? "Custom",
                UsageCount = 1,
                DisplayName = dungeonName ?? $"Custom Dungeon ({cells.Count} cell{(cells.Count != 1 ? "s" : "")})",
                Category = "Full Dungeon",
                Style = PrefabNamer.InferStyle(dungeonName ?? ""),
            };

            var cellIndexMap = new Dictionary<ushort, int>();
            for (int i = 0; i < cells.Count; i++) {
                var dc = cells[i];
                cellIndexMap[dc.CellNumber] = i;

                var relPos = Vector3.Transform(dc.Origin - originPos, invRot);
                var relRot = Quaternion.Normalize(invRot * Quaternion.Normalize(
                    dc.Orientation.LengthSquared() > 0.01f ? dc.Orientation : Quaternion.Identity));

                prefab.Cells.Add(new PrefabCell {
                    LocalIndex = i,
                    EnvId = dc.EnvironmentId,
                    CellStruct = dc.CellStructure,
                    PortalCount = dc.CellPortals.Count,
                    OffsetX = relPos.X, OffsetY = relPos.Y, OffsetZ = relPos.Z,
                    RotX = relRot.X, RotY = relRot.Y, RotZ = relRot.Z, RotW = relRot.W,
                    Surfaces = new List<ushort>(dc.Surfaces),
                });
            }

            foreach (var dc in cells) {
                int myIdx = cellIndexMap[dc.CellNumber];
                foreach (var cp in dc.CellPortals) {
                    if (cellNumSet.Contains(cp.OtherCellId) && cellIndexMap.TryGetValue(cp.OtherCellId, out int otherIdx)) {
                        if (myIdx < otherIdx) {
                            prefab.InternalPortals.Add(new PrefabPortal {
                                CellIndexA = myIdx, PolyIdA = cp.PolygonId,
                                CellIndexB = otherIdx, PolyIdB = cp.OtherPortalId,
                            });
                        }
                    }
                    else {
                        float nx = 0, ny = 0, nz = 0;
                        if (dats != null) {
                            uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
                            if (dats.TryGet<Acme.Dat.Environment>(envFileId, out var env) &&
                                env.Cells.TryGetValue(dc.CellStructure, out var cs)) {
                                var geom = PortalSnapper.GetPortalGeometry(cs, cp.PolygonId);
                                if (geom != null) {
                                    var cellRot = Quaternion.Normalize(new Quaternion(
                                        prefab.Cells[myIdx].RotX, prefab.Cells[myIdx].RotY,
                                        prefab.Cells[myIdx].RotZ, prefab.Cells[myIdx].RotW));
                                    var worldNormal = Vector3.Transform(geom.Value.Normal, cellRot);
                                    nx = worldNormal.X; ny = worldNormal.Y; nz = worldNormal.Z;
                                }
                            }
                        }
                        prefab.OpenFaces.Add(new PrefabOpenFace {
                            CellIndex = myIdx, PolyId = cp.PolygonId,
                            EnvId = dc.EnvironmentId,
                            CellStruct = dc.CellStructure,
                            NormalX = nx, NormalY = ny, NormalZ = nz,
                        });
                    }
                }

                if (dats != null) {
                    uint envFileId2 = (uint)(dc.EnvironmentId | 0x0D000000);
                    if (dats.TryGet<Acme.Dat.Environment>(envFileId2, out var env2) &&
                        env2.Cells.TryGetValue(dc.CellStructure, out var cs2)) {
                        var allPortalPolyIds = PortalSnapper.GetPortalPolygonIds(cs2);
                        var connectedPolyIds = new HashSet<ushort>(dc.CellPortals.Select(c => c.PolygonId));
                        foreach (var polyId in allPortalPolyIds) {
                            if (connectedPolyIds.Contains(polyId)) continue;
                            float nx = 0, ny = 0, nz = 0;
                            var geom = PortalSnapper.GetPortalGeometry(cs2, polyId);
                            if (geom != null) {
                                var cellRot = Quaternion.Normalize(new Quaternion(
                                    prefab.Cells[myIdx].RotX, prefab.Cells[myIdx].RotY,
                                    prefab.Cells[myIdx].RotZ, prefab.Cells[myIdx].RotW));
                                var worldNormal = Vector3.Transform(geom.Value.Normal, cellRot);
                                nx = worldNormal.X; ny = worldNormal.Y; nz = worldNormal.Z;
                            }
                            prefab.OpenFaces.Add(new PrefabOpenFace {
                                CellIndex = myIdx, PolyId = polyId,
                                EnvId = dc.EnvironmentId,
                                CellStruct = dc.CellStructure,
                                NormalX = nx, NormalY = ny, NormalZ = nz,
                            });
                        }
                    }
                }
            }

            foreach (var of in prefab.OpenFaces) {
                prefab.OpenFaceDirections.Add(DungeonPrefab.ClassifyDirection(of.NormalX, of.NormalY, of.NormalZ));
            }

            var timestamp = DateTime.UtcNow.Ticks;
            prefab.Signature = $"custom_{timestamp}_{string.Join("|",
                prefab.Cells.OrderBy(c => c.EnvId).ThenBy(c => c.CellStruct)
                    .Select(c => $"{c.EnvId:X4}_{c.CellStruct}"))}";

            Console.WriteLine($"[Dungeon] Extracted full dungeon prefab: {prefab.DisplayName} ({prefab.Cells.Count} cells, {prefab.OpenFaces.Count} open faces)");
            return prefab;
        }
    }
}
