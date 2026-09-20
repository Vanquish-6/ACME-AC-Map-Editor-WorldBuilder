using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Acme.Render.GL.Lib;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Services;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Services;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon {

    public partial class SurfaceBrowserItem : ObservableObject {
        public uint Id { get; }
        public ushort UnqualifiedId { get; }
        public string DisplayId { get; }

        [ObservableProperty]
        private WriteableBitmap? _thumbnail;

        [ObservableProperty]
        private bool _isFavorite;

        public SurfaceBrowserItem(uint id) {
            Id = id;
            UnqualifiedId = (ushort)(id & 0xFFFF);
            DisplayId = $"0x{id:X8}";
        }
    }

    /// <summary>
    /// Browses DAT surfaces (0x08000000 range) for assignment to dungeon cells.
    /// Generates CPU-side thumbnails from texture data.
    /// Has a "Dungeon Surfaces" mode that scans actual dungeon EnvCells to show only
    /// surfaces that are used in existing dungeons (walls, floors, ceilings).
    /// </summary>
    public partial class SurfaceBrowserViewModel : ViewModelBase {
        private const int ThumbSize = 64;

        private readonly IDatReaderWriter _dats;
        private readonly TextureImportService? _textureImport;
        private const int PageSize = 200;

        private uint[] _allSurfaceIds = Array.Empty<uint>();
        private uint[] _dungeonSurfaceIds = Array.Empty<uint>();
        private uint[] _currentCellSurfaceIds = Array.Empty<uint>();
        private uint[] _customSurfaceIds = Array.Empty<uint>();
        private readonly HashSet<uint> _favoriteSurfaceIds = new();
        private bool _dungeonSurfacesScanned;
        private int _displayCount = PageSize;
        private int _totalMatchCount;

        [ObservableProperty] private ObservableCollection<SurfaceBrowserItem> _filteredItems = new();
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private string _status = "Loading surfaces...";
        [ObservableProperty] private bool _showDungeonOnly = true;
        [ObservableProperty] private bool _showCurrentCellOnly;
        [ObservableProperty] private bool _showCustomOnly;
        [ObservableProperty] private bool _showFavoritesOnly;
        [ObservableProperty] private bool _canLoadMore;

        public event EventHandler<ushort>? SurfaceSelected;

        public SurfaceBrowserViewModel(IDatReaderWriter dats, TextureImportService? textureImport = null) {
            _dats = dats;
            _textureImport = textureImport;
            LoadCustomSurfaceIds();
            LoadSurfaceFavorites();
            _ = LoadSurfacesAsync();
        }

        private static TopLevel? GetTopLevel() {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;
            return null;
        }

        private void LoadCustomSurfaceIds() {
            if (_textureImport == null) return;
            _customSurfaceIds = _textureImport.Store.GetDungeonSurfaces()
                .Where(e => e.SurfaceGid != 0)
                .Select(e => e.SurfaceGid)
                .ToArray();
        }

        private async Task LoadSurfacesAsync() {
            var ids = await Task.Run(() => {
                try {
                    return _dats.GetAllIdsOfType<Surface>().OrderBy(id => id).ToArray();
                }
                catch (Exception ex) {
                    Console.WriteLine($"[SurfaceBrowser] Error loading surface IDs: {ex.Message}");
                    return Array.Empty<uint>();
                }
            });

            _allSurfaceIds = ids;
            Status = $"{ids.Length} surfaces loaded";

            Dispatcher.UIThread.Post(ApplyFilter);

            _ = ScanDungeonSurfacesAsync();
        }

        /// <summary>
        /// Scans all dungeon landblocks in cell.dat to find surface IDs actually used
        /// by dungeon EnvCells. This filters thousands of surfaces down to the ~100-200
        /// that are relevant for dungeon wall/floor/ceiling textures.
        /// </summary>
        private async Task ScanDungeonSurfacesAsync() {
            var dungeonSurfaces = await Task.Run(() => {
                var surfaceSet = new HashSet<uint>();
                try {
                    var lbiIds = _dats.GetAllIdsOfType<LandBlockInfo>().ToArray();

                    if (lbiIds.Length == 0) {
                        for (uint x = 0; x < 255; x++) {
                            for (uint y = 0; y < 255; y++) {
                                var infoId = (uint)(((x << 8) | y) << 16 | 0xFFFE);
                                if (_dats.TryGet<LandBlockInfo>(infoId, out var lbi) && lbi.NumCells > 0) {
                                    CollectDungeonSurfaces(lbi, infoId >> 16, surfaceSet);
                                }
                            }
                        }
                    }
                    else {
                        foreach (var lbiId in lbiIds) {
                            if (!_dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0)
                                continue;
                            CollectDungeonSurfaces(lbi, lbiId >> 16, surfaceSet);
                        }
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"[SurfaceBrowser] Error scanning dungeon surfaces: {ex.Message}");
                }
                return surfaceSet.OrderBy(id => id).ToArray();
            });

            _dungeonSurfaceIds = dungeonSurfaces;
            _dungeonSurfacesScanned = true;
            Console.WriteLine($"[SurfaceBrowser] Found {_dungeonSurfaceIds.Length} unique dungeon surfaces from DAT scan");

            Dispatcher.UIThread.Post(ApplyFilter);
        }

        private void CollectDungeonSurfaces(LandBlockInfo lbi, uint lbId, HashSet<uint> surfaceSet) {
            int buildingCellCount = 0;
            foreach (var b in lbi.Buildings) {
                buildingCellCount += b.Portals.Count > 0 ? b.Portals.Count * 4 : 2;
            }
            if (lbi.NumCells <= (uint)buildingCellCount && lbi.Buildings.Count > 0)
                return;

            for (uint i = 0; i < lbi.NumCells; i++) {
                uint cellId = (lbId << 16) | (0x0100 + i);
                if (_dats.TryGet<EnvCell>(cellId, out var envCell)) {
                    foreach (var surfId in envCell.Surfaces) {
                        surfaceSet.Add((uint)(surfId | 0x08000000));
                    }
                }
            }
        }

        /// <summary>
        /// Called by the editor when a cell is selected to update the "current cell" surface filter.
        /// </summary>
        public void SetCurrentCellSurfaces(IReadOnlyList<ushort>? surfaces) {
            if (surfaces == null || surfaces.Count == 0) {
                _currentCellSurfaceIds = Array.Empty<uint>();
            }
            else {
                _currentCellSurfaceIds = surfaces.Select(s => (uint)(s | 0x08000000)).Distinct().ToArray();
            }

            if (ShowCurrentCellOnly) {
                ApplyFilter();
            }
        }

        partial void OnSearchTextChanged(string value) { _displayCount = PageSize; ApplyFilter(); }
        partial void OnShowDungeonOnlyChanged(bool value) {
            if (value) { ShowCurrentCellOnly = false; ShowCustomOnly = false; ShowFavoritesOnly = false; }
            _displayCount = PageSize;
            ApplyFilter();
        }
        partial void OnShowCurrentCellOnlyChanged(bool value) {
            if (value) { ShowDungeonOnly = false; ShowCustomOnly = false; ShowFavoritesOnly = false; }
            _displayCount = PageSize;
            ApplyFilter();
        }
        partial void OnShowCustomOnlyChanged(bool value) {
            if (value) { ShowDungeonOnly = false; ShowCurrentCellOnly = false; ShowFavoritesOnly = false; }
            _displayCount = PageSize;
            ApplyFilter();
        }
        partial void OnShowFavoritesOnlyChanged(bool value) {
            if (value) { ShowDungeonOnly = false; ShowCurrentCellOnly = false; ShowCustomOnly = false; }
            _displayCount = PageSize;
            ApplyFilter();
        }

        [RelayCommand]
        private void LoadMore() {
            _displayCount += PageSize;
            ApplyFilter();
        }

        [RelayCommand]
        private async Task ImportSurface() {
            var topLevel = GetTopLevel();
            if (topLevel == null || _textureImport == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Import Surface Texture",
                AllowMultiple = false,
                FileTypeFilter = new[] {
                    new FilePickerFileType("Image Files") {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" }
                    }
                }
            });

            if (files.Count == 0) return;
            var localPath = files[0].TryGetLocalPath();
            if (localPath == null) return;

            try {
                var name = Path.GetFileNameWithoutExtension(localPath);
                var entry = _textureImport.ImportDungeonSurface(localPath, name);

                LoadCustomSurfaceIds();
                var combined = new HashSet<uint>(_allSurfaceIds);
                combined.UnionWith(_customSurfaceIds);
                _allSurfaceIds = combined.OrderBy(id => id).ToArray();
                ApplyFilter();

                Console.WriteLine($"[SurfaceBrowser] Imported custom surface: {name} (0x{entry.SurfaceGid:X8})");
            }
            catch (Exception ex) {
                Console.WriteLine($"[SurfaceBrowser] Failed to import surface: {ex.Message}");
            }
        }

        private void ApplyFilter() {
            IEnumerable<uint> surfaces;

            if (ShowFavoritesOnly) {
                surfaces = _favoriteSurfaceIds.OrderBy(id => id);
            }
            else if (ShowCustomOnly && _customSurfaceIds.Length > 0) {
                surfaces = _customSurfaceIds;
            }
            else if (ShowCurrentCellOnly && _currentCellSurfaceIds.Length > 0) {
                surfaces = _currentCellSurfaceIds;
            }
            else if (ShowDungeonOnly && _dungeonSurfacesScanned && _dungeonSurfaceIds.Length > 0) {
                surfaces = _dungeonSurfaceIds;
            }
            else {
                surfaces = _allSurfaceIds;
            }

            if (!string.IsNullOrWhiteSpace(SearchText)) {
                var hex = SearchText.TrimStart('0', 'x', 'X').ToUpperInvariant();
                if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out _)) {
                    surfaces = surfaces.Where(id => id.ToString("X8").Contains(hex));
                }
            }

            var allMatches = surfaces as uint[] ?? surfaces.ToArray();
            _totalMatchCount = allMatches.Length;
            var result = allMatches.Take(_displayCount).ToArray();
            var itemsList = new List<SurfaceBrowserItem>(result.Length);
            foreach (var id in result) {
                itemsList.Add(new SurfaceBrowserItem(id));
            }
            foreach (var item in itemsList)
                item.IsFavorite = _favoriteSurfaceIds.Contains(item.Id);

            FilteredItems = new ObservableCollection<SurfaceBrowserItem>(itemsList);
            CanLoadMore = result.Length < _totalMatchCount;

            string filterLabel = ShowFavoritesOnly ? "favorites" :
                                 ShowCustomOnly ? "custom" :
                                 ShowCurrentCellOnly ? "cell" :
                                 (ShowDungeonOnly && _dungeonSurfaceIds.Length > 0) ? "dungeon" : "all";
            Status = result.Length < _totalMatchCount
                ? $"Showing {result.Length} of {_totalMatchCount} surfaces ({filterLabel})"
                : $"Showing {result.Length} surfaces ({filterLabel})";

            _ = GenerateThumbnailsAsync(FilteredItems);
        }

        private async Task GenerateThumbnailsAsync(ObservableCollection<SurfaceBrowserItem> items) {
            var snapshot = items.ToArray();
            foreach (var item in snapshot) {
                var bitmap = await Task.Run(() => {
                    var customEntry = _textureImport?.Store.GetDungeonSurfaces()
                        .FirstOrDefault(e => e.SurfaceGid == item.Id);
                    if (customEntry != null) {
                        return _textureImport!.GenerateThumbnail(customEntry, ThumbSize);
                    }
                    return RenderSurfaceThumbnail(item.Id);
                });
                if (bitmap != null) {
                    Dispatcher.UIThread.Post(() => item.Thumbnail = bitmap);
                }
            }
        }

        private WriteableBitmap? RenderSurfaceThumbnail(uint surfaceId) =>
            DecodeSurfaceToBitmap(surfaceId, _dats, ThumbSize);

        /// <summary>
        /// Decode texture data to a CPU bitmap. Tries <see cref="RenderSurface"/> at <paramref name="surfaceId"/> first
        /// (UI/layout media and icons use 0x06… ids), then the <see cref="Surface"/> → <see cref="SurfaceTexture"/> chain (0x08…).
        /// </summary>
        internal static WriteableBitmap? DecodeSurfaceToBitmap(uint surfaceId, IDatReaderWriter dats, int maxEdge) {
            try {
                // Only attempt RenderSurface shortcut for 0x06 IDs; 0x08 IDs are Surface
                // records and TryGet<RenderSurface> on them can throw during deserialization.
                if ((surfaceId & 0xFF000000) == 0x06000000) {
                    if (dats.TryGet<RenderSurface>(surfaceId, out var rsDirect) && rsDirect != null) {
                        var fromRs = TryBitmapFromRenderSurface(rsDirect, dats, maxEdge);
                        if (fromRs != null) return fromRs;
                    }
                }

                if (!dats.TryGet<Surface>(surfaceId, out var surface) || surface == null) return null;

                bool isSolid = surface.Type.HasFlag(SurfaceType.Base1Solid);
                if (isSolid) {
                    var solidData = TextureHelpers.CreateSolidColorTexture(surface.ColorValue, maxEdge, maxEdge);
                    return CreateBitmap(solidData, maxEdge, maxEdge);
                }

                if (!dats.TryGet<SurfaceTexture>(surface.OrigTextureId, out var surfaceTexture) ||
                    surfaceTexture.Textures?.Any() != true) {
                    return null;
                }

                var renderSurfaceId = surfaceTexture.Textures.Last();
                if (!dats.TryGet<RenderSurface>(renderSurfaceId, out var renderSurface) || renderSurface == null)
                    return null;

                uint paletteOverrideId = surface.OrigPaletteId != 0
                    ? (uint)surface.OrigPaletteId
                    : renderSurface.DefaultPaletteId;
                bool transparentLowClipIndices = surface.Type.HasFlag(SurfaceType.Base1ClipMap);
                return TryBitmapFromRenderSurface(renderSurface, dats, maxEdge, paletteOverrideId, transparentLowClipIndices);
            }
            catch {
                return null;
            }
        }

        const long MaxDecodePixels = 4096L * 4096L;

        internal static WriteableBitmap? TryBitmapFromRenderSurface(RenderSurface renderSurface, IDatReaderWriter dats, int maxEdge, uint paletteOverrideId = 0, bool transparentLowClipIndices = false) {
            var src = renderSurface.SourceData;
            int w = renderSurface.Width, h = renderSurface.Height;
            if (src == null || src.Length == 0 || w <= 0 || h <= 0) return null;
            if ((long)w * h > MaxDecodePixels) return null;

            byte[]? rgba = null;

            try {
                switch (renderSurface.FormatEnum) {
                    case PixelFormat.PFID_A8R8G8B8:
                        if (src.Length < (long)w * h * 4) return null;
                        rgba = DatIconLoader.SwizzleBgraToRgba(src, w * h);
                        break;

                    case PixelFormat.PFID_R8G8B8:
                        if (src.Length < (long)w * h * 3) return null;
                        rgba = new byte[w * h * 4];
                        for (int i = 0; i < w * h; i++) {
                            rgba[i * 4 + 0] = src[i * 3 + 2];
                            rgba[i * 4 + 1] = src[i * 3 + 1];
                            rgba[i * 4 + 2] = src[i * 3 + 0];
                            rgba[i * 4 + 3] = 255;
                        }
                        break;

                    case PixelFormat.PFID_INDEX16:
                        if (src.Length < (long)w * h * 2) return null;
                        uint paletteId = paletteOverrideId != 0 ? paletteOverrideId : renderSurface.DefaultPaletteId;
                        if (!dats.TryGet<Palette>(paletteId, out var palette) ||
                            palette.Colors == null || palette.Colors.Count == 0) return null;
                        rgba = new byte[w * h * 4];
                        bool expand256Palette = !WorldBuilder.Shared.Lib.LegacyDatReader.IsSyntheticRenderSurfaceId(renderSurface.Id);
                        FillIndex16Safe(src, palette, rgba.AsSpan(), w, h, transparentLowClipIndices, expand256Palette);
                        break;

                    case PixelFormat.PFID_DXT1:
                        rgba = DecompressDxt1(src, w, h);
                        break;

                    case PixelFormat.PFID_DXT3:
                    case PixelFormat.PFID_DXT5:
                        rgba = DecompressDxt5(src, w, h,
                            renderSurface.FormatEnum == PixelFormat.PFID_DXT3);
                        break;

                    default:
                        return null;
                }
            }
            catch {
                return null;
            }

            if (rgba == null) return null;
            int dw = maxEdge, dh = maxEdge;
            if (w > h) {
                dh = System.Math.Max(1, h * maxEdge / w);
            }
            else if (h > w) {
                dw = System.Math.Max(1, w * maxEdge / h);
            }
            if (w != dw || h != dh) {
                rgba = DownsampleNearest(rgba, w, h, dw, dh);
            }
            return CreateBitmap(rgba, dw, dh);
        }

        /// <summary>
        /// Like <see cref="TextureHelpers.FillIndex16"/> but skips out-of-range palette indices (corrupt UI mips).
        /// </summary>
        static void FillIndex16Safe(byte[] src, Palette palette, Span<byte> dst, int width, int height, bool transparentLowClipIndices = false, bool expand256Palette = true) {
            var colors = palette.Colors;
            var useExpanded = expand256Palette && colors.Count == 256 ? TextureHelpers.ExpandPalette256To2048(colors) : null;
            int palCount = useExpanded?.Length ?? colors.Count;
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    var srcIdx = (y * width + x) * 2;
                    var palIdx = (ushort)(src[srcIdx] | (src[srcIdx + 1] << 8));
                    var dstIdx = (y * width + x) * 4;
                    if (transparentLowClipIndices && palIdx < 8) {
                        dst[dstIdx + 0] = 0;
                        dst[dstIdx + 1] = 0;
                        dst[dstIdx + 2] = 0;
                        dst[dstIdx + 3] = 0;
                        continue;
                    }
                    if (palIdx >= palCount) {
                        dst[dstIdx + 0] = 0;
                        dst[dstIdx + 1] = 0;
                        dst[dstIdx + 2] = 0;
                        dst[dstIdx + 3] = 0;
                        continue;
                    }
                    ColorARGB color = useExpanded != null
                        ? useExpanded[palIdx]
                        : ColorARGB.FromPacked(colors[palIdx]);
                    dst[dstIdx + 0] = color.Red;
                    dst[dstIdx + 1] = color.Green;
                    dst[dstIdx + 2] = color.Blue;
                    dst[dstIdx + 3] = color.Alpha;
                }
            }
        }

        internal static WriteableBitmap CreateBitmap(byte[] rgba, int width, int height) {
            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Rgba8888);
            using (var fb = bitmap.Lock()) {
                Marshal.Copy(rgba, 0, fb.Address, Math.Min(rgba.Length, fb.RowBytes * height));
            }
            return bitmap;
        }

        internal static byte[] DownsampleNearest(byte[] src, int srcW, int srcH, int dstW, int dstH) {
            var dst = new byte[dstW * dstH * 4];
            for (int y = 0; y < dstH; y++) {
                int srcY = y * srcH / dstH;
                for (int x = 0; x < dstW; x++) {
                    int srcX = x * srcW / dstW;
                    int si = (srcY * srcW + srcX) * 4;
                    int di = (y * dstW + x) * 4;
                    dst[di] = src[si]; dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2]; dst[di + 3] = src[si + 3];
                }
            }
            return dst;
        }

        internal static byte[] DecompressDxt1(byte[] data, int width, int height) {
            var rgba = new byte[width * height * 4];
            int blocksW = Math.Max(1, (width + 3) / 4);
            int blocksH = Math.Max(1, (height + 3) / 4);
            int offset = 0;

            for (int by = 0; by < blocksH; by++) {
                for (int bx = 0; bx < blocksW; bx++) {
                    if (offset + 8 > data.Length) break;
                    ushort c0 = (ushort)(data[offset] | (data[offset + 1] << 8));
                    ushort c1 = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
                    uint lookupTable = (uint)(data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16) | (data[offset + 7] << 24));
                    offset += 8;

                    var colors = new byte[4][];
                    colors[0] = TextureHelpers.Color565ToRgba(c0);
                    colors[1] = TextureHelpers.Color565ToRgba(c1);

                    if (c0 > c1) {
                        colors[2] = new byte[] { (byte)((2 * colors[0][0] + colors[1][0] + 1) / 3), (byte)((2 * colors[0][1] + colors[1][1] + 1) / 3), (byte)((2 * colors[0][2] + colors[1][2] + 1) / 3), 255 };
                        colors[3] = new byte[] { (byte)((colors[0][0] + 2 * colors[1][0] + 1) / 3), (byte)((colors[0][1] + 2 * colors[1][1] + 1) / 3), (byte)((colors[0][2] + 2 * colors[1][2] + 1) / 3), 255 };
                    }
                    else {
                        colors[2] = new byte[] { (byte)((colors[0][0] + colors[1][0]) / 2), (byte)((colors[0][1] + colors[1][1]) / 2), (byte)((colors[0][2] + colors[1][2]) / 2), 255 };
                        colors[3] = new byte[] { 0, 0, 0, 0 };
                    }

                    for (int row = 0; row < 4; row++) {
                        for (int col = 0; col < 4; col++) {
                            int px = bx * 4 + col, py = by * 4 + row;
                            if (px >= width || py >= height) { lookupTable >>= 2; continue; }
                            int idx = (int)(lookupTable & 3);
                            lookupTable >>= 2;
                            int di = (py * width + px) * 4;
                            rgba[di] = colors[idx][0]; rgba[di + 1] = colors[idx][1];
                            rgba[di + 2] = colors[idx][2]; rgba[di + 3] = colors[idx][3];
                        }
                    }
                }
            }
            return rgba;
        }

        internal static byte[] DecompressDxt5(byte[] data, int width, int height, bool isDxt3) {
            var rgba = new byte[width * height * 4];
            int blocksW = Math.Max(1, (width + 3) / 4);
            int blocksH = Math.Max(1, (height + 3) / 4);
            int offset = 0;

            for (int by = 0; by < blocksH; by++) {
                for (int bx = 0; bx < blocksW; bx++) {
                    if (offset + 16 > data.Length) break;

                    byte[] alphas = new byte[16];
                    if (isDxt3) {
                        for (int i = 0; i < 4; i++) {
                            ushort row = (ushort)(data[offset + i * 2] | (data[offset + i * 2 + 1] << 8));
                            for (int j = 0; j < 4; j++) {
                                alphas[i * 4 + j] = (byte)(((row >> (j * 4)) & 0xF) * 17);
                            }
                        }
                    }
                    else {
                        byte a0 = data[offset], a1 = data[offset + 1];
                        byte[] aLut = new byte[8];
                        aLut[0] = a0; aLut[1] = a1;
                        if (a0 > a1) {
                            for (int i = 1; i <= 6; i++) aLut[i + 1] = (byte)(((7 - i) * a0 + i * a1 + 3) / 7);
                        }
                        else {
                            for (int i = 1; i <= 4; i++) aLut[i + 1] = (byte)(((5 - i) * a0 + i * a1 + 2) / 5);
                            aLut[6] = 0; aLut[7] = 255;
                        }
                        ulong bits = 0;
                        for (int i = 0; i < 6; i++) bits |= (ulong)data[offset + 2 + i] << (8 * i);
                        for (int i = 0; i < 16; i++) {
                            alphas[i] = aLut[(bits >> (3 * i)) & 7];
                        }
                    }
                    offset += 8;

                    ushort c0 = (ushort)(data[offset] | (data[offset + 1] << 8));
                    ushort c1 = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
                    uint lookupTable = (uint)(data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16) | (data[offset + 7] << 24));
                    offset += 8;

                    var colors = new byte[4][];
                    colors[0] = TextureHelpers.Color565ToRgba(c0);
                    colors[1] = TextureHelpers.Color565ToRgba(c1);
                    colors[2] = new byte[] { (byte)((2 * colors[0][0] + colors[1][0] + 1) / 3), (byte)((2 * colors[0][1] + colors[1][1] + 1) / 3), (byte)((2 * colors[0][2] + colors[1][2] + 1) / 3), 255 };
                    colors[3] = new byte[] { (byte)((colors[0][0] + 2 * colors[1][0] + 1) / 3), (byte)((colors[0][1] + 2 * colors[1][1] + 1) / 3), (byte)((colors[0][2] + 2 * colors[1][2] + 1) / 3), 255 };

                    for (int row = 0; row < 4; row++) {
                        for (int col = 0; col < 4; col++) {
                            int px = bx * 4 + col, py = by * 4 + row;
                            if (px >= width || py >= height) { lookupTable >>= 2; continue; }
                            int idx = (int)(lookupTable & 3);
                            lookupTable >>= 2;
                            int di = (py * width + px) * 4;
                            rgba[di] = colors[idx][0]; rgba[di + 1] = colors[idx][1];
                            rgba[di + 2] = colors[idx][2]; rgba[di + 3] = alphas[row * 4 + col];
                        }
                    }
                }
            }
            return rgba;
        }

        [RelayCommand]
        private void ToggleFavorite(SurfaceBrowserItem item) {
            if (item == null) return;
            if (_favoriteSurfaceIds.Contains(item.Id)) {
                _favoriteSurfaceIds.Remove(item.Id);
                item.IsFavorite = false;
            } else {
                _favoriteSurfaceIds.Add(item.Id);
                item.IsFavorite = true;
            }
            SaveSurfaceFavorites();
            if (ShowFavoritesOnly) ApplyFilter();
        }

        private void LoadSurfaceFavorites() {
            try {
                var path = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "ACME WorldBuilder", "surface_favorites.json");
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                    _favoriteSurfaceIds.Add(el.GetUInt32());
            } catch (Exception ex) {
                Console.WriteLine($"[SurfaceBrowser] LoadFavorites: {ex.Message}");
            }
        }

        private void SaveSurfaceFavorites() {
            try {
                var path = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "ACME WorldBuilder", "surface_favorites.json");
                var dir = Path.GetDirectoryName(path);
                if (dir != null) Directory.CreateDirectory(dir);
                var json = System.Text.Json.JsonSerializer.Serialize(_favoriteSurfaceIds.ToList());
                File.WriteAllText(path, json);
            } catch (Exception ex) {
                Console.WriteLine($"[SurfaceBrowser] SaveFavorites: {ex.Message}");
            }
        }

        [RelayCommand]
        private void SelectSurface(SurfaceBrowserItem item) {
            SurfaceSelected?.Invoke(this, item.UnqualifiedId);
        }
    }
}
