namespace WorldBuilder.Shared.Lib.AceDb {

    /// <summary>Which portal DAT index list to show when picking a PropertyDataId value.</summary>
    public enum WeenieDidPickerListKind {
        /// <summary>User chooses list type (tabs).</summary>
        Mixed,
        Setup,
        GfxObj,
        RenderSurface,
        Surface,
    }

    /// <summary>Maps PropertyDataId to a sensible default DAT picker list.</summary>
    public static class AceWeenieDidPickerCatalog {
        public static WeenieDidPickerListKind DefaultListKind(ushort propertyDataId) {
            if (!System.Enum.IsDefined(typeof(AcePropertyDataId), propertyDataId))
                return WeenieDidPickerListKind.Mixed;
            return ((AcePropertyDataId)propertyDataId) switch {
                AcePropertyDataId.Setup or AcePropertyDataId.HeadObject => WeenieDidPickerListKind.Setup,
                AcePropertyDataId.Icon or AcePropertyDataId.IconOverlay or AcePropertyDataId.IconOverlaySecondary
                    or AcePropertyDataId.IconUnderlay or AcePropertyDataId.EyesTexture or AcePropertyDataId.NoseTexture
                    or AcePropertyDataId.MouthTexture or AcePropertyDataId.DefaultEyesTexture
                    or AcePropertyDataId.DefaultNoseTexture or AcePropertyDataId.DefaultMouthTexture
                    or AcePropertyDataId.HairPalette or AcePropertyDataId.EyesPalette or AcePropertyDataId.SkinPalette
                    => WeenieDidPickerListKind.RenderSurface,
                AcePropertyDataId.PaletteBase or AcePropertyDataId.ClothingBase or AcePropertyDataId.QualityFilter
                    or AcePropertyDataId.MutateFilter or AcePropertyDataId.CreationMutationFilter
                    or AcePropertyDataId.TsysMutationFilter or AcePropertyDataId.AugmentationMutationFilter
                    => WeenieDidPickerListKind.Surface,
                _ => WeenieDidPickerListKind.Mixed,
            };
        }
    }
}
