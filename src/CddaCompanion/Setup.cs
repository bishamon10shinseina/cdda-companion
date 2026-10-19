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
    /// <summary>A CDDA install has data/json; the save folder may live elsewhere (XDG builds).</summary>
    public static bool IsGameDir(string dir)
    {
        try { return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(Path.Combine(dir, "data", "json")); }
        catch { return false; }
    }

    /// <summary>Game folders near the executable or the working folder: the folder itself, up to two parents, and their children.</summary>
    public static List<string> DetectGameDirs()
    {
        var found = new List<string>();
        void Try(string d)
        {
            if (string.IsNullOrEmpty(d)) return;
            try { d = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); } catch { return; }
            if (IsGameDir(d) && !found.Contains(d)) found.Add(d);
        }
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            string d = start;
            for (int up = 0; up < 3 && d != null; up++)
            {
                Try(d);
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(d).Take(200))
                        if (Path.GetFileName(child).Contains("ataclysm", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(child).Contains("cdda", StringComparison.OrdinalIgnoreCase))
                            Try(child);
                }
                catch { }
                d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
        return found;
    }

    /// <summary>Where the worlds are: &lt;game&gt;/save, else the user folders some builds use.</summary>
    public static string SaveRootFor(string gameDir, string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var inGame = Path.Combine(gameDir ?? "", "save");
        if (Directory.Exists(inGame)) return inGame;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var alt in new[] {
            Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".local", "share"), "cataclysm-dda", "save"),
            Path.Combine(home, ".cataclysm-dda", "save"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cataclysm-dda", "save") })
            if (Directory.Exists(alt)) return alt;
        return inGame;
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
            bool looks = File.Exists(Path.Combine(dir, "worldoptions.json")) || File.Exists(Path.Combine(dir, "master.gsav"));
            if (!looks) continue;
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
    /// Fills in what's missing: the game folder (if it can be found unambiguously) and the world (the one played last).
    /// Returns a short description of what was filled in, or null when nothing changed.
    /// </summary>
    public static string Complete(Config cfg)
    {
        var said = new List<string>();
        if (!IsGameDir(cfg.GameDir))
        {
            var found = DetectGameDirs();
            if (found.Count >= 1) { cfg.GameDir = found[0]; said.Add($"game folder {cfg.GameDir}"); }
        }
        if (IsGameDir(cfg.GameDir) && (string.IsNullOrWhiteSpace(cfg.World) || !Directory.Exists(cfg.WorldDir)))
        {
            var root = SaveRootFor(cfg.GameDir, cfg.SaveRoot);
            if (string.IsNullOrWhiteSpace(cfg.SaveRoot) && root != Path.Combine(cfg.GameDir, "save")) cfg.SaveRoot = root;
            var worlds = Worlds(root);
            var pick = worlds.FirstOrDefault(w => w.Character != null) ?? worlds.FirstOrDefault();
            if (pick != null) { cfg.World = pick.Name; said.Add($"world \"{pick.Name}\" (played last)"); }
        }
        if (IsGameDir(cfg.GameDir) && string.IsNullOrWhiteSpace(cfg.DataDir) && !Config.CanWrite(Path.Combine(cfg.GameDir, "cdda-companion-data")))
        {
            cfg.DataDir = Config.UserDataDir();
            said.Add($"data folder {cfg.DataDir} (the game folder isn't writable)");
        }
        return said.Count > 0 ? string.Join(", ", said) : null;
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
