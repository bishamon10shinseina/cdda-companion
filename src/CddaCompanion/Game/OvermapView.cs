namespace CddaCompanion.Game;

public sealed class Style
{
    public string Sym, Name, Id;
    public int Fg, Bg;
}

public sealed class OvermapLayerView
{
    public int Ox, Oy, Z;
    public bool Exists;
    public List<Style> Styles = new();
    public ushort[] Grid;
    public List<Note> Notes = new();
    public List<City> Cities = new();
    public int Unknown;
    // tileset mode: per tile an index into Tiles plus one (0 = no sprite, show the glyph)
    public ushort[] TileGrid;
    public List<(int fg, int fgRot, int bg, int bgRot)> Tiles;
}

/// <summary>Turns terrain + what the character has seen into what the in-game ASCII overmap shows (overmap_ui.cpp).</summary>
public static class OvermapView
{
    const int N = OvermapTerrain.Size;

    public static OvermapLayerView Build(World world, GameData game, int ox, int oy, int z, bool full, TileResolver tiles = null)
    {
        var view = new OvermapLayerView { Ox = ox, Oy = oy, Z = z };
        int li = z + OvermapTerrain.Depth;
        if (li < 0 || li >= OvermapTerrain.Layers) return view;
        var ter = world.ReadTerrain(ox, oy);
        if (ter == null) return view;
        view.Exists = true;
        var seen = world.ReadSeen(ox, oy);
        var grid = ter.Grid[li];
        var vis = seen.Vision[li];
        var looks = new OterLook[ter.Ids.Length];
        for (int i = 0; i < looks.Length; i++) { looks[i] = game.Look(ter.Ids[i]); if (!looks[i].Known) view.Unknown++; }

        var styleIndex = new Dictionary<(string, int, int, string, string), ushort>();
        ushort StyleOf(string sym, string color, string name, string id)
        {
            var (fg, bg) = Colors.Parse(color);
            var key = (sym, fg, bg, name, id);
            if (!styleIndex.TryGetValue(key, out var s))
            {
                s = (ushort)view.Styles.Count;
                styleIndex[key] = s;
                view.Styles.Add(new Style { Sym = sym, Fg = fg, Bg = bg, Name = name, Id = id });
            }
            return s;
        }

        // the look of a tile at a vision level; null when it blends with its neighbours
        (string sym, string color, string name)? LookAt(int idx, int v)
        {
            var l = looks[grid[idx]];
            if (v >= 4) return (l.Sym, l.Color, l.Name);
            if (game.Visions.TryGetValue(l.Vision, out var levels) && v - 1 < levels.Length && v >= 1)
            {
                var lv = levels[v - 1];
                if (lv.Blends) return null;
                return (lv.Sym ?? l.Sym, lv.Color ?? l.Color, lv.Name ?? l.Name);
            }
            return (l.Sym, l.Color, l.Name);
        }

        var unexplored = game.Look("unexplored");
        string unSym = unexplored.Known ? unexplored.Sym : "#", unCol = unexplored.Known ? unexplored.Color : "dark_gray";
        ushort unseenStyle = StyleOf(unSym, unCol, "unexplored", "");

        view.Grid = new ushort[N * N];
        for (int i = 0; i < N * N; i++)
        {
            int v = full ? 4 : vis[i];
            if (v == 0) { view.Grid[i] = unseenStyle; continue; }
            var look = LookAt(i, v);
            if (look == null) look = Blend(i, v, vis, full, LookAt);
            if (look == null) { view.Grid[i] = unseenStyle; continue; }
            var (s, c, n) = look.Value;
            view.Grid[i] = StyleOf(s, c, n, v >= 4 ? ter.Ids[grid[i]] : "");
        }
        view.Notes = seen.Notes[li];
        if (z == 0) view.Cities = ter.Cities;
        if (tiles != null) BuildTiles(view, world, game, ter, grid, vis, looks, li, ox, oy, full, tiles);
        return view;
    }

    static readonly string[] LevelNames = { "unseen", "vague", "outlines", "details", "full" };

