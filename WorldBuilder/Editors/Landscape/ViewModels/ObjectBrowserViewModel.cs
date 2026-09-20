using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Converters;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.ViewModels;
using WorldBuilder.Editors.Landscape.WorldGen;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    /// <summary>
    /// Panel for browsing DAT objects and placing them in the scene.
    /// Lists Setups, weenies, and particle emitters — not GfxObj meshes (those are parts of Setups).
    /// </summary>
    public partial class ObjectBrowserViewModel : ViewModelBase {
        private readonly TerrainEditingContext _context;
        private readonly IDatReaderWriter _dats;
        private readonly ObjectTagIndex _tagIndex = new();
        private readonly Func<ThumbnailRenderService?> _getThumbnailService;
        private readonly ThumbnailCache _thumbnailCache;
        private readonly WorldBuilderSettings? _settings;
        private bool _thumbnailsReady;
        private bool _subscribedToThumbnailReady;
        private bool _displayActivated;
        private uint[] _allSetupIds = Array.Empty<uint>();
        private uint[] _allParticleEmitterIds = Array.Empty<uint>();
        private HashSet<uint> _buildingIds = new();
        /// <summary>Subset of <see cref="_buildingIds"/> suitable for placement (excludes fragments/modules).</summary>
        private HashSet<uint> _completeBuildingModelIds = new();
        private bool _buildingIdsLoaded;
        private HashSet<uint> _sceneryIds = new();
        private bool _sceneryIdsLoaded;
        private List<ObjectBrowserItem> _loadedWeenies = new();
        private readonly HashSet<uint> _favoriteIds = new();

        private readonly Dictionary<uint, ObjectBrowserItem> _itemLookup = new();

        [ObservableProperty] private ObservableCollection<ObjectBrowserItem> _filteredItems = new();
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private string _status = "Search by name or hex ID";

        [ObservableProperty] private bool _showSetups = true;
        [ObservableProperty] private bool _showBuildingsOnly;
        [ObservableProperty] private bool _showSceneryOnly;
        [ObservableProperty] private bool _showWeenies;
        /// <summary>When true, particle emitter definitions are included in the browser list (loaded from portal.dat).</summary>
        [ObservableProperty] private bool _showParticleEmitters = true;
        [ObservableProperty] private bool _showFavoritesOnly;
        [ObservableProperty] private bool _isLoadingWeenies;
        [ObservableProperty] private bool _hasMore;

        /// <summary>
        /// When true, clicking a weenie item creates an OutdoorInstancePlacement instead of a DAT static.
        /// When false (default), weenies are embedded in the DAT as static objects (original behavior).
        /// </summary>
        [ObservableProperty] private bool _placeWeenieAsInstance = false;

        private const int BatchSize = 100;
        private int _displayLimit = BatchSize;

        /// <summary>GfxObj (0x01xxxxxx) meshes are not placeable objects — only Setups (0x02xxxxxx) belong in this browser.</summary>
        private static bool IsSetupDid(uint id) => (id & 0xFF000000) == 0x02000000;
        private static bool IsGfxObjDid(uint id) => (id & 0xFF000000) == 0x01000000;

        /// <summary>
        /// Gets the tag index for use by the view (e.g., tooltips).
        /// </summary>
        public ObjectTagIndex TagIndex => _tagIndex;

        public event EventHandler<IReadOnlyList<(uint WeenieClassId, uint SetupId)>>? WeenieSetupsLoaded;

        public ObjectBrowserViewModel(TerrainEditingContext context, IDatReaderWriter dats,
            Func<ThumbnailRenderService?>? getThumbnailService = null, ThumbnailCache? thumbnailCache = null,
            WorldBuilderSettings? settings = null) {
            _context = context;
            _dats = dats;
            _getThumbnailService = getThumbnailService ?? (() => null);
            _thumbnailCache = thumbnailCache ?? new ThumbnailCache(_dats.CacheNamespace);
            _settings = settings;

            // Load keyword tag index for name-based search
            _tagIndex.LoadFromEmbeddedResource();
            ObjectIdToTagsConverter.TagIndex = _tagIndex;

            try {
                _allSetupIds = _dats.GetAllIdsOfType<Setup>().OrderBy(id => id).ToArray();
                try {
                    _allParticleEmitterIds = _dats.GetAllIdsOfType<ParticleEmitter>().OrderBy(id => id).ToArray();
                }
                catch (Exception pex) {
                    Console.WriteLine($"[ObjectBrowser] ParticleEmitter ID listing failed: {pex.Message}");
                    _allParticleEmitterIds = Array.Empty<uint>();
                }
                Console.WriteLine($"[ObjectBrowser] Loaded {_allSetupIds.Length} Setups, {_allParticleEmitterIds.Length} ParticleEmitters (GfxObj meshes are not listed)");
            }
            catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] Error loading object IDs: {ex.Message}");
            }

            LoadObjectFavorites();
            ApplyFilter();
        }

        /// <summary>
        /// Heavy catalog and GPU thumbnail work stays off until Select is actually using this panel.
        /// </summary>
        public void ActivateForDisplay() {
            if (_displayActivated) {
                return;
            }

            _displayActivated = true;
            Task.Run(LoadBuildingIds);
            Task.Run(LoadSceneryIds);
            _ = Task.Run(async () => {
                await Task.Delay(2000);
                for (int i = 0; i < 30; i++) {
                    if (_getThumbnailService() != null) break;
                    await Task.Delay(500);
                }
                _thumbnailsReady = true;
                Console.WriteLine($"[ObjectBrowser] Thumbnail loading ready (service={(_getThumbnailService() != null ? "available" : "unavailable")}), requesting for {FilteredItems.Count} items");
                Dispatcher.UIThread.Post(() => RequestThumbnails(FilteredItems));
            });
        }

        /// <summary>
        /// Called on the GL thread when a thumbnail has been rendered.
        /// Saves to disk cache and dispatches bitmap update to the UI thread.
        /// </summary>
        private void OnThumbnailReady(uint objectId, byte[] rgbaPixels) {
            // Save to disk cache (fire-and-forget background thread)
            _thumbnailCache.SaveAsync(objectId, rgbaPixels, ThumbnailRenderService.ThumbnailSize, ThumbnailRenderService.ThumbnailSize);

            // Create bitmap from pixels
            var bitmap = ThumbnailCache.CreateBitmapFromRgba(rgbaPixels,
                ThumbnailRenderService.ThumbnailSize, ThumbnailRenderService.ThumbnailSize);

            // Dispatch to UI thread to update the item
            Dispatcher.UIThread.Post(() => {
                foreach (var item in FilteredItems) {
                    if (item.ThumbnailGraphicsId == objectId || (!item.IsParticleEmitter && item.Id == objectId))
                        item.Thumbnail = bitmap;
                }
            });
        }

        private void LoadBuildingIds() {
            try {
                var buildingIds = new HashSet<uint>();

                // Try DatCollection-level enumeration first
                var allLbiIds = _dats.GetAllIdsOfType<LandBlockInfo>().ToArray();
                Console.WriteLine($"[ObjectBrowser] Found {allLbiIds.Length} LandBlockInfo entries in DAT");

                if (allLbiIds.Length == 0) {
                    // Last resort: brute-force scan all possible landblock IDs
                    Console.WriteLine("[ObjectBrowser] Brute-force scanning landblocks for buildings...");
                    for (uint x = 0; x < 255; x++) {
                        for (uint y = 0; y < 255; y++) {
                            var infoId = (uint)(((x << 8) | y) << 16 | 0xFFFE);
                            if (_dats.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                                foreach (var building in lbi.Buildings) {
                                    buildingIds.Add(building.ModelId);
                                }
                            }
                        }
                    }
                }
                else {
                    foreach (var infoId in allLbiIds) {
                        if (_dats.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                            foreach (var building in lbi.Buildings) {
                                buildingIds.Add(building.ModelId);
                            }
                        }
                    }
                }

                _buildingIds = buildingIds;

                if (_dats.Dats != null) {
                    try {
                        var complete = BuildingAnalyzer.GetEditorCompleteBuildingModelIds(_dats);
                        if (complete.Count > 0) {
                            _completeBuildingModelIds = complete.ToHashSet();
                            Console.WriteLine($"[ObjectBrowser] Building catalog (blueprint+EnvCell): {_completeBuildingModelIds.Count} models " +
                                $"(from {_buildingIds.Count} raw LBI IDs)");
                        } else {
                            _completeBuildingModelIds = BuildingAnalyzer.GetEditorEnvCellFallbackModelIds(_dats);
                            if (_completeBuildingModelIds.Count > 0) {
                                Console.WriteLine($"[ObjectBrowser] Using EnvCell fallback catalog (no blueprint pass): {_completeBuildingModelIds.Count} models");
                            } else {
                                Console.WriteLine("[ObjectBrowser] No EnvCell catalog entries; Buildings filter uses raw LBI minus denylist only.");
                            }
                        }
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"[ObjectBrowser] Could not build complete-building catalog: {ex.Message}");
                        _completeBuildingModelIds = new HashSet<uint>();
                    }
                }
                else {
                    _completeBuildingModelIds = new HashSet<uint>();
                }

                _buildingIdsLoaded = true;
                Console.WriteLine($"[ObjectBrowser] Found {_buildingIds.Count} unique building model IDs");
            }
            catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] Error scanning building IDs: {ex}");
                _buildingIdsLoaded = true; // Mark loaded so UI doesn't stay stuck on "Loading..."
            }
        }

        private void LoadSceneryIds() {
            try {
                var sceneryIds = new HashSet<uint>();
                var region = _context.TerrainSystem.Region;

                // Collect all unique Scene IDs from the Region's terrain/scene type mappings
                var sceneIds = new HashSet<uint>();
                foreach (var terrainType in region.TerrainInfo.TerrainTypes) {
                    foreach (var sceneTypeIdx in terrainType.SceneTypes) {
                        if (sceneTypeIdx < region.SceneInfo.SceneTypes.Count) {
                            var sceneType = region.SceneInfo.SceneTypes[(int)sceneTypeIdx];
                            foreach (var sceneId in sceneType.Scenes) {
                                sceneIds.Add(sceneId);
                            }
                        }
                    }
                }

                // Load each Scene and collect its object model IDs
                foreach (var sceneId in sceneIds) {
                    if (_dats.TryGet<Scene>(sceneId, out var scene)) {
                        foreach (var obj in scene.Objects) {
                            if (obj.ObjectId != 0) {
                                sceneryIds.Add(obj.ObjectId);
                            }
                        }
                    }
                }

                _sceneryIds = sceneryIds;
                _sceneryIdsLoaded = true;
                Console.WriteLine($"[ObjectBrowser] Found {_sceneryIds.Count} unique scenery model IDs from {sceneIds.Count} scenes");
            }
            catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] Error scanning scenery IDs: {ex}");
                _sceneryIdsLoaded = true;
            }
        }

        partial void OnSearchTextChanged(string value) { _displayLimit = BatchSize; ApplyFilter(); }
        partial void OnShowSetupsChanged(bool value) { _displayLimit = BatchSize; ApplyFilter(); }
        partial void OnShowWeeniesChanged(bool value) { _displayLimit = BatchSize; ApplyFilter(); }

        partial void OnShowParticleEmittersChanged(bool value) { _displayLimit = BatchSize; ApplyFilter(); }

        partial void OnShowFavoritesOnlyChanged(bool value) { _displayLimit = BatchSize; ApplyFilter(); }

        partial void OnShowBuildingsOnlyChanged(bool value) {
            if (value) { ShowSceneryOnly = false; ShowWeenies = false; }
            _displayLimit = BatchSize;
            ApplyFilter();
        }

        partial void OnShowSceneryOnlyChanged(bool value) {
            if (value) { ShowBuildingsOnly = false; ShowWeenies = false; }
            _displayLimit = BatchSize;
            ApplyFilter();
        }

        /// <summary>
        /// Returns true if the search text looks like a hex ID search (starts with 0x or is all hex chars).
        /// </summary>
        private static bool IsHexSearch(string text, out string normalizedHex) {
            normalizedHex = text.TrimStart('0', 'x', 'X').ToUpperInvariant();
            return uint.TryParse(normalizedHex, System.Globalization.NumberStyles.HexNumber, null, out _);
        }

        /// <summary>
        /// Filters a set of IDs by either hex substring match or keyword search.
        /// </summary>
        private (IEnumerable<uint> setups, IEnumerable<uint> gfxObjs, IEnumerable<uint> particles) ApplySearchFilter(
            IEnumerable<uint> setups, IEnumerable<uint> gfxObjs, IEnumerable<uint> particles, out string? statusSuffix) {
            statusSuffix = null;

            if (string.IsNullOrWhiteSpace(SearchText)) return (setups, gfxObjs, particles);

            if (IsHexSearch(SearchText, out var hexSearch)) {
                return (
                    setups.Where(id => id.ToString("X8").Contains(hexSearch)),
                    gfxObjs.Where(id => id.ToString("X8").Contains(hexSearch)),
                    particles.Where(id => id.ToString("X8").Contains(hexSearch))
                );
            }

            if (!_tagIndex.IsLoaded) {
                statusSuffix = "(keyword index not loaded)";
                return (Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());
            }

            var matchedIds = _tagIndex.Search(SearchText);
            if (matchedIds.Count == 0) {
                statusSuffix = $"No results for \"{SearchText}\"";
                return (Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());
            }

            statusSuffix = $"Found {matchedIds.Count} matches for \"{SearchText}\"";
            return (
                setups.Where(id => matchedIds.Contains(id)),
                gfxObjs.Where(id => matchedIds.Contains(id)),
                particles.Where(id => matchedIds.Contains(id))
            );
        }

        /// <summary>
        /// Creates ObjectBrowserItem instances from filtered setup and gfxobj ID arrays.
        /// Items start with placeholder thumbnails. Call RequestThumbnails() separately
        /// to load cached images and queue missing ones for rendering.
        /// </summary>
        private ObservableCollection<ObjectBrowserItem> BuildItems(uint[] setups, uint[] gfxObjs, uint[]? particles = null, ObjectBrowserItem[]? weenies = null) {
            var items = new ObservableCollection<ObjectBrowserItem>();
            _itemLookup.Clear();

            if (weenies != null) {
                foreach (var w in weenies) {
                    items.Add(w);
                    if (w.Id != 0 && w.WeenieClassId.HasValue)
                        _itemLookup.TryAdd(w.Id, w);
                }
            }

            foreach (var id in setups) {
                var tags = _tagIndex.IsLoaded ? _tagIndex.GetTagString(id) : null;
                var item = new ObjectBrowserItem(id, isSetup: true, tags);
                items.Add(item);
                _itemLookup[id] = item;
            }
            foreach (var id in gfxObjs) {
                var tags = _tagIndex.IsLoaded ? _tagIndex.GetTagString(id) : null;
                var item = new ObjectBrowserItem(id, isSetup: false, tags);
                items.Add(item);
                _itemLookup[id] = item;
            }
            if (particles != null) {
                foreach (var id in particles) {
                    var tags = _tagIndex.IsLoaded ? _tagIndex.GetTagString(id) : null;
                    var item = new ObjectBrowserItem(id, tags, _dats);
                    items.Add(item);
                    _itemLookup[id] = item;
                }
            }

            foreach (var item in items)
                item.IsFavorite = _favoriteIds.Contains(item.Id);

            if (_thumbnailsReady) {
                RequestThumbnails(items);
            }

            return items;
        }

        [RelayCommand]
        private async Task LoadWeeniesFromDbAsync() {
            if (_settings?.AceDbConnection == null) {
                Status = "Configure the world database in Settings first.";
                return;
            }

            IsLoadingWeenies = true;
            Status = "Loading weenies from DB...";
            try {
                var aceSettings = _settings.AceDbConnection.ToAceDbSettings();
                using var connector = new AceDbConnector(aceSettings);
                var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
                var list = await connector.GetWeenieNamesAsync(search, limit: 1000);

                _loadedWeenies.Clear();
                var mappings = new List<(uint, uint)>();
                foreach (var e in list) {
                    var item = new ObjectBrowserItem(e.SetupId, e.ClassId, e.Name);
                    _loadedWeenies.Add(item);
                    if (e.SetupId != 0)
                        mappings.Add((e.ClassId, e.SetupId));
                }

                ShowWeenies = true;
                Status = _loadedWeenies.Count > 0
                    ? $"{_loadedWeenies.Count} weenies loaded from DB"
                    : "No weenies found. Check DB connection.";

                WeenieSetupsLoaded?.Invoke(this, mappings);
                ApplyFilter();
            }
            catch (Exception ex) {
                Status = "DB error: " + ex.Message;
            }
            finally {
                IsLoadingWeenies = false;
            }
        }

        [RelayCommand]
        private void ShowMore() {
            _displayLimit += BatchSize;
            ApplyFilter();
        }

        /// <summary>
        /// For each item without a thumbnail, try the disk cache first.
        /// If not cached, queue for rendering via the ThumbnailRenderService.
        /// </summary>
        private void RequestThumbnails(ObservableCollection<ObjectBrowserItem> items) {
            var service = _getThumbnailService();

            if (service != null && !_subscribedToThumbnailReady) {
                service.ThumbnailReady += OnThumbnailReady;
                _subscribedToThumbnailReady = true;
            }

            int cached = 0, queued = 0, skipped = 0;
            foreach (var item in items) {
                if (item.Thumbnail != null || item.Id == 0) { skipped++; continue; }

                var thumbId = item.ThumbnailGraphicsId;
                var cachedBitmap = _thumbnailCache.TryLoadCached(thumbId);
                if (cachedBitmap != null) {
                    item.Thumbnail = cachedBitmap;
                    cached++;
                    continue;
                }

                if (service != null) {
                    service.RequestThumbnail(thumbId, item.IsParticleEmitter ? false : item.IsSetup);
                    queued++;
                }
            }
            Console.WriteLine($"[ObjectBrowser] RequestThumbnails: {items.Count} items, {cached} from cache, {queued} queued for render, {skipped} already have thumbnails" +
                (service == null ? " (WARNING: render service not yet available)" : ""));
        }

        private void ApplyFilter() {
            if (ShowFavoritesOnly && _favoriteIds.Count > 0) {
                var particleHs = new HashSet<uint>(_allParticleEmitterIds);
                var favSetups = _favoriteIds.Where(IsSetupDid).OrderBy(id => id);
                var favParticles = _favoriteIds.Where(particleHs.Contains).OrderBy(id => id);
                var (fs, _, fp) = ApplySearchFilter(favSetups, Enumerable.Empty<uint>(), favParticles, out var sfx);
                var setupArr = fs.ToArray();
                var partArr = fp.ToArray();
                FilteredItems = BuildItems(setupArr, Array.Empty<uint>(), partArr);
                Status = sfx ?? $"{setupArr.Length + partArr.Length} favorites";
                HasMore = false;
                return;
            }
            if (ShowFavoritesOnly) {
                FilteredItems = new ObservableCollection<ObjectBrowserItem>();
                Status = "No favorites yet — click the star on any object to add it";
                HasMore = false;
                return;
            }

            // Buildings: Setup models with interiors only. GfxObj shells are mesh pieces, not placeable buildings.
            if (ShowBuildingsOnly) {
                if (!_buildingIdsLoaded) {
                    Status = "Loading building list...";
                    FilteredItems = new ObservableCollection<ObjectBrowserItem>();
                    return;
                }

                IEnumerable<uint> buildingIdsForFilter = _completeBuildingModelIds.Count > 0
                    ? _completeBuildingModelIds
                    : _buildingIds.Where(id => !BuildingAnalyzer.KnownStructuralPieceModelIds.Contains(id));

                IEnumerable<uint> buildingSetups = buildingIdsForFilter
                    .Where(IsSetupDid).OrderBy(id => id);

                var (filtSetups, _, _) = ApplySearchFilter(buildingSetups, Enumerable.Empty<uint>(), Enumerable.Empty<uint>(), out var suffix);
                var sr = filtSetups.Take(100).ToArray();
                FilteredItems = BuildItems(sr, Array.Empty<uint>());
                int totalListed = buildingIdsForFilter.Count(IsSetupDid);
                if (suffix == null) {
                    if (totalListed == 0)
                        Status = "No Setup buildings in catalog — GfxObj mesh shells are hidden";
                    else if (_completeBuildingModelIds.Count > 0)
                        Status = $"{totalListed} buildings — showing {sr.Length}";
                    else
                        Status = $"{totalListed} buildings (raw − denylist) — showing {sr.Length}";
                } else {
                    Status = suffix;
                }
                return;
            }

            // Scenery: Setup objects used as outdoor statics. GfxObj meshes are not listed.
            if (ShowSceneryOnly) {
                if (!_sceneryIdsLoaded) {
                    Status = "Loading scenery list...";
                    FilteredItems = new ObservableCollection<ObjectBrowserItem>();
                    return;
                }

                IEnumerable<uint> scenerySetups = _sceneryIds.Where(IsSetupDid).OrderBy(id => id);
                var (filtSetups, _, _) = ApplySearchFilter(scenerySetups, Enumerable.Empty<uint>(), Enumerable.Empty<uint>(), out var suffix);
                var scsr = filtSetups.Take(100).ToArray();
                FilteredItems = BuildItems(scsr, Array.Empty<uint>());
                int scenerySetupCount = _sceneryIds.Count(IsSetupDid);
                Status = suffix ?? $"{scenerySetupCount} scenery — showing {scsr.Length}";
                return;
            }

            // Normal mode: Setups, particles, and weenies. GfxObj meshes are never listed.
            IEnumerable<uint> setups = ShowSetups ? _allSetupIds : Array.Empty<uint>();
            IEnumerable<uint> particles = ShowParticleEmitters ? _allParticleEmitterIds : Enumerable.Empty<uint>();

            var (fSetups, _, fParts) = ApplySearchFilter(setups, Enumerable.Empty<uint>(), particles, out var statusSuffix);

            var allWeenies = Array.Empty<ObjectBrowserItem>();
            if (ShowWeenies && _loadedWeenies.Count > 0) {
                var search = SearchText?.Trim();
                IEnumerable<ObjectBrowserItem> filtered = _loadedWeenies;
                if (!string.IsNullOrWhiteSpace(search))
                    filtered = filtered.Where(w =>
                        w.DisplayId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        w.WeenieClassId?.ToString().Contains(search) == true);
                allWeenies = filtered.ToArray();
            }

            var combinedDat = fSetups.Concat(fParts).OrderBy(id => id).ToArray();
            int totalMatches = combinedDat.Length + allWeenies.Length;

            var weeniesTaken = allWeenies.Take(_displayLimit).ToArray();
            int datBudget = Math.Max(0, _displayLimit - weeniesTaken.Length);
            var datTaken = combinedDat.Take(datBudget).ToArray();

            var setupHs = new HashSet<uint>(_allSetupIds);
            var partHs = new HashSet<uint>(_allParticleEmitterIds);
            var setupList = new List<uint>();
            var partList = new List<uint>();
            foreach (var id in datTaken) {
                if (partHs.Contains(id)) partList.Add(id);
                else if (setupHs.Contains(id)) setupList.Add(id);
            }

            FilteredItems = BuildItems(setupList.ToArray(), Array.Empty<uint>(), partList.ToArray(), weeniesTaken);
            HasMore = (weeniesTaken.Length + datTaken.Length) < totalMatches;

            int displayed = weeniesTaken.Length + datTaken.Length;
            if (statusSuffix != null)
                Status = statusSuffix;
            else if (totalMatches == 0)
                Status = "No results";
            else if (HasMore)
                Status = $"Showing {displayed} of {totalMatches} — click Show More";
            else
                Status = $"Showing all {displayed} results";
        }

        [RelayCommand]
        private void ToggleFavorite(ObjectBrowserItem item) {
            if (item == null) return;
            if (_favoriteIds.Contains(item.Id)) {
                _favoriteIds.Remove(item.Id);
                item.IsFavorite = false;
            } else {
                _favoriteIds.Add(item.Id);
                item.IsFavorite = true;
            }
            SaveObjectFavorites();
            if (ShowFavoritesOnly) ApplyFilter();
        }

        private void LoadObjectFavorites() {
            try {
                var path = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "ACME WorldBuilder", "object_browser_favorites.json");
                if (!System.IO.File.Exists(path)) return;
                var json = System.IO.File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                    _favoriteIds.Add(item.GetUInt32());
            } catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] LoadFavorites: {ex.Message}");
            }
        }

        private void SaveObjectFavorites() {
            try {
                var path = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "ACME WorldBuilder", "object_browser_favorites.json");
                var dir = System.IO.Path.GetDirectoryName(path);
                if (dir != null) System.IO.Directory.CreateDirectory(dir);
                var json = System.Text.Json.JsonSerializer.Serialize(_favoriteIds.ToList());
                System.IO.File.WriteAllText(path, json);
            } catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] SaveFavorites: {ex.Message}");
            }
        }

        /// <summary>
        /// Raised when an object is selected for placement, so the editor can switch to the Selector tool.
        /// </summary>
        public event EventHandler? PlacementRequested;

        public void BeginPlacementFromDrop(uint id, bool isSetup, uint? weenieClassId) {
            if (!weenieClassId.HasValue && IsGfxObjDid(id)) {
                Status = "GfxObj meshes can't be placed — pick a Setup or weenie.";
                return;
            }
            var item = FilteredItems.FirstOrDefault(i => i.Id == id)
                       ?? (weenieClassId.HasValue
                           ? new ObjectBrowserItem(id, weenieClassId.Value, $"WCID {weenieClassId.Value}")
                           : new ObjectBrowserItem(id, isSetup, null));
            SelectForPlacement(item);
        }

        [RelayCommand]
        private void SelectForPlacement(ObjectBrowserItem item) {
            if (_context.Project?.IsReadOnlyDatProject == true) {
                Status = "Legacy pre-ToD projects are read-only. Object placement is disabled.";
                return;
            }

            if (item != null && !item.IsParticleEmitter && !item.WeenieClassId.HasValue && IsGfxObjDid(item.Id)) {
                Status = "GfxObj meshes can't be placed — pick a Setup or weenie.";
                return;
            }

            _context.ObjectSelection.IsPlacementMode = true;
            _context.ObjectSelection.PendingWeenieClassId =
                (item.WeenieClassId.HasValue && PlaceWeenieAsInstance) ? item.WeenieClassId : null;
            _context.ObjectSelection.PlacementPreview = new StaticObject {
                Id = item.Id,
                IsSetup = item.IsParticleEmitter ? false : item.IsSetup,
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
                Scale = Vector3.One,
                IsParticleEmitter = item.IsParticleEmitter
            };

            if (item.WeenieClassId.HasValue && PlaceWeenieAsInstance) {
                Status = $"Placing weenie {item.WeenieClassId} as DB instance — click terrain to place, Escape to cancel";
            }
            else if (item.WeenieClassId.HasValue) {
                Status = $"Placing weenie {item.WeenieClassId} as static — click terrain to place, Escape to cancel";
            }
            else {
                Status = item.IsParticleEmitter
                    ? $"Placing particle emitter 0x{item.Id:X8} - click terrain to place, Escape to cancel"
                    : $"Placing 0x{item.Id:X8} - click terrain to place, Escape to cancel";
            }
            Console.WriteLine($"[ObjectBrowser] Selected 0x{item.Id:X8} for placement (IsSetup={item.IsSetup}, particle={item.IsParticleEmitter}, wcid={item.WeenieClassId}, asInstance={PlaceWeenieAsInstance})");

            PlacementRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
