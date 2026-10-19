using System.Buffers.Binary;
using System.Text.Json;

namespace CddaCompanion.Game;

/// <summary>A tileset folder under the game's gfx/ (found by its tileset.txt, like options.cpp build_tilesets_list).</summary>
public sealed class TilesetInfo
{
    public string Name, View, Dir, Json;
    public bool Iso => Name.EndsWith("_iso", StringComparison.Ordinal);
}

/// <summary>One sprite sheet ("tiles-new" entry). Sprites are numbered across all sheets in order.</summary>
public sealed class SpriteSheet
{
    public string File;
    public int SpriteW, SpriteH, OffsetX, OffsetY, Columns, First, Count;
    public double PixelScale = 1;
}

public sealed class TileType
{
    public List<(int weight, int[] sprites)> Fg = new(), Bg = new();
    public bool Multitile, Rotates;
    public HashSet<string> Subtiles;
    public int Sheet = -1;     // sheet whose offsets apply (the one the entry was defined in)
}

/// <summary>
/// A loaded tileset: tile_config.json parsed the way cata_tiles.cpp loads it (fg/bg sprite lists, rotations,
/// weighted variants, multitiles with additional_tiles stored as "id_subtile", season suffixes).
/// The PNGs themselves are sent to the browser untouched; only their sizes are read here.
/// </summary>
public sealed class Tileset
{
    public TilesetInfo Info;
    public int TileW, TileH;
    public readonly List<SpriteSheet> Sheets = new();
    public readonly Dictionary<string, TileType> Tiles = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------- discovery
    public static List<TilesetInfo> List(string gameDir)
    {
        var list = new List<TilesetInfo>();
        foreach (var root in new[] { Path.Combine(gameDir, "gfx") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var txt = Path.Combine(dir, "tileset.txt");
                if (!File.Exists(txt)) continue;
                string name = null, view = null, json = "tile_config.json";
                foreach (var raw in File.ReadAllLines(txt))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int c = line.IndexOf(':');
                    if (c < 0) continue;
                    var key = line[..c].Trim(); var val = line[(c + 1)..].Trim();
                    if (key.Contains("NAME")) name = val;
                    else if (key.Contains("VIEW")) view = val;
                    else if (key.StartsWith("JSON", StringComparison.Ordinal)) json = val;
                }
                if (string.IsNullOrEmpty(name) || list.Any(t => t.Name == name)) continue;
                if (!File.Exists(Path.Combine(dir, json))) continue;
                list.Add(new TilesetInfo { Name = name, View = string.IsNullOrEmpty(view) ? name : view, Dir = dir, Json = json });
            }
        }
        return list;
    }

    /// <summary>
    /// The overmap tileset the game uses: config/options.json USE_TILES + USE_OVERMAP_TILES + OVERMAP_TILES
    /// (defaults true, true, "Larwick Overmap"). Null means the game shows the ASCII overmap.
    /// </summary>
    public static string GameSetting(string gameDir)
    {
        bool useTiles = true, useOm = true; string om = "Larwick Overmap";
        foreach (var path in new[] {
            Path.Combine(gameDir, "config", "options.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cataclysm-dda", "config", "options.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cataclysm-dda", "config", "options.json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var o in doc.RootElement.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("name", out var n) || !o.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String) continue;
                    switch (n.GetString())
                    {
                        case "USE_TILES": useTiles = v.GetString() == "true"; break;
                        case "USE_OVERMAP_TILES": useOm = v.GetString() == "true"; break;
                        case "OVERMAP_TILES": om = v.GetString(); break;
                    }
                }
                break;
            }
            catch { }
        }
        return useTiles && useOm ? om : null;
    }

    // ---------------------------------------------------------------- loading
    static readonly Dictionary<string, Tileset> Cache = new(StringComparer.Ordinal);

    public static Tileset Load(TilesetInfo info)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(info.Dir, out var t)) return t;
            t = new Tileset { Info = info };
            t.Read();
            Cache[info.Dir] = t;
            return t;
        }
    }

    static (int w, int h) PngSize(string path)
    {
        Span<byte> head = stackalloc byte[24];
        using var fs = File.OpenRead(path);
        if (fs.Read(head) < 24 || head[1] != (byte)'P' || head[2] != (byte)'N' || head[3] != (byte)'G') throw new InvalidDataException(Path.GetFileName(path) + " is not a PNG");
        return (BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..]));
    }

    void Read()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Info.Dir, Info.Json)),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
        var root = doc.RootElement;
        TileW = 16; TileH = 16;
        if (root.TryGetProperty("tile_info", out var ti) && ti.ValueKind == JsonValueKind.Array && ti.GetArrayLength() > 0)
        {
            if (ti[0].TryGetProperty("width", out var w)) TileW = w.GetInt32();
            if (ti[0].TryGetProperty("height", out var h)) TileH = h.GetInt32();
        }
        int offset = 0;
        if (root.TryGetProperty("tiles-new", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                var file = part.GetProperty("file").GetString();
                var sheet = new SpriteSheet
                {
                    File = file,
                    SpriteW = Int(part, "sprite_width", TileW), SpriteH = Int(part, "sprite_height", TileH),
                    OffsetX = Int(part, "sprite_offset_x", 0), OffsetY = Int(part, "sprite_offset_y", 0), First = offset,
                    PixelScale = part.TryGetProperty("pixelscale", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetDouble() : 1
                };
                var (iw, ih) = PngSize(Path.Combine(Info.Dir, file));
                sheet.Columns = Math.Max(1, iw / sheet.SpriteW);
                sheet.Count = sheet.Columns * (ih / sheet.SpriteH);
                Sheets.Add(sheet);
                if (part.TryGetProperty("tiles", out var tiles) && tiles.ValueKind == JsonValueKind.Array) ReadTiles(tiles, Sheets.Count - 1);
                offset += sheet.Count;
            }
        }
        else
        {
            // old single-image format: tileset.txt TILESET image + "tiles"
            var img = Directory.EnumerateFiles(Info.Dir, "*.png").FirstOrDefault();
            if (img != null)
            {
                var (iw, ih) = PngSize(img);
                var sheet = new SpriteSheet { File = Path.GetFileName(img), SpriteW = TileW, SpriteH = TileH, Columns = Math.Max(1, iw / TileW) };
                sheet.Count = sheet.Columns * (ih / TileH);
                Sheets.Add(sheet);
            }
            if (root.TryGetProperty("tiles", out var tiles) && tiles.ValueKind == JsonValueKind.Array) ReadTiles(tiles, 0);
        }
    }

    static int Int(JsonElement o, string k, int def) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

    void ReadTiles(JsonElement tiles, int sheet)
    {
        foreach (var e in tiles.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idEl)) continue;
            var ids = idEl.ValueKind == JsonValueKind.String ? new[] { idEl.GetString() }
                : idEl.ValueKind == JsonValueKind.Array ? idEl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray() : Array.Empty<string>();
            bool multi = e.TryGetProperty("multitile", out var m) && m.ValueKind == JsonValueKind.True;
            bool rot = e.TryGetProperty("rotates", out var r) ? r.ValueKind == JsonValueKind.True : multi;
            foreach (var id in ids)
            {
                var t = new TileType { Fg = Sprites(e, "fg"), Bg = Sprites(e, "bg"), Multitile = multi, Rotates = rot, Sheet = sheet };
                if (multi && e.TryGetProperty("additional_tiles", out var add) && add.ValueKind == JsonValueKind.Array)
                {
                    t.Subtiles = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var sub in add.EnumerateArray())
                    {
                        if (!sub.TryGetProperty("id", out var sid) || sid.ValueKind != JsonValueKind.String) continue;
                        t.Subtiles.Add(sid.GetString());
                        Tiles[id + "_" + sid.GetString()] = new TileType { Fg = Sprites(sub, "fg"), Bg = Sprites(sub, "bg"), Rotates = true, Sheet = sheet };
                    }
                }
                Tiles[id] = t;
            }
        }
    }

    /// <summary>fg/bg: an int, a list of ints (one variant with rotations), or weighted variants.</summary>
    static List<(int, int[])> Sprites(JsonElement e, string key)
    {
        var l = new List<(int, int[])>();
        if (!e.TryGetProperty(key, out var v)) return l;
        if (v.ValueKind == JsonValueKind.Number) { int n = v.GetInt32(); if (n >= 0) l.Add((1, new[] { n })); }
        else if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
        {
            if (v[0].ValueKind == JsonValueKind.Number)
                l.Add((1, v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).Where(x => x >= 0).ToArray()));
            else
                foreach (var o in v.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object) continue;
                    int w = o.TryGetProperty("weight", out var wv) && wv.ValueKind == JsonValueKind.Number ? wv.GetInt32() : 1;
                    if (!o.TryGetProperty("sprite", out var sp)) continue;
                    int[] arr = sp.ValueKind == JsonValueKind.Number ? new[] { sp.GetInt32() }
                        : sp.ValueKind == JsonValueKind.Array ? sp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).Where(x => x >= 0).ToArray() : Array.Empty<int>();
                    if (w >= 0 && arr.Length > 0) l.Add((w, arr));
                }
        }
        l.RemoveAll(x => x.Item2.Length == 0);
        return l;
    }

    public TileType Find(string id, string season)
    {
        if (season != null && Tiles.TryGetValue(id + "_season_" + season, out var s)) return s;
        return Tiles.TryGetValue(id, out var t) ? t : null;
    }
}

