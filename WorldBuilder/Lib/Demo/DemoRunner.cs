using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using WorldBuilder.Editors.Dungeon;
using WorldBuilder.Editors.Landscape.ViewModels;
using WorldBuilder.Lib;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Lib.Demo;

/// <summary>
/// Runs the promo demo script: teleporting, tool changes, dungeon copy, texture/export, etc.
/// All steps that touch the UI run via the dispatcher.
/// </summary>
public static class DemoRunner {
    /// <summary>
    /// Default delay in seconds after a step (when not specified by the step).
    /// </summary>
    public const double DefaultStepDelaySeconds = 2.5;

    /// <summary>
    /// Builds the promo script: destroy Yaraq, Arwic terrain, random landblocks + houses/city, ocean island, dungeon template + textures.
    /// Uses Location list for Yaraq, Arwic, Red Rat Lair; ocean uses fallback landblock if no "Ocean" in Locations.
    /// </summary>
    public static List<DemoStep> BuildDefaultScript() {
        return new List<DemoStep> {
            // --- Destroy Yaraq (use location list, then show height lower tool) ---
            new(DemoStepKind.NavigateToLocationName, LocationName: "Yaraq", TypeFilter: "Town"),
            new(DemoStepKind.Wait, Seconds: 2.5),
            new(DemoStepKind.SelectTool, ToolIndex: 3, SubToolIndex: 0), // Height Raise/Lower – "destroy" by lowering
            new(DemoStepKind.Wait, Seconds: 3),
            // --- Teleport to Arwic, make it different terrain (texture paint) ---
            new(DemoStepKind.NavigateToLocationName, LocationName: "Arwic", TypeFilter: "Town"),
            new(DemoStepKind.Wait, Seconds: 2.5),
            new(DemoStepKind.SelectTool, ToolIndex: 1, SubToolIndex: 0), // Texture painting Brush (UpdateLeftPanel shows Texture Palette)
            new(DemoStepKind.ShowPanel, PanelId: "TexturePalette"),
            new(DemoStepKind.Wait, Seconds: 3),
            // --- Random landblock: add houses / static objects ---
            new(DemoStepKind.NavigateToRandomLandblock, TypeFilter: "Town"),
            new(DemoStepKind.Wait, Seconds: 2),
            new(DemoStepKind.SelectTool, ToolIndex: 0, SubToolIndex: 0), // Selector (UpdateLeftPanel shows Object Browser)
            new(DemoStepKind.ShowPanel, PanelId: "ObjectBrowser"),
            new(DemoStepKind.Wait, Seconds: 4),
            // --- Another random landblock: make a city (more buildings) ---
            new(DemoStepKind.NavigateToRandomLandblock),
            new(DemoStepKind.Wait, Seconds: 2),
            new(DemoStepKind.ShowPanel, PanelId: "ObjectBrowser"),
            new(DemoStepKind.Wait, Seconds: 4),
            // --- Ocean: make an island (height raise). No "Ocean" in Locations by default; use landblock 0x0080 as water placeholder – replace if you have one. ---
            new(DemoStepKind.NavigateToLandblock, LandblockId: 0x0080),
            new(DemoStepKind.Wait, Seconds: 2),
            new(DemoStepKind.SelectTool, ToolIndex: 3, SubToolIndex: 0), // Height Raise/Lower
            new(DemoStepKind.Wait, Seconds: 3),
            // --- Dungeon editor: Red Rat Lair at AAAA, then change wall/floor textures ---
            new(DemoStepKind.SwitchToDungeon),
            new(DemoStepKind.Wait, Seconds: 2),
            new(DemoStepKind.CopyDungeonFromTemplateByName, TemplateName: "A Red Rat Lair", TargetHex: "AAAA"),
            new(DemoStepKind.Wait, Seconds: 3),
            new(DemoStepKind.ApplyDungeonSurfacesToAllCells, WallSurfaceId: 0x032A, FloorSurfaceId: 0x032B),
            new(DemoStepKind.Wait, Seconds: 3),
            new(DemoStepKind.SwitchToLandscape),
            new(DemoStepKind.Wait, Seconds: 1),
            new(DemoStepKind.OpenExportDats),
        };
    }

