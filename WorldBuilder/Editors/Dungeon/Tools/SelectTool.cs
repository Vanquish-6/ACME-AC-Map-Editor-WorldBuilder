using Avalonia.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon.Tools {

    /// <summary>
    /// Unified select tool: click to select cells/objects, drag to move them.
    /// Ctrl+click for multi-select, drag in empty space for box select.
    /// Supports gizmo-based translate/rotate for selected objects.
    /// </summary>
    public class SelectTool : DungeonToolBase {
        public override string Name => "Select";
        public override string IconGlyph => "\u2B9E";
        public override string Description =>
            "Click to select. Drag arrows to move, rings to rotate. Ctrl-click adds. Ctrl+D duplicates. F frames. Grid snap in Options.";

        private static readonly ObservableCollection<DungeonSubToolBase> _empty = new();
        public override ObservableCollection<DungeonSubToolBase> AllSubTools => _empty;

        private readonly DungeonEditingContext _ctx;

        private enum DragMode { None, BoxSelect, MoveCell, MoveObject, Gizmo }
        private DragMode _dragMode;
        private Vector2 _dragScreenStart;
        private Vector3 _dragWorldStart;
        private Vector3 _dragOriginStart;
        private bool _didClickSelect;
        private const float DragThreshold = 4f;
        private bool _dragStarted;
        private readonly Dictionary<ushort, Vector3> _moveCellStarts = new();
        private readonly Dictionary<ushort, Quaternion> _gizmoCellRots = new();
        private bool _gizmoIsCells;
        private bool _moveVertical;

        private Vector3 _gizmoOrigPos;
        private Quaternion _gizmoOrigRot;
        private Vector3 _gizmoOrigScale;
        private Vector3 _gizmoWorldCenter;
        private Vector3 _gizmoCellHitStart;
        private Quaternion _dragOriginRot;

        public SelectTool(DungeonEditingContext ctx) {
            _ctx = ctx;
        }

        private TransformGizmo? Gizmo => _ctx.Scene?.Gizmo;

        public override void OnActivated() {
            StatusText = "Click to select. Drag gizmos to move/rotate. Ctrl+D duplicate. F to frame.";
        }
        public override void OnDeactivated() {
            _dragMode = DragMode.None;
            Gizmo?.CancelDrag();
        }

        private Vector3 StabToWorld(Vector3 stabOrigin, DungeonDocument doc) {
            uint lbId = doc.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            return stabOrigin + new Vector3(blockX * 192f, blockY * 192f, -50f);
        }

        public override bool HandleMouseDown(MouseState mouseState, DungeonEditingContext ctx) {
            if (!mouseState.LeftPressed || mouseState.RightPressed) return false;
            if (ctx.Scene?.EnvCellManager == null || ctx.Document == null) return false;

            var gizmo = Gizmo;
            if (gizmo != null && ctx.HasSelectedObject) {
                var cell = ctx.Document.GetCell(ctx.SelectedObjCellNum);
                if (cell != null && ctx.SelectedObjIndex < cell.StaticObjects.Count) {
                    var stab = cell.StaticObjects[ctx.SelectedObjIndex];
                    var worldPos = StabToWorld(stab.Origin, ctx.Document);
                    var camera = ctx.Scene.Camera;
                    var vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();

                    var axis = gizmo.HitTestScreen(mouseState.Position, camera, vp, worldPos, stab.Orientation);
                    if (axis != GizmoAxis.None) {
                        if (ctx.SelectionIsLocked()) {
                            ctx.SetStatus("Selection is locked — unlock it in the Outliner to move.");
                            return true;
                        }
                        gizmo.StartDrag(axis, mouseState.Position, camera, vp, worldPos, stab.Orientation);
                        _dragMode = DragMode.Gizmo;
                        _gizmoIsCells = false;
                        _gizmoOrigPos = stab.Origin;
                        _gizmoOrigRot = stab.Orientation;
                        _gizmoOrigScale = stab.Scale;
                        _gizmoWorldCenter = worldPos;

                        var startRay = ctx.ComputeRay(mouseState);
                        if (startRay.HasValue) {
                            if (TryIntersectPlane(startRay.Value.origin, startRay.Value.direction, worldPos, Vector3.UnitZ, out var planeHit))
                                _gizmoCellHitStart = planeHit;
                            else
                                _gizmoCellHitStart = worldPos;
                        } else {
                            _gizmoCellHitStart = worldPos;
                        }
                        return true;
                    }
                }
            }

            if (gizmo != null && ctx.HasSelectedCell && ctx.SelectedCells.Count > 0 && ctx.Scene != null) {
                var center = GetCellSelectionCenter(ctx);
                var camera = ctx.Scene.Camera;
                var vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
                var axis = gizmo.HitTestScreen(mouseState.Position, camera, vp, center, Quaternion.Identity);
                if (axis != GizmoAxis.None) {
                    if (ctx.SelectionIsLocked()) {
                        ctx.SetStatus("Selection is locked — unlock it in the Outliner to move.");
                        return true;
                    }
                    gizmo.StartDrag(axis, mouseState.Position, camera, vp, center, Quaternion.Identity);
                    _dragMode = DragMode.Gizmo;
                    _gizmoIsCells = true;
                    _gizmoWorldCenter = center;
                    _gizmoOrigPos = center;
                    _moveCellStarts.Clear();
                    _gizmoCellRots.Clear();
                    foreach (var sc in ctx.SelectedCells) {
                        var n = (ushort)(sc.CellId & 0xFFFF);
                        var d = ctx.Document.GetCell(n);
                        if (d == null) continue;
                        _moveCellStarts[n] = d.Origin;
                        _gizmoCellRots[n] = d.Orientation;
                    }
                    var startRay = ctx.ComputeRay(mouseState);
                    if (startRay.HasValue && TryIntersectPlane(startRay.Value.origin, startRay.Value.direction, center, Vector3.UnitZ, out var planeHit))
                        _gizmoCellHitStart = planeHit;
                    else
                        _gizmoCellHitStart = center;
                    return true;
                }
            }

            var ray = ctx.ComputeRay(mouseState);
            if (ray == null) return false;
            var (origin, dir) = ray.Value;

            _dragScreenStart = mouseState.Position;
            _dragStarted = false;
            _didClickSelect = false;

            var objHit = DungeonObjectRaycast.Raycast(origin, dir, ctx.Document, ctx.Scene);
            if (objHit.Hit) {
                if (objHit.IsInstancePlacement) {
                    var placement = ctx.Document.InstancePlacements[objHit.InstancePlacementIndex];
                    ctx.SelectedInstancePlacementIndex = objHit.InstancePlacementIndex;
                    ctx.SelectedObjIndex = -1;
                    ctx.SelectedCell = null;
                    ctx.SelectedCells.Clear();
                    ctx.NotifySelectionChanged();
                    _didClickSelect = true;
                    return true;
                }

                var cell = ctx.Document.GetCell(objHit.CellNumber);
                if (cell != null && objHit.ObjectIndex < cell.StaticObjects.Count) {
                    bool alreadySelected = ctx.SelectedObjCellNum == objHit.CellNumber
                                           && ctx.SelectedObjIndex == objHit.ObjectIndex;
                    ctx.SelectedObjCellNum = objHit.CellNumber;
                    ctx.SelectedObjIndex = objHit.ObjectIndex;
                    ctx.SelectedCell = null;
                    ctx.SelectedCells.Clear();
                    ctx.SelectedInstancePlacementIndex = -1;
                    ctx.NotifySelectionChanged();
                    _didClickSelect = true;

                    if (alreadySelected) {
                        _dragMode = DragMode.MoveObject;
                        _dragOriginStart = cell.StaticObjects[objHit.ObjectIndex].Origin;
                        _dragOriginRot = cell.StaticObjects[objHit.ObjectIndex].Orientation;
                        var planePt = StabToWorld(_dragOriginStart, ctx.Document);
                        if (ray != null && TryIntersectPlane(origin, dir, planePt, Vector3.UnitZ, out var planeHit))
                            _dragWorldStart = planeHit;
                        else
                            _dragWorldStart = new Vector3(objHit.HitPosition.X, objHit.HitPosition.Y, planePt.Z);
                    }
                    return true;
                }
            }

            // Check for cell hit
            var hit = ctx.Raycast(origin, dir);
            if (hit.Hit && ctx.IsCellHidden((ushort)(hit.Cell.CellId & 0xFFFF)))
                hit = default;
            if (hit.Hit) {
                bool ctrlAdd = mouseState.CtrlPressed;
                bool wasSelected = ctx.SelectedCells.Any(c => c.CellId == hit.Cell.CellId);

                if (ctrlAdd) {
                    var idx = ctx.SelectedCells.FindIndex(c => c.CellId == hit.Cell.CellId);
                    if (idx >= 0) ctx.SelectedCells.RemoveAt(idx);
                    else ctx.SelectedCells.Add(hit.Cell);
                    ctx.SelectedCell = ctx.SelectedCells.Count > 0 ? ctx.SelectedCells[0] : null;
                }
                else if (!wasSelected) {
                    ctx.SelectedCells.Clear();
                    ctx.SelectedCells.Add(hit.Cell);
                    ctx.SelectedCell = hit.Cell;
                }
                ctx.SelectedObjIndex = -1;
                ctx.SelectedInstancePlacementIndex = -1;
                ctx.NotifySelectionChanged();
                _didClickSelect = true;

                // Allow drag-move if cell was already selected (or just became selected without ctrl)
                if ((wasSelected || !ctrlAdd) && !ctx.SelectionIsLocked()) {
                    var cellNum = (ushort)(hit.Cell.CellId & 0xFFFF);
                    var dc = ctx.Document.GetCell(cellNum);
                    if (dc != null) {
                        _dragMode = DragMode.MoveCell;
                        _dragOriginStart = dc.Origin;
                        _moveCellStarts.Clear();
                        foreach (var sc in ctx.SelectedCells) {
                            var n = (ushort)(sc.CellId & 0xFFFF);
                            var d = ctx.Document.GetCell(n);
                            if (d != null) _moveCellStarts[n] = d.Origin;
                        }
                        if (!_moveCellStarts.ContainsKey(cellNum))
                            _moveCellStarts[cellNum] = dc.Origin;
                        if (TryIntersectPlane(origin, dir, dc.Origin, Vector3.UnitZ, out var planeHit))
                            _dragWorldStart = planeHit;
                        else
                            _dragWorldStart = new Vector3(hit.HitPosition.X, hit.HitPosition.Y, dc.Origin.Z);
                    }
                }
                return true;
            }

            // Missed everything -- start box select
            _dragMode = DragMode.BoxSelect;
            _dragScreenStart = mouseState.Position;
            return false;
        }

        public override bool HandleMouseUp(MouseState mouseState, DungeonEditingContext ctx) {
            var mode = _dragMode;
            _dragMode = DragMode.None;

            if (mode == DragMode.Gizmo) {
                FinalizeGizmoDrag(ctx);
                Gizmo?.EndDrag();
                _gizmoIsCells = false;
                return true;
            }

            if (mode == DragMode.MoveObject && _dragStarted && ctx.Document != null) {
                var cell = ctx.Document.GetCell(ctx.SelectedObjCellNum);
                if (cell != null && ctx.SelectedObjIndex < cell.StaticObjects.Count) {
                    var stab = cell.StaticObjects[ctx.SelectedObjIndex];
                    var delta = stab.Origin - _dragOriginStart;
                    bool hasMoved = delta.LengthSquared() > 0.001f;
                    bool hasRotated = MathF.Abs(Quaternion.Dot(stab.Orientation, _dragOriginRot)) < 0.9999f;

                    if (hasMoved || hasRotated) {
                        var newRot = stab.Orientation;
                        stab.Origin = _dragOriginStart;
                        stab.Orientation = _dragOriginRot;

                        var composite = new DungeonCompositeCommand("Move Object");
                        if (hasMoved)
                            composite.Add(new MoveStaticObjectCommand(ctx.SelectedObjCellNum, ctx.SelectedObjIndex, delta));
                        if (hasRotated)
                            composite.Add(new SetObjectOrientationCommand(ctx.SelectedObjCellNum, ctx.SelectedObjIndex, _dragOriginRot, newRot));
                        ctx.CommandHistory.Execute(composite, ctx.Document);
                        ctx.RefreshRendering();
                        ctx.NotifySelectionChanged();
                    }
                }
                return true;
            }

            if (mode == DragMode.MoveCell && _dragStarted && ctx.Document != null) {
                var moves = new List<(ushort num, Vector3 delta)>();
                foreach (var kv in _moveCellStarts) {
                    var dc = ctx.Document.GetCell(kv.Key);
                    if (dc == null) continue;
                    var delta = dc.Origin - kv.Value;
                    dc.Origin = kv.Value;
                    if (delta.LengthSquared() > 0.001f)
                        moves.Add((kv.Key, delta));
                }
                if (moves.Count == 1) {
                    ctx.CommandHistory.Execute(new NudgeCellCommand(moves[0].num, moves[0].delta), ctx.Document);
                    ctx.RefreshRendering();
                    ctx.NotifySelectionChanged();
                }
                else if (moves.Count > 1) {
                    var composite = new DungeonCompositeCommand("Move rooms");
                    foreach (var m in moves)
                        composite.Add(new NudgeCellCommand(m.num, m.delta));
                    ctx.CommandHistory.Execute(composite, ctx.Document);
                    ctx.RefreshRendering();
                    ctx.NotifySelectionChanged();
                }
                _moveCellStarts.Clear();
                return true;
            }

            if (mode == DragMode.BoxSelect) {
                var endPos = mouseState.Position;
                if ((endPos - _dragScreenStart).Length() > DragThreshold) {
                    float minX = Math.Min(_dragScreenStart.X, endPos.X);
                    float maxX = Math.Max(_dragScreenStart.X, endPos.X);
                    float minY = Math.Min(_dragScreenStart.Y, endPos.Y);
                    float maxY = Math.Max(_dragScreenStart.Y, endPos.Y);

                    ctx.SelectedCells.Clear();
                    ctx.SelectedCell = null;
                    var cells = ctx.Scene?.EnvCellManager?.GetLoadedCellsForLandblock(ctx.Document.LandblockKey);
                    if (cells != null) {
                        foreach (var cell in cells) {
                            var screenPos = ProjectToScreen(cell.WorldPosition, ctx);
                            if (screenPos.X >= minX && screenPos.X <= maxX && screenPos.Y >= minY && screenPos.Y <= maxY) {
                                ctx.SelectedCells.Add(cell);
                            }
                        }
                        if (ctx.SelectedCells.Count > 0) ctx.SelectedCell = ctx.SelectedCells[0];
                    }
                    ctx.NotifySelectionChanged();
                    return true;
                }

                // Tiny drag = deselect
                ctx.SelectedCells.Clear();
                ctx.SelectedCell = null;
                ctx.SelectedObjIndex = -1;
                ctx.SelectedInstancePlacementIndex = -1;
                ctx.NotifySelectionChanged();
                return false;
            }

            return false;
        }

        public override bool HandleMouseMove(MouseState mouseState, DungeonEditingContext ctx) {
            var gizmo = Gizmo;
            if (gizmo != null) {
                if (_dragMode == DragMode.Gizmo) {
                    ApplyGizmoDrag(mouseState, ctx);
                    return true;
                }

                if (ctx.HasSelectedObject && ctx.Scene != null && ctx.Document != null && _dragMode == DragMode.None) {
                    var cell = ctx.Document.GetCell(ctx.SelectedObjCellNum);
                    if (cell != null && ctx.SelectedObjIndex < cell.StaticObjects.Count) {
                        var stab = cell.StaticObjects[ctx.SelectedObjIndex];
                        var worldPos = StabToWorld(stab.Origin, ctx.Document);
                        var camera = ctx.Scene.Camera;
                        var vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
                        gizmo.UpdateHover(mouseState.Position, camera, vp, worldPos, stab.Orientation);
                    }
                }
                else if (ctx.HasSelectedCell && ctx.SelectedCells.Count > 0 && ctx.Scene != null && _dragMode == DragMode.None) {
                    var center = GetCellSelectionCenter(ctx);
                    var camera = ctx.Scene.Camera;
                    var vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
                    gizmo.UpdateHover(mouseState.Position, camera, vp, center, Quaternion.Identity);
                }
            }

            if (_dragMode == DragMode.BoxSelect) return true;

            float screenDist = (mouseState.Position - _dragScreenStart).Length();

            if (_dragMode == DragMode.MoveCell && ctx.Document != null && _moveCellStarts.Count > 0) {
                if (ctx.SelectionIsLocked()) {
                    ctx.SetStatus("Selection is locked — unlock it in the Outliner to move.");
                    return true;
                }
                if (!_dragStarted && screenDist < DragThreshold) return false;
                if (!_dragStarted) {
                    _dragStarted = true;
                    _moveVertical = mouseState.ShiftPressed;
                }

                var ray = ctx.ComputeRay(mouseState);
                if (ray == null) return true;
                Vector3 delta;
                if (_moveVertical) {
                    var cam = ctx.Scene?.Camera;
                    float dist = cam != null
                        ? MathF.Max(6f, Vector3.Distance(cam.Position, _dragOriginStart))
                        : 20f;
                    float zDelta = -(mouseState.Position.Y - _dragScreenStart.Y) * dist * 0.0035f;
                    delta = new Vector3(0f, 0f, zDelta);
                }
                else {
                    if (!TryIntersectPlane(ray.Value.origin, ray.Value.direction, _dragOriginStart, Vector3.UnitZ, out var hit))
                        return true;
                    delta = new Vector3(hit.X - _dragWorldStart.X, hit.Y - _dragWorldStart.Y, 0f);
                }

                var raw = _dragOriginStart + delta;
                if (ctx.GridSnapEnabled && ctx.GridSnapSize > 0.1f)
                    raw = SnapAxes(raw, ctx.GridSnapSize, xy: !_moveVertical, z: _moveVertical);
                var move = raw - _dragOriginStart;
                foreach (var kv in _moveCellStarts) {
                    var dc = ctx.Document.GetCell(kv.Key);
                    if (dc != null) dc.Origin = kv.Value + move;
                }
                ctx.RefreshRendering();
                ctx.SetStatus(_moveVertical
                    ? $"Height {raw.Z:F1}  (release Shift for floor move)"
                    : $"Move ({raw.X:F1}, {raw.Y:F1})  ·  Shift = up/down");
                return true;
            }

            if (_dragMode == DragMode.MoveObject && ctx.Document != null) {
                if (!_dragStarted && screenDist < DragThreshold) return false;
                if (!_dragStarted) {
                    _dragStarted = true;
                    _moveVertical = mouseState.ShiftPressed;
                }

                var ray = ctx.ComputeRay(mouseState);
                if (ray == null) return true;
                var cell = ctx.Document.GetCell(ctx.SelectedObjCellNum);
                if (cell == null || ctx.SelectedObjIndex >= cell.StaticObjects.Count) return true;

                var worldStart = StabToWorld(_dragOriginStart, ctx.Document);
                Vector3 worldDelta;
                if (_moveVertical) {
                    var cam = ctx.Scene?.Camera;
                    float dist = cam != null
                        ? MathF.Max(6f, Vector3.Distance(cam.Position, worldStart))
                        : 20f;
                    float zDelta = -(mouseState.Position.Y - _dragScreenStart.Y) * dist * 0.0035f;
                    worldDelta = new Vector3(0f, 0f, zDelta);
                }
                else {
                    if (!TryIntersectPlane(ray.Value.origin, ray.Value.direction, worldStart, Vector3.UnitZ, out var hit))
                        return true;
                    worldDelta = new Vector3(hit.X - _dragWorldStart.X, hit.Y - _dragWorldStart.Y, 0f);
                }

                var newWorld = worldStart + worldDelta;
                if (ctx.GridSnapEnabled && ctx.GridSnapSize > 0.1f)
                    newWorld = SnapAxes(newWorld, ctx.GridSnapSize, xy: !_moveVertical, z: _moveVertical);
                var lb = StabToWorld(Vector3.Zero, ctx.Document);
                var newLocal = newWorld - lb;
                cell.StaticObjects[ctx.SelectedObjIndex].Origin = newLocal;
                ctx.Scene?.UpdateStaticObjectTransform(ctx.Document, ctx.SelectedObjCellNum, ctx.SelectedObjIndex);
                return true;
            }

            // Hover feedback — detect hovered objects for highlight rendering
            if (ctx.Scene?.EnvCellManager == null) return false;
            var hoverRay = ctx.ComputeRay(mouseState);
            if (hoverRay == null) return false;

            if (ctx.Document != null && ctx.Scene != null) {
                var objHover = DungeonObjectRaycast.Raycast(hoverRay.Value.origin, hoverRay.Value.direction, ctx.Document, ctx.Scene);
                if (objHover.Hit) {
                    if (objHover.IsInstancePlacement) {
                        var placement = ctx.Document.InstancePlacements[objHover.InstancePlacementIndex];
                        if (ctx.Scene.TryGetWeenieSetupId(placement.WeenieClassId, out var setupId) && setupId != 0) {
                            bool isSetup = (setupId & 0xFF000000) == 0x02000000;
                            ctx.Scene.HoveredObjectId = setupId;
                            ctx.Scene.HoveredObjectIsSetup = isSetup;
                            ctx.Scene.HoveredObjectPosition = objHover.WorldOrigin;
                            ctx.Scene.HoveredObjectOrientation = placement.Orientation;
                            ctx.Scene.HoveredObjectScale = Vector3.One;
                        }
                    }
                    else {
                        var hoverCell = ctx.Document.GetCell(objHover.CellNumber);
                        if (hoverCell != null && objHover.ObjectIndex < hoverCell.StaticObjects.Count) {
                            var stab = hoverCell.StaticObjects[objHover.ObjectIndex];
                            ctx.Scene.HoveredObjectId = stab.Id;
                            ctx.Scene.HoveredObjectIsSetup = (stab.Id & 0xFF000000) == 0x02000000;
                            ctx.Scene.HoveredObjectPosition = StabToWorld(stab.Origin, ctx.Document);
                            ctx.Scene.HoveredObjectOrientation = stab.Orientation;
                            ctx.Scene.HoveredObjectScale = stab.Scale;
                        }
                    }
                } else {
                    ctx.Scene.HoveredObjectPosition = null;
                }
            }

            var hoverHit = ctx.Raycast(hoverRay.Value.origin, hoverRay.Value.direction);
            if (hoverHit.Hit) {
                var roomName = ctx.RoomPalette?.GetRoomDisplayName(hoverHit.Cell.EnvironmentId, (ushort)hoverHit.Cell.GpuKey.CellStructure);
                var label = !string.IsNullOrEmpty(roomName) ? roomName : $"Env 0x{hoverHit.Cell.EnvironmentId:X8}";
                ctx.SetStatus($"0x{hoverHit.Cell.CellId:X8}  |  {label}");
            }
            return false;
        }

        public override bool HandleKeyDown(KeyEventArgs e, DungeonEditingContext ctx) {
            var gizmo = Gizmo;
            if (gizmo != null && (ctx.HasSelectedObject || ctx.HasSelectedCell)) {
                switch (e.Key) {
                    case Key.W:
                        gizmo.Mode = GizmoMode.Translate;
                        return true;
                    case Key.E:
                        gizmo.Mode = GizmoMode.Rotate;
                        return true;
                    case Key.R:
                        if (ctx.HasSelectedObject)
                            gizmo.Mode = GizmoMode.Scale;
                        return true;
                }
            }

            if (e.Key == Key.F) {
                ctx.RequestCameraFocus();
                return true;
            }

            if (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
                DuplicateSelection(ctx);
                return true;
            }

            if (TryNudgeFromKey(e, ctx))
                return true;

            if (e.Key == Key.Escape) {
                if (_dragMode == DragMode.Gizmo) {
                    CancelGizmoDrag(ctx);
                    gizmo?.CancelDrag();
                    _dragMode = DragMode.None;
                    _gizmoIsCells = false;
                    return true;
                }
                _dragMode = DragMode.None;
                ctx.SelectedCells.Clear();
                ctx.SelectedCell = null;
                ctx.SelectedObjIndex = -1;
                ctx.SelectedInstancePlacementIndex = -1;
                ctx.NotifySelectionChanged();
                return true;
            }

            if (e.Key == Key.Delete && ctx.HasSelectedInstancePlacement && ctx.Document != null) {
                var idx = ctx.SelectedInstancePlacementIndex;
                if (idx >= 0 && idx < ctx.Document.InstancePlacements.Count) {
                    ctx.Document.InstancePlacements.RemoveAt(idx);
                    ctx.Document.MarkDirty();
                    ctx.SelectedInstancePlacementIndex = -1;
                    ctx.NotifySelectionChanged();
                    ctx.RefreshRendering();
                    ctx.SetStatus("Deleted instance placement.");
                    return true;
                }
            }
            return false;
        }

        private void ApplyGizmoDrag(MouseState mouseState, DungeonEditingContext ctx) {
            var gizmo = Gizmo;
            if (gizmo == null || ctx.Document == null || ctx.Scene == null) return;

            if (_gizmoIsCells) {
                ApplyCellGizmoDrag(mouseState, ctx, gizmo);
                return;
            }

            var cell = ctx.Document.GetCell(ctx.SelectedObjCellNum);
            if (cell == null || ctx.SelectedObjIndex >= cell.StaticObjects.Count) return;

            var camera = ctx.Scene.Camera;
            var stab = cell.StaticObjects[ctx.SelectedObjIndex];

            if (gizmo.Mode == GizmoMode.Translate) {
                Vector3 delta = Vector3.Zero;
                bool gotDelta = false;
                var axis = gizmo.ActiveAxis;

                var ray = ctx.ComputeRay(mouseState);
                if (ray.HasValue) {
                    if (axis == GizmoAxis.Z) {
                        delta = gizmo.ComputeTranslateDelta(mouseState.Position, camera, _gizmoWorldCenter);
                        delta = new Vector3(0, 0, delta.Z);
                        gotDelta = true;
                    }
                    else if (TryIntersectPlane(ray.Value.origin, ray.Value.direction, _gizmoWorldCenter, Vector3.UnitZ, out var xyHit)) {
                        delta = xyHit - _gizmoCellHitStart;
                        delta.Z = 0f;
                        gotDelta = true;
                    }
                }

                if (!gotDelta && axis == GizmoAxis.Z) {
                    delta = gizmo.ComputeTranslateDelta(mouseState.Position, camera, _gizmoWorldCenter);
                    delta = new Vector3(0, 0, delta.Z);
                    gotDelta = true;
                }

                if (!gotDelta) return;

                if (axis == GizmoAxis.X) delta = new Vector3(delta.X, 0, 0);
                else if (axis == GizmoAxis.Y) delta = new Vector3(0, delta.Y, 0);
                else if (axis == GizmoAxis.Z) delta = new Vector3(0, 0, delta.Z);
                else if (axis == GizmoAxis.XY) delta.Z = 0;
                else if (axis == GizmoAxis.XZ) delta.Y = 0;
                else if (axis == GizmoAxis.YZ) delta.X = 0;

                if (ctx.GridSnapEnabled && ctx.GridSnapSize > 0.1f) {
                    float g = ctx.GridSnapSize;
                    if (MathF.Abs(delta.X) > 1e-5f) delta.X = MathF.Round(delta.X / g) * g;
                    if (MathF.Abs(delta.Y) > 1e-5f) delta.Y = MathF.Round(delta.Y / g) * g;
                    if (MathF.Abs(delta.Z) > 1e-5f) delta.Z = MathF.Round(delta.Z / g) * g;
                }

                var newPos = _gizmoOrigPos + delta;
                stab.Origin = newPos;
            }
            else if (gizmo.Mode == GizmoMode.Rotate) {
                float angle = gizmo.ComputeRotationAngle(mouseState.Position, camera, _gizmoWorldCenter);
                var axisDir = gizmo.GetRotationAxisDirection();
                var rotation = Quaternion.CreateFromAxisAngle(axisDir, angle);
                stab.Orientation = Quaternion.Normalize(rotation * _gizmoOrigRot);
            }
            else if (gizmo.Mode == GizmoMode.Scale) {
                var scaleDelta = gizmo.ComputeScaleDelta(mouseState.Position, camera, _gizmoWorldCenter);
                var newScale = _gizmoOrigScale + _gizmoOrigScale * scaleDelta;
                newScale = Vector3.Max(newScale, new Vector3(0.01f));
                stab.Scale = newScale;
            }

            ctx.Scene.SelectedObjectPosition = StabToWorld(stab.Origin, ctx.Document);
            ctx.Scene.SelectedObjectOrientation = stab.Orientation;
            ctx.Scene.UpdateStaticObjectTransform(ctx.Document, ctx.SelectedObjCellNum, ctx.SelectedObjIndex);
        }

        private void FinalizeGizmoDrag(DungeonEditingContext ctx) {
            var gizmo = Gizmo;
            if (gizmo == null || ctx.Document == null) return;

            if (_gizmoIsCells) {
                FinalizeCellGizmoDrag(ctx, gizmo);
                return;
            }

            var cell = ctx.Document.GetCell(ctx.SelectedObjCellNum);
            if (cell == null || ctx.SelectedObjIndex >= cell.StaticObjects.Count) return;

            var stab = cell.StaticObjects[ctx.SelectedObjIndex];
            var composite = new DungeonCompositeCommand($"Gizmo {gizmo.Mode}");
            bool hasChange = false;

            if (gizmo.Mode == GizmoMode.Translate) {
                var delta = stab.Origin - _gizmoOrigPos;
                if (delta.LengthSquared() > 0.001f) {
                    stab.Origin = _gizmoOrigPos;
                    composite.Add(new MoveStaticObjectCommand(ctx.SelectedObjCellNum, ctx.SelectedObjIndex, delta));
                    hasChange = true;
                }
                if (MathF.Abs(Quaternion.Dot(stab.Orientation, _gizmoOrigRot)) < 0.9999f) {
                    var newRot = stab.Orientation;
                    stab.Orientation = _gizmoOrigRot;
                    composite.Add(new SetObjectOrientationCommand(ctx.SelectedObjCellNum, ctx.SelectedObjIndex, _gizmoOrigRot, newRot));
                    hasChange = true;
                }
            }
            else if (gizmo.Mode == GizmoMode.Rotate) {
                if (MathF.Abs(Quaternion.Dot(stab.Orientation, _gizmoOrigRot)) < 0.9999f) {
                    var newRot = stab.Orientation;
                    stab.Orientation = _gizmoOrigRot;
                    composite.Add(new SetObjectOrientationCommand(ctx.SelectedObjCellNum, ctx.SelectedObjIndex, _gizmoOrigRot, newRot));
                    hasChange = true;
                }
            }
            else if (gizmo.Mode == GizmoMode.Scale) {
                var diff = stab.Scale - _gizmoOrigScale;
                if (diff.LengthSquared() > 0.0001f) {
                    var newScale = stab.Scale;
                    stab.Scale = _gizmoOrigScale;
                    composite.Add(new ScaleStaticObjectCommand(ctx.SelectedObjCellNum, ctx.SelectedObjIndex, _gizmoOrigScale, newScale));
                    hasChange = true;
                }
            }

            if (hasChange) {
                ctx.CommandHistory.Execute(composite, ctx.Document);
                ctx.Document.MarkDirty();
                ctx.RefreshRendering();
                ctx.NotifySelectionChanged();
            }
        }

        private void CancelGizmoDrag(DungeonEditingContext ctx) {
            if (ctx.Document == null) return;
            if (_gizmoIsCells) {
                foreach (var kv in _moveCellStarts) {
                    var dc = ctx.Document.GetCell(kv.Key);
                    if (dc == null) continue;
                    dc.Origin = kv.Value;
                    if (_gizmoCellRots.TryGetValue(kv.Key, out var rot))
                        dc.Orientation = rot;
                }
                ctx.RefreshRendering();
                return;
            }
            var cell = ctx.Document.GetCell(ctx.SelectedObjCellNum);
            if (cell == null || ctx.SelectedObjIndex >= cell.StaticObjects.Count) return;
            cell.StaticObjects[ctx.SelectedObjIndex].Origin = _gizmoOrigPos;
            cell.StaticObjects[ctx.SelectedObjIndex].Orientation = _gizmoOrigRot;
            cell.StaticObjects[ctx.SelectedObjIndex].Scale = _gizmoOrigScale;
            ctx.Scene?.UpdateStaticObjectTransform(ctx.Document, ctx.SelectedObjCellNum, ctx.SelectedObjIndex);
        }

        private static float ExtractYaw(Quaternion q) {
            float siny = 2.0f * (q.W * q.Z + q.X * q.Y);
            float cosy = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
            return MathF.Atan2(siny, cosy);
        }

        private bool IsInsideAnyCell(Vector3 worldPos, DungeonEditingContext ctx) {
            if (ctx.Document == null || ctx.Scene?.EnvCellManager == null) return false;
            var cells = ctx.Scene.EnvCellManager.GetLoadedCellsForLandblock(ctx.Document.LandblockKey);
            if (cells == null) return false;

            const float margin = 2f;
            foreach (var cell in cells) {
                var localPos = Vector3.Transform(worldPos, cell.InverseWorldTransform);
                if (localPos.X >= cell.LocalBoundsMin.X - margin && localPos.X <= cell.LocalBoundsMax.X + margin &&
                    localPos.Y >= cell.LocalBoundsMin.Y - margin && localPos.Y <= cell.LocalBoundsMax.Y + margin &&
                    localPos.Z >= cell.LocalBoundsMin.Z - margin && localPos.Z <= cell.LocalBoundsMax.Z + margin) {
                    return true;
                }
            }
            return false;
        }

        private static Vector3 GetCellSelectionCenter(DungeonEditingContext ctx) {
            var cells = ctx.SelectedCells;
            if (cells.Count == 0) return Vector3.Zero;
            var sum = Vector3.Zero;
            foreach (var c in cells) sum += c.WorldPosition;
            return sum / cells.Count;
        }

        private void ApplyCellGizmoDrag(MouseState mouseState, DungeonEditingContext ctx, TransformGizmo gizmo) {
            var camera = ctx.Scene!.Camera;
            if (gizmo.Mode == GizmoMode.Translate) {
                Vector3 delta = Vector3.Zero;
                var axis = gizmo.ActiveAxis;
                var ray = ctx.ComputeRay(mouseState);
                if (axis == GizmoAxis.Z) {
                    delta = gizmo.ComputeTranslateDelta(mouseState.Position, camera, _gizmoWorldCenter);
                    delta = new Vector3(0, 0, delta.Z);
                }
                else if (ray.HasValue && TryIntersectPlane(ray.Value.origin, ray.Value.direction, _gizmoWorldCenter, Vector3.UnitZ, out var xyHit)) {
                    delta = xyHit - _gizmoCellHitStart;
                    delta.Z = 0f;
                }
                else return;

                if (axis == GizmoAxis.X) delta = new Vector3(delta.X, 0, 0);
                else if (axis == GizmoAxis.Y) delta = new Vector3(0, delta.Y, 0);
                else if (axis == GizmoAxis.Z) delta = new Vector3(0, 0, delta.Z);

                if (ctx.GridSnapEnabled && ctx.GridSnapSize > 0.1f) {
                    float g = ctx.GridSnapSize;
                    if (MathF.Abs(delta.X) > 1e-5f) delta.X = MathF.Round(delta.X / g) * g;
                    if (MathF.Abs(delta.Y) > 1e-5f) delta.Y = MathF.Round(delta.Y / g) * g;
                    if (MathF.Abs(delta.Z) > 1e-5f) delta.Z = MathF.Round(delta.Z / g) * g;
                }

                foreach (var kv in _moveCellStarts) {
                    var dc = ctx.Document!.GetCell(kv.Key);
                    if (dc != null) dc.Origin = kv.Value + delta;
                }
                ctx.RefreshRendering();
            }
            else if (gizmo.Mode == GizmoMode.Rotate) {
                float angle = gizmo.ComputeRotationAngle(mouseState.Position, camera, _gizmoWorldCenter);
                var axisDir = gizmo.GetRotationAxisDirection();
                var rotation = Quaternion.CreateFromAxisAngle(axisDir, angle);
                foreach (var kv in _moveCellStarts) {
                    var dc = ctx.Document!.GetCell(kv.Key);
                    if (dc == null) continue;
                    var orig = kv.Value;
                    var rel = orig - _moveCellStarts.Values.Aggregate(Vector3.Zero, (a, b) => a + b) / _moveCellStarts.Count;
                    // Rotate around selection centroid in cell space
                    var centroid = Vector3.Zero;
                    foreach (var o in _moveCellStarts.Values) centroid += o;
                    centroid /= _moveCellStarts.Count;
                    var fromCenter = orig - centroid;
                    dc.Origin = centroid + Vector3.Transform(fromCenter, rotation);
                    if (_gizmoCellRots.TryGetValue(kv.Key, out var origRot))
                        dc.Orientation = Quaternion.Normalize(rotation * origRot);
                }
                ctx.RefreshRendering();
            }
        }

        private void FinalizeCellGizmoDrag(DungeonEditingContext ctx, TransformGizmo gizmo) {
            if (_moveCellStarts.Count == 0) return;
            var composite = new DungeonCompositeCommand(gizmo.Mode == GizmoMode.Rotate ? "Rotate rooms" : "Move rooms");
            bool any = false;
            foreach (var kv in _moveCellStarts) {
                var dc = ctx.Document!.GetCell(kv.Key);
                if (dc == null) continue;
                var delta = dc.Origin - kv.Value;
                var newRot = dc.Orientation;
                var oldRot = _gizmoCellRots.TryGetValue(kv.Key, out var r) ? r : dc.Orientation;
                dc.Origin = kv.Value;
                dc.Orientation = oldRot;
                if (delta.LengthSquared() > 0.001f) {
                    composite.Add(new NudgeCellCommand(kv.Key, delta));
                    any = true;
                }
                if (MathF.Abs(Quaternion.Dot(newRot, oldRot)) < 0.9999f) {
                    composite.Add(new SetCellOrientationCommand(kv.Key, oldRot, newRot));
                    any = true;
                }
            }
            if (any) {
                ctx.CommandHistory.Execute(composite, ctx.Document!);
                ctx.Document!.MarkDirty();
                ctx.RefreshRendering();
                ctx.NotifySelectionChanged();
            }
        }

        public static void DuplicateCurrent(DungeonEditingContext ctx) => DuplicateSelection(ctx);

        private static void DuplicateSelection(DungeonEditingContext ctx) {
            if (ctx.Document == null) return;
            if (ctx.HasSelectedObject) {
                var cell = ctx.Document.GetCell(ctx.SelectedObjCellNum);
                if (cell == null || ctx.SelectedObjIndex >= cell.StaticObjects.Count) return;
                var stab = cell.StaticObjects[ctx.SelectedObjIndex];
                var offset = ctx.GridSnapEnabled && ctx.GridSnapSize > 0.1f
                    ? new Vector3(ctx.GridSnapSize, 0, 0)
                    : new Vector3(1f, 0, 0);
                ctx.CommandHistory.Execute(
                    new AddStaticObjectCommand(ctx.SelectedObjCellNum, stab.Id, stab.Origin + offset, stab.Orientation),
                    ctx.Document);
                ctx.Document.MarkDirty();
                ctx.RefreshRendering();
                ctx.SetStatus("Duplicated object");
                return;
            }

            if (ctx.SelectedCells.Count == 0) return;
            var clones = new List<DungeonCellData>();
            foreach (var sc in ctx.SelectedCells) {
                var dc = ctx.Document.GetCell((ushort)(sc.CellId & 0xFFFF));
                if (dc != null) clones.Add(CellEditingService.DeepCloneCell(dc));
            }
            if (clones.Count == 0) return;
            var pasteOffset = ctx.GridSnapEnabled && ctx.GridSnapSize > 0.1f
                ? new Vector3(ctx.GridSnapSize, 0, 0)
                : new Vector3(10f, 0, 0);
            ctx.CommandHistory.Execute(new PasteCellsCommand(clones, pasteOffset), ctx.Document);
            ctx.RefreshRendering();
            ctx.NotifySelectionChanged();
            ctx.SetStatus($"Duplicated {clones.Count} room{(clones.Count == 1 ? "" : "s")}");
        }

        private static Vector2 ProjectToScreen(Vector3 worldPos, DungeonEditingContext ctx) {
            var camera = ctx.Scene?.Camera;
            if (camera == null) return new Vector2(-1, -1);

            var view = camera.GetViewMatrix();
            var proj = camera.GetProjectionMatrix();
            var vp = view * proj;
            var clip = Vector4.Transform(new Vector4(worldPos, 1f), vp);
            if (clip.W <= 0) return new Vector2(-1, -1);

            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            float screenX = (ndcX + 1f) * 0.5f * camera.ScreenSize.X;
            float screenY = (ndcY + 1f) * 0.5f * camera.ScreenSize.Y;
            return new Vector2(screenX, screenY);
        }

        private bool TryNudgeFromKey(KeyEventArgs e, DungeonEditingContext ctx) {
            if (ctx.Document == null) return false;
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            float step = ctx.GridSnapEnabled && ctx.GridSnapSize > 0.1f ? ctx.GridSnapSize : ctx.NudgeStep;
            if (step < 0.05f) step = 1f;

            Vector3 offset = e.Key switch {
                Key.Left => new Vector3(-step, 0, 0),
                Key.Right => new Vector3(step, 0, 0),
                Key.Up when shift => new Vector3(0, 0, step),
                Key.Down when shift => new Vector3(0, 0, -step),
                Key.Up => new Vector3(0, step, 0),
                Key.Down => new Vector3(0, -step, 0),
                Key.PageUp => new Vector3(0, 0, step),
                Key.PageDown => new Vector3(0, 0, -step),
                _ => Vector3.Zero
            };
            if (offset.LengthSquared() < 1e-8f) return false;

            if (ctx.HasSelectedObject) {
                var cell = ctx.Document.GetCell(ctx.SelectedObjCellNum);
                if (cell == null || ctx.SelectedObjIndex >= cell.StaticObjects.Count) return false;
                ctx.CommandHistory.Execute(
                    new MoveStaticObjectCommand(ctx.SelectedObjCellNum, ctx.SelectedObjIndex, offset),
                    ctx.Document);
                ctx.Scene?.UpdateStaticObjectTransform(ctx.Document, ctx.SelectedObjCellNum, ctx.SelectedObjIndex);
                ctx.RefreshRendering();
                ctx.NotifySelectionChanged();
                return true;
            }

            if (ctx.SelectedCells.Count == 0 && ctx.SelectedCell == null) return false;
            var nums = ctx.SelectedCells.Count > 0
                ? ctx.SelectedCells.Select(c => (ushort)(c.CellId & 0xFFFF)).Distinct().ToList()
                : new List<ushort> { (ushort)(ctx.SelectedCell!.CellId & 0xFFFF) };
            if (nums.Count == 1)
                ctx.CommandHistory.Execute(new NudgeCellCommand(nums[0], offset), ctx.Document);
            else {
                var composite = new DungeonCompositeCommand("Move rooms");
                foreach (var n in nums)
                    composite.Add(new NudgeCellCommand(n, offset));
                ctx.CommandHistory.Execute(composite, ctx.Document);
            }
            ctx.RefreshRendering();
            ctx.NotifySelectionChanged();
            ctx.SetStatus($"Nudged {offset.X:0.#},{offset.Y:0.#},{offset.Z:0.#}");
            return true;
        }

        private static bool TryIntersectPlane(Vector3 origin, Vector3 dir, Vector3 point, Vector3 normal, out Vector3 hit) {
            hit = default;
            float denom = Vector3.Dot(dir, normal);
            if (MathF.Abs(denom) < 1e-6f) return false;
            float t = Vector3.Dot(point - origin, normal) / denom;
            if (t < 0f) return false;
            hit = origin + dir * t;
            return true;
        }

        private static Vector3 SnapAxes(Vector3 p, float grid, bool xy, bool z) {
            if (xy) {
                p.X = MathF.Round(p.X / grid) * grid;
                p.Y = MathF.Round(p.Y / grid) * grid;
            }
            if (z)
                p.Z = MathF.Round(p.Z / grid) * grid;
            return p;
        }
    }
}