/// <summary>
/// Which sprites one overmap tile shows: the lookup and fallback order of cata_tiles::draw_from_id_string_internal
/// and find_tile_looks_like for OVERMAP_TERRAIN / OVERMAP_VISION_LEVEL, rotation and subtile from draw_om, and the
/// same position-seeded variant pick, so forests get the same trees as in the game.
/// </summary>
public sealed class TileResolver
{
    static readonly string[] MultitileKeys = { "center", "corner", "edge", "t_connection", "end_piece", "unconnected", "open", "broken" };
    const int Center = 0, Corner = 1, Edge = 2, TConn = 3, EndPiece = 4, Unconnected = 5;

    readonly Tileset _ts;
    readonly GameData _game;
    readonly string _season;
    public TileResolver(Tileset ts, GameData game, string season) { _ts = ts; _game = game; _season = season?.ToLowerInvariant(); }

    (TileType t, string id)? LooksLike(string id, bool vision, int jumps)
    {
        if (string.IsNullOrEmpty(id) || jumps <= 0) return null;
        var t = _ts.Find(id, _season);
        if (t != null) return (t, id);
        if (vision)
        {
            int k = id.IndexOf('$');
            if (k > 0 && _game.Visions.TryGetValue(id[..k], out var levels))
            {
                int idx = id[(k + 1)..] switch { "vague" => 0, "outlines" => 1, "details" => 2, _ => -1 };
                if (idx >= 0 && idx < levels.Length && !string.IsNullOrEmpty(levels[idx].LooksLike))
                    return LooksLike(levels[idx].LooksLike, false, jumps - 1);
            }
            return null;
        }
        if (_game.OterTypes.TryGetValue(id, out var type))
        {
            int limit = jumps;
            foreach (var ll in type.LooksLike)
            {
                var r = LooksLike(ll, false, limit - 1);
                if (r != null) return r;
                if (--limit <= 0) return null;
            }
        }
        return null;
    }

