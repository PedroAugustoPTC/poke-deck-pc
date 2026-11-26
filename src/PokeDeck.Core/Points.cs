using System.Text.Json;

namespace PokeDeck.Core;

/// <summary>
/// Pontos de canal de uma conta num site: os scripts que rodam DENTRO da página real (player da Twitch/Kick, com a
/// checagem anti-robô e os cookies do próprio site) e o acompanhamento de cada canal (saldo, espera após recusa,
/// detecção de pontos devolvidos). Protocolos espelhados do PointsRedeemer do PokeIdleMulti.
/// </summary>
public sealed class Points
{
    public static readonly TimeSpan RefundWindow = TimeSpan.FromMinutes(30);

    public sealed class Channel
    {
        public int? Points, Cost;
        public bool Paused;
        public DateTime? RedeemedAt;
        public DateTime RetryAt, ClaimRetryAt;
        public string Text = "";
    }

    private readonly Dictionary<string, Channel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _now;

    public Points(string site, Func<DateTime>? now = null) => (Site, _now) = (site, now ?? (() => DateTime.Now));

    public string Site { get; }

    /// <summary>Por que parou de resgatar nesta conta (pontos devolvidos: site não vinculado no jogo). null: resgatando.</summary>
    public string? Stopped { get; set; }

    public TimeSpan RetryWait { get; set; } = TimeSpan.FromMinutes(30);

    public IReadOnlyDictionary<string, Channel> Channels => _channels;

    private Channel Of(string ch) => _channels.TryGetValue(ch, out var c) ? c : _channels[ch] = new Channel();

    public bool CanSpend(string ch) => Stopped is null && (!_channels.TryGetValue(ch, out var c) || _now() >= c.RetryAt);

    public bool CanClaim(string ch) => !_channels.TryGetValue(ch, out var c) || _now() >= c.ClaimRetryAt;

    /// <summary>A partir desse saldo, os pontos do último resgate voltaram (não resgata em cima da devolução).</summary>
    public int? RefundAt(string ch) =>
        _channels.TryGetValue(ch, out var c) && c is { Points: int p, Cost: int cost, RedeemedAt: DateTime at } && _now() - at < RefundWindow
            ? p + cost / 2 : null;