    /// <summary>Sprites per tile, the way cata_tiles::draw_om picks them.</summary>
    static void BuildTiles(OvermapLayerView view, World world, GameData game, OvermapTerrain ter, ushort[] grid, byte[] vis,
        OterLook[] looks, int li, int ox, int oy, bool full, TileResolver res)
    {
        bool Blends(int idx, int v)
        {
            if (v >= 4 || v < 1) return false;
            var l = looks[grid[idx]];
            return game.Visions.TryGetValue(l.Vision, out var lv) && v - 1 < lv.Length && lv[v - 1].Blends;
        }
        int Vis(int i) => full ? 4 : vis[i];
        // what each tile shows: its own terrain, or (when its vision level blends) the most common neighbour
        var shown = new int[N * N];
        for (int i = 0; i < N * N; i++)
        {
            int v = Vis(i);
            shown[i] = grid[i];
            if (v == 0 || !Blends(i, v)) continue;
            int x = i % N, y = i / N, best = -1;
            for (int r = 1; r <= 2 && best < 0; r++)
            {
                var counts = new Dictionary<int, int>();
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= N || ny >= N) continue;
                        int ni = ny * N + nx;
                        if (!full && vis[ni] == 0) continue;
                        if (Blends(ni, v)) continue;
                        counts[grid[ni]] = counts.GetValueOrDefault(grid[ni]) + 1;
                    }
                if (counts.Count > 0) best = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            }
            if (best >= 0) shown[i] = best;
        }

        // neighbours across the overmap edge come from the adjacent overmap (raw terrain, like overmap_buffer.ter)
        var edges = new Dictionary<(int, int), (OvermapTerrain t, OterLook[] l)>();
        OterTypeInfo TypeAt(int x, int y)
        {
            OterLook l;
            if (x >= 0 && y >= 0 && x < N && y < N) l = looks[shown[y * N + x]];
            else
            {
                int dox = x < 0 ? -1 : x >= N ? 1 : 0, doy = y < 0 ? -1 : y >= N ? 1 : 0;
                if (!edges.TryGetValue((dox, doy), out var e))
                {
                    var t = world.ReadTerrain(ox + dox, oy + doy);
                    e = (t, t == null ? null : t.Ids.Select(game.Look).ToArray());
                    edges[(dox, doy)] = e;
                }
                if (e.t == null) return null;
                int lx = x - dox * N, ly = y - doy * N;
                l = e.l[e.t.Grid[li][ly * N + lx]];
            }
            return l.TypeId != null && game.OterTypes.TryGetValue(l.TypeId, out var ti) ? ti : null;
        }

        view.TileGrid = new ushort[N * N];
        view.Tiles = new List<(int, int, int, int)>();
        var index = new Dictionary<(int, int, int, int), ushort>();
        int ax0 = ox * N, ay0 = oy * N;
        for (int i = 0; i < N * N; i++)
        {
            int x = i % N, y = i / N, v = Vis(i);
            string id; bool vision = false; int rota = 0, sub = -1;
            if (v == 0) id = "unknown_terrain";
            else
            {
                var look = looks[shown[i]];
                game.OterTypes.TryGetValue(look.TypeId ?? "", out var type);
                id = look.TypeId ?? look.Id;
                if (v < 4 && type != null && game.Visions.TryGetValue(type.Vision, out var levels) && v - 1 < levels.Length)
                { id = type.Vision + "$" + LevelNames[v]; vision = true; }
                if (type != null && type.ConnectGroup != null)
                {
                    int val = 0;
                    var nb = new[] { TypeAt(x, y + 1), TypeAt(x + 1, y), TypeAt(x - 1, y), TypeAt(x, y - 1) };
                    for (int k = 0; k < 4; k++) if (nb[k] != null && nb[k].ConnectGroup == type.ConnectGroup) val |= 1 << k;
                    (rota, sub) = TileResolver.Connected(val);
                }
                else if (type != null && type.Water)
                {
                    int val = 0;
                    var nb = new[] { TypeAt(x, y + 1), TypeAt(x + 1, y), TypeAt(x - 1, y), TypeAt(x, y - 1) };
                    for (int k = 0; k < 4; k++) if (nb[k] != null && nb[k].Water) val |= 1 << k;
                    (rota, sub) = TileResolver.Connected(val);
                }
                else (rota, sub) = TileResolver.Own(look, type);
            }
            var r = res.Resolve(id, vision, rota, sub, ax0 + x, ay0 + y);
            if (r == null) continue;
            if (!index.TryGetValue(r.Value, out var ti2))
            {
                view.Tiles.Add(r.Value);
                ti2 = (ushort)view.Tiles.Count;
                index[r.Value] = ti2;
            }
            view.TileGrid[i] = ti2;
        }
    }

    // Port of oter_vision::get_blended_omt_info: most common non-blending visible neighbour, radius 1 then 2.
    static (string, string, string)? Blend(int idx, int v, byte[] vis, bool full,
        Func<int, int, (string, string, string)?> lookAt)
    {
        int x = idx % N, y = idx / N;
        for (int r = 1; r <= 2; r++)
        {
            var counts = new Dictionary<(string, string, string), int>();
            (string, string, string)? first = null;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= N || ny >= N) continue;
                    int ni = ny * N + nx;
                    if (!full && vis[ni] == 0) continue;
                    var l = lookAt(ni, v);
                    if (l == null) continue;
                    counts[l.Value] = counts.TryGetValue(l.Value, out var k) ? k + 1 : 1;
                    first ??= l;
                }
            if (counts.Count > 0)
                return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Item3, StringComparer.Ordinal).First().Key;
        }
        return null;
    }
}
