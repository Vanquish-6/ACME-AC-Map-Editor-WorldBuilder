using Acme.Dat;
using System.Numerics;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Converts legacy DM BSP branch shapes (BPnn/BPIn/BpIN/BpnN) into retail-safe BPIN nodes
/// with both children present so pack/unpack and traversal stay aligned.
/// </summary>
internal static class LegacyBspRetailNormalizer {
    /// <summary>
    /// Replaces drawing BSP with a single retail BPOL root. Only used when legacy DM trees are
    /// missing or lack portal refs; do not call after a successful <see cref="LegacyBspReader"/> decode.
    /// </summary>
    internal static void RebuildRetailDrawingBsp(CellStruct cellStruct) {
        var drawPolys = cellStruct.Polygons;
        if (drawPolys is not { Count: > 0 }) {
            return;
        }

        var verts = cellStruct.VertexArray?.Vertices ?? new Dictionary<ushort, SWVertex>();
        cellStruct.DrawingBSP = new DrawingBSPTree {
            Root = BspGenerator.BuildLegacyDrawingRootForExport(
                drawPolys,
                verts,
                BspGenerator.BuildPortalRefs(cellStruct, drawPolys)),
        };
    }

    /// <summary>
    /// Counts portal refs present on a drawing BSP tree in memory, regardless of whether the
    /// current node type serializes them in the retail DAT format.
    /// </summary>
    internal static int CountDrawingPortalRefs(DrawingBSPNode? node) {
        if (node == null) {
            return 0;
        }

        int count = node.Portals?.Count ?? 0;
        if (node.PosNode != null) {
            count += CountDrawingPortalRefs(node.PosNode);
        }

        if (node.NegNode != null) {
            count += CountDrawingPortalRefs(node.NegNode);
        }

        return count;
    }

    /// <summary>
    /// Counts only portal refs that survive retail pack/unpack, meaning they live on Portal nodes.
    /// </summary>
    internal static int CountSerializableDrawingPortalRefs(DrawingBSPNode? node) {
        if (node == null) {
            return 0;
        }

        int count = node.Type == BSPNodeType.Portal ? node.Portals?.Count ?? 0 : 0;
        if (node.PosNode != null) {
            count += CountSerializableDrawingPortalRefs(node.PosNode);
        }

        if (node.NegNode != null) {
            count += CountSerializableDrawingPortalRefs(node.NegNode);
        }

        return count;
    }

    /// <summary>
    /// True when the drawing BSP already carries serializable portal linkage for every listed portal poly.
    /// </summary>
    internal static bool HasAdequateDrawingPortalCoverage(CellStruct cellStruct) {
        if (cellStruct.Portals is not { Count: > 0 }) {
            return true;
        }

        if (cellStruct.DrawingBSP?.Root == null) {
            return false;
        }

        return CountSerializableDrawingPortalRefs(cellStruct.DrawingBSP.Root) >= cellStruct.Portals.Count;
    }

    /// <summary>
    /// Ensures retail drawing BSP portal refs exist after pack/unpack or CellStruct.Portal merges.
    /// Preserves full legacy trees when they already contain portal refs.
    /// </summary>
    internal static bool EnsureDrawingBspPortalRefs(CellStruct cellStruct) {
        if (cellStruct.Portals is not { Count: > 0 }) {
            return false;
        }

        if (cellStruct.Polygons is not { Count: > 0 } drawPolys) {
            return false;
        }

        if (HasAdequateDrawingPortalCoverage(cellStruct)) {
            return false;
        }

        var root = cellStruct.DrawingBSP?.Root;
        if (root == null) {
            RebuildRetailDrawingBsp(cellStruct);
            return true;
        }

        if (CountSerializableDrawingPortalRefs(root) < cellStruct.Portals.Count) {
            RebuildRetailDrawingBsp(cellStruct);
            return true;
        }

        return false;
    }

