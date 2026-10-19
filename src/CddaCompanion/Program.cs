using System.Diagnostics;
using CddaCompanion;

try { return Run(args); }
catch (Exception ex)
{
    // never just vanish: a double-clicked console window would close before anyone could read why
    Console.Error.WriteLine();
    Console.Error.WriteLine("CDDA Companion stopped because of an error:");
    Console.Error.WriteLine(ex);
    PauseIfDoubleClicked();
    return 1;
}

static int Run(string[] args)
{
Console.OutputEncoding = System.Text.Encoding.UTF8;
string configPath = args.FirstOrDefault(a => a.EndsWith(".json")) is { } c ? Path.GetFullPath(c) : Config.DefaultPath();
bool noBrowser = args.Contains("--no-browser");

Console.WriteLine($"CDDA Companion {App.Version}");

// "cdda-companion --stop" quits whatever copy is running
if (args.Contains("--stop"))
{
    var running = Instance.HandleOthers();
    if (running != null)
    {
        var info = Instance.Read();
        Console.WriteLine($"Stopping the copy at {running}…");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Add("X-Companion-Token", info.Token);
            http.PostAsync(running + "api/shutdown", new StringContent("{}")).GetAwaiter().GetResult();
        }
        catch { }
    }
    Console.WriteLine("Done.");
    return 0;
}

var cfg = Config.Load(configPath);
bool firstRun = !cfg.Exists;
Console.WriteLine($"Settings: {cfg.FilePath}{(firstRun ? " (new)" : "")}");
// fill in what we can find by ourselves (game folder next to us, the world played last)
var filled = Setup.Complete(cfg);
if (filled != null) Console.WriteLine("Found " + filled + ".");
var problems = cfg.Problems();
if (problems.Count == 0 && (filled != null || firstRun))
{
    try { cfg.Save(); } catch (Exception ex) { Console.WriteLine($"  (couldn't write {cfg.FilePath}: {ex.Message})"); }
}

// one copy at a time: reuse a running one, stop old ones
var existing = Instance.HandleOthers();
if (existing != null)
{
    Console.WriteLine($"The tool is already running at {existing}. Opening it.");
    if (!noBrowser) OpenBrowser(existing);
    return 0;
}

var app = new App(cfg);
if (app.Ready)
{
    Console.WriteLine($"World:    {cfg.WorldDir}");
    Console.WriteLine($"Data:     {cfg.DataRoot}");
    app.LoadGameDataInBackground();
}
else
{
    foreach (var p in problems) Console.WriteLine("  - " + p);
    Console.WriteLine("Finish the setup in the browser page that opens.");
}
var server = new Server(app);
server.Start(cfg.Port);
Instance.Write(server.Port, app.Token);
app.StartAutoRefresh();
app.StartIdleWatch();

string url = $"http://127.0.0.1:{server.Port}/";
Console.WriteLine();
Console.WriteLine($"Open {url} in your browser.{(app.Ready ? "" : " It starts with a short setup.")}");
Console.WriteLine("Stop it with the Quit button on the page, Ctrl+C here, or: cdda-companion --stop");
if (cfg.IdleExitMinutes > 0) Console.WriteLine($"It also quits by itself when no page has been open for {cfg.IdleExitMinutes} minutes.");
if (cfg.OpenBrowser && !noBrowser) OpenBrowser(url);

Console.CancelKeyPress += (_, e) => { e.Cancel = true; app.RequestExit("Ctrl+C"); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => { app.RequestExit("system"); app.WaitForJob(TimeSpan.FromMinutes(2)); Instance.Remove(); };
app.Quit.Wait();
app.WaitForJob(TimeSpan.FromMinutes(10));
Instance.Remove();
Console.WriteLine("Bye.");
return 0;
}

static void PauseIfDoubleClicked()
{
    try
    {
        if (OperatingSystem.IsWindows() && !Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Press Enter to close this window.");
            Console.ReadLine();
        }
    }
    catch { }
}

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
        else Process.Start(new ProcessStartInfo("xdg-open", url) { RedirectStandardOutput = true, RedirectStandardError = true });
    }
    catch { Console.WriteLine("(Couldn't open the browser by myself; open the address above.)"); }
}