    static uint Rot32(uint x, int k) => (x << k) | (x >> (32 - k));

    static uint LocRand(uint seed)
    {
        uint a = seed, b = unchecked(0u - seed), c = unchecked(seed * seed);
        c ^= b; c -= Rot32(b, 14);
        a ^= c; a -= Rot32(c, 11);
        b ^= a; b -= Rot32(a, 25);
        c ^= b; c -= Rot32(b, 16);
        a ^= c; a -= Rot32(c, 4);
        b ^= a; b -= Rot32(a, 14);
        c ^= b; c -= Rot32(b, 24);
        return c;
    }

    static int[] Pick(List<(int weight, int[] sprites)> list, uint rand)
    {
        if (list.Count == 0) return null;
        if (list.Count == 1) return list[0].sprites;
        int total = list.Sum(x => x.weight);
        if (total <= 0) return null;
        int picked = (int)(rand % (uint)total) + 1, acc = 0;
        foreach (var (w, s) in list) { acc += w; if (acc >= picked) return s; }
        return list[^1].sprites;
    }

    /// <summary>(sprite, quarter turns or -1 for a horizontal flip) for one layer; sprite -1 when empty.</summary>
    static (int sprite, int rot) Layer(List<(int, int[])> list, uint rand, bool fg, int rota)
    {
        var s = Pick(list, rand);
        if (s == null || s.Length == 0) return (-1, 0);
        if (s.Length == 1) return (s[0], fg ? (rota == -1 ? -1 : ((rota % 4) + 4) % 4) : 0);
        return (s[((rota % s.Length) + s.Length) % s.Length], 0);
    }

