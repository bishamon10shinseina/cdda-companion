using System.Text.Json;

namespace CddaCompanion.Game;

/// <summary>
/// Items sorted the way the game's inventory sorts them (ITEM_CATEGORY), for category search.
/// Ammo, magazines and guns are split further by ammo type ("set") and by what fires them ("group"):
/// firearms, bows/slings/thrown, other guns (air, energy, sprays), and things no gun uses (ink, fuel, gases).
/// </summary>
public sealed class ItemCategories
{
    public sealed class Cat
    {
        public string Id, Name; public int Rank;
        public List<Group> Groups = new();
        public int Count;
    }
    public sealed class Group { public string Id, Name; public List<Set> Sets = new(); }
    public sealed class Set { public string Id, Name; public List<string> Items = new(); public string Group; }

    public const string Firearms = "firearms", Primitive = "primitive", OtherGuns = "otherguns", NoGun = "nogun", All = "all";
    static readonly (string id, string name)[] GroupNames = {
        (Firearms, "Firearms (gunpowder: cartridges, shells, black powder, grenades, rockets)"),
        (OtherGuns, "Other guns (air, energy, gauss, sprays, nail guns)"),
        (Primitive, "Bows, crossbows, slings and thrown"),
        (NoGun, "Not fired from a gun (fuel, ink, gases, tool charges)"),
    };

    public readonly List<Cat> Cats = new();
    readonly Dictionary<string, Cat> _cats = new(StringComparer.Ordinal);
    /// <summary>item id -> (category, set)</summary>
    public readonly Dictionary<string, (string cat, string set)> OfItem = new(StringComparer.Ordinal);
    /// <summary>Items that hold ammo when loaded: guns and magazines.</summary>
    public readonly HashSet<string> Holders = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> AmmoTypeNames = new(StringComparer.Ordinal);

    public Cat Get(string id) => id != null && _cats.TryGetValue(id, out var c) ? c : null;

