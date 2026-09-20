using Acme.Dat;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Maps legacy (Dark Majesty) CharGen starting locations into the retail CharGen shell used by SlimMerge.
/// Dark Majesty uses a pre-retail layout: heritage setup refs (0x31......), then length-prefixed area names.
/// </summary>
public static class LegacyDatCharGenMapper {
    public const uint CharGenId = LegacyDatRetailClientShellCollector.CharGenId;
    private const uint LegacyHeritageSetupPrefix = 0x31000000;

    public static bool TryDecodeLegacyCharGen(byte[] bytes, [MaybeNullWhen(false)] out CharGen charGen) {
        charGen = null;
        if (bytes.Length < 16) {
            return false;
        }

        // Full pre-ToD decode first (byte-exact grammar, both string revisions). Produces real DM
        // heritage groups keyed 1..N with genuine setup refs and starting-area index lists.
        if (LegacyDatDmCharGenDecoder.TryDecode(CharGenId, bytes, out var dmData, out _)
            && dmData != null && (dmData.Heritages.Count > 0 || dmData.StartingAreas.Count > 0)) {
            var decoded = new CharGen {
                Id = CharGenId,
                DataId = CharGenId,
            };

            foreach (var area in dmData.StartingAreas) {
                decoded.StartingAreas.Add(CloneStartingArea(area));
            }

            for (int i = 0; i < dmData.Heritages.Count; i++) {
                var heritage = dmData.Heritages[i];
                var firstGender = heritage.Genders.FirstOrDefault();
                var group = new HeritageGroupCG {
                    Name = heritage.Name,
                    IconId = heritage.Icon,
                    SetupId = heritage.Setup,
                    EnvironmentSetupId = heritage.SecondarySetup,
                    AttributeCredits = firstGender?.AttributeCredits ?? 0,
                    SkillCredits = firstGender?.SkillCredits ?? 0,
                };
                group.PrimaryStartAreas.AddRange(heritage.PrimaryStartAreas.Select(v => (int)v));
                group.SecondaryStartAreas.AddRange(heritage.SecondaryStartAreas.Select(v => (int)v));
                decoded.HeritageGroups[(uint)(i + 1)] = group;
            }

            charGen = decoded;
            return true;
        }

        // Fallback: legacy heuristic scan (kept for unknown/odd revisions).
        try {
            var reader = new DatBinReader(bytes);
            var result = new CharGen {
                Id = reader.ReadUInt32(),
            };

            foreach (uint heritageRef in ReadLegacyHeritageSetupReferences(bytes, ref reader)) {
                if (!result.HeritageGroups.ContainsKey(heritageRef)) {
                    result.HeritageGroups[heritageRef] = new HeritageGroupCG {
                        SetupId = heritageRef,
                        EnvironmentSetupId = 0,
                    };
                }
            }

            result.StartingAreas.AddRange(ReadLegacyStartingAreas(bytes, reader.Offset));

            charGen = result;
            return result.StartingAreas.Count > 0 || result.HeritageGroups.Count > 0;
        }
        catch {
            charGen = null;
            return false;
        }
    }

    public static void ApplySlimMergeStartingAreas(
        LegacyDatReader legacySource,
        DefaultDatReaderWriter outputWriter,
        LegacyDatWorldReferenceClosure? worldClosure,
        IList<string>? warnings = null,
        IReadOnlyDictionary<uint, DatBTreeFile>? seedFileTemplates = null) {
        ArgumentNullException.ThrowIfNull(legacySource);
        ArgumentNullException.ThrowIfNull(outputWriter);

        if (!legacySource.TryGet<CharGen>(CharGenId, out var legacyCharGen) || legacyCharGen == null) {
            warnings?.Add("CharGen: legacy source CharGen could not be decoded; retail starting areas were kept.");
            return;
        }

        if (!outputWriter.TryGet(CharGenId, out CharGen retailCharGen) || retailCharGen == null) {
            warnings?.Add("CharGen: export portal is missing retail CharGen; legacy starting areas were not applied.");
            return;
        }

        var mergedAreas = new List<StartingArea>();
        foreach (var area in legacyCharGen.StartingAreas) {
            var clone = CloneStartingArea(area);
            clone.Locations.RemoveAll(location => !IsPlausibleSpawnCellId(location.CellId));
            if (clone.Locations.Count == 0) {
                continue;
            }

            mergedAreas.Add(clone);
        }

        bool useDmStartingAreas = mergedAreas.Count >= 5;
        if (useDmStartingAreas) {
            retailCharGen.StartingAreas.Clear();
            retailCharGen.StartingAreas.AddRange(mergedAreas);
        }
        else if (mergedAreas.Count > 0) {
            warnings?.Add(
                $"CharGen: only {mergedAreas.Count} DM starting area(s) decoded; keeping retail CharGen starting areas for client login.");
        }
        else {
            warnings?.Add(
                "CharGen: legacy portal had no decodable DM starting areas; keeping retail CharGen starting areas.");
        }

        // Retail starter towns are not in DM cell.dat; pruning would remove every location and fail export validation.
        if (useDmStartingAreas) {
            PruneStartingAreasWithoutExportLandblocks(outputWriter, retailCharGen, warnings);
        }
        ValidateStartingAreaCells(retailCharGen, worldClosure, legacySource, warnings);

        if (!TrySaveCharGenToPortal(outputWriter, retailCharGen, warnings, seedFileTemplates)) {
            warnings?.Add("CharGen: failed to write merged starting areas to export portal.");
        }
    }

