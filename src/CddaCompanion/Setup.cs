using System.Text;

namespace CddaCompanion;

public sealed class WorldInfo
{
    public string Name, Dir, Character;
    public DateTime LastPlayedUtc;
    public bool Compressed;
}

/// <summary>
/// First-run help: finds the game folder and its worlds so a freshly downloaded copy works without editing JSON.
/// </summary>
public static class Setup
{
    /// <summary>Is there a Cataclysm game here? See Install.DataDirOf for the layouts.</summary>
    public static bool IsGameDir(string dir) => Install.IsGameDir(dir);

    public static bool IsWorld(string dir)
    {
        try { return File.Exists(Path.Combine(dir, "worldoptions.json")) || File.Exists(Path.Combine(dir, "master.gsav")); }
        catch { return false; }
    }

    /// <summary>A save folder: at least one world inside.</summary>
    public static bool HasWorlds(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Take(500).Any(d => !Path.GetFileName(d).StartsWith('.') && IsWorld(d)); }
        catch { return false; }
    }

    /// <summary>
    /// Games near the executable or the working folder (the folder itself, up to two parents, and their children named
    /// like the game or a launcher), the active DDA install of a launcher found there, and the places packages install to.
    /// </summary>
    public static List<string> DetectGameDirs()
    {
        var found = new List<string>();
        void Try(string d)
        {
            if (string.IsNullOrEmpty(d)) return;
            try { d = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); } catch { return; }
            if (IsGameDir(d) && !found.Contains(d)) found.Add(d);
            else if (Catapult.Locate(d) is { Code: "dda" } t && Catapult.Active(t.Root, "dda") is { } a && !found.Contains(a.Dir)) found.Add(a.Dir);
        }
        static bool Named(string n) => new[] { "ataclysm", "cdda", "catapult", "dabdoob" }.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase));
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            string d = start;
            for (int up = 0; up < 3 && d != null; up++)
            {
                Try(d);
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(d).Take(200))
                        if (Named(Path.GetFileName(child))) Try(child);
                }
                catch { }
                d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fixedPlaces = new List<string>();
        if (OperatingSystem.IsMacOS()) fixedPlaces.AddRange(new[] { "/Applications", Path.Combine(home, "Applications"), Path.Combine(home, "Library", "Application Support", "Dabdoob") });
        else if (!OperatingSystem.IsWindows())
            fixedPlaces.AddRange(new[] { "/usr/share/cataclysm-dda", "/usr/local/share/cataclysm-dda",
                "/var/lib/flatpak/app/org.cataclysmdda.CataclysmDDA/current/active/files/share/cataclysm-dda",
                Path.Combine(home, ".local", "share", "flatpak", "app", "org.cataclysmdda.CataclysmDDA", "current", "active", "files", "share", "cataclysm-dda") });
        foreach (var p in fixedPlaces) if (Directory.Exists(p)) Try(p);
        return found;
    }

    /// <summary>Worlds under a save folder, most recently played first.</summary>
    public static List<WorldInfo> Worlds(string saveRoot)
    {
        var list = new List<WorldInfo>();
        if (!Directory.Exists(saveRoot)) return list;
        foreach (var dir in Directory.EnumerateDirectories(saveRoot))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;   // our own restore staging folders
            if (!IsWorld(dir)) continue;
            var w = new WorldInfo { Name = name, Dir = dir, Compressed = File.Exists(Path.Combine(dir, "maps.dict")) || Directory.Exists(Path.Combine(dir, "overmaps")) };
            try
            {
                var saves = Directory.EnumerateFiles(dir, "#*.sav*").Where(f => f.EndsWith(".sav") || f.EndsWith(".sav.zzip"))
                    .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                if (saves.Count > 0)
                {
                    w.LastPlayedUtc = saves[0].LastWriteTimeUtc;
                    var b64 = saves[0].Name[1..saves[0].Name.IndexOf(".sav", StringComparison.Ordinal)];
                    try { w.Character = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); } catch { w.Character = b64; }
                }
                else w.LastPlayedUtc = Directory.GetLastWriteTimeUtc(dir);
            }
            catch { }
            list.Add(w);
        }
        return list.OrderByDescending(w => w.LastPlayedUtc).ToList();
    }

    /// <summary>
    /// Fills in what's missing: the game folder (if it can be found unambiguously), the player's folder, the world
    /// (the one played last) and a data folder a launcher won't delete. Follows a launcher to the install it now starts.
    /// Returns a short description of what was filled in, or null when nothing changed.
    /// </summary>
    public static string Complete(Config cfg)
    {
        var said = new List<string>();
        var tree = Catapult.Locate(cfg.GameDir);
        if (tree is { Code: "dda" } && Catapult.Active(tree.Root, "dda") is { } active && !Install.Same(active.Dir, cfg.GameDir))
        {
            // the launcher updated the game into another folder, or the player switched installs there
            cfg.GameDir = active.Dir;
            said.Add($"game folder {cfg.GameDir} (the launcher's \"{active.Name}\")");
        }
        else if (!IsGameDir(cfg.GameDir))
        {
            var found = DetectGameDirs();
            if (found.Count >= 1) { cfg.GameDir = found[0]; said.Add($"game folder {cfg.GameDir}"); }
        }
        if (IsGameDir(cfg.GameDir) && Install.Blank(cfg.UserDir) && Install.Blank(cfg.SaveRoot))
        {
            var user = Install.DefaultUserDir(cfg.GameDir);
            if (!Install.Same(user, cfg.GameDir)) { cfg.UserDir = user; said.Add($"user folder {user}"); }
        }
        if (IsGameDir(cfg.GameDir) && (string.IsNullOrWhiteSpace(cfg.World) || !Directory.Exists(cfg.WorldDir)))
        {
            var worlds = Worlds(cfg.SaveRootPath);
            var pick = worlds.FirstOrDefault(w => w.Character != null) ?? worlds.FirstOrDefault();
            if (pick != null) { cfg.World = pick.Name; said.Add($"world \"{pick.Name}\" (played last)"); }
        }
        if (FillDataDir(cfg) is { } dd) said.Add(dd);
        return said.Count > 0 ? string.Join(", ", said) : null;
    }

    /// <summary>
    /// The data folder (snapshot, save slots) defaults to &lt;game&gt;/cdda-companion-data. Launchers replace the game
    /// folder on every update, so there it goes into the launcher's userdata instead; read-only game folders use the
    /// user's data folder. An existing data folder is always kept where it is.
    /// </summary>
    public static string FillDataDir(Config cfg)
    {
        if (!IsGameDir(cfg.GameDir) || !string.IsNullOrWhiteSpace(cfg.DataDir)) return null;
        var inGame = Path.Combine(cfg.GameDir, "cdda-companion-data");
        if (Directory.Exists(inGame)) return null;
        if (Catapult.Locate(cfg.GameDir) is { Code: "dda" } && Config.CanWrite(Path.Combine(cfg.UserDirPath, "cdda-companion-data")))
        {
            cfg.DataDir = Path.Combine(cfg.UserDirPath, "cdda-companion-data");
            return $"data folder {cfg.DataDir} (outside the game folder, which the launcher replaces on updates)";
        }
        if (!Config.CanWrite(inGame))
        {
            cfg.DataDir = Config.UserDataDir();
            return $"data folder {cfg.DataDir} (the game folder isn't writable)";
        }
        return null;
    }

    /// <summary>
    /// Versions before 0.8 kept one snapshot and one set of slots for "the" world directly in snapshot/ and backups/.
    /// They belong to the world configured then, so they move into that world's folder.
    /// </summary>
    public static void MigrateDataLayout(Config cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.World)) return;
        try
        {
            var snap = Path.Combine(cfg.DataRoot, "snapshot");
            if (File.Exists(Path.Combine(snap, "manifest.tsv")) || File.Exists(Path.Combine(snap, "snapshot.json")))
                MoveInto(snap, cfg.World);
            var bak = Path.Combine(cfg.DataRoot, "backups");
            if (Directory.Exists(bak) && Directory.EnumerateDirectories(bak).Any(d => File.Exists(Path.Combine(d, "slot.json"))))
                MoveInto(bak, cfg.World);
        }
        catch (Exception ex) { Console.Error.WriteLine("Couldn't move old snapshot/slots into the per-world layout: " + ex.Message); }
    }

    static void MoveInto(string dir, string world)
    {
        var tmp = dir + ".moving-" + DateTime.UtcNow.Ticks;
        Directory.Move(dir, tmp);
        Directory.CreateDirectory(dir);
        Directory.Move(tmp, Path.Combine(dir, world));
        Console.WriteLine($"Moved {dir} into {Path.Combine(dir, world)} (one per world from now on).");
    }
}
