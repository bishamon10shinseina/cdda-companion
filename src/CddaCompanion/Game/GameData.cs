using System.Collections.Concurrent;
using System.Text.Json;

namespace CddaCompanion.Game;

public sealed class VisionLevel
{
    public string Sym, Color, Name, LooksLike;
    public bool Blends;
}

/// <summary>How one overmap terrain id (including its rotation / line suffix) looks.</summary>
public sealed class OterLook
{
    public string Id, Sym, Color, Name, Vision;
    public bool Known = true;
    public string TypeId;          // id without _north / _ns suffix
    public int Dir = -1, Line = -1; // rotation (north 0 .. west 3) or line index (om_lines, n=1 e=2 s=4 w=8)
}

/// <summary>Overmap terrain type facts the tileset renderer needs (abstract types included, for looks_like).</summary>
public sealed class OterTypeInfo
{
    public string Id, ConnectGroup, Vision;
    public List<string> LooksLike = new();
    public bool Water, Linear, Rotatable;
}

/// <summary>
/// Game definitions this tool needs, read from the game install in mod load order:
/// overmap terrain (symbols, colours, vision levels) and which items count by charges.
/// </summary>
public sealed class GameData
{
    public readonly Dictionary<string, bool> CountByCharges = new(StringComparer.Ordinal);
    public readonly Dictionary<string, OterLook> Oters = new(StringComparer.Ordinal);
    public readonly Dictionary<string, OterTypeInfo> OterTypes = new(StringComparer.Ordinal);
    public readonly Dictionary<string, VisionLevel[]> Visions = new(StringComparer.Ordinal);
    public readonly List<string> DataDirs = new();
    public readonly List<string> MissingMods = new();
    public DisassemblyIndex Disassembly = new();
    public Codex Codex = new();
    public ItemCategories Categories = new();
    public readonly List<string> ModNames = new();   // display name per DataDirs entry
    public int FilesRead, FilesFailed;

    static readonly HashSet<string> ItemTypes = new(StringComparer.Ordinal) {
        "ITEM", "AMMO", "COMESTIBLE", "TOOL", "GENERIC", "ARMOR", "TOOL_ARMOR", "GUN", "GUNMOD", "MAGAZINE",
        "BOOK", "CONTAINER", "ENGINE", "WHEEL", "BIONIC_ITEM", "PET_ARMOR", "BATTERY", "TOOLMOD" };