    /// <summary>
    /// EnvCell IDs referenced by legacy CharGen starting locations, plus parent landblock/LBI IDs.
    /// </summary>
    public static IEnumerable<uint> CollectStartingAreaCellFileIds(LegacyDatReader legacySource) {
        ArgumentNullException.ThrowIfNull(legacySource);
        if (!legacySource.TryGet<CharGen>(CharGenId, out CharGen? charGen) || charGen == null) {
            yield break;
        }

        var seen = new HashSet<uint>();
        foreach (var area in charGen.StartingAreas) {
            foreach (var location in area.Locations) {
                if (location.CellId == 0 || !seen.Add(location.CellId)) {
                    continue;
                }

                yield return location.CellId;
                uint landBlockId = LandBlockFileIdForCell(location.CellId);
                if (seen.Add(landBlockId)) {
                    yield return landBlockId;
                }

                uint landBlockInfoId = LandBlockInfoFileIdForCell(location.CellId);
                if (seen.Add(landBlockInfoId)) {
                    yield return landBlockInfoId;
                }
            }
        }
    }

    public static void TrackStartingAreasInWorldClosure(
        LegacyDatReader legacySource,
        LegacyDatWorldReferenceClosure closure) {
        ArgumentNullException.ThrowIfNull(legacySource);
        ArgumentNullException.ThrowIfNull(closure);

        if (!legacySource.TryGet<CharGen>(CharGenId, out CharGen? charGen) || charGen == null) {
            return;
        }

        foreach (var area in charGen.StartingAreas) {
            foreach (var location in area.Locations) {
                if (location.CellId == 0) {
                    continue;
                }

                closure.TrackDirect("EnvCell", location.CellId);
                if (legacySource.TryGet<EnvCell>(location.CellId, out EnvCell? envCell) && envCell != null) {
                    LegacyDatWorldReferenceCollector.TrackEnvCell(closure, envCell);
                }
            }
        }
    }

    internal static uint LandBlockFileIdForCell(uint cellId) =>
        ((uint)(cellId >> 16) << 16) | 0xFFFF;

    internal static uint LandBlockInfoFileIdForCell(uint cellId) =>
        ((uint)(cellId >> 16) << 16) | 0xFFFE;

    /// <summary>Outdoor/dungeon spawn cell ids use a landblock key in the high 16 bits.</summary>
    internal static bool IsPlausibleSpawnCellId(uint cellId) {
        if (cellId < 0x00010000) {
            return false;
        }

        uint landblockKey = cellId >> 16;
        if (landblockKey < 0x0040 || landblockKey >= 0xFF00) {
            return false;
        }

        if ((cellId & 0xFF000000u) == LegacyHeritageSetupPrefix
            || (landblockKey & 0xFF00) == (LegacyHeritageSetupPrefix >> 16)) {
            return false;
        }

        uint cellPart = cellId & 0xFFFF;
        return cellPart is not (0xFFFF or 0xFFFE);
    }

    public static void ApplyDmCharGenExport(
        LegacyDatReader legacySource,
        DefaultDatReaderWriter outputWriter,
        string? retailSeedDirectory,
        LegacyDatWorldReferenceClosure? worldClosure,
        IList<string>? warnings = null,
        IReadOnlyDictionary<uint, DatBTreeFile>? seedFileTemplates = null) {
        ArgumentNullException.ThrowIfNull(legacySource);
        ArgumentNullException.ThrowIfNull(outputWriter);

        // Faithful path: full pre-ToD decode of the DM CharGen — real heritage groups, genders,
        // appearance lists, profession templates, and per-heritage starting-area index lists.
        string? decodeError = null;
        if (legacySource.TryReadPortalRawBytes(CharGenId, out byte[]? rawBytes) && rawBytes != null
            && LegacyDatDmCharGenDecoder.TryDecode(CharGenId, rawBytes, out var dmData, out decodeError)
            && dmData != null && dmData.Heritages.Count > 0 && dmData.Heritages.All(h => h.Genders.Count > 0)) {
            ApplyFaithfulDmCharGen(
                dmData,
                outputWriter,
                retailSeedDirectory,
                worldClosure,
                legacySource,
                warnings,
                seedFileTemplates);
            return;
        }

        warnings?.Add(
            $"CharGen: full pre-ToD decode unavailable ({decodeError ?? "no raw payload"}); using retail-clone heritage mapping.");

        if (!legacySource.TryGet<CharGen>(CharGenId, out var legacyCharGen) || legacyCharGen == null) {
            warnings?.Add("CharGen: legacy source CharGen could not be decoded; export omitted DM CharGen.");
            return;
        }

        var exportCharGen = new CharGen {
            Id = CharGenId,
            DataId = CharGenId,
        };

        // Fallback path for undecodable revisions: each exported heritage group starts as a FULL
        // clone of a retail heritage group (closure-complete, validated content) and overlays the
        // DM fields we can map: setup refs, environment setup, and credits.
        var retailGroups = LoadRetailHeritageGroups(retailSeedDirectory, outputWriter, warnings);
        if (retailGroups.Count == 0) {
            warnings?.Add(
                "CharGen: retail heritage groups unavailable; cannot build client-complete DM CharGen. Export omitted DM CharGen.");
            return;
        }

        var orderedLegacyHeritage = legacyCharGen.HeritageGroups.OrderBy(pair => pair.Key).ToList();
        for (int i = 0; i < orderedLegacyHeritage.Count; i++) {
            var (legacyHeritageId, group) = orderedLegacyHeritage[i];
            int templateIndex = Math.Min(i, retailGroups.Count - 1);
            var (retailHeritageId, retailGroup) = retailGroups[templateIndex];

            HeritageGroupCG merged = CloneHeritageGroup(retailGroup, outputWriter.Portal());
            bool isLegacySyntheticSetup = (group.SetupId & 0xFF000000u) == LegacyHeritageSetupPrefix;
            if (!isLegacySyntheticSetup && group.SetupId != 0) {
                merged.SetupId = group.SetupId;
            }

            if (!isLegacySyntheticSetup && group.EnvironmentSetupId != 0) {
                merged.EnvironmentSetupId = group.EnvironmentSetupId;
            }

            if (group.AttributeCredits != 0) {
                merged.AttributeCredits = group.AttributeCredits;
            }

            if (group.SkillCredits != 0) {
                merged.SkillCredits = group.SkillCredits;
            }

            exportCharGen.HeritageGroups[retailHeritageId] = merged;
        }

        foreach (var area in legacyCharGen.StartingAreas) {
            var clone = CloneStartingArea(area);
            clone.Locations.RemoveAll(location => !IsPlausibleSpawnCellId(location.CellId));
            if (clone.Locations.Count == 0) {
                continue;
            }

            exportCharGen.StartingAreas.Add(clone);
        }

        PruneStartingAreasWithoutExportLandblocks(outputWriter, exportCharGen, warnings);
        ValidateStartingAreaCells(exportCharGen, worldClosure, legacySource, warnings);

        // PrimaryStartAreas / SecondaryStartAreas index into StartingAreas; the retail clones carry
        // retail indices (5 areas) while the DM list has a different shape. Rebuild every group's
        // index lists against the exported DM area list so the client never indexes out of range.
        RebuildHeritageStartAreaIndexes(exportCharGen);

        if (exportCharGen.HeritageGroups.Count == 0) {
            warnings?.Add("CharGen: no DM heritage groups were decoded; character creation may be incomplete.");
        }

        if (!TrySaveCharGenToPortal(outputWriter, exportCharGen, warnings, seedFileTemplates)) {
            warnings?.Add("CharGen: failed to write DM CharGen to export portal.");
        }
    }

