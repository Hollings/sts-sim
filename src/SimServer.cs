using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StS2Sim;

/// <summary>
/// Embedded HTTP + WebSocket server for the standalone UI.
/// Reads the vanilla save file, exposes the current deck, and runs sim batches
/// while live-streaming per-seed progress to connected browsers.
/// </summary>
internal sealed class SimServer
{
    public int Port { get; }
    public string WebRoot { get; }

    private readonly HttpListener _listener = new();
    private readonly List<WebSocket> _sockets = new();
    private readonly object _socketsLock = new();
    private CancellationTokenSource? _currentJobCts;

    public SimServer(int port, string webRoot)
    {
        Port = port;
        WebRoot = webRoot;
        // Bind both IPv4 loopback and IPv6 loopback explicitly. Firefox often
        // resolves "localhost" to ::1 first; if we only bound IPv4, the browser
        // gets a connection refusal even though curl (which prefers IPv4) works.
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add($"http://[::1]:{port}/");
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"\n  ERROR: could not bind port {Port}: {ex.Message}");
            Console.Error.WriteLine($"         Likely something else is using the port, or this user lacks URL ACL permission.");
            Console.Error.WriteLine($"         Try: STS2SIM_PORT=52325 dotnet run    (or close whatever is on 52324)");
            throw;
        }
        Console.WriteLine($"\n  StS2Sim server listening at:");
        foreach (var prefix in _listener.Prefixes)
            Console.WriteLine($"    {prefix}");
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            switch (path)
            {
                case "/api/deck":
                    await ServeDeck(ctx);
                    break;
                case "/api/sim/start":
                    await ServeSimStart(ctx);
                    break;
                case "/api/sim/stop":
                    _currentJobCts?.Cancel();
                    Send(ctx, 200, "application/json", "{\"ok\":true}");
                    break;
                case "/ws":
                    await HandleWebSocket(ctx);
                    break;
                default:
                    await ServeStatic(ctx, path);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SimServer] {ctx.Request.Url}: {ex}");
            try { Send(ctx, 500, "text/plain", ex.Message); } catch { /* response may be gone */ }
        }
    }

    // ─── Static files ───────────────────────────────────────────────────────

    private async Task ServeStatic(HttpListenerContext ctx, string path)
    {
        if (path == "/") path = "/index.html";
        var file = Path.Combine(WebRoot, path.TrimStart('/'));
        if (!File.Exists(file))
        {
            Send(ctx, 404, "text/plain", "Not found: " + path);
            return;
        }
        var bytes = await File.ReadAllBytesAsync(file);
        var mime = path.EndsWith(".html") ? "text/html"
                 : path.EndsWith(".js") ? "application/javascript"
                 : path.EndsWith(".css") ? "text/css"
                 : "application/octet-stream";
        ctx.Response.ContentType = mime;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    // ─── /api/deck ─────────────────────────────────────────────────────────

    private async Task ServeDeck(HttpListenerContext ctx)
    {
        var deck = SaveFileReader.ReadFreshest();
        if (deck == null)
        {
            Send(ctx, 404, "application/json", "{\"error\":\"no save file found\"}");
            return;
        }
        // Group cards by (id, upgradeLevel) so upgraded copies show separately.
        var grouped = deck.Cards
            .GroupBy(c => (c.Id, c.UpgradeLevel))
            .Select(g => new
            {
                id = g.Key.Id,
                upgrade = g.Key.UpgradeLevel,
                name = CardIdResolver.PrettyName(g.Key.Id) + (g.Key.UpgradeLevel > 0 ? "+" + (g.Key.UpgradeLevel == 1 ? "" : g.Key.UpgradeLevel.ToString()) : ""),
                count = g.Count(),
            })
            .OrderByDescending(x => x.count).ThenBy(x => x.id).ThenBy(x => x.upgrade)
            .ToList();
        var payload = new
        {
            sourcePath = deck.SourcePath,
            modified = deck.Modified.ToString("yyyy-MM-dd HH:mm:ss"),
            character = deck.CharacterId,
            characterPretty = CardIdResolver.PrettyName(deck.CharacterId),
            currentHp = deck.CurrentHp,
            maxHp = deck.MaxHp,
            gold = deck.Gold,
            deckSize = deck.Cards.Count,
            cardsGrouped = grouped,
            cardsRaw = deck.Cards.Select(c => c.Id).ToList(),
            relics = deck.Relics.Select(r => new { id = r, name = CardIdResolver.PrettyName(r) }).ToList(),
        };
        await SendJson(ctx, 200, payload);
    }

    // ─── /api/sim/start ────────────────────────────────────────────────────

    private async Task ServeSimStart(HttpListenerContext ctx)
    {
        var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
        var req = JsonSerializer.Deserialize<StartReq>(body, JsonOpts) ?? new StartReq();

        var deck = SaveFileReader.ReadFreshest();
        if (deck == null)
        {
            Send(ctx, 404, "application/json", "{\"error\":\"no save file found\"}");
            return;
        }

        // Resolve card IDs to C# Types and pair with upgrade level from save.
        List<Harness.DeckEntry> deckEntries;
        try
        {
            deckEntries = deck.Cards
                .Select(c =>
                {
                    var t = CardIdResolver.Resolve(c.Id) ?? throw new ArgumentException($"Unknown card id '{c.Id}'");
                    return new Harness.DeckEntry(t, c.UpgradeLevel);
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Send(ctx, 400, "application/json", JsonSerializer.Serialize(new { error = ex.Message }));
            return;
        }

        // Sim runs on a background task; we ack immediately and stream progress on /ws.
        _currentJobCts?.Cancel();
        _currentJobCts = new CancellationTokenSource();
        var ct = _currentJobCts.Token;

        Send(ctx, 200, "application/json", "{\"ok\":true,\"started\":true}");

        _ = Task.Run(async () =>
        {
            await BroadcastEvent(new { type = "started", deckSize = deck.Cards.Count, character = deck.CharacterId, seeds = req.Seeds, k = req.K, turns = req.Turns, epsilon = req.Epsilon });
            try
            {
                var policy = new EpsilonGreedyPolicy(new HighestDamagePolicy(), req.Epsilon);
                var runner = new BestOfKRunner
                {
                    DeckName = deck.CharacterId,
                    Deck = deckEntries,
                    Policy = policy,
                    Seeds = req.Seeds,
                    InnerSamples = req.K,
                    Turns = req.Turns,
                    Quiet = true,
                    Cancellation = ct,
                    OnSeedDone = p => _ = BroadcastEvent(new
                    {
                        type = "seed",
                        index = p.SeedIndex,
                        total = p.TotalSeeds,
                        bestForSeed = p.BestForSeed,
                        runningAvg = p.RunningAvg,
                        runningStdErr = p.RunningStdErr,
                        ci95 = 1.96 * p.RunningStdErr,
                        totalRuns = p.TotalRuns,
                        elapsedMs = (long)p.Elapsed.TotalMilliseconds,
                    }),
                    OnNewBest = trial => _ = BroadcastEvent(new
                    {
                        type = "newBest",
                        seed = trial.Seed,
                        totalDamage = trial.TotalDamage,
                        avgPerTurn = trial.AvgPerTurn,
                        turns = trial.Turns.Select(t => new
                        {
                            turn = t.Turn,
                            damage = t.Damage,
                            hand = t.Hand,
                            played = t.CardsPlayed,
                        }),
                    }),
                };
                var summary = await runner.Run();
                await BroadcastEvent(new
                {
                    type = "done",
                    avgOfBest = summary.AvgOfBest,
                    avgPerTurn = summary.AvgOfBest / summary.Seeds == 0 ? 0 : summary.AvgOfBest / req.Turns,
                    ci95 = summary.Ci95HalfWidth,
                    bestOfBest = summary.BestOfBest,
                    worstSeedBest = summary.WorstSeedBest,
                    totalRuns = summary.TotalRuns,
                    elapsedSec = summary.Elapsed.TotalSeconds,
                    medianConvergenceK = summary.MedianConvergenceK,
                    maxConvergenceK = summary.MaxConvergenceK,
                });
            }
            catch (OperationCanceledException)
            {
                await BroadcastEvent(new { type = "cancelled" });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[SimServer] sim run threw:\n" + ex);
                await BroadcastEvent(new { type = "error", message = ex.Message, stack = ex.ToString() });
            }
        }, ct);
    }

    // ─── WebSocket ─────────────────────────────────────────────────────────

    private async Task HandleWebSocket(HttpListenerContext ctx)
    {
        if (!ctx.Request.IsWebSocketRequest)
        {
            Send(ctx, 400, "text/plain", "expected WebSocket");
            return;
        }
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var ws = wsCtx.WebSocket;
        lock (_socketsLock) _sockets.Add(ws);
        try
        {
            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, default);
                if (result.MessageType == WebSocketMessageType.Close) break;
                // We don't currently expect client messages.
            }
        }
        catch { /* connection drop */ }
        finally
        {
            lock (_socketsLock) _sockets.Remove(ws);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default); } catch { }
            ws.Dispose();
        }
    }

    private async Task BroadcastEvent(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        List<WebSocket> snapshot;
        lock (_socketsLock) snapshot = _sockets.ToList();
        foreach (var ws in snapshot)
        {
            if (ws.State != WebSocketState.Open) continue;
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, default); }
            catch { /* drop dead conns */ }
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private static void Send(HttpListenerContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static async Task SendJson(HttpListenerContext ctx, int status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class StartReq
    {
        public int Seeds { get; set; } = 200;
        public int K { get; set; } = 30;
        public int Turns { get; set; } = 5;
        public double Epsilon { get; set; } = 0.30;
    }
}
