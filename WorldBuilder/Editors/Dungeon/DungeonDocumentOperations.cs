using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Linq;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {
    /// <summary>
    /// Document lifecycle operations for the dungeon editor.
    /// This class is the planned extraction target for operations currently in DungeonEditorViewModel:
    /// - OpenLandblock / LoadDungeon
    /// - NewDungeon / EnsureDocument
    /// - SaveDungeon
    /// - GenerateDungeon
    /// - StartFromTemplate / CopyTemplateToLandblock
    /// - AnalyzeRooms
    /// 
    /// The extraction is deferred to avoid breaking changes to the ViewModel's
    /// relay commands and UI bindings. Callers should begin routing new document
    /// operations through this class.
    /// </summary>
    public class DungeonDocumentOperations {
        private readonly DungeonEditingContext _ctx;
        private readonly DungeonDialogService _dialogs;

        public DungeonDocumentOperations(DungeonEditingContext ctx, DungeonDialogService dialogs) {
            _ctx = ctx;
            _dialogs = dialogs;
        }

        /// <summary>
        /// Scan DAT LandBlockInfo entries to find an EnvCell that uses the given
        /// (environmentId, cellStructureIndex) pair and has a non-empty surface list.
        /// Returns the surface list from the first matching cell, or an empty list if none is found.
        /// This is used to seed default surface IDs when placing new rooms, avoiding the
        /// generic-stone fallback whenever a real surface set is available in the DAT.
        /// </summary>
        public static List<ushort> FindDefaultSurfacesFromDat(
            IDatReaderWriter dats, ushort environmentId, ushort cellStructureIndex) {
            try {
                var lbiIds = dats.Dats.GetAllIdsOfType<LandBlockInfo>().Take(2000).ToArray();
                if (lbiIds.Length == 0)
                    lbiIds = dats.Cell().GetAllIdsOfType<LandBlockInfo>().Take(2000).ToArray();

                foreach (var lbiId in lbiIds) {
                    if (!dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) continue;
                    uint lbId = lbiId >> 16;

                    for (uint i = 0; i < lbi.NumCells && i < 100; i++) {
                        uint cellId = (lbId << 16) | (0x0100 + i);
                        if (!dats.TryGet<EnvCell>(cellId, out var envCell)) continue;

                        if (envCell.EnvironmentId == environmentId &&
                            envCell.CellStructure == cellStructureIndex &&
                            envCell.Surfaces.Count > 0) {
                            return new List<ushort>(envCell.Surfaces.Select(s => (ushort)s));
                        }
                    }
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[DungeonDocumentOperations] Error finding default surfaces: {ex.Message}");
            }
            return new List<ushort>();
        }
    }
}
