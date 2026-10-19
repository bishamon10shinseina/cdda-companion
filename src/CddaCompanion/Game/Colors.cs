using System.Text.Json;

namespace CddaCompanion.Game;

/// <summary>CDDA's 16 curses colours and its colour-name syntax (c_x, i_x inverted, h_x highlighted, fg_bg).</summary>
public static class Colors
{
    public static readonly string[] Names = {
        "black", "red", "green", "brown", "blue", "magenta", "cyan", "light_gray",
        "dark_gray", "light_red", "light_green", "yellow", "light_blue", "pink", "light_cyan", "white" };

    // data/raw/colors.json keys, same order
    static readonly string[] RawKeys = {
        "BLACK", "RED", "GREEN", "BROWN", "BLUE", "MAGENTA", "CYAN", "GRAY",
        "DGRAY", "LRED", "LGREEN", "YELLOW", "LBLUE", "LMAGENTA", "LCYAN", "WHITE" };

    static readonly int[][] Defaults = {
        new[] { 0, 0, 0 }, new[] { 255, 0, 0 }, new[] { 0, 110, 0 }, new[] { 97, 56, 28 },
        new[] { 10, 10, 220 }, new[] { 139, 58, 98 }, new[] { 0, 150, 180 }, new[] { 150, 150, 150 },
        new[] { 99, 99, 99 }, new[] { 255, 150, 150 }, new[] { 0, 255, 0 }, new[] { 255, 255, 0 },
        new[] { 100, 100, 255 }, new[] { 254, 0, 254 }, new[] { 0, 240, 255 }, new[] { 255, 255, 255 } };

    public static readonly int[][] Rgb = Defaults.Select(c => (int[])c.Clone()).ToArray();

    /// <summary>Where the current colours came from, for the Settings tab: a base_colors.json path, "game" or "built-in".</summary>
    public static string Source { get; private set; } = "built-in";

    static readonly Dictionary<string, int> Index = new(StringComparer.Ordinal);
    static readonly string[] ByLength;

    static Colors()
    {
        for (int i = 0; i < Names.Length; i++) Index[Names[i]] = i;
        foreach (var (alias, name) in new[] {
            ("ltgray", "light_gray"), ("lightgray", "light_gray"), ("gray", "light_gray"), ("grey", "light_gray"),
            ("light_grey", "light_gray"), ("dkgray", "dark_gray"), ("darkgray", "dark_gray"), ("dark_grey", "dark_gray"),
            ("ltred", "light_red"), ("ltgreen", "light_green"), ("ltblue", "light_blue"), ("ltcyan", "light_cyan"),
            ("light_magenta", "pink"), ("ltmagenta", "pink"), ("lmagenta", "pink") })
            Index[alias] = Index[name];
        ByLength = Index.Keys.OrderByDescending(k => k.Length).ToArray();
    }

    /// <summary>
    /// The colours the player's game uses: data/raw/colors.json (the game's defaults), then config/base_colors.json
    /// on top (written by the game's colour manager when a colour theme is picked or edited).
    /// </summary>
    public static void LoadFromGame(string gameDir)
    {
        for (int i = 0; i < Rgb.Length; i++) Rgb[i] = (int[])Defaults[i].Clone();
        Source = "built-in";
        if (ReadInto(Path.Combine(gameDir, "data", "raw", "colors.json"))) Source = "game";
        foreach (var dir in ConfigDirs(gameDir))
        {
            var f = Path.Combine(dir, "base_colors.json");
            if (ReadInto(f)) { Source = f; break; }
        }
    }

    /// <summary>Folders the game may keep its config in: next to the game, or in the user's data folder.</summary>
    public static IEnumerable<string> ConfigDirs(string gameDir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(gameDir)) yield return Path.Combine(gameDir, "config");
        yield return Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config"), "cataclysm-dda");
        yield return Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(home, ".local", "share"), "cataclysm-dda", "config");
        yield return Path.Combine(home, ".cataclysm-dda", "config");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cataclysm-dda", "config");
    }

    static bool ReadInto(string f)
    {
        try
        {
            if (!File.Exists(f)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(f), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var o = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return false;
            bool any = false;
            for (int i = 0; i < RawKeys.Length; i++)
                if (o.TryGetProperty(RawKeys[i], out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 3)
                {
                    Rgb[i] = new[] { Math.Clamp(v[0].GetInt32(), 0, 255), Math.Clamp(v[1].GetInt32(), 0, 255), Math.Clamp(v[2].GetInt32(), 0, 255) };
                    any = true;
                }
            return any;
        }
        catch { return false; }
    }

    /// <summary>Returns (foreground, background) palette indices; background -1 means none.</summary>
    public static (int fg, int bg) Parse(string name)
    {
        if (string.IsNullOrEmpty(name)) return (7, -1);
        var n = name.StartsWith("c_", StringComparison.Ordinal) ? name.Substring(2) : name;
        if (Index.TryGetValue(n, out var c)) return (c, -1);
        if (n.StartsWith("i_", StringComparison.Ordinal) && Index.TryGetValue(n.Substring(2), out var inv)) return (0, inv);
        if (n.StartsWith("h_", StringComparison.Ordinal) && Index.TryGetValue(n.Substring(2), out var hl)) return (hl, 4);
        foreach (var fg in ByLength)
            if (n.Length > fg.Length + 1 && n.StartsWith(fg, StringComparison.Ordinal) && n[fg.Length] == '_' &&
                Index.TryGetValue(n.Substring(fg.Length + 1), out var bg))
                return (Index[fg], bg);
        return (13, -1);
    }

    /// <summary>Map note colour codes (src/color.cpp).</summary>
    public static int NoteColor(string code) => code switch
    {
        "br" => 3, "lg" => 7, "dg" => 8, "r" => 9, "R" => 1, "g" => 10, "G" => 2, "b" => 12,
        "B" => 4, "W" => 15, "C" => 6, "c" => 14, "P" => 13, "m" => 5, _ => 11
    };
}
