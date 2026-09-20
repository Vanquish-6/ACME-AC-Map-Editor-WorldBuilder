using System;
using System.Globalization;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// Encapsulates static-object mutation operations: nudge, rotate, delete,
    /// apply position/rotation, and object placement. Operates on DungeonEditingContext.
    /// </summary>
    public class ObjectEditingService {
        private readonly DungeonEditingContext _ctx;
        private readonly DungeonSelectionManager _selection;

        private uint? _pendingObjectId;
        private bool _pendingObjectIsSetup;

        private uint? _pendingWeenieClassId;

        public uint? PendingObjectId => _pendingObjectId;
        public bool PendingObjectIsSetup => _pendingObjectIsSetup;
        public bool IsWeeniePlacement => _pendingWeenieClassId.HasValue;

        public ObjectEditingService(DungeonEditingContext ctx, DungeonSelectionManager selection) {
            _ctx = ctx;
            _selection = selection;
        }

        public void NudgeSelectedObject(Vector3 offset) {
            if (!_selection.HasSelectedObject || _ctx.Document == null) return;
            _ctx.CommandHistory.Execute(
                new MoveStaticObjectCommand(_selection.SelectedObjCellNum, _selection.SelectedObjIndex, offset),
                _ctx.Document);
            _ctx.Document.MarkDirty();
        }

        public void RotateSelectedObject(float degrees) {
            if (!_selection.HasSelectedObject || _ctx.Document == null) return;
            _ctx.CommandHistory.Execute(
                new RotateStaticObjectCommand(_selection.SelectedObjCellNum, _selection.SelectedObjIndex, degrees),
                _ctx.Document);
            _ctx.Document.MarkDirty();
        }

        public void DeleteSelectedObject() {
            if (!_selection.HasSelectedObject || _ctx.Document == null) return;
            _ctx.CommandHistory.Execute(
                new DeleteStaticObjectCommand(_selection.SelectedObjCellNum, _selection.SelectedObjIndex),
                _ctx.Document);
            _ctx.Document.MarkDirty();
        }

        public string? ApplyObjPosition(string posX, string posY, string posZ) {
            if (!_selection.HasSelectedObject || _ctx.Document == null) return null;
            if (!float.TryParse(posX, out var x) || !float.TryParse(posY, out var y) || !float.TryParse(posZ, out var z))
                return "Invalid position values";
            var cell = _ctx.Document.GetCell(_selection.SelectedObjCellNum);
            if (cell == null || _selection.SelectedObjIndex >= cell.StaticObjects.Count) return null;
            var delta = new Vector3(x, y, z) - cell.StaticObjects[_selection.SelectedObjIndex].Origin;
            if (delta.LengthSquared() < 0.001f) return null;
            _ctx.CommandHistory.Execute(
                new MoveStaticObjectCommand(_selection.SelectedObjCellNum, _selection.SelectedObjIndex, delta),
                _ctx.Document);
            _ctx.Document.MarkDirty();
            return $"Object moved to ({x:F1}, {y:F1}, {z:F1})";
        }

        public string? ApplyObjRotation(string rotDegrees) {
            if (!_selection.HasSelectedObject || _ctx.Document == null) return null;
            if (!float.TryParse(rotDegrees, out var targetDeg))
                return "Invalid rotation value";
            var cell = _ctx.Document.GetCell(_selection.SelectedObjCellNum);
            if (cell == null || _selection.SelectedObjIndex >= cell.StaticObjects.Count) return null;
            var q = cell.StaticObjects[_selection.SelectedObjIndex].Orientation;
            float currentDeg = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z)) * 180f / MathF.PI;
            float delta = targetDeg - currentDeg;
            if (MathF.Abs(delta) < 0.01f) return null;
            _ctx.CommandHistory.Execute(
                new RotateStaticObjectCommand(_selection.SelectedObjCellNum, _selection.SelectedObjIndex, delta),
                _ctx.Document);
            _ctx.Document.MarkDirty();
            return $"Object rotated to {targetDeg:F1} deg";
        }

        /// <summary>Returns (posX, posY, posZ, rotDeg, infoText) for UI binding.</summary>
        public (string px, string py, string pz, string rot, string info)? GetSelectedObjectFields() {
            if (!_selection.HasSelectedObject || _ctx.Document == null) return null;
            var cell = _ctx.Document.GetCell(_selection.SelectedObjCellNum);
            if (cell == null || _selection.SelectedObjIndex >= cell.StaticObjects.Count) return null;
            var stab = cell.StaticObjects[_selection.SelectedObjIndex];
            var q = stab.Orientation;
            float deg = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z)) * 180f / MathF.PI;
            var info = $"Object 0x{stab.Id:X8}  |  Room {_selection.SelectedObjCellNum:X4}\n" +
                $"Pos: ({stab.Origin.X:F1}, {stab.Origin.Y:F1}, {stab.Origin.Z:F1})";
            return (stab.Origin.X.ToString("F1"), stab.Origin.Y.ToString("F1"), stab.Origin.Z.ToString("F1"),
                    deg.ToString("F1"), info);
        }

        public void SetPendingObject(uint objId, bool isSetup) {
            _pendingObjectId = objId;
            _pendingObjectIsSetup = isSetup;
            _pendingWeenieClassId = null;
        }

        public void SetPendingWeenie(uint weenieClassId, uint setupId) {
            _pendingWeenieClassId = weenieClassId;
            _pendingObjectId = setupId;
            _pendingObjectIsSetup = (setupId & 0x02000000) != 0;
        }

        public uint? ParseObjectId(string input) {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var hex = input.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var objId))
                return objId;
            return null;
        }

        public void ClearPendingObject() {
            _pendingObjectId = null;
            _pendingWeenieClassId = null;
        }

        /// <summary>
        /// Attempt to place the pending object at the raycast hit point.
        /// When in weenie mode, creates a DungeonInstancePlacement; otherwise a DungeonStabData.
        /// Returns a status message on success, or null if nothing happened.
        /// </summary>
        public string? TryPlaceObject(Vector3 rayOrigin, Vector3 rayDir) {
            if (_pendingObjectId == null || _ctx.Document == null || _ctx.Scene == null) return null;

            var hit = _ctx.Scene.EnvCellManager?.Raycast(rayOrigin, rayDir);
            if (hit == null || !hit.Value.Hit) return null;

            var targetCell = hit.Value.Cell;
            var cellNum = (ushort)(targetCell.CellId & 0xFFFF);
            var dc = _ctx.Document.GetCell(cellNum);
            if (dc == null) return null;

            uint lbId = _ctx.Document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);

            var localOrigin = hit.Value.HitPosition - lbOffset;

            if (_pendingWeenieClassId.HasValue) {
                localOrigin.Z -= EnvCellManager.DungeonDepthOffset;
                var cmd = new AddInstancePlacementCommand(
                    _pendingWeenieClassId.Value, cellNum, localOrigin, Quaternion.Identity);
                _ctx.CommandHistory.Execute(cmd, _ctx.Document);
                _ctx.Document.MarkDirty();
                // Cache the setup DID so IntegrateInstancePlacements can render it immediately
                // without waiting for a DB round-trip.
                if (_ctx.Scene != null && _pendingObjectId.HasValue && _pendingObjectId.Value != 0)
                    _ctx.Scene.CacheWeenieSetup(_pendingWeenieClassId.Value, _pendingObjectId.Value);
                if (_ctx.Scene != null) _ctx.Scene.PlacementPreview = null;
                return $"Placed weenie {_pendingWeenieClassId.Value} in room 0x{cellNum:X4}";
            }
            else {
                localOrigin.Z += 50f;
                var cmd = new AddStaticObjectCommand(cellNum, _pendingObjectId.Value, localOrigin, Quaternion.Identity);
                _ctx.CommandHistory.Execute(cmd, _ctx.Document);
                _ctx.Document.MarkDirty();
                if (_ctx.Scene != null) _ctx.Scene.PlacementPreview = null;
                return $"Placed object 0x{_pendingObjectId.Value:X8} in room";
            }
        }
    }
}