    internal static void EnsureEnvironmentPackable(Acme.Dat.Environment environment) {
        if (TryVerifyEnvironmentPackable(environment)) {
            return;
        }

        foreach (CellStruct cellStruct in environment.Cells.Values) {
            EnsureDrawingBspPortalRefs(cellStruct);
        }

        if (TryVerifyEnvironmentPackable(environment)) {
            return;
        }

        foreach (CellStruct cellStruct in environment.Cells.Values) {
            BspGenerator.BuildLegacyRetailSafe(cellStruct);
        }

        foreach (CellStruct cellStruct in environment.Cells.Values) {
            NormalizeForRetail(cellStruct);
        }
    }

    internal static void EnsureGfxObjPackable(GfxObj gfxObj) {
        LegacyBspReader.FillMissingRetailBsp(gfxObj);
        NormalizeForRetail(gfxObj);
        if (TryVerifyGfxObjPackable(gfxObj)) {
            return;
        }

        BspGenerator.BuildLegacyRetailSafe(gfxObj);
        NormalizeForRetail(gfxObj);
    }

    internal static bool TryVerifyEnvironmentPackable(Acme.Dat.Environment environment) {
        try {
            var bytes = DatNativeRecords.Pack(environment);
            var copy = DatNativeRecords.Unpack<Acme.Dat.Environment>(bytes);
            return RoundTripPreservesDrawingPortalCoverage(environment, copy);
        }
        catch {
            return false;
        }
    }

    internal static bool TryVerifyGfxObjPackable(GfxObj gfxObj) {
        try {
            _ = DatNativeRecords.Pack(gfxObj);
            return true;
        }
        catch {
            return false;
        }
    }

    internal static void NormalizeForRetail(CellStruct cellStruct) {
        if (cellStruct.CellBSP?.Root != null) {
            cellStruct.CellBSP.Root = NormalizeCell(cellStruct.CellBSP.Root);
        }

        if (cellStruct.PhysicsBSP?.Root != null) {
            cellStruct.PhysicsBSP.Root = NormalizePhysics(cellStruct.PhysicsBSP.Root);
        }

        if (cellStruct.DrawingBSP?.Root != null) {
            cellStruct.DrawingBSP.Root = NormalizeDrawing(cellStruct.DrawingBSP.Root);
        }
    }

    internal static void NormalizeForRetail(GfxObj gfxObj) {
        if (gfxObj.PhysicsBSP?.Root != null) {
            gfxObj.PhysicsBSP.Root = NormalizePhysics(gfxObj.PhysicsBSP.Root);
        }

        if (gfxObj.DrawingBSP?.Root != null) {
            gfxObj.DrawingBSP.Root = NormalizeDrawing(gfxObj.DrawingBSP.Root);
        }
    }

    private static CellBSPNode NormalizeCell(CellBSPNode node) {
        if (node.Type == BSPNodeType.Leaf) {
            return node;
        }

        if (node.Type == BSPNodeType.Portal) {
            node.PosNode = node.PosNode != null ? NormalizeCell(node.PosNode) : EmptyCellLeaf();
            node.NegNode = node.NegNode != null ? NormalizeCell(node.NegNode) : EmptyCellLeaf();
            return node;
        }

        var posNode = node.PosNode;
        var negNode = node.NegNode;
        ApplySingleChildBranches(node.Type, ref posNode, ref negNode, NormalizeCell);
        node.Type = BSPNodeType.BPIN;
        node.PosNode = posNode ?? EmptyCellLeaf();
        node.NegNode = negNode ?? EmptyCellLeaf();
        return node;
    }

