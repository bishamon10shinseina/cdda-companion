using System.Text.Json;

namespace CddaCompanion;

/// <summary>
/// Where one Cataclysm: DDA installation keeps each kind of file. The game splits them in several ways
/// (path_info.cpp / main.cpp at 0.I): a portable folder that holds everything, home-folder and XDG builds that keep
/// the player's files in the user's profile, distro packages with no data/ folder at all, and launchers that start the
/// game with --userdir. Nothing else in the tool should assume that saves, config, mods and tilesets sit under the
/// game folder.
/// </summary>
public sealed class Install
{
    /// <summary>The folder the settings call the game folder.</summary>
    public string GameDir = "";
    /// <summary>Has json/, raw/ and mods/: &lt;game&gt;/data, or share/cataclysm-dda of a package.</summary>
    public string DataDir;
    /// <summary>The game's own tilesets.</summary>
    public string GfxDir;
    /// <summary>The player's folder (the game's --userdir): save/, config/, mods/, gfx/, font/.</summary>
    public string UserDir;
    public string ConfigDir;
    public string SaveRoot;
    /// <summary>How the files are laid out, in words for the setup page.</summary>
    public string Kind = "";
    /// <summary>The launcher's name for this install, when there is one.</summary>
    public string Label;

    public bool Valid => DataDir != null;

    /// <summary>Folders with mods: the game's own, a mods/ next to the game (old builds), and the player's.</summary>
    public IEnumerable<string> ModRoots => Distinct(Join(DataDir, "mods"), Join(GameDir, "mods"), Join(UserDir, "mods"));

    /// <summary>Folders with tilesets: the game's own, then the player's (PATH_INFO::user_gfx).</summary>
    public IEnumerable<string> GfxRoots => Distinct(GfxDir, Join(UserDir, "gfx"));

    /// <summary>Config folders to read options and colours from, best first.</summary>
    public IEnumerable<string> ConfigDirs => Distinct(new[] { ConfigDir }.Concat(StandardUserDirs().SelectMany(u => ConfigDirsFor(u.Dir))).ToArray());

    // ------------------------------------------------------------------ from the settings

    /// <summary>The layout the settings describe. Missing pieces follow the game's own rules.</summary>
    public static Install From(Config cfg)
    {
        var i = new Install { GameDir = cfg.GameDir ?? "" };
        i.DataDir = DataDirOf(i.GameDir);
        i.GfxDir = i.DataDir == null ? null : GfxDirOf(i.DataDir);
        i.UserDir = cfg.UserDirPath;
        i.SaveRoot = cfg.SaveRootPath;
        i.ConfigDir = !Blank(cfg.ConfigDir) ? cfg.ConfigDir
            : ConfigDirsFor(i.UserDir).FirstOrDefault(Directory.Exists) ?? Path.Combine(i.UserDir, "config");
        i.Describe();
        return i;
    }

    void Describe()
    {
        if (Catapult.Locate(GameDir) is { Code: "dda" })
        {
            Kind = "Catapult/Dabdoob launcher";
            Label = Catapult.InstallName(GameDir);
        }
        else if (DataDir != null && !NameIs(DataDir, "data")) Kind = "installed package";
        else if (Same(UserDir, GameDir)) Kind = "game folder";
        else if (StandardUserDirs().FirstOrDefault(u => Same(u.Dir, UserDir)) is { Dir: not null } std) Kind = std.Kind;
        else Kind = "custom folders";
    }

    // ------------------------------------------------------------------ the game's files

    /// <summary>Is there a Cataclysm game in this folder (any of the layouts DataDirOf knows)?</summary>
    public static bool IsGameDir(string dir) => DataDirOf(dir) != null;

