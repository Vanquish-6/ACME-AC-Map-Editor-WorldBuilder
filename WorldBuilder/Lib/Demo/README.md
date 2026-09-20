# Promo demo script

When you run the Windows app with `--demo "C:\path\to\your.wbproj"`, it:

1. Auto-opens that project
2. Runs an **advanced script** that demonstrates:
   - **Destroy Yaraq** – Uses the Location list to go to Yaraq (Town), then shows the Height (Raise/Lower) tool.
   - **Arwic, different terrain** – Teleports to Arwic (Town), then Texture painting + Texture Palette.
   - **Random landblock: houses/statics** – Random Town, Object Browser + Selector.
   - **Random landblock: city** – Another random overworld landblock, Object Browser.
   - **Ocean → island** – Landblock `0x0080` (ocean placeholder), then Height Raise to “make an island”. Replace in script if you have an Ocean location or known water LB.
   - **Dungeon: Red Rat Lair at AAAA** – Dungeon editor, copy template “A Red Rat Lair” to `AAAA`, then apply alternate wall/floor surfaces.
   - **Export** – Opens the Export DATs window.

## Customizing the script

- **Location list** – Uses `LocationDatabase` (embedded `Data/Locations.txt`). `NavigateToLocationName("Yaraq", "Town")`, `GetFirstByName("A Red Rat Lair", "Dungeon")`, etc. `NavigateToRandomLandblock(typeFilter)` for random towns or overworld.
- **Ocean** – Script uses landblock `0x0080`. Change in `BuildDefaultScript()` or add `NavigateToLocationName("Ocean")` if you have it in Locations.
- **Dungeon target** – Red Rat Lair is copied to `AAAA`. Change `TargetHex` in `CopyDungeonFromTemplateByName` if needed.
- **Wall/floor surfaces** – `ApplyDungeonSurfacesToAllCells` uses `0x032A` and `0x032B`. Set `WallSurfaceId` and `FloorSurfaceId` for a different look.
- **Steps** – Step kinds: `Wait`, `NavigateToLandblock`, `NavigateToLocationName`, `NavigateToRandomLandblock`, `SelectTool`, `SwitchToLandscape`, `SwitchToDungeon`, `CopyDungeon`, `CopyDungeonFromTemplateByName`, `ApplyDungeonSurfacesToAllCells`, `TogglePanel`, `OpenExportDats`, `GoToBookmark`.

## Tool indices (landscape)

- 0 = Selector (select/move/rotate/clone/paste objects)
- 1 = Texture painting (brush, bucket fill)
- 2 = Road drawing (point, line, remove)
- 3 = Height (raise/lower, set, smooth)

## Panel IDs

- `ObjectBrowser`, `TexturePalette`, `Layers`, `History`, `Bookmarks`, `Toolbox`

## Recording

Start your screen recorder (OBS, etc.), then run:

```bat
WorldBuilder.Windows.exe --demo "C:\Path\To\YourProject.wbproj"
```

The app will load the project and run the script; close the Export DATs window when it opens if you want the script to finish, or leave it open for the “upload” shot.
