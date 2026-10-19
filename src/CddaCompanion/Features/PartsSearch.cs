using System.Text;
using System.Text.RegularExpressions;
using CddaCompanion.Game;

namespace CddaCompanion.Features;

/// <summary>One line in a result tile: loose target items, or something that gives the target when taken apart.</summary>
public sealed class PartsLine
{
    public string Id, Kind, Via;
    public bool Direct;
    public long Qty;           // how many of this thing are on the tile
    public int Stacks;
    public double Min, Max, Exp;  // how many targets they give in total
}

public sealed class PartsTile
{
    public int X, Y, Z;
    public string InGame, Dir;
    public int? Dist;
    public double Min, Max, Exp;
    public List<PartsLine> Lines = new();
    public List<(Hit hit, double min, double max, bool direct)> Hits = new();
}

public sealed class PartsResult
{
    public SearchResult Raw;
    public List<string> Targets = new();
    public List<DisSource> Sources = new();
    public List<PartsTile> Tiles = new();
    public double TotalMin, TotalMax, TotalExp;
    public string Error;
}

/// <summary>
/// "Where can I get X": loose X, plus everything that gives X when disassembled (items) or
/// deconstructed (furniture, terrain), one step deep. Crafted items with recorded components are counted
/// by what they were really made from (the normal search already reports those components).
/// </summary>
public static class PartsSearch
{
    const int MaxSources = 800;

    public static PartsResult Run(string root, string query, DisassemblyIndex index, int[] zFilter, (int x, int y)? from,
        Dictionary<string, bool> cbc)
    {
        var res = new PartsResult();
        query = query.Trim().ToLowerInvariant();
        Regex rx = null;
        var targets = new HashSet<string>(StringComparer.Ordinal);
        if (ItemSearch.IsWildcard(query))
        {
            rx = new Regex("^" + Regex.Escape(query).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.CultureInvariant);
            foreach (var t in index.ByTarget.Keys) if (rx.IsMatch(t)) targets.Add(t);
        }
        else targets.Add(query);

        // every source, with what it gives of each target
        var sourceYields = new Dictionary<(string kind, string id), Dictionary<string, Yield>>();
        var vias = new Dictionary<(string kind, string id), string>();
        foreach (var t in targets)
            foreach (var s in index.SourcesOf(t))
            {
                var key = (s.Kind, s.Id);
                if (!sourceYields.TryGetValue(key, out var d)) sourceYields[key] = d = new Dictionary<string, Yield>(StringComparer.Ordinal);
                d[t] = new Yield(s.Min, s.Max, s.Exp);
                if (s.Via != null) vias.TryAdd(key, s.Via);
                res.Sources.Add(s);
            }
        if (sourceYields.Count > MaxSources)
        {
            res.Error = $"That matches {sourceYields.Count:N0} different sources. Make the search more specific.";
            return res;
        }
        res.Targets = targets.OrderBy(t => t, StringComparer.Ordinal).ToList();
        res.Sources = res.Sources.OrderByDescending(s => s.Exp).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();

        var exact = new HashSet<string>(targets, StringComparer.Ordinal);
        foreach (var (_, id) in sourceYields.Keys) exact.Add(id);
        var needles = exact.Select(id => Encoding.UTF8.GetBytes("\"" + id + "\"")).ToList();
        if (rx != null)
        {
            var chunk = query.Split('*', '?').OrderByDescending(s => s.Length).First();
            if (chunk.Length >= 2) needles.Add(Encoding.UTF8.GetBytes(chunk)); else needles = null;
        }
        bool IsTarget(string s) => targets.Contains(s) || (rx != null && rx.IsMatch(s));
        res.Raw = ItemSearch.RunMatch(root, s => exact.Contains(s) || (rx != null && rx.IsMatch(s)), needles, zFilter, from, cbc);

        var tiles = new Dictionary<(int, int, int), PartsTile>();
        var lines = new Dictionary<(int, int, int, string, string, bool), PartsLine>();
        foreach (var h in res.Raw.Hits)
        {
            double min, max, exp; bool direct; string via = null;
            if (IsTarget(h.Id))
            {
                direct = true; min = max = exp = h.Qty;
            }
            else
            {
                string kind = h.Kind switch { "item" => "item", "furniture" => "furniture", "terrain" => "terrain", _ => null };
                if (kind == null || !sourceYields.TryGetValue((kind, h.Id), out var ys)) continue;
                // crafted items give back what they were made of (found separately); components of crafted
                // items would be a second step
                if (kind == "item" && (h.Crafted || h.ComponentOf != null)) continue;
                direct = false; min = max = exp = 0;
                foreach (var y in ys.Values) { min += y.Min * h.Qty; max += y.Max * h.Qty; exp += y.Exp * h.Qty; }
                vias.TryGetValue((kind, h.Id), out via);
            }
            var tk = (h.Ox, h.Oy, h.Z);
            if (!tiles.TryGetValue(tk, out var tile))
                tiles[tk] = tile = new PartsTile { X = h.Ox, Y = h.Oy, Z = h.Z, InGame = h.InGame, Dist = h.Dist, Dir = h.Dir };
            tile.Min += min; tile.Max += max; tile.Exp += exp;
            tile.Hits.Add((h, min, max, direct));
            var lk = (h.Ox, h.Oy, h.Z, h.Id, h.Kind, direct);
            if (!lines.TryGetValue(lk, out var line))
            {
                lines[lk] = line = new PartsLine { Id = h.Id, Kind = h.Kind, Direct = direct, Via = via };
                tile.Lines.Add(line);
            }
            line.Qty += h.Qty; line.Stacks++; line.Min += min; line.Max += max; line.Exp += exp;
        }
        foreach (var t in tiles.Values)
        {
            t.Lines.Sort((a, b) => b.Exp.CompareTo(a.Exp));
            t.Hits.Sort((a, b) => b.max.CompareTo(a.max));
            res.TotalMin += t.Min; res.TotalMax += t.Max; res.TotalExp += t.Exp;
        }
        res.Tiles = tiles.Values.ToList();
        return res;
    }
}
