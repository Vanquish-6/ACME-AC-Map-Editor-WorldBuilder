using CommunityToolkit.Mvvm.DependencyInjection;
using Acme.Dat;
using MemoryPack;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace WorldBuilder.Shared.Documents {
    [MemoryPack.MemoryPackable]
    public partial struct StaticObject {
        public uint Id; // GfxObj or Setup ID, or ParticleEmitter DID when <see cref="IsParticleEmitter"/> is true
        public bool IsSetup; // True for Setup, false for GfxObj (ignored when <see cref="IsParticleEmitter"/>)
        public Vector3 Origin; // World-space position
        public Quaternion Orientation; // World-space rotation
        public Vector3 Scale;

        /// <summary>
        /// When true, <see cref="Id"/> is a portal <c>ParticleEmitter</c> definition; particles render via the simulator.
        /// Retail outdoor stabs may store these DIDs; we persist them as normal <c>Stab</c> rows on export.
        /// </summary>
        public bool IsParticleEmitter;
    }

    [MemoryPack.MemoryPackable]
    public partial class LandblockData {
        public List<StaticObject> StaticObjects = new();
    }

    public partial class LandblockDocument : BaseDocument {
        public override string Type => nameof(LandblockDocument);

        [MemoryPackInclude]
        private LandblockData _data = new();

        private bool _loadedFromProjection;

        /// <summary>
        /// True when this document was restored from a saved project rather than
        /// loaded fresh from the base DATs. Export must still write these documents
        /// even if they haven't been edited this session.
        /// </summary>
        [MemoryPack.MemoryPackIgnore]
        public bool LoadedFromProjection => _loadedFromProjection;

        public LandblockDocument(ILogger logger) : base(logger) {
        }

        protected override async Task<bool> InitInternal(IDatReaderWriter datreader, DocumentManager documentManager) {
            if (_loadedFromProjection) {
                ClearDirty();
                return true;
            }

            if (documentManager.SkipDatStatics) {
                ClearDirty();
                return true;
            }

            var lbIdHex = Id.Replace("landblock_", "");
            var lbId = uint.Parse(lbIdHex, System.Globalization.NumberStyles.HexNumber);
            var infoId = lbId << 16 | 0xFFFE;

            if (datreader.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                foreach (var obj in lbi.Objects) {
                    // Only IDs in the 0x32xxxxxx range can be ParticleEmitter entries.
                    // Portal.TryGet is a generic ID lookup, so non-emitter files (Setup 0x02,
                    // GfxObj 0x01, etc.) would be found and mis-parsed as ParticleEmitter,
                    // producing garbage data with non-zero GfxObjId fields.
                    bool isParticle = (obj.Id >> 24) == 0x32
                        && AcParticleEmitterSimulator.TryResolveVisualGfxObjectIdFromPortal(datreader, obj.Id, out _);

                    _data.StaticObjects.Add(new StaticObject {
                        Id = obj.Id,
                        IsSetup = isParticle ? false : (obj.Id & 0x02000000) != 0,
                        Origin = Offset(obj.Frame.Origin, lbId),
                        Orientation = obj.Frame.Orientation,
                        Scale = Vector3.One,
                        IsParticleEmitter = isParticle
                    });
                }

                foreach (var building in lbi.Buildings) {
                    _data.StaticObjects.Add(new StaticObject {
                        Id = building.ModelId,
                        IsSetup = (building.ModelId & 0x02000000) != 0,
                        Origin = Offset(building.Frame.Origin, lbId),
                        Orientation = building.Frame.Orientation,
                        Scale = Vector3.One
                    });
                }
            }

            ClearDirty();
            return true;
        }

        private Vector3 Offset(Vector3 origin, uint lbId) {
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            return new Vector3(blockX * 192f + origin.X, blockY * 192f + origin.Y, origin.Z);
        }

        protected override byte[] SaveToProjectionInternal() {
            return MemoryPackSerializer.Serialize(_data);
        }

        protected override bool LoadFromProjectionInternal(byte[] projection) {
            _data = MemoryPackSerializer.Deserialize<LandblockData>(projection) ?? new();
            _loadedFromProjection = true;
            return true;
        }

        protected override Task<bool> SaveToDatsInternal(IDatReaderWriter datwriter, int iteration = 0) {
            var lbIdHex = Id.Replace("landblock_", "");
            var lbId = uint.Parse(lbIdHex, System.Globalization.NumberStyles.HexNumber);
            var infoId = lbId << 16 | 0xFFFE;

            _logger.LogInformation("[LBDoc] Saving landblock 0x{LbId:X4} — {ObjCount} static objects", lbId, _data.StaticObjects.Count);

            // Read original LandBlockInfo to get building data (portals, cells, etc.)
            if (!datwriter.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                lbi = new LandBlockInfo();
                lbi.Id = infoId;
                _logger.LogInformation("[LBDoc]   No original LandBlockInfo found, creating new");
            }
            else {
                _logger.LogInformation("[LBDoc]   Original LandBlockInfo: {ObjCount} objects, {BldCount} buildings, NumCells={NumCells}",
                    lbi.Objects.Count, lbi.Buildings.Count, lbi.NumCells);
            }

            // Track which StaticObjects have been matched to an original building
            var consumed = new HashSet<int>();
            var originalNumCells = lbi.NumCells;

            // For each original building, try to find a matching StaticObject.
            // If found: update the building's position (preserving portal/cell data).
            // If not found: the building was deleted by the user.
            // NOTE: We do NOT use BFS exclusion sets here. In original AC data, each building's
            // cell graph is isolated (CellPortals don't cross between buildings), so the BFS
            // naturally stays within each building. Using exclusion sets caused incorrect cell
            // omission that led to missing EnvCells and server crashes.
            var survivingBuildings = new List<BuildingInfo>();
            foreach (var building in lbi.Buildings) {
                var buildingWorldPos = Offset(building.Frame.Origin, lbId);

                // Find the best matching StaticObject (same ID, closest to original position)
                int bestIdx = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < _data.StaticObjects.Count; i++) {
                    if (consumed.Contains(i)) continue;
                    var obj = _data.StaticObjects[i];
                    if (obj.IsParticleEmitter || obj.Id != building.ModelId) continue;

                    float dist = Vector3.Distance(obj.Origin, buildingWorldPos);
                    if (dist < bestDist) {
                        bestDist = dist;
                        bestIdx = i;
                    }
                }

                if (bestIdx >= 0) {
                    // Building still exists — update its Frame from the StaticObject (handles moves)
                    consumed.Add(bestIdx);
                    var matched = _data.StaticObjects[bestIdx];
                    var newLocal = ReverseOffset(matched.Origin, lbId);
                    var oldLocal = building.Frame.Origin;
                    var oldRotation = building.Frame.Orientation;

                    var hasMoved = Vector3.Distance(newLocal, oldLocal) > 0.01f;
                    // Quaternion dot of 1.0 means identical; allow small float precision tolerance
                    var dotProduct = Math.Abs(Quaternion.Dot(matched.Orientation, oldRotation));
                    var hasRotated = dotProduct < 0.9999f;

                    if (hasMoved || hasRotated) {
                        _logger.LogInformation("[LBDoc]   Building 0x{Id:X8} MOVED: ({OldX:F1},{OldY:F1},{OldZ:F1}) -> ({NewX:F1},{NewY:F1},{NewZ:F1})",
                            building.ModelId, oldLocal.X, oldLocal.Y, oldLocal.Z, newLocal.X, newLocal.Y, newLocal.Z);

                        // Discover and move all EnvCells belonging to this building
                        var cellIds = CollectBuildingCellIds(building, datwriter, lbId);
                        if (cellIds.Count > 0) {
                            _logger.LogInformation("[LBDoc]     Found {CellCount} EnvCells for building 0x{Id:X8}", cellIds.Count, building.ModelId);
                            MoveBuildingEnvCells(cellIds, datwriter, lbId, oldLocal, oldRotation, newLocal, matched.Orientation, iteration);
                        }
                    }

                    building.Frame = new Frame {
                        Origin = newLocal,
                        Orientation = matched.Orientation
                    };
                    survivingBuildings.Add(building);
                }
                else {
                    // Building was deleted -- count its EnvCells for NumCells adjustment
                    var deletedCellIds = CollectBuildingCellIds(building, datwriter, lbId);
                    if (deletedCellIds.Count > 0) {
                        _logger.LogInformation("[LBDoc]   Building 0x{Id:X8} DELETED — {CellCount} EnvCells orphaned (will be cleaned up in Phase 2)",
                            building.ModelId, deletedCellIds.Count);
                        // Safe subtraction: NumCells is uint; decrement without underflow.
                        lbi.NumCells = (uint)Math.Max(0L, (long)lbi.NumCells - deletedCellIds.Count);
                    }
                    else {
                        _logger.LogInformation("[LBDoc]   Building 0x{Id:X8} DELETED (no EnvCells)", building.ModelId);
                    }
                }
            }

            lbi.Buildings.Clear();
            lbi.Buildings.AddRange(survivingBuildings);

            if (lbi.NumCells != originalNumCells) {
                _logger.LogInformation("[LBDoc]   NumCells adjusted: {Old} -> {New}", originalNumCells, lbi.NumCells);
            }

            // Non-consumed StaticObjects: check for new buildings, rest become Stab entries
            lbi.Objects.Clear();
            for (int i = 0; i < _data.StaticObjects.Count; i++) {
                if (consumed.Contains(i)) continue;

                var obj = _data.StaticObjects[i];

                var localOrigin = ReverseOffset(obj.Origin, lbId);

                // Check if this is a known building model — if so, instantiate with EnvCells
                if (BuildingBlueprintCache.IsBuildingModelId(obj.Id, datwriter)) {
                    var blueprint = BuildingBlueprintCache.GetBlueprint(obj.Id, datwriter, _logger);
                    if (blueprint != null) {
                        var result = BuildingBlueprintCache.InstantiateBlueprint(
                            blueprint, localOrigin, obj.Orientation,
                            lbId, lbi.NumCells, datwriter, iteration, _logger);

                        if (result.HasValue) {
                            lbi.Buildings.Add(result.Value.building);
                            lbi.NumCells += (uint)result.Value.cellCount;
                            _logger.LogInformation("[LBDoc]   NEW building 0x{Id:X8} placed with {CellCount} EnvCells",
                                obj.Id, result.Value.cellCount);
                            continue; // Don't add as Stab
                        }
                        else {
                            _logger.LogWarning("[LBDoc]   Failed to instantiate building 0x{Id:X8}, adding as regular object", obj.Id);
                        }
                    }
                    else {
                        _logger.LogWarning("[LBDoc]   No blueprint found for building 0x{Id:X8}, adding as regular object", obj.Id);
                    }
                }

                lbi.Objects.Add(new Stab {
                    Id = obj.Id,
                    Frame = new Frame {
                        Origin = localOrigin,
                        Orientation = obj.Orientation
                    }
                });
            }

            _logger.LogInformation("[LBDoc]   Result: {ObjCount} objects, {BldCount} buildings",
                lbi.Objects.Count, lbi.Buildings.Count);

            if (!datwriter.TrySave(lbi, iteration)) {
                _logger.LogError("[LBDoc]   FAILED to save LandBlockInfo 0x{InfoId:X8}", infoId);
                return Task.FromResult(false);
            }

            _logger.LogInformation("[LBDoc]   Saved LandBlockInfo 0x{InfoId:X8} successfully", infoId);
            return Task.FromResult(true);
        }

        private Vector3 ReverseOffset(Vector3 worldPos, uint lbId) {
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            return new Vector3(worldPos.X - blockX * 192f, worldPos.Y - blockY * 192f, worldPos.Z);
        }

        /// <summary>
        /// Returns true if a cell ID is in the valid EnvCell range (0x0100-0xFFFD).
        /// Excludes outdoor LandCells (0x0001-0x0040), LandBlockInfo (0xFFFE), and LandBlock (0xFFFF).
        /// </summary>
        private static bool IsEnvCellId(ushort cellId) => cellId >= 0x0100 && cellId <= 0xFFFD;

        /// <summary>
        /// Builds a map from EnvCell number (low 16 bits of cell ID) to the BuildingInfo that owns that cell.
        /// Used so interior EnvCells can be placed 1:1 with the building exterior (same frame).
        /// </summary>
        public static Dictionary<ushort, BuildingInfo> GetCellToBuildingMap(LandBlockInfo lbi, IDatReaderWriter datAccess, uint lbId) {
            var map = new Dictionary<ushort, BuildingInfo>();
            if (lbi.Buildings == null) return map;
            var excluded = new HashSet<ushort>();
            foreach (var building in lbi.Buildings) {
                var cellIds = CollectBuildingCellIdsStatic(building, datAccess, lbId, excluded);
                foreach (var cellNum in cellIds)
                    map.TryAdd(cellNum, building);
                excluded.UnionWith(cellIds);
            }
            return map;
        }

        /// <summary>
        /// Walks a building's portal graph to collect all EnvCell IDs (0x0100-0xFFFD).
        /// An optional exclusion set prevents the BFS from crossing into cells belonging
        /// to other buildings in the same landblock (fixes duplicate building conflicts).
        /// </summary>
        private static HashSet<ushort> CollectBuildingCellIdsStatic(BuildingInfo building, IDatReaderWriter datAccess, uint lbId,
            HashSet<ushort>? excludeCellIds = null) {
            var cellIds = new HashSet<ushort>();
            var toVisit = new Queue<ushort>();

            // Seed from building portals
            foreach (var portal in building.Portals) {
                if (IsEnvCellId(portal.OtherCellId) &&
                    (excludeCellIds == null || !excludeCellIds.Contains(portal.OtherCellId)) &&
                    cellIds.Add(portal.OtherCellId))
                    toVisit.Enqueue(portal.OtherCellId);

                foreach (var stab in portal.StabList) {
                    if (IsEnvCellId(stab) &&
                        (excludeCellIds == null || !excludeCellIds.Contains(stab)) &&
                        cellIds.Add(stab))
                        toVisit.Enqueue(stab);
                }
            }

            // BFS: follow CellPortal links to discover connected cells
            while (toVisit.Count > 0) {
                var cellNum = toVisit.Dequeue();
                uint fullCellId = (lbId << 16) | cellNum;

                if (datAccess.TryGet<EnvCell>(fullCellId, out var envCell)) {
                    foreach (var cp in envCell.CellPortals) {
                        if (IsEnvCellId(cp.OtherCellId) &&
                            (excludeCellIds == null || !excludeCellIds.Contains(cp.OtherCellId)) &&
                            cellIds.Add(cp.OtherCellId))
                            toVisit.Enqueue(cp.OtherCellId);
                    }
                }
            }

            return cellIds;
        }

        private HashSet<ushort> CollectBuildingCellIds(BuildingInfo building, IDatReaderWriter datAccess, uint lbId,
            HashSet<ushort>? excludeCellIds = null) =>
            CollectBuildingCellIdsStatic(building, datAccess, lbId, excludeCellIds);

        /// <summary>
        /// Moves all EnvCells belonging to a building by applying a position delta and rotation delta.
        /// Each EnvCell's Position is in landblock-local space, same as the building's Frame.
        /// </summary>
        private void MoveBuildingEnvCells(
            HashSet<ushort> cellIds,
            IDatReaderWriter datAccess,
            uint lbId,
            Vector3 oldBuildingOrigin,
            Quaternion oldBuildingRotation,
            Vector3 newBuildingOrigin,
            Quaternion newBuildingRotation,
            int iteration) {

            var positionDelta = newBuildingOrigin - oldBuildingOrigin;
            var rotationDelta = newBuildingRotation * Quaternion.Inverse(oldBuildingRotation);
            var hasRotation = Math.Abs(Quaternion.Dot(rotationDelta, Quaternion.Identity) - 1.0f) > 0.0001f;

            // Compute outdoor cell delta for VisibleCells LandCell fixup (same as InstantiateBlueprint).
            // LandCell references (0x0001-0x0040) are position-dependent: when a building moves
            // across an outdoor cell boundary, stale references cause find_transit_cells
            // to fail portal lookups, resulting in walk-through walls.
            var (oldCellX, oldCellY) = PositionToOutdoorCell(oldBuildingOrigin);
            var (newCellX, newCellY) = PositionToOutdoorCell(newBuildingOrigin);
            int cellDeltaX = newCellX - oldCellX;
            int cellDeltaY = newCellY - oldCellY;
            bool needsLandCellFixup = cellDeltaX != 0 || cellDeltaY != 0;

            foreach (var cellNum in cellIds) {
                uint fullCellId = (lbId << 16) | cellNum;

                if (!datAccess.TryGet<EnvCell>(fullCellId, out var envCell)) {
                    _logger.LogWarning("[LBDoc]     EnvCell 0x{CellId:X8} not found in dat, skipping", fullCellId);
                    continue;
                }

                if (hasRotation) {
                    // Rotate the cell's position around the building's old origin, then translate
                    var relativePos = envCell.Position.Origin - oldBuildingOrigin;
                    var rotatedPos = Vector3.Transform(relativePos, rotationDelta);
                    envCell.Position.Origin = newBuildingOrigin + rotatedPos;

                    // Compose rotation deltas for the cell's own orientation
                    envCell.Position.Orientation = Quaternion.Normalize(rotationDelta * envCell.Position.Orientation);

                    // Also transform static objects inside the cell (landblock-local coords)
                    foreach (var stab in envCell.StaticObjects) {
                        var relObj = stab.Frame.Origin - oldBuildingOrigin;
                        var rotObj = Vector3.Transform(relObj, rotationDelta);
                        stab.Frame = new Frame {
                            Origin = newBuildingOrigin + rotObj,
                            Orientation = Quaternion.Normalize(rotationDelta * stab.Frame.Orientation)
                        };
                    }
                }
                else {
                    // Translation only -- just shift the origin
                    envCell.Position.Origin += positionDelta;

                    // Also shift static objects inside the cell (landblock-local coords)
                    foreach (var stab in envCell.StaticObjects) {
                        stab.Frame = new Frame {
                            Origin = stab.Frame.Origin + positionDelta,
                            Orientation = stab.Frame.Orientation
                        };
                    }
                }

                // Fix up LandCell references in VisibleCells when building crosses outdoor cell boundaries.
                // This matches the same fixup that InstantiateBlueprint performs for new buildings.
                if (needsLandCellFixup) {
                    for (int v = 0; v < envCell.VisibleCells.Count; v++) {
                        var vc = envCell.VisibleCells[v];
                        if (vc >= 0x0001 && vc <= 0x0040) {
                            var (vcCellX, vcCellY) = LandCellToXY(vc);
                            int fixedX = Math.Clamp(vcCellX + cellDeltaX, 0, 7);
                            int fixedY = Math.Clamp(vcCellY + cellDeltaY, 0, 7);
                            ushort fixedLandCell = XYToLandCell(fixedX, fixedY);

                            if (vc != fixedLandCell) {
                                _logger.LogInformation("[LBDoc]     VisibleCell LandCell fixup: 0x{Old:X4} -> 0x{New:X4} (building moved from cell ({OX},{OY}) to ({NX},{NY}))",
                                    vc, fixedLandCell, oldCellX, oldCellY, newCellX, newCellY);
                            }
                            envCell.VisibleCells[v] = fixedLandCell;
                        }
                    }
                }

                if (!datAccess.TrySave(envCell, iteration)) {
                    _logger.LogError("[LBDoc]     FAILED to save EnvCell 0x{CellId:X8}", fullCellId);
                }
                else {
                    _logger.LogInformation("[LBDoc]     Moved EnvCell 0x{CellId:X8} to ({X:F1},{Y:F1},{Z:F1})",
                        fullCellId, envCell.Position.Origin.X, envCell.Position.Origin.Y, envCell.Position.Origin.Z);
                }
            }
        }

        /// <summary>
        /// Converts a landblock-local position to outdoor cell grid coordinates (0-7, 0-7).
        /// Each outdoor cell is 24x24 units within a 192x192 landblock.
        /// </summary>
        private static (int cellX, int cellY) PositionToOutdoorCell(Vector3 localPos) {
            int cellX = Math.Clamp((int)(localPos.X / 24f), 0, 7);
            int cellY = Math.Clamp((int)(localPos.Y / 24f), 0, 7);
            return (cellX, cellY);
        }

        /// <summary>
        /// Decomposes a LandCell ID (0x0001-0x0040) into grid coordinates.
        /// Formula: landCellId = cellX * 8 + cellY + 1
        /// </summary>
        private static (int cellX, int cellY) LandCellToXY(ushort landCellId) {
            int id = landCellId - 1;
            return (id / 8, id % 8);
        }

        /// <summary>
        /// Converts outdoor cell grid coordinates to a LandCell ID (0x0001-0x0040).
        /// </summary>
        private static ushort XYToLandCell(int cellX, int cellY) {
            return (ushort)(cellX * 8 + cellY + 1);
        }

        public bool Apply(BaseDocumentEvent evt) {
            if (evt is StaticObjectUpdateEvent objEvt) {
                if (objEvt.IsAdd) {
                    // Id == 0 is never a valid Setup or GfxObj — the client would try to look
                    // up asset 0x00000000 and produce garbage or a crash.
                    if (objEvt.Object.Id == 0) {
                        _logger.LogWarning("[LBDoc] Apply: StaticObjectUpdateEvent has Id=0, skipping add");
                        return true;
                    }
                    _data.StaticObjects.Add(objEvt.Object);
                }
                else {
                    // Remove by matching Id and Origin
                    var idx = _data.StaticObjects.FindIndex(o =>
                        o.Id == objEvt.Object.Id &&
                        o.IsParticleEmitter == objEvt.Object.IsParticleEmitter &&
                        Vector3.Distance(o.Origin, objEvt.Object.Origin) < 0.01f);
                    if (idx >= 0) {
                        _data.StaticObjects.RemoveAt(idx);
                    }
                }
                MarkDirty();
                return true;
            }
            return true;
        }

        public IEnumerable<(Vector3 Position, Quaternion Rotation)> GetStaticSpawns() {
            foreach (var obj in _data.StaticObjects) {
                yield return (obj.Origin, obj.Orientation);
            }
        }

        public IEnumerable<StaticObject> GetStaticObjects() => _data.StaticObjects;

        /// <summary>
        /// Gets the number of static objects in this landblock
        /// </summary>
        public int StaticObjectCount => _data.StaticObjects.Count;

        /// <summary>
        /// Gets a static object by index
        /// </summary>
        public StaticObject GetStaticObject(int index) => _data.StaticObjects[index];

        /// <summary>
        /// Updates the Z (height) coordinate of a static object at the given index
        /// </summary>
        public void SetStaticObjectHeight(int index, float newZ) {
            if (index < 0 || index >= _data.StaticObjects.Count) return;
            var obj = _data.StaticObjects[index];
            obj.Origin = new Vector3(obj.Origin.X, obj.Origin.Y, newZ);
            _data.StaticObjects[index] = obj;
            MarkDirty();
        }

        /// <summary>
        /// Replaces a static object at the given index with updated data.
        /// </summary>
        public void UpdateStaticObject(int index, StaticObject updatedObj) {
            if (index < 0 || index >= _data.StaticObjects.Count) return;
            if (updatedObj.Id == 0) {
                _logger.LogWarning("[LBDoc] UpdateStaticObject: Id is 0 — skipping update (0x00000000 is not a valid asset ID)");
                return;
            }
            if ((updatedObj.Id >> 24) != 0x02) {
                _logger.LogWarning("[LBDoc] UpdateStaticObject: Id 0x{Id:X8} is not a Setup DID (high byte 0x{Type:X2} != 0x02) — skipping", updatedObj.Id, updatedObj.Id >> 24);
                return;
            }
            _data.StaticObjects[index] = updatedObj;
            MarkDirty();
        }

        /// <summary>
        /// Adds a new static object to the landblock. Returns the index, or -1 if the object is invalid.
        /// </summary>
        public int AddStaticObject(StaticObject obj) {
            if (obj.Id == 0) {
                _logger.LogWarning("[LBDoc] AddStaticObject: Id is 0 — skipping (0x00000000 is not a valid asset ID)");
                return -1;
            }
            if ((obj.Id >> 24) != 0x02) {
                _logger.LogWarning("[LBDoc] AddStaticObject: Id 0x{Id:X8} is not a Setup DID (high byte 0x{Type:X2} != 0x02) — skipping", obj.Id, obj.Id >> 24);
                return -1;
            }
            _data.StaticObjects.Add(obj);
            MarkDirty();
            return _data.StaticObjects.Count - 1;
        }

        /// <summary>
        /// Removes the static object at the given index
        /// </summary>
        public bool RemoveStaticObject(int index) {
            if (index < 0 || index >= _data.StaticObjects.Count) return false;
            _data.StaticObjects.RemoveAt(index);
            MarkDirty();
            return true;
        }

        /// <summary>
        /// Removes all static objects from this landblock and marks it dirty so the
        /// empty state is persisted. Used by the "Fresh Start" feature.
        /// </summary>
        public void ClearAllStatics() {
            if (_data.StaticObjects.Count == 0) return;
            _data.StaticObjects.Clear();
            _loadedFromProjection = true;
            MarkDirty();
        }
    }
    public class StaticObjectUpdateEvent : TerrainUpdateEvent {
        public StaticObject Object { get; }
        public bool IsAdd { get; } // True for add, false for remove

        public StaticObjectUpdateEvent(StaticObject obj, bool isAdd) {
            Object = obj;
            IsAdd = isAdd;
        }
    }
}