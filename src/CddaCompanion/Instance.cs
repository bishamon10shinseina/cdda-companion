using System.Diagnostics;
using System.Text.Json;

namespace CddaCompanion;

/// <summary>
/// Keeps exactly one copy of the tool running per user.
/// The running copy writes a small file (pid, port, version, token). A new start either reuses a healthy
/// copy of the same version (just opens the browser), or asks an older copy to quit; it waits for any backup
/// or load that copy is doing. Copies too old to know about this (0.1, 0.2) are stopped directly.
/// </summary>
public static class Instance
{
    public sealed record Info(int Pid, int Port, string Version, string Token);

    static string LockPath => Path.Combine(Path.GetTempPath(), $"cdda-companion-{Sanitize(Environment.UserName)}.json");

    static string Sanitize(string s) => string.Concat((s ?? "user").Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    public static Info Read()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(LockPath));
            var r = doc.RootElement;
            return new Info(r.GetProperty("pid").GetInt32(), r.GetProperty("port").GetInt32(),
                            r.GetProperty("version").GetString(), r.GetProperty("token").GetString());
        }
        catch { return null; }
    }

    public static void Write(int port, string token)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("pid", Environment.ProcessId); w.WriteNumber("port", port);
            w.WriteString("version", App.Version); w.WriteString("token", token);
            w.WriteEndObject();
        }
        var tmp = LockPath + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, LockPath, true);
    }

    public static void Remove()
    {
        try { if (Read()?.Pid == Environment.ProcessId) File.Delete(LockPath); } catch { }
    }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && IsOurs(p); }
        catch { return false; }
    }

    static bool IsOurs(Process p)
    {
        try { return p.ProcessName == "cdda-companion"; } catch { return false; }
    }

    static HttpClient Client(string token)
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        c.DefaultRequestHeaders.Add("X-Companion-Token", token);
        return c;
    }

    static bool Responds(Info i)
    {
        try
        {
            using var c = Client(i.Token);
            using var r = c.GetAsync($"http://127.0.0.1:{i.Port}/api/status").GetAwaiter().GetResult();
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Returns the address of a running copy of this same version, if there is one. Stops anything else.</summary>
    public static string HandleOthers()
    {
        var info = Read();
        if (info != null && info.Pid != Environment.ProcessId && Alive(info.Pid))
        {
            if (info.Version == App.Version && Responds(info))
            {
                StopLegacyCopies(info.Pid);
                return $"http://127.0.0.1:{info.Port}/";
            }

            Console.WriteLine($"Stopping the older copy of the tool ({info.Version})…");
            AskToQuit(info);
        }
        StopLegacyCopies(info?.Pid ?? -1);
        return null;
    }

    static void AskToQuit(Info info)
    {
        try
        {
            using var c = Client(info.Token);
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            c.PostAsync($"http://127.0.0.1:{info.Port}/api/shutdown", content).GetAwaiter().GetResult();
        }
        catch { }
        // it finishes any backup/load first; give it time
        var until = DateTime.UtcNow.AddMinutes(5);
        while (Alive(info.Pid) && DateTime.UtcNow < until) Thread.Sleep(250);
        if (Alive(info.Pid)) Kill(info.Pid);
    }

    /// <summary>Copies from before single-instance support have no lock file and no quit command.</summary>
    static void StopLegacyCopies(int alreadyHandled)
    {
        int stopped = 0;
        foreach (var p in Process.GetProcessesByName("cdda-companion"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId || p.Id == alreadyHandled) continue;
                try { p.Kill(); p.WaitForExit(3000); stopped++; } catch { }
            }
        }
        if (stopped > 0) Console.WriteLine($"Stopped {stopped} leftover cop{(stopped == 1 ? "y" : "ies")} of the tool.");
    }

    static void Kill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); if (IsOurs(p)) { p.Kill(); p.WaitForExit(3000); } } catch { }
    }
}
