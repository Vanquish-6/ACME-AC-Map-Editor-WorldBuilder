using System.Numerics;

namespace Acme.Dat;

public enum DatAccessType {
    Read = 0,
    ReadWrite = 1,
}

public enum FileCachingStrategy {
    Never = 0,
    OnDemand = 1,
}

public enum IndexCachingStrategy {
    Never = 0,
    OnDemand = 1,
}

[Flags]
public enum SurfaceType : uint {
    Base1Solid = 0x00000001,
    Base1Image = 0x00000002,
    Base1ClipMap = 0x00000004,
    Translucent = 0x00000010,
    Diffuse = 0x00000020,
    Luminous = 0x00000040,
    Alpha = 0x00000100,
    InvAlpha = 0x00000200,
    Additive = 0x00010000,
    Detail = 0x00020000,
    Gouraud = 0x10000000,
    Stippled = 0x40000000,
    Perspective = 0x80000000,
}

[Flags]
public enum EnvCellFlags : uint {
    SeenOutside = 0x00000001,
    HasStaticObjs = 0x00000002,
    HasRestrictionObj = 0x00000008,
}

public enum TerrainTextureType : int {
    BarrenRock = 0,
    Grassland = 1,
    Ice = 2,
    LushGrass = 3,
    MarshSparseSwamp = 4,
    MudRichDirt = 5,
    ObsidianPlain = 6,
    PackedDirt = 7,
    PatchyDirt = 8,
    PatchyGrassland = 9,
    SandYellow = 10,
    SandGrey = 11,
    SandRockStrewn = 12,
    SedimentaryRock = 13,
    SemiBarrenRock = 14,
    Snow = 15,
    WaterRunning = 16,
    WaterStandingFresh = 17,
    WaterShallowSea = 18,
    WaterShallowStillSea = 19,
    WaterDeepSea = 20,
    ForestFloor = 21,
    FauxWaterRunning = 22,
    SeaSlime = 23,
    Argila = 24,
    Volcano1 = 25,
    Volcano2 = 26,
    BlueIce = 27,
    Moss = 28,
    DarkMoss = 29,
    Olthoi = 30,
    DesolateLands = 31,
    RoadType = 32,
}

public enum Placement : uint {
    Default = 0x00000000,
    RightHandCombat = 0x00000001,
    RightHandNonCombat = 0x00000002,
    LeftHand = 0x00000003,
    Belt = 0x00000004,
    Quiver = 0x00000005,
    Shield = 0x00000006,
    LeftWeapon = 0x00000007,
    LeftUnarmed = 0x00000008,
    SpecialCrowssbowBolt = 0x00000033,
    MissileFlight = 0x00000034,
    Resting = 0x00000065,
    Other = 0x00000066,
    Hook = 0x00000067,
}

public enum PixelFormat : uint {
    PFID_UNKNOWN = 0x00000000,
    PFID_R8G8B8 = 0x00000014,
    PFID_A8R8G8B8 = 0x00000015,
    PFID_X8R8G8B8 = 0x00000016,
    PFID_R5G6B5 = 0x00000017,
    PFID_A8 = 0x0000001C,
    PFID_P8 = 0x00000029,
    PFID_INDEX16 = 0x00000065,
    PFID_CUSTOM_RAW_JPEG = 0x000001F4,
    PFID_DXT1 = 0x31545844,
    PFID_DXT3 = 0x33545844,
    PFID_DXT5 = 0x35545844,
    PFID_CUSTOM_LSCAPE_ALPHA = 0x000001F5,
}

/// <summary>DAT palette color as B,G,R,A bytes (packed 0xAARRGGBB on little-endian).</summary>
public struct ColorARGB {
    public byte Blue;
    public byte Green;
    public byte Red;
    public byte Alpha;

    public static ColorARGB FromPacked(uint packed) => new() {
        Blue = (byte)packed,
        Green = (byte)(packed >> 8),
        Red = (byte)(packed >> 16),
        Alpha = (byte)(packed >> 24),
    };

    public uint ToPacked() => (uint)Blue | ((uint)Green << 8) | ((uint)Red << 16) | ((uint)Alpha << 24);

    public static implicit operator ColorARGB(uint packed) => FromPacked(packed);
    public static implicit operator uint(ColorARGB color) => color.ToPacked();
}

public static class DatFrames {
    public static Vector3 Vec3(float[]? xyz) =>
        xyz is { Length: >= 3 } ? new Vector3(xyz[0], xyz[1], xyz[2]) : default;

    /// Native interchange delivers DAT Frame quats as x,y,z,w (System.Numerics).
    /// On-disk Frame is w,x,y,z; the native library converts at the DTO boundary.
    public static Quaternion Quat(float[]? xyzw) =>
        xyzw is { Length: >= 4 } ? new Quaternion(xyzw[0], xyzw[1], xyzw[2], xyzw[3]) : Quaternion.Identity;

    public static float[] From(Vector3 v) => [v.X, v.Y, v.Z];
    public static float[] From(Quaternion q) => [q.X, q.Y, q.Z, q.W];
}
