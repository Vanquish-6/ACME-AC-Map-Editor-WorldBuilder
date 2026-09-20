using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Full pre-ToD CharGen (0x0E000002) decoder for legacy Dark Majesty / Infiltration era payloads.
/// The DM client revision uses u32-length strings instead of u16 pstrings, so both revisions
/// are attempted.
///
/// Layout:
///   u32 id
///   N x u32 chargen-room ids (0x31......, list ends at first non-0x31 dword)
///   u32 numStartingAreas x { str name, align4, u32 n x Position(32B: cell, origin, quat wxyz) }
///   u32 numHeritages x HeritageGroup
/// HeritageGroup:
///   str name, align4, u32 icon(0x06), u32 setup(0x02), u32 envSetup(0x31), u32 setup2(0x02)
///   u32-list primaryStartAreas, u32-list secondaryStartAreas, u32 numGenders x SexCG
/// SexCG:
///   str name, align4, u32 setup(0x02), u32 soundTable(0x20), u32 icon(0x06), u32 envSetup(0x31)
///   ObjDesc baseBody, u32 setupAlt(0x02), u32 baseSkinTexture(0x05)
///   strList namePrefixes, strList nameSuffixes
///   u32 attributeCredits, u32 0, u32 skillCredits, u32 0
///   u32 n x { u32 skillNum, u32 normalCost, u32 primaryCost }
///   u32 n x Template { str name, align4, u32 icon, u32 setup(0x31), u32 title, u32 attrs[6],
///                      u32-list normalSkills, u32-list primarySkills, u32-list secondarySkills }
///   u32 unknown, u32 skinPalette(0x04), u32 skinPalSet(0x0F)
///   u32-list hairPalSets(0x0F)
///   u32 n x HairStyle { u32 icon, u32 bald, ObjDesc }
///   u32-list eyePalettes(0x04)
///   u32 n x EyeStrip { u32 icon, u32 iconBald, ObjDesc, ObjDesc bald }
///   u32 n x FaceStrip { u32 icon, ObjDesc }   (nose, then mouth)
///   4 x gear list { str name, align4, u32 clothingTable(0x10), u32 wcid }
///   u32-list clothingColors, u32 trailing
/// ObjDesc (packed): u8 ver(0x11), u8 nPal, u8 nTm, u8 nAp,
///   nPal x { u16 palId(0x04), u8 offset, u8 numColors },
///   nTm x { u8 part, u16 oldTex(0x05), u16 newTex(0x05) },
///   nAp x { u8 part, u16 partId(0x01) }, align4.
/// </summary>
public static class LegacyDatDmCharGenDecoder {
    public sealed class DmCharGen {
        public List<uint> RoomSetups { get; } = [];
        public List<StartingArea> StartingAreas { get; } = [];
        public List<DmHeritage> Heritages { get; } = [];
        /// <summary>True when the payload used the DM-client u32-length string revision.</summary>
        public bool UsedWideStrings { get; internal set; }
    }

    public sealed class DmHeritage {
        public string Name = string.Empty;
        public uint Icon;
        public uint Setup;
        public uint EnvironmentSetup31;
        public uint SecondarySetup;
        public uint[] PrimaryStartAreas = [];
        public uint[] SecondaryStartAreas = [];
        public List<DmSex> Genders { get; } = [];
    }

    public sealed class DmSex {
        public string Name = string.Empty;
        public uint Setup;
        public uint SoundTable;
        public uint Icon;
        public uint EnvironmentSetup31;
        public ObjDesc BaseObjDesc = new();
        public uint SetupAlternate;
        public uint BaseSkinTexture;
        public uint AttributeCredits;
        public uint SkillCredits;
        public List<SkillCG> Skills { get; } = [];
        public List<DmTemplate> Templates { get; } = [];
        public uint SkinPalette;
        public uint SkinPalSet;
        public List<uint> HairPalSets { get; } = [];
        public List<DmHairStyle> HairStyles { get; } = [];
        public List<uint> EyePalettes { get; } = [];
        public List<DmEyeStrip> EyeStrips { get; } = [];
        public List<DmFaceStrip> NoseStrips { get; } = [];
        public List<DmFaceStrip> MouthStrips { get; } = [];
        public List<DmGear> Headgear { get; } = [];
        public List<DmGear> Shirts { get; } = [];
        public List<DmGear> Pants { get; } = [];
        public List<DmGear> Footwear { get; } = [];
        public List<uint> ClothingColors { get; } = [];
    }

