using System.Text.Json;

namespace CddaCompanion.Features;

/// <summary>
/// What each category search leaves out, kept in the tool's own data folder (never the save):
/// whole sets (an ammo type like "pebbles") and single items. A category nobody has touched yet starts
/// from its default: "Firearms only" for ammo, magazines and guns, everything for the rest.
/// </summary>
public sealed class Filters
{
    public sealed class Filter
    {
        public HashSet<string> Sets = new(StringComparer.Ordinal);
        public HashSet<string> Items = new(StringComparer.Ordinal);
        public bool Saved;   // false = default, never edited
    }

    readonly string _path;
    readonly object _lock = new();
    Dictionary<string, Filter> _all;

    public Filters(string dataRoot) { _path = Path.Combine(dataRoot, "category-filters.json"); }

    Dictionary<string, Filter> All()
    {
        if (_all != null) return _all;
        _all = new Dictionary<string, Filter>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
                foreach (var c in doc.RootElement.EnumerateObject())
                {
                    var f = new Filter { Saved = true };
                    if (c.Value.TryGetProperty("sets", out var s) && s.ValueKind == JsonValueKind.Array)
                        foreach (var x in s.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) f.Sets.Add(x.GetString());
                    if (c.Value.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Array)
                        foreach (var x in i.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) f.Items.Add(x.GetString());
                    _all[c.Name] = f;
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("category-filters.json: " + ex.Message); }
        return _all;
    }

    public Filter Get(string cat, Func<IEnumerable<string>> defaultSets)
    {
        lock (_lock)
        {
            if (All().TryGetValue(cat, out var f)) return f;
            var d = new Filter();
            foreach (var s in defaultSets()) d.Sets.Add(s);
            return d;
        }
    }

    public void Set(string cat, IEnumerable<string> sets, IEnumerable<string> items)
    {
        lock (_lock)
        {
            All()[cat] = new Filter { Saved = true, Sets = new HashSet<string>(sets, StringComparer.Ordinal), Items = new HashSet<string>(items, StringComparer.Ordinal) };
            Save();
        }
    }

    /// <summary>Back to the category's default.</summary>
    public void Reset(string cat)
    {
        lock (_lock) { if (All().Remove(cat)) Save(); }
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        var tmp = _path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var (k, f) in All().OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(k);
                w.WriteStartArray("sets"); foreach (var x in f.Sets.Order(StringComparer.Ordinal)) w.WriteStringValue(x); w.WriteEndArray();
                w.WriteStartArray("items"); foreach (var x in f.Items.Order(StringComparer.Ordinal)) w.WriteStringValue(x); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        File.Move(tmp, _path, true);
    }
}
