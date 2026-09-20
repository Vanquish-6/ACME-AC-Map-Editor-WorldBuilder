using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WorldBuilder.Services;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class TerrainTextureItem : ViewModelBase {
        public TerrainTextureType TextureType { get; }
        public TerrainTexturePaletteViewModel Owner { get; set; } = null!;

        [ObservableProperty]
        private Bitmap? _thumbnail;

        public string DisplayName { get; }

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        private bool _isCustom;

        public TerrainTextureItem(TerrainTextureType type, Bitmap? thumbnail, bool isCustom = false) {
            TextureType = type;
            Thumbnail = thumbnail;
            DisplayName = type.ToString();
            IsCustom = isCustom;
        }
    }

    public partial class TerrainTexturePaletteViewModel : ViewModelBase {
        private readonly LandSurfaceManager _surfaceManager;
        private readonly TextureImportService? _textureImport;

        public ObservableCollection<TerrainTextureItem> Textures { get; } = new();

        [ObservableProperty]
        private TerrainTextureItem? _selectedTexture;

        /// <summary>
        /// Fires when the user selects a different texture in the palette.
        /// </summary>
        public event EventHandler<TerrainTextureType>? TextureSelected;

        /// <summary>
        /// Fires when a terrain texture is replaced and the terrain mesh needs rebuilding.
        /// </summary>
        public event EventHandler? TerrainTextureReplaced;

        public TerrainTexturePaletteViewModel(LandSurfaceManager surfaceManager, TextureImportService? textureImport = null) {
            _surfaceManager = surfaceManager;
            _textureImport = textureImport;

            var thumbnails = surfaceManager.GetTerrainThumbnails(64);
            var available = surfaceManager.GetAvailableTerrainTextures();

            foreach (var desc in available) {
                thumbnails.TryGetValue(desc.TerrainType, out var thumb);
                var hasCustom = textureImport?.Store.GetTerrainReplacement((int)desc.TerrainType) != null;
                TerrainTextureItem item;
                if (hasCustom) {
                    var customThumb = textureImport!.GenerateThumbnail(
                        textureImport.Store.GetTerrainReplacement((int)desc.TerrainType)!, 64);
                    item = new TerrainTextureItem(desc.TerrainType, customThumb ?? thumb, isCustom: true);
                }
                else {
                    item = new TerrainTextureItem(desc.TerrainType, thumb);
                }
                item.Owner = this;
                Textures.Add(item);
            }

            if (Textures.Count > 0) {
                SelectedTexture = Textures[0];
                SelectedTexture.IsSelected = true;
            }

            ApplyCustomTerrainTextures();
        }

        private static TopLevel? GetTopLevel() {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;
            return null;
        }

        [RelayCommand]
        private void SelectTexture(TerrainTextureItem item) {
            if (SelectedTexture != null) {
                SelectedTexture.IsSelected = false;
            }
            SelectedTexture = item;
            item.IsSelected = true;
            TextureSelected?.Invoke(this, item.TextureType);
        }

        [RelayCommand]
        private async Task ReplaceTexture(TerrainTextureItem item) {
            var topLevel = GetTopLevel();
            if (topLevel == null || _textureImport == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = $"Replace texture: {item.DisplayName}",
                AllowMultiple = false,
                FileTypeFilter = new[] {
                    new Avalonia.Platform.Storage.FilePickerFileType("Image Files") {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" }
                    }
                }
            });

            if (files.Count == 0) return;
            var localPath = files[0].TryGetLocalPath();
            if (localPath == null) return;

            try {
                var entry = _textureImport.ImportTerrainReplacement(localPath, item.DisplayName, item.TextureType);

                var rgbaData = _textureImport.LoadTextureRgba(entry, 512, 512);
                if (rgbaData != null) {
                    _surfaceManager.ReplaceTerrainTexture(item.TextureType, rgbaData);
                }

                var newThumb = _textureImport.GenerateThumbnail(entry, 64);
                item.Thumbnail = newThumb;
                item.IsCustom = true;

                TerrainTextureReplaced?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) {
                Console.WriteLine($"[TerrainPalette] Failed to replace texture: {ex.Message}");
            }
        }

        [RelayCommand]
        private void RevertTexture(TerrainTextureItem item) {
            if (_textureImport == null) return;

            var existing = _textureImport.Store.GetTerrainReplacement((int)item.TextureType);
            if (existing == null) return;

            _textureImport.Store.Remove(existing.Id);

            var thumbnails = _surfaceManager.GetTerrainThumbnails(64);
            thumbnails.TryGetValue(item.TextureType, out var originalThumb);
            item.Thumbnail = originalThumb;
            item.IsCustom = false;

            // TODO: reload original texture into atlas (requires re-reading from DAT)
        }

        /// <summary>
        /// Applies all persisted custom terrain texture replacements to the atlas.
        /// Should be called after the renderer is registered.
        /// </summary>
        public void ApplyCustomTerrainTextures() {
            if (_textureImport == null) return;

            foreach (var entry in _textureImport.Store.GetTerrainReplacements()) {
                if (entry.ReplacesTerrainType == null) continue;
                var type = (TerrainTextureType)entry.ReplacesTerrainType.Value;

                var rgbaData = _textureImport.LoadTextureRgba(entry, 512, 512);
                if (rgbaData != null) {
                    _surfaceManager.ReplaceTerrainTexture(type, rgbaData);
                }
            }
        }

        public void SyncSelection(TerrainTextureType type) {
            var match = Textures.FirstOrDefault(t => t.TextureType == type);
            if (match != null && match != SelectedTexture) {
                if (SelectedTexture != null) SelectedTexture.IsSelected = false;
                SelectedTexture = match;
                match.IsSelected = true;
            }
        }
    }
}
