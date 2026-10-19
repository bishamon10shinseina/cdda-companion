using System.Text.Json;
using CddaCompanion.Core;
using CddaCompanion.Game;

namespace CddaCompanion.Features;

public sealed class SlotInfo
{
    public string Id, Label, GameDate, Character;
    public DateTime CreatedUtc;
    public long Bytes, Files;
    public bool Exists;
}

/// <summary>
/// Save slots. Every operation copies into a temporary folder first and only renames it into place once
/// the copy is complete and verified, so a complete save always exists somewhere.
/// The live save is only written by Restore.
/// </summary>
public sealed class Backups
{
    readonly Config _cfg;
    readonly World _world;
    public Backups(Config cfg, World world) { _cfg = cfg; _world = world; }

    public const string Undo = "undo";
    string Root => _cfg.BackupsDir;
    string SlotDir(string id) => Path.Combine(Root, id == Undo ? Undo : "slot" + id);
    static string MetaPath(string dir) => Path.Combine(dir, "slot.json");

    public IEnumerable<string> SlotIds()
    {
        for (int i = 1; i <= _cfg.Slots; i++) yield return i.ToString();
        yield return Undo;
    }

    public bool IsValidId(string id) => id == Undo || (int.TryParse(id, out var n) && n >= 1 && n <= _cfg.Slots && n.ToString() == id);

    public List<SlotInfo> List() => SlotIds().Select(Read).ToList();