    /// <summary>
    /// Runs the given script. Call from the UI thread or from a fire-and-forget task after the main window is shown.
    /// Waits for the landscape editor to be ready (Tools populated) before running steps that depend on it.
    /// </summary>
    public static async Task RunAsync(ProjectManager projectManager, IReadOnlyList<DemoStep> steps) {
        if (steps.Count == 0) return;

        // Wait for landscape editor view to load and Init() to run (Tools populated)
        await WaitForLandscapeEditorReadyAsync(projectManager);

        for (int i = 0; i < steps.Count; i++) {
            var step = steps[i];
            try {
                await ExecuteStepAsync(projectManager, step);
                // Extra delay after step (skip for Wait - already delayed inside)
                if (step.Kind != DemoStepKind.Wait) {
                    double delay = step.Seconds ?? DefaultStepDelaySeconds;
                    if (delay > 0)
                        await Task.Delay(TimeSpan.FromSeconds(delay));
                }
            }
            catch (Exception ex) {
                System.Console.WriteLine($"[Demo] Step {i + 1} ({step.Kind}): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Polls on the UI thread until the landscape editor has tools (Init has run), or timeout.
    /// </summary>
    private static async Task WaitForLandscapeEditorReadyAsync(ProjectManager projectManager) {
        const int maxAttempts = 50;  // 5 seconds at 100ms
        for (int i = 0; i < maxAttempts; i++) {
            var ready = await Dispatcher.UIThread.InvokeAsync(() => {
                var editor = projectManager.GetProjectService<LandscapeEditorViewModel>();
                return editor != null && editor.Tools.Count > 0;
            });
            if (ready) return;
            await Task.Delay(100);
        }
        System.Console.WriteLine("[Demo] Landscape editor not ready after 5s; continuing anyway.");
    }

    private static async Task ExecuteStepAsync(ProjectManager projectManager, DemoStep step) {
        switch (step.Kind) {
            case DemoStepKind.Wait:
                if (step.Seconds.HasValue && step.Seconds.Value > 0)
                    await Task.Delay(TimeSpan.FromSeconds(step.Seconds.Value));
                return;

            case DemoStepKind.NavigateToLandblock:
                if (!step.LandblockId.HasValue) return;
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var editor = projectManager.GetProjectService<LandscapeEditorViewModel>();
                    editor?.NavigateToLandblock((ushort)step.LandblockId.Value);
                });
                return;

            case DemoStepKind.NavigateToLocationName:
                if (string.IsNullOrEmpty(step.LocationName)) return;
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var entry = LocationDatabase.GetFirstByName(step.LocationName!, step.TypeFilter);
                    if (entry == null) return;
                    var editor = projectManager.GetProjectService<LandscapeEditorViewModel>();
                    editor?.NavigateToLandblock(entry.LandblockId);
                });
                return;

            case DemoStepKind.NavigateToRandomLandblock:
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var entry = LocationDatabase.GetRandomLocation(step.TypeFilter);
                    if (entry == null) return;
                    var editor = projectManager.GetProjectService<LandscapeEditorViewModel>();
                    editor?.NavigateToLandblock(entry.LandblockId);
                });
                return;