    /// <summary>Lê o JSON que o script devolveu; devolve as linhas para o registro.</summary>
    public List<string> Apply(string ch, string json)
    {
        var notes = new List<string>();
        var c = Of(ch);
        var now = _now();
        JsonElement r;
        try
        {
            using var doc = JsonDocument.Parse(json);
            r = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            c.Text = "resposta estranha da página";
            return notes;
        }
        static int? Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : null;
        static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool Is(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

        int? points = Int(r, "points");
        if (points is int pts && RefundAt(ch) is int refund && pts >= refund)
        {
            Stopped = "os pontos do resgate voltaram (o site está vinculado no jogo?)";
            notes.Add($"{Site} {ch}: {Stopped}; parei de resgatar nesta conta");
        }
        if (Is(r, "claimed"))
            notes.Add($"{Site} {ch}: pegou o baú (+50)");
        if (Str(r, "claimError") is string ce)
        {
            c.ClaimRetryAt = now + RetryWait;
            notes.Add($"{Site} {ch}: baú recusado ({ce})");
        }
        if (Int(r, "cost") is int cost)
            c.Cost = cost;
        c.Paused = Is(r, "paused");
        string title = Str(r, "title") ?? "bônus";
        if (Is(r, "redeemed"))
        {
            c.RedeemedAt = now;
            if (points is int p2 && c.Cost is int k)
                points = p2 - k;
            notes.Add($"{Site} {ch}: resgatou {title}");
        }
        else if (r.TryGetProperty("refused", out _) || r.TryGetProperty("redeemed", out _))
        {
            c.RetryAt = now + RetryWait;
            notes.Add($"{Site} {ch}: resgate recusado ({Str(r, "refused") ?? "erro " + Int(r, "status")}); tento de novo em {RetryWait.TotalMinutes:0} min");
        }
        if (points is int final)
            c.Points = final;
        var error = Str(r, "error");
        c.Text = error ?? (c.Points is int show ? c.Cost is int ck ? $"{show}/{ck}{(c.Paused ? " pausado" : "")}" : $"{show}" : "");
        return notes;
    }

    /// <summary>A palavra do bônus no título da recompensa do canal.</summary>
    public static string Word(string kind) => kind switch { "captura" => "captur", "shiny" => "lure|shiny", _ => "\\bxp\\b" };

    // ---- Scripts (rodam na página real; devolvem uma Promise de texto JSON) ----

    private const string TwitchGql =
        " const base = { 'Client-Id': a.clientId, Authorization: 'OAuth ' + a.token, 'Content-Type': 'text/plain;charset=UTF-8'," +
        "  ...(a.device ? { 'X-Device-Id': a.device } : {}) };" +
        " let integrity = null;" +
        " const gql = async (query, variables) => {" +
        "  const body = JSON.stringify({ query, variables });" +
        "  for (let i = 0; i < 2; i++) {" +
        "   const r = await fetch(a.api + '/gql', { method: 'POST', cache: 'no-cache'," +
        "    headers: integrity ? { ...base, 'Client-Integrity': integrity } : base, body });" +
        "   if (!r.ok) return { failed: 'erro ' + r.status };" +
        "   const j = await r.json();" +
        "   const challenged = !!j?.extensions?.challenge || (j?.errors || []).some(e => e?.extensions?.code === 'IntegrityCheckFailed');" +
        "   if (!challenged) return j;" +
        "   if (integrity) return { failed: 'checagem anti-robô da Twitch' };" +
        "   const t = await fetch(a.api + '/integrity', { method: 'POST', cache: 'no-cache', headers: base });" +
        "   const tj = t.ok ? await t.json() : null;" +
        "   if (!tj?.token) return { failed: 'checagem anti-robô da Twitch (' + t.status + ')' };" +
        "   integrity = tj.token;" +
        "  }" +
        " };" +
        " const fail = (j, x) => j?.failed || x?.error?.code || j?.errors?.[0]?.message || 'sem resposta';";

    /// <summary>
    /// Twitch: lê o saldo; com <paramref name="claim"/>, pega o baú (do availableClaim ou do id do aviso); com
    /// <paramref name="kind"/> e <paramref name="spend"/>, resgata 1 (a duração liberada mais barata).
    /// </summary>
    public static string TwitchScript(string channel, string token, string? device, string? kind, bool claim, bool spend, int? refund, string? claimId = null)
    {
        var args = JsonSerializer.Serialize(new
        {
            api = TwitchApi.GqlBase, channel, token, device, clientId = TwitchApi.ClientId,
            word = kind is null ? null : Word(kind), spend, refund, claim, claimId,
        });
        return "(async a => {" + TwitchGql +
               " try {" +
               "  const j = await gql('query($login:String!){currentUser{id} user(login:$login){id channel{self{communityPoints{balance availableClaim{id}}}' +" +
               "   ' communityPointsSettings{customRewards{id title prompt cost isEnabled isPaused isInStock cooldownExpiresAt}}}}}', { login: a.channel });" +
               "  if (j?.failed) return { error: 'saldo: ' + j.failed };" +
               "  const user = j?.data?.user;" +
               "  if (!user?.channel) return { error: 'canal não encontrado' };" +
               "  const cp = user.channel.self?.communityPoints;" +
               "  if (typeof cp?.balance !== 'number') return { error: 'sem saldo (login da Twitch?)' };" +
               "  let points = cp.balance; const out = { points, me: j?.data?.currentUser?.id ?? null, channelId: user.id };" +
               "  const claimId = cp.availableClaim?.id || a.claimId;" +
               "  if (a.claim && claimId) {" +
               "   const k = await gql('mutation($i:ClaimCommunityPointsInput!){claimCommunityPoints(input:$i){claim{id} currentPoints error{code}}}'," +
               "    { i: { channelID: user.id, claimID: claimId } });" +
               "   const p = k?.data?.claimCommunityPoints;" +
               "   if (p && !p.error && !k.errors) { out.claimed = true; points = typeof p.currentPoints === 'number' ? p.currentPoints : points + 50; }" +
               "   else out.claimError = fail(k, p);" +
               "   out.points = points;" +
               "  }" +
               "  if (!a.word) return out;" +
               "  const word = new RegExp(a.word, 'i');" +
               "  const all = (user.channel.communityPointsSettings?.customRewards || []).slice().sort((x, y) => x.cost - y.cost)" +
               "   .filter(x => x.isEnabled && word.test(x.title || '') && /pokeidle/i.test((x.title || '') + ' ' + (x.prompt || '')));" +
               "  const shut = x => x.isPaused || !x.isInStock || !!x.cooldownExpiresAt;" +
               "  const reward = all.find(x => !shut(x)) || all[0];" +
               "  if (!reward) return { ...out, error: 'canal sem o bônus' };" +
               "  Object.assign(out, { cost: reward.cost, title: reward.title, paused: shut(reward) });" +
               "  if (!a.spend || out.paused || points < reward.cost || (a.refund !== null && points >= a.refund)) return out;" +
               "  const id = [...crypto.getRandomValues(new Uint8Array(16))].map(b => b.toString(16).padStart(2, '0')).join('');" +
               "  const r = await gql('mutation($i:RedeemCommunityPointsCustomRewardInput!){redeemCommunityPointsCustomReward(input:$i){redemption{id} error{code}}}'," +
               "   { i: { channelID: user.id, cost: reward.cost, prompt: reward.prompt ?? null, rewardID: reward.id, title: reward.title, transactionID: id } });" +
               "  const p = r?.data?.redeemCommunityPointsCustomReward;" +
               "  if (p && !p.error && !r.errors) return { ...out, redeemed: true };" +
               "  return { ...out, refused: fail(r, p) };" +
               " } catch (e) { return { error: 'sem resposta da Twitch: ' + (e && e.message || e) }; }" +
               $"}})({args}).then(o => JSON.stringify(o))";
    }

    /// <summary>Kick: lê o saldo e, com <paramref name="spend"/>, resgata 1 (a duração liberada mais barata).</summary>
    public static string KickScript(string channel, string token, string? kind, bool spend, int? refund)
    {
        var args = JsonSerializer.Serialize(new { api = KickApi.Base, channel, token, word = kind is null ? null : Word(kind), spend, refund });
        return "(async a => {" +
               " const call = (path, init) => fetch(a.api + '/api/v2/channels/' + encodeURIComponent(a.channel) + path," +
               "  { cache: 'no-cache', credentials: 'include', ...init, headers: { Accept: 'application/json', 'x-app-platform': 'web'," +
               "   Authorization: 'Bearer ' + a.token, ...(init && init.body ? { 'Content-Type': 'application/json' } : {}) } });" +
               " try {" +
               "  let r = await call('/points');" +
               "  if (!r.ok) return { error: 'saldo: erro ' + r.status };" +
               "  const points = (await r.json())?.data?.points;" +
               "  if (typeof points !== 'number') return { error: 'sem saldo (login da Kick?)' };" +
               "  if (!a.word) return { points };" +
               "  r = await call('/rewards');" +
               "  if (!r.ok) return { points, error: 'bônus: erro ' + r.status };" +
               "  const word = new RegExp(a.word, 'i');" +
               "  const all = ((await r.json())?.data || []).slice().sort((x, y) => x.cost - y.cost)" +
               "   .filter(x => x.is_enabled && word.test(x.title || '') && /pokeidle/i.test((x.title || '') + ' ' + (x.description || '')));" +
               "  const reward = all.find(x => !x.is_paused) || all[0];" +
               "  if (!reward) return { points, error: 'canal sem o bônus' };" +
               "  const out = { points, cost: reward.cost, title: reward.title, paused: !!reward.is_paused };" +
               "  if (!a.spend || reward.is_paused || points < reward.cost || (a.refund !== null && points >= a.refund)) return out;" +
               "  const abc = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'; let t = Date.now(), id = '';" +
               "  for (let i = 0; i < 10; i++) { id = abc[t % 32] + id; t = Math.floor(t / 32); }" +
               "  for (let i = 0; i < 16; i++) id += abc[Math.floor(Math.random() * 32)];" +
               "  r = await call('/rewards/' + reward.id + '/redeem', { method: 'POST', body: JSON.stringify({ transaction_id: id, message: '' }) });" +
               "  return { ...out, redeemed: r.ok, status: r.status };" +
               " } catch (e) { return { error: 'sem resposta da Kick: ' + (e && e.message || e) }; }" +
               $"}})({args}).then(o => JSON.stringify(o))";
    }
}

/// <summary>
/// A guarda do bônus numa conta e num site: quando o bônus escolhido vence em menos de X min, resgata 1 num canal com
/// pontos parados (fora os abertos, que o resgate automático já usa). Offline primeiro, depois o de mais pontos.
/// </summary>
public sealed class Guard
{
    public DateTime HoldUntil;
    public DateTime? RedeemedAt;
    public int LeftBefore;
    public bool NoneNoted;
    public readonly Dictionary<string, (DateTime At, int? Points, int? Cost, bool Paused)> Reads = new(StringComparer.OrdinalIgnoreCase);

    public static List<string> Candidates(IEnumerable<string> configured, IEnumerable<string> official, IEnumerable<string> open)
    {
        var skip = new HashSet<string>(open, StringComparer.OrdinalIgnoreCase);
        return configured.Concat(official).Where(c => c.Length > 0 && !skip.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Remember(string channel, string json, DateTime now)
    {
        int? points = null, cost = null;
        bool paused = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            points = r.TryGetProperty("points", out var p) && p.TryGetInt32(out var pv) ? pv : null;
            cost = r.TryGetProperty("cost", out var k) && k.TryGetInt32(out var kv) ? kv : null;
            paused = r.TryGetProperty("paused", out var z) && z.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
        }
        Reads[channel] = (now, points, cost, paused);
    }

    public string? Pick(IEnumerable<string> candidates, ISet<string> live) =>
        candidates
            .Where(c => Reads.TryGetValue(c, out var r) && r is { Points: int p, Cost: int k, Paused: false } && p >= k)
            .OrderBy(c => live.Contains(c)).ThenByDescending(c => Reads[c].Points)
            .FirstOrDefault();
}
