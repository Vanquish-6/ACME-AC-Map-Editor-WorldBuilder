using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Acme.Dat;
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
    /// Room/prefab placement tool. First piece drops at origin. After that: click a
    /// glowing doorway, then click a room — it stamps there. R only if a piece would
    /// cover another doorway.
    /// </summary>
    public partial class RoomPlacementTool : DungeonToolBase {
        public override string Name => "Rooms";
        public override string IconGlyph => "\u25A3";
        public override string Description =>
            "Click a doorway so it turns yellow. Hover a room to preview it there, then click the room to place.";

        private static readonly ObservableCollection<DungeonSubToolBase> _empty = new();
        public override ObservableCollection<DungeonSubToolBase> AllSubTools => _empty;

        [ObservableProperty] private RoomEntry? _pendingRoom;
        [ObservableProperty] private DungeonPrefab? _pendingPrefab;

        public event Action? CancelRequested;

        /// <summary>Which open face on the pending piece attaches. -1 = pick the best automatically.</summary>
        private int _attachFaceIndex = -1;
        /// <summary>Extra 90° turns around the doorway after the snap (0–3).</summary>
        private int _twistQuarters;
        private int _candidateIndex;
        private bool _previewOverlaps;
        private bool _hasLockedPortal;
        private ushort _lockedCellNum;
        private ushort _lockedPolyId;
        private ushort _lastPlacedCellNum;
        private bool _hasPreview;
        private Vector3 _previewOrigin;
        private Quaternion _previewRot;
        private ushort _previewTargetCell;
        private ushort _previewTargetPoly;
        private ushort _previewSourcePoly;
        private int _previewFaceCellIndex;
        private bool _lastPreviewSnappedToPortal;

        private readonly record struct FitCandidate(
            SnapResult Snap, float Score, int FaceIndex, int Yaw, bool Overlaps, int BlockedDoors);

        private List<FitCandidate> _candidates = new();
        private readonly Dictionary<CandidateCacheKey, List<FitCandidate>> _candidateMemo = new();
        private const int MaxCandidateMemo = 24;

        private readonly record struct CandidateCacheKey(
            string PieceKey, ushort TargetCell, ushort TargetPoly, int CellCount, int PortalFingerprint);

        public bool HasLockedPortal => _hasLockedPortal;

        public bool HasPendingPiece => _pendingPrefab != null || _pendingRoom != null;

        public bool NeedsFitHelp => HasPendingPiece && _previewOverlaps;

        public bool PreviewIsValid => HasPendingPiece && _hasPreview && !_previewOverlaps && _lastPreviewSnappedToPortal;

        public string PlacementBlockReason { get; private set; } = "";

        public string ConnectingDoorwayText { get; private set; } = "";

        public string FitHint {
            get {
                if (!HasPendingPiece) {
                    return _hasLockedPortal
                        ? "Yellow doorway is selected. Listed rooms used that door in retail — hover to preview, click to place."
                        : "Click a doorway — it turns yellow. That's where the next room goes.";
                }
                if (_previewOverlaps)
                    return string.IsNullOrEmpty(PlacementBlockReason)
                        ? "That room does not fit this doorway. Press R for another fit, or pick a different piece."
                        : PlacementBlockReason + " Press R for another fit, Q/E to rotate, Esc to cancel.";
                if (PreviewIsValid)
                    return "Green ghost is a valid fit. Click the room, or press Enter to place. Q/E rotate · Esc cancel.";
                return "Click a different doorway to move the preview, or click the room again to place.";
            }
        }

        public override void OnActivated() { UpdateStatus(); }

        public override void OnDeactivated() {
            _pendingRoom = null;
            _pendingPrefab = null;
            _attachFaceIndex = -1;
            _twistQuarters = 0;
            _candidateIndex = 0;
            _hasPreview = false;
            _previewOverlaps = false;
            _candidates.Clear();
            StatusText = "";
            _lastPreviewSnappedToPortal = false;
        }

        public void SetRoom(RoomEntry room) {
            _pendingRoom = room;
            _pendingPrefab = null;
            _attachFaceIndex = -1;
            _twistQuarters = 0;
            _candidateIndex = 0;
            _hasPreview = false;
            _previewOverlaps = false;
            _candidates.Clear();
            UpdateStatus();
        }

        public void SetPrefab(DungeonPrefab prefab) {
            _pendingPrefab = prefab;
            _pendingRoom = null;
            _attachFaceIndex = -1;
            _twistQuarters = 0;
            _candidateIndex = 0;
            _hasPreview = false;
            _previewOverlaps = false;
            _candidates.Clear();
            UpdateStatus();
        }

        public void ClearPendingKeepLock() {
            _pendingPrefab = null;
            _pendingRoom = null;
            _attachFaceIndex = -1;
            _twistQuarters = 0;
            _candidateIndex = 0;
            _hasPreview = false;
            _previewOverlaps = false;
            _candidates.Clear();
            UpdateStatus();
        }

        private void UpdateStatus() {
            UpdatePlacementDiagnostics(null);
            StatusText = FitHint;
        }

        private void UpdatePlacementDiagnostics(DungeonEditingContext? ctx) {
            if (_previewOverlaps) {
                FitCandidate c = default;
                if (_candidates.Count > 0)
                    c = _candidates[Math.Clamp(_candidateIndex, 0, _candidates.Count - 1)];
                if (c.Overlaps && c.BlockedDoors > 0)
                    PlacementBlockReason = "Blocked: this piece overlaps another room and would cover an open doorway.";
                else if (c.Overlaps)
                    PlacementBlockReason = "Blocked: this piece overlaps another room.";
                else if (c.BlockedDoors > 0)
                    PlacementBlockReason = "Blocked: this piece would cover another open doorway.";
                else
                    PlacementBlockReason = "Blocked: this piece does not fit the selected doorway.";
            }
            else {
                PlacementBlockReason = "";
            }

            if (_hasLockedPortal) {
                string label = $"room 0x{_lockedCellNum:X4}";
                if (ctx?.Document != null) {
                    var dc = ctx.Document.GetCell(_lockedCellNum);
                    if (dc != null) {
                        var name = ctx.RoomPalette?.GetRoomDisplayName(
                            (uint)(dc.EnvironmentId | 0x0D000000), dc.CellStructure);
                        if (!string.IsNullOrEmpty(name)) label = name;
                    }
                }
                ConnectingDoorwayText = $"Connecting to the yellow doorway on {label}.";
            }
            else {
                ConnectingDoorwayText = "";
            }
        }

        private string AttachDoorLabel() {
            if (_pendingPrefab != null && _attachFaceIndex >= 0 && _attachFaceIndex < _pendingPrefab.OpenFaces.Count) {
                var dir = FriendlyDir(_pendingPrefab.OpenFaces[_attachFaceIndex].DirectionLabel);
                return $" Attaching this piece's {dir} door.";
            }
            return "";
        }

        private static string FriendlyDir(string dir) => dir switch {
            "N" => "North",
            "S" => "South",
            "E" => "East",
            "W" => "West",
            _ => dir
        };

        public override bool HandleMouseDown(MouseState mouseState, DungeonEditingContext ctx) {
            if (!mouseState.LeftPressed || mouseState.RightPressed) return false;
            if (ctx.Document == null || ctx.Dats == null || ctx.Scene == null) return false;

            if (ctx.Document.Cells.Count == 0) {
                if (HasPendingPiece) {
                    PlacePendingAtOrigin(ctx);
                    return true;
                }
                return false;
            }

            var ray = ctx.ComputeRay(mouseState);
            DungeonPortalPicker.Hit? clicked = null;
            if (ray != null)
                clicked = DungeonPortalPicker.Pick(ctx.Scene.OpenPortalIndicators, ray.Value.origin, ray.Value.direction);

            if (clicked == null)
                return false;

            FocusPortal(ctx, clicked.Value.CellNum, clicked.Value.PolyId);
            if (HasPendingPiece)
                RefreshPreview(ctx);
            ctx.SetStatus("Yellow doorway selected. Hover a room to preview, click the room to place.");
            StatusText = FitHint;
            return true;
        }

        public override bool HandleMouseUp(MouseState mouseState, DungeonEditingContext ctx) => false;

        public override bool HandleMouseMove(MouseState mouseState, DungeonEditingContext ctx) {
            if (ctx.Scene == null) return false;
            var pick = DungeonPortalPicker.Pick(ctx, mouseState);
            if (pick != null) {
                ctx.Scene.HoveredPortalCellNum = pick.Value.CellNum;
                ctx.Scene.HoveredPortalPolyId = pick.Value.PolyId;
            }
            else {
                ctx.Scene.HoveredPortalCellNum = 0;
                ctx.Scene.HoveredPortalPolyId = 0;
            }
            return false;
        }

        public void RefreshPreview(DungeonEditingContext ctx, Vector3? rayOrigin = null, Vector3? rayDir = null) {
            if (!HasPendingPiece || ctx.Scene == null) return;
            if (ctx.Document == null || ctx.Dats == null) return;
            if (ctx.Document.Cells.Count == 0) {
                ApplyGhost(ctx, Vector3.Zero, Quaternion.Identity);
                _lastPreviewSnappedToPortal = false;
                return;
            }

            OpenPortalHit? target;
            if (_hasLockedPortal)
                target = GetLockedHit(ctx) ?? GetPreferredOpen(ctx);
            else if (rayOrigin != null && rayDir != null)
                target = FindNearestOpenPortalCell(ctx, rayOrigin.Value, rayDir.Value) ?? GetPreferredOpen(ctx);
            else
                target = GetPreferredOpen(ctx);
            if (target == null) {
                ctx.Scene.ClearPreview();
                ctx.Scene.RoomPlacementPreview = null;
                _hasPreview = false;
                return;
            }

            var snap = ComputeSnapToTarget(ctx, target.Value);
            if (snap == null) {
                ctx.Scene.ClearPreview();
                ctx.Scene.RoomPlacementPreview = null;
                _hasPreview = false;
                _lastPreviewSnappedToPortal = false;
                return;
            }

            StorePreview(target.Value, snap.Value);
            _lastPreviewSnappedToPortal = true;
            if (ctx.Scene != null) {
                ctx.Scene.HighlightedPortalCellNum = target.Value.CellNum;
                ctx.Scene.HighlightedPortalPolyId = target.Value.PortalId;
            }
            ApplyGhost(ctx, snap.Value.Origin, snap.Value.Orientation);
            UpdateStatus();
        }

        public bool TryGetLockedDoor(out ushort cellNum, out ushort polyId) {
            cellNum = _lockedCellNum;
            polyId = _lockedPolyId;
            return _hasLockedPortal;
        }

        public void ShowHoverPreview(DungeonEditingContext ctx, DungeonPrefab? prefab) {
            if (ctx.Scene == null) return;
            if (prefab == null) {
                if (HasPendingPiece) RefreshPreview(ctx);
                else {
                    ctx.Scene.ClearPreview();
                    ctx.Scene.RoomPlacementPreview = null;
                }
                return;
            }

            var savedPrefab = _pendingPrefab;
            var savedRoom = _pendingRoom;
            var savedHasPreview = _hasPreview;
            var savedOrigin = _previewOrigin;
            var savedRot = _previewRot;
            var savedTargetCell = _previewTargetCell;
            var savedTargetPoly = _previewTargetPoly;
            var savedSourcePoly = _previewSourcePoly;
            var savedFace = _previewFaceCellIndex;
            var savedOverlap = _previewOverlaps;
            var savedIndex = _candidateIndex;
            var savedAttach = _attachFaceIndex;
            var savedTwist = _twistQuarters;
            var savedCandidates = _candidates;
            var savedSnapped = _lastPreviewSnappedToPortal;

            _pendingPrefab = prefab;
            _pendingRoom = null;
            _attachFaceIndex = -1;
            _twistQuarters = 0;
            _candidateIndex = 0;
            _candidates = new List<FitCandidate>();
            if (!_hasLockedPortal) EnsureLockedPortal(ctx);
            var target = GetPreferredOpen(ctx);
            if (target != null) {
                RebuildCandidates(ctx, target.Value);
                int pick = _candidates.FindIndex(c => !c.Overlaps && c.BlockedDoors == 0);
                if (pick >= 0) {
                    var c = _candidates[pick];
                    _previewOverlaps = false;
                    _lastPreviewSnappedToPortal = true;
                    ApplyGhost(ctx, c.Snap.Origin, c.Snap.Orientation);
                }
                else {
                    ctx.Scene.ClearPreview();
                    ctx.Scene.RoomPlacementPreview = null;
                }
            }

            _pendingPrefab = savedPrefab;
            _pendingRoom = savedRoom;
            _hasPreview = savedHasPreview;
            _previewOrigin = savedOrigin;
            _previewRot = savedRot;
            _previewTargetCell = savedTargetCell;
            _previewTargetPoly = savedTargetPoly;
            _previewSourcePoly = savedSourcePoly;
            _previewFaceCellIndex = savedFace;
            _previewOverlaps = savedOverlap;
            _candidateIndex = savedIndex;
            _attachFaceIndex = savedAttach;
            _twistQuarters = savedTwist;
            _candidates = savedCandidates;
            _lastPreviewSnappedToPortal = savedSnapped;
        }

        public bool WouldFit(DungeonEditingContext ctx, DungeonPrefab prefab) {
            if (prefab == null || ctx.Document == null) return false;
            if (ctx.Document.Cells.Count == 0) return true;

            var savedPrefab = _pendingPrefab;
            var savedRoom = _pendingRoom;
            var savedHasPreview = _hasPreview;
            var savedOverlap = _previewOverlaps;
            var savedIndex = _candidateIndex;
            var savedAttach = _attachFaceIndex;
            var savedTwist = _twistQuarters;
            var savedCandidates = _candidates;
            var savedSnapped = _lastPreviewSnappedToPortal;

            _pendingPrefab = prefab;
            _pendingRoom = null;
            _attachFaceIndex = -1;
            _twistQuarters = 0;
            _candidateIndex = 0;
            _candidates = new List<FitCandidate>();
            if (!_hasLockedPortal) EnsureLockedPortal(ctx);
            var target = GetPreferredOpen(ctx);
            bool ok = false;
            if (target != null) {
                RebuildCandidates(ctx, target.Value);
                ok = _candidates.Exists(c => !c.Overlaps && c.BlockedDoors == 0);
            }

            _pendingPrefab = savedPrefab;
            _pendingRoom = savedRoom;
            _hasPreview = savedHasPreview;
            _previewOverlaps = savedOverlap;
            _candidateIndex = savedIndex;
            _attachFaceIndex = savedAttach;
            _twistQuarters = savedTwist;
            _candidates = savedCandidates;
            _lastPreviewSnappedToPortal = savedSnapped;
            return ok;
        }

        public void CycleAttachFace(DungeonEditingContext ctx) {
            CycleCandidate(ctx, +1);
        }

        public void CycleTwist(DungeonEditingContext ctx, int delta) {
            CycleCandidate(ctx, delta >= 0 ? +1 : -1);
        }

        private void CycleCandidate(DungeonEditingContext ctx, int delta) {
            var target = GetPreferredOpen(ctx);
            if (target == null) return;
            RebuildCandidates(ctx, target.Value);
            if (_candidates.Count == 0) return;
            _candidateIndex = ((_candidateIndex + delta) % _candidates.Count + _candidates.Count) % _candidates.Count;
            var c = _candidates[_candidateIndex];
            _attachFaceIndex = c.FaceIndex;
            _twistQuarters = c.Yaw;
            if (c.Overlaps || c.BlockedDoors > 0)
                ctx.SetStatus($"Fit {_candidateIndex + 1}/{_candidates.Count} would cover another doorway — press R again.");
            else
                ctx.SetStatus($"Fit {_candidateIndex + 1}/{_candidates.Count}. Click or press Enter to place.");
            UpdateStatus();
            UpdatePlacementDiagnostics(ctx);
            RefreshPreview(ctx);
        }

        /// <summary>
        /// Stamp the pending piece onto the locked/highlighted door using the best
        /// fit that does not overlap rooms or block other openings.
        /// </summary>
        public bool TryStampOnLocked(DungeonEditingContext ctx, bool allowOverlap) {
            var target = GetPreferredOpen(ctx);
            if (target == null) return false;
            RebuildCandidates(ctx, target.Value);
            if (_candidates.Count == 0) return false;

            int pick = _candidateIndex;
            if (!allowOverlap) {
                pick = _candidates.FindIndex(c => !c.Overlaps && c.BlockedDoors == 0);
                if (pick < 0) pick = _candidates.FindIndex(c => !c.Overlaps);
                if (pick < 0) {
                    _candidateIndex = 0;
                    StorePreview(target.Value, _candidates[0].Snap);
                    _previewOverlaps = true;
                    _lastPreviewSnappedToPortal = true;
                    return false;
                }
            }
            else {
                pick = Math.Clamp(_candidateIndex, 0, _candidates.Count - 1);
            }

            _candidateIndex = pick;
            var chosen = _candidates[pick];
            _previewOverlaps = chosen.Overlaps || chosen.BlockedDoors > 0;
            if (!allowOverlap && _previewOverlaps) return false;
            StorePreview(target.Value, chosen.Snap);
            _lastPreviewSnappedToPortal = true;
            return PlaceAtPreview(ctx);
        }

        public bool ConfirmLastPreview(DungeonEditingContext ctx) {
            if (!_hasPreview || !_lastPreviewSnappedToPortal) {
                var target = GetPreferredOpen(ctx);
                return target != null && TryPlaceAgainst(ctx, target.Value);
            }
            return PlaceAtPreview(ctx);
        }

        public bool TryGetFocusedPortal(DungeonEditingContext ctx, out (ushort env, ushort cs, ushort poly) key) {
            key = default;
            var hit = GetLockedHit(ctx);
            if (hit == null) return false;
            key = (hit.Value.Cell.EnvironmentId, hit.Value.Cell.CellStructure, hit.Value.PortalId);
            return true;
        }

        public void ApplyFocusHighlight(DungeonEditingContext ctx) {
            if (ctx.Scene == null) return;
            var hit = GetLockedHit(ctx);
            if (hit == null) {
                ctx.Scene.HighlightedPortalCellNum = 0;
                ctx.Scene.HighlightedPortalPolyId = 0;
                return;
            }
            ctx.Scene.HighlightedPortalCellNum = hit.Value.CellNum;
            ctx.Scene.HighlightedPortalPolyId = hit.Value.PortalId;
        }

        public void FocusPortal(DungeonEditingContext ctx, ushort cellNum, ushort polyId) {
            _hasLockedPortal = true;
            _lockedCellNum = cellNum;
            _lockedPolyId = polyId;
            ApplyFocusHighlight(ctx);
            var dc = ctx.Document?.GetCell(cellNum);
            if (dc != null && ctx.RoomPalette != null) {
                ctx.RoomPalette.SetActiveOpenPortals(new List<(ushort, ushort, ushort)> {
                    (dc.EnvironmentId, dc.CellStructure, polyId)
                }, compatibleOnly: ctx.RoomPalette.IsStarterKitMode);
            }
        }

        public void LockFirstOpenPortal(DungeonEditingContext ctx) {
            LockContinuationPortal(ctx);
        }

        private void LockContinuationPortal(DungeonEditingContext ctx) {
            var existing = ctx.Document != null && ctx.GeometryCache != null
                ? PortalPlacementFit.CollectExisting(ctx.Document, ctx.GeometryCache)
                : new List<PortalPlacementFit.ExistingRoom>();

            OpenPortalHit? best = null;
            float bestScore = float.MinValue;
            var connection = ctx.Document?.GetCell(_lastPlacedCellNum)?.Origin ?? Vector3.Zero;

            foreach (var hit in EnumerateOpenPortals(ctx)) {
                var geom = PortalSnapper.GetPortalGeometry(hit.CellStruct, hit.PortalId);
                if (geom == null) continue;
                var (centroid, normal) = PortalSnapper.TransformPortalToWorld(
                    geom.Value, hit.Cell.Origin, hit.Cell.Orientation);
                var probe = centroid + Vector3.Normalize(normal) * PortalPlacementFit.ProbeDistance;
                bool blocked = existing.Any(r => r.CellNum != hit.CellNum && r.WorldAabb.ContainsPoint(probe));
                float dist = (centroid - connection).Length();
                float score = (hit.CellNum == _lastPlacedCellNum ? 80f : 0f) + dist - (blocked ? 200f : 0f);
                if (score > bestScore) {
                    bestScore = score;
                    best = hit;
                }
            }

            if (best != null)
                LockPortal(ctx, best.Value, updatePalette: false);
        }

        public void EnsureLockedPortal(DungeonEditingContext ctx) {
            if (GetLockedHit(ctx) != null) {
                ApplyFocusHighlight(ctx);
                return;
            }
            LockFirstOpenPortal(ctx);
            ApplyFocusHighlight(ctx);
        }

        private void ApplyGhost(DungeonEditingContext ctx, Vector3 snapOrigin, Quaternion snapRot) {
            if (ctx.Scene == null) return;
            ctx.Scene.PreviewIsSnapped = _lastPreviewSnappedToPortal && !_previewOverlaps;
            UpdatePlacementDiagnostics(ctx);

            if (_pendingPrefab != null && _pendingPrefab.Cells.Count > 0) {
                var previewCells = ctx.BuildPrefabEnvCells(
                    _pendingPrefab, snapOrigin, snapRot, _previewFaceCellIndex);
                if (previewCells.Count > 0) {
                    ctx.Scene.PreviewEnvCells = previewCells;
                    ctx.Scene.RoomPlacementPreview = null;
                }
                return;
            }

            if (_pendingRoom != null) {
                var surfaces = ctx.GetSurfacesForRoom(_pendingRoom);
                var envCell = new EnvCell {
                    Id = 0xFFFE0100,
                    EnvironmentId = _pendingRoom.EnvironmentId,
                    CellStructure = _pendingRoom.CellStructureIndex,
                    Position = new Frame {
                        Origin = snapOrigin,
                        Orientation = snapRot
                    }
                };
                envCell.Surfaces.AddRange(surfaces.Select(s => (uint)s));
                ctx.Scene.PreviewEnvCells = new List<EnvCell> { envCell };

                ctx.Scene.RoomPlacementPreview = new RoomPlacementPreviewData {
                    Origin = snapOrigin,
                    Orientation = snapRot,
                    EnvFileId = _pendingRoom.EnvironmentFileId,
                    CellStructIndex = _pendingRoom.CellStructureIndex
                };
            }
        }

        private (Vector3 origin, Quaternion orientation)? ComputePreviewForAny(
            DungeonEditingContext ctx, Vector3 rayOrigin, Vector3 rayDir) {

            if (ctx.Document == null || ctx.Dats == null) return null;

            _lastPreviewSnappedToPortal = false;

            if (ctx.Document.Cells.Count == 0)
                return (Vector3.Zero, Quaternion.Identity);

            var target = GetLockedHit(ctx) ?? FindNearestOpenPortalCell(ctx, rayOrigin, rayDir);
            if (target != null) {
                var snapped = ComputeSnapToTarget(ctx, target.Value);
                if (snapped != null) {
                    _lastPreviewSnappedToPortal = true;
                    StorePreview(target.Value, snapped.Value);
                    if (ctx.Scene != null) {
                        ctx.Scene.HighlightedPortalCellNum = target.Value.CellNum;
                        ctx.Scene.HighlightedPortalPolyId = target.Value.PortalId;
                    }
                    return (snapped.Value.Origin, snapped.Value.Orientation);
                }
            }

            var geoHit = ctx.Scene?.EnvCellManager?.Raycast(rayOrigin, rayDir);
            if (geoHit != null && geoHit.Value.Hit)
                return (geoHit.Value.HitPosition, Quaternion.Identity);

            float planeZ = ctx.Document.Cells.Count > 0
                ? ctx.Document.Cells.Average(c => c.Origin.Z) : 0f;
            var hit = RayHitHorizontalPlane(rayOrigin, rayDir, planeZ);
            return hit.HasValue ? (hit.Value, Quaternion.Identity) : null;
        }

        private static Vector3? RayHitHorizontalPlane(Vector3 rayOrigin, Vector3 rayDir, float planeZ) {
            if (MathF.Abs(rayDir.Z) < 1e-6f) return null;
            float t = (planeZ - rayOrigin.Z) / rayDir.Z;
            if (t < 0) return null;
            return rayOrigin + rayDir * t;
        }

        public override bool HandleKeyDown(KeyEventArgs e, DungeonEditingContext ctx) {
            if (e.Key == Key.Escape) {
                if (ctx.Scene != null) {
                    ctx.Scene.RoomPlacementPreview = null;
                    ctx.Scene.ClearPreview();
                    ctx.Scene.HighlightedPortalCellNum = 0;
                    ctx.Scene.HighlightedPortalPolyId = 0;
                    ctx.Scene.HoveredPortalCellNum = 0;
                    ctx.Scene.HoveredPortalPolyId = 0;
                }
                CancelRequested?.Invoke();
                return true;
            }

            if (!HasPendingPiece) return false;

            if (e.Key is Key.R or Key.Tab) {
                CycleAttachFace(ctx);
                return true;
            }
            if (e.Key == Key.E || e.Key == Key.OemCloseBrackets) {
                CycleTwist(ctx, +1);
                return true;
            }
            if (e.Key == Key.Q || e.Key == Key.OemOpenBrackets) {
                CycleTwist(ctx, -1);
                return true;
            }
            if (e.Key is Key.Enter or Key.Space or Key.Return) {
                ConfirmLastPreview(ctx);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Drop the pending room or prefab at landblock origin. Used for the first piece
        /// so the user does not have to click empty 3D space.
        /// </summary>
        public void PlacePendingAtOrigin(DungeonEditingContext ctx) {
            if (ctx.Document == null) return;
            if (_pendingPrefab != null)
                PlacePrefabAtOrigin(ctx);
            else if (_pendingRoom != null)
                PlaceFirstCell(ctx);
        }

        private void PlaceFirstCell(DungeonEditingContext ctx) {
            if (_pendingRoom == null || ctx.Document == null) return;
            var surfaces = ctx.GetSurfacesForRoom(_pendingRoom);
            var cmd = new AddCellCommand(
                _pendingRoom.EnvironmentId, _pendingRoom.CellStructureIndex,
                Vector3.Zero, Quaternion.Identity, surfaces);
            ctx.CommandHistory.Execute(cmd, ctx.Document);
            _lastPlacedCellNum = cmd.CreatedCellNum;
            AfterSuccessfulPlace(ctx, new List<ushort> { cmd.CreatedCellNum }, focusCamera: true);
            ctx.SetStatus("First room placed. Click a doorway so it turns yellow, then hover a room to preview.");
        }

        private void PlacePrefabAtOrigin(DungeonEditingContext ctx) {
            if (_pendingPrefab == null || ctx.Document == null || ctx.Dats == null) return;

            var composite = new DungeonCompositeCommand("Place Prefab");
            var first = _pendingPrefab.Cells[0];

            var firstCmd = new AddCellCommand(first.EnvId, first.CellStruct,
                Vector3.Zero, Quaternion.Identity, first.Surfaces.ToList());
            firstCmd.Execute(ctx.Document);
            composite.Add(firstCmd);

            var cellMap = new Dictionary<int, ushort> { [0] = firstCmd.CreatedCellNum };
            PlaceRemainingPrefabCells(ctx, _pendingPrefab, cellMap, composite);

            ctx.CommandHistory.Record(composite);
            _lastPlacedCellNum = firstCmd.CreatedCellNum;
            AfterSuccessfulPlace(ctx, cellMap.Values.ToList(), focusCamera: true);
            ctx.SetStatus("First room placed. Click a doorway so it turns yellow, then hover a room to preview.");
        }

        private bool TryPlaceAgainst(DungeonEditingContext ctx, OpenPortalHit target) {
            LockPortal(ctx, target, updatePalette: false);
            RebuildCandidates(ctx, target);
            if (_candidates.Count == 0) return false;
            int pick = _candidates.FindIndex(c => !c.Overlaps && c.BlockedDoors == 0);
            if (pick < 0) pick = _candidates.FindIndex(c => !c.Overlaps);
            if (pick < 0) pick = 0;
            _candidateIndex = pick;
            var chosen = _candidates[pick];
            _previewOverlaps = chosen.Overlaps || chosen.BlockedDoors > 0;
            StorePreview(target, chosen.Snap);
            if (_previewOverlaps)
                return false;
            return PlaceAtPreview(ctx);
        }

        private bool PlaceAtPreview(DungeonEditingContext ctx) {
            if (!_hasPreview || ctx.Document == null || ctx.Dats == null) return false;

            if (_pendingPrefab != null)
                return PlacePrefabAt(ctx, _previewOrigin, _previewRot, _previewTargetCell, _previewTargetPoly, _previewSourcePoly, _previewFaceCellIndex);

            if (_pendingRoom != null) {
                var surfaces = ctx.GetSurfacesForRoom(_pendingRoom);
                var cmd = new AddCellCommand(
                    _pendingRoom.EnvironmentId, _pendingRoom.CellStructureIndex,
                    _previewOrigin, _previewRot, surfaces,
                    connectToCellNum: _previewTargetCell, connectToPolyId: _previewTargetPoly, sourcePolyId: _previewSourcePoly);
                ctx.CommandHistory.Execute(cmd, ctx.Document);
                _lastPlacedCellNum = cmd.CreatedCellNum;
                AfterSuccessfulPlace(ctx, new List<ushort> { cmd.CreatedCellNum }, focusCamera: false);
                ctx.SetStatus($"{ctx.Document.Cells.Count} rooms. Click a doorway, then hover a room to preview.");
                return true;
            }
            return false;
        }

        private bool PlacePrefabAt(DungeonEditingContext ctx, Vector3 snapOrigin, Quaternion snapRot,
            ushort targetCellNum, ushort targetPoly, ushort sourcePoly, int faceCellIndex) {
            if (_pendingPrefab == null || ctx.Document == null) return false;

            var prefabCell = _pendingPrefab.Cells[faceCellIndex];
            var composite = new DungeonCompositeCommand("Snap Prefab");
            var connectCmd = new AddCellCommand(prefabCell.EnvId, prefabCell.CellStruct,
                snapOrigin, snapRot, prefabCell.Surfaces.ToList(),
                connectToCellNum: targetCellNum, connectToPolyId: targetPoly, sourcePolyId: sourcePoly);
            connectCmd.Execute(ctx.Document);
            composite.Add(connectCmd);

            var cellMap = new Dictionary<int, ushort> { [faceCellIndex] = connectCmd.CreatedCellNum };
            PlaceRemainingPrefabCells(ctx, _pendingPrefab, cellMap, composite);

            ctx.CommandHistory.Record(composite);
            _lastPlacedCellNum = connectCmd.CreatedCellNum;
            AfterSuccessfulPlace(ctx, cellMap.Values.ToList(), focusCamera: false);
            ctx.SetStatus($"{ctx.Document.Cells.Count} rooms. Click a doorway, then hover a room to preview.");
            return true;
        }

        private void AfterSuccessfulPlace(DungeonEditingContext ctx, List<ushort> newCellNums, bool focusCamera) {
            if (ctx.Scene != null) {
                ctx.Scene.ClearPreview();
                ctx.Scene.RoomPlacementPreview = null;
            }
            _hasPreview = false;
            _previewOverlaps = false;
            _candidateIndex = 0;
            _attachFaceIndex = -1;
            _twistQuarters = 0;
            _candidates.Clear();
                ClearPendingKeepLock();
            LockContinuationPortal(ctx);
            ctx.NotifyCellsAdded(newCellNums);
            if (focusCamera)
            ctx.RequestCameraFocus();
            ApplyFocusHighlight(ctx);
        }

        private readonly record struct SnapResult(Vector3 Origin, Quaternion Orientation, ushort SourcePoly, int FaceCellIndex);

        private readonly record struct OpenPortalHit(
            DungeonCellData Cell, CellStruct CellStruct, ushort PortalId, ushort CellNum);

        private readonly record struct SourceFace(ushort EnvId, ushort CellStruct, ushort PolyId, int CellIndex, string Dir);

        private List<SourceFace> GetSourceFaces(DungeonEditingContext ctx) {
            var list = new List<SourceFace>();
            if (_pendingPrefab != null) {
                foreach (var of in _pendingPrefab.OpenFaces)
                    list.Add(new SourceFace(of.EnvId, of.CellStruct, of.PolyId, of.CellIndex, of.DirectionLabel));
                return list;
            }
            if (_pendingRoom == null || ctx.Dats == null) return list;
            uint envFileId = _pendingRoom.EnvironmentFileId;
            if (!ctx.Dats.TryGet<Acme.Dat.Environment>(envFileId, out var srcEnv)) return list;
            if (!srcEnv.Cells.TryGetValue(_pendingRoom.CellStructureIndex, out var srcCS)) return list;
            foreach (var pid in PortalSnapper.GetPortalPolygonIds(srcCS)) {
                var geom = PortalSnapper.GetPortalGeometry(srcCS, pid);
                string dir = "";
                if (geom != null)
                    dir = DungeonPrefab.ClassifyDirection(geom.Value.Normal.X, geom.Value.Normal.Y, geom.Value.Normal.Z);
                list.Add(new SourceFace(_pendingRoom.EnvironmentId, _pendingRoom.CellStructureIndex, pid, 0, dir));
            }
            return list;
        }

        private SnapResult? ComputeSnapToTarget(DungeonEditingContext ctx, OpenPortalHit target) {
            RebuildCandidates(ctx, target);
            if (_candidates.Count == 0) return null;
            if (_candidateIndex < 0 || _candidateIndex >= _candidates.Count)
                _candidateIndex = 0;
            var chosen = _candidates[_candidateIndex];
            _previewOverlaps = chosen.Overlaps || chosen.BlockedDoors > 0;
            _attachFaceIndex = chosen.FaceIndex;
            _twistQuarters = chosen.Yaw;
            return chosen.Snap;
        }

        private void RebuildCandidates(DungeonEditingContext ctx, OpenPortalHit target) {
            _candidates = new List<FitCandidate>();
            if (ctx.Dats == null || ctx.Document == null) return;

            var cacheKey = MakeCandidateKey(ctx.Document, target);
            if (_candidateMemo.TryGetValue(cacheKey, out var cached)) {
                _candidates = new List<FitCandidate>(cached);
                return;
            }

            var faces = GetSourceFaces(ctx);
            if (faces.Count == 0) return;

            var targetLocal = PortalSnapper.GetPortalGeometry(target.CellStruct, target.PortalId);
            if (targetLocal == null) return;
            var (centroidW, normalW) = PortalSnapper.TransformPortalToWorld(
                targetLocal.Value, target.Cell.Origin, target.Cell.Orientation);
            var targetWorld = PortalSnapper.TransformGeometryToWorld(
                targetLocal.Value, target.Cell.Origin, target.Cell.Orientation);
            bool wallDoor = MathF.Abs(normalW.Z) < 0.55f;
            var targetN = Vector3.Normalize(normalW);

            var existing = PortalPlacementFit.CollectExisting(ctx.Document, ctx.GeometryCache);
            var dungeonCenter = existing.Count > 0
                ? existing.Aggregate(Vector3.Zero, (s, r) => s + r.Origin) / existing.Count
                : Vector3.Zero;

            var otherDoors = new List<PortalPlacementFit.OpenDoorProbe>();
            foreach (var hit in EnumerateOpenPortals(ctx)) {
                if (hit.CellNum == target.CellNum && hit.PortalId == target.PortalId) continue;
                var g = PortalSnapper.GetPortalGeometry(hit.CellStruct, hit.PortalId);
                if (g == null) continue;
                var (c, n) = PortalSnapper.TransformPortalToWorld(g.Value, hit.Cell.Origin, hit.Cell.Orientation);
                otherDoors.Add(new PortalPlacementFit.OpenDoorProbe(hit.CellNum, hit.PortalId, c, n));
            }

            bool retailOnly = UsesRetailDoorWiring(_pendingPrefab)
                && ctx.PortalIndex != null
                && ctx.PortalIndex.PortalFaceCount > 0;

            for (int faceIdx = 0; faceIdx < faces.Count; faceIdx++) {
                var face = faces[faceIdx];
                uint envFileId = (uint)(face.EnvId | 0x0D000000);
                if (!ctx.Dats.TryGet<Acme.Dat.Environment>(envFileId, out var pfEnv)) continue;
                if (!pfEnv.Cells.TryGetValue(face.CellStruct, out var pfCS)) continue;
                var srcGeom = PortalSnapper.GetPortalGeometry(pfCS, face.PolyId);
                if (srcGeom == null) continue;

                TryAddProvenCandidates(
                    ctx, target, existing, otherDoors, dungeonCenter,
                    faceIdx, face, srcGeom.Value);

                // Kit cells: only the recorded DAT plug for this doorway.
                // Extracted multi-cell chunks can still try a geometric snap.
                if (retailOnly) continue;

                if (wallDoor) {
                    for (int yaw = 0; yaw < 4; yaw++) {
                        var rot = PortalSnapper.YawQuarter(yaw);
                        var rotatedN = Vector3.Normalize(Vector3.Transform(srcGeom.Value.Normal, rot));
                        if (Vector3.Dot(rotatedN, targetN) > -0.35f) continue;
                        var origin = centroidW - Vector3.Transform(srcGeom.Value.Centroid, rot);
                        AddCandidate(ctx, target, existing, otherDoors, dungeonCenter,
                            faceIdx, yaw, face, origin, rot, srcGeom.Value);
                    }
                }
                else {
                    var (baseOrigin, baseRot) = PortalSnapper.ComputeSnapTransform(
                        centroidW, normalW, srcGeom.Value, targetWorld);
                    for (int q = 0; q < 4; q++) {
                        var (origin, rot) = q == 0
                            ? (baseOrigin, baseRot)
                            : PortalSnapper.ApplyPortalTwist(centroidW, normalW, srcGeom.Value.Centroid, baseRot, q);
                        AddCandidate(ctx, target, existing, otherDoors, dungeonCenter,
                            faceIdx, q, face, origin, rot, srcGeom.Value);
                    }
                }
            }

            _candidates = _candidates
                .GroupBy(c => (
                    MathF.Round(c.Snap.Origin.X, 1),
                    MathF.Round(c.Snap.Origin.Y, 1),
                    MathF.Round(c.Snap.Origin.Z, 1),
                    c.FaceIndex,
                    c.Yaw))
                .Select(g => g.OrderBy(c => c.Score).First())
                .OrderBy(c => c.Overlaps ? 1 : 0)
                .ThenBy(c => c.BlockedDoors)
                .ThenBy(c => c.Score)
                .ToList();

            if (_candidateMemo.Count >= MaxCandidateMemo)
                _candidateMemo.Clear();
            _candidateMemo[cacheKey] = new List<FitCandidate>(_candidates);
        }

        private CandidateCacheKey MakeCandidateKey(DungeonDocument document, OpenPortalHit target) =>
            new(CurrentPieceKey(), target.CellNum, target.PortalId, document.Cells.Count, DocumentPortalFingerprint(document));

        private string CurrentPieceKey() {
            if (_pendingPrefab != null) return "p:" + _pendingPrefab.Signature;
            if (_pendingRoom != null) return $"r:{_pendingRoom.EnvironmentFileId:X8}:{_pendingRoom.CellStructureIndex}";
            return "";
        }

        private static int DocumentPortalFingerprint(DungeonDocument document) {
            int h = document.Cells.Count;
            foreach (var cell in document.Cells) {
                h = (h * 31) ^ cell.CellNumber;
                h = (h * 31) ^ cell.CellPortals.Count;
                foreach (var p in cell.CellPortals)
                    h = (h * 31) ^ (p.PolygonId << 16 | p.OtherCellId);
            }
            return h;
        }

        private static bool UsesRetailDoorWiring(DungeonPrefab? prefab) =>
            prefab != null && (
                prefab.Signature.StartsWith("kitcell_", StringComparison.OrdinalIgnoreCase)
                || (prefab.Cells.Count == 1 && !string.IsNullOrEmpty(prefab.KitRole)));

        private bool TryAddProvenCandidates(
            DungeonEditingContext ctx,
            OpenPortalHit target,
            List<PortalPlacementFit.ExistingRoom> existing,
            List<PortalPlacementFit.OpenDoorProbe> otherDoors,
            Vector3 dungeonCenter,
            int faceIdx, SourceFace face,
            PortalSnapper.PortalGeometry srcGeom) {

            if (ctx.PortalIndex == null) return false;
            var matches = ctx.PortalIndex.GetProvenMatches(
                target.Cell.EnvironmentId, target.Cell.CellStructure, target.PortalId,
                face.EnvId, face.CellStruct, face.PolyId);
            if (matches.Count == 0) return false;

            for (int i = 0; i < matches.Count; i++) {
                var match = matches[i];
                var origin = target.Cell.Origin + Vector3.Transform(match.RelOffset, target.Cell.Orientation);
                var ori = Quaternion.Normalize(target.Cell.Orientation * match.RelRot);
                float bias = -120f - Math.Min(match.Count, 4000) / 80f;
                if (match.ExactMatch) bias -= 20f;
                AddCandidate(ctx, target, existing, otherDoors, dungeonCenter,
                    faceIdx, i, face, origin, ori, srcGeom, bias);
            }
            return true;
        }

        private void AddCandidate(
            DungeonEditingContext ctx,
            OpenPortalHit target,
            List<PortalPlacementFit.ExistingRoom> existing,
            List<PortalPlacementFit.OpenDoorProbe> otherDoors,
            Vector3 dungeonCenter,
            int faceIdx, int yaw, SourceFace face,
            Vector3 origin, Quaternion rot,
            PortalSnapper.PortalGeometry srcGeom,
            float scoreBias = 0f) {

            var tgtGeom = PortalSnapper.GetPortalGeometry(target.CellStruct, target.PortalId);
            if (tgtGeom != null) {
                var (tc, tn) = PortalSnapper.TransformPortalToWorld(
                    tgtGeom.Value, target.Cell.Origin, target.Cell.Orientation);
                (origin, rot) = PortalSnapper.ComputeFlushSnap(tc, tn, srcGeom, rot);
            }

            List<PortalPlacementFit.WorldCellPose> poses;
            if (_pendingPrefab != null)
                poses = PortalPlacementFit.PrefabWorldCells(_pendingPrefab, face.CellIndex, origin, rot);
            else
                poses = new List<PortalPlacementFit.WorldCellPose> {
                    new(origin, rot, face.EnvId, face.CellStruct)
                };

            var aabbs = PortalPlacementFit.CandidateAabbs(poses, ctx.GeometryCache);
            bool overlaps = PortalPlacementFit.OverlapsExisting(aabbs, existing, target.CellNum);
            int blocked = PortalPlacementFit.CountBlockedOpenDoors(aabbs, otherDoors);

            int bornBlocked = 0;
            if (_pendingPrefab != null) {
                for (int i = 0; i < _pendingPrefab.OpenFaces.Count; i++) {
                    if (i == faceIdx) continue;
                    var of = _pendingPrefab.OpenFaces[i];
                    var n = new Vector3(of.NormalX, of.NormalY, of.NormalZ);
                    if (n.LengthSquared() < 0.01f) continue;
                    var worldN = Vector3.Normalize(Vector3.Transform(n, rot));
                    var probe = origin + worldN * PortalPlacementFit.ProbeDistance;
                    if (existing.Any(r => r.CellNum != target.CellNum && r.WorldAabb.ContainsPoint(probe)))
                        bornBlocked++;
                }
            }

            float frame = 0f;
            if (tgtGeom != null) {
                var (tc, tn) = PortalSnapper.TransformPortalToWorld(tgtGeom.Value, target.Cell.Origin, target.Cell.Orientation);
                var sc = origin + Vector3.Transform(srcGeom.Centroid, rot);
                var srcWorldN = Vector3.Normalize(Vector3.Transform(srcGeom.Normal, rot));
                frame = Vector3.Distance(tc, sc) + (1f + Vector3.Dot(srcWorldN, Vector3.Normalize(tn))) * 2f;
            }

            float growth = -PortalPlacementFit.GrowthScore(origin, dungeonCenter) * 0.02f;
            float score = (overlaps ? 1000f : 0f) + blocked * 180f + bornBlocked * 40f + frame + growth + scoreBias;

            var snap = new SnapResult(origin, rot, face.PolyId, face.CellIndex);
            _candidates.Add(new FitCandidate(snap, score, faceIdx, yaw, overlaps, blocked + bornBlocked));
        }

        private void StorePreview(OpenPortalHit target, SnapResult snap) {
            _hasPreview = true;
            _previewOrigin = snap.Origin;
            _previewRot = snap.Orientation;
            _previewTargetCell = target.CellNum;
            _previewTargetPoly = target.PortalId;
            _previewSourcePoly = snap.SourcePoly;
            _previewFaceCellIndex = snap.FaceCellIndex;
        }

        private void LockPortal(DungeonEditingContext ctx, OpenPortalHit hit, bool updatePalette) {
            _hasLockedPortal = true;
            _lockedCellNum = hit.CellNum;
            _lockedPolyId = hit.PortalId;
            ApplyFocusHighlight(ctx);
            if (updatePalette && ctx.RoomPalette != null) {
                ctx.RoomPalette.SetActiveOpenPortals(new List<(ushort, ushort, ushort)> {
                    (hit.Cell.EnvironmentId, hit.Cell.CellStructure, hit.PortalId)
                }, compatibleOnly: ctx.RoomPalette.IsStarterKitMode);
            }
        }

        private OpenPortalHit? GetLockedHit(DungeonEditingContext ctx) {
            if (!_hasLockedPortal || ctx.Document == null || ctx.Dats == null) return null;
            var dc = ctx.Document.GetCell(_lockedCellNum);
            if (dc == null) return null;
            if (dc.CellPortals.Any(cp => cp.PolygonId == _lockedPolyId)) return null;
            uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
            if (!ctx.Dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return null;
            if (!env.Cells.TryGetValue(dc.CellStructure, out var cs)) return null;
            var allPortals = PortalSnapper.GetPortalPolygonIds(cs);
            if (!allPortals.Contains(_lockedPolyId)) return null;
            return new OpenPortalHit(dc, cs, _lockedPolyId, dc.CellNumber);
        }

        private OpenPortalHit? GetPreferredOpen(DungeonEditingContext ctx) =>
            GetLockedHit(ctx) ?? GetFirstOpenOnCell(ctx, _lastPlacedCellNum) ?? GetAnyOpen(ctx);

        private OpenPortalHit? GetFirstOpenOnCell(DungeonEditingContext ctx, ushort cellNum) {
            foreach (var hit in EnumerateOpenPortals(ctx)) {
                if (hit.CellNum == cellNum) return hit;
            }
            return null;
        }

        private OpenPortalHit? GetAnyOpen(DungeonEditingContext ctx) {
            foreach (var hit in EnumerateOpenPortals(ctx))
                return hit;
            return null;
        }

        private static IEnumerable<OpenPortalHit> EnumerateOpenPortals(DungeonEditingContext ctx) {
            if (ctx.Document == null || ctx.Dats == null) yield break;
            foreach (var dc in ctx.Document.Cells) {
                uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
                if (!ctx.Dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(dc.CellStructure, out var cs)) continue;
                var allPortals = PortalSnapper.GetPortalPolygonIds(cs);
                var connected = new HashSet<ushort>(dc.CellPortals.Select(cp => cp.PolygonId));
                foreach (var pid in allPortals) {
                    if (connected.Contains(pid)) continue;
                    yield return new OpenPortalHit(dc, cs, pid, dc.CellNumber);
                }
            }
        }

        private void PlaceRemainingPrefabCells(DungeonEditingContext ctx, DungeonPrefab prefab,
            Dictionary<int, ushort> cellMap, DungeonCompositeCommand composite) {

            if (ctx.Document == null) return;

            int baseIdx = cellMap.Keys.First();
            var baseCellNum = cellMap[baseIdx];
            var baseDoc = ctx.Document.GetCell(baseCellNum);
            if (baseDoc == null) return;

            var basePC = prefab.Cells[baseIdx];
            var baseOffset = new Vector3(basePC.OffsetX, basePC.OffsetY, basePC.OffsetZ);
            var baseRelRot = Quaternion.Normalize(new Quaternion(basePC.RotX, basePC.RotY, basePC.RotZ, basePC.RotW));

            Quaternion invBaseRelRot = baseRelRot.LengthSquared() > 0.01f ? Quaternion.Inverse(baseRelRot) : Quaternion.Identity;
            var worldBaseRot = Quaternion.Normalize(baseDoc.Orientation * invBaseRelRot);
            var worldBaseOrigin = baseDoc.Origin - Vector3.Transform(baseOffset, worldBaseRot);

            for (int i = 0; i < prefab.Cells.Count; i++) {
                if (cellMap.ContainsKey(i)) continue;

                var pc = prefab.Cells[i];
                var offset = new Vector3(pc.OffsetX, pc.OffsetY, pc.OffsetZ);
                var relRot = Quaternion.Normalize(new Quaternion(pc.RotX, pc.RotY, pc.RotZ, pc.RotW));

                var worldOrigin = worldBaseOrigin + Vector3.Transform(offset, worldBaseRot);
                var worldRot = Quaternion.Normalize(worldBaseRot * relRot);

                var cmd = new AddCellCommand(pc.EnvId, pc.CellStruct,
                    worldOrigin, worldRot, pc.Surfaces.ToList());
                cmd.Execute(ctx.Document);
                composite.Add(cmd);
                cellMap[i] = cmd.CreatedCellNum;
            }

            foreach (var ip in prefab.InternalPortals) {
                if (cellMap.TryGetValue(ip.CellIndexA, out var cellA) && cellMap.TryGetValue(ip.CellIndexB, out var cellB)) {
                    var cmd = new ConnectPortalCommand(cellA, ip.PolyIdA, cellB, ip.PolyIdB);
                    cmd.Execute(ctx.Document);
                    composite.Add(cmd);
                }
            }
        }

        private static OpenPortalHit? FindNearestOpenPortalCell(
            DungeonEditingContext ctx, Vector3 rayOrigin, Vector3 rayDir) {

            if (ctx.Document == null || ctx.Dats == null) return null;

            var hit = ctx.Scene?.EnvCellManager?.Raycast(rayOrigin, rayDir);
            var open = new List<(OpenPortalHit hit, Vector3 centroid)>();

            foreach (var p in EnumerateOpenPortals(ctx)) {
                var geom = PortalSnapper.GetPortalGeometry(p.CellStruct, p.PortalId);
                if (geom == null) continue;
                var (centroid, _) = PortalSnapper.TransformPortalToWorld(geom.Value, p.Cell.Origin, p.Cell.Orientation);
                open.Add((p, centroid));
            }

            if (open.Count == 0) return null;

            const float maxDistSq = 20f * 20f;

            if (hit != null && hit.Value.Hit) {
                var hitPos = hit.Value.HitPosition;
                var hitCellId = hit.Value.Cell.CellId;
                var hitCellNum = (ushort)(hitCellId & 0xFFFF);

                var onHoveredCell = open.Where(p => p.hit.CellNum == hitCellNum).ToList();
                if (onHoveredCell.Count > 0) {
                    var nearest = onHoveredCell.OrderBy(p => (p.centroid - hitPos).LengthSquared()).First();
                    return nearest.hit;
                }

                var nearestAll = open.OrderBy(p => (p.centroid - hitPos).LengthSquared()).First();
                if ((nearestAll.centroid - hitPos).LengthSquared() <= maxDistSq)
                    return nearestAll.hit;
                return null;
            }

            float bestDist = float.MaxValue;
            OpenPortalHit? best = null;
            foreach (var p in open) {
                var toPoint = p.centroid - rayOrigin;
                var proj = Vector3.Dot(toPoint, rayDir);
                if (proj < 0) continue;
                var closest = rayOrigin + rayDir * proj;
                var dist = (p.centroid - closest).LengthSquared();
                if (dist < bestDist) { bestDist = dist; best = p.hit; }
            }
            return bestDist <= maxDistSq ? best : null;
        }
    }
}
