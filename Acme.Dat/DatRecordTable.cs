namespace Acme.Dat;

public enum DatKind : uint {
    LandBlock = 1,
    LandBlockInfo = 2,
    EnvCell = 3,
    Setup = 4,
    Scene = 5,
    Palette = 6,
    Surface = 7,
    SurfaceTexture = 8,
    RenderTexture = 9,
    RenderSurface = 10,
    ParticleEmitter = 11,
    LayoutDesc = 12,
    StringTable = 13,
    ClothingTable = 14,
    PalSet = 15,
    ExperienceTable = 16,
    SkillTable = 17,
    VitalTable = 18,
    GfxObj = 19,
    Environment = 20,
    Region = 21,
    CharGen = 22,
    SpellTable = 23,
    SpellComponentTable = 24,
}

public readonly record struct DatRecordInfo(
    DatKind Kind,
    DatArchive Archive,
    uint FirstId,
    uint LastId,
    uint Mask
) {
    public bool Matches(uint id) {
        if (Mask != 0) {
            return (id & 0xFFFF) == Mask;
        }
        if (FirstId == LastId) {
            return id == FirstId;
        }
        return id >= FirstId && id <= LastId;
    }
}

public static class DatRecordTable {
    public const uint IterationId = 0xFFFF0001;

    public static readonly DatRecordInfo LandBlock = new(DatKind.LandBlock, DatArchive.Cell, 0, 0, 0xFFFF);
    public static readonly DatRecordInfo LandBlockInfo = new(DatKind.LandBlockInfo, DatArchive.Cell, 0, 0, 0xFFFE);
    public static readonly DatRecordInfo EnvCell = new(DatKind.EnvCell, DatArchive.Cell, 0, 0, 0);
    public static readonly DatRecordInfo GfxObj = new(DatKind.GfxObj, DatArchive.Portal, 0x01000000, 0x01FFFFFF, 0);
    public static readonly DatRecordInfo Setup = new(DatKind.Setup, DatArchive.Portal, 0x02000000, 0x02FFFFFF, 0);
    public static readonly DatRecordInfo Palette = new(DatKind.Palette, DatArchive.Portal, 0x04000000, 0x04FFFFFF, 0);
    public static readonly DatRecordInfo SurfaceTexture = new(DatKind.SurfaceTexture, DatArchive.Portal, 0x05000000, 0x05FFFFFF, 0);
    public static readonly DatRecordInfo RenderSurface = new(DatKind.RenderSurface, DatArchive.Portal, 0x06000000, 0x06FFFFFF, 0);
    public static readonly DatRecordInfo Surface = new(DatKind.Surface, DatArchive.Portal, 0x08000000, 0x08FFFFFF, 0);
    public static readonly DatRecordInfo PalSet = new(DatKind.PalSet, DatArchive.Portal, 0x0F000000, 0x0F00FFFF, 0);
    public static readonly DatRecordInfo ClothingTable = new(DatKind.ClothingTable, DatArchive.Portal, 0x10000000, 0x1000FFFF, 0);
    public static readonly DatRecordInfo Scene = new(DatKind.Scene, DatArchive.Portal, 0x12000000, 0x1200FFFF, 0);
    public static readonly DatRecordInfo Region = new(DatKind.Region, DatArchive.Portal, 0x13000000, 0x1300FFFF, 0);
    public static readonly DatRecordInfo RenderTexture = new(DatKind.RenderTexture, DatArchive.Portal, 0x15000000, 0x15FFFFFF, 0);
    public static readonly DatRecordInfo Environment = new(DatKind.Environment, DatArchive.Portal, 0x0D000000, 0x0D00FFFF, 0);
    public static readonly DatRecordInfo CharGen = new(DatKind.CharGen, DatArchive.Portal, 0x0E000002, 0x0E000002, 0);
    public static readonly DatRecordInfo SpellTable = new(DatKind.SpellTable, DatArchive.Portal, 0x0E00000E, 0x0E00000E, 0);
    public static readonly DatRecordInfo SpellComponentTable = new(DatKind.SpellComponentTable, DatArchive.Portal, 0x0E00000F, 0x0E00000F, 0);
    public static readonly DatRecordInfo SkillTable = new(DatKind.SkillTable, DatArchive.Portal, 0x0E000004, 0x0E000004, 0);
    public static readonly DatRecordInfo VitalTable = new(DatKind.VitalTable, DatArchive.Portal, 0x0E000003, 0x0E000003, 0);
    public static readonly DatRecordInfo ExperienceTable = new(DatKind.ExperienceTable, DatArchive.Portal, 0x0E000018, 0x0E000018, 0);
    public static readonly DatRecordInfo ParticleEmitter = new(DatKind.ParticleEmitter, DatArchive.Portal, 0x32000000, 0x3200FFFF, 0);
    public static readonly DatRecordInfo LayoutDesc = new(DatKind.LayoutDesc, DatArchive.Local, 0x21000000, 0x21FFFFFF, 0);
    public static readonly DatRecordInfo StringTable = new(DatKind.StringTable, DatArchive.Local, 0x23000000, 0x24FFFFFF, 0);

    public static bool TryGet(Type type, out DatRecordInfo info) {
        if (_byType.TryGetValue(type, out info)) {
            return true;
        }
        info = default;
        return false;
    }

    public static IEnumerable<uint> FilterIds(Type type, IEnumerable<uint> ids) {
        if (!TryGet(type, out var info)) {
            return [];
        }
        if (type == typeof(EnvCell)) {
            return ids.Where(id => (id & 0xFFFF) != 0xFFFF && (id & 0xFFFF) != 0xFFFE);
        }
        if (info.Mask != 0 || info.FirstId != info.LastId || info.FirstId != 0) {
            return ids.Where(info.Matches);
        }
        return ids.Where(info.Matches);
    }

    private static readonly Dictionary<Type, DatRecordInfo> _byType = new() {
        [typeof(LandBlock)] = LandBlock,
        [typeof(LandBlockInfo)] = LandBlockInfo,
        [typeof(EnvCell)] = EnvCell,
        [typeof(Setup)] = Setup,
        [typeof(Scene)] = Scene,
        [typeof(Palette)] = Palette,
        [typeof(Surface)] = Surface,
        [typeof(SurfaceTexture)] = SurfaceTexture,
        [typeof(RenderTexture)] = RenderTexture,
        [typeof(RenderSurface)] = RenderSurface,
        [typeof(ParticleEmitter)] = ParticleEmitter,
        [typeof(LayoutDesc)] = LayoutDesc,
        [typeof(StringTable)] = StringTable,
        [typeof(ClothingTable)] = ClothingTable,
        [typeof(PalSet)] = PalSet,
        [typeof(ExperienceTable)] = ExperienceTable,
        [typeof(SkillTable)] = SkillTable,
        [typeof(VitalTable)] = VitalTable,
        [typeof(GfxObj)] = GfxObj,
        [typeof(Environment)] = Environment,
        [typeof(Region)] = Region,
        [typeof(CharGen)] = CharGen,
        [typeof(SpellTable)] = SpellTable,
        [typeof(SpellComponentTable)] = SpellComponentTable,
    };
}
