using Acme.Render;
using Acme.Render.GL;
using Acme.Dat;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Rendering;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {
    /// <summary>
    /// Simplified scene for the Dungeon Editor. Manages camera, EnvCell rendering,
    /// static object rendering, and GL state for a single dungeon landblock.
    /// Uses SceneContext (same as the landscape editor) for GPU resource management.
    /// </summary>
    public class DungeonScene : IDisposable {
        private readonly IDatReaderWriter _dats;
        private readonly WorldBuilderSettings _settings;
        private readonly TextureDiskCache _textureCache;

        public PerspectiveCamera Camera { get; }

        private OpenGLRenderer? _renderer;
        private SceneContext? _sceneContext;
        private bool _gpuInitialized;

        public EnvCellManager? EnvCellManager => _sceneContext?.EnvCellManager;
        public Landscape.TransformGizmo? Gizmo => _sceneContext?.Gizmo;

        public ThumbnailRenderService? ThumbnailService { get; private set; }

        private List<StaticObject> _dungeonStatics = new();
        private readonly Dictionary<(uint, bool), List<Matrix4x4>> _objectGroupBuffer = new();

        /// <summary>
        /// Runtime-only cache mapping WeenieClassId -> Setup DID for rendering instance placements.
        /// Populated from the DB picker; not persisted.
        /// </summary>
        private readonly Dictionary<uint, uint> _weenieSetupCache = new();

        /// <summary>
        /// Set by the editor to highlight selected cells with wireframe boxes.
        /// Primary (first) is used for portal indicators.
        /// </summary>
        public IReadOnlyList<LoadedEnvCell>? SelectedCells { get; set; }

        /// <summary>
        /// Primary selected cell (first in SelectedCells). For backward compat with portal indicator logic.
        /// </summary>
        public LoadedEnvCell? SelectedCell => SelectedCells?.Count > 0 ? SelectedCells[0] : null;

        /// <summary>
        /// Cells connected to the selected cell(s) via portals. Rendered with a
        /// subtler highlight so users can see at a glance what is linked.
        /// </summary>
        public IReadOnlyList<LoadedEnvCell>? ConnectedNeighborCells { get; set; }

        /// <summary>
        /// Set by the editor when a static object is selected, to render an AABB highlight.
        /// (worldMin, worldMax) of the selected object's bounding box.
        /// </summary>
        public (Vector3 Min, Vector3 Max)? SelectedObjectBounds { get; set; }

        /// <summary>
        /// World-space position and orientation of the selected object for gizmo rendering.
        /// </summary>
        public Vector3? SelectedObjectPosition { get; set; }
        public Quaternion SelectedObjectOrientation { get; set; } = Quaternion.Identity;

        /// <summary>
        /// Set by the editor when in object placement mode. Rendered as a ghost preview
        /// following the mouse cursor. Null when not in placement mode.
        /// </summary>
        public StaticObject? PlacementPreview { get; set; }

        /// <summary>
        /// Surface indicator for placement: shows an oriented disc at the cursor hit point.
        /// </summary>
        public Vector3? SurfaceIndicatorPosition { get; set; }
        public Vector3 SurfaceIndicatorNormal { get; set; } = Vector3.UnitZ;

        /// <summary>
        /// Hovered object info for highlight rendering. Set by the SelectTool on mouse move.
        /// </summary>
        public uint HoveredObjectId { get; set; }
        public bool HoveredObjectIsSetup { get; set; }
        public Vector3? HoveredObjectPosition { get; set; }
        public Quaternion HoveredObjectOrientation { get; set; } = Quaternion.Identity;
        public Vector3 HoveredObjectScale { get; set; } = Vector3.One;

        /// <summary>
        /// Whether the placement preview position is inside a valid cell.
        /// When false, the preview tints red.
        /// </summary>
        public bool PlacementPreviewValid { get; set; } = true;

        /// <summary>
        /// Set by the editor when in room placement mode. Rendered as a wireframe preview
        /// showing where the room would be placed. Null when not in placement mode.
        /// </summary>
        public RoomPlacementPreviewData? RoomPlacementPreview { get; set; }

        /// <summary>
        /// Connection lines between portal centroids (from, to) in world space.
        /// Set by the editor before each render to show which cells connect to which.
        /// </summary>
        public IReadOnlyList<(Vector3 From, Vector3 To)>? ConnectionLines { get; set; }

        /// <summary>
        /// Connection lines that touch the selected cell. Rendered brighter so you can
        /// instantly see which connections belong to the current selection.
        /// </summary>
        public IReadOnlyList<(Vector3 From, Vector3 To)>? SelectedConnectionLines { get; set; }

        public bool ShowConnectionLines { get; set; } = true;

        /// <summary>
        /// All open portals across the entire dungeon, for rendering green indicators.
        /// Set by the editor whenever the dungeon changes.
        /// </summary>
        public List<OpenPortalIndicator> OpenPortalIndicators { get; set; } = new();

        /// <summary>
        /// All connected portals across the entire dungeon, for rendering blue indicators.
        /// Only rendered when ShowPortalIndicators is true.
        /// </summary>
        public List<OpenPortalIndicator> ConnectedPortalIndicators { get; set; } = new();

        /// <summary>Whether to show portal indicators on all cells (not just selected).</summary>
        public bool ShowPortalIndicators { get; set; } = true;

        /// <summary>When true, renders with an orthographic top-down projection instead of perspective.</summary>
        public bool UseOrthographic { get; set; }

        /// <summary>Orthographic view half-height in world units. Adjustable via scroll wheel.</summary>
        public float OrthoSize { get; set; } = 50f;

        /// <summary>Index of the open portal nearest to the mouse during placement. -1 = none.</summary>
        public int NearestOpenPortalIndex { get; set; } = -1;

        /// <summary>Selected / locked doorway (yellow). 0 = none.</summary>
        public ushort HighlightedPortalCellNum { get; set; }
        /// <summary>Polygon ID of the selected doorway. 0 = none.</summary>
        public ushort HighlightedPortalPolyId { get; set; }
        /// <summary>Doorway under the cursor (white-gold). Ignored when it matches the selection.</summary>
        public ushort HoveredPortalCellNum { get; set; }
        /// <summary>Polygon ID of the hovered doorway. 0 = none.</summary>
        public ushort HoveredPortalPolyId { get; set; }

        /// <summary>True when the editor is in placement mode (show portal highlights prominently).</summary>
        public bool IsInPlacementMode { get; set; }

        /// <summary>True when the preview is snapped to a valid portal. False when free-floating.</summary>
        public bool PreviewIsSnapped { get; set; }

        /// <summary>When true, renders a reference grid on the XY plane to help orient in empty 3D space.</summary>
        public bool ShowGrid { get; set; } = true;

        /// <summary>
        /// Set by the placement tool: list of EnvCells to render as a textured placement preview.
        /// Uses a separate preview landblock so it renders alongside the main dungeon.
        /// </summary>
        public List<EnvCell>? PreviewEnvCells { get; set; }
        private bool _hasPreviewCells;
        private int _previewIdentity;
        private int _previewPose;
        private const ushort PreviewLandblockKey = EnvCellManager.PreviewLandblockKey;

        private uint _lineVAO;
        private uint _lineVBO;
        private uint _objSelVAO;
        private uint _objSelVBO;
        private uint _connLineVAO;
        private uint _connLineVBO;
        private uint _portalConnVAO;
        private uint _portalConnVBO;
        private uint _portalOpenVAO;
        private uint _portalOpenVBO;
        private uint _allOpenVAO;
        private uint _allOpenVBO;
        private uint _connPortalVAO;
        private uint _connPortalVBO;
        private readonly List<float> _connPortalVerts = new();
        private uint _neighborVAO;
        private uint _neighborVBO;
        private readonly List<float> _neighborBoxVerts = new();
        private uint _selConnLineVAO;
        private uint _selConnLineVBO;
        private readonly List<float> _selConnLineVerts = new();

        private readonly List<float> _selBoxVerts = new();
        private readonly List<float> _connLineVerts = new();
        private readonly List<float> _portalConnVerts = new();
        private readonly List<float> _portalOpenVerts = new();
        private readonly List<Vector3> _portalWorldVerts = new();
        private readonly List<Matrix4x4> _partTransformsBuffer = new();
        private readonly List<BatchDrawEntry> _staticDrawPlan = new();
        private struct BatchDrawEntry {
            public uint VAO;
            public List<RenderBatch> Batches;
            public int InstanceCount;
            public int BufferFloatOffset;
        }

        private static void WriteMatrixToBuffer(float[] buf, int offset, in Matrix4x4 m) {
            buf[offset +  0] = m.M11; buf[offset +  1] = m.M12; buf[offset +  2] = m.M13; buf[offset +  3] = m.M14;
            buf[offset +  4] = m.M21; buf[offset +  5] = m.M22; buf[offset +  6] = m.M23; buf[offset +  7] = m.M24;
            buf[offset +  8] = m.M31; buf[offset +  9] = m.M32; buf[offset + 10] = m.M33; buf[offset + 11] = m.M34;
            buf[offset + 12] = m.M41; buf[offset + 13] = m.M42; buf[offset + 14] = m.M43; buf[offset + 15] = m.M44;
        }
        private readonly List<float> _allOpenPortalVerts = new();
        private readonly List<float> _highlightedPortalVerts = new();
        private readonly List<float> _hoveredPortalVerts = new();
        private uint _highlightVAO;
        private uint _highlightVBO;
        private uint _hoverVAO;
        private uint _hoverVBO;
        private List<OpenPortalIndicator>? _cachedOpenPortalList;
        private ushort _cachedOpenHlCell;
        private ushort _cachedOpenHlPoly;
        private ushort _cachedOpenHoverCell;
        private ushort _cachedOpenHoverPoly;
        private bool _cachedOpenPlacement;
        private bool _openPortalUploaded;
        private int _openPortalDrawCount;
        private int _highlightDrawCount;
        private int _hoverDrawCount;
        private List<OpenPortalIndicator>? _cachedConnPortalList;
        private bool _connPortalUploaded;
        private int _connPortalDrawCount;

        private uint _gridVAO;
        private uint _gridVBO;
        private int _gridVertCount;
        private Vector3 _gridCenter;
        private bool _gridDirty = true;

        private uint _surfaceIndicatorVAO;
        private uint _surfaceIndicatorVBO;

        private ushort _loadedLandblockKey;
        private bool _hasLoadedCells;
        private int _docRefreshSerial;
        private readonly object _pendingRefreshLock = new();
        private PreparedEnvCellBatch? _pendingRefreshBatch;
        private DungeonDocument? _pendingRefreshDocument;
        private int _pendingRefreshSerial;

        public DungeonScene(IDatReaderWriter dats, WorldBuilderSettings settings) {
            _dats = dats;
            _settings = settings;

            var textureCacheDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "ACME WorldBuilder", "TextureCache", _dats.CacheNamespace);
            _textureCache = new TextureDiskCache(textureCacheDir);

            Camera = new PerspectiveCamera(Vector3.Zero, settings);
        }

        /// <summary>
        /// Initialize GPU resources on the GL thread when the renderer becomes available.
        /// Uses the same SceneContext pattern as the landscape editor.
        /// </summary>
        public void InitGpu(OpenGLRenderer renderer) {
            if (_gpuInitialized && _renderer == renderer) return;

            _sceneContext?.Dispose();
            _renderer = renderer;
            _sceneContext = new SceneContext(renderer, _dats, _textureCache);
            _sceneContext.EnvCellManager.ShowDungeonCells = true;
            _sceneContext.EnvCellManager.AlwaysShowBuildingInteriors = false;
            // Walk-as-player still uses collision, but neighboring rooms must stay
            // drawn through doorways so you can check that the frames line up.
            _sceneContext.EnvCellManager.UsePortalCulling = false;
            ThumbnailService = new ThumbnailRenderService(renderer, _sceneContext.ObjectManager);
            _gpuInitialized = true;
        }

        /// <summary>
        /// Load a landblock's dungeon cells. Unloads any previously loaded landblock.
        /// Must call ProcessUploads on the GL thread afterwards.
        /// </summary>
        public bool LoadLandblock(ushort landblockKey) {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null) return false;

            if (_hasLoadedCells) {
                ecm.QueueUnload(_loadedLandblockKey, releaseUnreferencedGpu: false);
                _hasLoadedCells = false;
            }

            uint lbId = landblockKey;
            var envCells = new List<EnvCell>();

            uint lbiId = (lbId << 16) | 0xFFFE;
            if (!_dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) {
                return false;
            }

            for (uint i = 0; i < lbi.NumCells; i++) {
                uint cellId = (lbId << 16) | (0x0100 + i);
                if (_dats.TryGet<EnvCell>(cellId, out var cell)) {
                    envCells.Add(cell);
                }
            }

            if (envCells.Count == 0) return false;

            var batch = ecm.PrepareLandblockEnvCells(landblockKey, lbId, envCells, isDungeonOnly: true);
            if (batch != null) {
                ecm.QueueForUpload(batch);
                IntegrateStatics(batch);
            }

            _loadedLandblockKey = landblockKey;
            ecm.FocusedDungeonLB = landblockKey;
            _hasLoadedCells = true;
            return true;
        }

        /// <summary>
        /// Reload rendering from a DungeonDocument's cell list.
        /// CPU mesh prep runs off the UI/GL thread; GPU upload happens on the next frames.
        /// </summary>
        public void RefreshFromDocument(DungeonDocument document) {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null) return;

            int serial = Interlocked.Increment(ref _docRefreshSerial);
            if (_hasLoadedCells) {
                ecm.QueueUnload(_loadedLandblockKey, releaseUnreferencedGpu: false);
                _hasLoadedCells = false;
            }

            var envCells = document.ToEnvCells();
            if (envCells.Count == 0) return;

            if (envCells.Count > 0) {
                var first = envCells[0];
                Console.WriteLine($"[DungeonScene] RefreshFromDocument: {envCells.Count} EnvCells, first has {first.Surfaces.Count} surfaces, EnvId=0x{first.EnvironmentId:X4}, Pos=({first.Position.Origin.X:F1},{first.Position.Origin.Y:F1},{first.Position.Origin.Z:F1})");
            }

            _loadedLandblockKey = document.LandblockKey;
            uint lbId = document.LandblockKey;
            ecm.FocusedDungeonLB = _loadedLandblockKey;

            Task.Run(() => {
                try {
                    var sw = Stopwatch.StartNew();
                    var batch = ecm.PrepareLandblockEnvCells(_loadedLandblockKey, lbId, envCells, isDungeonOnly: true);
                    if (serial != Volatile.Read(ref _docRefreshSerial)) return;
                    if (batch != null)
                        ecm.QueueForUpload(batch);
                    lock (_pendingRefreshLock) {
                        if (serial != _docRefreshSerial) return;
                        _pendingRefreshBatch = batch;
                        _pendingRefreshDocument = document;
                        _pendingRefreshSerial = serial;
                    }
                    if (sw.ElapsedMilliseconds > 16)
                        Console.WriteLine($"[DungeonScene] RefreshFromDocument prep {sw.ElapsedMilliseconds}ms ({envCells.Count} cells)");
                }
                catch (Exception ex) {
                    Console.WriteLine($"[DungeonScene] RefreshFromDocument failed: {ex.Message}");
                }
            });
        }

        private void ApplyPendingDocumentRefresh() {
            PreparedEnvCellBatch? batch;
            DungeonDocument? document;
            lock (_pendingRefreshLock) {
                if (_pendingRefreshBatch == null && _pendingRefreshDocument == null) return;
                if (_pendingRefreshSerial != Volatile.Read(ref _docRefreshSerial)) {
                    _pendingRefreshBatch = null;
                    _pendingRefreshDocument = null;
                    return;
                }
                batch = _pendingRefreshBatch;
                document = _pendingRefreshDocument;
                _pendingRefreshBatch = null;
                _pendingRefreshDocument = null;
            }

            if (batch != null)
                IntegrateStatics(batch);
            if (document != null) {
                ApplyDocumentScales(document);
                IntegrateInstancePlacements(document);
            }
            if (batch != null)
                _hasLoadedCells = true;
        }

        /// <summary>
        /// Upload only newly added cells without unloading the rest of the dungeon.
        /// </summary>
        public void AppendDocumentCells(DungeonDocument document, IReadOnlyList<ushort> newCellNums) {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null || newCellNums == null || newCellNums.Count == 0) return;
            if (!_hasLoadedCells) {
                RefreshFromDocument(document);
                return;
            }

            var want = new HashSet<ushort>(newCellNums);
            var envCells = document.ToEnvCells()
                .Where(c => want.Contains((ushort)(c.Id & 0xFFFF)))
                .ToList();
            if (envCells.Count == 0) return;

            uint lbId = document.LandblockKey;
            var batch = ecm.PrepareLandblockEnvCells(_loadedLandblockKey, lbId, envCells, isDungeonOnly: true);
            if (batch == null) return;
            batch.Append = true;
            ecm.QueueForUpload(batch);
            if (batch.DungeonStaticObjects.Count > 0)
                _dungeonStatics.AddRange(batch.DungeonStaticObjects);
        }

        /// <summary>
        /// Lightweight update for a single static object's transform during gizmo drag.
        /// Patches the render list in-place instead of rebuilding the entire scene,
        /// preventing the cell unload/reload cycle that causes geometry to vanish.
        /// </summary>
        public void UpdateStaticObjectTransform(DungeonDocument document, ushort cellNum, int objectIndex) {
            var cell = document.GetCell(cellNum);
            if (cell == null || objectIndex >= cell.StaticObjects.Count) return;

            var stab = cell.StaticObjects[objectIndex];
            var blockX = (_loadedLandblockKey >> 8) & 0xFF;
            var blockY = _loadedLandblockKey & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, EnvCellManager.DungeonDepthOffset);

            int staticIdx = 0;
            foreach (var dc in document.Cells) {
                for (int i = 0; i < dc.StaticObjects.Count; i++) {
                    if (dc.CellNumber == cellNum && i == objectIndex && staticIdx < _dungeonStatics.Count) {
                        var s = _dungeonStatics[staticIdx];
                        s.Origin = stab.Origin + lbOffset;
                        s.Orientation = stab.Orientation;
                        s.Scale = stab.Scale;
                        _dungeonStatics[staticIdx] = s;
                        return;
                    }
                    staticIdx++;
                }
            }
        }

        /// <summary>
        /// Apply per-object Scale from the document's DungeonStabData onto the rendered statics list.
        /// The DAT format doesn't carry scale, so we overlay it after building the statics from EnvCells.
        /// </summary>
        private void ApplyDocumentScales(DungeonDocument document) {
            var blockX = (_loadedLandblockKey >> 8) & 0xFF;
            var blockY = _loadedLandblockKey & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, EnvCellManager.DungeonDepthOffset);

            int staticIdx = 0;
            foreach (var dc in document.Cells) {
                foreach (var stab in dc.StaticObjects) {
                    if (staticIdx < _dungeonStatics.Count && stab.Scale != Vector3.One) {
                        var s = _dungeonStatics[staticIdx];
                        s.Scale = stab.Scale;
                        _dungeonStatics[staticIdx] = s;
                    }
                    staticIdx++;
                }
            }
        }

        private void UpdatePreviewLandblock(EnvCellManager ecm) {
            var previewCells = PreviewEnvCells;

            if (previewCells == null) return;

            if (previewCells.Count == 0) {
                if (_hasPreviewCells) {
                    ecm.UnloadLandblock(PreviewLandblockKey, releaseUnreferencedGpu: false);
                    _hasPreviewCells = false;
                }
                PreviewEnvCells = null;
                _previewIdentity = 0;
                _previewPose = 0;
                return;
            }

            int identity = PreviewIdentity(previewCells);
            int pose = PreviewPose(previewCells);

            if (_hasPreviewCells && identity == _previewIdentity) {
                if (pose != _previewPose) {
                    if (ecm.TryPatchLandblockTransforms(PreviewLandblockKey, _loadedLandblockKey, previewCells, isDungeonOnly: true)) {
                        _previewPose = pose;
                        PreviewEnvCells = null;
                        return;
                    }
                }
                else {
                    PreviewEnvCells = null;
                    return;
                }
            }

            if (_hasPreviewCells) {
                ecm.UnloadLandblock(PreviewLandblockKey, releaseUnreferencedGpu: false);
                _hasPreviewCells = false;
            }

            uint previewLbId = _loadedLandblockKey;
            var batch = ecm.PrepareLandblockEnvCells(PreviewLandblockKey, previewLbId, previewCells, isDungeonOnly: true);
            if (batch != null) {
                ecm.QueueForUpload(batch);
                _hasPreviewCells = true;
                _previewIdentity = identity;
                _previewPose = pose;
            }

            PreviewEnvCells = null;
        }

        private static int PreviewIdentity(List<EnvCell> cells) {
            var hash = new HashCode();
            hash.Add(cells.Count);
            foreach (var cell in cells) {
                hash.Add(cell.EnvironmentId);
                hash.Add(cell.CellStructure);
                hash.Add(cell.Surfaces.Count);
                foreach (var surface in cell.Surfaces) {
                    hash.Add(surface);
                }
            }
            return hash.ToHashCode();
        }

        private static int PreviewPose(List<EnvCell> cells) {
            var hash = new HashCode();
            foreach (var cell in cells) {
                var origin = cell.Position.Origin;
                var rot = cell.Position.Orientation;
                hash.Add(origin.X);
                hash.Add(origin.Y);
                hash.Add(origin.Z);
                hash.Add(rot.X);
                hash.Add(rot.Y);
                hash.Add(rot.Z);
                hash.Add(rot.W);
            }
            return hash.ToHashCode();
        }

        /// <summary>Remove preview cells (e.g. when placement is cancelled).</summary>
        public void ClearPreview() {
            PreviewEnvCells = new List<EnvCell>();
            if (_hasPreviewCells && _sceneContext?.EnvCellManager != null) {
                _sceneContext.EnvCellManager.QueueUnload(PreviewLandblockKey, releaseUnreferencedGpu: false);
                _hasPreviewCells = false;
            }
        }

        private void IntegrateStatics(PreparedEnvCellBatch batch) {
            _dungeonStatics.Clear();
            _objectGroupBuffer.Clear();
            _dungeonStatics.AddRange(batch.DungeonStaticObjects);

            if (_sceneContext == null) return;
            foreach (var obj in _dungeonStatics) {
                if (_sceneContext.ObjectManager.TryGetCachedRenderData(obj.Id) == null &&
                    !_sceneContext.ObjectManager.IsKnownFailure(obj.Id)) {
                    _sceneContext.ModelWarmupQueue.Enqueue((obj.Id, obj.IsSetup));
                }
            }
        }

        /// <summary>
        /// Register a WeenieClassId -> Setup DID mapping for rendering.
        /// Called by the ViewModel when weenies are loaded from the DB picker.
        /// </summary>
        public void CacheWeenieSetup(uint weenieClassId, uint setupId) {
            if (setupId != 0)
                _weenieSetupCache[weenieClassId] = setupId;
        }

        /// <summary>
        /// Returns the cached Setup DID for a given WeenieClassId, if available.
        /// Used by the raycast system to test bounds against instance placements.
        /// </summary>
        public bool TryGetWeenieSetupId(uint weenieClassId, out uint setupId) =>
            _weenieSetupCache.TryGetValue(weenieClassId, out setupId);

        /// <summary>
        /// Adds DungeonInstancePlacement entries (weenie/generator placements) to the
        /// render list so they appear in the viewport alongside cell statics.
        /// Uses the runtime WCID->SetupId cache for model lookup.
        /// Caps rendered instances to avoid performance issues in large dungeons.
        /// </summary>
        private void IntegrateInstancePlacements(DungeonDocument document) {
            if (_sceneContext == null) return;
            var blockX = (_loadedLandblockKey >> 8) & 0xFF;
            var blockY = _loadedLandblockKey & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, EnvCellManager.DungeonDepthOffset);

            int rendered = 0;
            const int maxRendered = 50;
            int newModelsQueued = 0;
            const int maxNewModels = 10;

            foreach (var p in document.InstancePlacements) {
                if (rendered >= maxRendered) break;
                if (!_weenieSetupCache.TryGetValue(p.WeenieClassId, out var setupId) || setupId == 0)
                    continue;
                bool isSetup = (setupId & 0x02000000) != 0;
                _dungeonStatics.Add(new StaticObject {
                    Id = setupId,
                    IsSetup = isSetup,
                    Origin = p.Origin + lbOffset,
                    Orientation = p.Orientation,
                    Scale = Vector3.One
                });
                rendered++;

                if (newModelsQueued < maxNewModels &&
                    _sceneContext.ObjectManager.TryGetCachedRenderData(setupId) == null &&
                    !_sceneContext.ObjectManager.IsKnownFailure(setupId)) {
                    _sceneContext.ModelWarmupQueue.Enqueue((setupId, isSetup));
                    newModelsQueued++;
                }
            }
        }

        /// <summary>
        /// Navigate the camera to a specific cell within the loaded dungeon.
        /// </summary>
        public void FocusCameraOnCell(ushort landblockKey, ushort cellId) {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null || !_hasLoadedCells) return;

            var cells = ecm.GetLoadedCellsForLandblock(landblockKey);
            if (cells == null || cells.Count == 0) {
                FocusCamera();
                return;
            }

            uint fullCellId = ((uint)landblockKey << 16) | cellId;
            var target = cells.FirstOrDefault(c => c.CellId == fullCellId);
            if (target == null) {
                FocusCamera();
                return;
            }

            Camera.SetPosition(target.WorldPosition + new Vector3(0, -15f, 8f));
            Camera.LookAt(target.WorldPosition);
        }

        public void FocusCameraOnSelection() {
            if (SelectedObjectPosition.HasValue) {
                Camera.SetPosition(SelectedObjectPosition.Value + new Vector3(0, -12f, 6f));
                Camera.LookAt(SelectedObjectPosition.Value);
                return;
            }
            if (SelectedCells != null && SelectedCells.Count > 0) {
                var center = Vector3.Zero;
                foreach (var c in SelectedCells) center += c.WorldPosition;
                center /= SelectedCells.Count;
                Camera.SetPosition(center + new Vector3(0, -15f, 8f));
                Camera.LookAt(center);
                return;
            }
            FocusCamera();
        }

        /// <summary>
        /// Navigate the camera to the center of the loaded dungeon cells.
        /// </summary>
        public void FocusCamera() {
            var center = GetDungeonCenter();
            if (center == null) return;

            Camera.SetPosition(center.Value + new Vector3(0, -20f, 10f));
            Camera.LookAt(center.Value);
        }

        /// <summary>
        /// Compute the centroid of all loaded dungeon cells (world-space).
        /// Returns null if no cells are loaded.
        /// </summary>
        public Vector3? GetDungeonCenter() {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null || !_hasLoadedCells) return null;

            var cells = ecm.GetLoadedCellsForLandblock(_loadedLandblockKey);
            if (cells == null || cells.Count == 0) return null;

            var center = Vector3.Zero;
            foreach (var cell in cells) {
                center += cell.WorldPosition;
            }
            return center / cells.Count;
        }

        public ushort LoadedLandblockKey => _loadedLandblockKey;

        public IReadOnlyList<LoadedEnvCell> GetLoadedCells() {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null || !_hasLoadedCells)
                return Array.Empty<LoadedEnvCell>();
            var loaded = ecm.GetLoadedCellsForLandblock(_loadedLandblockKey);
            if (loaded == null || loaded.Count == 0)
                return Array.Empty<LoadedEnvCell>();
            return loaded;
        }

        public void InvalidateGrid() => _gridDirty = true;

        /// <summary>
        /// Process pending GPU uploads and render the dungeon cells.
        /// Must be called on the GL thread.
        /// </summary>
        private int _diagFrame;

        public void Render(float aspectRatio) {
            var ecm = _sceneContext?.EnvCellManager;
            if (_renderer == null || ecm == null) return;

            var gl = _renderer.GraphicsDevice.GL;

            ecm.ProcessUploads(maxPerFrame: 8);

            // Explicitly set viewport to match camera screen size (FBO dimensions)
            int vpW = (int)Camera.ScreenSize.X;
            int vpH = (int)Camera.ScreenSize.Y;
            if (vpW > 0 && vpH > 0) {
                gl.Viewport(0, 0, (uint)vpW, (uint)vpH);
            }

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.ClearColor(0.05f, 0.03f, 0.08f, 1.0f);
            gl.ClearDepth(1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            Matrix4x4 view, projection;
            if (UseOrthographic) {
                var pos = Camera.Position;
                view = Matrix4x4.CreateLookAtLeftHanded(
                    new Vector3(pos.X, pos.Y, 500f),
                    new Vector3(pos.X, pos.Y, -500f),
                    new Vector3(0, -1, 0));
                projection = Matrix4x4.CreateOrthographicLeftHanded(
                    OrthoSize * aspectRatio, OrthoSize, 0.1f, 5000f);
            }
            else {
                view = Camera.GetViewMatrix();
                projection = Camera.GetProjectionMatrix();
            }
            var viewProjection = view * projection;

            if (++_diagFrame % 300 == 1 && _hasLoadedCells) {
                Console.WriteLine($"[DungeonScene] Cells: {ecm.LoadedCellCount}, " +
                    $"Statics: {_dungeonStatics.Count}, " +
                    $"ModelsQueued: {_sceneContext?.ModelWarmupQueue.Count ?? 0}, " +
                    $"ModelsPreparing: {_sceneContext?.ModelsPreparing.Count ?? 0}, " +
                    $"ModelsUploading: {_sceneContext?.ModelUploadQueue.Count ?? 0}");
            }

            var lightDir = Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f));
            float ambient = 0.4f;
            float specular = 16f;

            // Update textured placement preview (separate landblock)
            UpdatePreviewLandblock(ecm);
            ecm.ProcessUploads(maxPerFrame: 8);
            ApplyPendingDocumentRefresh();

            if (ShowGrid) {
                RenderGrid(gl, viewProjection);
            }

            ecm.Render(viewProjection, Camera, lightDir, ambient, specular);

            // Process model warmup/uploads for static objects
            ProcessModelUploads();

            // Render static objects (torches, furniture, etc.)
            if (_dungeonStatics.Count > 0) {
                RenderStaticObjects(gl, viewProjection);
            }

            // Render open portal highlights on ALL cells (green quads showing where you can attach)
            if (ShowPortalIndicators && OpenPortalIndicators.Count > 0) {
                RenderAllOpenPortals(gl, viewProjection);
            }

            // Render connected portal highlights on ALL cells (blue outlines)
            if (ShowPortalIndicators && ConnectedPortalIndicators.Count > 0) {
                RenderConnectedPortals(gl, viewProjection);
            }

            // Render portal indicators on primary selected cell (detailed)
            if (SelectedCell != null) {
                RenderPortalIndicators(gl, viewProjection);
            }

            // Render selection highlight on all selected cells
            if (SelectedCells != null && SelectedCells.Count > 0) {
                RenderSelectionBoxes(gl, viewProjection);

                if (!SelectedObjectPosition.HasValue && Gizmo != null) {
                    var center = Vector3.Zero;
                    foreach (var c in SelectedCells) center += c.WorldPosition;
                    center /= SelectedCells.Count;
                    Gizmo.UseLocalSpace = false;
                    Gizmo.Render(gl, viewProjection, Camera, center, Quaternion.Identity, true);
                }
            }

            // Render connected neighbor highlights (subtler color)
            if (ConnectedNeighborCells != null && ConnectedNeighborCells.Count > 0) {
                RenderNeighborBoxes(gl, viewProjection);
            }

            // Render selected object highlight + gizmo
            if (SelectedObjectBounds.HasValue) {
                RenderObjectSelectionBox(gl, viewProjection, SelectedObjectBounds.Value.Min, SelectedObjectBounds.Value.Max);
            }
            if (SelectedObjectPosition.HasValue && Gizmo != null) {
                Gizmo.UseLocalSpace = _settings.Landscape.Snap.UseLocalSpace;
                Gizmo.Render(gl, viewProjection, Camera, SelectedObjectPosition.Value, SelectedObjectOrientation, true);
                if (SelectedObjectBounds.HasValue) {
                    Gizmo.RenderSelectionBox(gl, viewProjection, Camera,
                        SelectedObjectBounds.Value.Min, SelectedObjectBounds.Value.Max,
                        new Vector3(1f, 0.6f, 0.15f));
                }
            }

            // Render hovered object highlight (before placement so it's visible under ghost)
            if (HoveredObjectPosition.HasValue && !PlacementPreview.HasValue) {
                RenderHoveredObjectHighlight(gl, viewProjection);
            }

            // Render placement preview (ghost object following mouse)
            if (PlacementPreview.HasValue) {
                RenderPlacementPreview(gl, viewProjection, PlacementPreview.Value);
            }

            // Render surface indicator (oriented disc at cursor position)
            if (SurfaceIndicatorPosition.HasValue) {
                RenderSurfaceIndicator(gl, viewProjection);
            }

            // Render room placement preview (wireframe ghost)
            if (RoomPlacementPreview.HasValue) {
                RenderRoomPlacementPreview(gl, viewProjection, RoomPlacementPreview.Value);
            }

            // Render connection lines between cells (shows what connects to what)
            if (ShowConnectionLines && ConnectionLines != null && ConnectionLines.Count > 0) {
                RenderConnectionLines(gl, viewProjection);
            }

            // Render highlighted connection lines for the selected cell (brighter)
            if (ShowConnectionLines && SelectedConnectionLines != null && SelectedConnectionLines.Count > 0) {
                RenderSelectedConnectionLines(gl, viewProjection);
            }

            ThumbnailService?.ProcessQueue(_renderer);
        }

        private void ProcessModelUploads() {
            if (_sceneContext == null) return;
            var objectManager = _sceneContext.ObjectManager;

            int uploaded = 0;
            while (uploaded < 16 && _sceneContext.ModelUploadQueue.TryDequeue(out var preparedModel)) {
                var renderData = objectManager.FinalizeGpuUpload(preparedModel);
                uploaded++;

                // Setup objects are containers that reference GfxObj parts.
                // The Setup itself has no geometry -- each part must be separately
                // prepared and uploaded. Queue any parts that aren't cached yet.
                if (renderData?.IsSetup == true && renderData.SetupParts != null) {
                    foreach (var (partId, _) in renderData.SetupParts) {
                        if (objectManager.TryGetCachedRenderData(partId) == null &&
                            !objectManager.IsKnownFailure(partId) &&
                            !_sceneContext.ModelsPreparing.Contains(partId)) {
                            _sceneContext.ModelWarmupQueue.Enqueue((partId, false));
                        }
                    }
                }
            }

            int warmupCount = 0;
            while (warmupCount < 16 && _sceneContext.ModelWarmupQueue.Count > 0) {
                var (id, isSetup) = _sceneContext.ModelWarmupQueue.Dequeue();
                if (objectManager.TryGetCachedRenderData(id) != null) continue;
                if (!_sceneContext.ModelsPreparing.Add(id)) continue;

                var localId = id;
                var localIsSetup = isSetup;
                var localCtx = _sceneContext;
                System.Threading.Tasks.Task.Run(() => {
                    var prepared = objectManager.PrepareModelData(localId, localIsSetup);
                    if (prepared != null) {
                        localCtx.ModelUploadQueue.Enqueue(prepared);
                    }
                    localCtx.ModelsPreparing.Remove(localId);
                });
                warmupCount++;
            }
        }

        private unsafe void RenderStaticObjects(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || _dungeonStatics.Count == 0) return;
            var objectManager = _sceneContext.ObjectManager;

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            objectManager._objectShader.Bind();
            objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            objectManager._objectShader.SetUniform("uCameraPosition", Camera.Position);
            objectManager._objectShader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            objectManager._objectShader.SetUniform("uAmbientIntensity", 0.4f);
            objectManager._objectShader.SetUniform("uSpecularPower", 16f);
            objectManager._objectShader.SetUniform("uHighlightColor", Vector3.Zero);
            objectManager._objectShader.SetUniform("uHighlightIntensity", 0f);

            foreach (var list in _objectGroupBuffer.Values) list.Clear();

            var frustum = new Frustum(viewProjection);

            foreach (var obj in _dungeonStatics) {
                var worldTransform =
                    Matrix4x4.CreateScale(obj.Scale)
                    * Matrix4x4.CreateFromQuaternion(obj.Orientation)
                    * Matrix4x4.CreateTranslation(obj.Origin);
                var localBounds = objectManager.GetBounds(obj.Id, obj.IsSetup);
                BoundingBox objBounds;
                if (localBounds.HasValue) {
                    var (localMin, localMax) = localBounds.Value;
                    var worldMin = new Vector3(float.MaxValue);
                    var worldMax = new Vector3(float.MinValue);
                    for (int ci = 0; ci < 8; ci++) {
                        var corner = new Vector3(
                            (ci & 1) == 0 ? localMin.X : localMax.X,
                            (ci & 2) == 0 ? localMin.Y : localMax.Y,
                            (ci & 4) == 0 ? localMin.Z : localMax.Z);
                        var worldCorner = Vector3.Transform(corner, worldTransform);
                        worldMin = Vector3.Min(worldMin, worldCorner);
                        worldMax = Vector3.Max(worldMax, worldCorner);
                    }
                    objBounds = new BoundingBox(worldMin, worldMax);
                }
                else {
                    const float fallbackRadius = 8f;
                    objBounds = new BoundingBox(
                        obj.Origin - new Vector3(fallbackRadius),
                        obj.Origin + new Vector3(fallbackRadius));
                }
                if (!frustum.IntersectsBoundingBox(objBounds)) continue;

                var key = (obj.Id, obj.IsSetup);
                if (!_objectGroupBuffer.TryGetValue(key, out var list)) {
                    list = new List<Matrix4x4>();
                    _objectGroupBuffer[key] = list;
                }
                list.Add(worldTransform);
            }

            _staticDrawPlan.Clear();
            int totalFloats = 0;

            foreach (var group in _objectGroupBuffer) {
                if (group.Value.Count == 0) continue;
                var (id, isSetup) = group.Key;

                var renderData = objectManager.TryGetCachedRenderData(id);
                if (renderData == null) continue;

                if (isSetup && renderData.SetupParts != null) {
                    foreach (var (partId, partTransform) in renderData.SetupParts) {
                        var partRenderData = objectManager.TryGetCachedRenderData(partId);
                        if (partRenderData == null) continue;

                        int offset = totalFloats;
                        int needed = totalFloats + group.Value.Count * 16;
                        if (_sceneContext.InstanceUploadBuffer.Length < needed) {
                            int newSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(needed, 256));
                            _sceneContext.InstanceUploadBuffer = new float[newSize];
                        }

                        foreach (var instanceMatrix in group.Value) {
                            var m = partTransform * instanceMatrix;
                            WriteMatrixToBuffer(_sceneContext.InstanceUploadBuffer, totalFloats, in m);
                            totalFloats += 16;
                        }

                        _staticDrawPlan.Add(new BatchDrawEntry {
                            VAO = partRenderData.VAO,
                            Batches = partRenderData.Batches,
                            InstanceCount = group.Value.Count,
                            BufferFloatOffset = offset
                        });
                    }
                }
                else {
                    int offset = totalFloats;
                    int needed = totalFloats + group.Value.Count * 16;
                    if (_sceneContext.InstanceUploadBuffer.Length < needed) {
                        int newSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(needed, 256));
                        _sceneContext.InstanceUploadBuffer = new float[newSize];
                    }

                    foreach (var m in group.Value) {
                        WriteMatrixToBuffer(_sceneContext.InstanceUploadBuffer, totalFloats, in m);
                        totalFloats += 16;
                    }

                    _staticDrawPlan.Add(new BatchDrawEntry {
                        VAO = renderData.VAO,
                        Batches = renderData.Batches,
                        InstanceCount = group.Value.Count,
                        BufferFloatOffset = offset
                    });
                }
            }

            if (totalFloats == 0) {
                gl.UseProgram(0);
                gl.Disable(EnableCap.Blend);
                return;
            }

            if (_sceneContext.InstanceVBO == 0) {
                gl.GenBuffers(1, out uint vbo);
                _sceneContext.InstanceVBO = vbo;
            }

            gl.BindBuffer(GLEnum.ArrayBuffer, _sceneContext.InstanceVBO);
            fixed (float* ptr = _sceneContext.InstanceUploadBuffer) {
                if (totalFloats > _sceneContext.InstanceBufferCapacity) {
                    int newCapacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(totalFloats, 256));
                    _sceneContext.InstanceBufferCapacity = newCapacity;
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(newCapacity * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
                else {
                    gl.BufferSubData(GLEnum.ArrayBuffer, 0, (nuint)(totalFloats * sizeof(float)), ptr);
                }
            }

            bool cullFaceEnabled = true;
            foreach (var entry in _staticDrawPlan) {
                gl.BindVertexArray(entry.VAO);

                int byteOffset = entry.BufferFloatOffset * sizeof(float);
                gl.BindBuffer(GLEnum.ArrayBuffer, _sceneContext.InstanceVBO);
                for (int i = 0; i < 4; i++) {
                    gl.EnableVertexAttribArray((uint)(3 + i));
                    gl.VertexAttribPointer((uint)(3 + i), 4, GLEnum.Float, false,
                        (uint)(16 * sizeof(float)), (void*)(byteOffset + i * 4 * sizeof(float)));
                    gl.VertexAttribDivisor((uint)(3 + i), 1);
                }

                foreach (var batch in entry.Batches) {
                    if (batch.TextureArray == null) continue;

                    if (batch.IsDoubleSided && cullFaceEnabled) {
                        gl.Disable(EnableCap.CullFace);
                        cullFaceEnabled = false;
                    }
                    else if (!batch.IsDoubleSided && !cullFaceEnabled) {
                        gl.Enable(EnableCap.CullFace);
                        cullFaceEnabled = true;
                    }

                    batch.TextureArray.Bind(0);
                    objectManager._objectShader.SetUniform("uTextureArray", 0);
                    objectManager._objectShader.SetUniform("uTextureIndex", (float)batch.TextureIndex);
                    gl.DisableVertexAttribArray(7);
                    gl.VertexAttrib1((uint)7, (float)batch.TextureIndex);

                    gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                    gl.DrawElementsInstanced(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null, (uint)entry.InstanceCount);
                }
            }

            if (!cullFaceEnabled) gl.Enable(EnableCap.CullFace);
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Disable(EnableCap.Blend);
        }

        private unsafe void RenderObjectSelectionBox(GL gl, Matrix4x4 viewProjection, Vector3 worldMin, Vector3 worldMax) {
            if (_sceneContext == null) return;
            if (worldMin.X >= worldMax.X || worldMin.Y >= worldMax.Y || worldMin.Z >= worldMax.Z) return;

            // Ensure clean GL state before using SphereShader
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            while (gl.GetError() != GLEnum.NoError) { }

            Vector3[] c = new Vector3[8];
            c[0] = new Vector3(worldMin.X, worldMin.Y, worldMin.Z);
            c[1] = new Vector3(worldMax.X, worldMin.Y, worldMin.Z);
            c[2] = new Vector3(worldMax.X, worldMax.Y, worldMin.Z);
            c[3] = new Vector3(worldMin.X, worldMax.Y, worldMin.Z);
            c[4] = new Vector3(worldMin.X, worldMin.Y, worldMax.Z);
            c[5] = new Vector3(worldMax.X, worldMin.Y, worldMax.Z);
            c[6] = new Vector3(worldMax.X, worldMax.Y, worldMax.Z);
            c[7] = new Vector3(worldMin.X, worldMax.Y, worldMax.Z);

            int[][] edges = { new[]{0,1}, new[]{1,2}, new[]{2,3}, new[]{3,0},
                              new[]{4,5}, new[]{5,6}, new[]{6,7}, new[]{7,4},
                              new[]{0,4}, new[]{1,5}, new[]{2,6}, new[]{3,7} };

            const float lineWidth = 0.08f;
            var camPos = Camera.Position;
            var verts = new List<float>();

            foreach (var e in edges) {
                var a = c[e[0]];
                var b = c[e[1]];
                var edgeDir = Vector3.Normalize(b - a);
                var midpoint = (a + b) * 0.5f;
                var toCamera = Vector3.Normalize(camPos - midpoint);
                var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * lineWidth;
                var normal = toCamera;
                var a0 = a - sideDir; var a1 = a + sideDir;
                var b0 = b - sideDir; var b1 = b + sideDir;

                void V(Vector3 p) { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); verts.Add(normal.X); verts.Add(normal.Y); verts.Add(normal.Z); }
                V(a0); V(b0); V(b1);
                V(a0); V(b1); V(a1);
            }

            int vertCount = verts.Count / 6;
            var data = CollectionsMarshal.AsSpan(verts);

            if (_objSelVAO == 0) {
                gl.GenVertexArrays(1, out _objSelVAO);
                gl.GenBuffers(1, out _objSelVBO);
            }

            gl.BindVertexArray(_objSelVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _objSelVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.DisableVertexAttribArray(2);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            shader.SetUniform("uSphereColor", new Vector3(1.0f, 0.7f, 0.2f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
            shader.SetUniform("uGlowIntensity", 0f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RenderPlacementPreview(GL gl, Matrix4x4 viewProjection, StaticObject previewObj) {
            if (_sceneContext == null) return;
            var objectManager = _sceneContext.ObjectManager;

            var renderData = objectManager.TryGetCachedRenderData(previewObj.Id);
            if (renderData == null) return;

            if (renderData.IsSetup && renderData.SetupParts != null) {
                foreach (var (partId, _) in renderData.SetupParts) {
                    objectManager.GetRenderData(partId, false);
                }
            }

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);
            gl.DepthMask(false);

            var tintColor = PlacementPreviewValid ? new Vector3(0.3f, 0.8f, 1.0f) : new Vector3(1.0f, 0.2f, 0.2f);
            float tintIntensity = 0.4f;

            objectManager._objectShader.Bind();
            objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            objectManager._objectShader.SetUniform("uCameraPosition", Camera.Position);
            objectManager._objectShader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            objectManager._objectShader.SetUniform("uAmbientIntensity", 0.8f);
            objectManager._objectShader.SetUniform("uSpecularPower", 4f);
            objectManager._objectShader.SetUniform("uHighlightColor", tintColor);
            objectManager._objectShader.SetUniform("uHighlightIntensity", tintIntensity);

            var worldMatrix = Matrix4x4.CreateScale(previewObj.Scale)
                * Matrix4x4.CreateFromQuaternion(previewObj.Orientation)
                * Matrix4x4.CreateTranslation(previewObj.Origin);

            if (renderData.IsSetup && renderData.SetupParts != null) {
                foreach (var (partId, partTransform) in renderData.SetupParts) {
                    var partRenderData = objectManager.TryGetCachedRenderData(partId);
                    if (partRenderData == null) continue;
                    RenderBatchedObject(gl, partRenderData, new List<Matrix4x4> { partTransform * worldMatrix });
                }
            }
            else {
                RenderBatchedObject(gl, renderData, new List<Matrix4x4> { worldMatrix });
            }

            objectManager._objectShader.SetUniform("uHighlightColor", Vector3.Zero);
            objectManager._objectShader.SetUniform("uHighlightIntensity", 0f);
            gl.DepthMask(true);
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Enable(EnableCap.CullFace);
        }

        private unsafe void RenderSurfaceIndicator(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || !SurfaceIndicatorPosition.HasValue) return;

            var center = SurfaceIndicatorPosition.Value;
            var normal = SurfaceIndicatorNormal;
            if (normal.LengthSquared() < 1e-6f) normal = Vector3.UnitZ;
            normal = Vector3.Normalize(normal);

            float absZ = MathF.Abs(Vector3.Dot(normal, Vector3.UnitZ));
            Vector3 color;
            if (absZ > 0.7f) {
                color = normal.Z > 0 ? new Vector3(0.2f, 0.9f, 0.3f) : new Vector3(0.9f, 0.2f, 0.2f);
            } else {
                color = new Vector3(0.3f, 0.6f, 1.0f);
            }

            const float radius = 0.6f;
            const int segments = 24;

            var perp1 = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) < 0.9f
                ? Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY))
                : Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitX));
            var perp2 = Vector3.Normalize(Vector3.Cross(normal, perp1));

            var verts = new List<float>();
            var camPos = Camera.Position;
            var n = Vector3.Normalize(camPos - center);

            for (int i = 0; i < segments; i++) {
                float a1 = 2f * MathF.PI * i / segments;
                float a2 = 2f * MathF.PI * ((i + 1) % segments) / segments;

                var p0 = center + normal * 0.02f;
                var p1 = center + (perp1 * MathF.Cos(a1) + perp2 * MathF.Sin(a1)) * radius + normal * 0.02f;
                var p2 = center + (perp1 * MathF.Cos(a2) + perp2 * MathF.Sin(a2)) * radius + normal * 0.02f;

                void V(Vector3 p) { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z); }
                V(p0); V(p1); V(p2);
            }

            // Normal arrow (small line from center along normal)
            float arrowLen = 1.0f;
            var arrowEnd = center + normal * arrowLen;
            float arrowW = 0.04f;
            var toCamera = Vector3.Normalize(camPos - center);
            var arrowSide = Vector3.Cross(normal, toCamera);
            if (arrowSide.LengthSquared() < 1e-6f) arrowSide = Vector3.Cross(normal, Vector3.UnitX);
            arrowSide = Vector3.Normalize(arrowSide) * arrowW;

            void AV(Vector3 p) { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z); }
            AV(center - arrowSide + normal * 0.02f); AV(arrowEnd - arrowSide); AV(arrowEnd + arrowSide);
            AV(center - arrowSide + normal * 0.02f); AV(arrowEnd + arrowSide); AV(center + arrowSide + normal * 0.02f);

            int vertCount = verts.Count / 6;
            var data = CollectionsMarshal.AsSpan(verts);

            if (_surfaceIndicatorVAO == 0) {
                gl.GenVertexArrays(1, out _surfaceIndicatorVAO);
                gl.GenBuffers(1, out _surfaceIndicatorVBO);
            }

            gl.BindVertexArray(_surfaceIndicatorVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _surfaceIndicatorVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", Camera.Position);
            shader.SetUniform("uSphereColor", color);
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", color * 0.4f);
            shader.SetUniform("uGlowIntensity", 0.3f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RenderHoveredObjectHighlight(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || !HoveredObjectPosition.HasValue) return;
            var objectManager = _sceneContext.ObjectManager;

            var renderData = objectManager.TryGetCachedRenderData(HoveredObjectId);
            if (renderData == null) return;

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);

            objectManager._objectShader.Bind();
            objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            objectManager._objectShader.SetUniform("uCameraPosition", Camera.Position);
            objectManager._objectShader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            objectManager._objectShader.SetUniform("uAmbientIntensity", 0.5f);
            objectManager._objectShader.SetUniform("uSpecularPower", 8f);
            objectManager._objectShader.SetUniform("uHighlightColor", new Vector3(0.4f, 0.7f, 1.0f));
            objectManager._objectShader.SetUniform("uHighlightIntensity", 0.8f);

            var worldMatrix = Matrix4x4.CreateScale(HoveredObjectScale)
                * Matrix4x4.CreateFromQuaternion(HoveredObjectOrientation)
                * Matrix4x4.CreateTranslation(HoveredObjectPosition.Value);

            if (renderData.IsSetup && renderData.SetupParts != null) {
                foreach (var (partId, partTransform) in renderData.SetupParts) {
                    var partRenderData = objectManager.TryGetCachedRenderData(partId);
                    if (partRenderData == null) continue;
                    RenderBatchedObject(gl, partRenderData, new List<Matrix4x4> { partTransform * worldMatrix });
                }
            }
            else {
                RenderBatchedObject(gl, renderData, new List<Matrix4x4> { worldMatrix });
            }

            objectManager._objectShader.SetUniform("uHighlightColor", Vector3.Zero);
            objectManager._objectShader.SetUniform("uHighlightIntensity", 0f);
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Enable(EnableCap.CullFace);
        }

        private unsafe void RenderRoomPlacementPreview(GL gl, Matrix4x4 viewProjection, RoomPlacementPreviewData preview) {
            if (_sceneContext == null) return;
            if (!_dats.TryGet<Acme.Dat.Environment>(preview.EnvFileId, out var env)) return;
            if (!env.Cells.TryGetValue(preview.CellStructIndex, out var cellStruct)) return;
            if (cellStruct.VertexArray?.Vertices == null || cellStruct.VertexArray.Vertices.Count == 0) return;

            // Render the actual room polygons as semi-transparent cyan
            var transform = Matrix4x4.CreateFromQuaternion(preview.Orientation) * Matrix4x4.CreateTranslation(preview.Origin);
            var camPos = Camera.Position;
            var verts = new List<float>();

            foreach (var kvp in cellStruct.Polygons) {
                var poly = kvp.Value;
                if (poly.VertexIds.Count < 3) continue;

                var pts = new List<Vector3>();
                foreach (var vid in poly.VertexIds) {
                    if (cellStruct.VertexArray.Vertices.TryGetValue((ushort)vid, out var vtx))
                        pts.Add(Vector3.Transform(vtx.Origin, transform));
                }
                if (pts.Count < 3) continue;

                var edge1 = pts[1] - pts[0];
                var edge2 = pts[2] - pts[0];
                var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));

                for (int i = 1; i < pts.Count - 1; i++) {
                    void V(Vector3 p) { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); verts.Add(normal.X); verts.Add(normal.Y); verts.Add(normal.Z); }
                    V(pts[0]); V(pts[i]); V(pts[i + 1]);
                }
            }

            if (verts.Count == 0) return;
            int vertCount = verts.Count / 6;
            var data = CollectionsMarshal.AsSpan(verts);

            if (_lineVAO == 0) {
                gl.GenVertexArrays(1, out _lineVAO);
                gl.GenBuffers(1, out _lineVBO);
            }

            gl.BindVertexArray(_lineVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _lineVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            var previewColor = PreviewIsSnapped ? new Vector3(0.1f, 0.9f, 0.3f) : new Vector3(0.9f, 0.4f, 0.1f);
            var glowColor = PreviewIsSnapped ? new Vector3(0f, 0.4f, 0.15f) : new Vector3(0.4f, 0.1f, 0f);
            shader.SetUniform("uSphereColor", previewColor);
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 0.8f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", glowColor);
            shader.SetUniform("uGlowIntensity", 0.5f);
            shader.SetUniform("uGlowPower", 2.0f);

            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);
            gl.DepthMask(false);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
            gl.DepthMask(true);
            gl.Enable(EnableCap.CullFace);
            gl.Disable(EnableCap.Blend);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        /// <summary>
        /// Queue a model for GPU warmup so it's ready to render as a preview.
        /// </summary>
        public void WarmupModel(uint id, bool isSetup) {
            if (_sceneContext == null) return;
            if (_sceneContext.ObjectManager.TryGetCachedRenderData(id) != null) return;
            if (_sceneContext.ObjectManager.IsKnownFailure(id)) return;
            _sceneContext.ModelWarmupQueue.Enqueue((id, isSetup));
        }

        public (Vector3 Min, Vector3 Max)? GetObjectBounds(uint id, bool isSetup) {
            return _sceneContext?.ObjectManager?.GetBounds(id, isSetup);
        }

        private unsafe void RenderPortalIndicators(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || SelectedCell == null) return;

            uint envFileId = SelectedCell.EnvironmentId;
            if (!_dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) return;

            var gpuKey = SelectedCell.GpuKey;
            ushort cellStructIdx = (ushort)gpuKey.CellStructure;
            if (!env.Cells.TryGetValue(cellStructIdx, out var cellStruct)) return;

            var portalIds = PortalSnapper.GetPortalPolygonIds(cellStruct);
            if (portalIds.Count == 0) return;

            var connectedPolys = new HashSet<ushort>();
            foreach (var p in SelectedCell.Portals) {
                connectedPolys.Add(p.PolygonId);
            }

            var transform = SelectedCell.WorldTransform;
            var camPos = Camera.Position;
            const float portalLineWidth = 0.06f;

            void AddPortalEdges(List<float> verts, ushort polyId) {
                if (!cellStruct.Polygons.TryGetValue(polyId, out var poly)) return;
                if (poly.VertexIds.Count < 3) return;

                _portalWorldVerts.Clear();
                foreach (var vid in poly.VertexIds) {
                    if (cellStruct.VertexArray.Vertices.TryGetValue((ushort)vid, out var vtx)) {
                        _portalWorldVerts.Add(Vector3.Transform(vtx.Origin, transform));
                    }
                }
                if (_portalWorldVerts.Count < 3) return;

                var polyNormal = Vector3.Zero;
                if (_portalWorldVerts.Count >= 3) {
                    var e1 = _portalWorldVerts[1] - _portalWorldVerts[0];
                    var e2 = _portalWorldVerts[2] - _portalWorldVerts[0];
                    polyNormal = Vector3.Cross(e1, e2);
                    if (polyNormal.LengthSquared() > 1e-8f) polyNormal = Vector3.Normalize(polyNormal);
                }

                for (int i = 0; i < _portalWorldVerts.Count; i++) {
                    int next = (i + 1) % _portalWorldVerts.Count;
                    var a = _portalWorldVerts[i];
                    var b = _portalWorldVerts[next];
                    var edgeDir = b - a;
                    if (edgeDir.LengthSquared() < 1e-8f) continue;
                    edgeDir = Vector3.Normalize(edgeDir);
                    var mid = (a + b) * 0.5f;
                    var toCamera = Vector3.Normalize(camPos - mid);
                    var cross = Vector3.Cross(edgeDir, toCamera);
                    var sideDir = cross.LengthSquared() > 1e-6f
                        ? Vector3.Normalize(cross) * portalLineWidth
                        : (polyNormal.LengthSquared() > 0.5f
                            ? Vector3.Normalize(Vector3.Cross(edgeDir, polyNormal)) * portalLineWidth
                            : Vector3.Normalize(Vector3.Cross(edgeDir, Vector3.UnitZ)) * portalLineWidth);
                    var n = toCamera;
                    void PV(Vector3 p) { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z); }
                    PV(a - sideDir); PV(b - sideDir); PV(b + sideDir);
                    PV(a - sideDir); PV(b + sideDir); PV(a + sideDir);
                }
            }

            _portalConnVerts.Clear();
            _portalOpenVerts.Clear();
            foreach (var polyId in portalIds) {
                if (connectedPolys.Contains(polyId))
                    AddPortalEdges(_portalConnVerts, polyId);
                else
                    AddPortalEdges(_portalOpenVerts, polyId);
            }

            void DrawPortalVerts(List<float> verts, ref uint cachedVao, ref uint cachedVbo, Vector3 color) {
                if (verts.Count == 0) return;
                int vertCount = verts.Count / 6;
                var data = CollectionsMarshal.AsSpan(verts);

                if (cachedVao == 0) {
                    gl.GenVertexArrays(1, out cachedVao);
                    gl.GenBuffers(1, out cachedVbo);
                }
                gl.BindVertexArray(cachedVao);
                gl.BindBuffer(GLEnum.ArrayBuffer, cachedVbo);
                fixed (float* ptr = data) {
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
                for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
                gl.DisableVertexAttribArray(2);
                gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

                var shader = _sceneContext.SphereShader;
                shader.Bind();
                shader.SetUniform("uViewProjection", viewProjection);
                shader.SetUniform("uCameraPosition", Camera.Position);
                shader.SetUniform("uSphereColor", color);
                shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
                shader.SetUniform("uAmbientIntensity", 1.0f);
                shader.SetUniform("uSpecularPower", 0f);
                shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
                shader.SetUniform("uGlowIntensity", 0f);
                shader.SetUniform("uGlowPower", 1.0f);

                gl.Disable(EnableCap.DepthTest);
                gl.Disable(EnableCap.CullFace);
                gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
                gl.Enable(EnableCap.DepthTest);
                gl.Enable(EnableCap.CullFace);

                gl.BindVertexArray(0);
                gl.UseProgram(0);
            }

            if (_portalConnVerts.Count == 0 && _portalOpenVerts.Count == 0) return;

            DrawPortalVerts(_portalConnVerts, ref _portalConnVAO, ref _portalConnVBO, new Vector3(0.3f, 0.5f, 1.0f));
            DrawPortalVerts(_portalOpenVerts, ref _portalOpenVAO, ref _portalOpenVBO, new Vector3(0.2f, 0.9f, 0.3f));
        }

        /// <summary>
        /// Render open portals as bright glowing doorway outlines (not filled quads).
        /// Geometry is cached until the doorway list or highlight changes so camera
        /// motion does not rebuild and re-upload every frame.
        /// </summary>
        private unsafe void RenderAllOpenPortals(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || OpenPortalIndicators.Count == 0) return;

            bool rebuild = !_openPortalUploaded
                || !ReferenceEquals(_cachedOpenPortalList, OpenPortalIndicators)
                || _cachedOpenHlCell != HighlightedPortalCellNum
                || _cachedOpenHlPoly != HighlightedPortalPolyId
                || _cachedOpenHoverCell != HoveredPortalCellNum
                || _cachedOpenHoverPoly != HoveredPortalPolyId
                || _cachedOpenPlacement != IsInPlacementMode;

            if (rebuild) {
            _allOpenPortalVerts.Clear();
            _highlightedPortalVerts.Clear();
            _hoveredPortalVerts.Clear();
                float lineWidth = IsInPlacementMode ? 0.32f : 0.16f;
                float highlightWidth = 0.55f;
            bool hasHighlight = HighlightedPortalCellNum != 0 || HighlightedPortalPolyId != 0;
            bool hasHover = HoveredPortalCellNum != 0 || HoveredPortalPolyId != 0;

            for (int i = 0; i < OpenPortalIndicators.Count; i++) {
                var indicator = OpenPortalIndicators[i];
                if (indicator.WorldVertices == null || indicator.WorldVertices.Length < 3) continue;

                bool isHighlighted = hasHighlight
                    && indicator.CellNum == HighlightedPortalCellNum
                    && indicator.PolyId == HighlightedPortalPolyId;
                bool isHovered = hasHover
                    && !isHighlighted
                    && indicator.CellNum == HoveredPortalCellNum
                    && indicator.PolyId == HoveredPortalPolyId;
                var targetVerts = isHighlighted ? _highlightedPortalVerts
                    : isHovered ? _hoveredPortalVerts
                    : _allOpenPortalVerts;
                float w = isHighlighted || isHovered ? highlightWidth : lineWidth;
                    AddDoorframe(targetVerts, indicator.WorldVertices, indicator.Normal, w, fill: isHighlighted || isHovered);
                }

                _cachedOpenPortalList = OpenPortalIndicators;
                _cachedOpenHlCell = HighlightedPortalCellNum;
                _cachedOpenHlPoly = HighlightedPortalPolyId;
                _cachedOpenHoverCell = HoveredPortalCellNum;
                _cachedOpenHoverPoly = HoveredPortalPolyId;
                _cachedOpenPlacement = IsInPlacementMode;
                _openPortalDrawCount = _allOpenPortalVerts.Count / 6;
                _highlightDrawCount = _highlightedPortalVerts.Count / 6;
                _hoverDrawCount = _hoveredPortalVerts.Count / 6;

                if (_openPortalDrawCount > 0) {
                if (_allOpenVAO == 0) {
                    gl.GenVertexArrays(1, out _allOpenVAO);
                    gl.GenBuffers(1, out _allOpenVBO);
                }
                gl.BindVertexArray(_allOpenVAO);
                gl.BindBuffer(GLEnum.ArrayBuffer, _allOpenVBO);
                    var data = CollectionsMarshal.AsSpan(_allOpenPortalVerts);
                fixed (float* ptr = data) {
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
                for (uint a = 2; a < 8; a++) gl.DisableVertexAttribArray(a);
                gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);
                }

                if (_highlightDrawCount > 0) {
                if (_highlightVAO == 0) {
                    gl.GenVertexArrays(1, out _highlightVAO);
                    gl.GenBuffers(1, out _highlightVBO);
                }
                gl.BindVertexArray(_highlightVAO);
                gl.BindBuffer(GLEnum.ArrayBuffer, _highlightVBO);
                    var hData = CollectionsMarshal.AsSpan(_highlightedPortalVerts);
                fixed (float* ptr = hData) {
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(hData.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
                for (uint a = 2; a < 8; a++) gl.DisableVertexAttribArray(a);
                gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);
                }

                if (_hoverDrawCount > 0) {
                if (_hoverVAO == 0) {
                    gl.GenVertexArrays(1, out _hoverVAO);
                    gl.GenBuffers(1, out _hoverVBO);
                }
                gl.BindVertexArray(_hoverVAO);
                gl.BindBuffer(GLEnum.ArrayBuffer, _hoverVBO);
                    var hoverData = CollectionsMarshal.AsSpan(_hoveredPortalVerts);
                fixed (float* ptr = hoverData) {
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(hoverData.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
                for (uint a = 2; a < 8; a++) gl.DisableVertexAttribArray(a);
                gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);
                }

                _openPortalUploaded = true;
            }

            var shader = _sceneContext.SphereShader;
            var camPos = Camera.Position;

            if (_openPortalDrawCount > 0) {
                gl.BindVertexArray(_allOpenVAO);
                shader.Bind();
                shader.SetUniform("uViewProjection", viewProjection);
                shader.SetUniform("uCameraPosition", camPos);
                shader.SetUniform("uSphereColor", IsInPlacementMode ? new Vector3(0.35f, 1.0f, 0.45f) : new Vector3(0.2f, 0.85f, 0.35f));
                shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
                shader.SetUniform("uAmbientIntensity", 1.0f);
                shader.SetUniform("uSpecularPower", 0f);
                shader.SetUniform("uGlowColor", IsInPlacementMode ? new Vector3(0f, 0.55f, 0.15f) : new Vector3(0f, 0.2f, 0.05f));
                shader.SetUniform("uGlowIntensity", IsInPlacementMode ? 0.85f : 0.3f);
                shader.SetUniform("uGlowPower", 1.0f);

                gl.Disable(EnableCap.DepthTest);
                gl.Disable(EnableCap.CullFace);
                gl.DrawArrays(GLEnum.Triangles, 0, (uint)_openPortalDrawCount);
                gl.Enable(EnableCap.DepthTest);
                gl.Enable(EnableCap.CullFace);
            }

            if (_highlightDrawCount > 0) {
                gl.BindVertexArray(_highlightVAO);
                shader.Bind();
                shader.SetUniform("uViewProjection", viewProjection);
                shader.SetUniform("uCameraPosition", camPos);
                shader.SetUniform("uSphereColor", new Vector3(1.0f, 0.92f, 0.15f));
                shader.SetUniform("uGlowColor", new Vector3(0.85f, 0.65f, 0f));
                shader.SetUniform("uGlowIntensity", 0.95f);
                shader.SetUniform("uAmbientIntensity", 1.0f);
                shader.SetUniform("uSpecularPower", 0f);
                shader.SetUniform("uGlowPower", 1.0f);

                gl.Disable(EnableCap.DepthTest);
                gl.Disable(EnableCap.CullFace);
                gl.DrawArrays(GLEnum.Triangles, 0, (uint)_highlightDrawCount);
                gl.Enable(EnableCap.DepthTest);
                gl.Enable(EnableCap.CullFace);
            }

            if (_hoverDrawCount > 0) {
                gl.BindVertexArray(_hoverVAO);
                shader.Bind();
                shader.SetUniform("uViewProjection", viewProjection);
                shader.SetUniform("uCameraPosition", camPos);
                shader.SetUniform("uSphereColor", new Vector3(0.95f, 0.95f, 0.82f));
                shader.SetUniform("uGlowColor", new Vector3(0.55f, 0.7f, 0.35f));
                shader.SetUniform("uGlowIntensity", 0.7f);
                shader.SetUniform("uAmbientIntensity", 1.0f);
                shader.SetUniform("uSpecularPower", 0f);
                shader.SetUniform("uGlowPower", 1.0f);

                gl.Disable(EnableCap.DepthTest);
                gl.Disable(EnableCap.CullFace);
                gl.DrawArrays(GLEnum.Triangles, 0, (uint)_hoverDrawCount);
                gl.Enable(EnableCap.DepthTest);
                gl.Enable(EnableCap.CullFace);
            }

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private static void AddDoorframe(List<float> dest, Vector3[] worldVerts, Vector3 portalNormal, float width, bool fill) {
            var n = portalNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(portalNormal) : Vector3.UnitZ;
            var offset = n * 0.12f;
            for (int v = 0; v < worldVerts.Length; v++) {
                int next = (v + 1) % worldVerts.Length;
                var a = worldVerts[v] + offset;
                var b = worldVerts[next] + offset;
                    var edgeDir = b - a;
                    if (edgeDir.LengthSquared() < 1e-8f) continue;
                    edgeDir = Vector3.Normalize(edgeDir);
                var cross = Vector3.Cross(edgeDir, n);
                    var sideDir = cross.LengthSquared() > 1e-6f
                    ? Vector3.Normalize(cross) * width
                    : Vector3.Normalize(Vector3.Cross(edgeDir, Vector3.UnitZ)) * width;
                    void PV(Vector3 p) {
                    dest.Add(p.X); dest.Add(p.Y); dest.Add(p.Z);
                    dest.Add(n.X); dest.Add(n.Y); dest.Add(n.Z);
                    }
                    PV(a - sideDir); PV(b - sideDir); PV(b + sideDir);
                    PV(a - sideDir); PV(b + sideDir); PV(a + sideDir);
                }

            if (!fill || worldVerts.Length < 3) return;
            var fillOffset = n * 0.08f;
            void FV(Vector3 p) {
                dest.Add(p.X); dest.Add(p.Y); dest.Add(p.Z);
                dest.Add(n.X); dest.Add(n.Y); dest.Add(n.Z);
            }
            var v0 = worldVerts[0] + fillOffset;
            for (int t = 1; t < worldVerts.Length - 1; t++) {
                FV(v0);
                FV(worldVerts[t] + fillOffset);
                FV(worldVerts[t + 1] + fillOffset);
            }
        }

        private unsafe void RenderConnectedPortals(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || ConnectedPortalIndicators.Count == 0) return;

            bool rebuild = !_connPortalUploaded
                || !ReferenceEquals(_cachedConnPortalList, ConnectedPortalIndicators);

            if (rebuild) {
                _connPortalVerts.Clear();
                foreach (var indicator in ConnectedPortalIndicators) {
                    if (indicator.WorldVertices == null || indicator.WorldVertices.Length < 3) continue;
                    AddDoorframe(_connPortalVerts, indicator.WorldVertices, indicator.Normal, 0.09f, fill: false);
                }
                _cachedConnPortalList = ConnectedPortalIndicators;
                _connPortalDrawCount = _connPortalVerts.Count / 6;
                if (_connPortalDrawCount > 0) {
            if (_connPortalVAO == 0) {
                gl.GenVertexArrays(1, out _connPortalVAO);
                gl.GenBuffers(1, out _connPortalVBO);
            }
            gl.BindVertexArray(_connPortalVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _connPortalVBO);
                    var data = CollectionsMarshal.AsSpan(_connPortalVerts);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint a = 2; a < 8; a++) gl.DisableVertexAttribArray(a);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);
                }
                _connPortalUploaded = true;
            }

            if (_connPortalDrawCount == 0) return;

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", Camera.Position);
            shader.SetUniform("uSphereColor", new Vector3(0.35f, 0.55f, 1.0f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0.1f, 0.15f, 0.4f));
            shader.SetUniform("uGlowIntensity", 0f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.BindVertexArray(_connPortalVAO);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)_connPortalDrawCount);
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);
            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RenderConnectionLines(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || ConnectionLines == null || ConnectionLines.Count == 0) return;

            const float lineWidth = 0.15f;
            var camPos = Camera.Position;
            _connLineVerts.Clear();

            foreach (var (from, to) in ConnectionLines) {
                var edgeDir = Vector3.Normalize(to - from);
                var midpoint = (from + to) * 0.5f;
                var toCamera = Vector3.Normalize(camPos - midpoint);
                var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * lineWidth;
                var normal = toCamera;

                var a0 = from - sideDir; var a1 = from + sideDir;
                var b0 = to - sideDir; var b1 = to + sideDir;

                void V(Vector3 p) { _connLineVerts.Add(p.X); _connLineVerts.Add(p.Y); _connLineVerts.Add(p.Z); _connLineVerts.Add(normal.X); _connLineVerts.Add(normal.Y); _connLineVerts.Add(normal.Z); }
                V(a0); V(b0); V(b1);
                V(a0); V(b1); V(a1);
            }

            if (_connLineVerts.Count == 0) return;
            int vertCount = _connLineVerts.Count / 6;
            var data = CollectionsMarshal.AsSpan(_connLineVerts);

            if (_connLineVAO == 0) {
                gl.GenVertexArrays(1, out _connLineVAO);
                gl.GenBuffers(1, out _connLineVBO);
            }

            gl.BindVertexArray(_connLineVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _connLineVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.DisableVertexAttribArray(2);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            shader.SetUniform("uSphereColor", new Vector3(0.3f, 0.6f, 1.0f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0.1f, 0.3f, 0.6f));
            shader.SetUniform("uGlowIntensity", 0.4f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RenderSelectionBoxes(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || SelectedCells == null) return;

            foreach (var cell in SelectedCells) {
                RenderSelectionBox(gl, viewProjection, cell);
            }
        }

        private unsafe void RenderSelectionBox(GL gl, Matrix4x4 viewProjection, LoadedEnvCell cell) {
            if (_sceneContext == null) return;
            var min = cell.LocalBoundsMin;
            var max = cell.LocalBoundsMax;
            if (min.X >= max.X) return;

            var transform = cell.WorldTransform;

            Vector3[] c = new Vector3[8];
            c[0] = Vector3.Transform(new Vector3(min.X, min.Y, min.Z), transform);
            c[1] = Vector3.Transform(new Vector3(max.X, min.Y, min.Z), transform);
            c[2] = Vector3.Transform(new Vector3(max.X, max.Y, min.Z), transform);
            c[3] = Vector3.Transform(new Vector3(min.X, max.Y, min.Z), transform);
            c[4] = Vector3.Transform(new Vector3(min.X, min.Y, max.Z), transform);
            c[5] = Vector3.Transform(new Vector3(max.X, min.Y, max.Z), transform);
            c[6] = Vector3.Transform(new Vector3(max.X, max.Y, max.Z), transform);
            c[7] = Vector3.Transform(new Vector3(min.X, max.Y, max.Z), transform);

            // Build billboard quads for each edge (2 tris per edge, camera-facing thickness)
            int[][] edges = { new[]{0,1}, new[]{1,2}, new[]{2,3}, new[]{3,0},
                              new[]{4,5}, new[]{5,6}, new[]{6,7}, new[]{7,4},
                              new[]{0,4}, new[]{1,5}, new[]{2,6}, new[]{3,7} };

            const float lineWidth = 0.07f;
            var camPos = Camera.Position;
            _selBoxVerts.Clear();

            foreach (var e in edges) {
                var a = c[e[0]];
                var b = c[e[1]];
                var edgeDir = Vector3.Normalize(b - a);
                var midpoint = (a + b) * 0.5f;
                var toCamera = Vector3.Normalize(camPos - midpoint);
                var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * lineWidth;

                var normal = toCamera;
                var a0 = a - sideDir; var a1 = a + sideDir;
                var b0 = b - sideDir; var b1 = b + sideDir;

                void V(Vector3 p) { _selBoxVerts.Add(p.X); _selBoxVerts.Add(p.Y); _selBoxVerts.Add(p.Z); _selBoxVerts.Add(normal.X); _selBoxVerts.Add(normal.Y); _selBoxVerts.Add(normal.Z); }
                V(a0); V(b0); V(b1);
                V(a0); V(b1); V(a1);
            }

            int vertCount = _selBoxVerts.Count / 6;
            var data = CollectionsMarshal.AsSpan(_selBoxVerts);

            if (_lineVAO == 0) {
                gl.GenVertexArrays(1, out _lineVAO);
                gl.GenBuffers(1, out _lineVBO);
            }

            gl.BindVertexArray(_lineVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _lineVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.DisableVertexAttribArray(2);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            shader.SetUniform("uSphereColor", new Vector3(0.7f, 0.3f, 1.0f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
            shader.SetUniform("uGlowIntensity", 0f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);

            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RenderNeighborBoxes(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || ConnectedNeighborCells == null) return;

            _neighborBoxVerts.Clear();
            var camPos = Camera.Position;
            const float lineWidth = 0.06f;

            foreach (var cell in ConnectedNeighborCells) {
                var min = cell.LocalBoundsMin;
                var max = cell.LocalBoundsMax;
                if (min.X >= max.X) continue;

                var transform = cell.WorldTransform;
                Vector3[] c = new Vector3[8];
                c[0] = Vector3.Transform(new Vector3(min.X, min.Y, min.Z), transform);
                c[1] = Vector3.Transform(new Vector3(max.X, min.Y, min.Z), transform);
                c[2] = Vector3.Transform(new Vector3(max.X, max.Y, min.Z), transform);
                c[3] = Vector3.Transform(new Vector3(min.X, max.Y, min.Z), transform);
                c[4] = Vector3.Transform(new Vector3(min.X, min.Y, max.Z), transform);
                c[5] = Vector3.Transform(new Vector3(max.X, min.Y, max.Z), transform);
                c[6] = Vector3.Transform(new Vector3(max.X, max.Y, max.Z), transform);
                c[7] = Vector3.Transform(new Vector3(min.X, max.Y, max.Z), transform);

                int[][] edges = { new[]{0,1}, new[]{1,2}, new[]{2,3}, new[]{3,0},
                                  new[]{4,5}, new[]{5,6}, new[]{6,7}, new[]{7,4},
                                  new[]{0,4}, new[]{1,5}, new[]{2,6}, new[]{3,7} };

                foreach (var e in edges) {
                    var a = c[e[0]];
                    var b = c[e[1]];
                    var edgeDir = Vector3.Normalize(b - a);
                    var midpoint = (a + b) * 0.5f;
                    var toCamera = Vector3.Normalize(camPos - midpoint);
                    var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * lineWidth;
                    var normal = toCamera;
                    void V(Vector3 p) { _neighborBoxVerts.Add(p.X); _neighborBoxVerts.Add(p.Y); _neighborBoxVerts.Add(p.Z); _neighborBoxVerts.Add(normal.X); _neighborBoxVerts.Add(normal.Y); _neighborBoxVerts.Add(normal.Z); }
                    V(a - sideDir); V(b - sideDir); V(b + sideDir);
                    V(a - sideDir); V(b + sideDir); V(a + sideDir);
                }
            }

            if (_neighborBoxVerts.Count == 0) return;
            int vertCount = _neighborBoxVerts.Count / 6;
            var data = CollectionsMarshal.AsSpan(_neighborBoxVerts);

            if (_neighborVAO == 0) {
                gl.GenVertexArrays(1, out _neighborVAO);
                gl.GenBuffers(1, out _neighborVBO);
            }

            gl.BindVertexArray(_neighborVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _neighborVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            shader.SetUniform("uSphereColor", new Vector3(0.3f, 0.7f, 1.0f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0.1f, 0.3f, 0.5f));
            shader.SetUniform("uGlowIntensity", 0.3f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RenderSelectedConnectionLines(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || SelectedConnectionLines == null || SelectedConnectionLines.Count == 0) return;

            const float lineWidth = 0.25f;
            var camPos = Camera.Position;
            _selConnLineVerts.Clear();

            foreach (var (from, to) in SelectedConnectionLines) {
                var edgeDir = Vector3.Normalize(to - from);
                var midpoint = (from + to) * 0.5f;
                var toCamera = Vector3.Normalize(camPos - midpoint);
                var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * lineWidth;
                var normal = toCamera;

                var a0 = from - sideDir; var a1 = from + sideDir;
                var b0 = to - sideDir; var b1 = to + sideDir;

                void V(Vector3 p) { _selConnLineVerts.Add(p.X); _selConnLineVerts.Add(p.Y); _selConnLineVerts.Add(p.Z); _selConnLineVerts.Add(normal.X); _selConnLineVerts.Add(normal.Y); _selConnLineVerts.Add(normal.Z); }
                V(a0); V(b0); V(b1);
                V(a0); V(b1); V(a1);
            }

            if (_selConnLineVerts.Count == 0) return;
            int vertCount = _selConnLineVerts.Count / 6;
            var data = CollectionsMarshal.AsSpan(_selConnLineVerts);

            if (_selConnLineVAO == 0) {
                gl.GenVertexArrays(1, out _selConnLineVAO);
                gl.GenBuffers(1, out _selConnLineVBO);
            }

            gl.BindVertexArray(_selConnLineVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _selConnLineVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            shader.SetUniform("uSphereColor", new Vector3(1.0f, 0.8f, 0.2f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0.5f, 0.4f, 0.05f));
            shader.SetUniform("uGlowIntensity", 0.6f);
            shader.SetUniform("uGlowPower", 1.5f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        /// <summary>
        /// Render a reference grid on the XY plane centered on the dungeon.
        /// Uses thin billboard quads (same technique as connection lines) so
        /// lines have consistent screen-space thickness regardless of distance.
        /// Major lines every GridStepH (10 units), with subtle axis indicators at the center.
        /// </summary>
        private unsafe void RenderGrid(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null) return;

            var center = GetDungeonCenter();
            if (center == null) {
                // No cells loaded yet — place grid at the landblock world-space origin
                var blockX = (_loadedLandblockKey >> 8) & 0xFF;
                var blockY = _loadedLandblockKey & 0xFF;
                center = new Vector3(blockX * 192f, blockY * 192f, -50f);
            }

            // Snap grid center to the nearest major grid step so it doesn't jitter when cells move
            float step = 10f;
            var snapped = new Vector3(
                MathF.Round(center.Value.X / step) * step,
                MathF.Round(center.Value.Y / step) * step,
                MathF.Round(center.Value.Z / step) * step);

            if (_gridDirty || snapped != _gridCenter || _gridVAO == 0) {
                _gridCenter = snapped;
                _gridDirty = false;
                RebuildGridGeometry(gl, snapped, step);
            }

            if (_gridVertCount == 0) return;

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", Camera.Position);
            shader.SetUniform("uSphereColor", new Vector3(0.35f, 0.3f, 0.5f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
            shader.SetUniform("uGlowIntensity", 0f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.Disable(EnableCap.CullFace);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            gl.BindVertexArray(_gridVAO);
            gl.DrawArrays(GLEnum.Triangles, 0, (uint)_gridVertCount);

            gl.Disable(EnableCap.Blend);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RebuildGridGeometry(GL gl, Vector3 center, float step) {
            int halfLines = 15;
            float extent = halfLines * step;
            float z = center.Z;
            float lineW = 0.07f;
            float majorW = 0.14f;
            var camPos = Camera.Position;

            var verts = new List<float>();

            void AddLine(Vector3 a, Vector3 b, float width) {
                var dir = Vector3.Normalize(b - a);
                var mid = (a + b) * 0.5f;
                var toCamera = Vector3.Normalize(camPos - mid);
                var side = Vector3.Normalize(Vector3.Cross(dir, toCamera)) * width;
                if (side.LengthSquared() < 1e-10f) {
                    side = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitZ)) * width;
                }
                var n = toCamera;

                void V(Vector3 p) {
                    verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z);
                    verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z);
                }
                V(a - side); V(b - side); V(b + side);
                V(a - side); V(b + side); V(a + side);
            }

            // Lines parallel to X axis (varying Y)
            for (int i = -halfLines; i <= halfLines; i++) {
                float y = center.Y + i * step;
                bool isMajor = i == 0;
                var a = new Vector3(center.X - extent, y, z);
                var b = new Vector3(center.X + extent, y, z);
                AddLine(a, b, isMajor ? majorW : lineW);
            }

            // Lines parallel to Y axis (varying X)
            for (int i = -halfLines; i <= halfLines; i++) {
                float x = center.X + i * step;
                bool isMajor = i == 0;
                var a = new Vector3(x, center.Y - extent, z);
                var b = new Vector3(x, center.Y + extent, z);
                AddLine(a, b, isMajor ? majorW : lineW);
            }

            // Vertical Z axis line through center (helps with depth orientation)
            var zBottom = new Vector3(center.X, center.Y, z - extent * 0.5f);
            var zTop = new Vector3(center.X, center.Y, z + extent * 0.5f);
            AddLine(zBottom, zTop, majorW);

            _gridVertCount = verts.Count / 6;
            var data = CollectionsMarshal.AsSpan(verts);

            if (_gridVAO == 0) {
                gl.GenVertexArrays(1, out _gridVAO);
                gl.GenBuffers(1, out _gridVBO);
            }

            gl.BindVertexArray(_gridVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _gridVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            gl.BindVertexArray(0);
        }

        private unsafe void RenderBatchedObject(GL gl, StaticObjectRenderData renderData, List<Matrix4x4> instanceTransforms) {
            if (_sceneContext == null || instanceTransforms.Count == 0 || renderData.Batches.Count == 0) return;

            int requiredFloats = instanceTransforms.Count * 16;

            if (_sceneContext.InstanceUploadBuffer.Length < requiredFloats) {
                int newSize = Math.Max(requiredFloats, 256);
                newSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newSize);
                _sceneContext.InstanceUploadBuffer = new float[newSize];
            }

            for (int i = 0; i < instanceTransforms.Count; i++) {
                var t = instanceTransforms[i];
                int o = i * 16;
                _sceneContext.InstanceUploadBuffer[o+ 0]=t.M11; _sceneContext.InstanceUploadBuffer[o+ 1]=t.M12;
                _sceneContext.InstanceUploadBuffer[o+ 2]=t.M13; _sceneContext.InstanceUploadBuffer[o+ 3]=t.M14;
                _sceneContext.InstanceUploadBuffer[o+ 4]=t.M21; _sceneContext.InstanceUploadBuffer[o+ 5]=t.M22;
                _sceneContext.InstanceUploadBuffer[o+ 6]=t.M23; _sceneContext.InstanceUploadBuffer[o+ 7]=t.M24;
                _sceneContext.InstanceUploadBuffer[o+ 8]=t.M31; _sceneContext.InstanceUploadBuffer[o+ 9]=t.M32;
                _sceneContext.InstanceUploadBuffer[o+10]=t.M33; _sceneContext.InstanceUploadBuffer[o+11]=t.M34;
                _sceneContext.InstanceUploadBuffer[o+12]=t.M41; _sceneContext.InstanceUploadBuffer[o+13]=t.M42;
                _sceneContext.InstanceUploadBuffer[o+14]=t.M43; _sceneContext.InstanceUploadBuffer[o+15]=t.M44;
            }

            if (_sceneContext.InstanceVBO == 0) {
                gl.GenBuffers(1, out uint vbo);
                _sceneContext.InstanceVBO = vbo;
            }

            gl.BindBuffer(GLEnum.ArrayBuffer, _sceneContext.InstanceVBO);

            if (requiredFloats > _sceneContext.InstanceBufferCapacity) {
                int newCapacity = Math.Max(requiredFloats, 256);
                newCapacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newCapacity);
                _sceneContext.InstanceBufferCapacity = newCapacity;
                fixed (float* ptr = _sceneContext.InstanceUploadBuffer) {
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(newCapacity * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
            }
            else {
                fixed (float* ptr = _sceneContext.InstanceUploadBuffer) {
                    gl.BufferSubData(GLEnum.ArrayBuffer, 0, (nuint)(requiredFloats * sizeof(float)), ptr);
                }
            }

            gl.BindVertexArray(renderData.VAO);

            gl.BindBuffer(GLEnum.ArrayBuffer, _sceneContext.InstanceVBO);
            for (int i = 0; i < 4; i++) {
                gl.EnableVertexAttribArray((uint)(3 + i));
                gl.VertexAttribPointer((uint)(3 + i), 4, GLEnum.Float, false, (uint)(16 * sizeof(float)), (void*)(i * 4 * sizeof(float)));
                gl.VertexAttribDivisor((uint)(3 + i), 1);
            }

            bool cullFaceEnabled = true;
            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;

                if (batch.IsDoubleSided && cullFaceEnabled) {
                    gl.Disable(EnableCap.CullFace);
                    cullFaceEnabled = false;
                }
                else if (!batch.IsDoubleSided && !cullFaceEnabled) {
                    gl.Enable(EnableCap.CullFace);
                    cullFaceEnabled = true;
                }

                batch.TextureArray.Bind(0);
                _sceneContext.ObjectManager._objectShader.SetUniform("uTextureArray", 0);
                _sceneContext.ObjectManager._objectShader.SetUniform("uTextureIndex", (float)batch.TextureIndex);
                gl.DisableVertexAttribArray(7);
                gl.VertexAttrib1((uint)7, (float)batch.TextureIndex);

                gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                gl.DrawElementsInstanced(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null, (uint)instanceTransforms.Count);
            }

            if (!cullFaceEnabled) gl.Enable(EnableCap.CullFace);
        }

        public void Dispose() {
            ThumbnailService?.Dispose();
            if (_renderer != null) {
                var gl = _renderer.GraphicsDevice.GL;
                if (_objSelVBO != 0) gl.DeleteBuffer(_objSelVBO);
                if (_objSelVAO != 0) gl.DeleteVertexArray(_objSelVAO);
                if (_lineVBO != 0) gl.DeleteBuffer(_lineVBO);
                if (_lineVAO != 0) gl.DeleteVertexArray(_lineVAO);
                if (_connLineVBO != 0) gl.DeleteBuffer(_connLineVBO);
                if (_connLineVAO != 0) gl.DeleteVertexArray(_connLineVAO);
                if (_portalConnVBO != 0) gl.DeleteBuffer(_portalConnVBO);
                if (_portalConnVAO != 0) gl.DeleteVertexArray(_portalConnVAO);
                if (_portalOpenVBO != 0) gl.DeleteBuffer(_portalOpenVBO);
                if (_portalOpenVAO != 0) gl.DeleteVertexArray(_portalOpenVAO);
                if (_allOpenVBO != 0) gl.DeleteBuffer(_allOpenVBO);
                if (_allOpenVAO != 0) gl.DeleteVertexArray(_allOpenVAO);
                if (_highlightVBO != 0) gl.DeleteBuffer(_highlightVBO);
                if (_highlightVAO != 0) gl.DeleteVertexArray(_highlightVAO);
                if (_hoverVBO != 0) gl.DeleteBuffer(_hoverVBO);
                if (_hoverVAO != 0) gl.DeleteVertexArray(_hoverVAO);
                if (_connPortalVBO != 0) gl.DeleteBuffer(_connPortalVBO);
                if (_connPortalVAO != 0) gl.DeleteVertexArray(_connPortalVAO);
                if (_neighborVBO != 0) gl.DeleteBuffer(_neighborVBO);
                if (_neighborVAO != 0) gl.DeleteVertexArray(_neighborVAO);
                if (_selConnLineVBO != 0) gl.DeleteBuffer(_selConnLineVBO);
                if (_selConnLineVAO != 0) gl.DeleteVertexArray(_selConnLineVAO);
                if (_gridVBO != 0) gl.DeleteBuffer(_gridVBO);
                if (_gridVAO != 0) gl.DeleteVertexArray(_gridVAO);
                if (_surfaceIndicatorVBO != 0) gl.DeleteBuffer(_surfaceIndicatorVBO);
                if (_surfaceIndicatorVAO != 0) gl.DeleteVertexArray(_surfaceIndicatorVAO);
            }
            _sceneContext?.Dispose();
        }
    }

    public struct RoomPlacementPreviewData {
        public Vector3 Origin;
        public Quaternion Orientation;
        public uint EnvFileId;
        public ushort CellStructIndex;
    }

    /// <summary>
    /// Represents an open portal on any cell in the dungeon, for rendering green indicators.
    /// </summary>
    public struct OpenPortalIndicator {
        public Vector3[] WorldVertices;
        public Vector3 Centroid;
        public Vector3 Normal;
        public ushort CellNum;
        public ushort PolyId;
    }
}
