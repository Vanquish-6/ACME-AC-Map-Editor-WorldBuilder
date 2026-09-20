using Avalonia.Input;
using System;
using System.Collections.ObjectModel;
using System.Numerics;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Dungeon.Tools {

    public class PortalConnectTool : DungeonToolBase {
        public override string Name => "Connect";
        public override string IconGlyph => "\u2194";
        public override string Description => "Click two open doorways to link them. Pieces stay on the left if you need to add rooms next.";

        private static readonly ObservableCollection<DungeonSubToolBase> _empty = new();
        public override ObservableCollection<DungeonSubToolBase> AllSubTools => _empty;

        private ushort _sourceCellNum;
        private ushort _sourcePolyId;
        private bool _hasSource;

        public bool HasPendingConnection => _hasSource;

        public override void OnActivated() {
            _hasSource = false;
            StatusText = "Click an open doorway to start connecting";
        }

        public override void OnDeactivated() {
            _hasSource = false;
            StatusText = "";
            ClearHighlight(null);
        }

        private static void ClearHighlight(DungeonEditingContext? ctx) {
            if (ctx?.Scene != null) {
                ctx.Scene.HighlightedPortalCellNum = 0;
                ctx.Scene.HighlightedPortalPolyId = 0;
                ctx.Scene.HoveredPortalCellNum = 0;
                ctx.Scene.HoveredPortalPolyId = 0;
            }
        }

        public override bool HandleMouseDown(MouseState mouseState, DungeonEditingContext ctx) {
            if (!mouseState.LeftPressed || mouseState.RightPressed) return false;
            if (ctx.Scene == null || ctx.Document == null) return false;

            var portal = DungeonPortalPicker.Pick(ctx, mouseState);
            if (portal == null) {
                StatusText = _hasSource
                    ? "No open doorway under cursor — click an open doorway, or Escape to cancel"
                    : "Click an open doorway to start connecting";
                return false;
            }

            var (cellNum, polyId) = (portal.Value.CellNum, portal.Value.PolyId);

            if (!_hasSource) {
                _sourceCellNum = cellNum;
                _sourcePolyId = polyId;
                _hasSource = true;
                ctx.Scene.HighlightedPortalCellNum = cellNum;
                ctx.Scene.HighlightedPortalPolyId = polyId;
                var sourceName = GetPortalRoomName(ctx, cellNum);
                StatusText = $"Source: {sourceName} — now click the target doorway";
                return true;
            }

            // Second click — validate and connect
            if (cellNum == _sourceCellNum && polyId == _sourcePolyId) {
                StatusText = "Can't connect a doorway to itself — click a different doorway";
                return true;
            }

            // Optional compatibility check
            if (ctx.GeometryCache != null) {
                var srcCell = ctx.Document.GetCell(_sourceCellNum);
                var tgtCell = ctx.Document.GetCell(cellNum);
                if (srcCell != null && tgtCell != null &&
                    !ctx.GeometryCache.AreCompatible(
                        srcCell.EnvironmentId, srcCell.CellStructure, _sourcePolyId,
                        tgtCell.EnvironmentId, tgtCell.CellStructure, polyId)) {
                    StatusText = "Portals are not geometrically compatible — try a different pair";
                    return true;
                }
            }

            var cmd = new ConnectPortalCommand(_sourceCellNum, _sourcePolyId, cellNum, polyId);
            ctx.CommandHistory.Execute(cmd, ctx.Document);
            ctx.RefreshRendering();

            var srcName = GetPortalRoomName(ctx, _sourceCellNum);
            var tgtName = GetPortalRoomName(ctx, cellNum);
            StatusText = $"Connected {srcName} \u2194 {tgtName} — click another doorway or Escape";
            _hasSource = false;
            ClearHighlight(ctx);
            return true;
        }

        public override bool HandleMouseUp(MouseState mouseState, DungeonEditingContext ctx) => false;

        public override bool HandleMouseMove(MouseState mouseState, DungeonEditingContext ctx) {
            if (ctx.Scene == null) return false;

            var portal = DungeonPortalPicker.Pick(ctx, mouseState);
            if (portal != null) {
                var (cellNum, polyId) = (portal.Value.CellNum, portal.Value.PolyId);
                ctx.Scene.HoveredPortalCellNum = cellNum;
                ctx.Scene.HoveredPortalPolyId = polyId;

                var roomName = GetPortalRoomName(ctx, cellNum);
                if (_hasSource) {
                    var srcName = GetPortalRoomName(ctx, _sourceCellNum);
                    ctx.SetStatus($"From: {srcName} \u2192 To: {roomName}  (click to connect)");
                }
                else {
                    ctx.SetStatus($"Doorway on: {roomName}  (click to select as source)");
                }
            }
            else {
                ctx.Scene.HoveredPortalCellNum = 0;
                ctx.Scene.HoveredPortalPolyId = 0;
                if (_hasSource) {
                    var srcName = GetPortalRoomName(ctx, _sourceCellNum);
                    ctx.SetStatus($"Source: {srcName} \u2014 hover over another doorway to connect");
                }
            }
            return false;
        }

        private static string GetPortalRoomName(DungeonEditingContext ctx, ushort cellNum) {
            var dc = ctx.Document?.GetCell(cellNum);
            if (dc == null) return $"Cell 0x{cellNum:X4}";
            uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
            var name = ctx.RoomPalette?.GetRoomDisplayName(envFileId, dc.CellStructure);
            return !string.IsNullOrEmpty(name) ? name : $"Cell 0x{cellNum:X4}";
        }

        public override bool HandleKeyDown(KeyEventArgs e, DungeonEditingContext ctx) {
            if (e.Key == Key.Escape) {
                if (_hasSource) {
                    _hasSource = false;
                    StatusText = "Cancelled — click an open doorway to start connecting";
                    return true;
                }
            }
            return false;
        }
    }
}