    /// <summary>
    /// Builds the retail-format CharGen entirely from the decoded DM data (workstream B): DM
    /// heritage groups keyed 1..N, real genders with DM appearance lists, profession templates,
    /// and DM per-heritage starting-area indices. Retail groups are only consulted for the few
    /// fields the pre-ToD table does not carry (Scale, physics/motion/combat tables) and as
    /// fallback when a DM list empties after closure sanitation.
    /// </summary>
    internal static void ApplyFaithfulDmCharGen(
        LegacyDatDmCharGenDecoder.DmCharGen dmData,
        DefaultDatReaderWriter outputWriter,
        string? retailSeedDirectory,
        LegacyDatWorldReferenceClosure? worldClosure,
        LegacyDatReader? legacySource,
        IList<string>? warnings,
        IReadOnlyDictionary<uint, DatBTreeFile>? seedFileTemplates) {
        var retailGroups = LoadRetailHeritageGroups(retailSeedDirectory, outputWriter, warnings);

        var exportCharGen = new CharGen {
            Id = CharGenId,
            DataId = CharGenId,
        };

        foreach (var area in dmData.StartingAreas) {
            // Keep every area slot for now (index stability); pruning + index remap runs below.
            exportCharGen.StartingAreas.Add(CloneStartingArea(area));
        }

        for (int i = 0; i < dmData.Heritages.Count; i++) {
            var dmHeritage = dmData.Heritages[i];
            HeritageGroupCG? retailTemplate = retailGroups.Count > 0
                ? retailGroups[Math.Min(i, retailGroups.Count - 1)].Group
                : null;
            var firstGender = dmHeritage.Genders[0];

            var group = new HeritageGroupCG {
                Name = dmHeritage.Name,
                IconId = dmHeritage.Icon,
                SetupId = dmHeritage.Setup,
                // DM stores the char-create room as the heritage "secondary setup"; the 0x31 env
                // ref is help text, not a Setup. Fall back to the retail room if DM has none.
                EnvironmentSetupId = dmHeritage.SecondarySetup != 0
                    ? dmHeritage.SecondarySetup
                    : retailTemplate?.EnvironmentSetupId ?? 0,
                // Pre-ToD keeps credits per gender; retail keeps them per heritage group.
                AttributeCredits = firstGender.AttributeCredits,
                SkillCredits = firstGender.SkillCredits,
            };
            group.PrimaryStartAreas.AddRange(dmHeritage.PrimaryStartAreas.Select(v => (int)v));
            group.SecondaryStartAreas.AddRange(dmHeritage.SecondaryStartAreas.Select(v => (int)v));
            group.Skills.AddRange(firstGender.Skills);
            group.Templates.AddRange(firstGender.Templates.Select(ConvertDmTemplate));

            for (int g = 0; g < dmHeritage.Genders.Count; g++) {
                var dmSex = dmHeritage.Genders[g];
                int genderKey = GenderKeyFromName(dmSex.Name, g);
                SexCG? retailSex = FindRetailSex(retailTemplate, genderKey);
                group.Genders[genderKey] = BuildSexCG(dmSex, retailSex);
            }

            exportCharGen.HeritageGroups[(uint)(i + 1)] = group;
        }

        SanitizeDmCharGenClosure(outputWriter, exportCharGen, retailGroups, warnings);
        PruneStartingAreasAndRemapIndexes(outputWriter, exportCharGen, warnings);
        ValidateStartingAreaCells(exportCharGen, worldClosure, legacySource, warnings);

        warnings?.Add(
            $"CharGen: faithful DM table exported — {exportCharGen.HeritageGroups.Count} heritage group(s), "
            + $"{exportCharGen.StartingAreas.Count} starting area(s) "
            + $"({(dmData.UsedWideStrings ? "DM-client" : "server")} string revision).");

        if (!TrySaveCharGenToPortal(outputWriter, exportCharGen, warnings, seedFileTemplates)) {
            warnings?.Add("CharGen: failed to write DM CharGen to export portal.");
        }
    }

    private static TemplateCG ConvertDmTemplate(LegacyDatDmCharGenDecoder.DmTemplate dmTemplate) {
        var template = new TemplateCG {
            Name = dmTemplate.Name,
            IconId = dmTemplate.Icon,
            Title = dmTemplate.Title,
            Strength = dmTemplate.Strength,
            Endurance = dmTemplate.Endurance,
            Coordination = dmTemplate.Coordination,
            Quickness = dmTemplate.Quickness,
            Focus = dmTemplate.Focus,
            Self = dmTemplate.Self,
        };
        // DM template "setup" is a 0x31 text blob (profession blurb) with no retail slot; the
        // pre-ToD secondary-skill list is always empty. Both are dropped intentionally.
        template.NormalSkills.AddRange(dmTemplate.NormalSkills.Select(v => (int)v));
        template.PrimarySkills.AddRange(dmTemplate.PrimarySkills.Select(v => (int)v));
        return template;
    }

