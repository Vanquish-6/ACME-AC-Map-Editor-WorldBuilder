using MessagePack;

namespace Acme.Dat;

public enum TextureType : byte {
    Undefined = 0x1,
    Texture2D = 0x2,
    Texture3D = 0x3,
    Cube = 0x4,
    Movie2D = 0x5,
}

public static class DatFlagExtensions {
    public static bool HasFlag(this uint value, EnvCellFlags flag) => (value & (uint)flag) != 0;
    public static bool HasFlag(this uint value, GfxObjFlags flag) => (value & (uint)flag) != 0;
    public static bool HasFlag(this uint value, SurfaceType flag) => (value & (uint)flag) != 0;
    public static bool HasFile(this uint[] ids, uint id) => Array.IndexOf(ids, id) >= 0;
}

public sealed partial class TerrainAlphaMap {
    [IgnoreMember] public uint TCode { get => Tcode; set => Tcode = value; }
}

public sealed partial class ExperienceTable {
    [IgnoreMember]
    public ulong[] Levels {
        get => [.. LevelXp];
        set => LevelXp = value?.ToList() ?? [];
    }
    [IgnoreMember]
    public uint[] Attributes {
        get => [.. AttributeXp];
        set => AttributeXp = value?.ToList() ?? [];
    }
    [IgnoreMember]
    public uint[] Vitals {
        get => [.. VitalXp];
        set => VitalXp = value?.ToList() ?? [];
    }
    [IgnoreMember]
    public uint[] TrainedSkills {
        get => [.. TrainedSkillXp];
        set => TrainedSkillXp = value?.ToList() ?? [];
    }
    [IgnoreMember]
    public uint[] SpecializedSkills {
        get => [.. SpecializedSkillXp];
        set => SpecializedSkillXp = value?.ToList() ?? [];
    }
}

public sealed partial class TerrainType {
    [IgnoreMember] public List<uint> SceneTypes { get => SceneTypeIndices; set => SceneTypeIndices = value; }
}

public sealed partial class SceneType {
    [IgnoreMember] public List<uint> Scenes { get => Dids; set => Dids = value; }
}

public sealed partial class ObjectDesc {
    [IgnoreMember]
    public Frame BaseLoc {
        get => new() { OriginArray = OriginArray, OrientationArray = OrientationArray };
        set {
            OriginArray = value.OriginArray;
            OrientationArray = value.OrientationArray;
        }
    }
}

public sealed partial class SurfaceTexture {
    [IgnoreMember]
    public TextureType Type {
        get => (TextureType)this.TextureType;
        set => this.TextureType = (byte)value;
    }
}

public sealed partial class SpellComponentBase {
    [IgnoreMember]
    public uint Type { get => ComponentType; set => ComponentType = value; }
    [IgnoreMember]
    public uint Icon { get => IconId; set => IconId = value; }
}

public sealed partial class SpellBase {
    [IgnoreMember]
    public uint Icon { get => IconId; set => IconId = value; }
    [IgnoreMember]
    public List<uint> Components { get => ComponentIds; set => ComponentIds = value; }
}
