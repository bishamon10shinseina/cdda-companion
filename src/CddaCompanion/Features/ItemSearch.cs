using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CddaCompanion.Features;

public sealed class Hit
{
    public string Kind, Id, Dir, InGame, Square, Detail, File;
    public long Qty;
    public int? Dist;
    public int Z, Ox, Oy;    // absolute overmap-tile coordinates
    public bool Crafted;     // item carries its own recorded components (disassembly gives exactly those)
    public string ComponentOf;
    public string Parent;    // the item it sits in directly (a magazine, a gun, a bag), if any
    public string Where;     // "on ground", "in 'Truck' cargo", ...
    public string[] Trail;   // containers from outermost to innermost
}

public sealed class IdCount
{
    public string Id, Kind;
    public int Places;
    public long TotalQty;
    public int? Nearest;
}

public sealed class SearchResult
{
    public List<Hit> Hits = new();
    public List<string> Errors = new();
    public long FilesTotal, FilesParsed, Bytes;
    public double Seconds;
}

/// <summary>
/// Item / furniture / terrain / vehicle search over extracted map chunks.
/// Grew out of an earlier script-based finder, with two fixes: matching is case-insensitive in the
/// byte prefilter too, and crafting components inside crafted items are labelled as such.
/// </summary>
public static class ItemSearch
{
    static readonly string[] Handled = { "terrain", "furniture", "items", "traps", "fields", "spawns", "vehicles", "coordinates" };

    sealed class Ctx
    {
        public int Sx, Sy, Z; public string File;
        public ConcurrentBag<Hit> Bag; public Func<string, bool> Match;
        public Dictionary<string, bool> Cbc;
    }

    static int Fdiv(int a, int b) => (int)Math.Floor((double)a / b);

    static Hit Add(Ctx c, string kind, string id, int? x, int? y, string detail, long qty = 1)
    {
        int ox = Fdiv(c.Sx, 2), oy = Fdiv(c.Sy, 2);
        int omx = Fdiv(ox, 180), omy = Fdiv(oy, 180);
        string sq = x.HasValue
            ? (c.Sx * 12 + x.Value) + ", " + (c.Sy * 12 + y.Value)
            : (c.Sx * 12) + ".." + (c.Sx * 12 + 11) + ", " + (c.Sy * 12) + ".." + (c.Sy * 12 + 11);
        var hit = new Hit
        {
            Kind = kind, Id = id, Qty = qty, Z = c.Z, Ox = ox, Oy = oy, Square = sq, Detail = detail ?? "", File = c.File,
            InGame = omx + "'" + (ox - omx * 180) + ", " + omy + "'" + (oy - omy * 180)
        };
        c.Bag.Add(hit);
        return hit;
    }

    static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    static int? Int(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;
    static JsonElement? Prop(JsonElement o, string n) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) ? v : null;

