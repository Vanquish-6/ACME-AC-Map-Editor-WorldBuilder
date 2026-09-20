using System.Numerics;
using MessagePack;

namespace Acme.Dat;

internal static class DatRecordMaps {
    public static void AfterDeserialize(object record) {
        switch (record) {
            case Setup setup:
                setup.HoldingLocations = ToDict(setup.HoldingLocationsEntries);
                setup.ConnectionPoints = ToDict(setup.ConnectionPointsEntries);
                setup.Lights = ToDict(setup.LightsEntries);
                setup.PlacementFrames = setup.PlacementFrameList.ToDictionary(p => (Placement)p.Key, p => p);
                break;
            case StringTable table:
                table.Entries = ToDict(table.EntriesEntries);
                break;
            case ClothingTable clothing:
                clothing.BaseEffects = ToDict(clothing.BaseEffectsEntries);
                clothing.SubPalEffects = ToDict(clothing.SubPalEffectsEntries);
                break;
            case SkillTable skills:
                skills.Skills = ToDict(skills.SkillsEntries)
                    .ToDictionary(kv => (SkillId)(int)kv.Key, kv => kv.Value);
                break;
            case GfxObj gfx:
                gfx.Vertices = ToDict(gfx.VerticesEntries);
                gfx.RenderVertices = ToDict(gfx.RenderVerticesEntries);
                gfx.DrawingPolygons = ToDict(gfx.DrawingPolygonsEntries);
                gfx.VertexArray = new VertexArray {
                    VertexType = (VertexType)gfx.VertexType,
                    Vertices = ToSwVertices(gfx.RenderVertices, gfx.Vertices),
                };
                gfx.Polygons = gfx.DrawingPolygons.ToDictionary(kv => (ushort)kv.Key, kv => kv.Value);
                if (gfx.Physics != null) {
                    var physics = ToDict(gfx.Physics.RawPolygonsEntries);
                    gfx.PhysicsPolygons = physics.ToDictionary(kv => (ushort)kv.Key, kv => kv.Value);
                }
                break;
            case CellStruct cell:
                HydrateCellStruct(cell);
                break;
            case Environment env:
                env.Cells = ToDict(env.CellsEntries);
                foreach (var cell in env.Cells.Values) {
                    cell.AfterDeserialize();
                }
                break;
            case HeritageGroupCG group:
                group.Genders = ToDict(group.GendersEntries);
                break;
            case CharGen chargen:
                chargen.HeritageGroups = ToDict(chargen.HeritageGroupsEntries);
                foreach (var heritage in chargen.HeritageGroups.Values) {
                    heritage.Genders = ToDict(heritage.GendersEntries);
                }
                break;
            case SpellTable spells:
                spells.Spells = ToDict(spells.SpellsEntries);
                foreach (var entry in spells.SpellSetsEntries) {
                    entry.Value.SpellSetTiers = ToDict(entry.Value.Tiers);
                }
                spells.SpellsSets = ToDict(spells.SpellSetsEntries)
                    .ToDictionary(kv => (EquipmentSet)kv.Key, kv => kv.Value);
                break;
            case SpellComponentTable comps:
                comps.Components = ToDict(comps.ComponentsEntries);
                break;
        }
    }