    public SlotInfo Read(string id)
    {
        var dir = SlotDir(id);
        var info = new SlotInfo { Id = id };
        if (!Directory.Exists(dir)) return info;
        info.Exists = true;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(MetaPath(dir)));
            var r = doc.RootElement;
            info.Label = r.TryGetProperty("label", out var l) ? l.GetString() : "";
            info.GameDate = r.TryGetProperty("gameDate", out var g) ? g.GetString() : "";
            info.Character = r.TryGetProperty("character", out var c) ? c.GetString() : "";
            info.CreatedUtc = r.TryGetProperty("created", out var t) ? t.GetDateTime() : Directory.GetLastWriteTimeUtc(dir);
            info.Bytes = r.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0;
            info.Files = r.TryGetProperty("files", out var f) ? f.GetInt64() : 0;
        }
        catch
        {
            info.CreatedUtc = Directory.GetLastWriteTimeUtc(dir);
            info.Label = "(no slot info)";
        }
        return info;
    }

    // ---------------------------------------------------------------- backup
    public string Backup(string id, string label, JobContext ctx)
    {
        if (!IsValidId(id) || id == Undo) throw new InvalidOperationException("Invalid slot");
        Directory.CreateDirectory(Root);
        string tmp = Path.Combine(Root, $".tmp-slot{id}-{DateTime.UtcNow.Ticks}");
        try
        {
            var (files, bytes) = CopyTree(_world.Dir, tmp, ctx, "Copying save");
            ctx.Progress("Checking the copy");
            VerifyArchives(tmp);
            WriteMeta(tmp, label, files, bytes, ReadCharacterQuiet(new World(tmp)));
            ReplaceDir(SlotDir(id), tmp);
            tmp = null;
            return $"Backed up to slot {id}.";
        }
        finally { if (tmp != null) TryDelete(tmp); }
    }

    // ---------------------------------------------------------------- restore
    public string Restore(string id, JobContext ctx)
    {
        if (!IsValidId(id)) throw new InvalidOperationException("Invalid slot");
        string src = SlotDir(id);
        if (!Directory.Exists(src)) throw new InvalidOperationException($"Slot {id} is empty.");
        string live = _world.Dir;
        string parent = Path.GetDirectoryName(Path.GetFullPath(live).TrimEnd(Path.DirectorySeparatorChar));
        string staging = Path.Combine(parent, $".{Path.GetFileName(live)}.restoring-{DateTime.UtcNow.Ticks}");
        string oldUndo = null;
        bool liveMoved = false;
        try
        {
            // 1. complete, verified copy of the slot next to the live save
            CopyTree(src, staging, ctx, $"Copying slot {id}", skipFile: "slot.json");
            ctx.Progress("Checking the copy");
            VerifyArchives(staging);

            // 2. the current save becomes the undo slot
            string undo = SlotDir(Undo);
            if (Directory.Exists(live))
            {
                Directory.CreateDirectory(Root);
                if (Directory.Exists(undo))
                {
                    oldUndo = Path.Combine(Root, $".old-undo-{DateTime.UtcNow.Ticks}");
                    Directory.Move(undo, oldUndo);
                }
                ctx.Progress("Keeping your current save in the undo slot");
                var current = ReadCharacterQuiet(_world);
                MoveDir(live, undo, ctx);
                liveMoved = true;
                var (f, b) = CountTree(undo);
                WriteMeta(undo, id == Undo ? "Before undoing a restore" : $"Before loading slot {id}", f, b, current);
            }

            // 3. swap the restored copy into place
            Directory.Move(staging, live);
            staging = null;
            if (oldUndo != null) { TryDelete(oldUndo); oldUndo = null; }
            return id == Undo ? "Undo done: the save from before the last restore is back." : $"Loaded slot {id}. The save you had is in the undo slot.";
        }
        catch
        {
            // put things back the way they were as far as possible
            if (liveMoved && !Directory.Exists(live) && Directory.Exists(SlotDir(Undo)))
                try { Directory.Move(SlotDir(Undo), live); } catch { }
            if (oldUndo != null && !Directory.Exists(SlotDir(Undo)))
                try { Directory.Move(oldUndo, SlotDir(Undo)); oldUndo = null; } catch { }
            throw;
        }
        finally
        {
            if (staging != null) TryDelete(staging);
        }
    }

    public string Delete(string id)
    {
        if (!IsValidId(id)) throw new InvalidOperationException("Invalid slot");
        var dir = SlotDir(id);
        if (!Directory.Exists(dir)) return $"Slot {id} was already empty.";
        string trash = Path.Combine(Root, $".deleting-{DateTime.UtcNow.Ticks}");
        Directory.Move(dir, trash);
        TryDelete(trash);
        return id == Undo ? "Undo slot cleared." : $"Slot {id} cleared.";
    }

    // ---------------------------------------------------------------- helpers
    static CharacterInfo ReadCharacterQuiet(World w)
    {
        try { return w.ReadCharacter(); } catch { return null; }
    }

    static void WriteMeta(string dir, string label, long files, long bytes, CharacterInfo ch)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("label", label ?? "");
            w.WriteString("created", DateTime.UtcNow);
            w.WriteString("gameDate", ch?.DateText ?? "");
            w.WriteNumber("turn", ch?.Turn ?? 0);
            w.WriteString("character", ch?.Name ?? "");
            w.WriteNumber("files", files);
            w.WriteNumber("bytes", bytes);
            w.WriteEndObject();
        }
        File.WriteAllBytes(MetaPath(dir), ms.ToArray());
    }

    /// <summary>Every zzip must have an intact footer and entries; a torn copy means the game was writing.</summary>
    static void VerifyArchives(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*.zzip", SearchOption.AllDirectories))
        {
            ZzipArchive z;
            try { z = ZzipArchive.Read(f); }
            catch (Exception ex) { throw new IOException($"The copy of {Path.GetFileName(f)} is incomplete ({ex.Message}). The game was probably saving; try again in a moment."); }
            foreach (var e in z.Entries)
                if (!z.Verify(e)) throw new IOException($"The copy of {Path.GetFileName(f)} has a damaged entry ({e.Name}). Try again in a moment.");
        }
    }

    static (long files, long bytes) CountTree(string dir)
    {
        long files = 0, bytes = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (f.Name == "slot.json") continue;
            files++; bytes += f.Length;
        }
        return (files, bytes);
    }

    static (long files, long bytes) CopyTree(string src, string dst, JobContext ctx, string what, string skipFile = null)
    {
        var all = new DirectoryInfo(src).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => skipFile == null || !string.Equals(Path.GetRelativePath(src, f.FullName), skipFile, StringComparison.Ordinal))
            .ToList();
        long total = all.Sum(f => f.Length), done = 0, count = 0;
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
        var buffer = new byte[1 << 20];
        foreach (var f in all)
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, f.FullName));
            using (var input = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { output.Write(buffer, 0, n); done += n; }
            }
            File.SetLastWriteTimeUtc(target, f.LastWriteTimeUtc);
            count++;
            if (count % 32 == 0) ctx.Progress(what, done, total);
        }
        ctx.Progress(what, total, total);
        return (count, total);
    }

    static void MoveDir(string src, string dst, JobContext ctx)
    {
        try { Directory.Move(src, dst); }
        catch (IOException)
        {
            // different drive: copy, then remove the original
            CopyTree(src, dst, ctx, "Moving your current save to the undo slot");
            Directory.Delete(src, true);
        }
    }

    void ReplaceDir(string target, string fresh)
    {
        string old = null;
        if (Directory.Exists(target))
        {
            old = Path.Combine(Root, $".old-{Path.GetFileName(target)}-{DateTime.UtcNow.Ticks}");
            Directory.Move(target, old);
        }
        Directory.Move(fresh, target);
        if (old != null) TryDelete(old);
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
