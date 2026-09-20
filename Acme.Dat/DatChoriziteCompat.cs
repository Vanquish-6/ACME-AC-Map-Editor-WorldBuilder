using System.Numerics;
using System.Text;
using MessagePack;

namespace Acme.Dat;

public struct TerrainInfo {
    private ushort _value;
    public TerrainInfo(ushort value) => _value = value;
    public byte Road {
        get => (byte)(_value & 0x3);
        set => _value = (ushort)((_value & ~0x3) | (value & 0x3));
    }
    public TerrainTextureType Type {
        get => (TerrainTextureType)((_value & 0x7C) >> 2);
        set => _value = (ushort)((_value & ~0x7C) | (((byte)value & 0x1F) << 2));
    }
    public byte Scenery {
        get => (byte)((_value & 0xF800) >> 11);
        set => _value = (ushort)((_value & ~0xF800) | ((value & 0x1F) << 11));
    }
    public static implicit operator ushort(TerrainInfo info) => info._value;
    public static implicit operator TerrainInfo(ushort value) => new(value);
}

[Flags]
public enum StipplingType : byte {
    None = 0x00,
    Positive = 0x01,
    Negative = 0x02,
    Both = 0x03,
    NoPos = 0x04,
    NoNeg = 0x08,
    NoUVS = 0x14,
}

public enum CullMode : int {
    Landblock = 0,
    None = 1,
    Clockwise = 2,
    CounterClockwise = 3,
}

public enum VertexType : int {
    UnknownVertexType = 0,
    CSWVertexType = 1,
}

[Flags]
public enum PortalFlags : ushort {
    ExactMatch = 0x0001,
    PortalSide = 0x0002,
}

public enum EmitterType : int {
    Unknown = 0,
    BirthratePerSec = 1,
    BirthratePerMeter = 2,
}

public static class PixelFormats {
    public const uint CustomLandscapeAlpha = 0x000001F5;
}

public sealed partial class SpellBase {
    public static uint GetStringHash(string strToHash) {
        if (string.IsNullOrEmpty(strToHash)) {
            return 0;
        }

        long result = 0;
        foreach (sbyte c in Encoding.Latin1.GetBytes(strToHash)) {
            result = c + (result << 4);
            if ((result & 0xF0000000) != 0) {
                result = (result ^ ((result & 0xF0000000) >> 24)) & 0x0FFFFFFF;
            }
        }

        return (uint)result;
    }
}

public sealed partial class StringTableString {
    [IgnoreMember]
    public string Value {
        get => Strings.Count > 0 ? Strings[0] : "";
        set {
            if (Strings.Count == 0) {
                Strings.Add(value);
            }
            else {
                Strings[0] = value;
            }
        }
    }
}

public sealed partial class StringTable {
    [IgnoreMember]
    public Dictionary<uint, StringTableString> Strings {
        get => Entries;
        set => Entries = value;
    }
}

public sealed partial class CharGen {
    [IgnoreMember] public uint DataId { get => Id; set => Id = value; }
}

public sealed partial class SkillCG {
    [IgnoreMember] public int Id { get => SkillId; set => SkillId = value; }
}

public sealed partial class TemplateCG {
    [IgnoreMember] public int Self { get => SelfStat; set => SelfStat = value; }
    [IgnoreMember] public List<int> NormalSkills { get => TrainedSkills; set => TrainedSkills = value; }
    [IgnoreMember] public List<int> PrimarySkills { get => SpecializedSkills; set => SpecializedSkills = value; }
}

public sealed partial class SexCG {
    [IgnoreMember] public uint IconId { get => IconDid; set => IconDid = value; }
    [IgnoreMember] public uint BasePalette { get => BasePaletteDid; set => BasePaletteDid = value; }
    [IgnoreMember] public uint SkinPalSet { get => SkinPaletteDid; set => SkinPaletteDid = value; }
}

public sealed partial class Position {
    [IgnoreMember]
    public Frame Frame {
        get => new() { OriginArray = OriginArray, OrientationArray = OrientationArray };
        set {
            OriginArray = value.OriginArray;
            OrientationArray = value.OrientationArray;
        }
    }
}

public sealed partial class ObjDesc {
    [IgnoreMember] public byte UnknownByte { get => Flags; set => Flags = value; }
}

public sealed partial class SubPalette {
    [IgnoreMember] public byte NumColors { get => Length; set => Length = value; }
}

public sealed partial class LandBlockInfo {
    [IgnoreMember]
    public Dictionary<uint, uint> RestrictionTable {
        get {
            var table = new Dictionary<uint, uint>();
            foreach (var (key, value) in Restrictions) {
                table[key] = value;
            }
            return table;
        }
        set => Restrictions = value.Select(kv => (kv.Key, kv.Value)).ToList();
    }
}

public sealed partial class BuildingPortal {
    [IgnoreMember] public List<ushort> StabList { get => StabCells; set => StabCells = value; }
}

public sealed partial class Polygon {
    [IgnoreMember] public List<byte> PosUVIndices { get => PosUvIndices; set => PosUvIndices = value; }
    [IgnoreMember] public List<byte> NegUVIndices { get => NegUvIndices; set => NegUvIndices = value; }
}

public sealed partial class GfxObj {
    [IgnoreMember]
    public Vector3 SortCenter {
        get => DatFrames.Vec3(SortCenterArray);
        set => SortCenterArray = DatFrames.From(value);
    }
}

public sealed partial class ParticleEmitter {
    [IgnoreMember]
    public Vector3 OffsetDir {
        get => DatFrames.Vec3(OffsetDirArray);
        set => OffsetDirArray = DatFrames.From(value);
    }
    [IgnoreMember]
    public Vector3 A {
        get => DatFrames.Vec3(AArray);
        set => AArray = DatFrames.From(value);
    }
    [IgnoreMember]
    public Vector3 B {
        get => DatFrames.Vec3(BArray);
        set => BArray = DatFrames.From(value);
    }
    [IgnoreMember]
    public Vector3 C {
        get => DatFrames.Vec3(CArray);
        set => CArray = DatFrames.From(value);
    }
    [IgnoreMember]
    public EmitterType EmitterKind {
        get => (EmitterType)EmitterType;
        set => EmitterType = (int)value;
    }
}