    public static void BeforeSerialize(object record) {
        switch (record) {
            case Setup setup:
                setup.HoldingLocationsEntries = FromDict(setup.HoldingLocations, setup.HoldingLocationEntryOrder);
                setup.ConnectionPointsEntries = FromDict(setup.ConnectionPoints, setup.ConnectionPointEntryOrder);
                setup.LightsEntries = FromDict(setup.Lights, setup.LightEntryOrder);
                setup.PlacementFrameList = setup.PlacementFrames.Select(kv => {
                    kv.Value.Key = (uint)kv.Key;
                    return kv.Value;
                }).ToList();
                break;
            case StringTable table:
                table.EntriesEntries = FromDict(table.Entries, table.EntryOrder);
                break;
            case ClothingTable clothing:
                clothing.BaseEffectsEntries = FromDict(clothing.BaseEffects, clothing.BaseEffectEntryOrder);
                clothing.SubPalEffectsEntries = FromDict(clothing.SubPalEffects, clothing.SubPalEffectEntryOrder);
                break;
            case SkillTable skills:
                skills.SkillsEntries = FromDict(
                    skills.Skills.ToDictionary(kv => (uint)(int)kv.Key, kv => kv.Value),
                    skills.EntryOrder);
                break;
            case GfxObj gfx:
                gfx.VerticesEntries = FromDict(gfx.Vertices, gfx.VertexEntryOrder);
                gfx.RenderVerticesEntries = FromDict(gfx.RenderVertices, gfx.VertexEntryOrder);
                gfx.DrawingPolygonsEntries = FromDict(gfx.DrawingPolygons, gfx.DrawingPolygonEntryOrder);
                break;
            case CellStruct cell:
                FlattenCellStruct(cell);
                break;
            case Environment env:
                foreach (var cell in env.Cells.Values) {
                    cell.BeforeSerialize();
                }
                env.CellsEntries = FromDict(env.Cells, env.CellEntryOrder);
                break;
            case HeritageGroupCG group:
                group.GendersEntries = FromDict(group.Genders, group.GenderEntryOrder);
                break;
            case CharGen chargen:
                foreach (var heritage in chargen.HeritageGroups) {
                    heritage.Value.GendersEntries = FromDict(heritage.Value.Genders, heritage.Value.GenderEntryOrder);
                }
                chargen.HeritageGroupsEntries = FromDict(chargen.HeritageGroups, chargen.HeritageEntryOrder);
                break;
            case SpellTable spells:
                foreach (var set in spells.SpellsSets.Values) {
                    set.Tiers = FromDict(set.SpellSetTiers, set.TierIds);
                }
                spells.SpellsEntries = FromDict(spells.Spells, spells.SpellIds);
                spells.SpellSetsEntries = FromDict(
                    spells.SpellsSets.ToDictionary(kv => (uint)kv.Key, kv => kv.Value),
                    spells.SpellSetIds);
                break;
            case SpellComponentTable comps:
                comps.ComponentsEntries = FromDict(comps.Components, comps.ComponentIds);
                break;
        }
    }

    private static void HydrateCellStruct(CellStruct cell) {
        var renderVertices = ToDict(cell.RenderVerticesEntries);
        var positions = ToDict(cell.VerticesEntries);
        cell.VertexArray = new VertexArray {
            VertexType = (VertexType)cell.VertexType,
            Vertices = ToSwVertices(renderVertices, positions),
        };
        cell.Polygons = ToUshortPolyMap(cell.PolygonsEntries);
        cell.PhysicsPolygons = ToUshortPolyMap(cell.RawPhysicsPolygonsEntries);
    }

    private static void FlattenCellStruct(CellStruct cell) {
        if (cell.VertexArray.Vertices.Count > 0) {
            cell.RenderVerticesEntries = FromDict(
                cell.VertexArray.Vertices.ToDictionary(
                    kv => kv.Key,
                    kv => new GfxVertex {
                        OriginArray = DatFrames.From(kv.Value.Origin),
                        NormalArray = DatFrames.From(kv.Value.Normal),
                        Uvs = kv.Value.UVs.Select(uv => new[] { uv.U, uv.V }).ToList(),
                    }),
                cell.VertexEntryOrder);
            cell.VerticesEntries = FromDict(
                cell.VertexArray.Vertices.ToDictionary(kv => kv.Key, kv => DatFrames.From(kv.Value.Origin)),
                cell.VertexEntryOrder);
        }
        if (cell.Polygons.Count > 0) {
            cell.PolygonsEntries = FromDict(
                cell.Polygons.ToDictionary(kv => (uint)kv.Key, kv => kv.Value),
                cell.PolygonEntryOrder);
        }
        if (cell.PhysicsPolygons.Count > 0) {
            cell.RawPhysicsPolygonsEntries = FromDict(
                cell.PhysicsPolygons.ToDictionary(kv => (uint)kv.Key, kv => kv.Value),
                cell.RawPhysicsPolygonEntryOrder);
        }
    }

    private static Dictionary<ushort, Polygon> ToUshortPolyMap(List<MapEntry<uint, Polygon>>? entries) =>
        ToDict(entries).ToDictionary(kv => (ushort)kv.Key, kv => kv.Value);

    private static Dictionary<ushort, SWVertex> ToSwVertices(
        Dictionary<ushort, GfxVertex> renderVertices,
        Dictionary<ushort, float[]> positions) {
        if (renderVertices.Count > 0) {
            return renderVertices.ToDictionary(kv => kv.Key, kv => ToSwVertex(kv.Value));
        }
        return positions.ToDictionary(
            kv => kv.Key,
            kv => new SWVertex {
                Origin = DatFrames.Vec3(kv.Value),
                Normal = Vector3.UnitZ,
                UVs = [],
            });
    }

