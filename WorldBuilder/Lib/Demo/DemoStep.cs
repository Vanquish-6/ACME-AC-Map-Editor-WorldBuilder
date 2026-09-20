namespace WorldBuilder.Lib.Demo;

/// <summary>
/// Kind of step in the promo demo script.
/// </summary>
public enum DemoStepKind {
    Wait,
    NavigateToLandblock,
    NavigateToLocationName,
    NavigateToRandomLandblock,
    SelectTool,
    SwitchToLandscape,
    SwitchToDungeon,
    CopyDungeon,
    CopyDungeonFromTemplateByName,
    ApplyDungeonSurfacesToAllCells,
    TogglePanel,
    ShowPanel,
    OpenExportDats,
    GoToBookmark,
}

/// <summary>
/// A single step in the demo script. Only fields relevant to <see cref="Kind"/> are used.
/// </summary>
public record DemoStep(
    DemoStepKind Kind,
    double? Seconds = null,
    int? LandblockId = null,
    int? ToolIndex = null,
    int? SubToolIndex = null,
    int? SourceLb = null,
    int? TargetLb = null,
    string? PanelId = null,
    int? BookmarkIndex = null,
    string? LocationName = null,
    string? TypeFilter = null,
    string? TemplateName = null,
    string? TargetHex = null,
    int? WallSurfaceId = null,
    int? FloorSurfaceId = null
);
