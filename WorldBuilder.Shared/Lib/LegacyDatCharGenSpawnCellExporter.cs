using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Ensures DM CharGen starting locations have outdoor landblock/LBI (and EnvCell when present)
/// in client_cell_1.dat. Full DM does not use world-closure portal filtering, but outdoor
/// spawns only need LBI + LandBlock catalog entries, not an EnvCell at the spawn cell id.
/// </summary>
public static class LegacyDatCharGenSpawnCellExporter {
    public static int EnsureSpawnCells(
        LegacyDatReader legacySource,
        DefaultDatReaderWriter outputWriter,
        Action<string>? onProgress = null) {
        ArgumentNullException.ThrowIfNull(legacySource);
        ArgumentNullException.ThrowIfNull(outputWriter);

        if (!legacySource.TryGet<CharGen>(LegacyDatCharGenMapper.CharGenId, out CharGen? legacyCharGen)
            || legacyCharGen == null) {
            return 0;
        }

        var cell = outputWriter.Cell();
        int iteration = cell.Iteration.CurrentIteration;
        int written = 0;

        onProgress?.Invoke("Ensuring CharGen spawn landblocks are present in client_cell_1.dat...");
        foreach (var area in legacyCharGen.StartingAreas) {
            foreach (var location in area.Locations) {
                if (!LegacyDatCharGenMapper.IsPlausibleSpawnCellId(location.CellId)) {
                    continue;
                }

                written += TryCopyCellObject<LandBlock>(
                    legacySource,
                    outputWriter,
                    cell,
                    LegacyDatCharGenMapper.LandBlockFileIdForCell(location.CellId),
                    iteration);
                written += TryCopyCellObject<LandBlockInfo>(
                    legacySource,
                    outputWriter,
                    cell,
                    LegacyDatCharGenMapper.LandBlockInfoFileIdForCell(location.CellId),
                    iteration);

                if (!cell.Catalog().Tree.HasFile(location.CellId)
                    && legacySource.TryGet<EnvCell>(location.CellId, out EnvCell? envCell)
                    && envCell != null) {
                    envCell.Id = location.CellId;
                    if (outputWriter.TrySave(envCell, iteration)) {
                        written++;
                    }
                }
            }
        }

        if (written > 0) {
            onProgress?.Invoke($"Wrote {written:N0} CharGen spawn cell object(s) missing from export.");
        }

        return written;
    }

    static int TryCopyCellObject<T>(
        LegacyDatReader legacySource,
        DefaultDatReaderWriter outputWriter,
        IDatReaderWriter cell,
        uint id,
        int iteration) where T : class, IDatRecord, new() {
        if (cell.Catalog().Tree.HasFile(id)) {
            return 0;
        }

        if (!legacySource.TryGet<T>(id, out T? obj) || obj == null) {
            return 0;
        }

        if (obj is LandBlock landBlock) {
            landBlock.Id = id;
        }
        else if (obj is LandBlockInfo landBlockInfo) {
            landBlockInfo.Id = id;
        }

        return outputWriter.TrySave(obj, iteration) ? 1 : 0;
    }
}
