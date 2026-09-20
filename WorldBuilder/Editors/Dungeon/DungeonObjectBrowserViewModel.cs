using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Converters;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.AceDb;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon {
    /// <summary>
    /// Object browser for the Dungeon Editor. Browses Setup objects
    /// from the DAT files for placement as static objects inside dungeon cells.
    /// GfxObj entries are intentionally excluded — only Setup DIDs (0x02xxxxxx)
    /// are valid stab references; placing a GfxObj directly would be rejected
    /// by the placement guard and is not renderable via the standard client path.
    /// Adapted from the landscape ObjectBrowserViewModel without terrain context dependencies.
    /// </summary>
    public partial class DungeonObjectBrowserViewModel : ViewModelBase {
        private readonly IDatReaderWriter _dats;
        private readonly ObjectTagIndex _tagIndex = new();
        private readonly Func<ThumbnailRenderService?> _getThumbnailService;
        private readonly ThumbnailCache _thumbnailCache;
        private readonly WorldBuilderSettings? _settings;
        private bool _thumbnailsReady;
        private bool _subscribedToThumbnailReady;
        private bool _displayActivated;
        private uint[] _allSetupIds = Array.Empty<uint>();
        private List<ObjectBrowserItem> _loadedWeenies = new();

        private readonly Dictionary<uint, ObjectBrowserItem> _itemLookup = new();
        private readonly HashSet<uint> _favoriteIds = new();

        [ObservableProperty] private ObservableCollection<ObjectBrowserItem> _filteredItems = new();
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private string _status = "Search by name or hex ID";
        [ObservableProperty] private bool _showSetups = true;
        [ObservableProperty] private bool _showWeenies;
        [ObservableProperty] private bool _showFavoritesOnly;
        [ObservableProperty] private bool _isLoadingWeenies;
        [ObservableProperty] private bool _hasMore;

        private const int BatchSize = 100;
        private int _displayLimit = BatchSize;

        public ObjectTagIndex TagIndex => _tagIndex;

        public event EventHandler<ObjectBrowserItem>? PlacementRequested;

        /// <summary>
        /// Fired after weenies are loaded from the DB, with all WCID->SetupId mappings
        /// so the scene can cache them for rendering.
        /// </summary>
        public event EventHandler<IReadOnlyList<(uint WeenieClassId, uint SetupId)>>? WeenieSetupsLoaded;

        public DungeonObjectBrowserViewModel(IDatReaderWriter dats,
            Func<ThumbnailRenderService?>? getThumbnailService = null,
            ThumbnailCache? thumbnailCache = null,
            WorldBuilderSettings? settings = null) {
            _dats = dats;
            _getThumbnailService = getThumbnailService ?? (() => null);
            _thumbnailCache = thumbnailCache ?? new ThumbnailCache(_dats.CacheNamespace);
            _settings = settings;

            _tagIndex.LoadFromEmbeddedResource();
            ObjectIdToTagsConverter.TagIndex = _tagIndex;

            try {
                _allSetupIds = _dats.GetAllIdsOfType<Setup>().OrderBy(id => id).ToArray();
            }
            catch (Exception ex) {
                Console.WriteLine($"[DungeonObjectBrowser] Error loading object IDs: {ex.Message}");
            }

            LoadObjectFavorites();
            ApplyFilter();
        }

        /// <summary>
        /// Heavy GPU thumbnail work stays off until the Objects tool is actually using this panel.
        /// </summary>
        public void ActivateForDisplay() {
            if (_displayActivated) {
                return;
            }

            _displayActivated = true;
            _ = Task.Run(async () => {
                await Task.Delay(2000);
                for (int i = 0; i < 30; i++) {
                    if (_getThumbnailService() != null) break;
                    await Task.Delay(500);
                }
                _thumbnailsReady = true;
                Dispatcher.UIThread.Post(() => RequestThumbnails(FilteredItems));
            });
        }

        private void OnThumbnailReady(uint objectId, byte[] rgbaPixels) {
            _thumbnailCache.SaveAsync(objectId, rgbaPixels, ThumbnailRenderService.ThumbnailSize, ThumbnailRenderService.ThumbnailSize);
            var bitmap = ThumbnailCache.CreateBitmapFromRgba(rgbaPixels,
                ThumbnailRenderService.ThumbnailSize, ThumbnailRenderService.ThumbnailSize);
            Dispatcher.UIThread.Post(() => {
                if (_itemLookup.TryGetValue(objectId, out var item)) {
                    item.Thumbnail = bitmap;
                }
            });
        }

        partial void OnSearchTextChanged(string value) { _displayLimit = BatchSize; ApplyFilter(); }
        partial void OnShowSetupsChanged(bool value) { _displayLimit = BatchSize; ApplyFilter(); }
        partial void OnShowWeeniesChanged(bool value) { _displayLimit = BatchSize; ApplyFilter(); }
        partial void OnShowFavoritesOnlyChanged(bool value) { _displayLimit = BatchSize; ApplyFilter(); }

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

        private static bool IsHexSearch(string text, out string normalizedHex) {
            normalizedHex = text.TrimStart('0', 'x', 'X').ToUpperInvariant();
            return uint.TryParse(normalizedHex, System.Globalization.NumberStyles.HexNumber, null, out _);
        }

        private (IEnumerable<uint> setups, IEnumerable<uint> gfxObjs) ApplySearchFilter(
            IEnumerable<uint> setups, IEnumerable<uint> gfxObjs, out string? statusSuffix) {
            statusSuffix = null;
            if (string.IsNullOrWhiteSpace(SearchText)) return (setups, gfxObjs);

            if (IsHexSearch(SearchText, out var hexSearch)) {
                return (
                    setups.Where(id => id.ToString("X8").Contains(hexSearch)),
                    gfxObjs.Where(id => id.ToString("X8").Contains(hexSearch))
                );
            }

            if (!_tagIndex.IsLoaded) {
                statusSuffix = "(keyword index not loaded)";
                return (Array.Empty<uint>(), Array.Empty<uint>());
            }

            var matchedIds = _tagIndex.Search(SearchText);
            if (matchedIds.Count == 0) {
                statusSuffix = $"No results for \"{SearchText}\"";
                return (Array.Empty<uint>(), Array.Empty<uint>());
            }

            statusSuffix = $"Found {matchedIds.Count} matches for \"{SearchText}\"";
            return (
                setups.Where(id => matchedIds.Contains(id)),
                gfxObjs.Where(id => matchedIds.Contains(id))
            );
        }

        private ObservableCollection<ObjectBrowserItem> BuildItems(uint[] setups, uint[] gfxObjs, ObjectBrowserItem[]? weenies = null) {
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

            foreach (var item in items)
                item.IsFavorite = _favoriteIds.Contains(item.Id);

            if (_thumbnailsReady) {
                RequestThumbnails(items);
            }

            return items;
        }

        private void RequestThumbnails(ObservableCollection<ObjectBrowserItem> items) {
            var service = _getThumbnailService();

            if (service != null && !_subscribedToThumbnailReady) {
                service.ThumbnailReady += OnThumbnailReady;
                _subscribedToThumbnailReady = true;
            }

            foreach (var item in items) {
                if (item.Thumbnail != null || item.Id == 0) continue;

                var cachedBitmap = _thumbnailCache.TryLoadCached(item.Id);
                if (cachedBitmap != null) {
                    item.Thumbnail = cachedBitmap;
                    continue;
                }

                service?.RequestThumbnail(item.Id, item.IsSetup);
            }
        }

        private void ApplyFilter() {
            if (ShowFavoritesOnly && _favoriteIds.Count > 0) {
                var favSetups = _favoriteIds.Where(id => (id & 0xFF000000) == 0x02000000).OrderBy(id => id);
                var (fs, _) = ApplySearchFilter(favSetups, Enumerable.Empty<uint>(), out var sfx);
                var setupArr = fs.ToArray();
                FilteredItems = BuildItems(setupArr, Array.Empty<uint>());
                Status = sfx ?? $"{setupArr.Length} favorites";
                HasMore = false;
                return;
            }
            if (ShowFavoritesOnly) {
                FilteredItems = new ObservableCollection<ObjectBrowserItem>();
                Status = "No favorites yet — click the star on any object to add it";
                HasMore = false;
                return;
            }

            IEnumerable<uint> setups = ShowSetups ? _allSetupIds : Array.Empty<uint>();

            var (fSetups, _) = ApplySearchFilter(setups, Enumerable.Empty<uint>(), out var statusSuffix);

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

            var combinedDat = fSetups.ToArray();
            int totalMatches = combinedDat.Length + allWeenies.Length;
            int showing = Math.Min(totalMatches, _displayLimit);

            var weeniesTaken = allWeenies.Take(_displayLimit).ToArray();
            int datBudget = Math.Max(0, _displayLimit - weeniesTaken.Length);
            var datTaken = combinedDat.Take(datBudget).ToArray();

            FilteredItems = BuildItems(datTaken, Array.Empty<uint>(), weeniesTaken);
            HasMore = showing < totalMatches;

            int displayed = weeniesTaken.Length + datTaken.Length;
            if (statusSuffix != null) {
                Status = statusSuffix;
            }
            else if (totalMatches == 0) {
                Status = "No results";
            }
            else if (HasMore) {
                Status = $"Showing {displayed} of {totalMatches} — click Show More";
            }
            else {
                Status = $"Showing all {displayed} results";
            }
        }

        [RelayCommand]
        private void ShowMore() {
            _displayLimit += BatchSize;
            ApplyFilter();
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
                foreach (var el in doc.RootElement.EnumerateArray())
                    _favoriteIds.Add(el.GetUInt32());
            } catch (Exception ex) {
                Console.WriteLine($"[DungeonObjectBrowser] LoadFavorites: {ex.Message}");
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
                Console.WriteLine($"[DungeonObjectBrowser] SaveFavorites: {ex.Message}");
            }
        }

        public Avalonia.Media.Imaging.Bitmap? FindThumbnail(uint id) =>
            _itemLookup.TryGetValue(id, out var item) ? item.Thumbnail : null;

        [RelayCommand]
        private void SelectForPlacement(ObjectBrowserItem item) {
            if (item != null && !item.WeenieClassId.HasValue && (item.Id & 0xFF000000) == 0x01000000) {
                Status = "GfxObj meshes can't be placed — pick a Setup or weenie.";
                return;
            }
            Status = $"Placing 0x{item.Id:X8} - click in cell to place, Escape to cancel";
            PlacementRequested?.Invoke(this, item);
        }
    }
}
