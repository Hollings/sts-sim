using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace StS2Sim;

/// <summary>
/// Embedded HTTP + WebSocket server for the standalone UI.
///
/// Concerns kept here: HTTP routing, static-file serving, WebSocket lifecycle,
/// and broadcast fan-out. Sim orchestration lives in <see cref="SimJob"/>;
/// deck I/O lives in <see cref="SaveFileReader"/> + <see cref="CardIdResolver"/>.
/// </summary>
internal sealed class SimServer
{
    public int Port { get; }
    public string WebRoot { get; }

    private readonly HttpListener _listener = new();
    private readonly List<SocketState> _sockets = new();
    private readonly object _socketsLock = new();

    // One sim job at a time: starting a new one cancels and awaits the old one
    // so two jobs never interleave their WebSocket events.
    private readonly SemaphoreSlim _jobGate = new(1, 1);
    private CancellationTokenSource? _currentJobCts;
    private Task? _currentJobTask;

    private sealed class SocketState
    {
        public required WebSocket Ws { get; init; }
        // Bounded queue: drops oldest events if the consumer (browser) can't
        // keep up. Keeps the server fast even when the UI is slow.
        public readonly Channel<byte[]> Outbox =
            Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
    }

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
                case "/api/cards":
                    await ServeCards(ctx);
                    break;
                case "/api/encounters":
                    await SendJson(ctx, 200, new { encounters = EncounterCatalog.GetEncounters()
                        .Select(e => new { id = e.Id, name = e.Name, roomType = e.RoomType, act = e.Act }) });
                    break;
                case "/api/sim/start":
                    await ServeSimStart(ctx);
                    break;
                case "/api/advise/combat":
                    await ServeAdvise(ctx);
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
        // Resolve and confine to the web root — "/../" must not escape it.
        var rootFull = Path.GetFullPath(WebRoot);
        var file = Path.GetFullPath(Path.Combine(rootFull, path.TrimStart('/')));
        if (!file.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(file))
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
                name = CardLabels.Format(g.Key.Id, g.Key.UpgradeLevel),
                count = g.Count(),
            })
            .OrderByDescending(x => x.count).ThenBy(x => x.id).ThenBy(x => x.upgrade)
            .ToList();
        var payload = new
        {
            sourcePath = deck.SourcePath,
            modified = deck.Modified.ToString("yyyy-MM-dd HH:mm:ss"),
            character = deck.CharacterId,
            characterPretty = CardLabels.PrettyName(deck.CharacterId),
            currentHp = deck.CurrentHp,
            maxHp = deck.MaxHp,
            gold = deck.Gold,
            deckSize = deck.Cards.Count,
            cardsGrouped = grouped,
            cardsRaw = deck.Cards.Select(c => c.Id).ToList(),
            relics = deck.Relics.Select(r => new { id = r, name = CardLabels.PrettyName(r) }).ToList(),
        };
        await SendJson(ctx, 200, payload);
    }

    // ─── /api/cards — addable-card catalog for the deck editor ──────────────

    private async Task ServeCards(HttpListenerContext ctx)
    {
        var deck = SaveFileReader.ReadFreshest();
        if (deck == null)
        {
            Send(ctx, 404, "application/json", "{\"error\":\"no save file found\"}");
            return;
        }
        var cards = CardCatalog.GetAddableCards(deck.CharacterId)
            .Select(c => new { id = c.Id, name = c.Name, cost = c.Cost, cardType = c.Type, rarity = c.Rarity });
        await SendJson(ctx, 200, new { character = deck.CharacterId, cards });
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

        List<Harness.DeckEntry> baseEntries;
        List<Harness.DeckEntry>? variantEntries = null;
        List<SimJob.Candidate> candidates = new();
        string? changeSummary = null;
        try
        {
            var removals = req.Removals ?? new List<CardChange>();
            var additions = req.Additions ?? new List<CardChange>();
            var candidateReqs = req.Candidates ?? new List<CardChange>();
            if (candidateReqs.Count > 8)
                throw new ArgumentException("At most 8 candidates per compare run");

            // Staged edits are a COMMON change applied to every side. With
            // candidates present they shift the baseline itself ("after I
            // remove this Strike, which reward is best?"); without candidates
            // they define the A/B variant against the untouched save deck.
            var baseCards = deck.Cards.ToList();
            if (removals.Count > 0 || additions.Count > 0)
            {
                changeSummary = DescribeChanges(removals, additions);
                if (candidateReqs.Count > 0)
                    baseCards = ApplyChanges(deck.Cards, removals, additions);
                else
                    variantEntries = ResolveEntries(ApplyChanges(deck.Cards, removals, additions));
            }
            baseEntries = ResolveEntries(baseCards);

            foreach (var cand in candidateReqs)
            {
                if (CardIdResolver.Resolve(cand.Id) == null)
                    throw new ArgumentException($"Unknown candidate card id '{cand.Id}'");
                var candCards = baseCards.ToList();
                candCards.Add(new SaveFileReader.DeckCard(cand.Id, FloorAdded: 0, UpgradeLevel: cand.Upgrade));
                candidates.Add(new SimJob.Candidate(
                    CardLabels.Format(cand.Id, cand.Upgrade), ResolveEntries(candCards)));
            }
        }
        catch (Exception ex)
        {
            Send(ctx, 400, "application/json", JsonSerializer.Serialize(new { error = ex.Message }));
            return;
        }

        // Sim runs on a background task; we ack immediately and stream progress on /ws.
        // The gate makes "stop old, start new" atomic so events never interleave.
        await _jobGate.WaitAsync();
        try
        {
            _currentJobCts?.Cancel();
            if (_currentJobTask != null)
            {
                try { await _currentJobTask; } catch { /* old job's exceptions already broadcast */ }
            }
            _currentJobCts?.Dispose();
            _currentJobCts = new CancellationTokenSource();
            var ct = _currentJobCts.Token;

            var job = new SimJob
            {
                Deck = baseEntries,
                VariantDeck = variantEntries,
                Candidates = candidates,
                ChangeSummary = changeSummary,
                EncounterId = string.IsNullOrEmpty(req.EncounterId) ? null : req.EncounterId,
                Relics = deck.Relics.Where(r => !string.IsNullOrEmpty(r)).ToList(),
                CharacterId = deck.CharacterId,
                BroadcastEvent = BroadcastEvent,
                Seeds = req.Seeds,
                K = req.K,
                Turns = req.Turns,
                Epsilon = req.Epsilon,
                Patience = req.Patience,
            };
            _currentJobTask = Task.Run(() => job.Run(ct), CancellationToken.None);
        }
        finally
        {
            _jobGate.Release();
        }

        Send(ctx, 200, "application/json", "{\"ok\":true,\"started\":true}");
    }

    /// <summary>
    /// POST /api/advise/combat — rank every legal action in a live combat
    /// state (see AI_PLAYER.md for the contract). Body: an AdviseRequest-
    /// shaped JSON (field-compatible with snecko-eye's GET /state). Query:
    /// ?seeds=12&amp;horizon=8. Runs synchronously (sub-second at defaults);
    /// the harness is a process singleton, so this waits briefly for any
    /// running sim job and refuses with 503 rather than interleaving.
    /// </summary>
    private async Task ServeAdvise(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            Send(ctx, 405, "application/json", "{\"error\":\"POST only\"}");
            return;
        }
        var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
        Advise.AdviseRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<Advise.AdviseRequest>(body);
        }
        catch (Exception ex)
        {
            await SendJson(ctx, 400, new { error = "bad request json: " + ex.Message });
            return;
        }
        if (req?.Combat == null)
        {
            await SendJson(ctx, 400, new { error = "missing 'combat' block" });
            return;
        }

        int seeds = ParseQueryInt(ctx, "seeds", 12, 1, 200);
        int horizon = ParseQueryInt(ctx, "horizon", 8, 1, 50);

        await _jobGate.WaitAsync();
        try
        {
            if (_currentJobTask is { IsCompleted: false })
            {
                var done = await Task.WhenAny(_currentJobTask, Task.Delay(TimeSpan.FromSeconds(10)));
                if (done != _currentJobTask)
                {
                    await SendJson(ctx, 503, new { error = "a sim job is running; stop it (POST /api/sim/stop) or retry later" });
                    return;
                }
            }
            var advice = await Advise.CombatAdvisor.Advise(req, seeds, horizon);
            await SendJson(ctx, 200, advice);
        }
        catch (Exception ex)
        {
            await SendJson(ctx, 500, new { error = ex.Message });
        }
        finally
        {
            _jobGate.Release();
        }
    }

    private static int ParseQueryInt(HttpListenerContext ctx, string name, int fallback, int min, int max)
        => int.TryParse(ctx.Request.QueryString[name], out var v) ? Math.Clamp(v, min, max) : fallback;

    private static List<Harness.DeckEntry> ResolveEntries(IEnumerable<SaveFileReader.DeckCard> cards)
        => cards.Select(c =>
        {
            var t = CardIdResolver.Resolve(c.Id) ?? throw new ArgumentException($"Unknown card id '{c.Id}'");
            return new Harness.DeckEntry(t, c.UpgradeLevel);
        }).ToList();

    /// <summary>Apply remove/add requests to the save-file deck to produce the variant.</summary>
    private static List<SaveFileReader.DeckCard> ApplyChanges(
        IReadOnlyList<SaveFileReader.DeckCard> cards,
        IReadOnlyList<CardChange> removals,
        IReadOnlyList<CardChange> additions)
    {
        var result = new List<SaveFileReader.DeckCard>(cards);
        foreach (var rem in removals)
        {
            for (int n = 0; n < Math.Max(1, rem.Count); n++)
            {
                var idx = result.FindIndex(c => c.Id == rem.Id && c.UpgradeLevel == rem.Upgrade);
                if (idx < 0)
                    throw new ArgumentException($"Cannot remove '{rem.Id}' (upgrade {rem.Upgrade}): not (or no longer) in deck");
                result.RemoveAt(idx);
            }
        }
        foreach (var add in additions)
        {
            if (CardIdResolver.Resolve(add.Id) == null)
                throw new ArgumentException($"Unknown card id '{add.Id}'");
            for (int n = 0; n < Math.Max(1, add.Count); n++)
                result.Add(new SaveFileReader.DeckCard(add.Id, FloorAdded: 0, UpgradeLevel: add.Upgrade));
        }
        if (result.Count == 0)
            throw new ArgumentException("Variant deck would be empty");
        return result;
    }

    private static string DescribeChanges(IReadOnlyList<CardChange> removals, IReadOnlyList<CardChange> additions)
    {
        var parts = new List<string>();
        foreach (var r in removals)
            parts.Add($"−{CardLabels.Format(r.Id, r.Upgrade)}{(r.Count > 1 ? $" ×{r.Count}" : "")}");
        foreach (var a in additions)
            parts.Add($"+{CardLabels.Format(a.Id, a.Upgrade)}{(a.Count > 1 ? $" ×{a.Count}" : "")}");
        return string.Join(" · ", parts);
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
        var state = new SocketState { Ws = ws };
        lock (_socketsLock) _sockets.Add(state);

        // Per-socket writer pump: serializes SendAsync calls so we never call
        // SendAsync concurrently on the same connection (which corrupts WS
        // framing) and drops oldest events if the browser can't keep up.
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var bytes in state.Outbox.Reader.ReadAllAsync())
                {
                    if (ws.State != WebSocketState.Open) break;
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, default);
                }
            }
            catch { /* connection drop */ }
        });

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
            state.Outbox.Writer.TryComplete();
            lock (_socketsLock) _sockets.Remove(state);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default); } catch { }
            ws.Dispose();
        }
    }

    private Task BroadcastEvent(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        List<SocketState> snapshot;
        lock (_socketsLock) snapshot = _sockets.ToList();
        foreach (var state in snapshot)
        {
            if (state.Ws.State != WebSocketState.Open) continue;
            // TryWrite never blocks; under DropOldest we evict the oldest queued
            // payload if the browser is too slow.
            state.Outbox.Writer.TryWrite(bytes);
        }
        return Task.CompletedTask;
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
        /// <summary>Adaptive early-stop patience; 0 disables.</summary>
        public int Patience { get; set; } = 0;
        /// <summary>Cards to remove from the save deck (A/B variant / common edit).</summary>
        public List<CardChange>? Removals { get; set; }
        /// <summary>Cards to add to the save deck (A/B variant / common edit).</summary>
        public List<CardChange>? Additions { get; set; }
        /// <summary>Compare mode: each entry is tested as baseline + that one card.</summary>
        public List<CardChange>? Candidates { get; set; }
        /// <summary>Empty/null = dummy damage mode. Otherwise fight this encounter.</summary>
        public string? EncounterId { get; set; }
    }

    private sealed class CardChange
    {
        public string Id { get; set; } = "";
        public int Upgrade { get; set; }
        public int Count { get; set; } = 1;
    }
}