            case DemoStepKind.SelectTool:
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var editor = projectManager.GetProjectService<LandscapeEditorViewModel>();
                    if (editor?.Tools.Count == 0) return;
                    int ti = step.ToolIndex ?? 0;
                    if (ti < 0 || ti >= editor!.Tools.Count) return;
                    var tool = editor.Tools[ti];
                    editor.SelectToolCommand.Execute(tool);
                    if (step.SubToolIndex.HasValue && tool.AllSubTools.Count > 0) {
                        int si = Math.Clamp(step.SubToolIndex.Value, 0, tool.AllSubTools.Count - 1);
                        editor.SelectSubToolCommand.Execute(tool.AllSubTools[si]);
                    }
                });
                return;

            case DemoStepKind.SwitchToLandscape:
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var main = projectManager.GetProjectService<MainViewModel>();
                    main?.SwitchToLandscapeEditorCommand.Execute(null);
                });
                return;

            case DemoStepKind.SwitchToDungeon:
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var main = projectManager.GetProjectService<MainViewModel>();
                    main?.SwitchToDungeonEditorCommand.Execute(null);
                });
                return;

            case DemoStepKind.CopyDungeon:
                if (!step.SourceLb.HasValue || !step.TargetLb.HasValue) return;
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var dungeon = projectManager.GetProjectService<DungeonEditorViewModel>();
                    dungeon?.CopyDungeonTemplate((ushort)step.SourceLb.Value, (ushort)step.TargetLb.Value);
                });
                return;

            case DemoStepKind.CopyDungeonFromTemplateByName:
                if (string.IsNullOrEmpty(step.TemplateName) || string.IsNullOrEmpty(step.TargetHex)) return;
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var template = LocationDatabase.GetFirstByName(step.TemplateName!, "Dungeon");
                    if (template == null) return;
                    var hex = step.TargetHex!.Trim().TrimStart('0', 'x', 'X');
                    if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var targetLb))
                        return;
                    var dungeon = projectManager.GetProjectService<DungeonEditorViewModel>();
                    dungeon?.CopyDungeonTemplate(template.LandblockId, targetLb);
                });
                return;

            case DemoStepKind.ApplyDungeonSurfacesToAllCells:
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var dungeon = projectManager.GetProjectService<DungeonEditorViewModel>();
                    if (dungeon == null) return;
                    ushort? w = step.WallSurfaceId.HasValue ? (ushort)step.WallSurfaceId.Value : null;
                    ushort? f = step.FloorSurfaceId.HasValue ? (ushort)step.FloorSurfaceId.Value : null;
                    dungeon.ApplyAlternateSurfacesToAllCells(w, f);
                });
                return;

            case DemoStepKind.TogglePanel:
                if (string.IsNullOrEmpty(step.PanelId)) return;
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var editor = projectManager.GetProjectService<LandscapeEditorViewModel>();
                    editor?.DockingManager.TogglePanelVisibility(step.PanelId!);
                });
                return;

            case DemoStepKind.ShowPanel:
                if (string.IsNullOrEmpty(step.PanelId)) return;
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var editor = projectManager.GetProjectService<LandscapeEditorViewModel>();
                    var panel = editor?.DockingManager.AllPanels.FirstOrDefault(p => p.Id == step.PanelId!);
                    if (panel != null && !panel.IsVisible)
                        panel.IsVisible = true;
                });
                return;

            case DemoStepKind.OpenExportDats:
                await Dispatcher.UIThread.InvokeAsync(async () => {
                    var main = projectManager.GetProjectService<MainViewModel>();
                    if (main != null)
                        await main.OpenExportDatsWindowCommand.ExecuteAsync(null);
                });
                return;

            case DemoStepKind.GoToBookmark:
                if (!step.BookmarkIndex.HasValue) return;
                await Dispatcher.UIThread.InvokeAsync(() => {
                    var editor = projectManager.GetProjectService<LandscapeEditorViewModel>();
                    var bookmarks = editor?.BookmarksPanel as CameraBookmarksPanelViewModel;
                    if (bookmarks?.Bookmarks.Count == 0) return;
                    int idx = Math.Clamp(step.BookmarkIndex!.Value, 0, bookmarks!.Bookmarks.Count - 1);
                    var item = bookmarks.Bookmarks[idx];
                    bookmarks.GoToBookmarkCommand.Execute(item);
                });
                return;
        }
    }
}
