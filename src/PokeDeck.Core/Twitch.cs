using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PokeDeck.Core;

/// <summary>A API do site da Twitch (gql.twitch.tv com o Client-Id público do site), como o PokeIdleMulti usa.</summary>
public static class TwitchApi
{
    public const string ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    public const string Gql = "https://gql.twitch.tv/gql";
    public const string GqlBase = "https://gql.twitch.tv";

    /// <summary>O user-agent do WebView2 (o app preenche ao abrir): os pedidos de fora da página dizem ser o mesmo navegador.</summary>
    public static string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36 Edg/141.0.0.0";

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string PlayerUrl(string channel) =>
        $"https://player.twitch.tv/?channel={Uri.EscapeDataString(channel)}&parent=twitch.tv&player=popout&autoplay=false&muted=true";

    internal static HttpRequestMessage Request(HttpMethod method, string url, string? token = null, string? device = null)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (url.StartsWith(Gql))
        {
            r.Headers.TryAddWithoutValidation("Client-Id", ClientId);
            if (token is not null)
                r.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
            if (device is not null)
                r.Headers.TryAddWithoutValidation("X-Device-Id", device);
        }
        return r;
    }

    internal static async Task<JsonElement?> QueryAsync(object body, string? token = null, string? device = null, CancellationToken stop = default)
    {
        using var r = Request(HttpMethod.Post, Gql, token, device);
        r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "text/plain");
        using var res = await Http.SendAsync(r, stop);
        if (!res.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(stop));
        return doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d.Clone() : null;
    }

    /// <summary>Cada canal: true no ar, false offline/inexistente, null sem resposta. Uma consulta para todos.</summary>
    public static async Task<Dictionary<string, bool?>> LiveAsync(IReadOnlyList<string> channels)
    {
        var result = channels.ToDictionary(c => c, _ => (bool?)null);
        if (channels.Count == 0)
            return result;
        try
        {
            if (await QueryAsync(new { query = "query($l:[String!]){users(logins:$l){login stream{id}}}", variables = new { l = channels } }) is JsonElement d)
                return ParseLive(channels, d);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
        }
        return result;
    }

    public static Dictionary<string, bool?> ParseLive(IReadOnlyList<string> channels, JsonElement data)
    {
        var result = channels.ToDictionary(c => c, _ => (bool?)null);
        if (!data.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array)
            return result;
        var found = new Dictionary<string, bool>();
        foreach (var u in users.EnumerateArray())
            if (u.ValueKind == JsonValueKind.Object && u.TryGetProperty("login", out var l) && l.GetString() is string name)
                found[name.ToLowerInvariant()] = u.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.Object;
        foreach (var c in channels)
            result[c] = found.TryGetValue(c.ToLowerInvariant(), out var live) ? live : false;
        return result;
    }

    /// <summary>O id da conta logada (o cookie auth-token).</summary>
    public static async Task<string?> UserIdAsync(string token, string? device)
    {
        try
        {
            var d = await QueryAsync(new { query = "query{currentUser{id}}" }, token, device);
            return d is JsonElement e && e.TryGetProperty("currentUser", out var u) && u.ValueKind == JsonValueKind.Object
                ? u.GetProperty("id").GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Assiste às lives da Twitch sem player: o aviso "minute-watched" a cada ~minuto (o que o player manda) e, como um
/// player, a lista de pedaços na menor qualidade com só o HEAD de cada pedaço novo. Espelho do TwitchWatcher do
/// PokeIdleMulti, com jitter em todos os tempos.
/// </summary>
public sealed class TwitchWatcher : IDisposable
{
    private const string PlaybackHash = "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9";
    private static readonly Regex SettingsJs = new(@"(https?://[^""'\s]+/config/settings[^""'\s]*?\.js)");
    private static readonly Regex SpadeRx = new(@"""spade_url""\s*:\s*""(.*?)""");
    private static readonly Regex Bandwidth = new(@"(?<![-A-Z])BANDWIDTH=(\d+)");
    private static (string Url, DateTime At)? _spade;

    private readonly string _userId, _token;
    private readonly string? _device;
    private readonly Func<double> _jitter;
    private readonly Dictionary<string, Live> _lives = new();
    private readonly CancellationTokenSource _stop = new();

    private sealed class Live
    {
        public required string Login;
        public string? ChannelId, BroadcastId, Game, GameId, MediaUrl, Error;
        public bool Offline;
        public DateTime InfoAt, MinuteAt = DateTime.Now, PlaylistAt, MediaAt;
        public DateTime? WatchedAt;
        public readonly HashSet<string> Seen = new();
        public readonly Queue<string> SeenOrder = new();
    }

    public TwitchWatcher(string userId, string token, string? device, Func<double> jitter)
    {
        (_userId, _token, _device, _jitter) = (userId, token, device, jitter);
        _ = RunAsync();
    }

    public string Token => _token;

    public void SetChannels(IEnumerable<string> logins)
    {
        var next = logins.ToHashSet();
        foreach (var gone in _lives.Keys.Where(l => !next.Contains(l)).ToList())
            _lives.Remove(gone);
        foreach (var l in next.Where(l => !_lives.ContainsKey(l)))
            _lives[l] = new Live { Login = l };
    }

    public IReadOnlyCollection<string> Channels => _lives.Keys;

    public string Status(string login) =>
        !_lives.TryGetValue(login, out var l) ? "" : l.Error ?? (l.WatchedAt is DateTime at ? $"✓ {at:HH:mm}" : "…");

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            foreach (var live in _lives.Values.ToList())
            {
                try
                {
                    await StepAsync(live);
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    live.Error = ex is TaskCanceledException ? "a Twitch não respondeu" : ex.Message;
                }
            }
            try
            {
                await Task.Delay(Jitter.Of(TimeSpan.FromSeconds(5), 0.4), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task StepAsync(Live live)
    {
        var now = DateTime.Now;
        if (live.ChannelId is null || now >= live.InfoAt)
            await InfoAsync(live);
        if (live.Offline || live.BroadcastId is null)
            return;
        if (now >= live.PlaylistAt)
        {
            live.PlaylistAt = now + Jitter.Of(TimeSpan.FromSeconds(20), _jitter());
            await SegmentsAsync(live);
        }
        if (now >= live.MinuteAt)
        {
            live.MinuteAt = now + Jitter.Of(TimeSpan.FromSeconds(60), Math.Min(_jitter(), 0.1)); // o player manda ~1/min
            await MinuteAsync(live);
        }
    }

    private async Task InfoAsync(Live live)
    {
        live.InfoAt = DateTime.Now + Jitter.Of(TimeSpan.FromMinutes(2), _jitter());
        var d = await TwitchApi.QueryAsync(new { query = "query($l:String!){user(login:$l){id stream{id game{id name}}}}", variables = new { l = live.Login } },
            stop: _stop.Token);
        if (d is not JsonElement e || !e.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            live.Error = "canal não encontrado";
            return;
        }
        live.ChannelId = user.GetProperty("id").GetString();
        if (!user.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Object)
        {
            live.Offline = true;
            live.Error = "offline";
            return;
        }
        var broadcast = stream.GetProperty("id").GetString();
        if (broadcast != live.BroadcastId)
        {
            live.MediaUrl = null;
            live.Seen.Clear();
            live.SeenOrder.Clear();
        }
        live.BroadcastId = broadcast;
        live.Offline = false;
        if (stream.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.Object)
            (live.Game, live.GameId) = (g.GetProperty("name").GetString(), g.GetProperty("id").GetString());
        if (live.Error == "offline")
            live.Error = null;
    }

    private async Task MinuteAsync(Live live)
    {
        if (await SpadeAsync(live.Login) is not string spade)
        {
            live.Error = "sem o endereço do minute-watched";
            return;
        }
        var props = new Dictionary<string, object?>
        {
            ["channel_id"] = live.ChannelId, ["broadcast_id"] = live.BroadcastId, ["player"] = "site",
            ["user_id"] = _userId, ["live"] = true, ["channel"] = live.Login,
        };
        if (live.Game is not null && live.GameId is not null)
            (props["game"], props["game_id"]) = (live.Game, live.GameId);
        var json = JsonSerializer.Serialize(new[] { new { @event = "minute-watched", properties = props } });
        using var r = TwitchApi.Request(HttpMethod.Post, spade);
        r.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) });
        using var res = await TwitchApi.Http.SendAsync(r, _stop.Token);
        if (res.IsSuccessStatusCode)
        {
            live.WatchedAt = DateTime.Now;
            live.Error = null;
        }
        else
        {
            live.Error = $"minute-watched: erro {(int)res.StatusCode}";
            _spade = null;
        }
    }

    private async Task<string?> SpadeAsync(string login)
    {
        if (_spade is { } known && DateTime.Now - known.At < TimeSpan.FromHours(6))
            return known.Url;
        using var page = TwitchApi.Request(HttpMethod.Get, $"https://www.twitch.tv/{Uri.EscapeDataString(login)}");
        using var pageRes = await TwitchApi.Http.SendAsync(page, _stop.Token);
        var settings = SettingsJs.Match(await pageRes.Content.ReadAsStringAsync(_stop.Token));
        if (!settings.Success)
            return null;
        using var cfg = TwitchApi.Request(HttpMethod.Get, settings.Groups[1].Value);
        using var cfgRes = await TwitchApi.Http.SendAsync(cfg, _stop.Token);
        var m = SpadeRx.Match(await cfgRes.Content.ReadAsStringAsync(_stop.Token));
        if (!m.Success)
            return null;
        var url = JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"") ?? m.Groups[1].Value;
        _spade = (url, DateTime.Now);
        return url;
    }

    private async Task SegmentsAsync(Live live)
    {
        if (live.MediaUrl is null || DateTime.Now - live.MediaAt > TimeSpan.FromMinutes(10))
        {
            live.MediaUrl = await MediaUrlAsync(live);
            live.MediaAt = DateTime.Now;
        }
        if (live.MediaUrl is not string media)
            return;
        using var r = TwitchApi.Request(HttpMethod.Get, media);
        using var res = await TwitchApi.Http.SendAsync(r, _stop.Token);
        if (!res.IsSuccessStatusCode)
        {
            live.MediaUrl = null;
            return;
        }
        foreach (var line in (await res.Content.ReadAsStringAsync(_stop.Token)).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var seg = new Uri(new Uri(media), line).ToString();
            if (!live.Seen.Add(seg))
                continue;
            live.SeenOrder.Enqueue(seg);
            while (live.SeenOrder.Count > 300)
                live.Seen.Remove(live.SeenOrder.Dequeue());
            using var head = TwitchApi.Request(HttpMethod.Head, seg);
            using var _ = await TwitchApi.Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, _stop.Token);
        }
    }

    private async Task<string?> MediaUrlAsync(Live live)
    {
        object body = new
        {
            operationName = "PlaybackAccessToken",
            variables = new { login = live.Login, isLive = true, isVod = false, vodID = "", playerType = "site", platform = "web" },
            extensions = new { persistedQuery = new { version = 1, sha256Hash = PlaybackHash } },
        };
        JsonElement? token = null;
        foreach (var withLogin in new[] { true, false })
            if (await TwitchApi.QueryAsync(body, withLogin ? _token : null, withLogin ? _device : null, _stop.Token) is JsonElement d
                && d.TryGetProperty("streamPlaybackAccessToken", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                token = t;
                break;
            }
        if (token is not JsonElement a)
            return null;
        string master = $"https://usher.ttvnw.net/api/channel/hls/{Uri.EscapeDataString(live.Login)}.m3u8" +
                        $"?sig={Uri.EscapeDataString(a.GetProperty("signature").GetString() ?? "")}" +
                        $"&token={Uri.EscapeDataString(a.GetProperty("value").GetString() ?? "")}" +
                        $"&allow_source=true&allow_audio_only=true&player=twitchweb&type=any&p={Random.Shared.Next(1_000_000, 9_999_999)}";
        using var r = TwitchApi.Request(HttpMethod.Get, master);
        using var res = await TwitchApi.Http.SendAsync(r, _stop.Token);
        return res.IsSuccessStatusCode ? LowestVariant(master, await res.Content.ReadAsStringAsync(_stop.Token)) : null;
    }

    /// <summary>A variante de menor banda que tem vídeo (RESOLUTION=), ou a última se nenhuma tiver.</summary>
    public static string? LowestVariant(string master, string playlist)
    {
        var lines = playlist.Replace("\r\n", "\n").Split('\n');
        var variants = new List<(long Bw, bool Video, string Uri)>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:"))
                continue;
            int next = i + 1;
            while (next < lines.Length && (lines[next].Length == 0 || lines[next].StartsWith('#')))
                next++;
            if (next >= lines.Length)
                break;
            var m = Bandwidth.Match(lines[i]);
            variants.Add((m.Success ? long.Parse(m.Groups[1].Value) : long.MaxValue, lines[i].Contains("RESOLUTION="), new Uri(new Uri(master), lines[next]).ToString()));
            i = next;
        }
        return variants.Where(v => v.Video).OrderBy(v => v.Bw).Select(v => v.Uri).FirstOrDefault() ?? variants.Select(v => v.Uri).LastOrDefault();
    }

    public void Dispose() => _stop.Cancel();
}

