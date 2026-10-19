using System.Text.Json;

namespace CddaCompanion.Game;

/// <summary>One way to get an item: disassembling an item, or deconstructing furniture / terrain.</summary>
public sealed class DisSource
{
    public string Id, Kind;          // Kind: "item", "furniture" or "terrain"
    public string Via;               // furniture that becomes an appliance item first (f_oven -> oven -> parts)
    public double Min, Max, Exp;     // how many of the target one source gives
}

public readonly record struct Yield(double Min, double Max, double Exp);

/// <summary>
/// Reverse index "item -> what gives it", built from the game's own data:
///  - disassembly: "uncraft" recipes, else the item's own recipe when it's reversible.
///    Following crafting.cpp complete_disassemble: an item without recorded components gives back the
///    first alternative of each component group, minus NO_RECOVER entries and UNRECOVERABLE items.
///  - deconstruction of furniture and terrain ("deconstruct": { "items": ... }), item groups included.
/// </summary>
public sealed class DisassemblyIndex
{
    public readonly Dictionary<string, List<DisSource>> ByTarget = new(StringComparer.Ordinal);
    /// <summary>(kind, source id) -> target -> yield per source.</summary>
    public readonly Dictionary<(string kind, string id), Dictionary<string, Yield>> BySource = new();
    /// <summary>What deconstructing furniture/terrain gives directly, before the appliance-item step (for the Codex).</summary>
    public readonly Dictionary<(string kind, string id), Dictionary<string, Yield>> DirectBySource = new();
    public int Recipes, Deconstructs;

    public List<DisSource> SourcesOf(string target) =>
        ByTarget.TryGetValue(target, out var l) ? l : new List<DisSource>();

    internal void Add(string kind, string source, Dictionary<string, Yield> yields, Dictionary<string, string> via = null)
    {
        if (yields.Count == 0) return;
        BySource[(kind, source)] = yields;
        foreach (var (target, y) in yields)
        {
            if (y.Max <= 0) continue;
            if (!ByTarget.TryGetValue(target, out var list)) ByTarget[target] = list = new List<DisSource>();
            list.Add(new DisSource { Id = source, Kind = kind, Min = y.Min, Max = y.Max, Exp = y.Exp, Via = via != null && via.TryGetValue(target, out var v) ? v : null });
        }
    }
}

/// <summary>Raw JSON records collected while the game data is read (in load order).</summary>
internal sealed class DisRecords
{
    public sealed class Recipe
    {
        public bool Uncraft, Abstract;
        public string Key, Result, CopyFrom;
        public JsonElement? Components, Using;
        public bool? Reversible, Obsolete;
    }
    public sealed class Decon { public string[] Ids; public string Kind, CopyFrom, ConvertItem; public JsonElement? Items; public bool HasDecon; }

    public readonly List<Recipe> Recipes = new();
    public readonly List<(string[] ids, JsonElement comps)> Requirements = new();
    public readonly List<Decon> Decons = new();
    public readonly List<(string id, JsonElement group)> Groups = new();

    static string Str(JsonElement o, string k) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string[] Ids(JsonElement o)
    {
        if (!o.TryGetProperty("id", out var v)) return Array.Empty<string>();
        if (v.ValueKind == JsonValueKind.String) return new[] { v.GetString() };
        if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray();
        return Array.Empty<string>();
    }

