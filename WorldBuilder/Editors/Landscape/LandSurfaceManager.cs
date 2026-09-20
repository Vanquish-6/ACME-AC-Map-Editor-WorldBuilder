using Acme.Render;
using Acme.Render.Enums;
using Acme.Render.Vertex;
using Acme.Render.GL;
using Acme.Dat;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Landscape {
    public class LandSurfaceManager {
        private const uint PlaceholderTextureId = 0;
        private const int TerrainAtlasDimension = 512;
        public const int MaxTerrainAtlasLayers = 36;
        private readonly IDatReaderWriter _dats;
        private readonly Region _region;
        private readonly LandSurf _landSurface;
        private readonly Dictionary<uint, int> _textureAtlasIndexLookup;
        private readonly Dictionary<uint, int> _alphaAtlasIndexLookup;
        private readonly float[] _terrainAtlasTilingByLayer = new float[MaxTerrainAtlasLayers];
        private readonly byte[] _textureBuffer;
        private uint _nextSurfaceNumber;

        private readonly Dictionary<OpenGLRenderer, ITextureArray> _terrainAtlases = new();
        private readonly Dictionary<OpenGLRenderer, ITextureArray> _alphaAtlases = new();
        private readonly ConcurrentQueue<(int layerIndex, byte[] rgbaData)> _pendingTextureUpdates = new();
        private readonly object _lock = new();

        private static readonly Vector2[] LandUVs = new Vector2[] {
            new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0)
        };

        private static readonly Vector2[][] LandUVsRotated = new Vector2[4][] {
            new Vector2[] { LandUVs[0], LandUVs[1], LandUVs[2], LandUVs[3] },
            new Vector2[] { LandUVs[3], LandUVs[0], LandUVs[1], LandUVs[2] },
            new Vector2[] { LandUVs[2], LandUVs[3], LandUVs[0], LandUVs[1] },
            new Vector2[] { LandUVs[1], LandUVs[2], LandUVs[3], LandUVs[0] }
        };

        public List<TerrainAlphaMap> CornerTerrainMaps { get; private set; }
        public List<TerrainAlphaMap> SideTerrainMaps { get; private set; }
        public List<RoadAlphaMap> RoadMaps { get; private set; }
        public List<TMTerrainDesc> TerrainDescriptors { get; private set; }
        public Dictionary<uint, SurfaceInfo> SurfaceInfoByPalette { get; private set; }
        public Dictionary<uint, TextureMergeInfo> SurfacesBySurfaceNumber { get; private set; }
        public uint TotalUniqueSurfaces { get; private set; }

        /// <summary>Per terrain-atlas layer <c>TexTiling</c> from Region (matches ImgTex::CopyCSI / TexMerge::CopyAndTile).</summary>
        public ReadOnlySpan<float> TerrainAtlasTilingByLayer => _terrainAtlasTilingByLayer;

        /// <summary>Bits 28–31 embedded in palette codes (1 or 4 per client tex_size).</summary>
        public uint TextureSizeBits { get; }

        /// <summary>Pal-shift regions pick the smallest pal_code slot; standard regions use cell-hash rotation.</summary>
        public bool MinimizePal { get; }

        public LandSurfaceManager(IDatReaderWriter dats, Region region) {
            _dats = dats ?? throw new ArgumentNullException(nameof(dats));
            _region = region ?? throw new ArgumentNullException(nameof(region));
            _textureAtlasIndexLookup = new Dictionary<uint, int>(36);
            _alphaAtlasIndexLookup = new Dictionary<uint, int>(16);
            _textureBuffer = ArrayPool<byte>.Shared.Rent(512 * 512 * 4);

            _landSurface = _region.LandSurf ?? new LandSurf { TexMerge = new TexMerge() };
            var textureMerge = _landSurface.TexMerge ?? new TexMerge();
            textureMerge.CornerTerrainMaps ??= [];
            textureMerge.SideTerrainMaps ??= [];
            textureMerge.RoadMaps ??= [];
            textureMerge.TerrainDesc ??= [];
            _landSurface.TexMerge = textureMerge;
            bool isPalShifted = _landSurface.Type == 1;
            MinimizePal = isPalShifted;
            TextureSizeBits = TerrainPalCode.GetTextureSizeBits(isPalShifted);

            SurfaceInfoByPalette = new Dictionary<uint, SurfaceInfo>();
            SurfacesBySurfaceNumber = new Dictionary<uint, TextureMergeInfo>();
            _nextSurfaceNumber = 0;

            CornerTerrainMaps = textureMerge.CornerTerrainMaps;
            SideTerrainMaps = textureMerge.SideTerrainMaps;
            RoadMaps = textureMerge.RoadMaps;
            TerrainDescriptors = textureMerge.TerrainDesc;
            if (TerrainDescriptors.Count == 0) {
                TerrainDescriptors = new List<TMTerrainDesc> {
                    new() {
                        TerrainType = TerrainTextureType.Grassland,
                        TerrainTex = new TerrainTex {
                            TextureId = PlaceholderTextureId,
                            TexTiling = 1,
                            DetailTexTiling = 1,
                        }
                    }
                };
            }

            // Note: We don't load textures yet, waiting for a renderer to be registered.
        }

        public void RegisterRenderer(OpenGLRenderer renderer) {
            lock (_lock) {
                if (_terrainAtlases.ContainsKey(renderer)) return;

                var terrainAtlas = renderer.GraphicsDevice.CreateTextureArray(TextureFormat.RGBA8, 512, 512, 36)
                    ?? throw new Exception("Unable to create terrain atlas.");
                var alphaAtlas = renderer.GraphicsDevice.CreateTextureArray(TextureFormat.RGBA8, 512, 512, 16)
                    ?? throw new Exception("Unable to create terrain atlas.");

                _terrainAtlases[renderer] = terrainAtlas;
                _alphaAtlases[renderer] = alphaAtlas;

                LoadTextures(renderer, terrainAtlas, alphaAtlas);
            }
        }

        public void UnregisterRenderer(OpenGLRenderer renderer) {
            lock (_lock) {
                if (_terrainAtlases.TryGetValue(renderer, out var t)) {
                    t.Dispose();
                    _terrainAtlases.Remove(renderer);
                }
                if (_alphaAtlases.TryGetValue(renderer, out var a)) {
                    a.Dispose();
                    _alphaAtlases.Remove(renderer);
                }
            }
        }

        public ITextureArray GetTerrainAtlas(OpenGLRenderer renderer) => _terrainAtlases[renderer];
        public ITextureArray GetAlphaAtlas(OpenGLRenderer renderer) => _alphaAtlases[renderer];

        public List<TMTerrainDesc> GetAvailableTerrainTextures() {
            return TerrainDescriptors
                .Where(t => t.TerrainType != TerrainTextureType.RoadType)
                .OrderBy(t => t.TerrainType.ToString())
                .ToList();
        }

        /// <summary>
        /// Returns the texture atlas layer index for a given TerrainTextureType.
        /// Returns -1 if the type is not found in the atlas.
        /// </summary>
        public int GetAtlasIndexForTerrainType(TerrainTextureType type) {
            var desc = TerrainDescriptors.FirstOrDefault(d => d.TerrainType == type);
            if (desc == null) return -1;
            if (_textureAtlasIndexLookup.TryGetValue(desc.TerrainTex.TextureId, out var index)) {
                return index;
            }
            return -1;
        }

        /// <summary>
        /// Computes the average RGB color for each terrain texture by sampling
        /// every pixel in the 512x512 source texture from the DAT.
        /// </summary>
        public Dictionary<TerrainTextureType, (byte R, byte G, byte B)> GetTerrainAverageColors() {
            var result = new Dictionary<TerrainTextureType, (byte, byte, byte)>();
            var buffer = new byte[512 * 512 * 4];
            const int pixelCount = 512 * 512;

            foreach (var tmDesc in GetAvailableTerrainTextures()) {
                try {
                    LoadTerrainTextureBytes(tmDesc.TerrainTex.TextureId, buffer.AsSpan());

                    long totalR = 0, totalG = 0, totalB = 0;
                    for (int i = 0; i < pixelCount; i++) {
                        totalR += buffer[i * 4];
                        totalG += buffer[i * 4 + 1];
                        totalB += buffer[i * 4 + 2];
                    }

                    result[tmDesc.TerrainType] = (
                        (byte)(totalR / pixelCount),
                        (byte)(totalG / pixelCount),
                        (byte)(totalB / pixelCount));
                }
                catch {
                    // Skip textures that fail to load
                }
            }

            return result;
        }

        /// <summary>
        /// Generates small Avalonia bitmap thumbnails for each available terrain texture.
        /// Reads the source textures from the DAT, downsamples to thumbnailSize, and returns
        /// a dictionary keyed by TerrainTextureType.
        /// </summary>
        public Dictionary<TerrainTextureType, Avalonia.Media.Imaging.Bitmap> GetTerrainThumbnails(int thumbnailSize = 64) {
            var result = new Dictionary<TerrainTextureType, Avalonia.Media.Imaging.Bitmap>();
            var buffer = new byte[512 * 512 * 4];

            foreach (var tmDesc in GetAvailableTerrainTextures()) {
                try {
                    LoadTerrainTextureBytes(tmDesc.TerrainTex.TextureId, buffer.AsSpan());

                    // Simple box downsample from 512x512 to thumbnailSize x thumbnailSize
                    int scale = 512 / thumbnailSize;
                    var thumbPixels = new byte[thumbnailSize * thumbnailSize * 4];
                    for (int ty = 0; ty < thumbnailSize; ty++) {
                        for (int tx = 0; tx < thumbnailSize; tx++) {
                            int srcIdx = (ty * scale * 512 + tx * scale) * 4;
                            int dstIdx = (ty * thumbnailSize + tx) * 4;
                            thumbPixels[dstIdx] = buffer[srcIdx];
                            thumbPixels[dstIdx + 1] = buffer[srcIdx + 1];
                            thumbPixels[dstIdx + 2] = buffer[srcIdx + 2];
                            thumbPixels[dstIdx + 3] = buffer[srcIdx + 3];
                        }
                    }

                    using var ms = new System.IO.MemoryStream();
                    // Write as raw bitmap via WriteableBitmap
                    var wb = new Avalonia.Media.Imaging.WriteableBitmap(
                        new Avalonia.PixelSize(thumbnailSize, thumbnailSize),
                        new Avalonia.Vector(96, 96),
                        Avalonia.Platform.PixelFormat.Rgba8888,
                        Avalonia.Platform.AlphaFormat.Premul);
                    using (var fb = wb.Lock()) {
                        System.Runtime.InteropServices.Marshal.Copy(thumbPixels, 0, fb.Address, thumbPixels.Length);
                    }
                    result[tmDesc.TerrainType] = wb;
                }
                catch {
                    // Skip textures that fail to load
                }
            }

            return result;
        }

        private void LoadTextures(OpenGLRenderer renderer, ITextureArray terrainAtlas, ITextureArray alphaAtlas) {
            Span<byte> bytes = _textureBuffer.AsSpan(0, 512 * 512 * 4);
            bool populateLookup = _textureAtlasIndexLookup.Count == 0;
            if (populateLookup) {
                Array.Fill(_terrainAtlasTilingByLayer, 1f);
            }

            foreach (var tmDesc in _landSurface.TexMerge.TerrainDesc) {
                if (populateLookup && _textureAtlasIndexLookup.ContainsKey(tmDesc.TerrainTex.TextureId)) {
                    continue;
                }
                LoadTerrainTextureBytes(tmDesc.TerrainTex.TextureId, bytes);
                var layerIndex = terrainAtlas.AddLayer(bytes);
                SetTerrainLayerTiling(layerIndex, tmDesc.TerrainTex.TextureId, tmDesc.TerrainTex.TexTiling);

                if (populateLookup)
                    _textureAtlasIndexLookup.Add(tmDesc.TerrainTex.TextureId, layerIndex);
            }

            foreach (var overlay in _landSurface.TexMerge.RoadMaps) {
                if (populateLookup && _alphaAtlasIndexLookup.ContainsKey(overlay.TextureId)) continue;

                LoadAlphaTextureBytes(overlay.TextureId, bytes);
                var layerIndex = alphaAtlas.AddLayer(bytes);

                if (populateLookup)
                    _alphaAtlasIndexLookup.Add(overlay.TextureId, layerIndex);
            }

            foreach (var overlay in _landSurface.TexMerge.CornerTerrainMaps) {
                if (populateLookup && _alphaAtlasIndexLookup.ContainsKey(overlay.TextureId)) continue;

                LoadAlphaTextureBytes(overlay.TextureId, bytes);
                var layerIndex = alphaAtlas.AddLayer(bytes);

                if (populateLookup)
                    _alphaAtlasIndexLookup.Add(overlay.TextureId, layerIndex);
            }

            foreach (var overlay in _landSurface.TexMerge.SideTerrainMaps) {
                if (populateLookup && _alphaAtlasIndexLookup.ContainsKey(overlay.TextureId)) continue;

                LoadAlphaTextureBytes(overlay.TextureId, bytes);
                var layerIndex = alphaAtlas.AddLayer(bytes);

                if (populateLookup)
                    _alphaAtlasIndexLookup.Add(overlay.TextureId, layerIndex);
            }
        }

        public void FillVertexData(uint landblockID, uint cellX, uint cellY, float baseLandblockX, float baseLandblockY,
                                 ref VertexLandscape v, int heightIdx, TextureMergeInfo surfInfo, int cornerIndex,
                                 TextureMergeInfo.Rotation baseRotation = TextureMergeInfo.Rotation.Rot0) {
            v.Position.X = baseLandblockX + cellX * 24f;
            v.Position.Y = baseLandblockY + cellY * 24f;
            v.Position.Z = _region.LandDefs.LandHeightTable[heightIdx];

            v.PackedBase = VertexLandscape.PackTexCoord(0, 0, 255, 255);
            v.PackedOverlay0 = VertexLandscape.PackTexCoord(-1, -1, 255, 255);
            v.PackedOverlay1 = VertexLandscape.PackTexCoord(-1, -1, 255, 255);
            v.PackedOverlay2 = VertexLandscape.PackTexCoord(-1, -1, 255, 255);
            v.PackedRoad0 = VertexLandscape.PackTexCoord(-1, -1, 255, 255);
            v.PackedRoad1 = VertexLandscape.PackTexCoord(-1, -1, 255, 255);

            var baseIndex = GetTextureAtlasIndex(surfInfo.TerrainBase.TextureId);
            var baseUV = LandUVsRotated[(int)baseRotation][cornerIndex];
            v.SetBase(baseUV.X, baseUV.Y, (byte)baseIndex, 255);

            for (int i = 0; i < surfInfo.TerrainOverlays.Count && i < 3; i++) {
                var overlayIndex = (byte)GetTextureAtlasIndex(surfInfo.TerrainOverlays[i].TextureId);
                var rotIndex = i < surfInfo.TerrainRotations.Count ? (byte)surfInfo.TerrainRotations[i] : (byte)0;
                var rotatedUV = LandUVsRotated[rotIndex][cornerIndex];
                byte alphaIndex = 255;

                if (i < surfInfo.TerrainAlphaOverlays.Count) {
                    alphaIndex = (byte)GetAlphaAtlasIndex(surfInfo.TerrainAlphaOverlays[i].TextureId);
                }

                switch (i) {
                    case 0: v.SetOverlay0(rotatedUV.X, rotatedUV.Y, overlayIndex, alphaIndex); break;
                    case 1: v.SetOverlay1(rotatedUV.X, rotatedUV.Y, overlayIndex, alphaIndex); break;
                    case 2: v.SetOverlay2(rotatedUV.X, rotatedUV.Y, overlayIndex, alphaIndex); break;
                }
            }

            if (surfInfo.RoadOverlay != null) {
                var roadOverlayIndex = (byte)GetTextureAtlasIndex(surfInfo.RoadOverlay.TextureId);
                var rotIndex = surfInfo.RoadRotations.Count > 0 ? (byte)surfInfo.RoadRotations[0] : (byte)0;
                var rotatedUV = LandUVsRotated[rotIndex][cornerIndex];
                byte alphaIndex = surfInfo.RoadAlphaOverlays.Count > 0
                    ? (byte)GetAlphaAtlasIndex(surfInfo.RoadAlphaOverlays[0].TextureId)
                    : (byte)255;
                v.SetRoad0(rotatedUV.X, rotatedUV.Y, roadOverlayIndex, alphaIndex);

                if (surfInfo.RoadAlphaOverlays.Count > 1) {
                    var rotIndex2 = surfInfo.RoadRotations.Count > 1 ? (byte)surfInfo.RoadRotations[1] : (byte)0;
                    var rotatedUV2 = LandUVsRotated[rotIndex2][cornerIndex];
                    byte alphaIndex2 = (byte)GetAlphaAtlasIndex(surfInfo.RoadAlphaOverlays[1].TextureId);
                    v.SetRoad1(rotatedUV2.X, rotatedUV2.Y, roadOverlayIndex, alphaIndex2);
                }
            }
        }

        public int GetTextureAtlasIndex(uint texGID) {
            if (_textureAtlasIndexLookup.TryGetValue(texGID, out var index)) {
                return index;
            }
            throw new Exception($"Texture GID not found in atlas: 0x{texGID:X8}");
        }

        /// <summary>
        /// Replaces the terrain texture for a given TerrainTextureType with custom RGBA data.
        /// Queues the update for the next render frame (GL thread). The rgbaData must be 512x512x4 bytes.
        /// </summary>
        public bool ReplaceTerrainTexture(TerrainTextureType type, byte[] rgbaData) {
            var desc = TerrainDescriptors.FirstOrDefault(d => d.TerrainType == type);
            if (desc == null) return false;

            if (!_textureAtlasIndexLookup.TryGetValue(desc.TerrainTex.TextureId, out var layerIndex))
                return false;

            _pendingTextureUpdates.Enqueue((layerIndex, rgbaData));
            return true;
        }

        /// <summary>
        /// Processes queued texture replacements. Must be called on the GL thread (e.g. during Render).
        /// </summary>
        public void ProcessPendingTextureUpdates() {
            while (_pendingTextureUpdates.TryDequeue(out var update)) {
                lock (_lock) {
                    foreach (var (renderer, atlas) in _terrainAtlases) {
                        atlas.UpdateLayer(update.layerIndex, update.rgbaData);
                    }
                }
            }
        }

        public int GetAlphaAtlasIndex(uint texGID) {
            if (_alphaAtlasIndexLookup.TryGetValue(texGID, out var index)) {
                return index;
            }
            throw new Exception($"Texture GID not found in atlas: 0x{texGID:X8}");
        }

        private void GetAlphaTexture(RenderSurface texture, Span<byte> bytes) {
            if (texture.Width <= 0 || texture.Height <= 0) {
                throw new Exception("Texture dimensions are invalid");
            }

            int pixelCount = texture.Width * texture.Height;
            if (texture.SourceData.Length >= pixelCount * 4) {
                ResampleBgraGrayToRgba(texture.SourceData.AsSpan(), texture.Width, texture.Height, bytes, TerrainAtlasDimension, TerrainAtlasDimension);
                return;
            }

            if (texture.SourceData.Length >= pixelCount) {
                ResampleAlphaToRgba(texture.SourceData.AsSpan(), texture.Width, texture.Height, bytes, TerrainAtlasDimension, TerrainAtlasDimension);
                return;
            }

            throw new Exception("Alpha texture source data is incomplete");
        }

        private void GetTerrainTexture(RenderSurface texture, Span<byte> bytes) {
            if (texture.Width <= 0 || texture.Height <= 0) {
                throw new Exception("Texture dimensions are invalid");
            }

            int pixelCount = texture.Width * texture.Height;
            if (texture.SourceData.Length < pixelCount * 4) {
                throw new Exception("Terrain texture source data is incomplete");
            }

            ResampleBgraToRgba(texture.SourceData.AsSpan(), texture.Width, texture.Height, bytes, TerrainAtlasDimension, TerrainAtlasDimension);
        }

        private void LoadTerrainTextureBytes(uint surfaceTextureId, Span<byte> bytes) {
            if (TryLoadRenderSurface(surfaceTextureId, out var texture)) {
                GetTerrainTexture(texture, bytes);
                return;
            }

            FillPlaceholderTerrainTexture(bytes);
        }

        private void LoadAlphaTextureBytes(uint surfaceTextureId, Span<byte> bytes) {
            if (TryLoadRenderSurface(surfaceTextureId, out var texture)) {
                GetAlphaTexture(texture, bytes);
                return;
            }

            FillPlaceholderAlphaTexture(bytes);
        }

        private void SetTerrainLayerTiling(int layerIndex, uint surfaceTextureId, uint texTiling) {
            if (layerIndex < 0 || layerIndex >= _terrainAtlasTilingByLayer.Length) {
                return;
            }

            if (texTiling > 0) {
                _terrainAtlasTilingByLayer[layerIndex] = texTiling;
                return;
            }

            var desc = TerrainDescriptors.FirstOrDefault(d => d.TerrainTex.TextureId == surfaceTextureId);
            _terrainAtlasTilingByLayer[layerIndex] = desc?.TerrainTex.TexTiling > 0
                ? desc.TerrainTex.TexTiling
                : 1f;
        }

        private bool TryLoadRenderSurface(uint surfaceTextureId, out RenderSurface texture) {
            texture = null!;

            if (surfaceTextureId == PlaceholderTextureId) {
                return false;
            }

            if (!_dats.TryGet<SurfaceTexture>(surfaceTextureId, out var surfaceTexture) || surfaceTexture.Textures.Count == 0) {
                return false;
            }

            if (!_dats.TryGet<RenderSurface>(surfaceTexture.Textures[^1], out texture)) {
                return false;
            }

            return texture.Width > 0 && texture.Height > 0 && texture.SourceData?.Length > 0;
        }

        private static void FillPlaceholderTerrainTexture(Span<byte> bytes) {
            for (int y = 0; y < TerrainAtlasDimension; y++) {
                for (int x = 0; x < TerrainAtlasDimension; x++) {
                    int i = (y * TerrainAtlasDimension + x) * 4;
                    bool dark = ((x / 32) + (y / 32)) % 2 == 0;
                    bytes[i + 0] = dark ? (byte)82 : (byte)118;
                    bytes[i + 1] = dark ? (byte)114 : (byte)146;
                    bytes[i + 2] = dark ? (byte)76 : (byte)98;
                    bytes[i + 3] = 255;
                }
            }
        }

        private static void FillPlaceholderAlphaTexture(Span<byte> bytes) {
            for (int i = 0; i < bytes.Length; i += 4) {
                bytes[i + 0] = 255;
                bytes[i + 1] = 255;
                bytes[i + 2] = 255;
                bytes[i + 3] = 255;
            }
        }

        private static void ResampleBgraToRgba(Span<byte> sourceData, int sourceWidth, int sourceHeight, Span<byte> data, int targetWidth, int targetHeight) {
            for (int y = 0; y < targetHeight; y++) {
                int sourceY = y * sourceHeight / targetHeight;
                for (int x = 0; x < targetWidth; x++) {
                    int sourceX = x * sourceWidth / targetWidth;
                    int sourceIndex = (sourceY * sourceWidth + sourceX) * 4;
                    int targetIndex = (y * targetWidth + x) * 4;
                    data[targetIndex + 0] = sourceData[sourceIndex + 2];
                    data[targetIndex + 1] = sourceData[sourceIndex + 1];
                    data[targetIndex + 2] = sourceData[sourceIndex + 0];
                    data[targetIndex + 3] = sourceData[sourceIndex + 3];
                }
            }
        }

        private static void ResampleAlphaToRgba(Span<byte> sourceData, int sourceWidth, int sourceHeight, Span<byte> data, int targetWidth, int targetHeight) {
            for (int y = 0; y < targetHeight; y++) {
                int sourceY = y * sourceHeight / targetHeight;
                for (int x = 0; x < targetWidth; x++) {
                    int sourceX = x * sourceWidth / targetWidth;
                    byte alpha = sourceData[sourceY * sourceWidth + sourceX];
                    int targetIndex = (y * targetWidth + x) * 4;
                    data[targetIndex + 0] = alpha;
                    data[targetIndex + 1] = alpha;
                    data[targetIndex + 2] = alpha;
                    data[targetIndex + 3] = alpha;
                }
            }
        }

        private static void ResampleBgraGrayToRgba(Span<byte> sourceData, int sourceWidth, int sourceHeight, Span<byte> data, int targetWidth, int targetHeight) {
            for (int y = 0; y < targetHeight; y++) {
                int sourceY = y * sourceHeight / targetHeight;
                for (int x = 0; x < targetWidth; x++) {
                    int sourceX = x * sourceWidth / targetWidth;
                    int sourceIndex = (sourceY * sourceWidth + sourceX) * 4;
                    byte alpha = sourceData[sourceIndex + 2];
                    int targetIndex = (y * targetWidth + x) * 4;
                    data[targetIndex + 0] = alpha;
                    data[targetIndex + 1] = alpha;
                    data[targetIndex + 2] = alpha;
                    data[targetIndex + 3] = alpha;
                }
            }
        }

        public bool SelectTerrain(int x, int y, out uint surfaceNumber, out TextureMergeInfo.Rotation rotation, uint paletteCode) {
            surfaceNumber = 0;
            rotation = TextureMergeInfo.Rotation.Rot0;

            if (paletteCode == 0)
                return false;

            if (SurfaceInfoByPalette.TryGetValue(paletteCode, out var existingSurfaceInfo)) {
                existingSurfaceInfo.LandCellCount++;
                surfaceNumber = existingSurfaceInfo.SurfaceNumber;
                return true;
            }

            var surface = BuildTexture(paletteCode, TerrainPalCode.GetTextureSizeFromPalCode(paletteCode));
            return surface != null && AddNewSurface(surface, paletteCode, out surfaceNumber);
        }

        private bool AddNewSurface(TextureMergeInfo surface, uint paletteCode, out uint surfaceNumber) {
            surfaceNumber = _nextSurfaceNumber++;

            var surfaceInfo = new SurfaceInfo {
                Surface = surface,
                PaletteCode = paletteCode,
                LandCellCount = 1,
                SurfaceNumber = surfaceNumber
            };

            SurfacesBySurfaceNumber.Add(surfaceNumber, surface);
            SurfaceInfoByPalette.Add(paletteCode, surfaceInfo);
            TotalUniqueSurfaces++;

            return true;
        }

        public TextureMergeInfo? GetLandSurface(uint surfaceId) {
            if (SurfacesBySurfaceNumber.TryGetValue(surfaceId, out var surface)) {
                return surface;
            }
            return null;
        }

        public TextureMergeInfo BuildTexture(uint paletteCode, uint textureSize) {
            var terrainTextures = GetTerrainTextures(paletteCode, out var terrainCodes);
            var roadCodes = GetRoadCodes(paletteCode, out var allRoad);
            var roadTexture = GetTerrainTexture(TerrainTextureType.RoadType);

            var result = new TextureMergeInfo {
                TerrainCodes = terrainCodes
            };

            if (allRoad) {
                result.TerrainBase = roadTexture;
                result.PostProcessing();
                return result;
            }

            result.TerrainBase = terrainTextures[0];
            ProcessTerrainOverlays(result, paletteCode, terrainTextures, terrainCodes);

            if (roadTexture != null) {
                ProcessRoadOverlays(result, paletteCode, roadTexture, roadCodes);
            }

            result.PostProcessing();
            return result;
        }

        private void ProcessTerrainOverlays(TextureMergeInfo result, uint paletteCode, List<TerrainTex> terrainTextures, List<uint> terrainCodes) {
            for (int i = 0; i < 3; i++) {
                if (terrainCodes[i] == 0) break;

                var terrainAlpha = FindTerrainAlpha(paletteCode, terrainCodes[i], out var rotation, out var alphaIndex);
                if (terrainAlpha == null) continue;

                result.TerrainOverlays[i] = terrainTextures[i + 1];
                result.TerrainRotations[i] = rotation;
                result.TerrainAlphaOverlays[i] = terrainAlpha;
                result.TerrainAlphaIndices[i] = alphaIndex;
            }
        }

        private void ProcessRoadOverlays(TextureMergeInfo result, uint paletteCode, TerrainTex roadTexture, List<uint> roadCodes) {
            for (int i = 0; i < 2; i++) {
                if (roadCodes[i] == 0) break;

                var roadAlpha = FindRoadAlpha(paletteCode, roadCodes[i], out var rotation, out var alphaIndex);
                if (roadAlpha == null) continue;

                result.RoadRotations[i] = rotation;
                result.RoadAlphaIndices[i] = alphaIndex;
                result.RoadAlphaOverlays[i] = roadAlpha;
                result.RoadOverlay = roadTexture;
            }
        }

        private TerrainTex GetTerrainTexture(TerrainTextureType terrainType) {
            var descriptor = TerrainDescriptors.FirstOrDefault(d => d.TerrainType == terrainType);
            return descriptor?.TerrainTex ?? TerrainDescriptors[0].TerrainTex;
        }

        private List<TerrainTextureType> ExtractTerrainCodes(uint paletteCode) {
            return new List<TerrainTextureType>
            {
            (TerrainTextureType)((paletteCode >> 15) & 0x1F),
            (TerrainTextureType)((paletteCode >> 10) & 0x1F),
            (TerrainTextureType)((paletteCode >> 5) & 0x1F),
            (TerrainTextureType)(paletteCode & 0x1F)
        };
        }

        private List<TerrainTex> GetTerrainTextures(uint paletteCode, out List<uint> terrainCodes) {
            terrainCodes = new List<uint> { 0, 0, 0 };
            var paletteCodes = ExtractTerrainCodes(paletteCode);

            for (int i = 0; i < 4; i++) {
                for (int j = i + 1; j < 4; j++) {
                    if (paletteCodes[i] == paletteCodes[j])
                        return BuildTerrainCodesWithDuplicates(paletteCodes, terrainCodes, i);
                }
            }

            var terrainTextures = new List<TerrainTex>(4);
            for (int i = 0; i < 4; i++) {
                terrainTextures.Add(GetTerrainTexture(paletteCodes[i]));
            }

            for (int i = 0; i < 3; i++) {
                terrainCodes[i] = (uint)(1 << (i + 1));
            }

            return terrainTextures;
        }

        private List<TerrainTex> BuildTerrainCodesWithDuplicates(List<TerrainTextureType> paletteCodes, List<uint> terrainCodes, int duplicateIndex) {
            var terrainTextures = new List<TerrainTex> { new(), new(), new() };
            var primaryTerrain = paletteCodes[duplicateIndex];
            var secondaryTerrain = (TerrainTextureType)0;

            terrainTextures[0] = GetTerrainTexture(primaryTerrain);

            for (int k = 0; k < 4; k++) {
                if (primaryTerrain == paletteCodes[k]) continue;

                if (terrainCodes[0] == 0) {
                    terrainCodes[0] = (uint)(1 << k);
                    secondaryTerrain = paletteCodes[k];
                    terrainTextures[1] = GetTerrainTexture(secondaryTerrain);
                }
                else {
                    if (secondaryTerrain == paletteCodes[k] && terrainCodes[0] == (1U << (k - 1))) {
                        terrainCodes[0] += (uint)(1 << k);
                    }
                    else {
                        terrainTextures[2] = GetTerrainTexture(paletteCodes[k]);
                        terrainCodes[1] = (uint)(1 << k);
                    }
                    break;
                }
            }

            return terrainTextures;
        }

        private List<uint> GetRoadCodes(uint paletteCode, out bool allRoad) {
            var roadCodes = new List<uint> { 0, 0 };
            uint mask = 0;

            if ((paletteCode & 0xC000000) != 0) mask |= 1;
            if ((paletteCode & 0x3000000) != 0) mask |= 2;
            if ((paletteCode & 0xC00000) != 0) mask |= 4;
            if ((paletteCode & 0x300000) != 0) mask |= 8;

            allRoad = mask == 0xF;

            if (allRoad) return roadCodes;

            switch (mask) {
                case 0xE: roadCodes[0] = 6; roadCodes[1] = 12; break;
                case 0xD: roadCodes[0] = 9; roadCodes[1] = 12; break;
                case 0xB: roadCodes[0] = 9; roadCodes[1] = 3; break;
                case 0x7: roadCodes[0] = 3; roadCodes[1] = 6; break;
                case 0x0: break;
                default: roadCodes[0] = mask; break;
            }

            return roadCodes;
        }

        private TerrainAlphaMap? FindTerrainAlpha(uint paletteCode, uint terrainCode, out TextureMergeInfo.Rotation rotation, out int alphaIndex) {
            rotation = TextureMergeInfo.Rotation.Rot0;
            alphaIndex = 0;

            var isCornerTerrain = terrainCode == 1 || terrainCode == 2 || terrainCode == 4 || terrainCode == 8;
            var terrainMaps = isCornerTerrain ? CornerTerrainMaps : SideTerrainMaps;
            var baseIndex = isCornerTerrain ? 0 : 4;

            if (terrainMaps.Count == 0) return null;

            var randomIndex = GeneratePseudoRandomIndex(paletteCode, terrainMaps.Count);
            var alpha = terrainMaps[randomIndex];
            alphaIndex = baseIndex + randomIndex;

            var rotationCount = 0;
            var currentAlphaCode = alpha.TCode;

            while (currentAlphaCode != terrainCode && rotationCount < 4) {
                currentAlphaCode = RotateTerrainCode(currentAlphaCode);
                rotationCount++;
            }

            if (rotationCount >= 4) return null;

            rotation = (TextureMergeInfo.Rotation)rotationCount;
            return alpha;
        }

        private RoadAlphaMap? FindRoadAlpha(uint paletteCode, uint roadCode, out TextureMergeInfo.Rotation rotation, out int alphaIndex) {
            rotation = TextureMergeInfo.Rotation.Rot0;
            alphaIndex = -1;

            if (RoadMaps.Count == 0) return null;

            var randomIndex = GeneratePseudoRandomIndex(paletteCode, RoadMaps.Count);

            for (int i = 0; i < RoadMaps.Count; i++) {
                var index = (i + randomIndex) % RoadMaps.Count;
                var alpha = RoadMaps[index];
                var currentRoadCode = alpha.RCode;
                alphaIndex = 5 + index;

                for (int rotationCount = 0; rotationCount < 4; rotationCount++) {
                    if (currentRoadCode == roadCode) {
                        rotation = (TextureMergeInfo.Rotation)rotationCount;
                        return alpha;
                    }
                    currentRoadCode = RotateTerrainCode(currentRoadCode);
                }
            }

            alphaIndex = -1;
            return null;
        }

        private int GeneratePseudoRandomIndex(uint paletteCode, int count) {
            var pseudoRandom = (int)Math.Floor((1379576222 * paletteCode - 1372186442) * 2.3283064e-10 * count);
            return pseudoRandom >= count ? 0 : pseudoRandom;
        }

        private static uint RotateTerrainCode(uint code) {
            code *= 2;
            return code >= 16 ? code - 15 : code;
        }
    }
}