    static readonly JsonDocumentOptions JOpts = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 };

    // ---------------------------------------------------------------- raw records
    sealed class ItemDef
    {
        public string Type, CopyFrom, Phase; public List<string> Subtypes; public bool? Stackable; public ItemDef Parent;
        public List<string> Flags, Extend, Delete;
    }
    sealed class OterDef
    {
        public string CopyFrom, Sym, Color, Name, Vision, ConnectGroup;
        public List<string> Flags, Extend, Delete, LooksLike;
        public bool Abstract;
        public OterDef Parent;
    }
    sealed class FileRecords
    {
        public readonly List<(string[] ids, ItemDef d)> Items = new();
        public readonly List<(string[] ids, OterDef d)> Oters = new();
        public readonly List<(string id, VisionLevel[] levels)> Visions = new();
        public readonly List<string[]> Rotations = new();
        public readonly DisRecords Dis = new();
        public readonly List<CodexRaw> Codex = new();
        public readonly List<(string id, string name)> Materials = new(), Categories = new(), AmmoTypes = new();
        public readonly List<(string id, int rank)> CategoryRanks = new();
        public int Mod;
        public bool Failed;
    }

    // ---------------------------------------------------------------- loading
    public static GameData Load(Install install, string worldDir)
    {
        var g = new GameData();
        g.ResolveDataDirs(install, worldDir);

        var files = new List<string>();
        var fileMod = new List<int>();
        for (int di = 0; di < g.DataDirs.Count; di++)
        {
            var dir = g.DataDirs[di];
            if (!Directory.Exists(dir)) continue;
            var list = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).ToList();
            list.Sort(StringComparer.Ordinal);
            files.AddRange(list);
            fileMod.AddRange(Enumerable.Repeat(di, list.Count));
        }
        var results = new FileRecords[files.Count];
        Parallel.For(0, files.Count, i => { results[i] = ReadFile(files[i]); results[i].Mod = fileMod[i]; });

        var items = new Dictionary<string, ItemDef>(StringComparer.Ordinal);
        var oters = new Dictionary<string, OterDef>(StringComparer.Ordinal);
        var rot = new Dictionary<string, (string[] tuple, int idx)>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            g.FilesRead++;
            if (r.Failed) { g.FilesFailed++; continue; }
            foreach (var (ids, d) in r.Items)
                foreach (var id in ids) { if (d.CopyFrom == id && items.TryGetValue(id, out var prev)) d.Parent = prev; items[id] = d; }
            foreach (var (ids, d) in r.Oters)
                foreach (var id in ids) { if (d.CopyFrom == id && oters.TryGetValue(id, out var prev)) d.Parent = prev; oters[id] = d; }
            foreach (var (id, levels) in r.Visions) g.Visions[id] = levels;
            foreach (var t in r.Rotations)
                for (int k = 0; k < t.Length; k++) rot[t[k]] = (t, k);
        }
        g.BuildItems(items);
        g.BuildOters(oters, rot);
        var dis = new DisRecords();
        foreach (var r in results)
        {
            if (r.Failed) continue;
            dis.Recipes.AddRange(r.Dis.Recipes); dis.Requirements.AddRange(r.Dis.Requirements);
            dis.Decons.AddRange(r.Dis.Decons); dis.Groups.AddRange(r.Dis.Groups);
        }
        try { g.Disassembly = dis.Build(g.Unrecoverable(items)); }
        catch (Exception ex) { Console.Error.WriteLine("Disassembly data: " + ex.Message); }
        try { g.Codex = Codex.Build(results.Where(r => !r.Failed).Select(r => (r.Mod, r.Codex, r.Materials, r.Categories)), g.ModNames); }
        catch (Exception ex) { Console.Error.WriteLine("Codex data: " + ex); }
        try
        {
            var ammoNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in results.Where(r => !r.Failed))
            {
                foreach (var (id, n) in r.AmmoTypes) ammoNames[id] = n;
                foreach (var (id, rank) in r.CategoryRanks) ranks[id] = rank;
            }
            g.Categories = ItemCategories.Build(g.Codex, ammoNames, ranks);
        }
        catch (Exception ex) { Console.Error.WriteLine("Item categories: " + ex); }
        return g;
    }

    void ResolveDataDirs(Install install, string worldDir)
    {
        string core = Path.Combine(install.DataDir ?? "", "json");
        if (Directory.Exists(core)) { DataDirs.Add(Path.GetFullPath(core)); ModNames.Add("Base game"); }
        var modPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var modTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        // the game's own mods first, then the player's (a launcher keeps those in its userdata)
        foreach (var root in install.ModRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var mi in EnumerateModInfos(root, 3))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(mi), JOpts);
                    var r = doc.RootElement;
                    var arr = r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().ToList() : new List<JsonElement> { r };
                    foreach (var e in arr)
                    {
                        if (Str(e, "type") != "MOD_INFO") continue;
                        var id = Str(e, "id"); if (id == null) continue;
                        var p = Str(e, "path");
                        var dir = p != null ? Path.Combine(Path.GetDirectoryName(mi), p) : Path.GetDirectoryName(mi);
                        if (Directory.Exists(dir) && !modPaths.ContainsKey(id))
                        {
                            modPaths[id] = Path.GetFullPath(dir);
                            modTitles[id] = Name(e) ?? id;
                        }
                    }
                }
                catch { }
            }
        }
        string modsFile = Path.Combine(worldDir, "mods.json");
        if (!File.Exists(modsFile)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(modsFile), JOpts);
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.String) continue;
                var id = m.GetString();
                if (modPaths.TryGetValue(id, out var path)) { if (!DataDirs.Contains(path)) { DataDirs.Add(path); ModNames.Add(modTitles[id]); } }
                else MissingMods.Add(id);
            }
        }
        catch { }
    }

    static IEnumerable<string> EnumerateModInfos(string root, int depth)
    {
        var f = Path.Combine(root, "modinfo.json");
        if (File.Exists(f)) yield return f;
        if (depth == 0) yield break;
        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(root).ToList(); } catch { yield break; }
        foreach (var d in subs)
            foreach (var x in EnumerateModInfos(d, depth - 1)) yield return x;
    }

    static FileRecords ReadFile(string path)
    {
        var res = new FileRecords();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllBytes(path), JOpts); }
        catch { res.Failed = true; return res; }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Array) foreach (var o in r.EnumerateArray()) ReadObject(o, res);
            else ReadObject(r, res);
        }
        return res;
    }

    static void ReadObject(JsonElement o, FileRecords res)
    {
        if (o.ValueKind != JsonValueKind.Object) return;
        var type = Str(o, "type");
        if (type == null) return;
        if (ItemTypes.Contains(type))
        {
            var d = new ItemDef { Type = type, CopyFrom = Str(o, "copy-from"), Phase = Str(o, "phase") };
            if (o.TryGetProperty("stackable", out var st) && (st.ValueKind == JsonValueKind.True || st.ValueKind == JsonValueKind.False)) d.Stackable = st.GetBoolean();
            d.Subtypes = StrList(o, "subtypes");
            d.Flags = StrList(o, "flags");
            if (o.TryGetProperty("extend", out var ex)) d.Extend = StrList(ex, "flags");
            if (o.TryGetProperty("delete", out var de)) d.Delete = StrList(de, "flags");
            var ids = Ids(o);
            if (ids.Length > 0) { res.Items.Add((ids, d)); res.Codex.Add(new CodexRaw("item", ids, o.Clone(), d.CopyFrom)); }
        }
        else if (type is "recipe" or "uncraft" or "requirement" or "furniture" or "terrain" or "item_group")
        {
            res.Dis.Collect(type, o);
            if (type is "furniture" or "terrain") { var ids = Ids(o); if (ids.Length > 0) res.Codex.Add(new CodexRaw(type, ids, o.Clone(), Str(o, "copy-from"))); }
        }
        else if (type is "vehicle" or "vehicle_part")
        {
            var ids = Ids(o);
            if (ids.Length > 0) res.Codex.Add(new CodexRaw(type, ids, o.Clone(), Str(o, "copy-from")));
        }
        else if (type == "material")
        {
            if (Str(o, "id") is { } mid) res.Materials.Add((mid, Name(o) ?? mid));
        }
        else if (type == "ITEM_CATEGORY")
        {
            if (Str(o, "id") is { } cid)
            {
                string n = null;
                foreach (var k in new[] { "name_header", "name", "name_noun" })
                    if (o.TryGetProperty(k, out var v)) { n = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Object && v.TryGetProperty("str", out var st2) ? st2.GetString() : null; if (n != null) break; }
                res.Categories.Add((cid, n ?? cid));
                if (o.TryGetProperty("sort_rank", out var sr) && sr.ValueKind == JsonValueKind.Number && sr.TryGetInt32(out var rank)) res.CategoryRanks.Add((cid, rank));
            }
        }
        else if (type == "ammunition_type")
        {
            if (Str(o, "id") is { } aid) res.AmmoTypes.Add((aid, Name(o) ?? aid));
        }
        else if (type == "overmap_terrain")
        {
            var d = new OterDef
            {
                CopyFrom = Str(o, "copy-from"), Sym = Str(o, "sym"), Color = Str(o, "color"), Name = Name(o),
                Vision = Str(o, "vision_levels"), Flags = StrList(o, "flags"), Abstract = o.TryGetProperty("abstract", out _),
                ConnectGroup = Str(o, "connect_group")
            };
            if (o.TryGetProperty("looks_like", out var ll))
                d.LooksLike = ll.ValueKind == JsonValueKind.String ? new List<string> { ll.GetString() } : StrList(o, "looks_like") ?? new List<string>();
            if (o.TryGetProperty("extend", out var ex)) d.Extend = StrList(ex, "flags");
            if (o.TryGetProperty("delete", out var de)) d.Delete = StrList(de, "flags");
            var ids = Ids(o);
            if (ids.Length > 0) res.Oters.Add((ids, d));
        }
        else if (type == "oter_vision")
        {
            var id = Str(o, "id");
            if (id == null || !o.TryGetProperty("levels", out var lv) || lv.ValueKind != JsonValueKind.Array) return;
            var levels = new List<VisionLevel>();
            foreach (var l in lv.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.Object) continue;
                levels.Add(new VisionLevel
                {
                    Sym = Str(l, "sym"), Color = Str(l, "color"), Name = Name(l), LooksLike = Str(l, "looks_like"),
                    Blends = l.TryGetProperty("blends_adjacent", out var b) && b.ValueKind == JsonValueKind.True
                });
            }
            res.Visions.Add((id, levels.ToArray()));
        }
        else if (type == "rotatable_symbol")
        {
            var t = StrList(o, "tuple");
            if (t != null && (t.Count == 2 || t.Count == 4)) res.Rotations.Add(t.ToArray());
        }
    }

    // ---------------------------------------------------------------- items: count by charges
    // Mirrors itype::count_by_charges(): stackable || ammo || (comestible && phase != solid)
    void BuildItems(Dictionary<string, ItemDef> defs)
    {
        T Inherit<T>(string id, Func<ItemDef, T> get) where T : class
        {
            if (!defs.TryGetValue(id, out var d)) return null;
            for (int i = 0; i < 32 && d != null; i++)
            {
                var v = get(d); if (v != null) return v;
                ItemDef next = d.Parent;
                if (next == null && d.CopyFrom != null && defs.TryGetValue(d.CopyFrom, out var n) && n != d) next = n;
                d = next;
            }
            return null;
        }
        foreach (var (id, d) in defs)
        {
            var subs = Inherit(id, x => x.Subtypes) ?? new List<string>();
            bool ammo = d.Type == "AMMO" || subs.Contains("AMMO");
            bool food = d.Type == "COMESTIBLE" || subs.Contains("COMESTIBLE");
            bool stack = (Inherit(id, x => x.Stackable.HasValue ? (object)x.Stackable.Value : null) as bool?) ?? false;
            string phase = Inherit(id, x => x.Phase) ?? "solid";
            CountByCharges[id] = stack || ammo || (food && phase != "solid");
        }
    }

    /// <summary>Items flagged UNRECOVERABLE (thread, glue, ...) never come back from disassembly.</summary>
    HashSet<string> Unrecoverable(Dictionary<string, ItemDef> defs)
    {
        HashSet<string> FlagsOf(ItemDef d, int depth)
        {
            if (d == null || depth > 32) return new HashSet<string>();
            ItemDef next = d.Parent;
            if (next == null && d.CopyFrom != null && defs.TryGetValue(d.CopyFrom, out var n) && n != d) next = n;
            var fl = d.Flags != null ? new HashSet<string>(d.Flags) : FlagsOf(next, depth + 1);
            if (d.Extend != null) fl.UnionWith(d.Extend);
            if (d.Delete != null) fl.ExceptWith(d.Delete);
            return fl;
        }
        var res = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, d) in defs) if (FlagsOf(d, 0).Contains("UNRECOVERABLE")) res.Add(id);
        return res;
    }

    // ---------------------------------------------------------------- overmap terrain
    static readonly string[] LineSyms = { "┼", "│", "─", "└", "│", "│", "┌", "├", "─", "┘", "─", "┴", "┐", "┤", "┬", "┼" };
    static readonly string[] LineSuffix = { "_isolated", "_end_south", "_end_west", "_ne", "_end_north", "_ns", "_es", "_nes",
                                            "_end_east", "_wn", "_ew", "_new", "_sw", "_nsw", "_esw", "_nesw" };
    static readonly string[] DirSuffix = { "_north", "_east", "_south", "_west" };

    void BuildOters(Dictionary<string, OterDef> defs, Dictionary<string, (string[] tuple, int idx)> rot)
    {
        OterDef Next(OterDef d)
        {
            if (d.Parent != null) return d.Parent;
            if (d.CopyFrom != null && defs.TryGetValue(d.CopyFrom, out var n) && n != d) return n;
            return null;
        }
        string Get(OterDef d, Func<OterDef, string> f)
        {
            for (int i = 0; i < 32 && d != null; i++) { var v = f(d); if (v != null) return v; d = Next(d); }
            return null;
        }
        HashSet<string> Flags(OterDef d, int depth)
        {
            if (d == null || depth > 32) return new HashSet<string>();
            var fl = d.Flags != null ? new HashSet<string>(d.Flags) : Flags(Next(d), depth + 1);
            if (d.Extend != null) fl.UnionWith(d.Extend);
            if (d.Delete != null) fl.ExceptWith(d.Delete);
            return fl;
        }
        string Rotate(string sym, int dir)
        {
            if (sym == null || !rot.TryGetValue(sym, out var r)) return sym;
            return r.tuple[(r.idx + dir) % r.tuple.Length];
        }
        // looks_like as the game reads it: its own list, else copy-from first, then what the parent looked like
        List<string> LooksLike(OterDef d, int depth)
        {
            if (d == null || depth > 32) return new List<string>();
            if (d.LooksLike != null) return d.LooksLike;
            if (d.CopyFrom == null) return new List<string>();
            var l = new List<string> { d.CopyFrom };
            l.AddRange(LooksLike(Next(d), depth + 1));
            return l;
        }
        foreach (var (id, d) in defs)
        {
            var tflags = Flags(d, 0);
            OterTypes[id] = new OterTypeInfo
            {
                Id = id, ConnectGroup = Get(d, x => x.ConnectGroup), Vision = Get(d, x => x.Vision) ?? "default",
                LooksLike = LooksLike(d, 0), Water = tflags.Contains("WATER"), Linear = tflags.Contains("LINEAR"),
                Rotatable = !tflags.Contains("LINEAR") && !tflags.Contains("NO_ROTATE")
            };
        }
        foreach (var (id, d) in defs)
        {
            if (d.Abstract) continue;
            var flags = Flags(d, 0);
            string sym = Get(d, x => x.Sym) ?? "?", color = Get(d, x => x.Color) ?? "light_gray";
            string name = Get(d, x => x.Name) ?? id, vision = Get(d, x => x.Vision) ?? "default";
            void Put(string key, string s, int dir, int line) => Oters[key] = new OterLook { Id = key, Sym = s, Color = color, Name = name, Vision = vision, TypeId = id, Dir = dir, Line = line };
            if (flags.Contains("LINEAR"))
                for (int k = 0; k < LineSuffix.Length; k++) Put(id + LineSuffix[k], LineSyms[k], -1, k);
            else if (!flags.Contains("NO_ROTATE"))
                for (int k = 0; k < 4; k++) Put(id + DirSuffix[k], Rotate(sym, k), k, -1);
            Put(id, sym, flags.Contains("NO_ROTATE") || flags.Contains("LINEAR") ? -1 : 0, -1);
        }
    }

    public OterLook Look(string oterId)
    {
        if (oterId != null && Oters.TryGetValue(oterId, out var l)) return l;
        return new OterLook { Id = oterId, Sym = "?", Color = "pink", Name = oterId + " (unknown)", Vision = "default", Known = false };
    }

    // ---------------------------------------------------------------- JSON helpers
    static string Str(JsonElement o, string k) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static List<string> StrList(JsonElement o, string k)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var l = new List<string>();
        foreach (var x in v.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) l.Add(x.GetString());
        return l;
    }

    static string[] Ids(JsonElement o)
    {
        var l = new List<string>();
        foreach (var key in new[] { "id", "abstract" })
        {
            if (!o.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) l.Add(v.GetString());
            else if (v.ValueKind == JsonValueKind.Array)
                foreach (var x in v.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) l.Add(x.GetString());
        }
        return l.ToArray();
    }

    static string Name(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("name", out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Object)
        {
            if (v.TryGetProperty("str", out var s) && s.ValueKind == JsonValueKind.String) return s.GetString();
            if (v.TryGetProperty("str_sp", out var sp) && sp.ValueKind == JsonValueKind.String) return sp.GetString();
        }
        return null;
    }
}
