# CDDA Companion

<img width="1920" height="928" alt="2026-10-20" src="https://github.com/user-attachments/assets/8072426d-3906-451f-aca1-cfb22189e2a2" />

A local companion app for **Cataclysm: Dark Days Ahead 0.I**. It reads your save, runs on your machine,
opens in your browser next to the game, and gives you:

- **The overmap**, drawn like the game draws it: with your overmap tileset (the one set in the game's
  options, or any other in your `gfx` folder) or as ASCII. Fog of war, all z-levels, your map notes,
  city names, your character (`@`) and your base. A **Reveal** toggle shows everything
  generated (spoilers). Tiles follow the game's own rules: looks_like fallbacks, vision levels,
  connected roads, rivers and forests, rotation, and the same random variants per tile.
- **Item search** across the explored world: items (including inside bags, containers and
  vehicle cargo), furniture, terrain, vehicles and parts, traps, fields and spawn points.
  Vehicles are found by prototype id or by name, including ones you built or renamed and placed appliances.
  Results become pins on the map, sorted by distance from your base, from you, or from any tile.
  The result you pick gets corner brackets, a crosshair and a label; when it's off screen or on another
  level, an arrow at the edge points to it.
  With **+ from parts**, it also finds everything that gives the item when disassembled or deconstructed
  (one step: toasters and hot plates for heating elements, kitchen ovens via their appliance item),
  with how many each gives, read from your game's own recipes and furniture data.
- **Category search**: switch Find to **Category** and search a whole inventory category at once (Ammo,
  Magazines, Guns, Food, Drugs, Tools...), sorted the same way the game sorts your inventory. Ammo, magazines
  and guns are grouped by ammo type and by what fires them, and start out as **Firearms only** (cartridges,
  shells, black powder, 40mm grenades, rockets). **Edit filter** lets you tick or untick whole groups, single
  ammo types or single items; your choices are remembered per category. Rounds loaded in magazines and guns
  count too and are marked "loaded". **Summary** gives one row per ammo type for the whole map.
- **Codex**: look up any item, furniture, terrain, vehicle or vehicle part your game and mods define,
  by name or id. Each card shows the basics (weight, volume, material, category, move cost, storage,
  vehicle part lists, flags), what it takes apart into, what gives it, and a **Find in my world**
  button that searches your map for it.
- **Save slots** per world with in-game date labels, plus an automatic **Undo** slot before every load.
- **Your game's colours**: the page is drawn in the game's 16 colours, read from your install. Pick a colour
  theme in the game's colour manager and the companion follows it.

It never writes to your save, except when you load a slot.

**Download:** Windows and Linux builds are on the [Releases](../../releases) page. One file, nothing to install.

## Running it