    private static int GenderKeyFromName(string name, int index) {
        if (string.Equals(name, "Male", StringComparison.OrdinalIgnoreCase)) {
            return 1;
        }

        if (string.Equals(name, "Female", StringComparison.OrdinalIgnoreCase)) {
            return 2;
        }

        return index + 1;
    }

    private static SexCG? FindRetailSex(HeritageGroupCG? retailGroup, int genderKey) {
        if (retailGroup?.Genders == null || retailGroup.Genders.Count == 0) {
            return null;
        }

        if (retailGroup.Genders.TryGetValue(genderKey, out var match)) {
            return match;
        }

        return retailGroup.Genders.Values.FirstOrDefault();
    }

    private static SexCG BuildSexCG(LegacyDatDmCharGenDecoder.DmSex dmSex, SexCG? retailSex) {
        var sex = new SexCG {
            Name = dmSex.Name,
            // Pre-ToD has no per-sex scale or physics/motion/combat table refs; those bands are
            // retail-compatible (gap-filled), so the retail values are the era-correct choices.
            Scale = retailSex?.Scale ?? 100,
            SetupId = dmSex.Setup,
            SoundTable = dmSex.SoundTable != 0 ? dmSex.SoundTable : retailSex?.SoundTable ?? 0,
            IconId = dmSex.Icon,
            BasePalette = dmSex.SkinPalette,
            SkinPalSet = dmSex.SkinPalSet,
            PhysicsTable = retailSex?.PhysicsTable ?? 0,
            MotionTable = retailSex?.MotionTable ?? 0,
            CombatTable = retailSex?.CombatTable ?? 0,
            BaseObjDesc = FixUpObjDesc(dmSex.BaseObjDesc, dmSex.SkinPalette),
        };

        sex.HairColors.AddRange(dmSex.HairPalSets);
        sex.HairStyles.AddRange(dmSex.HairStyles.Select(style => new HairStyleCG {
            IconId = style.Icon,
            Bald = style.Bald,
            AlternateSetup = 0,
            ObjDesc = FixUpObjDesc(style.ObjDesc, dmSex.SkinPalette),
        }));
        sex.EyeColors.AddRange(dmSex.EyePalettes);
        sex.EyeStrips.AddRange(dmSex.EyeStrips.Select(strip => new EyeStripCG {
            IconId = strip.Icon,
            BaldIconId = strip.IconBald,
            ObjDesc = FixUpObjDesc(strip.ObjDesc, dmSex.SkinPalette),
            BaldObjDesc = FixUpObjDesc(strip.ObjDescBald, dmSex.SkinPalette),
        }));
        sex.NoseStrips.AddRange(dmSex.NoseStrips.Select(strip => new FaceStripCG {
            IconId = strip.Icon,
            ObjDesc = FixUpObjDesc(strip.ObjDesc, dmSex.SkinPalette),
        }));
        sex.MouthStrips.AddRange(dmSex.MouthStrips.Select(strip => new FaceStripCG {
            IconId = strip.Icon,
            ObjDesc = FixUpObjDesc(strip.ObjDesc, dmSex.SkinPalette),
        }));
        sex.Headgears.AddRange(dmSex.Headgear.Select(ConvertDmGear));
        sex.Shirts.AddRange(dmSex.Shirts.Select(ConvertDmGear));
        sex.Pants.AddRange(dmSex.Pants.Select(ConvertDmGear));
        sex.Footwear.AddRange(dmSex.Footwear.Select(ConvertDmGear));
        sex.ClothingColors.AddRange(dmSex.ClothingColors);
        return sex;
    }

    private static GearCG ConvertDmGear(LegacyDatDmCharGenDecoder.DmGear gear) => new() {
        Name = gear.Name,
        ClothingTable = gear.ClothingTable,
        WeenieDefault = gear.Wcid,
    };

    /// <summary>
    /// Retail ObjDesc packs a base palette id ahead of the sub-palette list; the pre-ToD inline
    /// form has none, so the gender's skin palette (or the first sub-palette id) fills the slot.
    /// </summary>
    private static ObjDesc FixUpObjDesc(ObjDesc objDesc, uint skinPalette) {
        if (objDesc.SubPalettes.Count > 0 && objDesc.PaletteId == 0) {
            objDesc.PaletteId = skinPalette != 0 ? skinPalette : objDesc.SubPalettes[0].SubId;
        }

        return objDesc;
    }