    static List<string> Strings(JsonElement? v)
    {
        var l = new List<string>();
        if (v is not { } e) return l;
        if (e.ValueKind == JsonValueKind.String) l.Add(e.GetString());
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String) l.Add(x.GetString());
                else if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) l.Add(t.GetString());
            }
        return l;
    }

    static HashSet<string> Subtypes(CodexDef d)
    {
        var s = new HashSet<string>(Strings(Codex.Field(d, "subtypes")), StringComparer.Ordinal);
        // pre-0.I style "type": "AMMO" etc.
        if (d.O.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() != "ITEM") s.Add(t.GetString());
        return s;
    }

    /// <summary>Same order as item_factory.cpp calc_category() when the item doesn't name a category.</summary>
    static string CalcCategory(CodexDef d, HashSet<string> subs)
    {
        if (subs.Contains("GUN") && !subs.Contains("GUNMOD")) return "guns";
        if (subs.Contains("MAGAZINE")) return "magazines";
        if (subs.Contains("AMMO")) return "ammo";
        if (subs.Contains("TOOL") || subs.Contains("TOOL_ARMOR")) return "tools";
        if (subs.Contains("ARMOR")) return "clothing";
        if (subs.Contains("COMESTIBLE")) return Codex.Str(d, "comestible_type") == "MED" ? "drugs" : "food";
        if (subs.Contains("BOOK")) return "books";
        if (subs.Contains("GUNMOD")) return "mods";
        if (subs.Contains("BIONIC_ITEM") || subs.Contains("BIONIC")) return "bionics";
        if (Codex.Field(d, "melee_damage") is { ValueKind: JsonValueKind.Object } md)
            foreach (var p in md.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.GetDouble() > 5) return "weapons";   // MELEE_STAT
        return "other";
    }

    public static ItemCategories Build(Codex codex, Dictionary<string, string> ammoTypeNames, Dictionary<string, int> ranks)
    {
        var ic = new ItemCategories();
        foreach (var (k, v) in ammoTypeNames) ic.AmmoTypeNames[k] = v;

        // pass 1: what each item is
        var info = new List<(CodexEntry e, string cat, string caliber, bool gun, bool primitiveGun, bool ammo, bool powder)>();
        var gunsUsing = new Dictionary<string, (bool prim, bool nonPrim)>(StringComparer.Ordinal);
        var powderTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in codex.Entries)
        {
            if (e.Kind != "item") continue;
            var d = e.Def;
            var subs = Subtypes(d);
            string cat = Codex.Str(d, "category") ?? CalcCategory(d, subs);
            bool gun = subs.Contains("GUN") && !subs.Contains("GUNMOD"), ammo = subs.Contains("AMMO"), mag = subs.Contains("MAGAZINE");
            string caliber = null; bool prim = false, powder = false;
            if (ammo)
            {
                caliber = Strings(Codex.Field(d, "ammo_type")).FirstOrDefault();
                powder = Strings(Codex.Field(d, "material")).Contains("powder");
                if (caliber != null && powder) powderTypes.Add(caliber);
            }
            else if (mag) caliber = Strings(Codex.Field(d, "ammo_type")).FirstOrDefault();
            if (gun)
            {
                var skill = Codex.Str(d, "skill");
                prim = skill is "archery" or "throw" || codex.Flags(d).Contains("PRIMITIVE_RANGED_WEAPON");
                var cals = Strings(Codex.Field(d, "ammo"));
                caliber = cals.FirstOrDefault(c => c != "NULL");
                foreach (var c in cals)
                {
                    gunsUsing.TryGetValue(c, out var u);
                    gunsUsing[c] = (u.prim || prim, u.nonPrim || !prim);
                }
            }
            if (gun || mag) ic.Holders.Add(e.Id);
            info.Add((e, cat, caliber, gun, prim, ammo, powder));
        }

        string ClassOf(string caliber)
        {
            if (caliber == null || caliber == "NULL") return NoGun;
            gunsUsing.TryGetValue(caliber, out var u);
            bool anyGun = u.prim || u.nonPrim;
            if (powderTypes.Contains(caliber) && (u.nonPrim || !anyGun)) return Firearms;
            if (u.prim && !u.nonPrim) return Primitive;
            if (u.nonPrim) return powderTypes.Contains(caliber) ? Firearms : OtherGuns;
            return NoGun;
        }

        // pass 2: build category -> group -> set
        foreach (var (e, cat, caliber, gun, prim, ammo, powder) in info)
        {
            if (!ic._cats.TryGetValue(cat, out var c))
            {
                c = new Cat { Id = cat, Name = Pretty(codex.Categories.GetValueOrDefault(cat, cat)), Rank = ranks.GetValueOrDefault(cat, 1000) };
                ic._cats[cat] = c; ic.Cats.Add(c);
            }
            c.Count++;
            bool split = cat is "ammo" or "magazines" or "guns";
            string gid, gname, sid, sname;
            if (split)
            {
                gid = gun && prim ? Primitive : gun && caliber == null ? OtherGuns : ClassOf(caliber);
                gname = GroupNames.First(g => g.id == gid).name;
                sid = caliber ?? "-";
                sname = caliber == null ? (gun ? "No ammo (UPS, bionic power, built-in)" : "No ammo type") : ic.AmmoTypeNames.GetValueOrDefault(caliber, caliber);
            }
            else { gid = All; gname = c.Name; sid = cat; sname = c.Name; }
            var g = c.Groups.FirstOrDefault(x => x.Id == gid);
            if (g == null) { g = new Group { Id = gid, Name = gname }; c.Groups.Add(g); }
            var s = g.Sets.FirstOrDefault(x => x.Id == sid);
            if (s == null) { s = new Set { Id = sid, Name = sname, Group = gid }; g.Sets.Add(s); }
            s.Items.Add(e.Id);
            ic.OfItem[e.Id] = (cat, sid);
        }
        int GroupOrder(string id) => Array.FindIndex(GroupNames, g => g.id == id) is var i && i >= 0 ? i : 99;
        foreach (var c in ic.Cats)
        {
            c.Groups.Sort((a, b) => GroupOrder(a.Id).CompareTo(GroupOrder(b.Id)));
            foreach (var g in c.Groups)
            {
                g.Sets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var s in g.Sets) s.Items.Sort((a, b) => string.Compare(codex.NameOf("item", a), codex.NameOf("item", b), StringComparison.OrdinalIgnoreCase));
            }
        }
        ic.Cats.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return ic;
    }

    /// <summary>"AMMO" -> "Ammo", "spare parts" stays.</summary>
    static string Pretty(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.ToUpperInvariant() == s) s = s.ToLowerInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>The ids a category search looks for, after exclusions.</summary>
    public HashSet<string> Resolve(string catId, ICollection<string> excludedSets, ICollection<string> excludedItems, string onlySet = null)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var c = Get(catId);
        if (c == null) return ids;
        foreach (var g in c.Groups)
            foreach (var s in g.Sets)
            {
                if (onlySet != null ? s.Id != onlySet : excludedSets.Contains(s.Id)) continue;
                foreach (var i in s.Items) if (!excludedItems.Contains(i)) ids.Add(i);
            }
        return ids;
    }

    /// <summary>Sets outside the "Firearms" group: what the Firearms-only preset excludes.</summary>
    public List<string> NonFirearmSets(string catId) =>
        Get(catId)?.Groups.Where(g => g.Id != Firearms).SelectMany(g => g.Sets).Select(s => s.Id).ToList() ?? new List<string>();
}
