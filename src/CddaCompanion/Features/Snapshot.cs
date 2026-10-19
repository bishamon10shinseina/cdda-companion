using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CddaCompanion.Core;
using CddaCompanion.Game;

namespace CddaCompanion.Features;

public sealed class SnapshotInfo
{
    public DateTime TakenUtc, SaveTimeUtc;
    public long Files, Bytes;
    public int Errors;
    public bool Exists => TakenUtc != default;
}

/// <summary>
/// Keeps an extracted copy of the world's map chunks for searching. The live save is only ever read.
/// Each archive entry carries a checksum, so a refresh only unpacks chunks that changed since last time.
/// </summary>
public sealed class Snapshot
{
    readonly Config _cfg;
    readonly World _world;
    public Snapshot(Config cfg, World world) { _cfg = cfg; _world = world; }

    string Root => _cfg.SnapshotDir;
    string ManifestPath => Path.Combine(Root, "manifest.tsv");
    string InfoPath => Path.Combine(Root, "snapshot.json");

    /// <summary>Folder the item search reads. Uncompressed worlds are searched in place.</summary>
    public string MapsDir => _world.Compressed ? Path.Combine(Root, "maps") : _world.MapsDir;

    public SnapshotInfo Info()
    {
        if (!_world.Compressed)
            return new SnapshotInfo { TakenUtc = DateTime.UtcNow, SaveTimeUtc = _world.LastSaveTime() };
        try
        {
            if (!File.Exists(InfoPath)) return new SnapshotInfo();
            using var doc = JsonDocument.Parse(File.ReadAllBytes(InfoPath));
            var r = doc.RootElement;
            return new SnapshotInfo
            {
                TakenUtc = r.GetProperty("taken").GetDateTime(),
                SaveTimeUtc = r.GetProperty("saveTime").GetDateTime(),
                Files = r.GetProperty("files").GetInt64(),
                Bytes = r.GetProperty("bytes").GetInt64(),
                Errors = r.TryGetProperty("errors", out var e) ? e.GetInt32() : 0
            };
        }
        catch { return new SnapshotInfo(); }
    }

    // "different" rather than "newer": loading a backup puts older files in place, and the snapshot must follow
    public bool IsStale(SnapshotInfo info) => !info.Exists || _world.LastSaveTime() != info.SaveTimeUtc;

    public string Refresh(JobContext ctx)
    {
        if (!_world.Compressed) return "This world isn't compressed; the search reads it directly.";
        var taken = DateTime.UtcNow;
        var saveTime = _world.LastSaveTime();
        var mapsOut = Path.Combine(Root, "maps");
        Directory.CreateDirectory(mapsOut);
        string dict = File.Exists(_world.MapsDict) ? _world.MapsDict : null;

        var old = LoadManifest();
        var oldStamps = LoadStamps();
        var stamps = new ConcurrentDictionary<string, (long ticks, long size)>(StringComparer.Ordinal);
        var archives = Directory.Exists(_world.MapsDir)
            ? Directory.GetFiles(_world.MapsDir, "*.zzip") : Array.Empty<string>();
        var manifest = new ConcurrentDictionary<string, Dictionary<string, ulong>>(StringComparer.Ordinal);
        var errors = new ConcurrentBag<string>();
        long done = 0, written = 0, files = 0, bytes = 0;
        ctx.Progress("Reading map archives", 0, archives.Length);

        Parallel.ForEach(archives, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, path =>
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string outDir = Path.Combine(mapsOut, name);
            old.TryGetValue(name, out var before);
            var fi = new FileInfo(path);
            var stamp = (fi.LastWriteTimeUtc.Ticks, fi.Length);
            // untouched since last time: nothing to read
            if (before != null && oldStamps.TryGetValue(name, out var st) && st == stamp && Directory.Exists(outDir))
            {
                manifest[name] = before;
                stamps[name] = stamp;
                Interlocked.Add(ref files, before.Count);
                long d0 = Interlocked.Increment(ref done);
                if (d0 % 64 == 0 || d0 == archives.Length) ctx.Progress($"Unpacked {written:N0} changed map chunks", d0, archives.Length);
                return;
            }
            try
            {
                var z = ZzipArchive.ReadStable(path);
                Directory.CreateDirectory(outDir);
                var now = new Dictionary<string, ulong>(StringComparer.Ordinal);
                var decompressor = Zstd.For(dict);
                foreach (var e in z.Entries)
                {
                    if (e.Name.Contains('/') || e.Name.Contains('\\') || e.Name.Contains("..")) continue;
                    string target = Path.Combine(outDir, e.Name);
                    if (before != null && before.TryGetValue(e.Name, out var sum) && sum == e.Checksum && File.Exists(target))
                    {
                        now[e.Name] = sum;
                        Interlocked.Increment(ref files);
                        continue;
                    }
                    if (!z.Verify(e)) { errors.Add($"{name}/{e.Name}: checksum mismatch"); continue; }
                    var content = z.Extract(e, decompressor);
                    var tmp = target + ".tmp";
                    File.WriteAllBytes(tmp, content);
                    File.Move(tmp, target, true);
                    now[e.Name] = e.Checksum;
                    Interlocked.Increment(ref written);
                    Interlocked.Increment(ref files);
                }
                foreach (var f in Directory.EnumerateFiles(outDir))
                    if (!now.ContainsKey(Path.GetFileName(f))) File.Delete(f);
                manifest[name] = now;
                stamps[name] = stamp;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
                if (before != null) manifest[name] = before;   // keep the previous extract of this archive
            }
            long d = Interlocked.Increment(ref done);
            if (d % 16 == 0 || d == archives.Length) ctx.Progress($"Unpacked {written:N0} changed map chunks", d, archives.Length);
        });

