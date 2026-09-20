using Acme.Dat;
using MemoryPack;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Shared.Documents {

    [MemoryPackable]
    public partial class DungeonData {
        public ushort LandblockKey;
        public List<DungeonCellData> Cells = new();
        /// <summary>Generators/items/portals to push to landblock_instance on export (when the world database is configured).</summary>
        public List<DungeonInstancePlacement> InstancePlacements = new();
    }

    [MemoryPackable]
    public partial class DungeonCellData {
        public ushort CellNumber;
        public ushort EnvironmentId;
        public ushort CellStructure;
        public Vector3 Origin;
        public Quaternion Orientation = Quaternion.Identity;
        public uint Flags;
        public uint RestrictionObj;
        public List<ushort> Surfaces = new();
        public List<DungeonCellPortalData> CellPortals = new();
        public List<ushort> VisibleCells = new();
        public List<DungeonStabData> StaticObjects = new();
    }

    [MemoryPackable]
    public partial class DungeonCellPortalData {
        public ushort OtherCellId;
        public ushort PolygonId;
        public ushort OtherPortalId;
        public ushort Flags;
    }

    [MemoryPackable]
    public partial class DungeonStabData {
        public uint Id;
        public Vector3 Origin;
        public Quaternion Orientation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;
    }

    /// <summary>
    /// A generator, item, or portal placement for a dungeon cell to be written to the
    /// landblock_instance table on export. Lets server operators place spawns in dungeons
    /// and sync them via the same DB connection used for reposition.
    /// </summary>
    [MemoryPackable]
    public partial class DungeonInstancePlacement {
        public uint WeenieClassId { get; set; }
        public ushort CellNumber { get; set; }
        public Vector3 Origin { get; set; }
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
    }

    public partial class DungeonDocument : BaseDocument {
        public override string Type => nameof(DungeonDocument);

        [MemoryPackInclude]
        private DungeonData _data = new();

        public ushort LandblockKey {
            get => _data.LandblockKey;
            set => _data.LandblockKey = value;
        }

        public List<DungeonCellData> Cells => _data.Cells;

        /// <summary>
        /// Generators, items, or portals to push to the landblock_instance table on export
        /// when AceDb is configured. Enables server operators to place spawns in dungeons.
        /// </summary>
        public List<DungeonInstancePlacement> InstancePlacements => _data.InstancePlacements;

        private ushort _nextCellNumber = 0x0100;
        private readonly Queue<ushort> _recycledCellNumbers = new();

        public DungeonDocument(ILogger logger) : base(logger) {
        }

        public void SetLandblockKey(ushort key) {
            _data.LandblockKey = key;
            Id = $"dungeon_{key:X4}";
        }

        protected override async Task<bool> InitInternal(IDatReaderWriter datreader, DocumentManager documentManager) {
            // Parse landblock key from document Id (format: "dungeon_XXXX")
            if (LandblockKey == 0 && Id.StartsWith("dungeon_")) {
                var hex = Id.Replace("dungeon_", "");
                if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedKey)) {
                    _data.LandblockKey = parsedKey;
                }
            }

            if (Cells.Count == 0 && LandblockKey != 0) {
                LoadCellsFromDat(datreader);
            }
            if (Cells.Count > 0) {
                _nextCellNumber = (ushort)(Cells.Max(c => c.CellNumber) + 1);
            }
            ClearDirty();
            return true;
        }

        protected override byte[] SaveToProjectionInternal() {
            return MemoryPackSerializer.Serialize(_data);
        }

        protected override bool LoadFromProjectionInternal(byte[] projection) {
            try {
                _data = MemoryPackSerializer.Deserialize<DungeonData>(projection) ?? new();
            }
            catch (MemoryPack.MemoryPackSerializationException) {
                _logger.LogWarning("[DungeonDoc] Project cache has incompatible format (schema changed), will reload from DAT");
                _data = new();
            }
            if (Cells.Count > 0) {
                _nextCellNumber = (ushort)(Cells.Max(c => c.CellNumber) + 1);
            }
            _recycledCellNumbers.Clear();
            return true;
        }

        protected override Task<bool> SaveToDatsInternal(IDatReaderWriter datwriter, int iteration = 0) {
            if (Cells.Count == 0) {
                _logger.LogWarning("[DungeonDoc] Nothing to export: dungeon has no cells");
                return Task.FromResult(true);
            }

            // ── Pre-export hard validation ─────────────────────────────────────────────
            // Run full validation and abort on any ERROR-level issue before touching the DAT.
            // Errors here mean the export would write data that crashes or corrupts the client.
            var preCheckErrors = ValidateComprehensive()
                .Where(r => r.Severity == ValidationSeverity.Error)
                .ToList();
            if (preCheckErrors.Count > 0) {
                foreach (var err in preCheckErrors)
                    _logger.LogError("[DungeonDoc] Validation error: {Msg}", err.Message);
                _logger.LogError(
                    "[DungeonDoc] Export aborted: {Count} error(s) must be fixed before exporting. " +
                    "Use Dungeon Editor → Validate to see all issues.",
                    preCheckErrors.Count);
                return Task.FromResult(false);
            }

            uint lbId = LandblockKey;
            uint lbEntryId = (lbId << 16) | 0xFFFF;
            uint lbiId = (lbId << 16) | 0xFFFE;

            // Step 1: Ensure a LandBlock (0xFFFF) terrain entry exists.
            // Existing outdoor landblocks already have one; empty ones need a stub.
            bool hasLandBlock = datwriter.TryGet<LandBlock>(lbEntryId, out _);
            if (!hasLandBlock) {
                var lb = new LandBlock { Id = lbEntryId };
                if (!datwriter.TrySave(lb, iteration)) {
                    _logger.LogError("[DungeonDoc] Failed to create LandBlock 0x{Id:X8}", lbEntryId);
                    return Task.FromResult(false);
                }
                _logger.LogInformation("[DungeonDoc] Created new LandBlock 0x{Id:X8} (dungeon-only landblock, no prior terrain)", lbEntryId);
            }

            // Step 2: Get or create LandBlockInfo (0xFFFE).
            // If it exists (landblock has buildings/objects), preserve them — only update NumCells.
            bool isNewLbi = !datwriter.TryGet<LandBlockInfo>(lbiId, out var lbi);
            if (isNewLbi) {
                lbi = new LandBlockInfo { Id = lbiId };
            }
            uint prevNumCells = lbi.NumCells;
            int existingBuildings = lbi.Buildings?.Count ?? 0;
            int existingObjects = lbi.Objects?.Count ?? 0;

            // Step 2.5: Auto-compute VisibleCells (stab lists) if any are empty
            bool anyEmpty = Cells.Any(c => c.VisibleCells.Count == 0);
            if (anyEmpty) {
                int updated = ComputeVisibleCells();
                _logger.LogInformation("[DungeonDoc] Auto-computed VisibleCells for {Updated}/{Total} cells before export", updated, Cells.Count);
            }

            // Step 2.6: Ensure portal flags (PortalSide) are correct before export.
            // The AC client uses portal_side to determine which half-space of the
            // portal polygon plane is the cell interior. Incorrect values cause
            // broken rendering and traversal.
            int flagsFixed = RecomputePortalFlags(datwriter);
            if (flagsFixed > 0)
                _logger.LogInformation("[DungeonDoc] Fixed {Count} portal flag(s) before export", flagsFixed);

            // Step 3: Save all EnvCells
            var envCells = ToEnvCells(forDatExport: true);
            SanitizePortalTopologyForExport(datwriter, envCells);
            SanitizeEnvCellSurfacesForExport(datwriter, envCells);
            SanitizeEnvCellStaticsForExport(datwriter, envCells);
            ReorderCellPortalsToMatchCellStruct(datwriter, envCells);
            RewriteOtherPortalIndices(envCells);
            PopulateVisibilityStabs(envCells);
            int saved = 0;
            foreach (var envCell in envCells) {
                if (!datwriter.TrySave(envCell, iteration)) {
                    _logger.LogError("[DungeonDoc] Failed to save EnvCell 0x{CellId:X8}", envCell.Id);
                    return Task.FromResult(false);
                }
                saved++;
            }

            // Step 4: Update NumCells and add dungeon BuildingInfo to the LBI.
            // Original dungeons have buildings=0 in their LBI. Cell visibility is handled
            // through the EnvCell VisibleCells list (light_list.data in the client),
            // not through BuildingInfo/BuildingPortal stab lists.
            var maxCellNum = Cells.Max(c => c.CellNumber);
            if (maxCellNum < 0x0100) {
                // Belt-and-suspenders guard — pre-export validation should have caught this.
                _logger.LogError(
                    "[DungeonDoc] maxCellNum 0x{Max:X4} is below 0x0100; NumCells would underflow. Aborting.",
                    maxCellNum);
                return Task.FromResult(false);
            }
            lbi.NumCells = (uint)(maxCellNum - 0x00FF);

            if (!datwriter.TrySave(lbi, iteration)) {
                _logger.LogError("[DungeonDoc] Failed to save LandBlockInfo 0x{InfoId:X8}", lbiId);
                return Task.FromResult(false);
            }

            _logger.LogInformation(
                "[DungeonDoc] Exported LB {LB:X4}: {Saved}/{Total} cells saved, " +
                "LBI 0x{LbiId:X8} (new={IsNew}, NumCells: {Prev}->{New}, buildings={Bldg}, objects={Obj}), " +
                "LandBlock 0x{LbId:X8} (existed={HasLB})",
                LandblockKey, saved, envCells.Count,
                lbiId, isNewLbi, prevNumCells, lbi.NumCells, existingBuildings, existingObjects,
                lbEntryId, hasLandBlock);

            // Diagnostic: compare with a known original dungeon's LBI and cell
            uint refLbiId = 0x01D9FFFE;
            if (datwriter.TryGet<LandBlockInfo>(refLbiId, out var refLbi)) {
                string bldgInfo = "";
                if (refLbi.Buildings?.Count > 0) {
                    var b = refLbi.Buildings[0];
                    string portalInfo = "";
                    if (b.Portals?.Count > 0) {
                        var p = b.Portals[0];
                        var refStabs = p.StabList.Take(5).Select(s => $"0x{s:X4}");
                        portalInfo = $" portal0=[other=0x{p.OtherCellId:X4},otherP={p.OtherPortalId},flags={p.Flags},stabs={p.StabList.Count} ids=[{string.Join(",", refStabs)}]]";
                    }
                    bldgInfo = $" bldg0=[model=0x{b.ModelId:X8},leaves={b.NumLeaves},portals={b.Portals?.Count ?? 0}{portalInfo}]";
                }
                _logger.LogInformation(
                    "[DungeonDoc] REFERENCE LBI 0x{Id:X8}: numCells={NC}, buildings={BC}, objects={OC}{BldgInfo}",
                    refLbiId, refLbi.NumCells, refLbi.Buildings?.Count ?? 0, refLbi.Objects?.Count ?? 0, bldgInfo);
            }

            // Binary format diagnostic: pack a reference cell and compare sizes
            // to detect any field width mismatches between DatReaderWriter and the client
            uint refCellId = 0x01D90100;
            if (datwriter.TryGet<EnvCell>(refCellId, out var refCell)) {
                int rawSize = -1;

                var packed = DatNativeRecords.Pack(refCell);
                int drwSize = packed.Length;

                // Calculate expected client size manually:
                // Header: 4(id)
                // Flags: 4, CellId: 4, numSurf: 1, numPort: 1, numVis: 2
                // Surfaces: 2*n, EnvId: 2, CellStruct: ?, Frame: 28
                // Portals: 8*n, VisCells: 2*n
                // Stabs (if flag 0x2): 4 + 32*n
                // RestrictionObj (if flag 0x8): 4
                int nSurf = refCell.Surfaces.Count;
                int nPort = refCell.CellPortals.Count;
                int nVis = refCell.VisibleCells.Count;
                int nStab = refCell.StaticObjects.Count;
                bool hasStabs = ((uint)refCell.Flags & 2) != 0;
                bool hasRestrict = ((uint)refCell.Flags & 8) != 0;

                int expectedWith2 = 4 + 4 + 4 + 1 + 1 + 2 + (2 * nSurf) + 2 + 2 + 28 +
                    (8 * nPort) + (2 * nVis) +
                    (hasStabs ? 4 + (32 * nStab) : 0) +
                    (hasRestrict ? 4 : 0);
                int expectedWith1 = expectedWith2 - 1;

                _logger.LogInformation(
                    "[DungeonDoc] BINARY FORMAT TEST cell 0x{Id:X8}: rawDatSize={Raw}, drwPackSize={DRW}, " +
                    "expectedWith2ByteCS={Exp2}, expectedWith1ByteCS={Exp1} " +
                    "(surfaces={S}, portals={P}, visCells={V}, stabs={St}, flags=0x{F:X})",
                    refCellId, rawSize, drwSize, expectedWith2, expectedWith1,
                    nSurf, nPort, nVis, nStab, (uint)refCell.Flags);
            }

            // Verify: read back first cell and LBI to confirm persistence
            uint firstCellId = (lbId << 16) | 0x0100;
            bool cellOk = datwriter.TryGet<EnvCell>(firstCellId, out var verifyCell);
            bool lbiOk = datwriter.TryGet<LandBlockInfo>(lbiId, out var verifyLbi);
            int verifyStabs = cellOk ? verifyCell!.StaticObjects?.Count ?? 0 : 0;
            int verifyPortals = cellOk ? verifyCell!.CellPortals?.Count ?? 0 : 0;
            int verifyVis = cellOk ? verifyCell!.VisibleCells?.Count ?? 0 : 0;
            uint verifyFlags = cellOk ? (uint)verifyCell!.Flags : 0;
            string stabSample = "";
            if (cellOk && verifyCell!.StaticObjects?.Count > 0) {
                var first3 = verifyCell.StaticObjects.Take(3).Select(s => $"0x{s.Id:X8}");
                stabSample = $" stabIds=[{string.Join(",", first3)}]";
            }
            string ourPortalSample = "";
            if (cellOk && verifyCell!.CellPortals?.Count > 0) {
                var pSample = verifyCell.CellPortals.Take(3).Select(p =>
                    $"poly={p.PolygonId}→cell=0x{p.OtherCellId:X4},otherP={p.OtherPortalId},flags={p.Flags}");
                ourPortalSample = $" portals=[{string.Join(" | ", pSample)}]";
            }
            _logger.LogInformation(
                "[DungeonDoc] Verify LB {LB:X4}: cell 0x{CellId:X8} exists={CellOk} (env=0x{Env:X4}, " +
                "flags=0x{Flags:X}, portals={Portals}, visCells={Vis}, stabs={Stabs}{StabSample}{PortalSample}), " +
                "LBI exists={LbiOk} (numCells={Num})",
                LandblockKey, firstCellId, cellOk,
                cellOk ? verifyCell!.EnvironmentId : 0,
                verifyFlags, verifyPortals, verifyVis, verifyStabs, stabSample, ourPortalSample,
                lbiOk,
                lbiOk ? verifyLbi!.NumCells : 0);

            // Verify second cell exists and log VisibleCells for first cell
            uint secondCellId = (lbId << 16) | 0x0101;
            bool cell2Ok = datwriter.TryGet<EnvCell>(secondCellId, out var verifyCell2);
            string visListStr = "";
            if (cellOk && verifyCell!.VisibleCells?.Count > 0) {
                visListStr = string.Join(",", verifyCell.VisibleCells.Select(v => $"0x{v:X4}"));
            }
            _logger.LogInformation(
                "[DungeonDoc] Cell2 0x{Id:X8} exists={Ok} (env=0x{Env:X4}, portals={P}), " +
                "Cell1 visCells=[{VisList}]",
                secondCellId, cell2Ok,
                cell2Ok ? verifyCell2!.EnvironmentId : 0,
                cell2Ok ? verifyCell2!.CellPortals?.Count ?? 0 : 0,
                visListStr);

            return Task.FromResult(true);
        }

        private void SanitizeEnvCellSurfacesForExport(IDatReaderWriter datwriter, List<EnvCell> envCells) {
            uint? fallbackSurfaceId = ResolveFallbackSurfaceId(datwriter);
            if (!fallbackSurfaceId.HasValue) {
                _logger.LogWarning("[DungeonDoc] Surface sanitizer: no valid fallback surface found; leaving surfaces unchanged");
                return;
            }

            int filledEmpty = 0;
            int padded = 0;
            int overcount = 0;

            foreach (var envCell in envCells) {
                int requiredSlots = GetRequiredSurfaceSlots(datwriter, envCell.EnvironmentId, envCell.CellStructure);
                if (requiredSlots <= 0) continue;

                if (envCell.Surfaces.Count == 0) {
                    // Completely empty: fill all slots with fallback.
                    envCell.Surfaces.AddRange(Enumerable.Repeat(fallbackSurfaceId.Value, requiredSlots));
                    filledEmpty++;
                }
                else if (envCell.Surfaces.Count < requiredSlots) {
                    // Under-count: polygons with PosSurface >= Surfaces.Count would go out of bounds
                    // in the AC client. Pad the remainder using the last known surface so existing
                    // slots keep their original textures and only the gap is filled with a safe value.
                    uint padValue = envCell.Surfaces[envCell.Surfaces.Count - 1];
                    int deficit = requiredSlots - envCell.Surfaces.Count;
                    envCell.Surfaces.AddRange(Enumerable.Repeat(padValue, deficit));
                    padded++;
                }
                else if (envCell.Surfaces.Count > requiredSlots) {
                    // Over-count: extra slots are harmless to the client but keep the list tidy.
                    overcount++;
                }
            }

            if (filledEmpty > 0 || padded > 0 || overcount > 0) {
                _logger.LogInformation(
                    "[DungeonDoc] Surface sanitizer: filled {FilledEmpty} empty surface list(s), padded {Padded} under-count list(s); {Overcount} over-count list(s) left as-is",
                    filledEmpty, padded, overcount);
            }
        }

        private static int GetRequiredSurfaceSlots(IDatReaderWriter dats, uint envId, ushort cellStruct) {
            uint envFileId = (uint)(envId | 0x0D000000);
            if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return 0;
            if (!env.Cells.TryGetValue(cellStruct, out var cs)) return 0;

            var portalIds = cs.Portals != null ? new HashSet<ushort>(cs.Portals) : new HashSet<ushort>();
            int maxIndex = -1;
            foreach (var kvp in cs.Polygons) {
                if (portalIds.Contains(kvp.Key)) continue;
                if (kvp.Value.PosSurface > maxIndex) maxIndex = kvp.Value.PosSurface;
            }
            return maxIndex + 1;
        }

        private static uint? ResolveFallbackSurfaceId(IDatReaderWriter dats) {
            // Prefer the historical dungeon fallback.
            const uint preferred = 0x032A;
            if (dats.TryGet<Surface>(preferred, out _)) return preferred;

            foreach (var id in dats.GetAllIdsOfType<Surface>()) {
                if (dats.TryGet<Surface>(id, out _))
                    return id;
            }
            return null;
        }

        private void SanitizeEnvCellStaticsForExport(IDatReaderWriter datwriter, List<EnvCell> envCells) {
            // Only strip stabs with a null/zero ID — those are definitively invalid and would crash
            // the AC client.  Do NOT gate on TryGet<Setup>: the referenced setup may legitimately
            // live in the base-game portal.dat rather than the project's portal.dat (e.g. vanilla AC
            // static objects in a dungeon that is being re-exported to a custom DAT set).  Removing
            // valid stab references based on DAT presence was the root cause of placed objects
            // silently disappearing on every open-then-export cycle.
            int removed = 0;
            foreach (var envCell in envCells) {
                if (envCell.StaticObjects == null || envCell.StaticObjects.Count == 0) continue;
                int before = envCell.StaticObjects.Count;
                envCell.StaticObjects.RemoveAll(stab => stab.Id == 0);
                removed += before - envCell.StaticObjects.Count;
            }

            if (removed > 0) {
                _logger.LogWarning("[DungeonDoc] Static sanitizer removed {Removed} zero-ID stab(s) before export", removed);
            }
        }

        private void SanitizePortalTopologyForExport(IDatReaderWriter datwriter, List<EnvCell> envCells) {
            var byId = envCells.ToDictionary(c => c.Id, c => c);
            var validPortalPolys = new Dictionary<uint, HashSet<ushort>>();
            var validAllPolys = new Dictionary<uint, HashSet<ushort>>();

            int removedInvalidPoly = 0;
            int removedDuplicate = 0;
            int removedDangling = 0;
            int fixedBacklinks = 0;

            (HashSet<ushort> portalPolys, HashSet<ushort> allPolys) getPolySets(EnvCell cell) {
                if (validPortalPolys.TryGetValue(cell.Id, out var cachedPortal) &&
                    validAllPolys.TryGetValue(cell.Id, out var cachedAll)) {
                    return (cachedPortal, cachedAll);
                }

                uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
                var portalSet = new HashSet<ushort>();
                var allSet = new HashSet<ushort>();
                if (datwriter.TryGet<Acme.Dat.Environment>(envFileId, out var env) &&
                    env.Cells.TryGetValue(cell.CellStructure, out var cs)) {
                    if (cs.Portals != null) {
                        foreach (var p in cs.Portals)
                            portalSet.Add(p);
                    }
                    if (cs.Polygons != null) {
                        foreach (var p in cs.Polygons.Keys)
                            allSet.Add(p);
                    }
                }
                validPortalPolys[cell.Id] = portalSet;
                validAllPolys[cell.Id] = allSet;
                return (portalSet, allSet);
            }

            foreach (var cell in envCells) {
                var (portalPolys, allPolys) = getPolySets(cell);
                var seenPoly = new HashSet<ushort>();
                var sanitized = new List<CellPortal>(cell.CellPortals.Count);
                foreach (var cp in cell.CellPortals) {
                    ushort poly = (ushort)cp.PolygonId;
                    if (!allPolys.Contains(poly) || !portalPolys.Contains(poly)) {
                        removedInvalidPoly++;
                        continue;
                    }
                    if (!seenPoly.Add(poly)) {
                        removedDuplicate++;
                        continue;
                    }
                    uint otherId = (cell.Id & 0xFFFF0000u) | cp.OtherCellId;
                    if (!byId.ContainsKey(otherId)) {
                        removedDangling++;
                        continue;
                    }
                    sanitized.Add(cp);
                }
                cell.CellPortals.Clear();
                cell.CellPortals.AddRange(sanitized);
            }

            // Ensure reciprocal portal links exist and are valid.
            foreach (var cell in envCells) {
                foreach (var cp in cell.CellPortals.ToList()) {
                    uint otherId = (cell.Id & 0xFFFF0000u) | cp.OtherCellId;
                    if (!byId.TryGetValue(otherId, out var otherCell)) continue;

                    ushort otherPoly = (ushort)cp.OtherPortalId;
                    var (otherPortalPolys, otherAllPolys) = getPolySets(otherCell);
                    if (!otherAllPolys.Contains(otherPoly) || !otherPortalPolys.Contains(otherPoly))
                        continue;

                    bool hasBack = otherCell.CellPortals.Any(p =>
                        p.OtherCellId == (ushort)(cell.Id & 0xFFFF) &&
                        (ushort)p.PolygonId == otherPoly &&
                        (ushort)p.OtherPortalId == (ushort)cp.PolygonId);
                    if (hasBack) continue;

                    // If polygon slot already used on other cell, do not force conflicting backlink.
                    bool otherPolyUsed = otherCell.CellPortals.Any(p => (ushort)p.PolygonId == otherPoly);
                    if (otherPolyUsed) continue;

                    otherCell.CellPortals.Add(new CellPortal {
                        OtherCellId = (ushort)(cell.Id & 0xFFFF),
                        PolygonId = otherPoly,
                        OtherPortalId = cp.PolygonId,
                        Flags = cp.Flags
                    });
                    fixedBacklinks++;
                }
            }

            if (removedInvalidPoly > 0 || removedDuplicate > 0 || removedDangling > 0 || fixedBacklinks > 0) {
                _logger.LogWarning(
                    "[DungeonDoc] Portal sanitizer: removed invalidPoly={InvalidPoly}, duplicate={Duplicate}, dangling={Dangling}; addedBacklinks={Backlinks}",
                    removedInvalidPoly, removedDuplicate, removedDangling, fixedBacklinks);
            }
        }

        /// <summary>
        /// Populates each EnvCell's StaticObjects (stab section, flag 0x2) with visibility
        /// entries so the AC client can preload adjacent cells for portal rendering.
        ///
        /// The client's CEnvCell::grab_visible_cells iterates the stab_list (StaticObjects
        /// section) and calls add_visible_cell(stab_list[i]) for each entry. This populates
        /// the visible_cell_table used by GetVisible(), which GetOtherCell() uses to resolve
        /// portal targets. Without stab entries, GetVisible returns NULL for all adjacent
        /// cells and every portal renders as a black void.
        ///
        /// Each visibility entry is a Stab with Id = full 32-bit cell ID and
        /// Frame = the referenced cell's position. The client tolerates non-cell IDs
        /// (they silently fail to load), so actual static objects can coexist.
        /// </summary>
        private void PopulateVisibilityStabs(List<EnvCell> envCells) {
            var cellPositions = envCells.ToDictionary(c => c.Id, c => c.Position);
            int totalAdded = 0;

            foreach (var cell in envCells) {
                uint blockMask = cell.Id & 0xFFFF0000u;
                var existingIds = new HashSet<uint>(cell.StaticObjects.Select(s => s.Id));

                foreach (var visibleCellNum in cell.VisibleCells) {
                    uint fullCellId = blockMask | visibleCellNum;
                    if (fullCellId == cell.Id) continue;
                    if (existingIds.Contains(fullCellId)) continue;

                    if (!cellPositions.TryGetValue(fullCellId, out var pos))
                        pos = new Frame();

                    cell.StaticObjects.Add(new Stab {
                        Id = fullCellId,
                        Frame = pos
                    });
                    totalAdded++;
                }

                if (cell.StaticObjects.Count > 0)
                    cell.Flags |= (uint)EnvCellFlags.HasStaticObjs;
            }

            if (totalAdded > 0) {
                _logger.LogInformation(
                    "[DungeonDoc] Visibility stabs: added {Count} cell-ID stab(s) for portal rendering preload",
                    totalAdded);
            }
        }

        /// <summary>
        /// Reorders each EnvCell's CellPortals to match the CellStruct.Portals ordering
        /// from the Environment file, and pads with placeholder entries for unconnected portals.
        ///
        /// The AC client's PView rendering loop iterates CellStruct portal polygons and
        /// EnvCell CellPortals in lockstep by array index. CellPortals[i] must correspond
        /// to CellStruct.Portals[i]. If the ordering doesn't match, the BSP portal
        /// visibility check pairs with the wrong CellPortal, causing portals to render
        /// as black voids.
        ///
        /// Must run BEFORE RewriteOtherPortalIndices (since this changes CellPortal ordering).
        /// </summary>
        private void ReorderCellPortalsToMatchCellStruct(IDatReaderWriter dats, List<EnvCell> envCells) {
            int reordered = 0;
            int padded = 0;

            foreach (var cell in envCells) {
                uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
                if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(cell.CellStructure, out var cs)) continue;
                if (cs.Portals == null || cs.Portals.Count == 0) continue;

                // CellStruct.Portals may contain raw indices OR polygon IDs depending
                // on how DatReaderWriter unpacked. Resolve to polygon IDs consistently.
                var resolvedPortalIds = ResolvePortalPolygonIds(cs);

                var portalsByPoly = new Dictionary<ushort, CellPortal>();
                foreach (var cp in cell.CellPortals) {
                    portalsByPoly.TryAdd((ushort)cp.PolygonId, cp);
                }

                var ordered = new List<CellPortal>(resolvedPortalIds.Count);
                bool needsReorder = false;

                for (int i = 0; i < resolvedPortalIds.Count; i++) {
                    ushort polyId = resolvedPortalIds[i];
                    if (portalsByPoly.TryGetValue(polyId, out var existing)) {
                        ordered.Add(existing);
                        if (i < cell.CellPortals.Count && (ushort)cell.CellPortals[i].PolygonId != polyId)
                            needsReorder = true;
                    }
                    else {
                        ordered.Add(new CellPortal {
                            PolygonId = polyId,
                            OtherCellId = 0xFFFF,
                            OtherPortalId = 0xFFFF,
                            Flags = 0
                        });
                        padded++;
                        needsReorder = true;
                    }
                }

                if (!needsReorder && ordered.Count == cell.CellPortals.Count)
                    continue;

                cell.CellPortals.Clear();
                cell.CellPortals.AddRange(ordered);
                reordered++;
            }

            if (reordered > 0 || padded > 0) {
                _logger.LogInformation(
                    "[DungeonDoc] Portal ordering: reordered {Reordered} cell(s) to match CellStruct.Portals, added {Padded} placeholder(s) for unconnected portals",
                    reordered, padded);
            }
        }

        /// <summary>
        /// Resolve CellStruct.Portals entries to polygon IDs. The DAT stores indices into
        /// the polygon array, but DatReaderWriter may expose them as raw values or resolved IDs.
        /// This handles both cases, matching PortalSnapper.GetPortalPolygonIds logic.
        /// </summary>
        private static List<ushort> ResolvePortalPolygonIds(CellStruct cs) {
            if (cs.Portals == null || cs.Portals.Count == 0)
                return new List<ushort>();

            ushort[]? sortedPolyKeys = null;
            var result = new List<ushort>(cs.Portals.Count);

            foreach (var portalRef in cs.Portals) {
                ushort resolvedId = portalRef;
                if (!cs.Polygons.ContainsKey(resolvedId)) {
                    if (sortedPolyKeys == null)
                        sortedPolyKeys = cs.Polygons.Keys.OrderBy(k => k).ToArray();
                    int idx = portalRef;
                    if (idx >= 0 && idx < sortedPolyKeys.Length)
                        resolvedId = sortedPolyKeys[idx];
                }
                result.Add(resolvedId);
            }
            return result;
        }

        /// <summary>
        /// Rewrites CellPortal.OtherPortalId from polygon IDs to portal array indices.
        /// The AC client uses other_portal_id as a direct array index into the other
        /// cell's portals[] array (see PView::OtherPortalClip, CEnvCell::check_building_transit).
        /// ConnectPortals and SanitizePortalTopologyForExport store polygon IDs in this
        /// field, which causes out-of-bounds crashes when the polygon ID exceeds the
        /// portal count. This method must run after all portal additions/removals are final.
        /// </summary>
        private void RewriteOtherPortalIndices(List<EnvCell> envCells) {
            var byId = envCells.ToDictionary(c => c.Id, c => c);
            int rewritten = 0;
            int clamped = 0;

            foreach (var cell in envCells) {
                ushort cellNum = (ushort)(cell.Id & 0xFFFF);

                foreach (var cp in cell.CellPortals) {
                    if (cp.OtherCellId == 0xFFFF) continue;
                    uint otherFullId = (cell.Id & 0xFFFF0000u) | cp.OtherCellId;
                    if (!byId.TryGetValue(otherFullId, out var otherCell)) continue;

                    int bestIndex = -1;
                    int backlinksFound = 0;

                    for (int j = 0; j < otherCell.CellPortals.Count; j++) {
                        if (otherCell.CellPortals[j].OtherCellId != cellNum) continue;
                        backlinksFound++;

                        if (backlinksFound == 1)
                            bestIndex = j;

                        if ((ushort)otherCell.CellPortals[j].PolygonId == cp.OtherPortalId) {
                            bestIndex = j;
                            break;
                        }
                    }

                    if (bestIndex >= 0) {
                        if (cp.OtherPortalId != (ushort)bestIndex) {
                            cp.OtherPortalId = (ushort)bestIndex;
                            rewritten++;
                        }
                    } else {
                        // No backlink found — clamp to 0 to prevent out-of-bounds crash.
                        // The client uses OtherPortalId as a direct array index into the
                        // other cell's portals[]; leaving it as a polygon ID (e.g. 31)
                        // when the other cell has only 2 portals causes a crash.
                        if (otherCell.CellPortals.Count > 0) {
                            cp.OtherPortalId = 0;
                        }
                        _logger.LogWarning(
                            "[DungeonDoc] Portal index rewriter: no backlink found for cell 0x{CellId:X4} → 0x{OtherId:X4} (poly {Poly}), clamped OtherPortalId to 0",
                            cellNum, cp.OtherCellId, (ushort)cp.PolygonId);
                        clamped++;
                    }
                }
            }

            if (rewritten > 0 || clamped > 0) {
                _logger.LogInformation(
                    "[DungeonDoc] Portal index rewriter: fixed {Count} OtherPortalId value(s) (polygon ID → array index), clamped {Clamped} missing backlink(s)",
                    rewritten, clamped);
            }
        }

        /// <summary>
        /// Force reload all cells from the DAT file, discarding any in-memory edits.
        /// </summary>
        public void ReloadFromDat(IDatReaderWriter dats) {
            LoadCellsFromDat(dats);
            if (Cells.Count > 0)
                _nextCellNumber = (ushort)(Cells.Max(c => c.CellNumber) + 1);
            _recycledCellNumbers.Clear();
            ClearDirty();
        }

        private void LoadCellsFromDat(IDatReaderWriter dats) {
            Cells.Clear();
            uint lbId = LandblockKey;
            uint lbiId = (lbId << 16) | 0xFFFE;

            if (!dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0)
                return;

            for (uint i = 0; i < lbi.NumCells; i++) {
                ushort cellNum = (ushort)(0x0100 + i);
                uint cellId = (lbId << 16) | cellNum;
                if (dats.TryGet<EnvCell>(cellId, out var envCell)) {
                    var origin = envCell.Position.Origin;
                    var dc = new DungeonCellData {
                        CellNumber = cellNum,
                        EnvironmentId = (ushort)envCell.EnvironmentId,
                        CellStructure = envCell.CellStructure,
                        Origin = origin,
                        Orientation = envCell.Position.Orientation,
                        Flags = (uint)envCell.Flags,
                        RestrictionObj = envCell.RestrictionObj,
                    };
                    dc.Surfaces.AddRange(envCell.Surfaces.Select(s => (ushort)s));
                    if (envCell.CellPortals != null) {
                        foreach (var cp in envCell.CellPortals) {
                            dc.CellPortals.Add(new DungeonCellPortalData {
                                OtherCellId = cp.OtherCellId,
                                PolygonId = (ushort)cp.PolygonId,
                                OtherPortalId = (ushort)cp.OtherPortalId,
                                Flags = (ushort)cp.Flags
                            });
                        }
                    }
                    if (envCell.VisibleCells != null) dc.VisibleCells.AddRange(envCell.VisibleCells);
                    if (envCell.StaticObjects != null) {
                        uint blockPrefix = (uint)LandblockKey << 16;
                        foreach (var stab in envCell.StaticObjects) {
                            // Skip visibility stabs injected by PopulateVisibilityStabs on a previous
                            // export.  They carry a full cell ID (same landblock prefix, cell number in
                            // the dungeon range [0x0100, 0xFFFD]) rather than a Setup DID.  They are
                            // regenerated from VisibleCells on every export so storing them in the
                            // document would cause them to accumulate across round-trips.
                            uint stabLow = stab.Id & 0xFFFFu;
                            if ((stab.Id & 0xFFFF0000u) == blockPrefix && stabLow >= 0x0100u && stabLow <= 0xFFFDu)
                                continue;

                            var stabOrigin = stab.Frame.Origin;
                            dc.StaticObjects.Add(new DungeonStabData {
                                Id = stab.Id,
                                Origin = stabOrigin,
                                Orientation = stab.Frame.Orientation
                            });
                        }
                    }
                    Cells.Add(dc);
                }
            }
        }

        public ushort AllocateCellNumber() {
            // Drain the recycle queue, skipping any numbers that slipped outside the
            // valid EnvCell range [0x0100, 0xFFFD].
            while (_recycledCellNumbers.Count > 0) {
                var recycled = _recycledCellNumbers.Dequeue();
                if (recycled >= 0x0100 && recycled < 0xFFFE)
                    return recycled;
                _logger.LogWarning("[DungeonDoc] Discarded recycled cell number 0x{Num:X4} (outside valid range)", recycled);
            }

            // Skip the two reserved IDs: 0xFFFE (LandBlockInfo) and 0xFFFF (LandBlock).
            if (_nextCellNumber == 0xFFFE || _nextCellNumber == 0xFFFF)
                _nextCellNumber = 0;

            // If the ushort has wrapped around into the outdoor/invalid range, we've
            // exhausted the cell number space. Return 0 as an error sentinel — the
            // caller will get a "CellNumber < 0x0100" validation error and export aborts.
            if (_nextCellNumber < 0x0100) {
                _logger.LogError("[DungeonDoc] Cell number space exhausted — cannot allocate a number in [0x0100, 0xFFFD]");
                return 0;
            }

            return _nextCellNumber++;
        }

        /// <summary>
        /// Adds a new cell. Returns the assigned cell number, or 0 if the parameters
        /// are invalid (the cell is not added in that case).
        /// </summary>
        public ushort AddCell(ushort environmentId, ushort cellStructure, Vector3 origin, Quaternion orientation, List<ushort> surfaces) {
            // EnvironmentId 0 means no Environment asset — the client will crash
            // trying to dereference env->get_cellstruct() on a null pointer.
            if (environmentId == 0) {
                _logger.LogWarning("[DungeonDoc] AddCell rejected: EnvironmentId is 0. Select a valid room before placing.");
                return 0;
            }

            var cellNum = AllocateCellNumber();
            if (cellNum == 0) return 0; // exhaustion already logged above

            var cell = new DungeonCellData {
                CellNumber = cellNum,
                EnvironmentId = environmentId,
                CellStructure = cellStructure,
                Origin = origin,
                Orientation = orientation,
            };
            cell.Surfaces.AddRange(surfaces);
            Cells.Add(cell);
            MarkDirty();
            return cellNum;
        }

        public void ConnectPortals(ushort cellNumA, ushort polyIdA, ushort cellNumB, ushort polyIdB) {
            var cellA = Cells.FirstOrDefault(c => c.CellNumber == cellNumA);
            var cellB = Cells.FirstOrDefault(c => c.CellNumber == cellNumB);
            if (cellA == null || cellB == null) return;

            cellA.CellPortals.Add(new DungeonCellPortalData {
                OtherCellId = cellNumB,
                PolygonId = polyIdA,
                OtherPortalId = polyIdB,
                Flags = 0
            });

            cellB.CellPortals.Add(new DungeonCellPortalData {
                OtherCellId = cellNumA,
                PolygonId = polyIdB,
                OtherPortalId = polyIdA,
                Flags = 0
            });
            MarkDirty();
        }

        public void RemoveCell(ushort cellNumber) {
            var cell = Cells.FirstOrDefault(c => c.CellNumber == cellNumber);
            if (cell == null) return;

            foreach (var other in Cells) {
                other.CellPortals.RemoveAll(cp => cp.OtherCellId == cellNumber);
            }

            Cells.Remove(cell);

            // Only recycle numbers in the valid EnvCell range so the allocator
            // never hands out an outdoor LandCell or reserved slot.
            if (cellNumber >= 0x0100 && cellNumber < 0xFFFE)
                _recycledCellNumbers.Enqueue(cellNumber);

            MarkDirty();
        }

        // NOTE: We intentionally preserve authored dungeon coordinates on export.
        // No export-time depth remapping is applied.

        /// <summary>
        /// Legacy hook kept for compatibility with telemetry/manifest callers.
        /// Export-depth correction is disabled to match retail-authored behavior.
        /// </summary>
        public float ComputeDatExportZLift() {
            return 0f;
        }

        /// <summary>Convert all document cells to EnvCell objects for rendering or DAT export.</summary>
        public List<EnvCell> ToEnvCells(bool forDatExport = false) {
            uint lbId = LandblockKey;
            var result = new List<EnvCell>();

            foreach (var dc in Cells) {
                uint fullCellId = (lbId << 16) | dc.CellNumber;
                var origin = dc.Origin;
                var envCell = new EnvCell {
                    Id = fullCellId,
                    EnvironmentId = dc.EnvironmentId,
                    CellStructure = dc.CellStructure,
                    Flags = dc.Flags,
                    RestrictionObj = dc.RestrictionObj,
                    Position = new Frame {
                        Origin = origin,
                        Orientation = dc.Orientation
                    }
                };

                envCell.Surfaces.AddRange(dc.Surfaces.Select(s => (uint)s));
                foreach (var cp in dc.CellPortals) {
                    envCell.CellPortals.Add(new CellPortal {
                        OtherCellId = cp.OtherCellId,
                        PolygonId = cp.PolygonId,
                        OtherPortalId = cp.OtherPortalId,
                        Flags = cp.Flags
                    });
                }
                envCell.VisibleCells.AddRange(dc.VisibleCells);
                foreach (var stab in dc.StaticObjects) {
                    var stabOrigin = stab.Origin;
                    envCell.StaticObjects.Add(new Stab {
                        Id = stab.Id,
                        Frame = new Frame {
                            Origin = stabOrigin,
                            Orientation = stab.Orientation
                        }
                    });
                }

                if (dc.StaticObjects.Count > 0)
                    envCell.Flags |= (uint)EnvCellFlags.HasStaticObjs;
                else
                    envCell.Flags &= ~(uint)EnvCellFlags.HasStaticObjs;

                if (dc.RestrictionObj != 0)
                    envCell.Flags |= (uint)EnvCellFlags.HasRestrictionObj;
                else
                    envCell.Flags &= ~(uint)EnvCellFlags.HasRestrictionObj;

                result.Add(envCell);
            }

            return result;
        }

        /// <summary>
        /// Copy all cells from another dungeon document into this one.
        /// Renumbers cells sequentially starting from <paramref name="startCellNum"/>,
        /// remaps portal and visible-cell references, and preserves all other data.
        /// </summary>
        /// <param name="startCellNum">First cell number to use. Pass 0x0100 for empty landblocks,
        /// or a higher value to avoid overwriting existing building cells.</param>
        public void CopyFrom(DungeonDocument source, ushort startCellNum = 0x0100) {
            // Clamp startCellNum to the valid EnvCell range.
            if (startCellNum < 0x0100) {
                _logger.LogWarning(
                    "[DungeonDoc] CopyFrom: startCellNum 0x{Num:X4} is below 0x0100; clamped to 0x0100",
                    startCellNum);
                startCellNum = 0x0100;
            }

            // Verify the source cells fit within the available cell-number space.
            uint endRaw = (uint)startCellNum + (uint)source.Cells.Count;
            if (endRaw > 0xFFFE) {
                _logger.LogError(
                    "[DungeonDoc] CopyFrom: source has {Count} cells starting at 0x{Start:X4} which would " +
                    "overflow into reserved range (≥ 0xFFFE). Aborting copy.",
                    source.Cells.Count, startCellNum);
                return;
            }

            Cells.Clear();
            _nextCellNumber = startCellNum;
            _recycledCellNumbers.Clear();

            var cellMap = new Dictionary<ushort, ushort>();
            ushort nextNum = startCellNum;
            foreach (var src in source.Cells) {
                cellMap[src.CellNumber] = nextNum++;
            }

            foreach (var src in source.Cells) {
                ushort newCellNum = cellMap[src.CellNumber];
                var dc = new DungeonCellData {
                    CellNumber = newCellNum,
                    EnvironmentId = src.EnvironmentId,
                    CellStructure = src.CellStructure,
                    Origin = src.Origin,
                    Orientation = src.Orientation,
                    Flags = src.Flags,
                    RestrictionObj = src.RestrictionObj,
                };
                dc.Surfaces.AddRange(src.Surfaces);
                foreach (var cp in src.CellPortals) {
                    ushort remappedOther = cp.OtherCellId;
                    if (cellMap.TryGetValue(cp.OtherCellId, out var mapped))
                        remappedOther = mapped;
                    dc.CellPortals.Add(new DungeonCellPortalData {
                        OtherCellId = remappedOther,
                        PolygonId = cp.PolygonId,
                        OtherPortalId = cp.OtherPortalId,
                        Flags = cp.Flags
                    });
                }
                foreach (var vc in src.VisibleCells) {
                    // Skip stale zero entries — they are invalid and poison the client's
                    // visible-cell hash table (grab_visible_cells → add_visible_cell(0)).
                    if (vc == 0) continue;
                    dc.VisibleCells.Add(cellMap.TryGetValue(vc, out var mappedVc) ? mappedVc : vc);
                }
                foreach (var stab in src.StaticObjects) {
                    dc.StaticObjects.Add(new DungeonStabData {
                        Id = stab.Id,
                        Origin = stab.Origin,
                        Orientation = stab.Orientation
                    });
                }
                Cells.Add(dc);
            }
            _nextCellNumber = nextNum;
            MarkDirty();
        }

        public enum ValidationSeverity { Info, Warning, Error }

        public record ValidationResult(
            ValidationSeverity Severity,
            string Message,
            ushort? CellNumber = null,
            ushort? PortalPolygonId = null,
            string? Explanation = null,
            string? LocationLabel = null) {
            public string Icon => Severity switch {
                ValidationSeverity.Error => "[ERROR]",
                ValidationSeverity.Warning => "[WARN]",
                _ => "[INFO]"
            };

            public bool HasLocation => CellNumber.HasValue;
        }

        public List<string> Validate() {
            return ValidateComprehensive()
                .Where(r => r.Severity != ValidationSeverity.Info)
                .Select(r => r.Message)
                .ToList();
        }

        public List<ValidationResult> ValidateComprehensive() {
            var results = new List<ValidationResult>();

            // ── Landblock key ────────────────────────────────────────────────────────────
            // LandblockKey == 0 makes the LBI/LandBlock IDs 0x0000FFFE / 0x0000FFFF,
            // which are reserved system slots. Writing there silently corrupts the DAT.
            if (LandblockKey == 0)
                results.Add(new(ValidationSeverity.Error,
                    "LandblockKey is 0 — dungeon has no landblock assigned. Export would write to reserved DAT slot 0x0000FFFE."));

            if (Cells.Count == 0) {
                results.Add(new(ValidationSeverity.Warning, "Dungeon has no cells."));
                return results;
            }

            var cellNums = new HashSet<ushort>(Cells.Select(c => c.CellNumber));

            // ── Cell number range (from AC client CellManager / 0x100 threshold) ────────
            // Cell IDs with low-16 < 0x0100 are outdoor land-cells (1–64 = 0x0001–0x0040)
            // or reserved (0x0041–0x00FF). Writing EnvCell data into those slots corrupts
            // terrain and can crash the client loader (CellManager::PreFetchCells routes
            // low-IDs to LScape, high-IDs to CEnvCell — mixing them is fatal).
            // 0xFFFE / 0xFFFF are the LandBlockInfo and LandBlock records respectively;
            // overwriting them destroys the block's terrain entry.
            foreach (var cell in Cells) {
                if (cell.CellNumber < 0x0100)
                    results.Add(new(ValidationSeverity.Error,
                        $"Cell 0x{cell.CellNumber:X4} has a cell number below 0x0100. " +
                        "The client routes IDs < 0x0100 to the outdoor land-cell pipeline — " +
                        "this will corrupt terrain data.",
                        cell.CellNumber));
                else if (cell.CellNumber == 0xFFFE)
                    results.Add(new(ValidationSeverity.Error,
                        "Cell number 0xFFFE is reserved for the LandBlockInfo record — exporting would destroy it.",
                        cell.CellNumber));
                else if (cell.CellNumber == 0xFFFF)
                    results.Add(new(ValidationSeverity.Error,
                        "Cell number 0xFFFF is reserved for the LandBlock terrain record — exporting would destroy it.",
                        cell.CellNumber));
                else if (cell.CellNumber > 0xFEFF)
                    results.Add(new(ValidationSeverity.Warning,
                        $"Cell 0x{cell.CellNumber:X4} is close to the reserved range (0xFFFE/0xFFFF). " +
                        "Consider keeping cell numbers below 0xFF00.",
                        cell.CellNumber));
            }

            // ── Environment ID (from CEnvCell::UnPack / CEnvironment::get_cellstruct) ───
            // EnvironmentId == 0 means there is no Environment asset. The client calls
            // CEnvironment::get_cellstruct() on it unconditionally; a null env results in
            // a null-pointer dereference inside find_env_collisions and point_in_cell.
            foreach (var cell in Cells) {
                if (cell.EnvironmentId == 0)
                    results.Add(new(ValidationSeverity.Error,
                        $"Cell 0x{cell.CellNumber:X4} has EnvironmentId 0. " +
                        "The client will crash dereferencing the null environment pointer.",
                        cell.CellNumber));
            }

            // ── Duplicate cell numbers ────────────────────────────────────────────────
            var dupes = Cells.GroupBy(c => c.CellNumber).Where(g => g.Count() > 1);
            foreach (var dupe in dupes)
                results.Add(new(ValidationSeverity.Error,
                    $"Duplicate cell number 0x{dupe.Key:X4} ({dupe.Count()} cells)", dupe.Key));

            // ── Orphaned portal references ────────────────────────────────────────────
            foreach (var cell in Cells) {
                foreach (var portal in cell.CellPortals) {
                    if (portal.OtherCellId != 0 && portal.OtherCellId != 0xFFFF &&
                        !cellNums.Contains(portal.OtherCellId)) {
                        results.Add(new(ValidationSeverity.Error,
                            $"A portal points at a room that does not exist (0x{portal.OtherCellId:X4})",
                            cell.CellNumber,
                            portal.PolygonId,
                            $"Room 0x{cell.CellNumber:X4} lists a connection to 0x{portal.OtherCellId:X4}, but that room is not in this dungeon.",
                            $"Room 0x{cell.CellNumber:X4}"));
                    }
                }
            }

            // ── One-way portals (A→B exists but B→A doesn't) ─────────────────────────
            foreach (var cell in Cells) {
                foreach (var portal in cell.CellPortals) {
                    var other = GetCell(portal.OtherCellId);
                    if (other != null && !other.CellPortals.Any(p => p.OtherCellId == cell.CellNumber)) {
                        results.Add(new(ValidationSeverity.Warning,
                            "These doorways aren't connected both ways",
                            cell.CellNumber,
                            portal.PolygonId,
                            $"Room 0x{cell.CellNumber:X4} leads to 0x{portal.OtherCellId:X4}, but the other room has no return doorway. Players may not be able to walk back.",
                            $"Room 0x{cell.CellNumber:X4} → 0x{portal.OtherCellId:X4}"));
                    }
                }
            }

            // ── OtherPortalId out-of-bounds (from CEnvCell::find_transit_cells) ───────
            // The client uses OtherPortalId as a direct array index into the target cell's
            // portals[] array (CCellPortal::GetOtherCell). If the index >= portals.Count
            // the client reads garbage or crashes. RewriteOtherPortalIndices will fix this
            // on export, but flag it so users know their portal data is inconsistent.
            var cellByNum = new Dictionary<ushort, DungeonCellData>();
            foreach (var c in Cells)
                cellByNum.TryAdd(c.CellNumber, c); // safe with duplicates; duplicates already flagged above
            foreach (var cell in Cells) {
                foreach (var portal in cell.CellPortals) {
                    if (portal.OtherCellId == 0xFFFF || portal.OtherCellId == 0) continue;
                    if (!cellByNum.TryGetValue(portal.OtherCellId, out var targetCell)) continue;
                    if (portal.OtherPortalId >= targetCell.CellPortals.Count && targetCell.CellPortals.Count > 0)
                        results.Add(new(ValidationSeverity.Warning,
                            $"Cell 0x{cell.CellNumber:X4} → 0x{portal.OtherCellId:X4}: " +
                            $"OtherPortalId {portal.OtherPortalId} is out of range " +
                            $"(target has {targetCell.CellPortals.Count} portal(s)). " +
                            "Will be rewritten on export.",
                            cell.CellNumber));
                }
            }

            // ── Disconnected cells (unreachable from first cell) ──────────────────────
            var visited = new HashSet<ushort>();
            var queue = new Queue<ushort>();
            var firstCell = Cells[0].CellNumber;
            queue.Enqueue(firstCell);
            visited.Add(firstCell);
            while (queue.Count > 0) {
                var current = queue.Dequeue();
                var cell = GetCell(current);
                if (cell == null) continue;
                foreach (var portal in cell.CellPortals) {
                    if (cellNums.Contains(portal.OtherCellId) && visited.Add(portal.OtherCellId))
                        queue.Enqueue(portal.OtherCellId);
                }
            }
            foreach (var cell in Cells) {
                if (!visited.Contains(cell.CellNumber))
                    results.Add(new(ValidationSeverity.Warning,
                        $"This room is disconnected from the rest of the dungeon",
                        cell.CellNumber,
                        null,
                        $"Room 0x{cell.CellNumber:X4} cannot be reached by walking from the first room (0x{firstCell:X4}). Connect a doorway or delete the orphan.",
                        $"Room 0x{cell.CellNumber:X4}"));
            }

            // ── Open / unconnected doorways ───────────────────────────────────────────
            foreach (var cell in Cells) {
                for (int i = 0; i < cell.CellPortals.Count; i++) {
                    var portal = cell.CellPortals[i];
                    if (portal.OtherCellId != 0 && portal.OtherCellId != 0xFFFF) continue;
                    results.Add(new(
                        ValidationSeverity.Warning,
                        "These doorways aren't connected",
                        cell.CellNumber,
                        portal.PolygonId,
                        "This opening has no matching room on the other side. Cap it with a dead-end, or place a room that fits this doorway.",
                        $"Room 0x{cell.CellNumber:X4}, door {i + 1}"));
                }
            }

            // ── Missing surfaces ──────────────────────────────────────────────────────
            foreach (var cell in Cells) {
                if (cell.Surfaces.Count == 0)
                    results.Add(new(ValidationSeverity.Warning,
                        $"Cell 0x{cell.CellNumber:X4} has no surfaces assigned (will be invisible in-game)",
                        cell.CellNumber));
            }

            // ── Zero entries in VisibleCells (from CEnvCell::grab_visible_cells) ──────
            // stab_list[i] == 0 causes the client to call add_visible_cell(0), which hashes
            // to bucket 0 and silently poisons the visible-cell table with a null entry.
            // grab_visible_cells then tries to back-link the null cell to the owning landblock,
            // which is a null-pointer write.
            foreach (var cell in Cells) {
                if (cell.VisibleCells.Any(vc => vc == 0))
                    results.Add(new(ValidationSeverity.Warning,
                        $"Cell 0x{cell.CellNumber:X4} has a zero entry in VisibleCells. " +
                        "This poisons the client's visible-cell hash table.",
                        cell.CellNumber));
            }

            // ── Empty VisibleCells ────────────────────────────────────────────────────
            int emptyStabs = Cells.Count(c => c.VisibleCells.Count == 0);
            if (emptyStabs > 0)
                results.Add(new(ValidationSeverity.Info,
                    $"{emptyStabs} cell(s) have empty VisibleCells — auto-computed on export if still empty"));

            // ── Summary ───────────────────────────────────────────────────────────────
            if (results.Count == 0)
                results.Add(new(ValidationSeverity.Info,
                    $"All {Cells.Count} cells passed validation."));

            return results;
        }

        /// <summary>
        /// Auto-fixes common issues: adds missing back-portals for one-way connections.
        /// Returns the number of fixes applied.
        /// </summary>
        public int AutoFixPortals() {
            int fixes = 0;
            foreach (var cell in Cells) {
                foreach (var portal in cell.CellPortals) {
                    var other = GetCell(portal.OtherCellId);
                    if (other != null && !other.CellPortals.Any(p => p.OtherCellId == cell.CellNumber)) {
                        other.CellPortals.Add(new DungeonCellPortalData {
                            OtherCellId = cell.CellNumber,
                            PolygonId = portal.OtherPortalId,
                            OtherPortalId = portal.PolygonId,
                            Flags = portal.Flags
                        });
                        fixes++;
                    }
                }
            }
            if (fixes > 0) MarkDirty();
            return fixes;
        }

        /// <summary>
        /// Computes VisibleCells (stab lists) for all cells via BFS through portal connections.
        /// The AC client uses these to know which cells to prefetch and keep loaded.
        /// Each cell's visible set includes itself plus all cells reachable within maxDepth portal hops.
        /// </summary>
        public int ComputeVisibleCells(int maxDepth = 3) {
            var adjacency = new Dictionary<ushort, List<ushort>>();
            foreach (var cell in Cells) {
                adjacency[cell.CellNumber] = cell.CellPortals
                    .Select(p => p.OtherCellId)
                    .Where(id => id != 0 && id != 0xFFFF && Cells.Any(c => c.CellNumber == id))
                    .ToList();
            }

            int totalUpdated = 0;
            foreach (var cell in Cells) {
                var visible = new HashSet<ushort> { cell.CellNumber };
                var frontier = new Queue<(ushort id, int depth)>();
                frontier.Enqueue((cell.CellNumber, 0));

                while (frontier.Count > 0) {
                    var (current, depth) = frontier.Dequeue();
                    if (depth >= maxDepth) continue;
                    if (!adjacency.TryGetValue(current, out var neighbors)) continue;

                    foreach (var neighbor in neighbors) {
                        if (visible.Add(neighbor)) {
                            frontier.Enqueue((neighbor, depth + 1));
                        }
                    }
                }

                var sorted = visible.OrderBy(x => x).ToList();
                if (!cell.VisibleCells.SequenceEqual(sorted)) {
                    cell.VisibleCells.Clear();
                    cell.VisibleCells.AddRange(sorted);
                    totalUpdated++;
                }
            }

            if (totalUpdated > 0) MarkDirty();
            return totalUpdated;
        }

        /// <summary>
        /// Recomputes PortalFlags for all cell portals from CellStruct geometry.
        /// The PortalSide flag (bit 1) indicates which half-space of the portal
        /// polygon plane is the cell interior. This must be correct for the AC client
        /// to traverse portals properly.
        /// </summary>
        public int RecomputePortalFlags(IDatReaderWriter dats) {
            int updated = 0;
            foreach (var cell in Cells) {
                uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
                if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(cell.CellStructure, out var cs)) continue;

                var centroid = Vector3.Zero;
                int vtxCount = cs.VertexArray.Vertices.Count;
                foreach (var vtx in cs.VertexArray.Vertices.Values)
                    centroid += vtx.Origin;
                if (vtxCount > 0) centroid /= vtxCount;

                foreach (var portal in cell.CellPortals) {
                    if (!cs.Polygons.TryGetValue(portal.PolygonId, out var poly)) continue;
                    if (poly.VertexIds.Count < 3) continue;

                    if (!cs.VertexArray.Vertices.TryGetValue((ushort)poly.VertexIds[0], out var v0)) continue;
                    if (!cs.VertexArray.Vertices.TryGetValue((ushort)poly.VertexIds[1], out var v1)) continue;
                    if (!cs.VertexArray.Vertices.TryGetValue((ushort)poly.VertexIds[2], out var v2)) continue;

                    var normal = Vector3.Normalize(Vector3.Cross(v1.Origin - v0.Origin, v2.Origin - v0.Origin));
                    float d = -Vector3.Dot(normal, v0.Origin);
                    float centroidDot = Vector3.Dot(normal, centroid) + d;

                    // AC client: PortalSide flag (0x0002) SET → portal_side=0 (interior on positive side)
                    //            PortalSide flag CLEAR        → portal_side=1 (interior on negative side)
                    ushort newFlags = (ushort)(portal.Flags & ~0x0002);
                    if (centroidDot >= 0)
                        newFlags |= 0x0002;

                    if (portal.Flags != newFlags) {
                        portal.Flags = newFlags;
                        updated++;
                    }
                }
            }
            if (updated > 0) MarkDirty();
            return updated;
        }

        public DungeonCellData? GetCell(ushort cellNumber) =>
            Cells.FirstOrDefault(c => c.CellNumber == cellNumber);
    }
}
