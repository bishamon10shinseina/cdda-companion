## Download

- **Windows:** `cdda-companion-{{VERSION}}-windows-x64.zip`
- **Linux:** `cdda-companion-{{VERSION}}-linux-x64.tar.gz` (glibc 2.35 or newer)

## Install

1. Unpack it and put `cdda-companion` (`cdda-companion.exe` on Windows) in your Cataclysm: DDA game folder
   or your launcher's folder. Anywhere else works too.
2. Start it. Your browser opens on your map.

It finds the game and the world you played last by itself. If it can't, the page asks: paste the folder you
start the game from, your launcher's folder, or your save folder.
It never writes to your save, except when you load one of its save slots.

Made for **Cataclysm: DDA 0.I**.

## What's new

- Works with the Catapult and Dabdoob launchers. They keep the game and your saves in separate folders; the
  companion now finds both, uses the install the launcher starts, and follows it when the launcher updates the game.
  Its own backups go into the launcher's `userdata`, so a game update can't delete them.
- The setup page takes any folder: the game folder, a launcher's folder, the save folder or a world. It shows where
  it found the game, the saves and the game's settings, and the save folder can be changed by hand.
- Mods and tilesets in your user folder (`userdata/mods`, `userdata/gfx`) are found too, and so is the overmap
  tileset picked in the game's options when the settings live outside the game folder.
- Linux packages (`/usr/share/cataclysm-dda`), XDG and home-folder builds, and the Windows and macOS user folders are
  recognised.
- Forks of the game (Bright Nights, The Last Generation and others) are named and turned down instead of half-working.
