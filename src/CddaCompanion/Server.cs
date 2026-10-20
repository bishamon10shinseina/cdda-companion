using System.Net;
using System.Text;
using System.Text.Json;
using CddaCompanion.Features;
using CddaCompanion.Game;

namespace CddaCompanion;

/// <summary>Tiny HTTP server bound to 127.0.0.1 only. The page gets a random token; every API call must send it back.</summary>
public sealed class Server
{
    readonly App _app;
    HttpListener _listener;
    public int Port { get; private set; }

    public Server(App app) { _app = app; }

    public void Start(int preferredPort)
    {
        for (int p = preferredPort; p < preferredPort + 20; p++)
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{p}/");
            try { l.Start(); _listener = l; Port = p; break; }
            catch (HttpListenerException) { l.Close(); }
        }
        if (_listener == null) throw new InvalidOperationException($"No free port between {preferredPort} and {preferredPort + 19}.");
        new Thread(Loop) { IsBackground = true, Name = "http" }.Start();
    }

    void Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); } catch { break; }
            ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request; var res = ctx.Response;
        try
        {
            // DNS-rebinding guard: only accept our own host names
            var host = req.UserHostName ?? "";
            if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}") { Status(res, 403, "forbidden"); return; }
            string path = req.Url.AbsolutePath;
            if (!path.StartsWith("/api/"))
            {
                ServeStatic(res, path);
                return;
            }
            if (req.Headers["X-Companion-Token"] != _app.Token) { Status(res, 403, "bad token"); return; }
            Route(req, res, path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[http] {req.Url}: {ex}");
            try { Json(res, w => { w.WriteStartObject(); w.WriteString("error", ex.Message); w.WriteEndObject(); }, 500); } catch { }
        }
        finally { try { res.Close(); } catch { } }
    }

    static readonly HashSet<string> SetupPaths = new() { "/api/status", "/api/setup/scan", "/api/settings", "/api/shutdown" };

    void Route(HttpListenerRequest req, HttpListenerResponse res, string path)
    {
        var q = req.QueryString;
        bool post = req.HttpMethod == "POST";
        if (!_app.Ready && !SetupPaths.Contains(path)) { Error(res, "Finish the setup first."); return; }
        switch (path)
        {
            case "/api/setup/scan": SetupScanJson(res, q["gameDir"], q["saves"]); return;
            case "/api/status": _app.LastSeenUtc = DateTime.UtcNow; StatusJson(res); return;
            case "/api/overmap": OvermapJson(res, Int(q["x"]), Int(q["y"]), Int(q["z"]), q["mode"] == "full", q["ts"]); return;
            case "/api/tilesets": TilesetsJson(res); return;
            case "/api/tileset": TilesetJson(res, q["name"] ?? ""); return;
            case "/api/gfx": GfxFile(res, q["name"] ?? "", q["file"] ?? ""); return;
            case "/api/search": SearchJson(res, q["q"] ?? "", q["z"], q["from"], q["ids"] == "1", q["sort"] == "qty", q["src"] == "1", q["cat"], q["set"]); return;
            case "/api/categories": CategoriesJson(res); return;
            case "/api/category": CategoryJson(res, q["cat"] ?? ""); return;
            case "/api/slots": SlotsJson(res); return;
            case "/api/codex/search": CodexSearchJson(res, q["q"] ?? "", q["kind"]); return;
            case "/api/codex/entry": CodexEntryJson(res, q["kind"] ?? "", q["id"] ?? ""); return;
        }
        if (!post) { Status(res, 404, "not found"); return; }
        var body = ReadBody(req);
        string Field(string k) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (path)
        {
            case "/api/settings": SaveSettings(res, body); return;
            case "/api/settings/base":
            {
                var b = (Field("base") ?? "").Trim();
                if (b.Length > 0 && ItemSearch.ParseInGame(b) == null) { Error(res, "Write the base like the game's map shows it, e.g. 0'12, 0'34."); return; }
                _app.Config.Base = b;
                try { _app.Config.Save(); } catch (Exception ex) { Error(res, "Couldn't save the settings: " + ex.Message); return; }
                Json(res, w => { w.WriteStartObject(); w.WriteBoolean("ok", true); w.WriteString("base", b); w.WriteEndObject(); });
                return;
            }
            case "/api/shutdown":
            {
                bool busy = _app.Jobs.Running;
                Json(res, w => { w.WriteStartObject(); w.WriteBoolean("ok", true); w.WriteBoolean("waiting", busy); w.WriteString("job", busy ? _app.Jobs.Name : null); w.WriteEndObject(); });
                _app.RequestExit("asked to quit");
                return;
            }
            case "/api/category/filter":
            {
                var game = _app.Game;
                var cat = Field("cat");
                if (game?.Categories.Get(cat) == null) { Error(res, "Unknown category."); return; }
                if (body.TryGetProperty("reset", out var rs) && rs.ValueKind == JsonValueKind.True) _app.Filters.Reset(cat);
                else
                {
                    List<string> Arr(string k) => body.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array
                        ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList() : new List<string>();
                    _app.Filters.Set(cat, Arr("sets"), Arr("items"));
                }
                CategoryJson(res, cat); return;
            }
            case "/api/snapshot/refresh":
                StartJob(res, "snapshot", _app.Snapshot.Refresh); return;
            case "/api/slots/backup":
            {
                var id = Field("slot"); var label = Field("label") ?? "";
                if (!_app.Backups.IsValidId(id) || id == Backups.Undo) { Error(res, "Pick a slot to save into."); return; }
                StartJob(res, "backup", c => _app.Backups.Backup(id, label.Length > 120 ? label[..120] : label, c)); return;
            }
            case "/api/slots/restore":
            {
                var id = Field("slot");
                if (!_app.Backups.IsValidId(id)) { Error(res, "Unknown slot."); return; }
                StartJob(res, "restore", c => _app.Backups.Restore(id, c)); return;
            }
            case "/api/slots/delete":
            {
                var id = Field("slot");
                if (!_app.Backups.IsValidId(id)) { Error(res, "Unknown slot."); return; }
                StartJob(res, "delete", _ => _app.Backups.Delete(id)); return;
            }
        }
        Status(res, 404, "not found");
    }

    void StartJob(HttpListenerResponse res, string name, Func<JobContext, string> work)
    {
        bool started = _app.Jobs.TryStart(name, work);
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("started", started);
            if (!started) w.WriteString("error", $"Busy with: {_app.Jobs.Name}. Try again when it's done.");
            w.WriteEndObject();
        });
    }

    // ---------------------------------------------------------------- endpoints
    void StatusJson(HttpListenerResponse res)
    {
        var app = _app;
        if (!app.Ready)
        {
            Json(res, w =>
            {
                w.WriteStartObject();
                w.WriteString("version", App.Version);
                w.WriteBoolean("setup", true);
                w.WriteString("configPath", app.Config.FilePath);
                w.WriteStartArray("problems"); foreach (var p in app.Config.Problems()) w.WriteStringValue(p); w.WriteEndArray();
                w.WriteEndObject();
            });
            return;
        }
        CharacterInfo ch = null; string chErr = null;
        try { ch = app.World.ReadCharacter(); } catch (Exception ex) { chErr = ex.Message; }
        var snap = app.Snapshot.Info();
        var game = app.Game;
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteString("version", App.Version);
            w.WriteString("world", app.Config.World);
            w.WriteString("worldDir", app.World.Dir);
            w.WriteString("configPath", app.Config.FilePath);
            w.WriteBoolean("compressed", app.World.Compressed);
            var saved = app.World.LastSaveTime();
            if (saved != DateTime.MinValue) w.WriteString("lastSave", saved);
            if (ch != null)
            {
                w.WriteStartObject("character");
                w.WriteString("name", ch.Name); w.WriteString("date", ch.DateText);
                w.WriteNumber("x", ch.X); w.WriteNumber("y", ch.Y); w.WriteNumber("z", ch.Z);
                w.WriteString("inGame", ItemSearch.FormatInGame(ch.X, ch.Y));
                w.WriteEndObject();
            }
            if (chErr != null) w.WriteString("characterError", chErr);
            if (app.BasePos is { } b)
            {
                w.WriteStartObject("base");
                w.WriteNumber("x", b.x); w.WriteNumber("y", b.y); w.WriteString("inGame", ItemSearch.FormatInGame(b.x, b.y));
                w.WriteEndObject();
            }
            w.WriteStartObject("snapshot");
            w.WriteBoolean("exists", snap.Exists);
            w.WriteBoolean("stale", app.Snapshot.IsStale(snap));
            if (snap.Exists) { w.WriteString("taken", snap.TakenUtc); w.WriteString("saveTime", snap.SaveTimeUtc); }
            w.WriteNumber("files", snap.Files); w.WriteNumber("bytes", snap.Bytes); w.WriteNumber("errors", snap.Errors);
            w.WriteBoolean("auto", app.Config.AutoRefreshSnapshot);
            w.WriteEndObject();
            lock (app.Jobs.Lock)
            {
                w.WriteStartObject("job");
                w.WriteBoolean("running", app.Jobs.Running);
                w.WriteString("name", app.Jobs.Name);
                w.WriteString("text", app.Jobs.Text);
                w.WriteNumber("done", app.Jobs.Done); w.WriteNumber("total", app.Jobs.Total);
                w.WriteString("result", app.Jobs.Result); w.WriteString("error", app.Jobs.Error);
                if (app.Jobs.FinishedUtc != default) w.WriteString("finished", app.Jobs.FinishedUtc);
                w.WriteEndObject();
            }
            w.WriteStartObject("game");
            w.WriteBoolean("ready", game != null);
            w.WriteString("error", app.GameError);
            if (game != null)
            {
                w.WriteNumber("terrain", game.Oters.Count); w.WriteNumber("items", game.CountByCharges.Count);
                w.WriteNumber("files", game.FilesRead); w.WriteNumber("failed", game.FilesFailed);
                w.WriteNumber("seconds", Math.Round(app.GameLoadSeconds, 2));
                w.WriteStartArray("missingMods"); foreach (var m in game.MissingMods) w.WriteStringValue(m); w.WriteEndArray();
                w.WriteStartArray("dataDirs"); foreach (var d in game.DataDirs) w.WriteStringValue(d); w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteStartArray("overmaps");
            try { foreach (var (x, y) in app.World.Overmaps()) { w.WriteStartArray(); w.WriteNumberValue(x); w.WriteNumberValue(y); w.WriteEndArray(); } } catch { }
            w.WriteEndArray();
            w.WriteStartArray("palette");
            foreach (var c in Colors.Rgb) w.WriteStringValue($"#{c[0]:x2}{c[1]:x2}{c[2]:x2}");
            w.WriteEndArray();
            w.WriteString("paletteSource", Colors.Source);
            w.WriteStartObject("disk");
            w.WriteNumber("backups", BackupsSize());
            try
            {
                var root = Directory.Exists(app.Config.DataRoot) ? app.Config.DataRoot : app.Config.GameDir;
                w.WriteNumber("free", new DriveInfo(root).AvailableFreeSpace);
            }
            catch { }
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    static long _backupsSize; static DateTime _backupsSizeAt;
    long BackupsSize()
    {
        if (DateTime.UtcNow - _backupsSizeAt > TimeSpan.FromSeconds(15) || _app.Jobs.Running || _app.Jobs.FinishedUtc > _backupsSizeAt)
        {
            _backupsSize = DirSize(_app.Config.BackupsDir);
            _backupsSizeAt = DateTime.UtcNow;
        }
        return _backupsSize;
    }

    static long DirSize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    // ---------------------------------------------------------------- tilesets
    List<TilesetInfo> _tilesetList; DateTime _tilesetListAt;
    List<TilesetInfo> TilesetList()
    {
        if (_tilesetList == null || DateTime.UtcNow - _tilesetListAt > TimeSpan.FromSeconds(30))
        {
            try { _tilesetList = Tileset.List(_app.Install.GfxRoots).Where(t => !t.Iso).ToList(); } catch { _tilesetList = new List<TilesetInfo>(); }
            _tilesetListAt = DateTime.UtcNow;
        }
        return _tilesetList;
    }

    Tileset LoadTileset(string name)
    {
        var info = TilesetList().FirstOrDefault(t => t.Name == name);
        return info == null ? null : Tileset.Load(info);
    }

    readonly Dictionary<(string, string), TileResolver> _resolvers = new();
    TileResolver ResolverFor(string name, GameData game)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var ts = LoadTileset(name);
        if (ts == null) return null;
        string season = null;
        try { season = _app.World.ReadCharacter()?.Season; } catch { }
        lock (_resolvers)
        {
            var key = (name + "\u0001" + game.GetHashCode(), season ?? "");
            if (!_resolvers.TryGetValue(key, out var r)) _resolvers[key] = r = new TileResolver(ts, game, season);
            return r;
        }
    }

    void TilesetsJson(HttpListenerResponse res)
    {
        var list = TilesetList();
        string game = null;
        try { game = Tileset.GameSetting(_app.Install.ConfigDirs); } catch { }
        Json(res, w =>
        {
            w.WriteStartObject();
            if (game != null && list.Any(t => t.Name == game)) w.WriteString("game", game);
            else if (game != null) w.WriteString("gameMissing", game);
            w.WriteStartArray("tilesets");
            foreach (var t in list) { w.WriteStartObject(); w.WriteString("name", t.Name); w.WriteString("view", t.View); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    void TilesetJson(HttpListenerResponse res, string name)
    {
        Tileset ts;
        try { ts = LoadTileset(name); }
        catch (Exception ex) { Error(res, $"Couldn't read the tileset \"{name}\": {ex.Message}"); return; }
        if (ts == null) { Error(res, $"No tileset called \"{name}\" in the game's gfx folder."); return; }
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteString("name", ts.Info.Name); w.WriteString("view", ts.Info.View);
            w.WriteNumber("tileW", ts.TileW); w.WriteNumber("tileH", ts.TileH);
            w.WriteStartArray("sheets");
            foreach (var sh in ts.Sheets)
            {
                w.WriteStartObject();
                w.WriteString("file", sh.File); w.WriteNumber("w", sh.SpriteW); w.WriteNumber("h", sh.SpriteH);
                w.WriteNumber("ox", sh.OffsetX); w.WriteNumber("oy", sh.OffsetY); w.WriteNumber("cols", sh.Columns);
                w.WriteNumber("first", sh.First); w.WriteNumber("count", sh.Count); w.WriteNumber("scale", sh.PixelScale);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>A sprite sheet of a tileset: only files its tile_config.json names, read from the game's gfx folder.</summary>
    void GfxFile(HttpListenerResponse res, string name, string file)
    {
        var ts = LoadTileset(name);
        var sheet = ts?.Sheets.FirstOrDefault(x => x.File == file);
        if (sheet == null) { Status(res, 404, "not found"); return; }
        var path = Path.GetFullPath(Path.Combine(ts.Info.Dir, sheet.File));
        if (!path.StartsWith(Path.GetFullPath(ts.Info.Dir) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { Status(res, 404, "not found"); return; }
        var bytes = File.ReadAllBytes(path);
        res.ContentType = "image/png";
        res.Headers["Cache-Control"] = "private, max-age=3600";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
    }

    void OvermapJson(HttpListenerResponse res, int ox, int oy, int z, bool full, string tileset)
    {
        var game = _app.Game;
        if (game == null) { Error(res, "Game data is still loading."); return; }
        TileResolver resolver = null;
        try { resolver = ResolverFor(tileset, game); } catch (Exception ex) { Console.Error.WriteLine("tileset: " + ex.Message); }
        var v = OvermapView.Build(_app.World, game, ox, oy, z, full, resolver);
        SeenData seen = v.Exists && !full ? _app.World.ReadSeen(ox, oy) : null;
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteNumber("ox", ox); w.WriteNumber("oy", oy); w.WriteNumber("z", z);
            w.WriteBoolean("exists", v.Exists);
            if (v.Exists)
            {
                w.WriteStartArray("styles");
                foreach (var s in v.Styles)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(s.Sym); w.WriteNumberValue(s.Fg); w.WriteNumberValue(s.Bg);
                    w.WriteStringValue(s.Name); w.WriteStringValue(s.Id);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteStartArray("grid");
                foreach (var g in v.Grid) w.WriteNumberValue(g);
                w.WriteEndArray();
                w.WriteStartArray("notes");
                foreach (var n in v.Notes)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(n.X); w.WriteNumberValue(n.Y); w.WriteStringValue(n.Sym); w.WriteNumberValue(n.Color);
                    w.WriteStringValue(n.Text); w.WriteBooleanValue(n.Dangerous); w.WriteNumberValue(n.Radius);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteStartArray("cities");
                foreach (var c in v.Cities)
                {
                    if (seen != null && (c.X < 0 || c.Y < 0 || c.X >= 180 || c.Y >= 180 || seen.Vision[OvermapTerrain.Depth][c.Y * 180 + c.X] == 0)) continue;
                    w.WriteStartArray();
                    w.WriteNumberValue(c.X); w.WriteNumberValue(c.Y); w.WriteStringValue(c.Name); w.WriteNumberValue(c.Size);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteNumber("unknown", v.Unknown);
                if (v.TileGrid != null)
                {
                    w.WriteStartArray("tiles");
                    foreach (var (fg, fr, bg, br) in v.Tiles) { w.WriteStartArray(); w.WriteNumberValue(fg); w.WriteNumberValue(fr); w.WriteNumberValue(bg); w.WriteNumberValue(br); w.WriteEndArray(); }
                    w.WriteEndArray();
                    w.WriteStartArray("tgrid");
                    foreach (var g in v.TileGrid) w.WriteNumberValue(g);
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
        });
    }

    void SearchJson(HttpListenerResponse res, string query, string zParam, string fromParam, bool idsOnly, bool byQty, bool parts, string cat = null, string onlySet = null)
    {
        if (_app.World.Compressed && !_app.Snapshot.Info().Exists) { Error(res, "There's no snapshot yet. Build it first (it unpacks the map so it can be searched)."); return; }
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrEmpty(cat)) { Error(res, "Type an id to search for, like forge or *rifle*."); return; }
        int[] zs = null;
        if (!string.IsNullOrWhiteSpace(zParam))
            zs = zParam.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s.Trim(), out var z) ? z : int.MinValue).Where(z => z != int.MinValue).ToArray();
        (int x, int y)? from = null;
        if (fromParam == "base") from = _app.BasePos;
        else if (fromParam == "me") { try { var c = _app.World.ReadCharacter(); if (c != null) from = (c.X, c.Y); } catch { } }
        else if (!string.IsNullOrEmpty(fromParam) && fromParam.Contains(','))
        {
            var p = fromParam.Split(',');
            if (int.TryParse(p[0], out var fx) && int.TryParse(p[1], out var fy)) from = (fx, fy);
        }
        if (!string.IsNullOrEmpty(cat)) { CategorySearchJson(res, cat, string.IsNullOrEmpty(onlySet) ? null : onlySet, zs, from, idsOnly, byQty); return; }
        if (parts) { PartsJson(res, query, zs, from, idsOnly, byQty); return; }
        var r = RunWorldSearch(query, zs, from);
        // one group per map tile (x, y, z): that's what you walk to
        var tiles = r.Hits.GroupBy(h => (h.Ox, h.Oy, h.Z)).Select(g => g.ToList()).ToList();
        IEnumerable<List<Hit>> ordered = byQty
            ? tiles.OrderByDescending(t => t.Sum(h => h.Qty)).ThenBy(t => t[0].Dist ?? 0)
            : tiles.OrderBy(t => t[0].Dist ?? 0).ThenBy(t => Math.Abs(t[0].Z)).ThenByDescending(t => t.Sum(h => h.Qty));
        const int MaxTiles = 3000, MaxHitsPerTile = 200;
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteString("query", query.Trim().ToLowerInvariant());
            w.WriteNumber("total", r.Hits.Count);
            w.WriteNumber("tileCount", tiles.Count);
            w.WriteNumber("qtyTotal", r.Hits.Sum(h => h.Qty));
            w.WriteNumber("files", r.FilesTotal); w.WriteNumber("parsed", r.FilesParsed);
            w.WriteNumber("mb", Math.Round(r.Bytes / 1048576.0, 1)); w.WriteNumber("seconds", Math.Round(r.Seconds, 2));
            w.WriteStartArray("errors"); foreach (var e in r.Errors.Take(10)) w.WriteStringValue(e); w.WriteEndArray();
            if (idsOnly)
            {
                var ids = ItemSearch.Summarize(r.Hits);
                if (byQty) ids = ids.OrderByDescending(i => i.TotalQty).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
                w.WriteStartArray("ids");
                foreach (var s in ids)
                {
                    w.WriteStartObject();
                    w.WriteString("id", s.Id); w.WriteString("kind", s.Kind); w.WriteNumber("places", s.Places); w.WriteNumber("qty", s.TotalQty);
                    if (s.Nearest.HasValue) w.WriteNumber("nearest", s.Nearest.Value);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            else
            {
                w.WriteStartArray("tiles");
                foreach (var t in ordered.Take(MaxTiles))
                {
                    var h0 = t[0];
                    w.WriteStartObject();
                    w.WriteNumber("x", h0.Ox); w.WriteNumber("y", h0.Oy); w.WriteNumber("z", h0.Z);
                    w.WriteString("inGame", h0.InGame);
                    if (h0.Dist.HasValue) { w.WriteNumber("dist", h0.Dist.Value); w.WriteString("dir", h0.Dir); }
                    w.WriteNumber("qty", t.Sum(h => h.Qty));
                    w.WriteNumber("count", t.Count);
                    // per id: total quantity and where it sits (on ground, in a bag, in a car...)
                    w.WriteStartArray("ids");
                    foreach (var g in t.GroupBy(h => (h.Id, h.Kind)).OrderByDescending(g => g.Sum(h => h.Qty)))
                    {
                        w.WriteStartObject();
                        w.WriteString("id", g.Key.Id); w.WriteString("kind", g.Key.Kind);
                        w.WriteNumber("qty", g.Sum(h => h.Qty)); w.WriteNumber("stacks", g.Count());
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("hits");
                    foreach (var h in t.OrderByDescending(h => h.Qty).Take(MaxHitsPerTile))
                    {
                        w.WriteStartObject();
                        w.WriteString("kind", h.Kind); w.WriteString("id", h.Id); w.WriteNumber("qty", h.Qty);
                        w.WriteString("square", h.Square); w.WriteString("detail", h.Detail);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        });
    }

    static string Num(double v) => Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0") : v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    static string Range(double min, double max) => Math.Abs(max - min) < 0.05 ? Num(max) : Num(min) + "–" + Num(max);

    void PartsJson(HttpListenerResponse res, string query, int[] zs, (int x, int y)? from, bool idsOnly, bool byQty)
    {
        var game = _app.Game;
        if (game == null) { Error(res, "Game data is still loading; try again in a moment."); return; }
        var p = PartsSearch.Run(_app.Snapshot.MapsDir, query, game.Disassembly, zs, from, game.CountByCharges);
        if (p.Error != null) { Error(res, p.Error); return; }
        var r = p.Raw;
        IEnumerable<PartsTile> ordered = byQty
            ? p.Tiles.OrderByDescending(t => t.Exp).ThenBy(t => t.Dist ?? 0)
            : p.Tiles.OrderBy(t => t.Dist ?? 0).ThenBy(t => Math.Abs(t.Z)).ThenByDescending(t => t.Exp);
        const int MaxTiles = 3000, MaxHitsPerTile = 200;
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteString("query", query.Trim().ToLowerInvariant());
            w.WriteBoolean("parts", true);
            w.WriteStartArray("targets"); foreach (var t in p.Targets.Take(50)) w.WriteStringValue(t); w.WriteEndArray();
            w.WriteNumber("targetCount", p.Targets.Count);
            w.WriteStartArray("sources");
            foreach (var s in p.Sources.Take(80))
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id); w.WriteString("kind", s.Kind); w.WriteString("yield", Range(s.Min, s.Max));
                if (s.Via != null) w.WriteString("via", s.Via);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("sourceCount", p.Sources.Count);
            w.WriteNumber("total", r.Hits.Count);
            w.WriteNumber("tileCount", p.Tiles.Count);
            w.WriteString("yield", Range(p.TotalMin, p.TotalMax));
            w.WriteNumber("files", r.FilesTotal); w.WriteNumber("parsed", r.FilesParsed);
            w.WriteNumber("mb", Math.Round(r.Bytes / 1048576.0, 1)); w.WriteNumber("seconds", Math.Round(r.Seconds, 2));
            w.WriteStartArray("errors"); foreach (var e in r.Errors.Take(10)) w.WriteStringValue(e); w.WriteEndArray();
            if (idsOnly)
            {
                var agg = p.Tiles.SelectMany(t => t.Lines.Select(l => (t, l)))
                    .GroupBy(x => (x.l.Id, x.l.Kind, x.l.Direct))
                    .Select(g => (g.Key, places: g.Count(), qty: g.Sum(x => x.l.Qty), min: g.Sum(x => x.l.Min), max: g.Sum(x => x.l.Max),
                                  exp: g.Sum(x => x.l.Exp), nearest: g.Min(x => x.t.Dist)))
                    .OrderByDescending(x => byQty ? x.exp : -(x.nearest ?? 0)).ThenBy(x => x.Key.Id, StringComparer.Ordinal).ToList();
                w.WriteStartArray("ids");
                foreach (var a in agg)
                {
                    w.WriteStartObject();
                    w.WriteString("id", a.Key.Id); w.WriteString("kind", a.Key.Direct ? a.Key.Kind : a.Key.Kind + " → parts");
                    w.WriteNumber("places", a.places); w.WriteNumber("qty", a.qty); w.WriteString("yield", Range(a.min, a.max));
                    if (a.nearest.HasValue) w.WriteNumber("nearest", a.nearest.Value);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            else
            {
                w.WriteStartArray("tiles");
                foreach (var t in ordered.Take(MaxTiles))
                {
                    w.WriteStartObject();
                    w.WriteNumber("x", t.X); w.WriteNumber("y", t.Y); w.WriteNumber("z", t.Z);
                    w.WriteString("inGame", t.InGame);
                    if (t.Dist.HasValue) { w.WriteNumber("dist", t.Dist.Value); w.WriteString("dir", t.Dir); }
                    w.WriteNumber("qty", Math.Round(t.Exp));
                    w.WriteString("yield", Range(t.Min, t.Max));
                    w.WriteNumber("count", t.Hits.Count);
                    w.WriteStartArray("ids");
                    foreach (var l in t.Lines)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", l.Id); w.WriteString("kind", l.Kind); w.WriteNumber("qty", l.Qty); w.WriteNumber("stacks", l.Stacks);
                        w.WriteBoolean("direct", l.Direct); w.WriteString("yield", Range(l.Min, l.Max));
                        if (l.Via != null) w.WriteString("via", l.Via);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("hits");
                    foreach (var (h, min, max, direct) in t.Hits.Take(MaxHitsPerTile))
                    {
                        w.WriteStartObject();
                        w.WriteString("kind", h.Kind); w.WriteString("id", h.Id); w.WriteNumber("qty", h.Qty);
                        w.WriteString("square", h.Square); w.WriteString("detail", h.Detail);
                        w.WriteString("yield", Range(min, max)); w.WriteBoolean("direct", direct);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        });
    }

    // ---------------------------------------------------------------- category search
    ItemSearch_Filter FilterOf(ItemCategories ic, string cat)
    {
        var f = _app.Filters.Get(cat, () => cat is "ammo" or "magazines" or "guns" ? ic.NonFirearmSets(cat) : Enumerable.Empty<string>());
        return new ItemSearch_Filter(f.Sets, f.Items, f.Saved);
    }
    sealed record ItemSearch_Filter(HashSet<string> Sets, HashSet<string> Items, bool Saved);

    void CategoriesJson(HttpListenerResponse res)
    {
        var game = _app.Game;
        if (game == null) { Error(res, "Game data is still loading."); return; }
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("categories");
            foreach (var c in game.Categories.Cats)
            {
                w.WriteStartObject(); w.WriteString("id", c.Id); w.WriteString("name", c.Name); w.WriteNumber("count", c.Count);
                var f = FilterOf(game.Categories, c.Id);
                w.WriteNumber("included", game.Categories.Resolve(c.Id, f.Sets, f.Items).Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    void CategoryJson(HttpListenerResponse res, string cat)
    {
        var game = _app.Game;
        if (game == null) { Error(res, "Game data is still loading."); return; }
        var ic = game.Categories; var c = ic.Get(cat);
        if (c == null) { Error(res, "Unknown category."); return; }
        var f = FilterOf(ic, cat);
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteString("id", c.Id); w.WriteString("name", c.Name); w.WriteNumber("count", c.Count);
            w.WriteNumber("included", ic.Resolve(cat, f.Sets, f.Items).Count);
            w.WriteBoolean("saved", f.Saved);
            w.WriteBoolean("split", c.Groups.Any(g => g.Id != ItemCategories.All));
            w.WriteStartArray("firearmSets"); foreach (var s in c.Groups.Where(g => g.Id == ItemCategories.Firearms).SelectMany(g => g.Sets)) w.WriteStringValue(s.Id); w.WriteEndArray();
            w.WriteStartArray("excludedSets"); foreach (var s in f.Sets) w.WriteStringValue(s); w.WriteEndArray();
            w.WriteStartArray("excludedItems"); foreach (var s in f.Items) w.WriteStringValue(s); w.WriteEndArray();
            w.WriteStartArray("groups");
            foreach (var g in c.Groups)
            {
                w.WriteStartObject(); w.WriteString("id", g.Id); w.WriteString("name", g.Name);
                w.WriteStartArray("sets");
                foreach (var s in g.Sets)
                {
                    w.WriteStartObject(); w.WriteString("id", s.Id); w.WriteString("name", s.Name);
                    w.WriteStartArray("items");
                    foreach (var i in s.Items) { w.WriteStartArray(); w.WriteStringValue(i); w.WriteStringValue(game.Codex.NameOf("item", i)); w.WriteEndArray(); }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    void CategorySearchJson(HttpListenerResponse res, string cat, string onlySet, int[] zs, (int x, int y)? from, bool summary, bool byQty)
    {
        var game = _app.Game;
        if (game == null) { Error(res, "Game data is still loading."); return; }
        var ic = game.Categories; var c = ic.Get(cat);
        if (c == null) { Error(res, "Unknown category."); return; }
        var f = FilterOf(ic, cat);
        var ids = ic.Resolve(cat, f.Sets, f.Items, onlySet);
        if (ids.Count == 0) { Error(res, onlySet != null ? "Nothing in that group." : "Everything in " + c.Name + " is excluded. Open the filter and tick something."); return; }
        var r = ItemSearch.RunItemIds(_app.Snapshot.MapsDir, ids, zs, from, game.CountByCharges);
        var setName = c.Groups.SelectMany(g => g.Sets).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        string SetOf(string id) => ic.OfItem.TryGetValue(id, out var v) ? v.set : "-";
        bool Loaded(Hit h) => h.Parent != null && h.ComponentOf == null && ic.Holders.Contains(h.Parent);
        var tiles = r.Hits.GroupBy(h => (h.Ox, h.Oy, h.Z)).Select(g => g.ToList()).ToList();
        IEnumerable<List<Hit>> ordered = byQty
            ? tiles.OrderByDescending(t => t.Sum(h => h.Qty)).ThenBy(t => t[0].Dist ?? 0)
            : tiles.OrderBy(t => t[0].Dist ?? 0).ThenBy(t => Math.Abs(t[0].Z)).ThenByDescending(t => t.Sum(h => h.Qty));
        const int MaxTiles = 3000, MaxHitsPerTile = 200;
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteString("cat", c.Id); w.WriteString("catName", c.Name);
            if (onlySet != null) { w.WriteString("set", onlySet); w.WriteString("setName", setName.GetValueOrDefault(onlySet, onlySet)); }
            w.WriteNumber("searched", ids.Count); w.WriteNumber("of", c.Count);
            w.WriteNumber("total", r.Hits.Count);
            w.WriteNumber("tileCount", tiles.Count);
            w.WriteNumber("qtyTotal", r.Hits.Sum(h => h.Qty));
            w.WriteNumber("loadedTotal", r.Hits.Where(Loaded).Sum(h => h.Qty));
            w.WriteNumber("files", r.FilesTotal); w.WriteNumber("parsed", r.FilesParsed);
            w.WriteNumber("seconds", Math.Round(r.Seconds, 2));
            w.WriteStartArray("errors"); foreach (var e in r.Errors.Take(10)) w.WriteStringValue(e); w.WriteEndArray();
            if (summary)
            {
                w.WriteStartArray("sets");
                var sets = r.Hits.GroupBy(h => SetOf(h.Id)).Select(g => new
                {
                    Set = g.Key, Qty = g.Sum(h => h.Qty), Loaded = g.Where(Loaded).Sum(h => h.Qty),
                    Places = g.Select(h => (h.Ox, h.Oy, h.Z)).Distinct().Count(), Nearest = g.Min(h => h.Dist),
                    Items = g.GroupBy(h => h.Id).Select(x => (id: x.Key, qty: x.Sum(h => h.Qty))).OrderByDescending(x => x.qty).ToList()
                });
                sets = byQty ? sets.OrderByDescending(s => s.Qty) : sets.OrderBy(s => setName.GetValueOrDefault(s.Set, s.Set), StringComparer.OrdinalIgnoreCase);
                foreach (var s in sets)
                {
                    w.WriteStartObject();
                    w.WriteString("id", s.Set); w.WriteString("name", setName.GetValueOrDefault(s.Set, s.Set));
                    w.WriteNumber("qty", s.Qty); w.WriteNumber("loaded", s.Loaded); w.WriteNumber("places", s.Places);
                    if (s.Nearest.HasValue) w.WriteNumber("nearest", s.Nearest.Value);
                    w.WriteStartArray("items");
                    foreach (var (id, qty) in s.Items) { w.WriteStartArray(); w.WriteStringValue(id); w.WriteStringValue(game.Codex.NameOf("item", id)); w.WriteNumberValue(qty); w.WriteEndArray(); }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            else
            {
                w.WriteStartArray("tiles");
                foreach (var t in ordered.Take(MaxTiles))
                {
                    var h0 = t[0];
                    w.WriteStartObject();
                    w.WriteNumber("x", h0.Ox); w.WriteNumber("y", h0.Oy); w.WriteNumber("z", h0.Z);
                    w.WriteString("inGame", h0.InGame);
                    if (h0.Dist.HasValue) { w.WriteNumber("dist", h0.Dist.Value); w.WriteString("dir", h0.Dir); }
                    w.WriteNumber("qty", t.Sum(h => h.Qty));
                    w.WriteNumber("loaded", t.Where(Loaded).Sum(h => h.Qty));
                    w.WriteNumber("count", t.Count);
                    w.WriteStartArray("sets");
                    foreach (var g in t.GroupBy(h => SetOf(h.Id)).OrderByDescending(g => g.Sum(h => h.Qty)))
                    { w.WriteStartObject(); w.WriteString("name", setName.GetValueOrDefault(g.Key, g.Key)); w.WriteNumber("qty", g.Sum(h => h.Qty)); w.WriteEndObject(); }
                    w.WriteEndArray();
                    w.WriteStartArray("ids");
                    foreach (var g in t.GroupBy(h => h.Id).OrderByDescending(g => g.Sum(h => h.Qty)))
                    {
                        w.WriteStartObject();
                        w.WriteString("id", g.Key); w.WriteString("kind", "item"); w.WriteString("name", game.Codex.NameOf("item", g.Key));
                        w.WriteNumber("qty", g.Sum(h => h.Qty)); w.WriteNumber("stacks", g.Count()); w.WriteNumber("loaded", g.Where(Loaded).Sum(h => h.Qty));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("hits");
                    foreach (var h in t.OrderByDescending(h => h.Qty).Take(MaxHitsPerTile))
                    {
                        w.WriteStartObject();
                        w.WriteString("kind", "item"); w.WriteString("id", h.Id); w.WriteString("name", game.Codex.NameOf("item", h.Id)); w.WriteNumber("qty", h.Qty);
                        w.WriteString("square", h.Square);
                        string where = h.ComponentOf != null || h.Where == null ? h.Detail
                            : h.Where + (h.Trail is { Length: > 0 } tr ? " · " + string.Join(" › ", tr.Select(x => game.Codex.NameOf("item", x))) : "");
                        w.WriteString("detail", where);
                        w.WriteBoolean("loaded", Loaded(h));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        });
    }

    // ---------------------------------------------------------------- setup and settings
    /// <summary>
    /// What the setup form shows for a folder someone pasted (or, with none, for the current settings): the game, the
    /// player's folders, the worlds. "save" is what the form sends back to store; "paths" is where things really are.
    /// </summary>
    void SetupScanJson(HttpListenerResponse res, string folder, string saves)
    {
        var cfg = _app.Config;
        var detected = Setup.DetectGameDirs();
        Install.Found f;
        string shown;
        if (string.IsNullOrWhiteSpace(folder) && Setup.IsGameDir(cfg.GameDir))
        {
            f = new Install.Found { GameDir = cfg.GameDir, UserDir = cfg.UserDir, SaveRoot = cfg.SaveRoot };
            shown = cfg.GameDir;
        }
        else
        {
            shown = string.IsNullOrWhiteSpace(folder) ? detected.FirstOrDefault() ?? "" : folder.Trim().Trim('"');
            f = shown.Length == 0 ? new Install.Found { Problem = "Couldn't find the game by myself. Paste the folder you start the game from, your launcher's folder, or your save folder." }
                : Install.Locate(shown, cfg.GameDir);
        }
        if (!string.IsNullOrWhiteSpace(saves))
        {
            saves = saves.Trim().Trim('"');
            if (!Directory.Exists(saves)) f.Problem ??= $"The save folder {saves} doesn't exist.";
            else if (Setup.IsWorld(saves)) { f.World ??= Path.GetFileName(saves.TrimEnd('/', '\\')); f.SaveRoot = Path.GetDirectoryName(saves.TrimEnd('/', '\\')); }
            else f.SaveRoot = saves;
        }
        var tmp = cfg.Clone();
        tmp.GameDir = f.GameDir; tmp.UserDir = f.UserDir; tmp.SaveRoot = f.SaveRoot;
        if (!Install.Same(f.GameDir, cfg.GameDir)) tmp.ConfigDir = "";
        var inst = Install.From(tmp);
        bool valid = f.Problem == null && inst.Valid && tmp.Problems().All(p => p.StartsWith("No world") || p.StartsWith("World folder"));
        var worlds = valid ? Setup.Worlds(inst.SaveRoot) : new List<WorldInfo>();
        string problem = f.Problem
            ?? (!valid ? tmp.Problems().FirstOrDefault() : null)
            ?? (worlds.Count == 0 ? $"No worlds in {inst.SaveRoot}. Start the game once and make a world, then check again. If your saves are somewhere else, change the save folder." : null);
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteString("gameDir", shown);
            w.WriteBoolean("valid", valid);
            if (problem != null) w.WriteString("problem", problem);
            if (f.World != null) w.WriteString("world", f.World);
            w.WriteString("saveRoot", inst.SaveRoot ?? "");
            w.WriteStartObject("save");
            w.WriteString("gameDir", f.GameDir ?? ""); w.WriteString("userDir", f.UserDir ?? ""); w.WriteString("saveRoot", f.SaveRoot ?? "");
            w.WriteEndObject();
            w.WriteStartObject("paths");
            if (inst.Valid)
            {
                w.WriteString("kind", inst.Kind); if (inst.Label != null) w.WriteString("label", inst.Label);
                w.WriteString("game", inst.GameDir); w.WriteString("config", inst.ConfigDir);
            }
            w.WriteString("saves", !string.IsNullOrWhiteSpace(f.SaveRoot) || inst.Valid ? inst.SaveRoot : "");
            w.WriteBoolean("savesChosen", !string.IsNullOrWhiteSpace(f.SaveRoot));
            w.WriteEndObject();
            w.WriteString("configPath", cfg.FilePath);
            w.WriteString("exeDir", AppContext.BaseDirectory);
            w.WriteStartArray("detected"); foreach (var d in detected) w.WriteStringValue(d); w.WriteEndArray();
            w.WriteStartArray("worlds");
            foreach (var wi in worlds)
            {
                w.WriteStartObject();
                w.WriteString("name", wi.Name); w.WriteString("character", wi.Character);
                w.WriteString("lastPlayed", wi.LastPlayedUtc); w.WriteBoolean("compressed", wi.Compressed);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("bases"); foreach (var (k, v) in cfg.Bases) w.WriteString(k, v); w.WriteEndObject();
            w.WriteStartObject("current");
            w.WriteString("gameDir", cfg.GameDir); w.WriteString("world", cfg.World); w.WriteString("base", cfg.Base);
            w.WriteString("dataDir", cfg.DataDir); w.WriteString("dataRoot", Setup.IsGameDir(cfg.GameDir) || !string.IsNullOrWhiteSpace(cfg.DataDir) ? cfg.DataRoot : "");
            w.WriteNumber("slots", cfg.Slots); w.WriteNumber("idleExitMinutes", cfg.IdleExitMinutes);
            w.WriteBoolean("autoRefreshSnapshot", cfg.AutoRefreshSnapshot); w.WriteNumber("port", cfg.Port);
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    void SaveSettings(HttpListenerResponse res, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) { Error(res, "Nothing to save."); return; }
        string S(string k) => body.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString().Trim() : null;
        int? I(string k) => body.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : null;
        bool? B(string k) => body.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
        var cfg = _app.Config.Clone();
        // the folders as the setup scan worked them out (it accepts any folder; see Install.Locate)
        if (S("gameDir") is { } gd)
        {
            gd = gd.Trim('"');
            if (!Install.Same(gd, cfg.GameDir)) cfg.ConfigDir = "";
            cfg.GameDir = gd;
            cfg.UserDir = S("userDir")?.Trim('"') ?? "";
            cfg.SaveRoot = S("saveRoot")?.Trim('"') ?? "";
            if (Install.Blank(cfg.UserDir) && Install.Blank(cfg.SaveRoot) && Setup.IsGameDir(gd))
            {
                var user = Install.DefaultUserDir(gd);
                if (!Install.Same(user, gd)) cfg.UserDir = user;
            }
        }
        if (S("world") is { } world) cfg.World = world;
        if (S("base") is { } b)
        {
            if (b.Length > 0 && ItemSearch.ParseInGame(b) == null) { Error(res, "Write the base like the game's map shows it, e.g. 0'12, 0'34 (or leave it empty)."); return; }
            cfg.Base = b;
        }
        if (S("dataDir") is { } dd) cfg.DataDir = dd.Trim('"');
        if (I("slots") is { } sl) cfg.Slots = Math.Clamp(sl, 1, 20);
        if (I("idleExitMinutes") is { } im) cfg.IdleExitMinutes = Math.Clamp(im, 0, 24 * 60);
        if (B("autoRefreshSnapshot") is { } ar) cfg.AutoRefreshSnapshot = ar;
        var problems = cfg.Problems();
        if (problems.Count > 0) { Error(res, string.Join(" ", problems)); return; }
        Setup.FillDataDir(cfg);
        if (string.IsNullOrWhiteSpace(cfg.DataDir) && !Config.CanWrite(cfg.DataRoot)) cfg.DataDir = Config.UserDataDir();
        if (!Config.CanWrite(cfg.DataRoot)) { Error(res, $"Can't write to the data folder {cfg.DataRoot}. Pick another one."); return; }
        try { cfg.Save(); }
        catch (Exception ex) { Error(res, $"Couldn't save {cfg.FilePath}: {ex.Message}"); return; }
        try { _app.Apply(cfg); }
        catch (Exception ex) { Error(res, ex.Message); return; }
        lock (_resolvers) _resolvers.Clear();
        _tilesetList = null;
        Console.WriteLine($"Settings saved: world \"{cfg.World}\" in {cfg.GameDir}.");
        Json(res, w => { w.WriteStartObject(); w.WriteBoolean("ok", true); w.WriteEndObject(); });
    }

    void CodexSearchJson(HttpListenerResponse res, string query, string kind)
    {
        var game = _app.Game;
        if (game == null) { Error(res, "Game data is still loading."); return; }
        var c = game.Codex;
        if (!string.IsNullOrEmpty(kind) && Array.IndexOf(Codex.Kinds, kind) < 0) kind = null;
        var list = c.Search(query, kind, 200);
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteStartObject("counts"); foreach (var (k, n) in c.Counts) w.WriteNumber(k, n); w.WriteEndObject();
            w.WriteStartArray("results");
            foreach (var e in list)
            {
                var (sym, color) = c.Look(e);
                var (fg, bg) = Colors.Parse(color);
                w.WriteStartObject();
                w.WriteString("kind", e.Kind); w.WriteString("id", e.Id); w.WriteString("name", e.Name);
                w.WriteString("sym", sym); w.WriteNumber("fg", fg); w.WriteNumber("bg", bg);
                if (e.Def.Mod > 0) w.WriteString("mod", c.ModName(e));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    void CodexEntryJson(HttpListenerResponse res, string kind, string id)
    {
        var game = _app.Game;
        if (game == null) { Error(res, "Game data is still loading."); return; }
        var c = game.Codex;
        var e = c.Get(kind, id);
        if (e == null) { Error(res, $"No {kind.Replace('_', ' ')} with id {id}."); return; }
        var d = c.Describe(e);
        if (e.Kind == "item" && game.Categories.OfItem.TryGetValue(e.Id, out var ci) && game.Categories.Get(ci.cat) is { } icat)
        {
            d.Facts.RemoveAll(f => f.label == "Category");
            d.Facts.Insert(0, ("Category", icat.Name));
            if (ci.set != ci.cat && ci.set != "-") d.Facts.Insert(1, ("Ammo type", game.Categories.AmmoTypeNames.GetValueOrDefault(ci.set, ci.set)));
        }
        var (sym, color) = c.Look(e);
        var (fg, bg) = Colors.Parse(color);
        var dis = game.Disassembly;
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteString("kind", e.Kind); w.WriteString("id", e.Id); w.WriteString("name", e.Name);
            w.WriteString("sym", sym); w.WriteNumber("fg", fg); w.WriteNumber("bg", bg);
            w.WriteString("mod", c.ModName(e));
            w.WriteString("description", d.Description);
            w.WriteStartArray("facts"); foreach (var (l, v) in d.Facts) { w.WriteStartArray(); w.WriteStringValue(l); w.WriteStringValue(v); w.WriteEndArray(); } w.WriteEndArray();
            w.WriteStartArray("flags"); foreach (var f in d.Flags) w.WriteStringValue(f); w.WriteEndArray();
            w.WriteStartArray("parts");
            foreach (var (pk, pid, pname, cnt) in d.Parts)
            {
                w.WriteStartObject(); w.WriteString("kind", pk); w.WriteString("id", pid); w.WriteString("name", pname); w.WriteString("count", cnt);
                w.WriteBoolean("known", c.Get(pk, pid) != null); w.WriteEndObject();
            }
            w.WriteEndArray();
            if (d.Item is { } it)
            {
                w.WriteStartObject("item"); w.WriteString("kind", it.kind); w.WriteString("id", it.id); w.WriteString("name", it.name);
                w.WriteBoolean("known", c.Get(it.kind, it.id) != null); w.WriteEndObject();
            }
            // what it gives when taken apart
            string srcKind = e.Kind is "item" or "furniture" or "terrain" ? e.Kind : null;
            w.WriteStartArray("givesParts");
            if (srcKind != null && (dis.DirectBySource.TryGetValue((srcKind, e.Id), out var gives) || dis.BySource.TryGetValue((srcKind, e.Id), out gives)))
                foreach (var (tid, y) in gives.OrderByDescending(kv => kv.Value.Exp))
                {
                    w.WriteStartObject(); w.WriteString("id", tid); w.WriteString("name", c.NameOf("item", tid)); w.WriteString("yield", Range(y.Min, y.Max));
                    w.WriteBoolean("known", c.Get("item", tid) != null);
                    w.WriteBoolean("more", dis.BySource.ContainsKey(("item", tid))); w.WriteEndObject();
                }
            w.WriteEndArray();
            // what gives it
            var sources = e.Kind == "item" ? dis.SourcesOf(e.Id) : new List<DisSource>();
            w.WriteNumber("sourceCount", sources.Count);
            w.WriteStartArray("comesFrom");
            foreach (var s in sources.Take(40))
            {
                w.WriteStartObject(); w.WriteString("kind", s.Kind); w.WriteString("id", s.Id); w.WriteString("name", c.NameOf(s.Kind, s.Id));
                w.WriteString("yield", Range(s.Min, s.Max)); if (s.Via != null) w.WriteString("via", s.Via);
                w.WriteBoolean("known", c.Get(s.Kind, s.Id) != null); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>Normal search, plus: a vehicle prototype id also finds vehicles by that prototype's name.</summary>
    SearchResult RunWorldSearch(string query, int[] zs, (int x, int y)? from)
    {
        var q = query.Trim().ToLowerInvariant();
        var vname = !ItemSearch.IsWildcard(q) ? _app.Game?.Codex.VehicleName(q) : null;
        if (vname == null) return ItemSearch.Run(_app.Snapshot.MapsDir, query, zs, from, _app.Game?.CountByCharges);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { q, vname };
        var needles = new List<byte[]> { System.Text.Encoding.UTF8.GetBytes("\"" + q + "\""), System.Text.Encoding.UTF8.GetBytes("\"" + vname + "\"") };
        return ItemSearch.RunMatch(_app.Snapshot.MapsDir, keys.Contains, needles, zs, from, _app.Game?.CountByCharges);
    }

    void SlotsJson(HttpListenerResponse res)
    {
        var slots = _app.Backups.List();
        Json(res, w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("slots");
            foreach (var s in slots)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id); w.WriteBoolean("exists", s.Exists);
                if (s.Exists)
                {
                    w.WriteString("label", s.Label); w.WriteString("gameDate", s.GameDate); w.WriteString("character", s.Character);
                    w.WriteString("created", s.CreatedUtc); w.WriteNumber("bytes", s.Bytes); w.WriteNumber("files", s.Files);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    // ---------------------------------------------------------------- plumbing
    static int Int(string s) => int.TryParse(s, out var v) ? v : 0;

    static JsonElement ReadBody(HttpListenerRequest req)
    {
        if (!req.HasEntityBody) return default;
        using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
        var text = sr.ReadToEnd();
        if (text.Length > 100_000) return default;
        try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); } catch { return default; }
    }

    static void Json(HttpListenerResponse res, Action<Utf8JsonWriter> write, int status = 200)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) write(w);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.Headers["Cache-Control"] = "no-store";
        res.ContentLength64 = ms.Length;
        ms.Position = 0;
        ms.CopyTo(res.OutputStream);
    }

    static void Error(HttpListenerResponse res, string msg) =>
        Json(res, w => { w.WriteStartObject(); w.WriteString("error", msg); w.WriteEndObject(); });

    static void Status(HttpListenerResponse res, int code, string text)
    {
        res.StatusCode = code;
        var b = Encoding.UTF8.GetBytes(text);
        res.ContentType = "text/plain; charset=utf-8";
        res.ContentLength64 = b.Length;
        res.OutputStream.Write(b);
    }

    void ServeStatic(HttpListenerResponse res, string path)
    {
        if (path == "/") path = "/index.html";
        var name = "web" + path;
        using var s = typeof(Server).Assembly.GetManifestResourceStream(name);
        if (s == null) { Status(res, 404, "not found"); return; }
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        if (path == "/index.html")
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("__TOKEN__", _app.Token));
        res.ContentType = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
        res.Headers["Cache-Control"] = "no-store";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
    }
}