    private static SWVertex ToSwVertex(GfxVertex vertex) => new() {
        Origin = DatFrames.Vec3(vertex.OriginArray),
        Normal = DatFrames.Vec3(vertex.NormalArray),
        UVs = vertex.Uvs.Select(uv => new Vec2Duv {
            U = uv.Length > 0 ? uv[0] : 0,
            V = uv.Length > 1 ? uv[1] : 0,
        }).ToList(),
    };

    private static Dictionary<TKey, TValue> ToDict<TKey, TValue>(List<MapEntry<TKey, TValue>>? entries)
        where TKey : notnull =>
        entries?.ToDictionary(e => e.Key, e => e.Value) ?? new();

    private static List<MapEntry<TKey, TValue>> FromDict<TKey, TValue>(
        Dictionary<TKey, TValue>? map, List<TKey>? order) where TKey : notnull {
        if (map == null || map.Count == 0) {
            return [];
        }
        var keys = order is { Count: > 0 } ? order.Where(map.ContainsKey).Concat(map.Keys.Where(k => order.Contains(k) == false)) : map.Keys;
        var seen = new HashSet<TKey>();
        var list = new List<MapEntry<TKey, TValue>>();
        foreach (var key in keys) {
            if (seen.Add(key)) {
                list.Add(new MapEntry<TKey, TValue> { Key = key, Value = map[key] });
            }
        }
        return list;
    }
}

public sealed partial class Frame {
    [IgnoreMember]
    public Vector3 Origin {
        get => DatFrames.Vec3(OriginArray);
        set => OriginArray = DatFrames.From(value);
    }

    [IgnoreMember]
    public Quaternion Orientation {
        get => DatFrames.Quat(OrientationArray);
        set => OrientationArray = DatFrames.From(value);
    }
}

public sealed partial class Stab {
    [IgnoreMember]
    public Frame Frame {
        get => new() { OriginArray = OriginArray, OrientationArray = AnglesArray };
        set {
            OriginArray = value.OriginArray;
            AnglesArray = value.OrientationArray;
        }
    }

    [IgnoreMember]
    public Vector3 Origin {
        get => DatFrames.Vec3(OriginArray);
        set => OriginArray = DatFrames.From(value);
    }

    [IgnoreMember]
    public Quaternion Orientation {
        get => DatFrames.Quat(AnglesArray);
        set => AnglesArray = DatFrames.From(value);
    }
}

public sealed partial class BuildingInfo {
    [IgnoreMember]
    public Frame Frame {
        get => new() { OriginArray = OriginArray, OrientationArray = AnglesArray };
        set {
            OriginArray = value.OriginArray;
            AnglesArray = value.OrientationArray;
        }
    }
}

public sealed partial class EnvCell {
    [IgnoreMember]
    public Frame Position {
        get => new() { OriginArray = PositionOriginArray, OrientationArray = PositionQuatArray };
        set {
            PositionOriginArray = value.OriginArray;
            PositionQuatArray = value.OrientationArray;
        }
    }

    [IgnoreMember]
    public uint RestrictionObj {
        get => RestrictionObjNullable ?? 0;
        set => RestrictionObjNullable = value;
    }
}

public sealed partial class Surface {
    [IgnoreMember] public uint Id { get; set; }
    [IgnoreMember]
    public SurfaceType Type {
        get => (SurfaceType)Stype;
        set => Stype = (uint)value;
    }
}

public sealed partial class ExperienceTable {
    [IgnoreMember] public uint Id { get; set; } = 0x0E000018;
}

public sealed partial class SkillTable {
    [IgnoreMember] public uint Id { get; set; } = 0x0E000004;
    [IgnoreMember] public Dictionary<SkillId, SkillBase> Skills { get; set; } = new();
}

public sealed partial class VitalTable {
    [IgnoreMember] public uint Id { get; set; } = 0x0E000003;
}

public sealed partial class CharGen {
    [IgnoreMember] public uint Id { get; set; } = 0x0E000002;
    [IgnoreMember] public Dictionary<uint, HeritageGroupCG> HeritageGroups { get; set; } = new();
}

public sealed partial class RenderSurface {
    [IgnoreMember]
    public PixelFormat FormatEnum {
        get => (PixelFormat)Format;
        set => Format = (uint)value;
    }
}

