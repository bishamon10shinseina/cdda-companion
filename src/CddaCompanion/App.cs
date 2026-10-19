using CddaCompanion.Features;
using CddaCompanion.Game;

namespace CddaCompanion;

/// <summary>Everything the server needs, shared between requests.</summary>
public sealed class App
{
    public static readonly string Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // swapped as a set when the settings change (see Apply); requests read whatever is current
    public Config Config { get; private set; }
    public World World { get; private set; }
    public Snapshot Snapshot { get; private set; }
    public Backups Backups { get; private set; }
    public Filters Filters { get; private set; }
    /// <summary>False until there's a game folder and a world: the page then shows the setup screen.</summary>
    public bool Ready { get; private set; }
    int _generation;
    public readonly Jobs Jobs = new();
    public readonly string Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    volatile GameData _game;
    public GameData Game => _game;
    public string GameError;
    public readonly ManualResetEventSlim Quit = new();
    public DateTime LastSeenUtc = DateTime.UtcNow;   // last time a page asked for status
    public double GameLoadSeconds;

    public App(Config cfg) { Use(cfg); }

    void Use(Config cfg)
    {
        Ready = cfg.Problems().Count == 0;
        if (Ready) Setup.MigrateDataLayout(cfg);
        var world = new World(cfg.WorldDir);
        Snapshot = new Snapshot(cfg, world);
        Backups = new Backups(cfg, world);
        Filters = new Filters(cfg.DataRoot);
        World = world;
        Config = cfg;
    }

    /// <summary>New settings take effect at once: other world, other game folder, no restart needed.</summary>
    public void Apply(Config cfg)
    {
        if (Jobs.Running) throw new InvalidOperationException($"Wait until \"{Jobs.Name}\" has finished.");
        Interlocked.Increment(ref _generation);
        _game = null; GameError = null;
        Use(cfg);
        if (Ready) LoadGameDataInBackground();
    }

    public void LoadGameDataInBackground()
    {
        int gen = _generation;
        var cfg = Config;
        new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Colors.LoadFromGame(cfg.GameDir);
                var g = GameData.Load(cfg.GameDir, cfg.WorldDir);
                if (gen != _generation) return;      // settings changed meanwhile; a newer load is running
                _game = g;
                GameLoadSeconds = sw.Elapsed.TotalSeconds;
                Console.WriteLine($"Game data: {g.Oters.Count:N0} map tile types, {g.CountByCharges.Count:N0} items, {g.DataDirs.Count} data folders ({GameLoadSeconds:F1} s)");
                if (g.MissingMods.Count > 0)
                    Console.WriteLine("  Mods in mods.json not found in the game folder: " + string.Join(", ", g.MissingMods));
            }
            catch (Exception ex) { if (gen == _generation) GameError = ex.Message; Console.Error.WriteLine("Game data failed to load: " + ex); }
        }) { IsBackground = true, Name = "game-data" }.Start();
    }

    /// <summary>Refreshes the snapshot by itself when the game has saved since the last one.</summary>
    public void StartAutoRefresh()
    {
        new Thread(() =>
        {
            Thread.Sleep(3000);
            while (true)
            {
                try
                {
                    if (Ready && Config.AutoRefreshSnapshot && !Jobs.Running && World.Compressed && World.LastSaveTime() != DateTime.MinValue)
                    {
                        var info = Snapshot.Info();
                        var saved = World.LastSaveTime();
                        // wait until the game has finished writing (no change for a few seconds)
                        if ((!info.Exists || saved != info.SaveTimeUtc) && DateTime.UtcNow - saved > TimeSpan.FromSeconds(4))
                            Jobs.TryStart("snapshot", Snapshot.Refresh);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("auto-refresh: " + ex.Message); }
                Thread.Sleep(5000);
            }
        }) { IsBackground = true, Name = "auto-refresh" }.Start();
    }

    public void RequestExit(string why)
    {
        if (Quit.IsSet) return;
        if (Jobs.Running) Console.WriteLine($"Quitting ({why}) as soon as \"{Jobs.Name}\" finishes…");
        else Console.WriteLine($"Quitting ({why}).");
        Quit.Set();
    }

    public void WaitForJob(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (Jobs.Running && DateTime.UtcNow < until) Thread.Sleep(200);
    }

    /// <summary>Quits when no page has been open for a while, so forgotten copies don't pile up.</summary>
    public void StartIdleWatch()
    {
        new Thread(() =>
        {
            while (!Quit.IsSet)
            {
                Thread.Sleep(15000);
                if (Config.IdleExitMinutes > 0 && !Jobs.Running && DateTime.UtcNow - LastSeenUtc > TimeSpan.FromMinutes(Config.IdleExitMinutes))
                    RequestExit($"no page open for {Config.IdleExitMinutes} minute{(Config.IdleExitMinutes == 1 ? "" : "s")}");
            }
        }) { IsBackground = true, Name = "idle-watch" }.Start();
    }

    public (int x, int y)? BasePos => ItemSearch.ParseInGame(Config.Base);
}