    /// <summary>
    /// The folder with json/ for a game folder: &lt;dir&gt;/data (release zips, launchers), &lt;dir&gt; itself when it is a
    /// package's share/cataclysm-dda, &lt;dir&gt;/share/cataclysm-dda (a package prefix such as /usr), or the data
    /// inside a macOS Cataclysm.app. Null when there's none.
    /// </summary>
    public static string DataDirOf(string dir)
    {
        if (Blank(dir)) return null;
        try
        {
            dir = Clean(dir);
            static string Has(string d) => Directory.Exists(Path.Combine(d, "json")) ? d : null;
            if (Has(Path.Combine(dir, "data")) is { } a) return a;
            // a game's data/ folder looks like a package's data folder; let its parent be the game folder
            if (!NameIs(dir, "data") && Directory.Exists(Path.Combine(dir, "raw")) && Has(dir) is { } b) return b;
            if (Has(Path.Combine(dir, "share", "cataclysm-dda")) is { } c) return c;
            if (Has(Path.Combine(dir, "Contents", "Resources", "data")) is { } m) return m;
            if (Directory.Exists(dir))
                foreach (var app in Directory.EnumerateDirectories(dir, "*.app").Take(8))
                    if (Has(Path.Combine(app, "Contents", "Resources", "data")) is { } n) return n;
        }
        catch { }
        return null;
    }

    /// <summary>Tilesets sit next to data/ (gfx/), or inside a package's share/cataclysm-dda.</summary>
    public static string GfxDirOf(string dataDir) =>
        NameIs(dataDir, "data") ? Path.Combine(Path.GetDirectoryName(Clean(dataDir)) ?? "", "gfx") : Path.Combine(dataDir, "gfx");

    // ------------------------------------------------------------------ the player's files