    /// <summary>
    /// Drops appearance entries whose referenced portal objects are missing from the export so the
    /// char-create preview never dereferences a dangling id. Falls back to the retail list when a
    /// required list would become empty.
    /// </summary>
    private static void SanitizeDmCharGenClosure(
        DefaultDatReaderWriter outputWriter,
        CharGen charGen,
        List<(uint HeritageId, HeritageGroupCG Group)> retailGroups,
        IList<string>? warnings) {
        var portal = outputWriter.Portal();
        if (portal == null) {
            return;
        }

        bool Missing(uint id) => id != 0 && !portal.Catalog().Tree.HasFile(id);

        bool ObjDescBroken(ObjDesc objDesc) =>
            objDesc.AnimPartChanges.Any(part => Missing(part.PartId))
            || objDesc.TextureChanges.Any(tex => Missing(tex.OldTexture) || Missing(tex.NewTexture))
            || objDesc.SubPalettes.Any(pal => Missing(pal.SubId))
            || (objDesc.SubPalettes.Count > 0 && Missing(objDesc.PaletteId));

        int dropped = 0;
        int groupIndex = 0;
        foreach (var (heritageKey, group) in charGen.HeritageGroups.OrderBy(pair => pair.Key)) {
            if (Missing(group.SetupId)) {
                warnings?.Add($"CharGen: heritage '{group.Name}' setup 0x{group.SetupId:X8} missing from export portal.");
            }

            if (Missing(group.EnvironmentSetupId)) {
                warnings?.Add(
                    $"CharGen: heritage '{group.Name}' environment setup 0x{group.EnvironmentSetupId:X8} missing from export portal.");
            }

            HeritageGroupCG? retailTemplate = retailGroups.Count > 0
                ? retailGroups[Math.Min(groupIndex, retailGroups.Count - 1)].Group
                : null;

            foreach (var (genderKey, sex) in group.Genders.OrderBy(pair => pair.Key)) {
                SexCG? retailSex = FindRetailSex(retailTemplate, genderKey);

                if (Missing(sex.SetupId)) {
                    warnings?.Add($"CharGen: '{group.Name}/{sex.Name}' setup 0x{sex.SetupId:X8} missing from export portal.");
                }

                dropped += sex.HairColors.RemoveAll(Missing);
                dropped += sex.EyeColors.RemoveAll(Missing);
                dropped += sex.HairStyles.RemoveAll(style => ObjDescBroken(style.ObjDesc));
                dropped += sex.EyeStrips.RemoveAll(strip => ObjDescBroken(strip.ObjDesc) || ObjDescBroken(strip.BaldObjDesc));
                dropped += sex.NoseStrips.RemoveAll(strip => ObjDescBroken(strip.ObjDesc));
                dropped += sex.MouthStrips.RemoveAll(strip => ObjDescBroken(strip.ObjDesc));
                dropped += sex.Headgears.RemoveAll(gear => Missing(gear.ClothingTable));
                dropped += sex.Shirts.RemoveAll(gear => Missing(gear.ClothingTable));
                dropped += sex.Pants.RemoveAll(gear => Missing(gear.ClothingTable));
                dropped += sex.Footwear.RemoveAll(gear => Missing(gear.ClothingTable));

                if (ObjDescBroken(sex.BaseObjDesc)) {
                    warnings?.Add($"CharGen: '{group.Name}/{sex.Name}' base ObjDesc references missing portal objects.");
                }

                // The char-create screens index these lists directly; an empty list is a crash.
                RestoreListIfEmpty(sex.HairColors, retailSex?.HairColors, $"{group.Name}/{sex.Name} hair colors", warnings);
                RestoreListIfEmpty(sex.HairStyles, retailSex?.HairStyles, $"{group.Name}/{sex.Name} hair styles", warnings);
                RestoreListIfEmpty(sex.EyeColors, retailSex?.EyeColors, $"{group.Name}/{sex.Name} eye colors", warnings);
                RestoreListIfEmpty(sex.EyeStrips, retailSex?.EyeStrips, $"{group.Name}/{sex.Name} eye strips", warnings);
                RestoreListIfEmpty(sex.NoseStrips, retailSex?.NoseStrips, $"{group.Name}/{sex.Name} nose strips", warnings);
                RestoreListIfEmpty(sex.MouthStrips, retailSex?.MouthStrips, $"{group.Name}/{sex.Name} mouth strips", warnings);
                RestoreListIfEmpty(sex.Shirts, retailSex?.Shirts, $"{group.Name}/{sex.Name} shirts", warnings);
                RestoreListIfEmpty(sex.Pants, retailSex?.Pants, $"{group.Name}/{sex.Name} pants", warnings);
                RestoreListIfEmpty(sex.Footwear, retailSex?.Footwear, $"{group.Name}/{sex.Name} footwear", warnings);
            }

            groupIndex++;
        }

        if (dropped > 0) {
            warnings?.Add($"CharGen: dropped {dropped} appearance entry(ies) referencing portal objects missing from the export.");
        }
    }

    private static void RestoreListIfEmpty<T>(List<T> list, List<T>? retailList, string label, IList<string>? warnings) {
        if (list.Count > 0) {
            return;
        }

        if (retailList is { Count: > 0 }) {
            list.AddRange(retailList);
            warnings?.Add($"CharGen: {label} emptied after closure sanitation; restored retail list.");
        }
        else {
            warnings?.Add($"CharGen: {label} is empty and no retail fallback was available; char-create may be unstable.");
        }
    }

    /// <summary>
    /// Removes starting locations without landblock info in the export, drops areas left with no
    /// locations, and remaps every heritage group's Primary/Secondary start-area indices onto the
    /// surviving area list (preserving DM's per-heritage town assignments).
    /// </summary>
    private static void PruneStartingAreasAndRemapIndexes(
        DefaultDatReaderWriter outputWriter,
        CharGen charGen,
        IList<string>? warnings) {
        int removedLocations = 0;
        if (outputWriter.Cell() != null) {
            foreach (var area in charGen.StartingAreas) {
                removedLocations += area.Locations.RemoveAll(location => {
                    if (!IsPlausibleSpawnCellId(location.CellId)) {
                        return true;
                    }

                    uint lbiId = LandBlockInfoFileIdForCell(location.CellId);
                    return !outputWriter.ContainsFile(DatArchive.Cell, lbiId)
                        && !outputWriter.TryGet<LandBlockInfo>(lbiId, out _);
                });
            }
        }

        if (removedLocations > 0) {
            warnings?.Add($"CharGen: removed {removedLocations} starting location(s) with no landblock info in export cell.dat.");
        }

        var indexMap = new Dictionary<int, int>();
        var keptAreas = new List<StartingArea>();
        for (int i = 0; i < charGen.StartingAreas.Count; i++) {
            if (charGen.StartingAreas[i].Locations.Count == 0) {
                continue;
            }

            indexMap[i] = keptAreas.Count;
            keptAreas.Add(charGen.StartingAreas[i]);
        }

        if (keptAreas.Count != charGen.StartingAreas.Count) {
            warnings?.Add(
                $"CharGen: dropped {charGen.StartingAreas.Count - keptAreas.Count} starting area(s) with no usable locations.");
            charGen.StartingAreas.Clear();
            charGen.StartingAreas.AddRange(keptAreas);
        }

        var allAreaIndexes = Enumerable.Range(0, charGen.StartingAreas.Count).ToList();
        foreach (var group in charGen.HeritageGroups.Values) {
            RemapAreaIndexes(group.PrimaryStartAreas, indexMap);
            RemapAreaIndexes(group.SecondaryStartAreas, indexMap);
            if (group.PrimaryStartAreas.Count == 0) {
                group.PrimaryStartAreas.AddRange(allAreaIndexes);
                group.SecondaryStartAreas.Clear();
            }
        }
    }

