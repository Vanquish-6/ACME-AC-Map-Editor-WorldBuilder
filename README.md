# ACME WorldBuilder

ACME WorldBuilder is a desktop editor for Asheron's Call client data. Paint terrain, build dungeons, edit spells and weenies, then export updated `client_*.dat` files.

This is an ACME project, licensed under the [MIT License](LICENSE).

Source and releases: https://github.com/Vanquish-6/ACME-AC-Map-Editor-WorldBuilder

## What it does

- **Retail DAT projects**: full editor workflow with export to `client_cell_1.dat`, `client_portal.dat`, `client_highres.dat`, and `client_local_English.dat`.
- **Legacy pre-ToD DAT projects**: read-only viewing of older `cell.dat` / `portal.dat` layouts, with an optional conversion path to retail-format client DATs.

## Editors

| Area | Tools |
|------|--------|
| Scenes | World (landscape), Dungeon |
| Content | Spells, Weenies, Monsters, Skills, Spell Sets, Character creation, XP table, Vitals |
| Inspect | UI Layout, Objects |

Some content tools can connect to a MySQL **world database** (weenies, creatures, and export helpers). Configure host, port, database, user, and password under **File → Settings**.

## Requirements

- Windows 10 or 11 (primary). macOS and Linux projects exist for source builds.
- .NET 8 SDK to build, or the .NET 8 runtime to run an installer build (the installer can prompt to install it).
- A copy of the game DAT folder you are allowed to modify. Always work on a copy.
- The native DAT library `native/acme_dat.dll` (in this repo, copied next to the exe, and included in the installer).

## First launch

The first startup can take longer than later runs while caches are built (textures, thumbnails, terrain).

Projects live under `Documents/ACME WorldBuilder/Projects` by default. Creating a project stores a `.wbproj` and SQLite data; it does not overwrite your DATs until you export.

## Documentation

See [docs/USER_GUIDE.md](docs/USER_GUIDE.md) for install, editors, export, and backup notes.

## Building from source

```powershell
dotnet build WorldBuilder.Windows/WorldBuilder.Windows.csproj
dotnet run --project WorldBuilder.Windows/WorldBuilder.Windows.csproj
```

`native/acme_dat.dll` ships in this repo. Building Windows copies it next to the exe and into the installer publish folder.

## Releases

Windows installers are published on GitHub Releases and include `acme_dat.dll`. In-app updates use GitHub Pages (`appcast.xml`). If you fork the repo, set `GitHubOwner` and `GitHubRepo` in `WorldBuilder/Lib/AppReleaseInfo.cs` and enable Pages.