    /// <summary>
    /// Where the game keeps the player's files when nothing says otherwise (PATH_INFO::init_user_dir): %LOCALAPPDATA%
    /// on Windows, Application Support on macOS, XDG or ~/.cataclysm-dda on Linux. Flatpak keeps XDG inside its sandbox.
    /// </summary>
    public static IEnumerable<(string Dir, string Kind)> StandardUserDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cataclysm-dda"), "user folder");
            // read by versions before 0.8.2; harmless to keep looking there
            yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cataclysm-dda"), "user folder");
            yield break;
        }
        if (string.IsNullOrEmpty(home)) yield break;
        if (OperatingSystem.IsMacOS()) yield return (Path.Combine(home, "Library", "Application Support", "Cataclysm"), "user folder");
        yield return (Path.Combine(XdgData(), "cataclysm-dda"), "XDG user folder");
        yield return (Path.Combine(home, ".cataclysm-dda"), "home folder");
        yield return (Path.Combine(home, ".var", "app", "org.cataclysmdda.CataclysmDDA", "data", "cataclysm-dda"), "Flatpak");
    }

    /// <summary>Config folders that go with a user folder: XDG builds (and Flatpak) keep config apart from the data.</summary>
    public static IEnumerable<string> ConfigDirsFor(string userDir)
    {
        if (Blank(userDir)) yield break;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!OperatingSystem.IsWindows() && !string.IsNullOrEmpty(home))
        {
            if (Same(userDir, Path.Combine(XdgData(), "cataclysm-dda")))
                yield return Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config"), "cataclysm-dda");
            var flat = Path.Combine(home, ".var", "app", "org.cataclysmdda.CataclysmDDA");
            if (Same(userDir, Path.Combine(flat, "data", "cataclysm-dda"))) yield return Path.Combine(flat, "config", "cataclysm-dda");
        }
        yield return Path.Combine(userDir, "config");
    }

    /// <summary>
    /// The user folder for a game folder when the settings don't name one: the launcher's userdata, the game folder
    /// itself when it has a save/ (portable), else the standard folder whose worlds were played last.
    /// </summary>
    public static string DefaultUserDir(string gameDir)
    {
        if (Catapult.Locate(gameDir) is { Code: "dda" } t) return t.UserData;
        if (Directory.Exists(Path.Combine(gameDir, "save"))) return Clean(gameDir);
        var withWorlds = StandardUserDirs().Select(u => u.Dir)
            .Select(d => (d, last: Setup.Worlds(Path.Combine(d, "save")).Select(w => w.LastPlayedUtc).DefaultIfEmpty(DateTime.MinValue).Max()))
            .Where(x => x.last > DateTime.MinValue).OrderByDescending(x => x.last).Select(x => x.d).FirstOrDefault();
        if (withWorlds != null) return withWorlds;
        // no worlds anywhere yet: a package can't keep saves next to itself, so the standard folder it will use
        if (DataDirOf(gameDir) is { } data && !NameIs(data, "data"))
            return StandardUserDirs().Select(u => u.Dir).FirstOrDefault(Directory.Exists) ?? StandardUserDirs().Select(u => u.Dir).FirstOrDefault() ?? Clean(gameDir);
        return Clean(gameDir);
    }

    // ------------------------------------------------------------------ any folder the player points at

    public sealed class Found
    {
        /// <summary>What the settings should store; UserDir and SaveRoot stay empty when the game's defaults give them.</summary>
        public string GameDir = "", UserDir = "", SaveRoot = "";
        /// <summary>A world, when the folder was a world folder.</summary>
        public string World;
        public string Problem;
    }

    /// <summary>
    /// Works out the game and the player's folders from whatever folder someone pastes: the game folder, a launcher's
    /// folder (or any folder inside it), a user folder, the save folder or one world. <paramref name="knownGame"/> is
    /// used when the folder only has saves and no game.
    /// </summary>
    public static Found Locate(string path, string knownGame)
    {
        var f = new Found();
        if (Blank(path)) { f.Problem = "Paste the folder you start the game from, your launcher's folder, or your save folder."; return f; }
        try { path = Clean(Path.GetFullPath(path.Trim().Trim('"'))); } catch { f.Problem = "That isn't a folder path."; return f; }
        if (File.Exists(path)) path = Path.GetDirectoryName(path);
        if (!Directory.Exists(path)) { f.Problem = "That folder doesn't exist."; return f; }

        // a world or save folder: remember it, and carry on from the folder above
        string start = path;
        if (Setup.IsWorld(path)) { f.World = Path.GetFileName(path); path = Path.GetDirectoryName(path); }
        string saveRoot = null;
        if (path != null && !IsGameDir(path) && Setup.HasWorlds(path)) { saveRoot = path; path = Path.GetDirectoryName(path); }

        // launchers: the game, its userdata and other games all live under one folder
        if (Catapult.Locate(start) is { } t)
        {
            if (t.Code != "dda")
            {
                f.Problem = $"That's a {Catapult.GameName(t.Code)} install. CDDA Companion only works with Cataclysm: DDA.";
                return f;
            }
            var active = Catapult.Active(t.Root, "dda");
            if (active == null) { f.Problem = "That launcher has no Cataclysm: DDA installed yet."; return f; }
            f.GameDir = active.Value.Dir;
            f.UserDir = t.UserData;
            if (saveRoot != null && !Same(saveRoot, Path.Combine(t.UserData, "save"))) f.SaveRoot = saveRoot;
            return f;
        }

        // the game folder is this one, or the one above when a sub-folder (data/, save/, gfx/) was pasted
        string game = path != null && IsGameDir(path) ? path
            : path != null && Path.GetDirectoryName(path) is { } up && IsGameDir(up) && saveRoot == null ? up : null;
        string user = saveRoot != null && NameIs(saveRoot, "save") ? path : null;
        if (saveRoot == null && path != null && Setup.HasWorlds(Path.Combine(path, "save"))) user = path;

        if (game == null)
        {
            if (user == null && saveRoot == null) { f.Problem = "There's no Cataclysm: DDA game in that folder (no data/json inside), and no saves either."; return f; }
            game = IsGameDir(knownGame) ? Clean(knownGame) : Setup.DetectGameDirs().FirstOrDefault();
            if (game == null)
            {
                f.UserDir = user ?? "";
                f.SaveRoot = saveRoot ?? Path.Combine(user, "save");
                f.Problem = $"Found your saves in {f.SaveRoot}, but not the game itself. Paste the game folder (the one with data/ inside) above.";
                return f;
            }
        }
        f.GameDir = Clean(game);
        user ??= saveRoot == null ? DefaultUserDir(game) : null;
        if (user != null && !Same(user, game)) f.UserDir = user;
        if (saveRoot != null && !(user != null && Same(saveRoot, Path.Combine(user, "save")))) f.SaveRoot = saveRoot;
        return f;
    }

    // ------------------------------------------------------------------ helpers

    internal static bool Blank(string s) => string.IsNullOrWhiteSpace(s);
    internal static string Clean(string d) => Blank(d) ? d : d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is { Length: > 0 } t ? t : d;
    static bool NameIs(string dir, string name) => string.Equals(Path.GetFileName(Clean(dir)), name, StringComparison.OrdinalIgnoreCase);
    internal static bool Same(string a, string b)
    {
        if (Blank(a) || Blank(b)) return false;
        try { return string.Equals(Clean(Path.GetFullPath(a)), Clean(Path.GetFullPath(b)), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    static string Join(string dir, string sub) => Blank(dir) ? null : Path.Combine(dir, sub);
    static string XdgData() => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    static IEnumerable<string> Distinct(params string[] dirs)
    {
        var seen = new List<string>();
        foreach (var d in dirs)
            if (!Blank(d) && !seen.Any(s => Same(s, d))) { seen.Add(d); yield return d; }
    }
}

/// <summary>
/// Catapult and its fork Dabdoob: &lt;launcher&gt;/&lt;game&gt;/game0, game1… (each with catapult_install_info.json) and
/// &lt;launcher&gt;/&lt;game&gt;/userdata, passed to the game as --userdir. catapult_settings.json says which install is active.
/// Updating the game replaces the install folder, so nothing of ours may live inside it.
/// </summary>
public static class Catapult
{
    public const string InfoFile = "catapult_install_info.json", SettingsFile = "catapult_settings.json";
    static readonly (string Code, string Name)[] Games = {
        ("dda", "Cataclysm: DDA"), ("bn", "Cataclysm: Bright Nights"), ("tlg", "Cataclysm: The Last Generation"),
        ("eod", "Cataclysm: Era of Decay"), ("tish", "Cataclysm: There Is Still Hope") };

    public sealed record Tree(string Root, string Code)
    {
        public string GameFolder => Path.Combine(Root, Code);
        public string UserData => Path.Combine(Root, Code, "userdata");
    }

    public static string GameName(string code) => Games.FirstOrDefault(g => g.Code == code).Name ?? $"\"{code}\"";

    /// <summary>The launcher tree a path sits in (the path itself or up to four folders above), or null.</summary>
    public static Tree Locate(string path)
    {
        if (Install.Blank(path)) return null;
        string d;
        try { d = Install.Clean(Path.GetFullPath(path)); } catch { return null; }
        for (int i = 0; i < 5 && !string.IsNullOrEmpty(d); i++)
        {
            var name = Path.GetFileName(d);
            var parent = Path.GetDirectoryName(d);
            if (parent != null && Games.Any(g => g.Code == name) && IsGameFolder(d)) return new Tree(parent, name);
            if (IsRoot(d)) return new Tree(d, PickGame(d));
            d = parent;
        }
        return null;
    }

    static bool IsRoot(string dir)
    {
        try { return File.Exists(Path.Combine(dir, SettingsFile)) && Games.Any(g => Directory.Exists(Path.Combine(dir, g.Code))); }
        catch { return false; }
    }

    static bool IsGameFolder(string dir)
    {
        try { return Directory.Exists(dir) && (Directory.Exists(Path.Combine(dir, "userdata")) || Installs(dir).Any()); }
        catch { return false; }
    }

    /// <summary>A launcher folder holds several games; DDA is ours, else whatever the launcher has selected.</summary>
    static string PickGame(string root)
    {
        if (IsGameFolder(Path.Combine(root, "dda"))) return "dda";
        return ReadSetting(root, "game") is { } g && Games.Any(x => x.Code == g) ? g
            : Games.Select(x => x.Code).FirstOrDefault(c => IsGameFolder(Path.Combine(root, c))) ?? "dda";
    }

    public static IEnumerable<(string Name, string Dir)> Installs(string gameFolder)
    {
        List<string> subs;
        try { subs = Directory.EnumerateDirectories(gameFolder).ToList(); } catch { yield break; }
        foreach (var d in subs)
        {
            if (!File.Exists(Path.Combine(d, InfoFile))) continue;
            yield return (InstallName(d) ?? Path.GetFileName(d), d);
        }
    }

    public static string InstallName(string installDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(installDir, InfoFile)));
            return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>The install the launcher starts: active_install_&lt;game&gt; in its settings, else the newest one.</summary>
    public static (string Name, string Dir)? Active(string root, string code)
    {
        var all = Installs(Path.Combine(root, code)).Where(i => Install.IsGameDir(i.Dir)).ToList();
        if (all.Count == 0) return null;
        var want = ReadSetting(root, "active_install_" + code);
        foreach (var i in all) if (want != null && i.Name == want) return i;
        return all.OrderByDescending(i => Directory.GetLastWriteTimeUtc(i.Dir)).First();
    }

    static string ReadSetting(string root, string key)
    {
        try
        {
            var f = Path.Combine(root, SettingsFile);
            if (!File.Exists(f)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(f), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch { return null; }
    }
}
