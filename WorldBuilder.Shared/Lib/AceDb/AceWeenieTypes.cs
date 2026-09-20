// WeenieType / PropertyDataId names used for editor display.

namespace WorldBuilder.Shared.Lib.AceDb {

    /// <summary>weenie.class_Id / weenie.type discriminator.</summary>
    public enum AceWeenieType : uint {
        Undef,
        Generic,
        Clothing,
        MissileLauncher,
        Missile,
        Ammunition,
        MeleeWeapon,
        Portal,
        Book,
        Coin,
        Creature,
        Admin,
        Vendor,
        HotSpot,
        Corpse,
        Cow,
        AI,
        Machine,
        Food,
        Door,
        Chest,
        Container,
        Key,
        Lockpick,
        PressurePlate,
        LifeStone,
        Switch,
        PKModifier,
        Healer,
        LightSource,
        Allegiance,
        UnknownGues32,
        SpellComponent,
        ProjectileSpell,
        Scroll,
        Caster,
        Channel,
        ManaStone,
        Gem,
        AdvocateFane,
        AdvocateItem,
        Sentinel,
        GSpellEconomy,
        LSpellEconomy,
        CraftTool,
        LScoreKeeper,
        GScoreKeeper,
        GScoreGatherer,
        ScoreBook,
        EventCoordinator,
        Entity,
        Stackable,
        HUD,
        House,
        Deed,
        SlumLord,
        Hook,
        Storage,
        BootSpot,
        HousePortal,
        Game,
        GamePiece,
        SkillAlterationDevice,
        AttributeTransferDevice,
        Hooker,
        AllegianceBindstone,
        InGameStatKeeper,
        AugmentationDevice,
        SocialManager,
        Pet,
        PetDevice,
        CombatPet,
    }

    /// <summary>Subset of PropertyDataId used for labels in the weenie editor.</summary>
    public enum AcePropertyDataId : ushort {
        Undef = 0,
        Setup = 1,
        MotionTable = 2,
        SoundTable = 3,
        CombatTable = 4,
        QualityFilter = 5,
        PaletteBase = 6,
        ClothingBase = 7,
        Icon = 8,
        EyesTexture = 9,
        NoseTexture = 10,
        MouthTexture = 11,
        DefaultEyesTexture = 12,
        DefaultNoseTexture = 13,
        DefaultMouthTexture = 14,
        HairPalette = 15,
        EyesPalette = 16,
        SkinPalette = 17,
        HeadObject = 18,
        ActivationAnimation = 19,
        InitMotion = 20,
        ActivationSound = 21,
        PhysicsEffectTable = 22,
        UseSound = 23,
        UseTargetAnimation = 24,
        UseTargetSuccessAnimation = 25,
        UseTargetFailureAnimation = 26,
        UseUserAnimation = 27,
        Spell = 28,
        SpellComponent = 29,
        PhysicsScript = 30,
        LinkedPortalOne = 31,
        WieldedTreasureType = 32,
        InventoryTreasureType = 33,
        ShopTreasureType = 34,
        DeathTreasureType = 35,
        MutateFilter = 36,
        ItemSkillLimit = 37,
        UseCreateItem = 38,
        DeathSpell = 39,
        VendorsClassId = 40,
        ItemSpecializedOnly = 41,
        HouseId = 42,
        AccountHouseId = 43,
        RestrictionEffect = 44,
        CreationMutationFilter = 45,
        TsysMutationFilter = 46,
        LastPortal = 47,
        LinkedPortalTwo = 48,
        OriginalPortal = 49,
        IconOverlay = 50,
        IconOverlaySecondary = 51,
        IconUnderlay = 52,
        AugmentationMutationFilter = 53,
        AugmentationEffect = 54,
        ProcSpell = 55,
        AugmentationCreateItem = 56,
        AlternateCurrency = 57,
        BlueSurgeSpell = 58,
        YellowSurgeSpell = 59,
        RedSurgeSpell = 60,
        OlthoiDeathTreasureType = 61,
    }

    public static class AceWeeniePropertyLabels {
        public static string DataId(ushort type) =>
            System.Enum.IsDefined(typeof(AcePropertyDataId), type)
                ? ((AcePropertyDataId)type).ToString()
                : $"DID {type}";

        public static string WeenieType(uint type) =>
            System.Enum.IsDefined(typeof(AceWeenieType), type)
                ? ((AceWeenieType)type).ToString()
                : $"Type {type}";

        /// <summary>PropertyInt name from <see cref="AcePropertyInt"/>; unknown ids shown as PropertyInt N.</summary>
        public static string Int(ushort type) =>
            System.Enum.IsDefined(typeof(AcePropertyInt), type)
                ? ((AcePropertyInt)type).ToString()
                : $"PropertyInt {type}";

        public static string Int64(ushort type) =>
            System.Enum.IsDefined(typeof(AcePropertyInt64), type)
                ? ((AcePropertyInt64)type).ToString()
                : $"PropertyInt64 {type}";

        public static string Bool(ushort type) =>
            System.Enum.IsDefined(typeof(AcePropertyBool), type)
                ? ((AcePropertyBool)type).ToString()
                : $"PropertyBool {type}";

        public static string Float(ushort type) =>
            System.Enum.IsDefined(typeof(AcePropertyFloat), type)
                ? ((AcePropertyFloat)type).ToString()
                : $"PropertyFloat {type}";

        public static string String(ushort type) =>
            System.Enum.IsDefined(typeof(AcePropertyString), type)
                ? ((AcePropertyString)type).ToString()
                : $"PropertyString {type}";

        public static string InstanceId(ushort type) =>
            System.Enum.IsDefined(typeof(AcePropertyInstanceId), type)
                ? ((AcePropertyInstanceId)type).ToString()
                : $"PropertyInstanceId {type}";
    }
}