    // Item tree: the item itself plus everything nested in its pockets.
    static void Item(Ctx c, JsonElement n, int? x, int? y, string where, List<string> trail, string componentOf)
    {
        if (n.ValueKind == JsonValueKind.Array) { foreach (var e in n.EnumerateArray()) Item(c, e, x, y, where, trail, componentOf); return; }
        if (n.ValueKind != JsonValueKind.Object) return;
        string tid = null;
        var t = Prop(n, "typeid");
        if (t.HasValue) tid = Str(t.Value);
        if (tid != null && c.Match(tid))
        {
            string d = where;
            if (componentOf != null) d = (d + " — crafting component of " + componentOf + " (only via disassembly)").Trim();
            else if (trail.Count > 0) d = (d + " inside " + string.Join(" > ", trail)).Trim();
            long charges = 0;
            var ch = Prop(n, "charges");
            if (ch.HasValue && ch.Value.ValueKind == JsonValueKind.Number) ch.Value.TryGetInt64(out charges);
            bool cbc;
            if (c.Cbc == null || !c.Cbc.TryGetValue(tid, out cbc)) cbc = charges > 0;   // fallback guess
            if (!cbc && charges > 0) d += " [" + charges + " charges]";
            var h = Add(c, "item", tid, x, y, d, cbc ? Math.Max(charges, 1) : 1);
            h.Crafted = n.TryGetProperty("components", out var cps) && cps.ValueKind == JsonValueKind.Array && cps.GetArrayLength() > 0;
            h.ComponentOf = componentOf;
            if (trail.Count > 0) { h.Parent = trail[^1]; h.Trail = trail.ToArray(); }
            h.Where = where;
        }
        if (tid != null) trail.Add(tid);
        foreach (var p in n.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object && p.Value.ValueKind != JsonValueKind.Array) continue;
            if (tid != null && p.NameEquals("components"))
                Item(c, p.Value, x, y, where, new List<string>(), componentOf ?? tid);
            else
                Item(c, p.Value, x, y, where, trail, componentOf);
        }
        if (tid != null) trail.RemoveAt(trail.Count - 1);
    }

    static void Any(Ctx c, JsonElement n, string key)
    {
        switch (n.ValueKind)
        {
            case JsonValueKind.String: { var s = n.GetString(); if (c.Match(s)) Add(c, "other (" + key + ")", s, null, null, ""); break; }
            case JsonValueKind.Array: foreach (var e in n.EnumerateArray()) Any(c, e, key); break;
            case JsonValueKind.Object: foreach (var p in n.EnumerateObject()) Any(c, p.Value, key); break;
        }
    }

    static void Submap(Ctx c, JsonElement sub)
    {
        var co = Prop(sub, "coordinates");
        if (!co.HasValue || co.Value.ValueKind != JsonValueKind.Array || co.Value.GetArrayLength() < 3) return;
        c.Sx = co.Value[0].GetInt32(); c.Sy = co.Value[1].GetInt32(); c.Z = co.Value[2].GetInt32();

        // terrain: run-length encoded, 12x12, row by row; summarised per submap
        var ter = Prop(sub, "terrain");
        if (ter.HasValue && ter.Value.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            var seen = new Dictionary<string, int[]>();
            foreach (var e in ter.Value.EnumerateArray())
            {
                string name; int n = 1;
                if (e.ValueKind == JsonValueKind.Array) { name = Str(e[0]); n = Int(e[1]) ?? 1; } else name = Str(e);
                if (name != null && c.Match(name))
                {
                    if (!seen.TryGetValue(name, out var fc)) { fc = new[] { idx, 0 }; seen[name] = fc; }
                    fc[1] += n;
                }
                idx += n;
            }
            foreach (var kv in seen)
                Add(c, "terrain", kv.Key, kv.Value[0] % 12, kv.Value[0] / 12, "tiles in this submap (first shown)", kv.Value[1]);
        }

        // furniture / traps: [x, y, id, ...]
        foreach (var (key, kind) in new[] { ("furniture", "furniture"), ("traps", "trap") })
        {
            var arr = Prop(sub, key);
            if (!arr.HasValue || arr.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in arr.Value.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 3) continue;
                var s = Str(e[2]);
                if (s != null && c.Match(s)) Add(c, kind, s, Int(e[0]), Int(e[1]), "");
            }
        }

        // items: flat x, y, [items]
        var items = Prop(sub, "items");
        if (items.HasValue && items.Value.ValueKind == JsonValueKind.Array)
        {
            int len = items.Value.GetArrayLength();
            for (int k = 0; k + 2 < len; k += 3)
                Item(c, items.Value[k + 2], Int(items.Value[k]), Int(items.Value[k + 1]), "on ground", new List<string>(), null);
        }

        // fields: flat x, y, [type, intensity, age, ...]
        var fields = Prop(sub, "fields");
        if (fields.HasValue && fields.Value.ValueKind == JsonValueKind.Array)
        {
            int len = fields.Value.GetArrayLength();
            for (int k = 0; k + 2 < len; k += 3)
            {
                var fl = fields.Value[k + 2];
                if (fl.ValueKind != JsonValueKind.Array) continue;
                foreach (var v in fl.EnumerateArray()) { var s = Str(v); if (s != null && c.Match(s)) Add(c, "field", s, Int(fields.Value[k]), Int(fields.Value[k + 1]), ""); }
            }
        }

        // spawn points: [type, count, x, y, ...]
        var spawns = Prop(sub, "spawns");
        if (spawns.HasValue && spawns.Value.ValueKind == JsonValueKind.Array)
            foreach (var e in spawns.Value.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 4) continue;
                var s = Str(e[0]);
                if (s != null && c.Match(s)) Add(c, "spawn point", s, Int(e[2]), Int(e[3]), "", Int(e[1]) ?? 1);
            }

        // vehicles & appliances (appliances are vehicles internally)
        var vehs = Prop(sub, "vehicles");
        if (vehs.HasValue && vehs.Value.ValueKind == JsonValueKind.Array)
            foreach (var v in vehs.Value.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                var vt = Prop(v, "type"); var vn = Prop(v, "name");
                var vx = Prop(v, "posx"); var vy = Prop(v, "posy");
                string type = vt.HasValue ? Str(vt.Value) : null;
                string name = vn.HasValue && !string.IsNullOrEmpty(Str(vn.Value)) ? Str(vn.Value) : type;
                int? px = vx.HasValue ? Int(vx.Value) : null, py = vy.HasValue ? Int(vy.Value) : null;
                // spawned vehicles keep their prototype id as "type"; player-built ones and placed appliances
                // are saved as type "none", so match the name too
                if (type != null && c.Match(type)) Add(c, "vehicle", type, px, py, "name: " + name);
                var parts = Prop(v, "parts");
                if (!(type != null && c.Match(type)) && name != null && c.Match(name))
                    Add(c, "vehicle", type == "none" ? name : type, px, py, "name: " + name + (type != "none" ? "" : IsAppliance(parts) ? " (appliance)" : " (player-built)"));
                if (!parts.HasValue || parts.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in parts.Value.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    var pid = Prop(part, "id");
                    var ps = pid.HasValue ? Str(pid.Value) : null;
                    if (ps != null && c.Match(ps)) Add(c, "vehicle part", ps, px, py, "on '" + name + "' (square = vehicle origin)");
                    foreach (var p in part.EnumerateObject())
                    {
                        if (p.NameEquals("base")) Item(c, p.Value, px, py, "installed on '" + name + "'", new List<string>(), null);
                        else Item(c, p.Value, px, py, "in '" + name + "' cargo", new List<string>(), null);
                    }
                }
            }

        // everything else (computers, partial constructions, ...)
        foreach (var p in sub.EnumerateObject())
            if (Array.IndexOf(Handled, p.Name) < 0) Any(c, p.Value, p.Name);
    }

    public static string Compass(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return "here";
        double a = Math.Atan2(-dy, dx) * 180 / Math.PI;          // map y grows southwards
        string[] d = { "E", "NE", "N", "NW", "W", "SW", "S", "SE" };
        return d[(int)Math.Round(((a + 360) % 360) / 45) % 8];
    }

    /// <summary>Placed appliances are one-part "vehicles" whose part id starts with ap_.</summary>
    static bool IsAppliance(JsonElement? parts)
    {
        if (!parts.HasValue || parts.Value.ValueKind != JsonValueKind.Array) return false;
        foreach (var p in parts.Value.EnumerateArray())
        {
            var id = Prop(p, "id");
            if (!id.HasValue || !(Str(id.Value) ?? "").StartsWith("ap_", StringComparison.Ordinal)) return false;
        }
        return parts.Value.GetArrayLength() > 0;
    }

    public static bool IsWildcard(string id) => id.IndexOf('*') >= 0 || id.IndexOf('?') >= 0;

    public static SearchResult Run(string root, string id, int[] zFilter, (int x, int y)? from, Dictionary<string, bool> cbc)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = new SearchResult();
        var typed = id.Trim();
        id = typed.ToLowerInvariant();   // matching ignores case; the byte prefilter gets case variants (below)
        if (id.Length == 0 || !Directory.Exists(root)) return res;
        Func<string, bool> match;
        string literal;
        bool wild = IsWildcard(id);
        if (wild)
        {
            var rx = new Regex("^" + Regex.Escape(id).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            match = s => rx.IsMatch(s);
            var ti = typed.Split('*', '?').OrderByDescending(s => s.Length).First();
            literal = ti.Length >= 2 ? ti : null;
        }
        else
        {
            match = s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase);
            literal = "\"" + typed + "\"";
        }
        return RunMatch(root, match, literal == null ? null : Needles(literal, wild), zFilter, from, cbc, sw);
    }

    /// <summary>
    /// Byte prefilter strings for a query. Ids are lowercase, so a plain lowercase query needs one needle.
    /// Names the player typed (a built vehicle called "My Deathmobile") can be any case, so wildcard
    /// queries and anything with a space or a capital also try the usual capitalisations.
    /// </summary>
    public static List<byte[]> Needles(string literal, bool wild)
    {
        var lower = literal.ToLowerInvariant();
        var set = new List<string> { lower };
        if (wild || literal != lower || literal.IndexOf(' ') >= 0)
        {
            set.Add(literal);
            set.Add(literal.ToUpperInvariant());
            set.Add(Title(lower, false));
            set.Add(Title(lower, true));
        }
        return set.Distinct(StringComparer.Ordinal).Select(Encoding.UTF8.GetBytes).ToList();
    }

    static string Title(string s, bool everyWord)
    {
        var a = s.ToCharArray();
        bool start = true;
        for (int i = 0; i < a.Length; i++)
        {
            if (char.IsLetter(a[i])) { if (start) a[i] = char.ToUpperInvariant(a[i]); start = false; }
            else if (everyWord && (a[i] == ' ' || a[i] == '"')) start = true;
            else if (a[i] == '"') start = true;
        }
        return new string(a);
    }

    /// <summary>Search with any matcher. needles: byte strings of which at least one must occur in a file (null = parse every file).</summary>
    public static SearchResult RunMatch(string root, Func<string, bool> match, List<byte[]> needles, int[] zFilter,
        (int x, int y)? from, Dictionary<string, bool> cbc, System.Diagnostics.Stopwatch sw = null)
    {
        Func<byte[], bool> pre = null;
        if (needles != null) pre = data => { foreach (var nd in needles) if (data.AsSpan().IndexOf(nd) >= 0) return true; return false; };
        return RunMatch(root, match, pre, zFilter, from, cbc, sw);
    }

    static readonly byte[] TypeIdKey = Encoding.UTF8.GetBytes("\"typeid\"");

    /// <summary>
    /// Many item ids at once (category search). The prefilter walks every "typeid" in the raw bytes and looks the
    /// value up in a set, so hundreds of ids cost about the same as one. Only items are reported.
    /// </summary>
    public static SearchResult RunItemIds(string root, HashSet<string> ids, int[] zFilter, (int x, int y)? from, Dictionary<string, bool> cbc)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lookup = ids.GetAlternateLookup<ReadOnlySpan<char>>();
        int maxLen = ids.Count == 0 ? 0 : ids.Max(i => i.Length);
        bool Pre(byte[] data)
        {
            Span<char> buf = stackalloc char[128];
            var span = data.AsSpan();
            int pos = 0;
            while (true)
            {
                int k = span[pos..].IndexOf(TypeIdKey);
                if (k < 0) return false;
                int p = pos + k + TypeIdKey.Length;
                while (p < span.Length && (span[p] == ' ' || span[p] == ':' || span[p] == '\n' || span[p] == '\r' || span[p] == '\t')) p++;
                if (p < span.Length && span[p] == '"')
                {
                    int start = ++p;
                    while (p < span.Length && span[p] != '"') p++;
                    int len = p - start;
                    if (len > 0 && len <= maxLen && len <= buf.Length)
                    {
                        bool ascii = true;
                        for (int i = 0; i < len; i++) { byte b = span[start + i]; if (b >= 0x80) { ascii = false; break; } buf[i] = (char)b; }
                        if (ascii && lookup.Contains(buf[..len])) return true;
                    }
                }
                pos = Math.Max(p, pos + k + 1);
                if (pos >= span.Length) return false;
            }
        }
        var r = RunMatch(root, ids.Contains, Pre, zFilter, from, cbc, sw);
        r.Hits.RemoveAll(h => h.Kind != "item");
        return r;
    }

    static SearchResult RunMatch(string root, Func<string, bool> match, Func<byte[], bool> prefilter, int[] zFilter,
        (int x, int y)? from, Dictionary<string, bool> cbc, System.Diagnostics.Stopwatch sw)
    {
        sw ??= System.Diagnostics.Stopwatch.StartNew();
        var res = new SearchResult();
        if (!Directory.Exists(root)) return res;
        var files = Directory.EnumerateFiles(root, "*.map", SearchOption.AllDirectories);
        if (zFilter != null && zFilter.Length > 0)
        {
            var zs = new HashSet<int>(zFilter);
            files = files.Where(f =>
            {
                var parts = Path.GetFileNameWithoutExtension(f).Split('.');
                return parts.Length == 3 && int.TryParse(parts[2], out var zz) && zs.Contains(zz);
            });
        }

        var bag = new ConcurrentBag<Hit>();
        var errs = new ConcurrentBag<string>();
        long total = 0, parsed = 0, bytes = 0;
        var opts = new JsonDocumentOptions { MaxDepth = 1024, AllowTrailingCommas = true };
        var po = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        Parallel.ForEach(files, po, f =>
        {
            Interlocked.Increment(ref total);
            byte[] data;
            try { data = File.ReadAllBytes(f); } catch (Exception ex) { errs.Add(f + ": " + ex.Message); return; }
            Interlocked.Add(ref bytes, data.Length);
            if (prefilter != null && !prefilter(data)) return;                     // SIMD prefilter
            Interlocked.Increment(ref parsed);
            try
            {
                int start = (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) ? 3 : 0;
                using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(data, start, data.Length - start), opts);
                var ctx = new Ctx { File = Path.GetFileName(f), Bag = bag, Match = match, Cbc = cbc };
                var rootEl = doc.RootElement;
                if (rootEl.ValueKind == JsonValueKind.Array) foreach (var sub in rootEl.EnumerateArray()) Submap(ctx, sub);
                else Submap(ctx, rootEl);
            }
            catch (Exception ex) { errs.Add(Path.GetFileName(f) + ": " + ex.Message); }
        });

        res.Hits = bag.GroupBy(h => h.Kind + "\u0001" + h.Id + "\u0001" + h.Z + "\u0001" + h.Square + "\u0001" + h.Detail + "\u0001" + h.File)
                      .Select(g => { var h = g.First(); h.Qty = g.Sum(x => x.Qty); return h; }).ToList();
        if (from.HasValue)
        {
            foreach (var h in res.Hits)
            {
                int dx = h.Ox - from.Value.x, dy = h.Oy - from.Value.y;
                h.Dist = Math.Max(Math.Abs(dx), Math.Abs(dy));     // overmap tiles; diagonal steps count as 1
                h.Dir = Compass(dx, dy);
            }
            res.Hits = res.Hits.OrderBy(h => h.Dist).ThenBy(h => Math.Abs(h.Z)).ThenBy(h => h.Kind, StringComparer.Ordinal)
                               .ThenBy(h => h.Square, StringComparer.Ordinal).ToList();
        }
        else
        {
            res.Hits = res.Hits.OrderBy(h => h.Kind, StringComparer.Ordinal).ThenBy(h => h.Z).ThenBy(h => h.Oy).ThenBy(h => h.Ox).ToList();
        }
        res.Errors = errs.ToList();
        res.FilesTotal = total; res.FilesParsed = parsed; res.Bytes = bytes;
        res.Seconds = sw.Elapsed.TotalSeconds;
        return res;
    }

    public static List<IdCount> Summarize(List<Hit> hits) =>
        hits.GroupBy(h => h.Id + "\u0001" + h.Kind)
            .Select(g => new IdCount
            {
                Id = g.First().Id, Kind = g.First().Kind, Places = g.Count(), TotalQty = g.Sum(h => h.Qty),
                Nearest = g.Min(h => h.Dist)
            })
            .OrderBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Kind, StringComparer.Ordinal).ToList();

    /// <summary>"0'87, 0'41" -> absolute overmap tile.</summary>
    public static (int x, int y)? ParseInGame(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"^\s*(-?\d+)'(-?\d+)[\s,;]+(-?\d+)'(-?\d+)\s*$");
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value) * 180 + int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value) * 180 + int.Parse(m.Groups[4].Value));
    }

    public static string FormatInGame(int x, int y)
    {
        int ox = Fdiv(x, 180), oy = Fdiv(y, 180);
        return $"{ox}'{x - ox * 180}, {oy}'{y - oy * 180}";
    }
}
