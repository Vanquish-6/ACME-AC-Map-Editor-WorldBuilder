using System.Numerics;
using System.Text.Json.Serialization;

namespace WorldBuilder.Shared.Models {
    /// <summary>
    /// A generator, item, or portal placement for an outdoor landblock, to be written to
    /// landblock_instance on export. Used by the Terrain editor instance panel.
    /// </summary>
    public class OutdoorInstancePlacement {
        public ushort LandblockId { get; set; }
        public uint WeenieClassId { get; set; }
        /// <summary>Outdoor cell index (typically 1–64).</summary>
        public ushort CellNumber { get; set; }
        public float OriginX { get; set; }
        public float OriginY { get; set; }
        public float OriginZ { get; set; }
        public float AnglesW { get; set; }
        public float AnglesX { get; set; }
        public float AnglesY { get; set; }
        public float AnglesZ { get; set; } = 1f;

        [JsonIgnore]
        public Vector3 Origin {
            get => new Vector3(OriginX, OriginY, OriginZ);
            set { OriginX = value.X; OriginY = value.Y; OriginZ = value.Z; }
        }

        [JsonIgnore]
        public Quaternion Orientation {
            get => new Quaternion(AnglesX, AnglesY, AnglesZ, AnglesW);
            set { AnglesW = value.W; AnglesX = value.X; AnglesY = value.Y; AnglesZ = value.Z; }
        }
    }
}