public sealed partial class Palette {
    [IgnoreMember]
    public ColorARGB[] ColorValues => Colors.Select(ColorARGB.FromPacked).ToArray();
}

public sealed partial class Setup {
    [IgnoreMember] public Dictionary<uint, SetupLocation> HoldingLocations { get; set; } = new();
    [IgnoreMember] public Dictionary<uint, SetupLocation> ConnectionPoints { get; set; } = new();
    [IgnoreMember] public Dictionary<uint, LightInfo> Lights { get; set; } = new();
    [IgnoreMember] public Dictionary<Placement, SetupPlacement> PlacementFrames { get; set; } = new();
}

public sealed partial class SetupPlacement {
    public SetupPlacement() { }
    public SetupPlacement(int _) { }

    [IgnoreMember]
    public List<Frame> Frames {
        get => PartFrames;
        set => PartFrames = value;
    }
}

public sealed partial class StringTable {
    [IgnoreMember] public Dictionary<uint, StringTableString> Entries { get; set; } = new();
}

public sealed partial class RoadAlphaMap {
    [IgnoreMember] public uint RCode { get => Rcode; set => Rcode = value; }
}

public sealed partial class ClothingTable {
    [IgnoreMember] public Dictionary<uint, ClothingBaseEffect> BaseEffects { get; set; } = new();
    [IgnoreMember] public Dictionary<uint, CloSubPalEffect> SubPalEffects { get; set; } = new();
    [IgnoreMember] public Dictionary<uint, CloSubPalEffect> ClothingSubPalEffects {
        get => SubPalEffects;
        set => SubPalEffects = value;
    }
}

public sealed partial class CloSubPalEffect {
    [IgnoreMember] public List<CloSubPalette> CloSubPalettes { get => SubPalettes; set => SubPalettes = value; }
}

public sealed partial class PalSet {
    [IgnoreMember] public List<uint> Palettes { get => PaletteIds; set => PaletteIds = value; }
}

public sealed partial class GfxObj {
    [IgnoreMember] public Dictionary<ushort, float[]> Vertices { get; set; } = new();
    [IgnoreMember] public Dictionary<ushort, GfxVertex> RenderVertices { get; set; } = new();
    [IgnoreMember] public Dictionary<uint, Polygon> DrawingPolygons { get; set; } = new();
}

public sealed partial class Environment {
    [IgnoreMember] public Dictionary<uint, CellStruct> Cells { get; set; } = new();
}

public sealed partial class HeritageGroupCG {
    [IgnoreMember] public Dictionary<int, SexCG> Genders { get; set; } = new();
}

public sealed partial class SexCG {
    [IgnoreMember] public uint SetupId { get => SetupDid; set => SetupDid = value; }
    [IgnoreMember] public uint SoundTable { get => SoundTableDid; set => SoundTableDid = value; }
    [IgnoreMember] public uint Icon { get => IconDid; set => IconDid = value; }
    [IgnoreMember] public uint PhysicsTable { get => PhysicsTableDid; set => PhysicsTableDid = value; }
    [IgnoreMember] public uint MotionTable { get => MotionTableDid; set => MotionTableDid = value; }
    [IgnoreMember] public uint CombatTable { get => CombatTableDid; set => CombatTableDid = value; }
    [IgnoreMember] public List<GearCG> Headgears { get => HeadgearList; set => HeadgearList = value; }
    [IgnoreMember] public List<GearCG> Shirts { get => ShirtList; set => ShirtList = value; }
    [IgnoreMember] public List<GearCG> Pants { get => PantsList; set => PantsList = value; }
    [IgnoreMember] public List<GearCG> Footwear { get => FootwearList; set => FootwearList = value; }
}

public sealed partial class ObjDesc {
    [IgnoreMember] public List<AnimPartChange> AnimPartChanges { get => ModelChanges; set => ModelChanges = value; }
    [IgnoreMember]
    public uint PaletteId {
        get => BasePalette ?? 0;
        set => BasePalette = value;
    }
}

public sealed partial class SubPalette {
    [IgnoreMember] public uint SubId { get => PaletteDid; set => PaletteDid = value; }
}

public sealed partial class AnimPartChange {
    [IgnoreMember] public uint PartId { get => NewModelDid; set => NewModelDid = value; }
}

public sealed partial class TextureMapChange {
    [IgnoreMember] public uint OldTexture { get => OldTextureDid; set => OldTextureDid = value; }
    [IgnoreMember] public uint NewTexture { get => NewTextureDid; set => NewTextureDid = value; }
}