/// <summary>
/// Os avisos da Twitch da conta (PubSub, tópico community-points-user-v1.&lt;id&gt;): o baú que apareceu
/// (claim-available) e os pontos que entraram. Reconecta sozinho (5 s … 2 min); ERR_BADAUTH para de vez.
/// </summary>
public sealed class TwitchPubSub : IDisposable
{
    public sealed record Notice(string Type, string? ChannelId, string? ClaimId);

    private readonly string _token, _userId;
    private readonly Action<Notice> _notice;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private DateTime _pongAt;

    public TwitchPubSub(string token, string userId, Action<Notice> notice)
    {
        (_token, _userId, _notice) = (token, userId, notice);
        _ = RunAsync();
    }

    public string? Error { get; private set; }

    private async Task RunAsync()
    {
        var wait = TimeSpan.FromSeconds(5);
        while (!_stop.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("User-Agent", TwitchApi.UserAgent);
            bool quit = false;
            try
            {
                await ws.ConnectAsync(new Uri("wss://pubsub-edge.twitch.tv/v1"), _stop.Token);
                Error = null;
                wait = TimeSpan.FromSeconds(5);
                _pongAt = DateTime.Now;
                await SendAsync(ws, JsonSerializer.Serialize(new
                {
                    type = "LISTEN", nonce = Guid.NewGuid().ToString("N"),
                    data = new { topics = new[] { "community-points-user-v1." + _userId }, auth_token = _token },
                }));
                quit = await ReceiveAsync(ws);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            if (quit)
                return;
            try
            {
                await Task.Delay(Jitter.Of(wait, 0.3), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, TimeSpan.FromMinutes(2).Ticks));
        }
    }

    private async Task<bool> ReceiveAsync(ClientWebSocket ws)
    {
        using var alive = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _ = PingAsync(ws, alive.Token);
        try
        {
            var buf = new byte[16 * 1024];
            var text = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                var part = await ws.ReceiveAsync(buf, alive.Token);
                if (part.MessageType == WebSocketMessageType.Close)
                    return false;
                text.Append(Encoding.UTF8.GetString(buf, 0, part.Count));
                if (!part.EndOfMessage)
                    continue;
                var msg = text.ToString();
                text.Clear();
                if (Handle(msg) is bool stop)
                    return stop;
            }
            return false;
        }
        finally
        {
            alive.Cancel();
        }
    }