    private static void RemapAreaIndexes(List<int> indexes, Dictionary<int, int> indexMap) {
        var remapped = indexes
            .Where(indexMap.ContainsKey)
            .Select(oldIndex => indexMap[oldIndex])
            .Distinct()
            .ToList();
        indexes.Clear();
        indexes.AddRange(remapped);
    }

    /// <summary>
    /// Round-trips a retail heritage group through pack/unpack so the clone owns its collections
    /// (Genders, Skills, Templates, appearance lists) instead of aliasing the seed object graph.
    /// </summary>
    internal static HeritageGroupCG CloneHeritageGroup(HeritageGroupCG source, IDatReaderWriter? unpackContext = null) {
        ArgumentNullException.ThrowIfNull(source);
        _ = unpackContext;
        return DatNativeRecords.CloneMessagePack(source);
    }

    /// <summary>
    /// Points every heritage group's start-area index lists at the full exported starting-area list.
    /// </summary>
    internal static void RebuildHeritageStartAreaIndexes(CharGen charGen) {
        ArgumentNullException.ThrowIfNull(charGen);

        var allAreaIndexes = Enumerable.Range(0, charGen.StartingAreas.Count).ToList();
        foreach (var group in charGen.HeritageGroups.Values) {
            group.PrimaryStartAreas.Clear();
            group.PrimaryStartAreas.AddRange(allAreaIndexes);
            group.SecondaryStartAreas.Clear();
        }
    }

    /// <summary>
    /// Writes CharGen 0x0E000002 with the seed entry template when available so the catalog metadata
    /// stays retail-shaped. TryWriteFile replaces an existing id in place (no delete needed).
    /// </summary>
    internal static bool TrySaveCharGenToPortal(
        DefaultDatReaderWriter outputWriter,
        CharGen charGen,
        IList<string>? warnings = null,
        IReadOnlyDictionary<uint, DatBTreeFile>? seedFileTemplates = null) {
        ArgumentNullException.ThrowIfNull(outputWriter);
        ArgumentNullException.ThrowIfNull(charGen);

        var portal = outputWriter.Portal();
        if (portal == null) {
            return false;
        }

        int iteration = portal.Iteration.CurrentIteration;

        if (seedFileTemplates != null
            && seedFileTemplates.TryGetValue(CharGenId, out DatBTreeFile template)) {
            return portal.TryWriteFile(charGen, template);
        }

        if (!portal.TryWriteFile(charGen, iteration)) {
            return false;
        }

        if (LegacyDatPortalCatalogDeduplicator.CountCatalogEntries(portal, CharGenId) > 1) {
            warnings?.Add(
                "CharGen: portal catalog still has duplicate 0x0E000002 entries after replace write; run portal dedupe.");
        }

        return true;
    }

    private static void PruneStartingAreasWithoutExportLandblocks(
        DefaultDatReaderWriter outputWriter,
        CharGen charGen,
        IList<string>? warnings) {
        if (outputWriter.Cell() == null) {
            return;
        }

        int removed = 0;
        foreach (var area in charGen.StartingAreas) {
            removed += area.Locations.RemoveAll(location => {
                if (!IsPlausibleSpawnCellId(location.CellId)) {
                    return true;
                }

                uint lbiId = LandBlockInfoFileIdForCell(location.CellId);
                return !outputWriter.ContainsFile(DatArchive.Cell, lbiId)
                    && !outputWriter.TryGet<LandBlockInfo>(lbiId, out _);
            });
        }

        charGen.StartingAreas.RemoveAll(area => area.Locations.Count == 0);
        if (removed > 0) {
            warnings?.Add($"CharGen: removed {removed} starting location(s) with no landblock info in export cell.dat.");
        }
    }

    private static void ValidateStartingAreaCells(
        CharGen charGen,
        LegacyDatWorldReferenceClosure? worldClosure,
        LegacyDatReader? legacySource,
        IList<string>? warnings) {
        int missingCells = 0;
        int missingFromLegacy = 0;
        HashSet<uint>? legacyCellIds = legacySource != null
            ? legacySource.GetAllCellFileIds()
            : null;

        foreach (var area in charGen.StartingAreas) {
            foreach (var location in area.Locations) {
                if (location.CellId == 0) {
                    continue;
                }

                if (legacyCellIds != null && !legacyCellIds.Contains(location.CellId)) {
                    missingFromLegacy++;
                }

                if (worldClosure != null && !worldClosure.Contains("EnvCell", location.CellId)) {
                    missingCells++;
                }
            }
        }

        if (missingFromLegacy > 0) {
            warnings?.Add(
                $"CharGen: {missingFromLegacy} starting-location cell id(s) are not present in legacy cell.dat.");
        }

        if (missingCells > 0) {
            warnings?.Add($"CharGen: {missingCells} starting-location cell id(s) were not found in the converted world closure.");
        }
    }

    private static IReadOnlyList<uint> ReadLegacyHeritageSetupReferences(byte[] bytes, ref DatBinReader reader) {
        var refs = new List<uint>();
        while (reader.Offset + 4 <= bytes.Length) {
            uint value = BitConverter.ToUInt32(bytes, reader.Offset);
            if ((value & 0xFF000000u) != LegacyHeritageSetupPrefix) {
                break;
            }

            refs.Add(reader.ReadUInt32());
        }

        return refs;
    }