    public void Collect(string type, JsonElement o)
    {
        switch (type)
        {
            case "recipe":
            case "uncraft":
            {
                var r = new Recipe { Uncraft = type == "uncraft", Result = Str(o, "result"), CopyFrom = Str(o, "copy-from") };
                var abs = Str(o, "abstract");
                if (abs != null) { r.Abstract = true; r.Key = abs; }
                else if (r.Result != null)
                {
                    var suffix = Str(o, "id_suffix");
                    r.Key = suffix != null ? r.Result + "_" + suffix : r.Result;
                }
                else return;
                if (r.Uncraft) r.Key = "uncraft:" + r.Key;
                if (o.TryGetProperty("components", out var c) && c.ValueKind == JsonValueKind.Array) r.Components = c.Clone();
                if (o.TryGetProperty("using", out var u)) r.Using = u.Clone();
                if (o.TryGetProperty("reversible", out var rv))
                    r.Reversible = rv.ValueKind == JsonValueKind.True || rv.ValueKind == JsonValueKind.Object;
                if (o.TryGetProperty("obsolete", out var ob)) r.Obsolete = ob.ValueKind == JsonValueKind.True;
                Recipes.Add(r);
                break;
            }
            case "requirement":
            {
                var ids = Ids(o);
                if (ids.Length > 0 && o.TryGetProperty("components", out var c) && c.ValueKind == JsonValueKind.Array)
                    Requirements.Add((ids, c.Clone()));
                break;
            }
            case "furniture":
            case "terrain":
            {
                var d = new DisRecords.Decon { Ids = Ids(o), Kind = type, CopyFrom = Str(o, "copy-from") };
                if (d.Ids.Length == 0 && Str(o, "abstract") is { } a) d.Ids = new[] { a };
                if (d.Ids.Length == 0) return;
                if (o.TryGetProperty("deconstruct", out var de) && de.ValueKind == JsonValueKind.Object)
                {
                    d.HasDecon = true;
                    if (de.TryGetProperty("items", out var it)) d.Items = it.Clone();
                }
                // appliances: examining converts the furniture into its item
                if (o.TryGetProperty("examine_action", out var ea) && ea.ValueKind == JsonValueKind.Object &&
                    Str(ea, "type") == "appliance_convert" && Str(ea, "item") is { } conv)
                    d.ConvertItem = conv;
                Decons.Add(d);
                break;
            }
            case "item_group":
            {
                var id = Str(o, "id");
                if (id != null) Groups.Add((id, o.Clone()));
                break;
            }
        }
    }

