using System.Text;
using System.Text.Json;
using CddaCompanion.Core;

namespace CddaCompanion.Game;

public sealed class CharacterInfo
{
    public string Name, FileBase, DateText;
    public long Turn;
    public int X, Y, Z;          // absolute overmap-tile coordinates
    public int Year, Day;
    public string Season;
}

public sealed class Note
{
    public int X, Y;             // local to the overmap
    public string Text, Sym;
    public int Color;
    public bool Dangerous;
    public int Radius;
}

public sealed class City
{
    public int X, Y, Size;       // local to the overmap
    public string Name;
}

public sealed class OvermapTerrain
{
    public const int Size = 180, Layers = 21, Depth = 10;
    public string[] Ids;                  // palette of terrain ids
    public ushort[][] Grid = new ushort[Layers][]; // per z: index into Ids, row-major
    public List<City> Cities = new();
}

public sealed class SeenData
{
    public byte[][] Vision = new byte[OvermapTerrain.Layers][];
    public List<Note>[] Notes = new List<Note>[OvermapTerrain.Layers];
}

/// <summary>Everything read from the world's save folder. Never writes there.</summary>
public sealed class World
{
    public readonly string Dir;
    public World(string dir) { Dir = dir; }

    static readonly JsonDocumentOptions JOpts = new() { AllowTrailingCommas = true, MaxDepth = 4096 };

    public string MapsDir => Path.Combine(Dir, "maps");
    public string OvermapsDir => Path.Combine(Dir, "overmaps");
    public string MapsDict => Path.Combine(Dir, "maps.dict");
    public string OvermapsDict => Path.Combine(Dir, "overmaps.dict");
    public bool Compressed => File.Exists(MapsDict) || Directory.Exists(OvermapsDir);

