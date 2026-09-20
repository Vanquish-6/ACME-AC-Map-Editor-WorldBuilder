using System.Collections.Generic;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Shared.Lib.AceDb {
    /// <summary>
    /// All the data the reposition service needs from the export pipeline.
    /// </summary>
    public class RepositionContext {
        /// <summary>
        /// Landblock IDs whose terrain was modified during this export.
        /// </summary>
        public required IReadOnlyCollection<ushort> ModifiedLandblocks { get; init; }

        /// <summary>
        /// Old (pre-edit) terrain entries per landblock, keyed by landblock ID.
        /// Each array has 81 entries (9x9 vertex grid).
        /// </summary>
        public required Dictionary<ushort, TerrainEntry[]> OldTerrain { get; init; }

        /// <summary>
        /// New (post-edit, composited) terrain entries per landblock.
        /// </summary>
        public required Dictionary<ushort, TerrainEntry[]> NewTerrain { get; init; }

        /// <summary>
        /// The LandHeightTable from Region.LandDefs, used to convert height indices to world Z.
        /// </summary>
        public required float[] LandHeightTable { get; init; }

        /// <summary>
        /// Directory where the SQL file will be written.
        /// </summary>
        public required string ExportDirectory { get; init; }
    }
}