    public sealed class DmTemplate {
        public string Name = string.Empty;
        public uint Icon;
        public uint Setup31;
        public uint Title;
        public int Strength;
        public int Endurance;
        public int Coordination;
        public int Quickness;
        public int Focus;
        public int Self;
        public uint[] NormalSkills = [];
        public uint[] PrimarySkills = [];
        public uint[] SecondarySkills = [];
    }

    public sealed class DmHairStyle {
        public uint Icon;
        public bool Bald;
        public ObjDesc ObjDesc = new();
    }

    public sealed class DmEyeStrip {
        public uint Icon;
        public uint IconBald;
        public ObjDesc ObjDesc = new();
        public ObjDesc ObjDescBald = new();
    }

    public sealed class DmFaceStrip {
        public uint Icon;
        public ObjDesc ObjDesc = new();
    }

    public sealed class DmGear {
        public string Name = string.Empty;
        public uint ClothingTable;
        public uint Wcid;
    }

    public static bool TryDecode(uint id, byte[] payload, out DmCharGen? data, out string? error) {
        // Server revision (u16 pstrings) first, then the DM client revision (u32 strings).
        if (TryDecode(id, payload, wideStrings: false, out data, out string? compactError)) {
            error = null;
            return true;
        }

        if (TryDecode(id, payload, wideStrings: true, out data, out string? wideError)) {
            error = null;
            return true;
        }

        error = $"compact: {compactError}; wide: {wideError}";
        return false;
    }

    private static bool TryDecode(uint id, byte[] payload, bool wideStrings, out DmCharGen? data, out string? error) {
        data = null;
        error = null;
        if (payload.Length < 64) {
            error = "payload too short";
            return false;
        }

        var p = new Reader(payload, wideStrings);
        try {
            uint fileId = p.U32();
            if (fileId != id) {
                error = $"id echo mismatch 0x{fileId:X8}";
                return false;
            }

            var result = new DmCharGen { UsedWideStrings = wideStrings };
            while (p.Pos + 4 <= payload.Length && p.Peek32() >> 24 == 0x31) {
                result.RoomSetups.Add(p.U32());
            }

            uint areaCount = p.U32();
            if (areaCount > 64) {
                error = $"areaCount {areaCount} @0x{p.Pos:X}";
                return false;
            }

            for (int i = 0; i < areaCount; i++) {
                var area = new StartingArea { Name = p.Str() };
                p.Align4();
                uint locationCount = p.U32();
                if (locationCount > 64) {
                    error = $"locCount {locationCount} area {i} @0x{p.Pos:X}";
                    return false;
                }

                for (int j = 0; j < locationCount; j++) {
                    area.Locations.Add(p.Position());
                }

                result.StartingAreas.Add(area);
            }

            uint heritageCount = p.U32();
            if (heritageCount > 16) {
                error = $"heritageCount {heritageCount} @0x{p.Pos:X}";
                return false;
            }

            for (int i = 0; i < heritageCount; i++) {
                result.Heritages.Add(ReadHeritage(p));
            }

            if (p.Pos != payload.Length) {
                error = $"trailing bytes: pos=0x{p.Pos:X} len=0x{payload.Length:X}";
                return false;
            }

            data = result;
            return true;
        }
        catch (Exception ex) {
            error = $"{ex.GetType().Name}: {ex.Message} @0x{p.Pos:X}";
            return false;
        }
    }

    private static DmHeritage ReadHeritage(Reader p) {
        var heritage = new DmHeritage { Name = p.Str() };
        p.Align4();
        heritage.Icon = p.Did(0x06, "heritage icon");
        heritage.Setup = p.Did(0x02, "heritage setup");
        heritage.EnvironmentSetup31 = p.Did(0x31, "heritage env");
        heritage.SecondarySetup = p.Did(0x02, "heritage setup2");
        heritage.PrimaryStartAreas = p.U32List(16);
        heritage.SecondaryStartAreas = p.U32List(16);
        uint genderCount = p.U32();
        if (genderCount > 4) {
            throw new InvalidDataException($"genderCount {genderCount}");
        }

        for (int i = 0; i < genderCount; i++) {
            heritage.Genders.Add(ReadSex(p));
        }

        return heritage;
    }

