using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CddaCompanion.Game;

public sealed record CodexRaw(string Kind, string[] Ids, JsonElement O, string CopyFrom);

/// <summary>One definition in load order; Parent is what it copied from.</summary>
public sealed class CodexDef
{
    public JsonElement O;
    public int Mod;
    public CodexDef Parent;
    public bool Abstract;
}

public sealed class CodexEntry
{
    public string Kind, Id, Name, Search;
    public CodexDef Def;
}

/// <summary>
/// Look-up of game things: items, furniture, terrain, vehicles and vehicle parts, from the game install and the
/// world's mods. Fields are taken from the nearest definition along the copy-from chain; weight and volume also
/// apply "relative" and "proportional" changes.
/// </summary>
public sealed class Codex
{
    public static readonly string[] Kinds = { "item", "furniture", "terrain", "vehicle", "vehicle_part" };

    readonly Dictionary<(string, string), CodexDef> _defs = new();
    readonly Dictionary<(string, string), CodexEntry> _byKey = new();
    public readonly List<CodexEntry> Entries = new();
    public readonly Dictionary<string, string> Materials = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> Categories = new(StringComparer.Ordinal);
    public List<string> ModNames = new();
    public readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);

    public static Codex Build(IEnumerable<(int mod, List<CodexRaw> raws, List<(string, string)> mats, List<(string, string)> cats)> files, List<string> modNames)
    {
        var c = new Codex { ModNames = modNames };
        // copy-from may point at something defined later in load order (the game defers those), so parents are
        // linked after everything is read. Only a self copy-from (a mod overriding an id) means "the previous one".
        var pending = new List<(CodexDef def, string kind, string from)>();
        foreach (var (mod, raws, mats, cats) in files)
        {
            foreach (var (id, n) in mats) c.Materials[id] = n;
            foreach (var (id, n) in cats) c.Categories[id] = n;
            foreach (var r in raws)
            {
                bool isAbstract = !r.O.TryGetProperty("id", out _) && r.O.TryGetProperty("abstract", out _);
                foreach (var id in r.Ids)
                {
                    var def = new CodexDef { O = r.O, Mod = mod, Abstract = isAbstract };
                    if (r.CopyFrom == id) c._defs.TryGetValue((r.Kind, id), out def.Parent);
                    else if (r.CopyFrom != null) pending.Add((def, r.Kind, r.CopyFrom));
                    c._defs[(r.Kind, id)] = def;
                }
            }
        }
        foreach (var (def, kind, from) in pending)
            if (c._defs.TryGetValue((kind, from), out var parent) && parent != def) def.Parent = parent;
        foreach (var ((kind, id), def) in c._defs)
        {
            if (def.Abstract) continue;
            if (Bool(def, "obsolete")) continue;
            var name = c.NameOf(kind, id, def);
            var e = new CodexEntry { Kind = kind, Id = id, Name = name, Def = def, Search = (name + " " + id).ToLowerInvariant() };
            c.Entries.Add(e);
            c._byKey[(kind, id)] = e;
            c.Counts[kind] = c.Counts.GetValueOrDefault(kind) + 1;
        }
        c.Entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return c;
    }

    public CodexEntry Get(string kind, string id) => _byKey.TryGetValue((kind, id), out var e) ? e : null;

    // ---------------------------------------------------------------- field access along copy-from
    public static JsonElement? Field(CodexDef d, string key)
    {
        for (int i = 0; i < 24 && d != null; i++)
        {
            if (d.O.TryGetProperty(key, out var v)) return v;
            d = d.Parent;
        }
        return null;
    }

    static bool Bool(CodexDef d, string key) => Field(d, key) is { ValueKind: JsonValueKind.True };

    public static string Text(JsonElement? v)
    {
        if (v is not { } e) return null;
        if (e.ValueKind == JsonValueKind.String) return e.GetString();
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("str", out var s) && s.ValueKind == JsonValueKind.String) return s.GetString();
            if (e.TryGetProperty("str_sp", out var sp) && sp.ValueKind == JsonValueKind.String) return sp.GetString();
        }
        return null;
    }

    public static string Str(CodexDef d, string key) => Text(Field(d, key));

    string NameOf(string kind, string id, CodexDef d)
    {
        var n = Str(d, "name");
        if (n == null && kind == "vehicle_part" && Str(d, "item") is { } item && _defs.TryGetValue(("item", item), out var idef))
            n = Str(idef, "name");
        return string.IsNullOrWhiteSpace(n) ? id : n;
    }

    public string NameOf(string kind, string id) => Get(kind, id)?.Name ?? id;

    public List<string> Flags(CodexDef d)
    {
        var chain = new List<CodexDef>();
        for (var x = d; x != null && chain.Count < 24; x = x.Parent) chain.Add(x);
        var set = new List<string>();
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var o = chain[i].O;
            if (o.TryGetProperty("flags", out var f) && f.ValueKind == JsonValueKind.Array)
                set = f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList();
            if (o.TryGetProperty("extend", out var ex) && ex.TryGetProperty("flags", out var ef) && ef.ValueKind == JsonValueKind.Array)
                foreach (var x in ef.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && !set.Contains(x.GetString())) set.Add(x.GetString());
            if (o.TryGetProperty("delete", out var de) && de.TryGetProperty("flags", out var df) && df.ValueKind == JsonValueKind.Array)
                foreach (var x in df.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) set.Remove(x.GetString());
        }
        return set;
    }

    // ---------------------------------------------------------------- units
    static readonly Regex UnitRx = new(@"(-?\d+(?:\.\d+)?)\s*([a-zA-Z]*)", RegexOptions.CultureInvariant);

    /// <summary>Weight in grams or volume in millilitres from "1 kg", "500 g", "250 ml", "2 L" or a bare number.</summary>
    static double? ParseQuantity(JsonElement v, bool volume)
    {
        if (v.ValueKind == JsonValueKind.Number) return volume ? v.GetDouble() * 250 : v.GetDouble();
        if (v.ValueKind != JsonValueKind.String) return null;
        double total = 0; bool any = false;
        foreach (Match m in UnitRx.Matches(v.GetString()))
        {
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string u = m.Groups[2].Value.ToLowerInvariant();
            double f = u switch
            {
                "mg" => 0.001, "g" => 1, "kg" => 1000, "lb" => 453.6, "oz" => 28.35,
                "ml" => 1, "l" => 1000, "" => 1, _ => 1
            };
            total += n * f; any = true;
        }
        return any ? total : null;
    }

    /// <summary>Resolves weight/volume through copy-from, applying relative (+) and proportional (×) on the way down.</summary>
    public static double? Quantity(CodexDef d, string key, bool volume, int depth = 0)
    {
        if (d == null || depth > 24) return null;
        double? v;
        if (d.O.TryGetProperty(key, out var own)) v = ParseQuantity(own, volume);
        else v = Quantity(d.Parent, key, volume, depth + 1);
        if (v == null) return null;
        if (d.O.TryGetProperty("relative", out var rel) && rel.TryGetProperty(key, out var rv) && ParseQuantity(rv, volume) is { } add) v += add;
        if (d.O.TryGetProperty("proportional", out var pro) && pro.TryGetProperty(key, out var pv) && pv.ValueKind == JsonValueKind.Number) v *= pv.GetDouble();
        return v;
    }

    public static string FormatWeight(double g) => g >= 1000 ? (g / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " kg" : g.ToString("0.#", CultureInfo.InvariantCulture) + " g";
    public static string FormatVolume(double ml) => ml >= 1000 ? (ml / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " L" : ml.ToString("0.#", CultureInfo.InvariantCulture) + " ml";

    // ---------------------------------------------------------------- look
    public (string sym, string color) Look(CodexEntry e)
    {
        var d = e.Def;
        string sym = Str(d, "symbol") ?? Str(d, "sym");
        string color = Str(d, "color") ?? Str(d, "bgcolor");
        if (e.Kind == "vehicle") { sym ??= "V"; color ??= "light_gray"; }
        if (e.Kind == "vehicle_part" && sym == null && Field(d, "variants") is { ValueKind: JsonValueKind.Array } vars)
            foreach (var v in vars.EnumerateArray())
                if (v.TryGetProperty("symbols", out var sy) && sy.ValueKind == JsonValueKind.String && sy.GetString().Length > 0) { sym = sy.GetString(); break; }
        if (sym != null && sym.Length > 2) sym = sym.Substring(0, 1);
        return (sym ?? "?", color ?? "light_gray");
    }

    // ---------------------------------------------------------------- search
    public List<CodexEntry> Search(string query, string kind, int max)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<CodexEntry> pool = Entries;
        if (!string.IsNullOrEmpty(kind)) pool = pool.Where(e => e.Kind == kind);
        if (tokens.Length == 0) return pool.Take(max).ToList();
        var hits = new List<(CodexEntry e, int score)>();
        foreach (var e in pool)
        {
            bool all = true;
            foreach (var t in tokens) if (!e.Search.Contains(t, StringComparison.Ordinal)) { all = false; break; }
            if (!all) continue;
            string name = e.Name.ToLowerInvariant();
            int score = e.Id == q ? 0 : name == q ? 1 : name.StartsWith(q, StringComparison.Ordinal) ? 2 : e.Id.StartsWith(q, StringComparison.Ordinal) ? 3 :
                        name.Contains(" " + q, StringComparison.Ordinal) ? 4 : 5;
            hits.Add((e, score));
        }
        return hits.OrderBy(h => h.score).ThenBy(h => h.e.Name.Length).ThenBy(h => h.e.Name, StringComparer.Ordinal)
                   .Take(max).Select(h => h.e).ToList();
    }

    /// <summary>Vehicle prototype display name, so the world search also finds vehicles by name.</summary>
    public string VehicleName(string id) => Get("vehicle", id)?.Name;

    // ---------------------------------------------------------------- details
    public sealed class Detail
    {
        public List<(string label, string value)> Facts = new();
        public List<string> Flags = new();
        public List<(string kind, string id, string name, string count)> Parts = new();   // vehicle parts / "made of"
        public (string kind, string id, string name)? Item;                              // vehicle part -> item
        public string Description, ItemType;
    }

    public Detail Describe(CodexEntry e)
    {
        var d = e.Def;
        var det = new Detail { Description = Str(d, "description") };
        switch (e.Kind)
        {
            case "item":
            {
                string type = Text(d.O.TryGetProperty("type", out var t) ? t : null);
                var subs = Field(d, "subtypes") is { ValueKind: JsonValueKind.Array } sb
                    ? sb.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList() : new List<string>();
                det.ItemType = subs.Count > 0 && type == "ITEM" ? string.Join(", ", subs) : type;
                if (Str(d, "category") is { } cat) det.Facts.Add(("Category", Categories.GetValueOrDefault(cat, cat)));
                if (det.ItemType != null && det.ItemType != "ITEM" && det.ItemType != "GENERIC") det.Facts.Add(("Type", det.ItemType.ToLowerInvariant().Replace('_', ' ')));
                if (Quantity(d, "weight", false) is { } w) det.Facts.Add(("Weight", FormatWeight(w)));
                if (Quantity(d, "volume", true) is { } v) det.Facts.Add(("Volume", FormatVolume(v)));
                if (Quantity(d, "longest_side", false) is { } ls && Field(d, "longest_side") is { ValueKind: JsonValueKind.String } lsr)
                    det.Facts.Add(("Longest side", lsr.GetString()));
                if (Field(d, "material") is { } mat)
                {
                    var names = new List<string>();
                    if (mat.ValueKind == JsonValueKind.String) names.Add(mat.GetString());
                    else if (mat.ValueKind == JsonValueKind.Array)
                        foreach (var m in mat.EnumerateArray())
                            names.Add(m.ValueKind == JsonValueKind.String ? m.GetString() : m.ValueKind == JsonValueKind.Object && m.TryGetProperty("type", out var mt) ? mt.GetString() : null);
                    var shown = names.Where(n => n != null).Select(n => Materials.GetValueOrDefault(n, n)).ToList();
                    if (shown.Count > 0) det.Facts.Add(("Material", string.Join(", ", shown)));
                }
                break;
            }
            case "furniture":
            case "terrain":
            {
                if (Field(d, "move_cost_mod") is { ValueKind: JsonValueKind.Number } mc) det.Facts.Add(("Move cost", mc.GetInt32() < 0 ? "impassable" : "+" + mc.GetInt32()));
                if (Field(d, "move_cost") is { ValueKind: JsonValueKind.Number } mct) det.Facts.Add(("Move cost", mct.GetInt32() == 0 ? "impassable" : mct.GetInt32().ToString()));
                if (Field(d, "coverage") is { ValueKind: JsonValueKind.Number } cov) det.Facts.Add(("Coverage", cov.GetInt32() + "%"));
                if (Field(d, "max_volume") is { ValueKind: JsonValueKind.String } mv) det.Facts.Add(("Storage", mv.GetString()));
                break;
            }
            case "vehicle":
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                if (Field(d, "parts") is { ValueKind: JsonValueKind.Array } parts)
                    foreach (var p in parts.EnumerateArray())
                    {
                        void Add(JsonElement x)
                        {
                            string pid = x.ValueKind == JsonValueKind.String ? x.GetString()
                                : x.ValueKind == JsonValueKind.Object && x.TryGetProperty("part", out var pp) ? pp.GetString() : null;
                            if (pid == null) return;
                            int hash = pid.IndexOf('#'); if (hash > 0) pid = pid[..hash];
                            counts[pid] = counts.GetValueOrDefault(pid) + 1;
                        }
                        if (p.TryGetProperty("parts", out var list) && list.ValueKind == JsonValueKind.Array) foreach (var x in list.EnumerateArray()) Add(x);
                        else if (p.TryGetProperty("part", out var single)) Add(single);
                    }
                int total = counts.Values.Sum();
                det.Facts.Add(("Parts", total.ToString()));
                foreach (var (pid, n) in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                    det.Parts.Add(("vehicle_part", pid, NameOf("vehicle_part", pid), "×" + n));
                break;
            }
            case "vehicle_part":
            {
                if (Str(d, "item") is { } item) det.Item = ("item", item, NameOf("item", item));
                if (Field(d, "durability") is { ValueKind: JsonValueKind.Number } du) det.Facts.Add(("Durability", du.GetInt32().ToString()));
                if (Str(d, "location") is { } loc) det.Facts.Add(("Mount", loc.Replace('_', ' ')));
                break;
            }
        }
        det.Flags = Flags(d);
        return det;
    }

    public string ModName(CodexEntry e) => e.Def.Mod >= 0 && e.Def.Mod < ModNames.Count ? ModNames[e.Def.Mod] : "";
}
