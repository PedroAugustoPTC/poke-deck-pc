using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PokeDeck.Core;

public static class KickApi
{
    public const string Base = "https://kick.com";

    // O token público do player para o websocket de espectador (NEXT_PUBLIC_WEBSOCKET_CLIENT_TOKEN, lido pelo PokeIdleMulti).
    public const string ClientToken = "e1393935a959b4020a4491574f6490129f678acdaa92760471263db43487f823";

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string PlayerUrl(string slug) => $"https://player.kick.com/{Uri.EscapeDataString(slug)}?autoplay=false&muted=true";

    internal static HttpRequestMessage Request(string url, string? token = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.TryAddWithoutValidation("User-Agent", TwitchApi.UserAgent);
        r.Headers.TryAddWithoutValidation("Accept", "application/json");
        r.Headers.TryAddWithoutValidation("x-app-platform", "web");
        r.Headers.TryAddWithoutValidation("Referer", "https://kick.com");
        if (token is not null)
            r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return r;
    }

    /// <summary>true no ar, false offline, null sem resposta.</summary>
    public static async Task<bool?> IsLiveAsync(string slug)
    {
        try
        {
            using var r = Request($"{Base}/api/v2/channels/{Uri.EscapeDataString(slug)}");
            using var res = await Http.SendAsync(r);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return false;
            return res.IsSuccessStatusCode ? ParseLive(await res.Content.ReadAsStringAsync()) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public static bool? ParseLive(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("livestream", out var live))
                return null;
            return live.ValueKind == JsonValueKind.Object && !(live.TryGetProperty("is_live", out var l) && l.ValueKind == JsonValueKind.False);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Assiste às lives da Kick sem player (espelho do KickWatcher do PokeIdleMulti): token de espectador com o login,
/// websocket de espectador por live, "channel_handshake" ~15 s, "ping" ~30 s e "tracking.user.watch.livestream" no
/// intervalo que a Kick pede (~120 s). Todos com jitter.
/// </summary>
public sealed class KickWatcher : IDisposable
{
    private readonly string _token;
    private readonly Func<double> _jitter;
    private readonly Dictionary<string, Live> _lives = new();
    private readonly CancellationTokenSource _stop = new();
    private TimeSpan? _asked;

    private sealed class Live
    {
        public required string Slug;
        public required CancellationTokenSource Stop;
        public JsonNode? ChannelId, LivestreamId, VodId;
        public bool Offline;
        public DateTime? WatchedAt;
        public string? Error;
    }

    public KickWatcher(string token, Func<double> jitter) => (_token, _jitter) = (token, jitter);

    public string Token => _token;
    public IReadOnlyCollection<string> Channels => _lives.Keys;

    public void SetChannels(IEnumerable<string> slugs)
    {
        var next = slugs.ToHashSet();
        foreach (var gone in _lives.Keys.Where(s => !next.Contains(s)).ToList())
        {
            _lives[gone].Stop.Cancel();
            _lives.Remove(gone);
        }
        foreach (var slug in next.Where(s => !_lives.ContainsKey(s)))
        {
            var live = new Live { Slug = slug, Stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token) };
            _lives[slug] = live;
            _ = RunAsync(live);
        }
    }

    public string Status(string slug) =>
        !_lives.TryGetValue(slug, out var l) ? "" : l.Error ?? (l.Offline ? "offline" : l.WatchedAt is DateTime at ? $"✓ {at:HH:mm}" : "…");

    private TimeSpan J(double seconds) => Jitter.Of(TimeSpan.FromSeconds(seconds), _jitter());

    private async Task RunAsync(Live live)
    {
        var stop = live.Stop.Token;
        int failures = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await InfoAsync(live, stop);
                var viewer = await ViewerTokenAsync(stop);
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("User-Agent", TwitchApi.UserAgent);
                ws.Options.SetRequestHeader("Origin", "https://kick.com");
                await ws.ConnectAsync(new Uri($"wss://websockets.kick.com/viewer/v1/connect?token={Uri.EscapeDataString(viewer)}"), stop);
                live.Error = null;
                failures = 0;
                var every = await AskedIntervalAsync(stop);
                var receiving = DrainAsync(ws);
                DateTime now = DateTime.Now, handshakeAt = now, watchAt = now + J(10), pingAt = now + J(30), infoAt = now + J(120);
                try
                {
                    while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        now = DateTime.Now;
                        if (now >= infoAt)
                        {
                            infoAt = now + J(120);
                            try
                            {
                                await InfoAsync(live, stop);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                            }
                        }
                        if (now >= handshakeAt && live.ChannelId is JsonNode cid)
                        {
                            handshakeAt = now + J(15);
                            await SendAsync(ws, Message("channel_handshake", new JsonObject { ["channelId"] = cid.ToString() }), stop);
                        }
                        if (now >= pingAt)
                        {
                            pingAt = now + J(30);
                            await SendAsync(ws, "{\"type\":\"ping\"}", stop);
                        }
                        if (now >= watchAt && !live.Offline && live is { ChannelId: not null, LivestreamId: not null, VodId: not null })
                        {
                            watchAt = now + Jitter.Of(every, Math.Min(_jitter(), 0.1));
                            await SendAsync(ws, Message("user_event", new JsonObject
                            {
                                ["name"] = "tracking.user.watch.livestream",
                                ["channel_id"] = live.ChannelId.DeepClone(),
                                ["livestream_id"] = live.LivestreamId.DeepClone(),
                                ["vod_id"] = live.VodId.DeepClone(),
                            }), stop);
                            live.WatchedAt = DateTime.Now;
                        }
                        await Task.Delay(TimeSpan.FromSeconds(1), stop);
                    }
                    if (!stop.IsCancellationRequested)
                        live.Error = "a Kick fechou a conexão";
                }
                finally
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try
                        {
                            if (live.ChannelId is JsonNode cid)
                                await SendAsync(ws, Message("channel_disconnect", new JsonObject { ["channelId"] = cid.ToString() }), quick.Token);
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", quick.Token);
                        }
                        catch
                        {
                        }
                    }
                    await receiving;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                live.Error = ex is TaskCanceledException ? "a Kick não respondeu" : ex.Message;
            }
            failures++;
            try
            {
                await Task.Delay(J(Math.Min(120, 2 << Math.Min(failures, 6))), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string Message(string type, JsonObject message) =>
        new JsonObject { ["type"] = type, ["data"] = new JsonObject { ["message"] = message } }.ToJsonString();

    private static Task SendAsync(ClientWebSocket ws, string text, CancellationToken stop) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, stop);

    private static async Task DrainAsync(ClientWebSocket ws)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
                if ((await ws.ReceiveAsync(buf, CancellationToken.None)).MessageType == WebSocketMessageType.Close)
                    return;
        }
        catch
        {
        }
    }

    private async Task InfoAsync(Live live, CancellationToken stop)
    {
        using var r = KickApi.Request($"{KickApi.Base}/api/v2/channels/{Uri.EscapeDataString(live.Slug)}/info", _token);
        using var res = await KickApi.Http.SendAsync(r, stop);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"canal: erro {(int)res.StatusCode}");
        var root = JsonNode.Parse(await res.Content.ReadAsStringAsync(stop)) as JsonObject ?? throw new InvalidOperationException("canal sem dados");
        if (root["data"] is JsonObject wrapped && root["id"] is null)
            root = wrapped;
        live.ChannelId = root["id"]?.DeepClone() ?? throw new InvalidOperationException("canal sem id");
        var stream = root["livestream"] as JsonObject;
        live.Offline = stream is null || stream["is_live"]?.GetValueKind() == JsonValueKind.False;
        live.LivestreamId = stream?["id"]?.DeepClone();
        live.VodId = stream?["vod_id"] is JsonNode vod && vod.GetValueKind() != JsonValueKind.Null ? vod.DeepClone() : null;
    }

    private async Task<string> ViewerTokenAsync(CancellationToken stop)
    {
        using var r = KickApi.Request("https://websockets.kick.com/viewer/v1/token", _token);
        r.Headers.TryAddWithoutValidation("X-CLIENT-TOKEN", KickApi.ClientToken);
        using var res = await KickApi.Http.SendAsync(r, stop);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"token de espectador: erro {(int)res.StatusCode}");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(stop));
        return doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("token", out var t) && t.GetString() is { Length: > 0 } v
            ? v : throw new InvalidOperationException("sem token de espectador");
    }

    private async Task<TimeSpan> AskedIntervalAsync(CancellationToken stop)
    {
        if (_asked is TimeSpan known)
            return known;
        var asked = TimeSpan.FromSeconds(120);
        try
        {
            using var r = KickApi.Request(KickApi.Base + "/api/internal/settings/global", _token);
            using var res = await KickApi.Http.SendAsync(r, stop);
            if (res.IsSuccessStatusCode && JsonNode.Parse(await res.Content.ReadAsStringAsync(stop)) is JsonObject root
                && (root["events"] ?? root["data"]?["events"])?["tracking.user.watch.livestream"]?["interval"] is JsonNode i
                && i.GetValueKind() == JsonValueKind.Number)
                asked = TimeSpan.FromSeconds(Math.Clamp(i.GetValue<double>(), 30, 600));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        return (_asked = asked).Value;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _lives.Clear();
    }
}
