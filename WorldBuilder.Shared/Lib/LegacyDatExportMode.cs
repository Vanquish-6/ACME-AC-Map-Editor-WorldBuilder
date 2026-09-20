namespace WorldBuilder.Shared.Lib {
    /// <summary>
    /// Controls how legacy pre-ToD (Dark Majesty) DATs are merged into retail-format output.
    /// </summary>
    public enum LegacyDatExportMode {
        /// <summary>
        /// Full retail seed plus every converted legacy object. Keeps retail Dereth world cells and the
        /// full retail portal catalog, with legacy terrain/dungeons/portals overlaid on top.
        /// Largest output; useful when you want retail + DM content together.
        /// </summary>
        FullMerge = 0,

        /// <summary>
        /// Legacy DM world only in cell.dat, plus the minimum retail client shell needed to run:
        /// local/highres UI files, world-referenced converted portal assets, and required retail globals
        /// (spell tables, CharGen, clothing tables, etc.). Smaller than full merge.
        /// </summary>
        SlimMerge = 1,

        /// <summary>
        /// Pure legacy port into retail format: empty cell/portal shells, all legacy cell and portal
        /// catalog objects converted 1:1, no retail world or retail portal catalog mixed in.
        /// Intended for archival/modding and smallest DM-faithful output.
        /// </summary>
        FullDm = 2,
    }
}
