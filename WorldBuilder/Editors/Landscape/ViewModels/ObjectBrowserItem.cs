using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Acme.Dat;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    /// <summary>
    /// Represents a single object entry in the Object Browser thumbnail grid.
    /// Used for both DAT objects (Setup/GfxObj) and world-database weenies.
    /// </summary>
    public partial class ObjectBrowserItem : ObservableObject {
        /// <summary>
        /// The DAT object ID (Setup or GfxObj) used for rendering/placement.
        /// For weenies this is the Setup DID from the DB.
        /// For particle emitters this is the ParticleEmitter DID.
        /// </summary>
        public uint Id { get; }

        /// <summary>
        /// True if this is a Setup object, false if GfxObj (or particle emitter).
        /// </summary>
        public bool IsSetup { get; }

        /// <summary>
        /// When true, <see cref="Id"/> is a portal <see cref="ParticleEmitter"/>; thumbnails use <see cref="ThumbnailGraphicsId"/>.
        /// </summary>
        public bool IsParticleEmitter { get; }

        /// <summary>
        /// Model id passed to the thumbnail renderer (HwGfxObj for particles, otherwise <see cref="Id"/>).
        /// </summary>
        public uint ThumbnailGraphicsId { get; }

        /// <summary>
        /// Formatted display string shown below the thumbnail.
        /// </summary>
        public string DisplayId { get; }

        /// <summary>
        /// Keyword tags for tooltip display. Null if no tags.
        /// </summary>
        public string? Tags { get; }

        /// <summary>
        /// Non-null when this item represents a world-database weenie rather than a DAT object.
        /// </summary>
        public uint? WeenieClassId { get; }

        /// <summary>
        /// Rendered thumbnail bitmap. Null until the thumbnail has been generated or loaded from cache.
        /// The UI shows a placeholder when this is null.
        /// </summary>
        [ObservableProperty]
        private Bitmap? _thumbnail;

        [ObservableProperty]
        private bool _isFavorite;

        public ObjectBrowserItem(uint id, bool isSetup, string? tags) {
            Id = id;
            IsSetup = isSetup;
            IsParticleEmitter = false;
            ThumbnailGraphicsId = id;
            Tags = tags;
            WeenieClassId = null;
            DisplayId = isSetup ? $"Setup  0x{id:X8}" : $"GfxObj 0x{id:X8}";
        }

        /// <summary>
        /// Particle emitter browser entry; placement uses <see cref="Id"/> as the emitter DID.
        /// </summary>
        public ObjectBrowserItem(uint particleEmitterId, string? tags, IDatReaderWriter dats) {
            Id = particleEmitterId;
            IsSetup = false;
            IsParticleEmitter = true;
            WeenieClassId = null;
            Tags = tags;
            uint thumb = particleEmitterId;
            if (AcParticleEmitterSimulator.TryResolveVisualGfxObjectIdFromPortal(dats, particleEmitterId, out var g) && g != 0)
                thumb = g;
            ThumbnailGraphicsId = thumb;
            DisplayId = $"Particle 0x{particleEmitterId:X8}";
        }

        /// <summary>
        /// Constructor for weenie items loaded from the world database.
        /// </summary>
        public ObjectBrowserItem(uint setupId, uint weenieClassId, string weenieName) {
            Id = setupId;
            IsSetup = setupId != 0 && (setupId & 0x02000000) != 0;
            IsParticleEmitter = false;
            ThumbnailGraphicsId = setupId;
            WeenieClassId = weenieClassId;
            DisplayId = $"{weenieName}\n({weenieClassId})";
            Tags = setupId != 0
                ? $"{weenieName}\nWCID: {weenieClassId}\nSetup: 0x{setupId:X8}"
                : $"{weenieName}\nWCID: {weenieClassId}\n(no 3D model)";
        }
    }
}