    private static DmSex ReadSex(Reader p) {
        var sex = new DmSex { Name = p.Str() };
        p.Align4();
        sex.Setup = p.Did(0x02, "sex setup");
        sex.SoundTable = p.Did(0x20, "sex sound");
        sex.Icon = p.Did(0x06, "sex icon");
        sex.EnvironmentSetup31 = p.Did(0x31, "sex env");
        sex.BaseObjDesc = p.ObjDesc();
        sex.SetupAlternate = p.Did(0x02, "sex setupAlt");
        sex.BaseSkinTexture = p.Did(0x05, "sex skinTex");

        p.StrList(); // name prefixes — retail CharGen has no equivalent
        p.StrList(); // name suffixes

        sex.AttributeCredits = p.U32();
        p.U32();
        sex.SkillCredits = p.U32();
        p.U32();

        uint skillCount = p.U32();
        if (skillCount > 64) {
            throw new InvalidDataException($"skillCount {skillCount}");
        }

        for (int i = 0; i < skillCount; i++) {
            sex.Skills.Add(new SkillCG {
                Id = (int)p.U32(),
                NormalCost = (int)p.U32(),
                PrimaryCost = (int)p.U32(),
            });
        }

        uint templateCount = p.U32();
        if (templateCount > 64) {
            throw new InvalidDataException($"templateCount {templateCount}");
        }

        for (int i = 0; i < templateCount; i++) {
            sex.Templates.Add(ReadTemplate(p));
        }

        p.U32(); // unknown
        sex.SkinPalette = p.Did(0x04, "skinPal");
        sex.SkinPalSet = p.Did(0x0F, "skinPalSet");
        sex.HairPalSets.AddRange(p.U32List(64));

        uint hairStyleCount = p.U32();
        if (hairStyleCount > 64) {
            throw new InvalidDataException($"hairStyleCount {hairStyleCount}");
        }

        for (int i = 0; i < hairStyleCount; i++) {
            sex.HairStyles.Add(new DmHairStyle {
                Icon = p.Did(0x06, "hair icon"),
                Bald = p.U32() != 0,
                ObjDesc = p.ObjDesc(),
            });
        }

        sex.EyePalettes.AddRange(p.U32List(64));

        uint eyeStripCount = p.U32();
        if (eyeStripCount > 64) {
            throw new InvalidDataException($"eyeStripCount {eyeStripCount}");
        }

        for (int i = 0; i < eyeStripCount; i++) {
            sex.EyeStrips.Add(new DmEyeStrip {
                Icon = p.Did(0x06, "eye icon"),
                IconBald = p.Did(0x06, "eye iconBald"),
                ObjDesc = p.ObjDesc(),
                ObjDescBald = p.ObjDesc(),
            });
        }

        ReadFaceStrips(p, sex.NoseStrips);
        ReadFaceStrips(p, sex.MouthStrips);

        ReadGearList(p, sex.Headgear);
        ReadGearList(p, sex.Shirts);
        ReadGearList(p, sex.Pants);
        ReadGearList(p, sex.Footwear);

        sex.ClothingColors.AddRange(p.U32List(64));
        p.U32(); // trailing value

        return sex;
    }

    private static DmTemplate ReadTemplate(Reader p) {
        var template = new DmTemplate { Name = p.Str() };
        p.Align4();
        template.Icon = p.Did(0x06, "template icon");
        template.Setup31 = p.Did(0x31, "template setup");
        template.Title = p.U32();
        var attrs = new uint[6];
        for (int i = 0; i < 6; i++) {
            attrs[i] = p.U32();
            if (attrs[i] > 1000) {
                throw new InvalidDataException($"template attr {attrs[i]}");
            }
        }

        template.Strength = (int)attrs[0];
        template.Endurance = (int)attrs[1];
        template.Coordination = (int)attrs[2];
        template.Quickness = (int)attrs[3];
        template.Focus = (int)attrs[4];
        template.Self = (int)attrs[5];
        template.NormalSkills = p.U32List(64);
        template.PrimarySkills = p.U32List(64);
        template.SecondarySkills = p.U32List(64);
        return template;
    }

    private static void ReadFaceStrips(Reader p, List<DmFaceStrip> strips) {
        uint count = p.U32();
        if (count > 64) {
            throw new InvalidDataException($"faceStripCount {count}");
        }

        for (int i = 0; i < count; i++) {
            strips.Add(new DmFaceStrip {
                Icon = p.Did(0x06, "face icon"),
                ObjDesc = p.ObjDesc(),
            });
        }
    }