    public List<(int x, int y)> Overmaps()
    {
        var res = new List<(int, int)>();
        IEnumerable<string> files = Compressed
            ? (Directory.Exists(OvermapsDir) ? Directory.EnumerateFiles(OvermapsDir, "o.*.zzip") : Array.Empty<string>())
            : Directory.EnumerateFiles(Dir, "o.*");
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            if (name.EndsWith(".zzip")) name = name[..^5];
            var p = name.Split('.');
            if (p.Length == 3 && int.TryParse(p[1], out var x) && int.TryParse(p[2], out var y)) res.Add((x, y));
        }
        return res;
    }

    public string OvermapPath(int x, int y) => Compressed
        ? Path.Combine(OvermapsDir, $"o.{x}.{y}.zzip")
        : Path.Combine(Dir, $"o.{x}.{y}");

    // ---------------------------------------------------------------- character
    /// <summary>"#QWxleA==" style prefix of the (most recently saved) character's files.</summary>
    public string CharacterBase()
    {
        var best = Directory.EnumerateFiles(Dir, "#*.sav*")
            .Where(f => f.EndsWith(".sav") || f.EndsWith(".sav.zzip"))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (best == null) return null;
        var n = Path.GetFileName(best);
        return n.Substring(0, n.IndexOf(".sav", StringComparison.Ordinal));
    }

    public string CharacterSavePath()
    {
        var b = CharacterBase();
        if (b == null) return null;
        var z = Path.Combine(Dir, b + ".sav.zzip");
        return File.Exists(z) ? z : Path.Combine(Dir, b + ".sav");
    }

    public static string DecodeName(string fileBase)
    {
        var s = fileBase.TrimStart('#');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return s; }
    }

    public int SeasonLength()
    {
        try
        {
            var f = Path.Combine(Dir, "worldoptions.json");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(f), JOpts);
            foreach (var o in doc.RootElement.EnumerateArray())
                if (o.TryGetProperty("name", out var n) && n.GetString() == "SEASON_LENGTH" &&
                    o.TryGetProperty("value", out var v) && int.TryParse(v.GetString(), out var days) && days > 0)
                    return days;
        }
        catch { }
        return 91;
    }

    (string path, DateTime t, long len, CharacterInfo info) _charCache;

    public CharacterInfo ReadCharacter()
    {
        var path = CharacterSavePath();
        if (path == null || !File.Exists(path)) return null;
        var fi = new FileInfo(path);
        var cached = _charCache;
        if (cached.path == path && cached.t == fi.LastWriteTimeUtc && cached.len == fi.Length) return cached.info;
        var info = ReadCharacterUncached(path);
        _charCache = (path, fi.LastWriteTimeUtc, fi.Length, info);
        return info;
    }

    CharacterInfo ReadCharacterUncached(string path)
    {
        byte[] data = path.EndsWith(".zzip")
            ? Zstd.ReadEntry(path, Path.GetFileName(path)[..^5], null)
            : File.ReadAllBytes(path);
        var info = new CharacterInfo { FileBase = CharacterBase() };
        info.Name = DecodeName(info.FileBase);
        using var doc = JsonDocument.Parse(Text.JsonPart(data), JOpts);
        var r = doc.RootElement;
        if (r.TryGetProperty("turn", out var t) && t.TryGetInt64(out var turn)) info.Turn = turn;
        if (r.TryGetProperty("player", out var pl))
        {
            if (pl.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) info.Name = nm.GetString();
            if (pl.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Array && loc.GetArrayLength() >= 3)
            {
                info.X = FloorDiv(loc[0].GetInt32(), 24); info.Y = FloorDiv(loc[1].GetInt32(), 24); info.Z = loc[2].GetInt32();
            }
        }
        int seasonDays = SeasonLength();
        long day = info.Turn / 86400, secs = info.Turn % 86400;
        info.Year = (int)(day / (4L * seasonDays)) + 1;
        int dayOfYear = (int)(day % (4L * seasonDays));
        info.Season = new[] { "Spring", "Summer", "Autumn", "Winter" }[dayOfYear / seasonDays];
        info.Day = dayOfYear % seasonDays + 1;
        info.DateText = $"{info.Season}, day {info.Day}, {secs / 3600:00}:{secs % 3600 / 60:00} (year {info.Year})";
        return info;
    }

    public static string TurnToDate(long turn, int seasonDays)
    {
        long day = turn / 86400, secs = turn % 86400;
        int year = (int)(day / (4L * seasonDays)) + 1, doy = (int)(day % (4L * seasonDays));
        var season = new[] { "Spring", "Summer", "Autumn", "Winter" }[doy / seasonDays];
        return $"{season}, day {doy % seasonDays + 1}, {secs / 3600:00}:{secs % 3600 / 60:00} (year {year})";
    }

    public static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    // ---------------------------------------------------------------- overmap terrain
    readonly Dictionary<string, (DateTime t, long len, OvermapTerrain data)> _terrainCache = new();
    readonly Dictionary<string, (DateTime t, long len, SeenData data)> _seenCache = new();

    public OvermapTerrain ReadTerrain(int ox, int oy)
    {
        var path = OvermapPath(ox, oy);
        if (!File.Exists(path)) return null;
        var fi = new FileInfo(path);
        lock (_terrainCache)
            if (_terrainCache.TryGetValue(path, out var c) && c.t == fi.LastWriteTimeUtc && c.len == fi.Length) return c.data;

        byte[] data = Compressed ? Zstd.ReadEntry(path, $"o.{ox}.{oy}", File.Exists(OvermapsDict) ? OvermapsDict : null) : File.ReadAllBytes(path);
        var res = new OvermapTerrain();
        var ids = new Dictionary<string, ushort>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(Text.JsonPart(data), JOpts))
        {
            var r = doc.RootElement;
            var layers = r.GetProperty("layers");
            int z = 0;
            foreach (var layer in layers.EnumerateArray())
            {
                if (z >= OvermapTerrain.Layers) break;
                var grid = new ushort[OvermapTerrain.Size * OvermapTerrain.Size];
                int i = 0;
                foreach (var run in layer.EnumerateArray())
                {
                    var id = run[0].GetString(); int n = run[1].GetInt32();
                    if (!ids.TryGetValue(id, out var idx)) { idx = (ushort)ids.Count; ids[id] = idx; }
                    for (int k = 0; k < n && i < grid.Length; k++) grid[i++] = idx;
                }
                res.Grid[z++] = grid;
            }
            if (r.TryGetProperty("cities", out var cities))
                foreach (var c in cities.EnumerateArray())
                {
                    var city = new City { Name = c.TryGetProperty("name", out var nm) ? nm.GetString() : "?" };
                    if (c.TryGetProperty("pos", out var pos) && pos.ValueKind == JsonValueKind.Array) { city.X = pos[0].GetInt32(); city.Y = pos[1].GetInt32(); }
                    else
                    {
                        if (c.TryGetProperty("x", out var x)) city.X = x.GetInt32();
                        if (c.TryGetProperty("y", out var y)) city.Y = y.GetInt32();
                    }
                    if (c.TryGetProperty("size", out var s)) city.Size = s.GetInt32();
                    res.Cities.Add(city);
                }
        }
        res.Ids = new string[ids.Count];
        foreach (var (k, v) in ids) res.Ids[v] = k;
        for (int z = 0; z < OvermapTerrain.Layers; z++) res.Grid[z] ??= new ushort[OvermapTerrain.Size * OvermapTerrain.Size];
        lock (_terrainCache) _terrainCache[path] = (fi.LastWriteTimeUtc, fi.Length, res);
        return res;
    }

    public string SeenPath(int ox, int oy)
    {
        var b = CharacterBase();
        return b == null ? null : Path.Combine(Dir, $"{b}.seen.{ox}.{oy}");
    }

    public SeenData ReadSeen(int ox, int oy)
    {
        var path = SeenPath(ox, oy);
        var res = new SeenData();
        for (int z = 0; z < OvermapTerrain.Layers; z++) { res.Vision[z] = new byte[OvermapTerrain.Size * OvermapTerrain.Size]; res.Notes[z] = new List<Note>(); }
        if (path == null) return res;
        byte[] data;
        if (File.Exists(path)) data = null;
        else if (File.Exists(path + ".zzip")) path += ".zzip";
        else return res;
        var fi = new FileInfo(path);
        lock (_seenCache)
            if (_seenCache.TryGetValue(path, out var c) && c.t == fi.LastWriteTimeUtc && c.len == fi.Length) return c.data;
        data = path.EndsWith(".zzip") ? Zstd.ReadEntry(path, Path.GetFileName(path)[..^5], null) : File.ReadAllBytes(path);

        using (var doc = JsonDocument.Parse(Text.JsonPart(data), JOpts))
        {
            var r = doc.RootElement;
            if (r.TryGetProperty("visible", out var vis))
            {
                int z = 0;
                foreach (var layer in vis.EnumerateArray())
                {
                    if (z >= OvermapTerrain.Layers) break;
                    var v = res.Vision[z++]; int i = 0;
                    foreach (var run in layer.EnumerateArray())
                    {
                        var val = run[0];
                        byte b = val.ValueKind switch
                        {
                            JsonValueKind.True => 4, JsonValueKind.False => 0,
                            JsonValueKind.Number => (byte)Math.Clamp(val.GetInt32(), 0, 4), _ => 0
                        };
                        int n = run[1].GetInt32();
                        for (int k = 0; k < n && i < v.Length; k++) v[i++] = b;
                    }
                }
            }
            if (r.TryGetProperty("notes", out var notes))
            {
                int z = 0;
                foreach (var layer in notes.EnumerateArray())
                {
                    if (z >= OvermapTerrain.Layers) break;
                    var list = res.Notes[z++];
                    foreach (var n in layer.EnumerateArray())
                    {
                        if (n.GetArrayLength() < 3) continue;
                        var note = new Note { X = n[0].GetInt32(), Y = n[1].GetInt32(), Text = n[2].GetString() ?? "" };
                        if (n.GetArrayLength() > 3 && (n[3].ValueKind == JsonValueKind.True)) note.Dangerous = true;
                        if (n.GetArrayLength() > 4 && n[4].ValueKind == JsonValueKind.Number) note.Radius = n[4].GetInt32();
                        ParseNoteDisplay(note);
                        list.Add(note);
                    }
                }
            }
        }
        lock (_seenCache) _seenCache[path] = (fi.LastWriteTimeUtc, fi.Length, res);
        return res;
    }

    /// <summary>Port of overmap_ui::get_note_display_info: optional "S:" symbol and "cc;" colour prefixes.</summary>
    static void ParseNoteDisplay(Note note)
    {
        string s = note.Text;
        char sym = 'N'; int color = 11; int textStart = 0;
        bool setSym = false, setCol = false; int pos = 0;
        for (int i = 0; i < 2; i++)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == ':' || s[pos] == ';')) pos++;
            if (pos >= s.Length) break;
            int end = pos;
            while (end < s.Length && s[end] != ' ' && s[end] != ':' && s[end] != ';') end++;
            if (end >= s.Length) break;
            if (!setSym && s[end] == ':') { sym = s[end - 1]; textStart = end + 1; setSym = true; }
            else if (!setCol && s[end] == ';') { color = Colors.NoteColor(s.Substring(pos, end - pos)); textStart = end + 1; setCol = true; }
            pos = end + 1;
        }
        note.Sym = sym.ToString(); note.Color = color;
        note.Text = StripTags(s.Substring(Math.Min(textStart, s.Length)));
    }

    public static string StripTags(string s)
    {
        if (s.IndexOf('<') < 0) return s.Trim();
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '<')
            {
                int j = s.IndexOf('>', i);
                if (j > i && (s.AsSpan(i).StartsWith("<color") || s.AsSpan(i).StartsWith("</color"))) { i = j; continue; }
            }
            sb.Append(s[i]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Newest write time of the files the game rewrites on every save.</summary>
    public DateTime LastSaveTime()
    {
        DateTime t = DateTime.MinValue;
        foreach (var f in new[] { Path.Combine(Dir, "master.gsav"), CharacterSavePath() })
            if (f != null && File.Exists(f)) { var w = File.GetLastWriteTimeUtc(f); if (w > t) t = w; }
        return t;
    }
}
