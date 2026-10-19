using System.Text;
using System.Text.Json;

namespace CddaCompanion;

/// <summary>
/// Settings from cdda-companion.json: next to the executable when that folder is writable, otherwise in the
/// user's config folder. A missing or half-filled file is fine: the tool detects what it can and asks for the rest.
/// </summary>
public sealed class Config
{
    public string GameDir = "";
    public string SaveRoot = "";      // optional; default <GameDir>/save
    public string World = "";
    /// <summary>Base per world, as the in-game map shows it, e.g. 0'87, 0'41.</summary>
    public Dictionary<string, string> Bases = new(StringComparer.Ordinal);
    public string Base
    {
        get => Bases.TryGetValue(World ?? "", out var b) ? b : "";
        set { if (string.IsNullOrWhiteSpace(value)) Bases.Remove(World ?? ""); else Bases[World ?? ""] = value.Trim(); }
    }
    public string DataDir = "";       // optional; default <GameDir>/cdda-companion-data
    public int Slots = 5;
    public int Port = 8771;
    public bool OpenBrowser = true;
    public bool AutoRefreshSnapshot = true;
    public int IdleExitMinutes = 30;  // quit when no page has been open this long; 0 = never

    public string FilePath;

    public string WorldDir => Path.Combine(string.IsNullOrWhiteSpace(SaveRoot) ? Path.Combine(GameDir, "save") : SaveRoot, World);
    public string DataRoot => string.IsNullOrWhiteSpace(DataDir) ? Path.Combine(GameDir, "cdda-companion-data") : DataDir;
    // one snapshot and one set of save slots per world, so a slot can never be loaded into another world
    public string SnapshotDir => Path.Combine(DataRoot, "snapshot", World);
    public string BackupsDir => Path.Combine(DataRoot, "backups", World);
    public string CacheDir => Path.Combine(DataRoot, "cache");

    const string FileName = "cdda-companion.json";

    public static string UserConfigDir() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cdda-companion")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "cdda-companion");

    public static string UserDataDir() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cdda-companion")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "cdda-companion");

    /// <summary>The settings file next to the executable, else the one in the user config folder; for a new file, the first of those we can write to.</summary>
    public static string DefaultPath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, FileName);
        var user = Path.Combine(UserConfigDir(), FileName);
        if (File.Exists(local)) return local;
        if (File.Exists(user)) return user;
        return CanWrite(AppContext.BaseDirectory) ? local : user;
    }

    public static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".cdda-companion-write-test-{Environment.ProcessId}");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public bool Exists => File.Exists(FilePath);

    public static Config Load(string path)
    {
        var c = new Config { FilePath = path };
        if (!File.Exists(path)) return c;   // nothing yet: setup fills it in
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var r = doc.RootElement;
        string S(string k, string d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : d;
        int I(string k, int d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : d;
        bool B(string k, bool d) => r.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : d;
        c.GameDir = S("gameDir", c.GameDir);
        c.SaveRoot = S("saveRoot", c.SaveRoot);
        c.World = S("world", c.World);
        if (r.TryGetProperty("bases", out var bs) && bs.ValueKind == JsonValueKind.Object)
            foreach (var p in bs.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString().Length > 0) c.Bases[p.Name] = p.Value.GetString();
        // 0.7 and earlier: one "base" for the configured world
        if (S("base", "") is { Length: > 0 } legacy && !c.Bases.ContainsKey(c.World)) c.Bases[c.World] = legacy;
        c.DataDir = S("dataDir", c.DataDir);
        c.Slots = Math.Clamp(I("slots", c.Slots), 1, 20);
        c.Port = I("port", c.Port);
        c.OpenBrowser = B("openBrowser", c.OpenBrowser);
        c.AutoRefreshSnapshot = B("autoRefreshSnapshot", c.AutoRefreshSnapshot);
        c.IdleExitMinutes = Math.Max(0, I("idleExitMinutes", c.IdleExitMinutes));
        return c;
    }

    public void Save()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("gameDir", GameDir);
            w.WriteString("saveRoot", SaveRoot);
            w.WriteString("world", World);
            w.WriteStartObject("bases");
            foreach (var (k, v) in Bases.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.WriteString(k, v);
            w.WriteEndObject();
            w.WriteString("dataDir", DataDir);
            w.WriteNumber("slots", Slots);
            w.WriteNumber("port", Port);
            w.WriteBoolean("openBrowser", OpenBrowser);
            w.WriteBoolean("autoRefreshSnapshot", AutoRefreshSnapshot);
            w.WriteNumber("idleExitMinutes", IdleExitMinutes);
            w.WriteEndObject();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, FilePath, true);
    }

    /// <summary>Returns a list of problems that stop the tool from working; empty when fine.</summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(GameDir)) p.Add("The game folder isn't set yet.");
        else if (!Setup.IsGameDir(GameDir)) p.Add($"\"{GameDir}\" doesn't look like a Cataclysm: DDA folder (no data/json inside).");
        if (string.IsNullOrWhiteSpace(World)) p.Add("No world picked yet.");
        else if (!Directory.Exists(WorldDir)) p.Add($"World folder not found: \"{WorldDir}\".");
        return p;
    }

    public Config Clone()
    {
        var c = (Config)MemberwiseClone();
        c.Bases = new Dictionary<string, string>(Bases, StringComparer.Ordinal);
        return c;
    }
}