    private static PhysicsBSPNode NormalizePhysics(PhysicsBSPNode node) {
        if (node.Type == BSPNodeType.Leaf) {
            node.Polygons ??= new List<ushort>();
            node.BoundingSphere ??= EmptySphere();
            return node;
        }

        if (node.Type == BSPNodeType.Portal) {
            node.PosNode = node.PosNode != null ? NormalizePhysics(node.PosNode) : EmptyPhysicsLeaf();
            node.NegNode = node.NegNode != null ? NormalizePhysics(node.NegNode) : EmptyPhysicsLeaf();
            return node;
        }

        node.Polygons ??= new List<ushort>();
        node.BoundingSphere ??= EmptySphere();
        var posNode = node.PosNode;
        var negNode = node.NegNode;
        ApplySingleChildBranches(node.Type, ref posNode, ref negNode, NormalizePhysics);
        node.Type = BSPNodeType.BPIN;
        node.PosNode = posNode ?? EmptyPhysicsLeaf();
        node.NegNode = negNode ?? EmptyPhysicsLeaf();
        return node;
    }

    private static DrawingBSPNode NormalizeDrawing(DrawingBSPNode node) {
        if (node.Type == BSPNodeType.Leaf) {
            node.Polygons ??= new List<ushort>();
            node.Portals ??= new List<PortalRef>();
            return node;
        }

        node.Polygons ??= new List<ushort>();
        node.Portals ??= new List<PortalRef>();
        node.BoundingSphere ??= EmptySphere();

        if (node.Type == BSPNodeType.Portal || node.Type == BSPNodeType.BPOL) {
            if (node.Type == BSPNodeType.Portal) {
                node.PosNode = node.PosNode != null ? NormalizeDrawing(node.PosNode) : EmptyDrawingLeaf();
                node.NegNode = node.NegNode != null ? NormalizeDrawing(node.NegNode) : EmptyDrawingLeaf();
            }

            return node;
        }

        var posNode = node.PosNode;
        var negNode = node.NegNode;
        ApplySingleChildBranches(node.Type, ref posNode, ref negNode, NormalizeDrawing);
        node.Type = BSPNodeType.BPIN;
        node.PosNode = posNode ?? EmptyDrawingLeaf();
        node.NegNode = negNode ?? EmptyDrawingLeaf();
        return node;
    }

    private static bool RoundTripPreservesDrawingPortalCoverage(
        Acme.Dat.Environment source,
        Acme.Dat.Environment roundTripped) {
        foreach (var (structId, sourceCell) in source.Cells) {
            if (sourceCell.Portals is not { Count: > 0 }) {
                continue;
            }

            if (!roundTripped.Cells.TryGetValue(structId, out var roundTrippedCell)) {
                return false;
            }

            if (!HasAdequateDrawingPortalCoverage(roundTrippedCell)) {
                return false;
            }
        }

        return true;
    }

    private static void ApplySingleChildBranches<T>(
        BSPNodeType type,
        ref T? posNode,
        ref T? negNode,
        Func<T, T> normalize) where T : class {
        switch (type) {
            case BSPNodeType.BPnn:
            case BSPNodeType.BPIn:
                if (posNode != null) {
                    posNode = normalize(posNode);
                }

                break;
            case BSPNodeType.BpIN:
            case BSPNodeType.BPnN:
            case BSPNodeType.BpnN:
                if (negNode != null) {
                    negNode = normalize(negNode);
                }

                break;
            default:
                if (posNode != null) {
                    posNode = normalize(posNode);
                }

                if (negNode != null) {
                    negNode = normalize(negNode);
                }

                break;
        }
    }

    private static CellBSPNode EmptyCellLeaf() => new() {
        Type = BSPNodeType.Leaf,
        LeafIndex = 0,
    };

    private static PhysicsBSPNode EmptyPhysicsLeaf() {
        var leaf = new PhysicsBSPNode {
            Type = BSPNodeType.Leaf,
            LeafIndex = 0,
            BoundingSphere = EmptySphere(),
            Polygons = new List<ushort>(),
        };
        BspGenerator.SetPhysicsSolid(leaf, 0);
        return leaf;
    }

    private static DrawingBSPNode EmptyDrawingLeaf() => new() {
        Type = BSPNodeType.Leaf,
        LeafIndex = 0,
        Polygons = new List<ushort>(),
        Portals = new List<PortalRef>(),
    };

    private static Sphere EmptySphere() => new() {
        Origin = Vector3.Zero,
        Radius = 0.01f,
    };
}