public sealed partial class SpellTable {
    [IgnoreMember] public Dictionary<uint, SpellBase> Spells { get; set; } = new();
    [IgnoreMember] public Dictionary<EquipmentSet, SpellSet> SpellsSets { get; set; } = new();
}

[MessagePackObject]
public sealed class SpellSetTiers {
    [Key("spells")] public List<uint> Spells { get; set; } = [];
}

[MessagePackObject]
public sealed class SpellSet {
    [Key("buckets")] public ushort Buckets { get; set; }
    [Key("tier_ids")] public List<uint> TierIds { get; set; } = [];
    [Key("tiers")] public List<MapEntry<uint, SpellSetTiers>> Tiers { get; set; } = [];
    [IgnoreMember] public Dictionary<uint, SpellSetTiers> SpellSetTiers { get; set; } = new();
}

public sealed partial class SkillFormula {
    [IgnoreMember]
    public AttributeId Attribute1 { get => (AttributeId)Attr1; set => Attr1 = (uint)value; }
    [IgnoreMember]
    public AttributeId Attribute2 { get => (AttributeId)Attr2; set => Attr2 = (uint)value; }
    [IgnoreMember]
    public int Attribute1Multiplier { get => Attr1Multiplier; set => Attr1Multiplier = value; }
    [IgnoreMember]
    public int Attribute2Multiplier { get => Attr2Multiplier; set => Attr2Multiplier = value; }
}

public sealed partial class VitalFormula {
    [IgnoreMember]
    public AttributeId Attribute1 { get => (AttributeId)Attr1; set => Attr1 = (uint)value; }
    [IgnoreMember]
    public AttributeId Attribute2 { get => (AttributeId)Attr2; set => Attr2 = (uint)value; }
    [IgnoreMember]
    public uint Attribute1Multiplier { get => Attr1Multiplier; set => Attr1Multiplier = value; }
    [IgnoreMember]
    public uint Attribute2Multiplier { get => Attr2Multiplier; set => Attr2Multiplier = value; }
}

public sealed partial class SpellComponentTable {
    [IgnoreMember] public Dictionary<uint, SpellComponentBase> Components { get; set; } = new();
}

public sealed partial class Sphere {
    [IgnoreMember]
    public Vector3 Origin {
        get => new(X, Y, Z);
        set { X = value.X; Y = value.Y; Z = value.Z; }
    }
}

public sealed partial class LayoutDesc {
    [IgnoreMember] public Dictionary<uint, ElementDesc> Elements { get; set; } = new();
}

public readonly struct QualifiedDataId {
    public uint DataId { get; }
    public QualifiedDataId(uint id) => DataId = id;
    public static implicit operator uint(QualifiedDataId id) => id.DataId;
    public static implicit operator QualifiedDataId(uint id) => new(id);
}

public readonly struct QualifiedDataId<T> {
    public uint DataId { get; }
    public QualifiedDataId(uint id) => DataId = id;
    public static implicit operator uint(QualifiedDataId<T> id) => id.DataId;
    public static implicit operator QualifiedDataId<T>(uint id) => new(id);
}

public sealed partial class ParticleEmitter {
    [IgnoreMember] public QualifiedDataId GfxObject { get => new(GfxObjId); set => GfxObjId = value.DataId; }
    [IgnoreMember] public QualifiedDataId HwGfxObject { get => new(HwGfxObjId); set => HwGfxObjId = value.DataId; }
}

public sealed class TerrainDesc {
    public List<TerrainType> TerrainTypes { get; set; } = [];
    public LandSurf? LandSurfaces { get; set; }
}

public sealed class SceneDesc {
    public List<SceneType> SceneTypes { get; set; } = [];
}

public sealed partial class Region {
    [IgnoreMember]
    public SceneDesc SceneInfo => new() { SceneTypes = SceneTypes };

    [IgnoreMember]
    public LandSurf? LandSurfaces {
        get => LandSurf;
        set => LandSurf = value;
    }

    [IgnoreMember]
    public TerrainDesc TerrainInfo => new() {
        TerrainTypes = TerrainTypes,
        LandSurfaces = LandSurf,
    };
}

public sealed partial class TMTerrainDesc {
    [IgnoreMember]
    public TerrainTextureType TerrainTexture {
        get => TerrainType;
        set => TerrainType = value;
    }
}