    private static List<StartingArea> ReadLegacyStartingAreas(byte[] bytes, int areaSectionStart) {
        var dmTrail = ReadLegacyStartingAreasDmTrail(bytes, areaSectionStart);
        var flexible = ReadLegacyStartingAreasFlexible(bytes, areaSectionStart);
        var padded = ReadLegacyStartingAreasClassic(bytes, areaSectionStart);

        int dmScore = CountPlausibleAreas(dmTrail);
        int bestOther = Math.Max(CountPlausibleAreas(padded), CountPlausibleAreas(flexible));
        if (dmTrail.Count >= 5 && dmScore >= bestOther) {
            return dmTrail;
        }

        return BestLegacyStartingAreaDecode(dmTrail, padded, flexible);
    }

    private static List<StartingArea> BestLegacyStartingAreaDecode(params List<StartingArea>[] candidates) {
        List<StartingArea>? best = null;
        int bestScore = -1;
        foreach (var candidate in candidates) {
            int score = CountPlausibleAreas(candidate);
            if (score > bestScore) {
                bestScore = score;
                best = candidate;
            }
        }

        return best ?? [];
    }

    private static int CountPlausibleAreas(IReadOnlyList<StartingArea> areas) {
        int count = 0;
        foreach (var area in areas) {
            if (string.IsNullOrWhiteSpace(area.Name)) {
                continue;
            }

            if (area.Locations.Any(location => IsPlausibleSpawnCellId(location.CellId))) {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Original DM CharGen area trail: heritage offset, optional 0x12 prefix, length-prefixed names with align(4).
    /// </summary>
    private static List<StartingArea> ReadLegacyStartingAreasDmTrail(byte[] bytes, int areaSectionStart) {
        var reader = new DatBinReader(bytes);
        if (areaSectionStart > 0) {
            reader.ReadBytes(areaSectionStart);
        }

        return ReadLegacyStartingAreasDmTrail(reader);
    }

    private static List<StartingArea> ReadLegacyStartingAreasDmTrail(DatBinReader reader) {
        var areas = new List<StartingArea>();
        if (reader.Offset + 8 >= reader.Length) {
            return areas;
        }

        // Dark Majesty prefixes the first starter-area record with an extra uint32 (observed 0x12).
        reader.ReadUInt32();

        while (reader.Offset + 8 < reader.Length) {
            uint nameLength = reader.ReadUInt32();
            if (nameLength == 0 || nameLength > 256 || reader.Offset + nameLength > reader.Length) {
                break;
            }

            string name = Encoding.ASCII.GetString(reader.ReadBytes((int)nameLength));
            reader.Align(4);

            if (reader.Offset + 4 > reader.Length) {
                break;
            }

            uint locationCount = reader.ReadUInt32();
            var area = new StartingArea {
                Name = name,
            };

            for (int i = 0; i < locationCount; i++) {
                if (reader.Offset + 32 > reader.Length) {
                    break;
                }

                area.Locations.Add(ReadLegacyPositionClassic(reader));
            }

            areas.Add(area);
        }

        return areas;
    }

    private static List<StartingArea> ReadLegacyStartingAreasFlexible(byte[] bytes, int areaSectionStart) {
        var areas = new List<StartingArea>();
        if (areaSectionStart + 8 >= bytes.Length) {
            return areas;
        }

        int offset = areaSectionStart;
        if (offset + 4 <= bytes.Length && BitConverter.ToUInt32(bytes, offset) == 0x12) {
            offset += 4;
        }

        offset = FindFirstStartingAreaOffset(bytes, offset);
        while (offset + 8 < bytes.Length) {
            if (!TryValidateNameRecordAt(bytes, offset, out uint nameLength, out int nameOffset)) {
                break;
            }

            offset += 4;
            string name = Encoding.ASCII.GetString(bytes, nameOffset, (int)nameLength);
            offset = nameOffset + (int)nameLength;
            offset = FindLocationCountOffset(bytes, offset);

            if (offset + 4 > bytes.Length) {
                break;
            }

            uint locationCount = BitConverter.ToUInt32(bytes, offset);
            if (locationCount == 0 || locationCount > 512) {
                break;
            }

            offset += 4;
            var area = new StartingArea {
                Name = name,
            };

            for (int i = 0; i < locationCount; i++) {
                if (offset + 32 > bytes.Length) {
                    break;
                }

                area.Locations.Add(ReadLegacyPosition(bytes, ref offset));
            }

            areas.Add(area);

            int resumeAt = offset;
            int nextArea = FindFirstStartingAreaOffset(bytes, resumeAt);
            if (nextArea <= resumeAt) {
                break;
            }

            offset = nextArea;
        }

        return areas;
    }

    private static List<StartingArea> ReadLegacyStartingAreasClassic(byte[] bytes, int areaSectionStart) {
        var areas = new List<StartingArea>();
        if (areaSectionStart + 8 >= bytes.Length) {
            return areas;
        }

        var reader = new DatBinReader(bytes);
        if (areaSectionStart > 0) {
            reader.ReadBytes(areaSectionStart);
        }

        if (reader.Offset + 8 >= reader.Length) {
            return areas;
        }

        if (reader.Offset + 4 <= bytes.Length
            && BitConverter.ToUInt32(bytes, reader.Offset) == 0x12) {
            reader.ReadUInt32();
        }

        int areaOffset = FindFirstStartingAreaOffset(bytes, reader.Offset);
        if (areaOffset > reader.Offset) {
            reader.ReadBytes(areaOffset - reader.Offset);
        }

        while (reader.Offset + 8 < reader.Length) {
            if (!TryValidateNameRecordAt(bytes, reader.Offset, out uint nameLength, out int nameOffset)) {
                break;
            }

            reader.ReadBytes(4);
            string name = Encoding.ASCII.GetString(bytes, nameOffset, (int)nameLength);
            int afterName = nameOffset + (int)nameLength;
            int locationCountOffset = FindLocationCountOffset(bytes, afterName);
            if (locationCountOffset > reader.Offset) {
                reader.ReadBytes(locationCountOffset - reader.Offset);
            }

            if (reader.Offset + 4 > reader.Length) {
                break;
            }

            uint locationCount = reader.ReadUInt32();
            if (locationCount == 0 || locationCount > 512) {
                break;
            }

            var area = new StartingArea {
                Name = name,
            };

            for (int i = 0; i < locationCount; i++) {
                if (reader.Offset + 32 > reader.Length) {
                    break;
                }

                area.Locations.Add(ReadLegacyPositionClassic(reader));
            }

            areas.Add(area);

            int resumeAt = reader.Offset;
            int nextArea = FindFirstStartingAreaOffset(bytes, resumeAt);
            if (nextArea <= resumeAt) {
                break;
            }

            if (nextArea > reader.Offset) {
                reader.ReadBytes(nextArea - reader.Offset);
            }

            continue;
        }

        return areas;
    }

    private static Position ReadLegacyPositionClassic(DatBinReader reader) {
        uint cellId = reader.ReadUInt32();
        var origin = new Vector3(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());

        float qw = reader.ReadSingle();
        float qx = reader.ReadSingle();
        float qy = reader.ReadSingle();
        float qz = reader.ReadSingle();

        return new Position {
            CellId = cellId,
            Frame = new Frame {
                Origin = origin,
                Orientation = new Quaternion(qx, qy, qz, qw),
            },
        };
    }

    private static int FindLocationCountOffset(byte[] bytes, int afterNameOffset) {
        int scanEnd = Math.Min(bytes.Length, afterNameOffset + 32);
        for (int candidate = afterNameOffset; candidate + 4 <= scanEnd; candidate++) {
            uint locationCount = BitConverter.ToUInt32(bytes, candidate);
            if (locationCount is 0 or > 512) {
                continue;
            }

            if (candidate + 4 + 32 > bytes.Length) {
                continue;
            }

            return candidate;
        }

        return (afterNameOffset + 3) & ~3;
    }

    private static int FindFirstStartingAreaOffset(byte[] bytes, int areaSectionStart) {
        int scanEnd = Math.Min(bytes.Length, areaSectionStart + 128);
        for (int offset = areaSectionStart; offset + 8 < scanEnd; offset++) {
            if (TryValidateNameRecordAt(bytes, offset, out _, out _)) {
                return offset;
            }
        }

        return areaSectionStart;
    }

    private static bool TryValidateNameRecordAt(
        byte[] bytes,
        int offset,
        out uint nameLength,
        out int nameOffset) {
        nameOffset = -1;
        if (!TryValidateNameLengthAt(bytes, offset, out nameLength)) {
            return false;
        }

        for (int pad = 0; pad <= 8; pad++) {
            int candidate = offset + 4 + pad;
            if (candidate + nameLength > bytes.Length) {
                break;
            }

            if (IsAsciiNameStart(bytes[candidate])) {
                nameOffset = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsAsciiNameStart(byte value) =>
        (value >= (byte)'A' && value <= (byte)'Z')
        || (value >= (byte)'a' && value <= (byte)'z')
        || value == (byte)'-';

    private static bool TryValidateNameLengthAt(byte[] bytes, int offset, out uint nameLength) {
        nameLength = 0;
        if (offset + 8 > bytes.Length) {
            return false;
        }

        nameLength = BitConverter.ToUInt32(bytes, offset);
        if (nameLength is < 3 or > 64) {
            return false;
        }

        return offset + 4 + nameLength <= bytes.Length;
    }

    private static StartingArea CloneStartingArea(StartingArea source) {
        var clone = new StartingArea {
            Name = source.Name,
        };

        foreach (var location in source.Locations) {
            clone.Locations.Add(new Position {
                CellId = location.CellId,
                Frame = new Frame {
                    Origin = location.Frame.Origin,
                    Orientation = location.Frame.Orientation,
                },
            });
        }

        return clone;
    }

    private static List<(uint HeritageId, HeritageGroupCG Group)> LoadRetailHeritageGroups(
        string? retailSeedDirectory,
        DefaultDatReaderWriter outputWriter,
        IList<string>? warnings) {
        CharGen? retailCharGen = null;

        // Prefer the pristine retail seed: the export's own CharGen may already have been replaced
        // by a previous DM CharGen write (repair / re-run), which would feed incomplete groups back
        // into the clone path.
        if (!string.IsNullOrWhiteSpace(retailSeedDirectory) && Directory.Exists(retailSeedDirectory)) {
            using var seedReader = new DefaultDatReaderWriter(
                retailSeedDirectory,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            seedReader.Portal().TryGet(CharGenId, out retailCharGen);
        }

        if (retailCharGen?.HeritageGroups == null || retailCharGen.HeritageGroups.Count == 0) {
            if (outputWriter.Portal().TryGet(CharGenId, out retailCharGen) != true) {
                retailCharGen = null;
            }
        }

        if (retailCharGen?.HeritageGroups == null || retailCharGen.HeritageGroups.Count == 0) {
            warnings?.Add("CharGen: retail heritage template unavailable; DM 0x31 setup refs were kept.");
            return [];
        }

        // A heritage group is only usable as a clone template when char-create can actually walk it.
        return retailCharGen.HeritageGroups
            .Where(pair => pair.Value.Genders != null && pair.Value.Genders.Count > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => (pair.Key, pair.Value))
            .ToList();
    }

    private static Position ReadLegacyPosition(byte[] bytes, ref int offset) {
        uint cellId = BitConverter.ToUInt32(bytes, offset);
        offset += 4;
        var origin = new Vector3(
            BitConverter.ToSingle(bytes, offset),
            BitConverter.ToSingle(bytes, offset + 4),
            BitConverter.ToSingle(bytes, offset + 8));
        offset += 12;

        float qw = BitConverter.ToSingle(bytes, offset);
        float qx = BitConverter.ToSingle(bytes, offset + 4);
        float qy = BitConverter.ToSingle(bytes, offset + 8);
        float qz = BitConverter.ToSingle(bytes, offset + 12);
        offset += 16;

        return new Position {
            CellId = cellId,
            Frame = new Frame {
                Origin = origin,
                Orientation = new Quaternion(qx, qy, qz, qw),
            },
        };
    }
}