    // ---------------------------------------------------------------- build
    public DisassemblyIndex Build(HashSet<string> unrecoverable)
    {
        var index = new DisassemblyIndex();
        var requirements = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (ids, comps) in Requirements) foreach (var id in ids) requirements[id] = comps;
        var groups = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, g) in Groups) groups[id] = g;

        // recipes: later definitions win; copy-from of the same id inherits from the earlier one
        var recipes = new Dictionary<string, (Recipe r, Recipe parent)>(StringComparer.Ordinal);
        foreach (var r in Recipes)
        {
            Recipe parent = null;
            string parentKey = r.CopyFrom == null ? null : (r.Uncraft ? "uncraft:" + r.CopyFrom : r.CopyFrom);
            if (parentKey != null && recipes.TryGetValue(parentKey, out var p)) parent = p.r;
            recipes[r.Key] = (r, parent);
        }
        Recipe ParentOf(Recipe r) => recipes.TryGetValue(r.Key, out var e) && e.r == r ? e.parent : null;
        bool? GetBool(Recipe r, Func<Recipe, bool?> f)
        {
            for (int i = 0; i < 16 && r != null; i++) { var v = f(r); if (v.HasValue) return v; r = ParentOf(r); }
            return null;
        }
        JsonElement? GetEl(Recipe r, Func<Recipe, JsonElement?> f)
        {
            for (int i = 0; i < 16 && r != null; i++) { var v = f(r); if (v.HasValue) return v; r = ParentOf(r); }
            return null;
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        void FromRecipe(string itemId, Recipe r)
        {
            if (GetBool(r, x => x.Obsolete) == true) return;
            var groupsList = ExpandComponents(GetEl(r, x => x.Components), GetEl(r, x => x.Using), requirements);
            var yields = new Dictionary<string, Yield>(StringComparer.Ordinal);
            foreach (var alts in groupsList)
            {
                var first = alts.FirstOrDefault(a => !a.noRecover && !unrecoverable.Contains(a.id));
                if (first.id == null || first.n <= 0) continue;
                yields.TryGetValue(first.id, out var y);
                yields[first.id] = new Yield(y.Min + first.n, y.Max + first.n, y.Exp + first.n);
            }
            if (yields.Count == 0) return;
            index.Add("item", itemId, yields);
            index.Recipes++;
            done.Add(itemId);
        }
        // 1. explicit disassembly recipes
        foreach (var (key, (r, _)) in recipes)
            if (r.Uncraft && !r.Abstract && r.Result != null && key == "uncraft:" + r.Result) FromRecipe(r.Result, r);
        // 2. reversible crafting recipes (only the one whose id is the item id, like recipe_dictionary.cpp)
        foreach (var (key, (r, _)) in recipes)
            if (!r.Uncraft && !r.Abstract && r.Result != null && key == r.Result && !done.Contains(r.Result) && GetBool(r, x => x.Reversible) == true)
                FromRecipe(r.Result, r);

        // 3. furniture / terrain deconstruction
        var decons = new Dictionary<(string, string), (Decon d, Decon parent)>();
        foreach (var d in Decons)
            foreach (var id in d.Ids)
            {
                Decon parent = null;
                if (d.CopyFrom != null && decons.TryGetValue((d.Kind, d.CopyFrom), out var p)) parent = p.d;
                decons[(d.Kind, id)] = (d, parent);
            }
        var deconYields = new List<(string kind, string id, Dictionary<string, Yield> y)>();
        foreach (var ((kind, id), (d, parent)) in decons)
        {
            var cur = d; JsonElement? items = null; bool has = false; string convert = null;
            for (int i = 0; i < 16 && cur != null; i++)
            {
                convert ??= cur.ConvertItem;
                if (cur.HasDecon && !has) { has = true; items = cur.Items; }
                if (has && convert != null) break;
                var key = (kind, cur.CopyFrom);
                cur = cur.CopyFrom != null && decons.TryGetValue(key, out var e) ? e.d : null;
            }
            var y = has && items != null ? new GroupEval(groups).Collection(items.Value, 0) : new Dictionary<string, Yield>(StringComparer.Ordinal);
            if (!has && convert != null) y[convert] = new Yield(1, 1, 1);
            if (y.Count > 0) deconYields.Add((kind, id, y));
        }
        // furniture -> appliance item -> its parts counts as one step (that's how the game models appliances)
        foreach (var (kind, id, y) in deconYields)
        {
            var combined = new Dictionary<string, Yield>(y, StringComparer.Ordinal);
            var via = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (item, iy) in y)
            {
                if (!index.BySource.TryGetValue(("item", item), out var parts)) continue;
                foreach (var (target, py) in parts)
                {
                    combined.TryGetValue(target, out var c);
                    combined[target] = new Yield(c.Min + iy.Min * py.Min, c.Max + iy.Max * py.Max, c.Exp + iy.Exp * py.Exp);
                    via.TryAdd(target, item);
                }
            }
            index.Add(kind, id, combined, via);
            index.DirectBySource[(kind, id)] = y;
            index.Deconstructs++;
        }
        foreach (var l in index.ByTarget.Values) l.Sort((a, b) => b.Exp.CompareTo(a.Exp));
        return index;
    }

    static List<List<(string id, double n, bool noRecover)>> ExpandComponents(JsonElement? comps, JsonElement? usingEl,
        Dictionary<string, JsonElement> reqs)
    {
        var result = new List<List<(string, double, bool)>>();
        void AddGroups(JsonElement groupsEl, double mult, int depth)
        {
            if (groupsEl.ValueKind != JsonValueKind.Array || depth > 6) return;
            foreach (var g in groupsEl.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Array) continue;
                var alts = new List<(string, double, bool)>();
                foreach (var e in g.EnumerateArray()) AddAlt(e, mult, alts, depth);
                if (alts.Count > 0) result.Add(alts);
            }
        }
        void AddAlt(JsonElement e, double mult, List<(string, double, bool)> alts, int depth)
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 1 || e[0].ValueKind != JsonValueKind.String) return;
            string id = e[0].GetString();
            double n = e.GetArrayLength() > 1 && e[1].ValueKind == JsonValueKind.Number ? e[1].GetDouble() : 1;
            string flag = e.GetArrayLength() > 2 && e[2].ValueKind == JsonValueKind.String ? e[2].GetString() : null;
            if (flag == "LIST")
            {
                // a requirement used as a list of alternatives
                if (depth < 6 && reqs.TryGetValue(id, out var req) && req.ValueKind == JsonValueKind.Array)
                    foreach (var g in req.EnumerateArray())
                        if (g.ValueKind == JsonValueKind.Array)
                            foreach (var a in g.EnumerateArray()) AddAlt(a, mult * n, alts, depth + 1);
                return;
            }
            alts.Add((id, n * mult, flag == "NO_RECOVER"));
        }
        if (comps.HasValue) AddGroups(comps.Value, 1, 0);
        if (usingEl.HasValue)
        {
            var u = usingEl.Value;
            IEnumerable<(string, double)> uses = u.ValueKind switch
            {
                JsonValueKind.String => new[] { (u.GetString(), 1.0) },
                JsonValueKind.Array when u.GetArrayLength() > 0 && u[0].ValueKind == JsonValueKind.String =>
                    new[] { (u[0].GetString(), u.GetArrayLength() > 1 && u[1].ValueKind == JsonValueKind.Number ? u[1].GetDouble() : 1.0) },
                JsonValueKind.Array => u.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() > 0 && x[0].ValueKind == JsonValueKind.String)
                    .Select(x => (x[0].GetString(), x.GetArrayLength() > 1 && x[1].ValueKind == JsonValueKind.Number ? x[1].GetDouble() : 1.0)).ToList(),
                _ => Array.Empty<(string, double)>()
            };
            foreach (var (rid, mult) in uses)
                if (reqs.TryGetValue(rid, out var req)) AddGroups(req, mult, 1);
        }
        return result;
    }

    /// <summary>Expected / min / max item counts of item groups (collection = each entry rolls, distribution = one entry).</summary>
    sealed class GroupEval
    {
        readonly Dictionary<string, JsonElement> _groups;
        public GroupEval(Dictionary<string, JsonElement> groups) { _groups = groups; }

        static (double min, double max) Range(JsonElement o, string key)
        {
            if (!o.TryGetProperty(key, out var c)) return (-1, -1);
            if (c.ValueKind == JsonValueKind.Number) { var v = c.GetDouble(); return (v, v); }
            if (c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 2 && c[0].ValueKind == JsonValueKind.Number && c[1].ValueKind == JsonValueKind.Number)
                return (c[0].GetDouble(), c[1].GetDouble());
            return (-1, -1);
        }

        public Dictionary<string, Yield> Collection(JsonElement spec, int depth) => Eval(spec, "collection", depth);

        Dictionary<string, Yield> Eval(JsonElement spec, string defaultSubtype, int depth)
        {
            if (depth > 6) return new();
            switch (spec.ValueKind)
            {
                case JsonValueKind.String:
                    return _groups.TryGetValue(spec.GetString(), out var g) ? Group(g, depth + 1) : new();
                case JsonValueKind.Array:
                    return Combine(spec.EnumerateArray().ToList(), defaultSubtype, depth);
                case JsonValueKind.Object:
                    if (spec.TryGetProperty("item", out _) || spec.TryGetProperty("group", out _))
                        return Combine(new List<JsonElement> { spec }, "collection", depth);
                    return Group(spec, depth + 1);
            }
            return new();
        }

        Dictionary<string, Yield> Group(JsonElement g, int depth)
        {
            string subtype = g.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : "old";
            var entries = new List<JsonElement>();
            foreach (var key in new[] { "items", "entries", "groups" })
                if (g.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var e in arr.EnumerateArray())
                        entries.Add(key == "groups" && e.ValueKind == JsonValueKind.Array ? WrapGroup(e) : e);
            return Combine(entries, subtype == "collection" ? "collection" : "distribution", depth);
        }

        static JsonElement WrapGroup(JsonElement arr)
        {
            // ["group_id", prob] in a "groups" list
            var id = arr.GetArrayLength() > 0 ? arr[0].GetString() : "";
            var prob = arr.GetArrayLength() > 1 && arr[1].ValueKind == JsonValueKind.Number ? arr[1].GetDouble() : 100;
            using var doc = JsonDocument.Parse($"{{\"group\":{JsonSerializerEscape(id)},\"prob\":{prob.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
            return doc.RootElement.Clone();
        }

        static string JsonSerializerEscape(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        Dictionary<string, Yield> Combine(List<JsonElement> entries, string subtype, int depth)
        {
            var parts = new List<(double prob, Dictionary<string, Yield> y)>();
            foreach (var e in entries)
            {
                double prob = 100;
                Dictionary<string, Yield> y;
                if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 1 && e[0].ValueKind == JsonValueKind.String)
                {
                    // old style ["item_id", prob]
                    if (e.GetArrayLength() > 1 && e[1].ValueKind == JsonValueKind.Number) prob = e[1].GetDouble();
                    y = new() { [e[0].GetString()] = new Yield(1, 1, 1) };
                }
                else if (e.ValueKind == JsonValueKind.Object)
                {
                    if (e.TryGetProperty("prob", out var p) && p.ValueKind == JsonValueKind.Number) prob = p.GetDouble();
                    var (cmin, cmax) = Range(e, "count");
                    var (hmin, hmax) = Range(e, "charges");
                    if (cmin < 0) (cmin, cmax) = (1, 1);
                    if (e.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.String)
                    {
                        double mn = cmin, mx = cmax;
                        if (hmin >= 0) { mn = cmin * hmin; mx = cmax * hmax; }
                        y = new() { [it.GetString()] = new Yield(mn, mx, (mn + mx) / 2) };
                    }
                    else if (e.TryGetProperty("group", out var gr))
                    {
                        var inner = Eval(gr, "distribution", depth + 1);
                        y = inner.ToDictionary(kv => kv.Key, kv => new Yield(kv.Value.Min * cmin, kv.Value.Max * cmax, kv.Value.Exp * (cmin + cmax) / 2));
                    }
                    else if (e.TryGetProperty("collection", out var col)) y = Combine(col.EnumerateArray().ToList(), "collection", depth + 1);
                    else if (e.TryGetProperty("distribution", out var dis)) y = Combine(dis.EnumerateArray().ToList(), "distribution", depth + 1);
                    else continue;
                }
                else if (e.ValueKind == JsonValueKind.String)
                {
                    y = new() { [e.GetString()] = new Yield(1, 1, 1) };
                }
                else continue;
                parts.Add((prob, y));
            }
            var res = new Dictionary<string, Yield>(StringComparer.Ordinal);
            if (parts.Count == 0) return res;
            if (subtype == "collection")
            {
                foreach (var (prob, y) in parts)
                {
                    double p = Math.Clamp(prob / 100.0, 0, 1);
                    foreach (var (id, v) in y)
                    {
                        res.TryGetValue(id, out var r);
                        res[id] = new Yield(r.Min + (p >= 1 ? v.Min : 0), r.Max + v.Max, r.Exp + p * v.Exp);
                    }
                }
            }
            else
            {
                double total = parts.Sum(x => Math.Max(0, x.prob));
                if (total <= 0) return res;
                bool single = parts.Count == 1;
                foreach (var (prob, y) in parts)
                {
                    double w = Math.Max(0, prob) / total;
                    foreach (var (id, v) in y)
                    {
                        res.TryGetValue(id, out var r);
                        res[id] = new Yield(single ? v.Min : 0, Math.Max(r.Max, v.Max), r.Exp + w * v.Exp);
                    }
                }
            }
            return res;
        }
    }
}