    /// <summary>The tile to draw, or null when the tileset has nothing (the map then shows the ASCII glyph).</summary>
    public (int fg, int fgRot, int bg, int bgRot)? Resolve(string id, bool vision, int rota, int subtile, int ax, int ay)
    {
        var found = LooksLike(id, vision, 10);
        if (found == null) return null;
        var (tile, foundId) = found.Value;
        if (subtile >= 0 && tile.Multitile && tile.Subtiles != null && tile.Subtiles.Contains(MultitileKeys[subtile]))
        {
            var sub = _ts.Find(foundId + "_" + MultitileKeys[subtile], _season);
            if (sub != null) tile = sub;
        }
        if (!tile.Rotates) rota = 0;
        uint rand = tile.Fg.Count > 1 || tile.Bg.Count > 1 ? LocRand(unchecked((uint)(ax + ay * 65536))) : 0;
        var (bg, bgRot) = Layer(tile.Bg, rand, false, rota);
        var (fg, fgRot) = Layer(tile.Fg, rand, true, rota);
        if (fg < 0 && bg < 0) return null;
        return (fg, fgRot, bg, bgRot);
    }

    // ---------------------------------------------------------------- rotation / subtile (cata_tiles.cpp)
    /// <summary>
    /// get_rotation_and_subtile(val, rot_to = -1): val bits are south 1, east 2, west 4, north 8 (draw_om order).
    /// With rot_to = -1 every "rotate towards" bit is set, so edges and end pieces get the "both sides" rotations.
    /// </summary>
    public static (int rota, int subtile) Connected(int val)
    {
        switch (val)
        {
            case 0: return (0, Unconnected);           // get_rotation_unconnected(-1) matches no case: 0
            case 15: return (0, Center);
            case 8: return (2 + 4 * 2, EndPiece);
            case 4: return (3 + 4 * 2, EndPiece);
            case 2: return (1 + 4 * 2, EndPiece);
            case 1: return (4 * 2, EndPiece);
            case 9: return (2 * 2, Edge);
            case 6: return (1 + 2 * 2, Edge);
            case 12: return (2, Corner);
            case 10: return (1, Corner);
            case 3: return (0, Corner);
            case 5: return (3, Corner);
            case 14: return (2, TConn);
            case 11: return (1, TConn);
            case 7: return (0, TConn);
            case 13: return (3, TConn);
        }
        return (0, -1);
    }

    // om_lines::all: subtile and rotation per line index (n=1 e=2 s=4 w=8)
    static readonly (int sub, int rot)[] Lines = {
        (Unconnected, 0), (EndPiece, 2), (EndPiece, 1), (Corner, 1), (EndPiece, 0), (Edge, 0), (Corner, 0), (TConn, 1),
        (EndPiece, 3), (Corner, 2), (Edge, 1), (TConn, 2), (Corner, 3), (TConn, 3), (TConn, 0), (Center, 0) };

    /// <summary>oter_t::get_rotation_and_subtile for terrain without connections.</summary>
    public static (int rota, int subtile) Own(OterLook look, OterTypeInfo type)
    {
        if (type != null && type.Linear) { var l = Lines[look.Line is >= 0 and < 16 ? look.Line : 0]; return (l.rot, l.sub); }
        if (type != null && type.Rotatable && look.Dir >= 0) return ((4 - look.Dir) % 4, -1);
        return (0, -1);
    }
}
