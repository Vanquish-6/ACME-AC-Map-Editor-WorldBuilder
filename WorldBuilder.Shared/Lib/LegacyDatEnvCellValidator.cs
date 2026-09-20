using Acme.Dat;
using System.Collections.Generic;
using System.Linq;

namespace WorldBuilder.Shared.Lib;

public static class LegacyDatEnvCellValidator {
    public static void ValidateEnvironments(DefaultDatReaderWriter writer, IReadOnlyList<EnvCell> envCells, IList<string> warnings) {
        // OtherPortalId is an index into the TARGET cell's CellPortals array, so target cells must be
        // resolved by full id (landblock | OtherCellId) to bounds-check correctly.
        var cellsByFullId = new Dictionary<uint, EnvCell>();
        foreach (var cell in envCells) {
            cellsByFullId.TryAdd(cell.Id, cell);
        }

        foreach (var group in envCells.GroupBy(cell => 0x0D000000u | cell.EnvironmentId)) {
            uint envId = group.Key;
            
            if (!writer.TryGet(envId, out Acme.Dat.Environment environment) || environment == null) {
                warnings.Add($"EnvCell: environment 0x{envId:X8} is missing but referenced by {group.Count()} cell(s).");
                continue;
            }

            foreach (var envCell in group) {
                var cellId = envCell.Id;
                if (!environment.Cells.TryGetValue(envCell.CellStructure, out var cellStruct)) {
                    warnings.Add($"EnvCell: environment 0x{envId:X8} missing struct {envCell.CellStructure} for cell 0x{cellId:X8}.");
                    continue;
                }

                if (cellStruct.Portals == null || cellStruct.Portals.Count == 0) {
                    if (envCell.CellPortals != null && envCell.CellPortals.Count > 0) {
                        warnings.Add($"EnvCell: environment 0x{envId:X8} struct {envCell.CellStructure} has empty CellStruct.Portals but cell 0x{cellId:X8} has {envCell.CellPortals.Count} portals.");
                    }
                    continue;
                }

                if (envCell.CellPortals == null) {
                    warnings.Add($"EnvCell: environment 0x{envId:X8} struct {envCell.CellStructure} has null CellPortals but CellStruct has {cellStruct.Portals.Count} portals.");
                    continue;
                }

                if (cellStruct.Portals.Count != envCell.CellPortals.Count) {
                    warnings.Add($"EnvCell: environment 0x{envId:X8} struct {envCell.CellStructure} portal count mismatch. CellStruct: {cellStruct.Portals.Count}, Cell 0x{cellId:X8}: {envCell.CellPortals.Count}.");
                }

                for (int i = 0; i < envCell.CellPortals.Count; i++) {
                    var portal = envCell.CellPortals[i];

                    // OtherPortalId indexes into the TARGET cell's portal array (the cell named by
                    // OtherCellId), not this cell's. Outdoor portals (OtherCellId 0xFFFF) store a
                    // landcell id here, not an index, so they are excluded.
                    if (portal.OtherCellId is not 0 and not 0xFFFF) {
                        uint targetCellId = (cellId & 0xFFFF0000u) | (uint)portal.OtherCellId;
                        if (cellsByFullId.TryGetValue(targetCellId, out var targetCell)
                            && targetCell.CellPortals is { Count: > 0 }
                            && portal.OtherPortalId >= targetCell.CellPortals.Count) {
                            warnings.Add($"EnvCell: environment 0x{envId:X8} cell 0x{cellId:X8} portal {i} OtherPortalId {portal.OtherPortalId} is out of bounds (target 0x{targetCellId:X8} has {targetCell.CellPortals.Count} portal(s)).");
                        }
                    }

                    if (portal.Flags.HasFlag(PortalFlags.ExactMatch) && portal.OtherCellId == 0) {
                        warnings.Add($"EnvCell: environment 0x{envId:X8} cell 0x{cellId:X8} portal {i} has ExactMatch flag but OtherCellId is 0.");
                    }
                }
            }
        }
    }
}