1. Download the build for your system and put `cdda-companion` (`cdda-companion.exe` on Windows) anywhere.
   In your game folder (or your launcher's folder) it finds everything by itself.
2. Start it (Linux: `chmod +x cdda-companion` once, then `./cdda-companion`). Your browser opens.
3. That's it. It finds the game and opens the world you played last. If it can't, the page asks for a folder:
   paste the folder you start the game from, your launcher's folder, or your save folder, and pick a world.

Change the world, the game folder or anything else later in the **Settings** tab, no restart needed.
Set your base by clicking its tile on the map and choosing **Set as base** (each world has its own).

### Launchers and other installs

The game can keep its files in different places, and the companion follows the game's own rules:

- **Game folder** (the release zips): everything in one folder.
- **[Catapult](https://github.com/qrrk/Catapult) and [Dabdoob](https://github.com/Hihahahalol/Catapult_Dabdoob)**: the game
  in `dda/game0` (and so on), your saves, settings, mods and tilesets in `dda/userdata`. Paste the launcher's folder or any
  folder inside it. The companion uses the install the launcher starts, follows it when the launcher updates the game,
  and keeps its own data in `dda/userdata/cdda-companion-data`, because launchers replace the game folder on updates.
- **Linux packages and home-folder builds**: the game in `/usr/share/cataclysm-dda` (or similar), your files in
  `~/.local/share/cataclysm-dda` (settings in `~/.config/cataclysm-dda`) or `~/.cataclysm-dda`.
- **Windows and macOS user folders**: `%LOCALAPPDATA%\cataclysm-dda`, `~/Library/Application Support/Cataclysm`.

The setup page shows where it found the game, the saves and the game's settings. If your saves are somewhere else,
**Change** next to the save folder lets you point at them.

Made for Cataclysm: DDA only. Forks such as Bright Nights or The Last Generation aren't supported.

### Running and stopping

Only one copy runs at a time. Starting it again just opens the page of the copy that's already
running, and copies from older versions are stopped automatically. To stop it, use the **Quit**
button on the page, Ctrl+C in its window, or `cdda-companion --stop`. It also quits by itself
when no page has been open for 30 minutes, so forgotten copies don't pile up.
A running backup or load always finishes before it quits.

For compressed worlds (the 0.I default), the first start unpacks the explored map once so it can be
searched, which can take a few minutes for a big world. The unpacked copy lives in the data folder and
takes about as much disk space as the map uncompressed (often a few GB for a long game). After that, only
map chunks that changed get unpacked, a few seconds after every game save. Uncompressed worlds are
searched directly.

The Linux build needs glibc 2.35 or newer (Ubuntu 22.04+, Mint 21+, Fedora 36+, current Arch).

## Settings file: `cdda-companion.json`

Written by the setup and the Settings tab; you don't have to edit it. It sits next to the executable,
or in your user config folder (`~/.config/cdda-companion`, `%APPDATA%\cdda-companion`) when that folder isn't writable.

| Key | Meaning |
|---|---|
| `gameDir` | The game folder (contains `data/`, or `share/cataclysm-dda` for a package). |
| `userDir` | Optional. Your own game files (`save/`, `config/`, `mods/`, `gfx/`) when they aren't in the game folder, like a launcher's `userdata`. |
| `world` | World folder name. |
| `saveRoot` | Optional. The save folder, when it isn't `<userDir>/save`. |
| `configDir` | Optional. The game's settings folder, when it isn't `<userDir>/config`. |
| `bases` | Base per world, as the in-game map shows it, e.g. `"My World": "0'12, 0'34"`. |
| `dataDir` | Optional. Where snapshots, save slots and filters go (one folder per world inside). Default `<gameDir>/cdda-companion-data`; with a launcher, `<userDir>/cdda-companion-data`. Same drive as the save makes loading fast. |
| `slots` | Number of save slots (1–20). |
| `port` | Local port; the next free one is used if taken. |
| `openBrowser` | Open the browser on start. |
| `autoRefreshSnapshot` | Refresh the search snapshot after each game save. |
| `idleExitMinutes` | Quit when no page has been open this long (0 = never). Default 30. |

Category filters are saved in `<dataDir>/category-filters.json`.

## Safety

- The server listens on `127.0.0.1` only and every API call needs a random per-run token.
- Saves are copied into a temporary folder, checked (every archive checksum), and only then
  renamed into place. A complete save always exists somewhere.
- Load slots only while CDDA is closed or at the main menu.

## Building from source

Needs the .NET 10 SDK and the Native AOT toolchain. The app has no library dependencies (the zstd decoder
is vendored as source); Microsoft's SDK downloads the AOT compiler from nuget.org on the first build.

Ubuntu 24.04:

```sh
sudo apt install dotnet-sdk-10.0 dotnet-sdk-aot-10.0 clang zlib1g-dev libbrotli-dev libssl-dev
./build.sh            # output in ./dist
```

Windows: install the .NET 10 SDK and Visual Studio (or its Build Tools) with "Desktop development with C++",
then `dotnet publish src/CddaCompanion/CddaCompanion.csproj -c Release -r win-x64 -o dist`.

Releases are built by `.github/workflows/release.yml` (Windows and Ubuntu 22.04).

## How it reads the save

CDDA 0.I compressed saves are "zzip" archives: zstd frames with a FlexBuffers index, each entry
checksummed with XXH64 (seed `0x1337C0DE`). Map chunks use the world's `maps.dict`, overmaps use
`overmaps.dict`, and the character save has no dictionary. See `src/CddaCompanion/Core/Zzip.cs`.

## Third-party code

- [ZstdSharp](https://github.com/oleg-st/ZstdSharp) (MIT), in `third_party/ZstdSharp`.
- Map symbols, colours, tilesets and item data come from your own game install at runtime; no game data is bundled.
- The Codex was inspired by nornagon's [CDDA Guide](https://github.com/nornagon/cdda-guide); no code from it is used.