    private static void ReadGearList(Reader p, List<DmGear> gear) {
        uint count = p.U32();
        if (count > 64) {
            throw new InvalidDataException($"gearCount {count}");
        }

        for (int i = 0; i < count; i++) {
            var item = new DmGear { Name = p.Str() };
            p.Align4();
            item.ClothingTable = p.Did(0x10, "gear clothing");
            item.Wcid = p.U32();
            if (item.Wcid > 0xFFFF) {
                throw new InvalidDataException($"gear wcid {item.Wcid}");
            }

            gear.Add(item);
        }
    }

    private sealed class Reader {
        private readonly byte[] _payload;
        private readonly bool _wideStrings;

        public Reader(byte[] payload, bool wideStrings) {
            _payload = payload;
            _wideStrings = wideStrings;
        }

        public int Pos { get; private set; }

        public void Align4() => Pos = (Pos + 3) & ~3;

        public byte U8() {
            Ensure(1);
            return _payload[Pos++];
        }

        public ushort U16() {
            Ensure(2);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_payload.AsSpan(Pos));
            Pos += 2;
            return value;
        }

        public uint U32() {
            Ensure(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_payload.AsSpan(Pos));
            Pos += 4;
            return value;
        }

        public uint Peek32() {
            Ensure(4);
            return BinaryPrimitives.ReadUInt32LittleEndian(_payload.AsSpan(Pos));
        }

        public float F32() {
            Ensure(4);
            float value = BinaryPrimitives.ReadSingleLittleEndian(_payload.AsSpan(Pos));
            Pos += 4;
            return value;
        }

        public uint Did(byte prefix, string what) {
            uint value = U32();
            if (value != 0 && value >> 24 != prefix) {
                throw new InvalidDataException($"{what}: 0x{value:X8}");
            }

            return value;
        }

        public uint[] U32List(int max) {
            uint count = U32();
            if (count > max) {
                throw new InvalidDataException($"list count {count}");
            }

            var values = new uint[count];
            for (int i = 0; i < count; i++) {
                values[i] = U32();
            }

            return values;
        }

        public string Str() {
            uint length = _wideStrings ? U32() : U16();
            if (length > 256) {
                throw new InvalidDataException($"string length {length}");
            }

            Ensure((int)length);
            string value = Encoding.ASCII.GetString(_payload, Pos, (int)length).TrimEnd('\0');
            Pos += (int)length;
            return value;
        }

        public void StrList() {
            uint count = U32();
            if (count > 32) {
                throw new InvalidDataException($"string list count {count}");
            }

            for (int i = 0; i < count; i++) {
                Str();
                Align4();
            }
        }

        public Position Position() {
            uint cellId = U32();
            var origin = new Vector3(F32(), F32(), F32());
            float qw = F32();
            float qx = F32();
            float qy = F32();
            float qz = F32();
            return new Position {
                CellId = cellId,
                Frame = new Frame {
                    Origin = origin,
                    Orientation = new Quaternion(qx, qy, qz, qw),
                },
            };
        }

        /// <summary>Packed pre-ToD ObjDesc widened into the retail <see cref="ObjDesc"/> type.</summary>
        public ObjDesc ObjDesc() {
            byte version = U8();
            if (version != 0x11) {
                throw new InvalidDataException($"objdesc version 0x{version:X2}");
            }

            byte palCount = U8();
            byte tmCount = U8();
            byte apCount = U8();
            var objDesc = new ObjDesc { UnknownByte = version };

            for (int i = 0; i < palCount; i++) {
                objDesc.SubPalettes.Add(new SubPalette {
                    SubId = 0x04000000u | U16(),
                    Offset = U8(),
                    NumColors = U8(),
                });
            }

            for (int i = 0; i < tmCount; i++) {
                objDesc.TextureChanges.Add(new TextureMapChange {
                    PartIndex = U8(),
                    OldTexture = 0x05000000u | U16(),
                    NewTexture = 0x05000000u | U16(),
                });
            }

            for (int i = 0; i < apCount; i++) {
                objDesc.AnimPartChanges.Add(new AnimationPartChange {
                    PartIndex = U8(),
                    PartId = 0x01000000u | U16(),
                });
            }

            Align4();
            return objDesc;
        }

        private void Ensure(int count) {
            if (Pos + count > _payload.Length) {
                throw new InvalidDataException("read past end");
            }
        }
    }
}