    /// <summary>true: parar; false: reconectar; null: seguir.</summary>
    private bool? Handle(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "PONG":
                    _pongAt = DateTime.Now;
                    return null;
                case "RECONNECT":
                    return false;
                case "RESPONSE":
                    var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                    if (string.IsNullOrEmpty(err))
                        return null;
                    Error = err == "ERR_BADAUTH" ? "a Twitch recusou o login nos avisos" : err;
                    return err == "ERR_BADAUTH" ? true : null;
                case "MESSAGE":
                    using (var inner = JsonDocument.Parse(root.GetProperty("data").GetProperty("message").GetString() ?? "{}"))
                        _notice(ParseNotice(inner.RootElement));
                    return null;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
        return null;
    }

    public static Notice ParseNotice(JsonElement m)
    {
        string type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        string? channel = null, claim = null;
        if (m.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            if (d.TryGetProperty("claim", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                channel = c.TryGetProperty("channel_id", out var ch) ? ch.GetString() : null;
                claim = c.TryGetProperty("id", out var id) ? id.GetString() : null;
            }
            channel ??= d.TryGetProperty("channel_id", out var direct) ? direct.GetString() : null;
            if (channel is null && d.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.Object)
                channel = b.TryGetProperty("channel_id", out var bc) ? bc.GetString() : null;
        }
        return new Notice(type, channel, claim);
    }

    private async Task PingAsync(ClientWebSocket ws, CancellationToken alive)
    {
        try
        {
            while (!alive.IsCancellationRequested)
            {
                await Task.Delay(Jitter.Of(TimeSpan.FromMinutes(1), 0.2), alive);
                var sent = DateTime.Now;
                await SendAsync(ws, "{\"type\":\"PING\"}");
                await Task.Delay(TimeSpan.FromSeconds(15), alive);
                if (_pongAt < sent)
                {
                    ws.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAsync(ClientWebSocket ws, string text)
    {
        await _sending.WaitAsync();
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            // caiu no meio: o laço reconecta
        }
        finally
        {
            _sending.Release();
        }
    }

    public void Dispose() => _stop.Cancel();
}