        var live = new HashSet<string>(archives.Select(Path.GetFileNameWithoutExtension), StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(mapsOut))
            if (!live.Contains(Path.GetFileName(dir))) Directory.Delete(dir, true);

        SaveManifest(manifest);
        SaveStamps(stamps);
        bytes = DirBytes(mapsOut);
        SaveInfo(taken, saveTime, files, bytes, errors.Count);
        foreach (var e in errors.Take(20)) Console.Error.WriteLine("[snapshot] " + e);
        string msg = $"Snapshot updated: {written:N0} of {files:N0} map chunks changed.";
        if (!errors.IsEmpty) msg += $" {errors.Count} archive(s) couldn't be read (the game may have been saving); they keep their previous copy.";
        return msg;
    }

    string StampsPath => Path.Combine(Root, "archives.tsv");

    Dictionary<string, (long, long)> LoadStamps()
    {
        var res = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        if (!File.Exists(StampsPath)) return res;
        foreach (var line in File.ReadLines(StampsPath))
        {
            var p = line.Split('\t');
            if (p.Length == 3 && long.TryParse(p[1], out var t) && long.TryParse(p[2], out var s)) res[p[0]] = (t, s);
        }
        return res;
    }

    void SaveStamps(IDictionary<string, (long ticks, long size)> stamps)
    {
        var sb = new StringBuilder();
        foreach (var (a, (t, s)) in stamps.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(a).Append('\t').Append(t).Append('\t').Append(s).Append('\n');
        var tmp = StampsPath + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        File.Move(tmp, StampsPath, true);
    }

    static long DirBytes(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }
    }

    Dictionary<string, Dictionary<string, ulong>> LoadManifest()
    {
        var res = new Dictionary<string, Dictionary<string, ulong>>(StringComparer.Ordinal);
        if (!File.Exists(ManifestPath)) return res;
        foreach (var line in File.ReadLines(ManifestPath))
        {
            var p = line.Split('\t');
            if (p.Length != 3 || !ulong.TryParse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var sum)) continue;
            if (!res.TryGetValue(p[0], out var d)) res[p[0]] = d = new Dictionary<string, ulong>(StringComparer.Ordinal);
            d[p[1]] = sum;
        }
        return res;
    }

    void SaveManifest(IDictionary<string, Dictionary<string, ulong>> m)
    {
        var sb = new StringBuilder();
        foreach (var (a, entries) in m.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (var (e, sum) in entries) sb.Append(a).Append('\t').Append(e).Append('\t').Append(sum.ToString("x16")).Append('\n');
        var tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        File.Move(tmp, ManifestPath, true);
    }

    void SaveInfo(DateTime taken, DateTime saveTime, long files, long bytes, int errors)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("taken", taken); w.WriteString("saveTime", saveTime);
            w.WriteNumber("files", files); w.WriteNumber("bytes", bytes); w.WriteNumber("errors", errors);
            w.WriteEndObject();
        }
        var tmp = InfoPath + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, InfoPath, true);
    }
}
