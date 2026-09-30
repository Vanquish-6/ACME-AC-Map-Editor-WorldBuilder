namespace WorldBuilder.Shared.Lib.MonsterBuilder;

/// <summary>
/// Editor-only body roles. They are stored in the Monster Builder project, not in DAT records.
/// </summary>
public static class MonsterRoles {
    public const string Head = "head";
    public const string Torso = "torso";
    public const string Pelvis = "pelvis";
    public const string UpperArmL = "upper_arm_l";
    public const string UpperArmR = "upper_arm_r";
    public const string ForearmL = "forearm_l";
    public const string ForearmR = "forearm_r";
    public const string HandL = "hand_l";
    public const string HandR = "hand_r";
    public const string ThighL = "thigh_l";
    public const string ThighR = "thigh_r";
    public const string ShinL = "shin_l";
    public const string ShinR = "shin_r";
    public const string FootL = "foot_l";
    public const string FootR = "foot_r";
    public const string Other = "other";

    public static readonly string[] All = [
        Head, Torso, Pelvis,
        UpperArmL, UpperArmR,
        ForearmL, ForearmR,
        HandL, HandR,
        ThighL, ThighR,
        ShinL, ShinR,
        FootL, FootR,
        Other,
    ];

    public static string DisplayName(string? role) => role switch {
        Head => "Head",
        Torso => "Torso",
        Pelvis => "Pelvis",
        UpperArmL => "Upper Arm L",
        UpperArmR => "Upper Arm R",
        ForearmL => "Forearm L",
        ForearmR => "Forearm R",
        HandL => "Hand L",
        HandR => "Hand R",
        ThighL => "Thigh L",
        ThighR => "Thigh R",
        ShinL => "Shin L",
        ShinR => "Shin R",
        FootL => "Foot L",
        FootR => "Foot R",
        Other => "Other",
        _ => "Unassigned",
    };
}
