using System;
using System.Collections.Generic;
using System.Globalization;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib.AceDb {

    /// <summary>Optional enum pick-lists for PropertyInt fields.</summary>
    public static class AceWeenieIntEnumOptions {

        public static IReadOnlyList<(int Value, string Name)>? TryGetChoices(ushort propertyIntType) {
            if (!Enum.IsDefined(typeof(AcePropertyInt), propertyIntType))
                return null;
            return ((AcePropertyInt)propertyIntType) switch {
                AcePropertyInt.ItemType or AcePropertyInt.TargetType or AcePropertyInt.HookItemType
                    or AcePropertyInt.MerchandiseItemTypes => List<ItemType>(),
                AcePropertyInt.CreatureType or AcePropertyInt.SlayerCreatureType or AcePropertyInt.FoeType
                    or AcePropertyInt.FriendType => List<CreatureType>(),
                AcePropertyInt.DamageType or AcePropertyInt.FullDamageType or AcePropertyInt.ResistanceModifierType
                    => List<DamageType>(),
                AcePropertyInt.AttackType => List<AttackType>(),
                AcePropertyInt.WeaponSkill or AcePropertyInt.WieldSkillType or AcePropertyInt.WieldSkillType2
                    or AcePropertyInt.WieldSkillType3 or AcePropertyInt.WieldSkillType4
                    or AcePropertyInt.AppraisalItemSkill or AcePropertyInt.SkillToBeAltered
                    or AcePropertyInt.UseRequiresSkill or AcePropertyInt.UseRequiresSkillSpec => List<Skill>(),
                AcePropertyInt.PhysicsState => List<PhysicsState>(),
                AcePropertyInt.ItemUseable => List<Usable>(),
                AcePropertyInt.MaterialType => List<MaterialType>(),
                AcePropertyInt.WieldRequirements or AcePropertyInt.WieldRequirements2 or AcePropertyInt.WieldRequirements3
                    or AcePropertyInt.WieldRequirements4 => List<WieldRequirement>(),
                AcePropertyInt.CombatUse => List<CombatUse>(),
                AcePropertyInt.DefaultCombatStyle or AcePropertyInt.AiAllowedCombatStyle => List<CombatStyle>(),
                AcePropertyInt.PlayerKillerStatus => List<PlayerKillerStatus>(),
                AcePropertyInt.Gender or AcePropertyInt.HeritageSpecificArmor => List<HeritageGroup>(),
                AcePropertyInt.PortalBitmask => List<PortalBitmask>(),
                AcePropertyInt.PaletteTemplate => List<PaletteTemplate>(),
                AcePropertyInt.ValidLocations or AcePropertyInt.CurrentWieldedLocation => List<EquipMask>(),
                AcePropertyInt.ImbuedEffect or AcePropertyInt.ImbuedEffect2 or AcePropertyInt.ImbuedEffect3
                    or AcePropertyInt.ImbuedEffect4 or AcePropertyInt.ImbuedEffect5 => List<ImbuedEffectType>(),
                AcePropertyInt.WeaponType => List<WeaponType>(),
                AcePropertyInt.Bonded => List<BondedStatus>(),
                AcePropertyInt.Attuned => List<AttunedStatus>(),
                AcePropertyInt.CombatMode => List<CombatMode>(),
                _ => null,
            };
        }

        static List<(int Value, string Name)> List<TEnum>() where TEnum : struct, Enum {
            var r = new List<(int Value, string Name)>();
            foreach (var e in Enum.GetValues<TEnum>()) {
                var v = Convert.ToInt32(e, CultureInfo.InvariantCulture);
                r.Add((v, e.ToString() ?? ""));
            }
            r.Sort((a, b) => a.Value.CompareTo(b.Value));
            return r;
        }
    }
}
