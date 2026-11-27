using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PokeDeck.Core;

namespace PokeDeck;

/// <summary>
/// Uma conta: a página do jogo (perfil WebView2 próprio), a recompra, ULTRA/Economia, o vigia de "jogo não abriu", as
/// lives leves (watchers), o baú e os resgates da Twitch/Kick e a guarda do bônus. Tudo roda na thread da interface.
/// </summary>
internal sealed class Account : IDisposable
{
    private const string GameUrl = "https://pokeidle.io/app";
    private static readonly string BridgeScript = Resource("game-bridge.js");

    private readonly MainForm _host;
    private readonly Restock _restock = new();
    private readonly Points _tp = new("Twitch"), _kp = new("Kick");
    private readonly Guard _tg = new(), _kg = new();
    private readonly Dictionary<string, string> _twitchIds = new(); // id do canal → login (os avisos vêm por id)
    private CoreWebView2Environment? _env;
    private CancellationTokenSource? _stop;
    private string? _prefsScriptId;
    private bool _ultraOn, _economyOn, _reduceOn;
    private DateTime _loadedAt = DateTime.Now, _phaseSince = DateTime.Now;
    private int _reloads;
    private AgentPage? _twitchPage, _kickPage;
    private TwitchWatcher? _tw;
    private KickWatcher? _kw;
    private TwitchPubSub? _ps;
    private string? _twitchToken, _twitchDevice, _twitchUser, _kickToken;
    private bool _busyTwitch, _busyKick;
    private DateTime _voteCheckAt, _votedAt;

    public Account(int index, MainForm host)
    {
        Index = index;
        _host = host;
        _restock.Note += t => Log.Write(Index, "recompra: " + t);
    }

    public int Index { get; }
    public WebView2? Game { get; private set; }
    public GameState? State { get; private set; }
    public string Phase { get; private set; } = "off";
    public VoteState? Vote { get; private set; }
    public List<string> TwitchOpen { get; private set; } = new();
    public List<string> KickOpen { get; private set; } = new();
    private AccountConfig Cfg => _host.Config.Accounts[Index];
    private Timing Timing => _host.Config.Timing;
    public string Profile => $"conta{Index + 1}";

    private static string Resource(string name)
    {
        using var s = typeof(Account).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("falta " + name);
        return new StreamReader(s).ReadToEnd();
    }

    // ---------------- jogo ----------------

    public async Task StartAsync(CoreWebView2Environment env)
    {
        if (Game is not null)
            return;
        _env = env;
        _stop = new CancellationTokenSource();
        Phase = "loading";
        var view = new WebView2 { Visible = false, DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 20, 26) };
        Game = view;
        _host.AddGameView(view);
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = Profile;
        await view.EnsureCoreWebView2Async(env, options);
        var core = view.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.IsMuted = Cfg.Muted;
        view.ZoomFactor = Cfg.Zoom;
        core.WebMessageReceived += (_, e) => OnBridge(e.WebMessageAsJson);
        core.NavigationStarting += (_, e) =>
        {
            if (!e.IsRedirected)
                SetPhase("loading");
        };
        view.GotFocus += (_, _) => _host.SetActive(Index);
        core.NavigationCompleted += (_, e) =>
        {
            _loadedAt = DateTime.Now;
            ApplyPerf();
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                SetPhase("failed");
        };
        core.NewWindowRequested += (_, e) =>
        {
            if (!IdleHub.IsIdleHubLink(e.Uri))
                return;
            // O "Votar no IdleHub.io" de dentro do jogo abre na janela de voto do app, que percebe quando o voto entra.
            e.Handled = true;
            _host.OpenVote(Index, e.Uri);
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Write(Index, $"o navegador da conta caiu ({e.ProcessFailedKind}); recarregando");
            Reload();
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
        await ApplyPrefsAsync(firstTime: true);
        _restock.Configure(Cfg.Restock, Timing);
        core.Navigate(GameUrl);

        var stop = _stop.Token;
        _ = Jitter.Every(() => TimeSpan.FromSeconds(10), () => 0.3, () => { Watchdog(); return Task.CompletedTask; }, stop);
        _ = Jitter.Every(() => TimeSpan.FromSeconds(Timing.TwitchPointsSec), () => Timing.JitterPct, TwitchPointsAsync, stop, TimeSpan.FromSeconds(40));
        _ = Jitter.Every(() => TimeSpan.FromSeconds(Timing.KickPointsSec), () => Timing.JitterPct, KickPointsAsync, stop, TimeSpan.FromSeconds(60));
        _ = Jitter.Every(() => TimeSpan.FromSeconds(Timing.GuardSec), () => Timing.JitterPct,
            async () => { await GuardAsync("twitch"); await GuardAsync("kick"); }, stop, TimeSpan.FromSeconds(90));
        _ = Jitter.Every(() => TimeSpan.FromSeconds(20), () => 0.5, VoteCheckAsync, stop, TimeSpan.FromSeconds(30));
        Log.Write(Index, "conta ligada");
    }

    public void Stop()
    {
        _stop?.Cancel();
        StopLives();
        if (Game is not null)
        {
            _host.RemoveGameView(Game);
            Game.Dispose();
            Game = null;
            Log.Write(Index, "conta desligada");
        }
        State = null;
        Vote = null;
        _voteCheckAt = default;
        Active = false;
        _prefsScriptId = null;
        Phase = "off";
    }

    public void Reload()
    {
        _loadedAt = DateTime.Now;
        Game?.CoreWebView2?.Reload();
    }

    private void SetPhase(string phase)
    {
        if (phase == Phase)
            return;
        Phase = phase;
        _phaseSince = DateTime.Now;
        if (phase == "game")
            (_reloads, _voteCheckAt) = (0, default); // entrou no jogo (login, recarga): confere o voto logo
    }

    /// <summary>Recarrega o jogo preso na abertura (ou que não carregou), esperando mais a cada vez: 1, 2 … 5 min.</summary>
    private void Watchdog()
    {
        if (Game is null || Phase is "game" or "login" or "elsewhere")
            return;
        var wait = TimeSpan.FromMinutes(Math.Min(5, _reloads + 1));
        if (DateTime.Now - _loadedAt < wait || DateTime.Now - _phaseSince < wait)
            return;
        _reloads++;
        Log.Write(Index, $"o jogo não abriu ({Phase}); recarregando (tentativa {_reloads})");
        Reload();
    }

    private void OnBridge(string json)
    {
        JsonElement m;
        try
        {
            using var doc = JsonDocument.Parse(json);
            m = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        switch (m.TryGetProperty("t", out var t) ? t.GetString() : null)
        {
            case "state":
                State = GameState.Parse(m);
                SetPhase(m.TryGetProperty("phase", out var p) ? p.GetString() ?? "loading" : "loading");
                foreach (var msg in _restock.Tick(State))
                    Send(msg);
                break;
            case "bought":
                _restock.Bought(m.GetProperty("qtd").GetInt64(), m.GetProperty("gasto").GetInt64());
                break;
            case "fullscreen":
                _host.ToggleFullscreen();
                break;
            case "refused":
                _restock.Refused(m.GetProperty("msg").GetString() ?? "");
                break;
            case "echo":
                _host.Echo(Index, m.GetProperty("msg").GetString() ?? "");
                break;
        }
    }

    public sealed record ExtInfo(string Id, string Name, bool Enabled, string? Page, bool Window);

    /// <summary>As extensões do perfil desta conta (relido depois de instalar, ligar/desligar ou remover).</summary>
    public List<ExtInfo> Extensions { get; set; } = new();

    /// <summary>A conta em uso (a última clicada): desenha fluida; as outras ficam no limite de fundo.</summary>
    public bool Active { get; private set; }

    public void SetActive(bool active)
    {
        Active = active;
        ApplyPerf();
    }

    private void ApplyPerf()
    {
        int fps = Active ? Timing.ActiveFps : Timing.BackgroundFps;
        int sprite = Active ? 220 : Timing.BackgroundSpriteMs;
        _ = Game?.CoreWebView2?.ExecuteScriptAsync($"window.__pdFps={fps};window.__pdSpriteMs={sprite}");
    }

    public void Send(string message) =>
        _ = Game?.CoreWebView2?.ExecuteScriptAsync($"window.__pd && window.__pd.send({JsonSerializer.Serialize(message)})");

    /// <summary>
    /// ULTRA/Economia são escolhas do próprio jogo no localStorage ("cfg-ultra", "cfg-economia"): o script entra antes do
    /// jogo carregar, o jogo lê ao abrir. Mudou com a conta aberta: grava e recarrega (nenhum clique sintético).
    /// </summary>
    public async Task ApplyPrefsAsync(bool firstTime = false)
    {
        if (Game?.CoreWebView2 is not CoreWebView2 core)
            return;
        core.IsMuted = Cfg.Muted;
        Game.ZoomFactor = Cfg.Zoom;
        _restock.Configure(Cfg.Restock, Timing);
        ApplyPerf();
        bool changed = !firstTime && (Cfg.Ultra != _ultraOn || Cfg.Economy != _economyOn);
        if (changed)
        {
            // Só grava o que mudou: desligado no app = devolve ao jogo o "0" uma vez, depois o jogo decide.
            var sets = new List<string>();
            if (Cfg.Ultra != _ultraOn)
                sets.Add($"localStorage.setItem('cfg-ultra','{(Cfg.Ultra ? 1 : 0)}')");
            if (Cfg.Economy != _economyOn)
                sets.Add($"localStorage.setItem('cfg-economia','{(Cfg.Economy ? 1 : 0)}')");
            await core.ExecuteScriptAsync("try{" + string.Join(";", sets) + "}catch(e){}");
        }
        if (firstTime || changed)
        {
            if (_prefsScriptId is not null)
                core.RemoveScriptToExecuteOnDocumentCreated(_prefsScriptId);
            var keep = new List<string>();
            if (Cfg.Ultra)
                keep.Add("localStorage.setItem('cfg-ultra','1')");
            if (Cfg.Economy)
                keep.Add("localStorage.setItem('cfg-economia','1')");
            _prefsScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "if(window.top===window&&/(^|\\.)pokeidle\\.io$/.test(location.hostname)){try{" + string.Join(";", keep) + "}catch(e){}}");
            (_ultraOn, _economyOn) = (Cfg.Ultra, Cfg.Economy);
        }
        if (firstTime || Cfg.ReduceMotion != _reduceOn)
        {
            _reduceOn = Cfg.ReduceMotion;
            var features = _reduceOn ? "[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]" : "[]";
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", $"{{\"features\":{features}}}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }
        if (changed)
        {
            Log.Write(Index, $"ULTRA {(Cfg.Ultra ? "ligado" : "desligado")}, Economia {(Cfg.Economy ? "ligada" : "desligada")}: recarregando o jogo");
            Reload();
        }
    }

    // ---------------- voto no IdleHub ----------------

    private bool JustVoted => DateTime.Now - _votedAt < TimeSpan.FromMinutes(3);

    /// <summary>Pergunta ao jogo (na página da conta) quando o voto libera: a cada ~30 min, ou logo que liberar.</summary>
    private async Task VoteCheckAsync()
    {
        if (Phase != "game" || DateTime.Now < _voteCheckAt || Game?.CoreWebView2 is not CoreWebView2 core)
            return;
        _voteCheckAt = DateTime.Now.AddMinutes(5); // se a pergunta travar ou falhar, tenta de novo daqui a 5 min
        var call = core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression = IdleHub.StateScript, awaitPromise = true, returnByValue = true }));
        if (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))) != call)
            return;
        VoteState? state = null;
        try
        {
            using var doc = JsonDocument.Parse(await call);
            if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
                state = IdleHub.ParseState(v, DateTime.Now);
        }
        catch (Exception ex) when (ex is JsonException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return;
        }
        var now = DateTime.Now;
        if (state is null)
            _voteCheckAt = now.AddMinutes(1); // a página não estava no jogo ou o jogo não respondeu
        else if (state.ReadyAt(now) && JustVoted)
            _voteCheckAt = now.AddSeconds(20); // o jogo ainda não recebeu o voto: segura o "votou"
        else
        {
            Vote = state;
            var next = now + Jitter.Of(TimeSpan.FromMinutes(state.LoggedIn ? 30 : 2), Timing.JitterPct);
            if (state.NextVoteAt is DateTime at && at > now && at.AddSeconds(3) < next)
                next = at + Jitter.Between(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15));
            _voteCheckAt = next;
        }
    }

    /// <summary>A janela de voto viu o voto entrar: o botão vira "votou" e o jogo é consultado logo depois.</summary>
    public void MarkVoted(DateTime? next)
    {
        _votedAt = DateTime.Now;
        if (Vote is VoteState state && next is DateTime at && at > DateTime.Now)
            Vote = state with { NextVoteAt = at };
        _voteCheckAt = DateTime.Now.AddSeconds(3);
    }

    // ---------------- lives ----------------

    private async Task<(string? Token, string? Device, string? Kick)> ReadLoginsAsync()
    {
        if (Game?.CoreWebView2 is not CoreWebView2 core)
            return (null, null, null);
        var tw = await core.CookieManager.GetCookiesAsync("https://www.twitch.tv");
        var kk = await core.CookieManager.GetCookiesAsync("https://kick.com");
        static string? Val(IEnumerable<CoreWebView2Cookie> c, string name) => c.FirstOrDefault(x => x.Name == name)?.Value is { Length: > 0 } v ? v : null;
        var kick = Val(kk, "session_token");
        return (Val(tw, "auth-token"), Val(tw, "unique_id"), kick is null ? null : Uri.UnescapeDataString(kick));
    }

    private static List<string> Pick(IEnumerable<string> channels, IReadOnlyDictionary<string, bool?> live, int slots) =>
        channels.Where(c => live.GetValueOrDefault(c) != false).Take(slots).ToList(); // sem resposta conta como no ar

    /// <summary>Depois de cada conferência de quem está no ar: decide as lives desta conta e liga os watchers.</summary>
    public async Task UpdateLivesAsync(IReadOnlyDictionary<string, bool?> twitchLive, IReadOnlyDictionary<string, bool?> kickLive)
    {
        if (Game is null || !Cfg.On || !Cfg.Lives)
        {
            StopLives();
            return;
        }
        var (token, device, kick) = await ReadLoginsAsync();
        double J() => Timing.JitterPct;

        if (token != _twitchToken)
        {
            _tw?.Dispose();
            _ps?.Dispose();
            (_tw, _ps, _twitchUser) = (null, null, null);
            _twitchToken = token;
            _twitchDevice = device;
            if (token is not null)
            {
                _twitchUser = await TwitchApi.UserIdAsync(token, device);
                if (_twitchUser is null)
                    Log.Write(Index, "Twitch: não consegui ler a conta logada (login expirado?)");
                else
                {
                    _tw = new TwitchWatcher(_twitchUser, token, device, J);
                    _ps = new TwitchPubSub(token, _twitchUser, n => _host.BeginInvoke(() => OnTwitchNotice(n)));
                    Log.Write(Index, "Twitch: logado, assistindo sem player");
                }
            }
        }
        TwitchOpen = _tw is null ? new() : Pick(_host.Config.TwitchChannels, twitchLive, _host.Config.TwitchLives);
        _tw?.SetChannels(TwitchOpen);

        if (kick != _kickToken)
        {
            _kw?.Dispose();
            _kw = null;
            _kickToken = kick;
            if (kick is not null)
            {
                _kw = new KickWatcher(kick, J);
                Log.Write(Index, "Kick: logado, assistindo sem player");
            }
        }
        KickOpen = _kw is null ? new() : Pick(_host.Config.KickChannels, kickLive, _host.Config.KickLives);
        _kw?.SetChannels(KickOpen);

        // As páginas de dentro do site só enquanto há live aberta (a guarda abre a dela quando precisa).
        if (TwitchOpen.Count > 0)
            await EnsurePageAsync("twitch", TwitchOpen[0]);
        if (KickOpen.Count > 0)
            await EnsurePageAsync("kick", KickOpen[0]);
    }

    private async Task<AgentPage?> EnsurePageAsync(string site, string channel)
    {
        if (_env is null || Game is null)
            return null;
        if (site == "twitch")
            return _twitchPage ??= await AgentPage.CreateAsync(_env, Profile, _host.Handle, TwitchApi.PlayerUrl(channel));
        return _kickPage ??= await AgentPage.CreateAsync(_env, Profile, _host.Handle, KickApi.PlayerUrl(channel));
    }

    private void StopLives()
    {
        _tw?.Dispose();
        _kw?.Dispose();
        _ps?.Dispose();
        _twitchPage?.Dispose();
        _kickPage?.Dispose();
        (_tw, _kw, _ps, _twitchPage, _kickPage) = (null, null, null, null, null);
        (_twitchToken, _twitchUser, _kickToken) = (null, null, null);
        TwitchOpen = new();
        KickOpen = new();
    }

    private void OnTwitchNotice(TwitchPubSub.Notice n)
    {
        if (n.Type != "claim-available" || !_host.Config.ClaimChest || _twitchPage is null || _twitchToken is null)
            return;
        // O baú espera um clique: pega depois de uns segundos, como alguém que viu o aviso.
        var channel = n.ChannelId is not null && _twitchIds.TryGetValue(n.ChannelId, out var login) ? login : null;
        _ = Task.Delay(Jitter.Between(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20))).ContinueWith(_ =>
            _host.BeginInvoke(async () =>
            {
                foreach (var ch in channel is null ? TwitchOpen.ToList() : new List<string> { channel })
                    await TwitchRunAsync(ch, kind: null, spend: false, claimId: channel is null ? null : n.ClaimId);
            }));
    }

    private async Task<bool> TwitchRunAsync(string ch, string? kind, bool spend, string? claimId = null, bool claim = true)
    {
        if (_twitchPage is null || _twitchToken is null)
            return false;
        var script = Points.TwitchScript(ch, _twitchToken, _twitchDevice, kind,
            claim: claim && _host.Config.ClaimChest && (claimId is not null || _tp.CanClaim(ch)),
            spend: spend && kind is not null && _tp.CanSpend(ch), refund: _tp.RefundAt(ch), claimId);
        if (await _twitchPage.EvalAsync(script) is not string json)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("channelId", out var id) && id.GetString() is string cid)
                _twitchIds[cid] = ch;
        }
        catch (JsonException)
        {
        }
        var notes = _tp.Apply(ch, json);
        foreach (var (what, text) in notes)
            Log.Redeem(Index, "Twitch", ch, what, text);
        return notes.Any(x => x.Kind == "resgate");
    }

    private async Task<bool> KickRunAsync(string ch, string? kind, bool spend)
    {
        if (_kickPage is null || _kickToken is null)
            return false;
        var script = Points.KickScript(ch, _kickToken, kind, spend && kind is not null && _kp.CanSpend(ch), _kp.RefundAt(ch));
        if (await _kickPage.EvalAsync(script) is not string json)
            return false;
        var notes = _kp.Apply(ch, json);
        foreach (var (what, text) in notes)
            Log.Redeem(Index, "Kick", ch, what, text);
        return notes.Any(x => x.Kind == "resgate");
    }

    private static string SiteName(string site) => site == "twitch" ? "Twitch" : "Kick";

    private static Task Breather() => Task.Delay(Jitter.Between(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)));

    /// <summary>Saldo, baú e resgate automático em cada live aberta da Twitch.</summary>
    private async Task TwitchPointsAsync()
    {
        if (_busyTwitch || TwitchOpen.Count == 0)
            return;
        _busyTwitch = true;
        try
        {
            _tp.RetryWait = TimeSpan.FromMinutes(Timing.RedeemRetryMin);
            foreach (var ch in TwitchOpen.ToList())
            {
                await TwitchRunAsync(ch, Cfg.RedeemTwitch, spend: true);
                await Breather();
            }
        }
        finally
        {
            _busyTwitch = false;
        }
    }

    private async Task KickPointsAsync()
    {
        if (_busyKick || KickOpen.Count == 0)
            return;
        _busyKick = true;
        try
        {
            _kp.RetryWait = TimeSpan.FromMinutes(Timing.RedeemRetryMin);
            foreach (var ch in KickOpen.ToList())
            {
                await KickRunAsync(ch, Cfg.RedeemKick, spend: true);
                await Breather();
            }
        }
        finally
        {
            _busyKick = false;
        }
    }

    /// <summary>
    /// Guarda do bônus: o bônus escolhido do site vence em menos de X min → lê o saldo dos canais fechados (lista + oficiais
    /// do jogo), resgata 1 no melhor e confere no jogo se o tempo subiu; se não subir em 5 min, pausa 1 h.
    /// </summary>
    private async Task GuardAsync(string site)
    {
        bool twitch = site == "twitch";
        string? kind = twitch ? Cfg.RedeemTwitch : Cfg.RedeemKick;
        var bonus = twitch ? State?.Twitch : State?.Kick;
        var guard = twitch ? _tg : _kg;
        if (!Cfg.Guard || !Cfg.Lives || kind is null || bonus is not { Linked: true } || (twitch ? _twitchToken : _kickToken) is null
            || (twitch ? _busyTwitch : _busyKick))
            return;
        var now = DateTime.Now;
        int left = bonus.Left.GetValueOrDefault(kind);
        if (guard.RedeemedAt is DateTime at)
        {
            if (left > guard.LeftBefore + 60)
            {
                Log.Redeem(Index, SiteName(site), "—", "guarda", $"o bônus {kind} subiu no jogo");
                guard.RedeemedAt = null;
            }
            else if (now - at > TimeSpan.FromMinutes(5))
            {
                Log.Redeem(Index, SiteName(site), "—", "guarda", "o bônus não subiu no jogo (site vinculado?); pausa de 1 h");
                guard.RedeemedAt = null;
                guard.HoldUntil = now.AddHours(1);
            }
            return;
        }
        if (now < guard.HoldUntil)
            return;
        if (left > Timing.GuardThresholdMin * 60)
        {
            guard.NoneNoted = false;
            return;
        }
        var open = twitch ? TwitchOpen : KickOpen;
        var candidates = Guard.Candidates(twitch ? _host.Config.TwitchChannels : _host.Config.KickChannels,
            bonus.Channels.Select(c => c.Login), open);
        if (candidates.Count == 0 || await EnsurePageAsync(site, candidates[0]) is null)
            return;
        if (twitch) _busyTwitch = true; else _busyKick = true;
        try
        {
            foreach (var ch in candidates.Where(c => !guard.Reads.TryGetValue(c, out var r) || now - r.At > TimeSpan.FromMinutes(30)))
            {
                var page = twitch ? _twitchPage : _kickPage;
                var script = twitch
                    ? Points.TwitchScript(ch, _twitchToken!, _twitchDevice, kind, claim: false, spend: false, refund: null)
                    : Points.KickScript(ch, _kickToken!, kind, spend: false, refund: null);
                if (page is not null && await page.EvalAsync(script) is string json)
                    guard.Remember(ch, json, DateTime.Now);
                await Breather();
            }
            var live = bonus.Channels.Where(c => c.Live).Select(c => c.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (guard.Pick(candidates, live) is not string pick)
            {
                if (!guard.NoneNoted)
                    Log.Redeem(Index, SiteName(site), "—", "guarda", $"o bônus {kind} vence em {left / 60} min e nenhum canal tem os pontos");
                guard.NoneNoted = true;
                return;
            }
            Log.Redeem(Index, SiteName(site), pick, "guarda", $"o bônus {kind} vence em {left / 60} min; resgatando aqui");
            bool ok = twitch ? await TwitchRunAsync(pick, kind, spend: true, claim: false) : await KickRunAsync(pick, kind, spend: true);
            guard.Reads.Remove(pick);
            if (ok)
                (guard.RedeemedAt, guard.LeftBefore) = (DateTime.Now, left);
        }
        finally
        {
            if (twitch) _busyTwitch = false; else _busyKick = false;
        }
    }

    // ---------------- interface ----------------

    public object Snapshot()
    {
        var s = State;
        var now = DateTime.Now;
        var pots = s?.Shop.Where(i => i.Category == "heal").ToList() ?? new();
        var revs = s?.Shop.Where(i => i.Category == "revive").ToList() ?? new();
        object Lives(IEnumerable<string> open, Func<string, string> watch, Points pts) =>
            open.Select(c => new { channel = c, watch = watch(c), points = pts.Channels.TryGetValue(c, out var x) ? x.Text : "" }).ToList();
        return new
        {
            i = Index,
            phase = Phase,
            active = Active,
            extensions = Extensions,
            nick = s?.Nick,
            gold = s?.Gold,
            vip = s?.Vip ?? false,
            stock = s is null ? null : new
            {
                balls = s.BallsOf(Cfg.Restock.Balls.Id),
                potions = pots.Sum(p => s.Have(p.Id)),
                revives = revs.Sum(r => s.Have(r.Id)),
            },
            restock = new { paused = _restock.Paused, potion = _restock.PotionPick },
            ballCatalog = s?.BallCatalog.Where(b => b.Buyable).Select(b => new { id = b.Id, name = b.Name, price = b.Price }),
            potionList = pots.Select(p => new { id = p.Id, name = p.Name, price = p.Price }),
            // Sem login no jogo ou com o Vote & Ganhe desligado no servidor o voto não daria nada: sem botão.
            vote = Vote is not { LoggedIn: true, Active: true } v ? null : new
            {
                ready = v.ReadyAt(now) && !JustVoted,
                voted = JustVoted,
                left = v.NextVoteAt is DateTime at && at > now ? (int)(at - now).TotalSeconds : 0,
                nick = v.Nick,
                votes = v.Votes,
                inPair = v.InPair,
                perPair = v.PerPair,
                perReward = v.VotesPerReward,
            },
            bonus = new { twitch = s?.Twitch?.Left, kick = s?.Kick?.Left, twitchLinked = s?.Twitch?.Linked, kickLinked = s?.Kick?.Linked },
            lives = new
            {
                twitchLogged = _twitchToken is not null,
                kickLogged = _kickToken is not null,
                twitch = Lives(TwitchOpen, c => _tw?.Status(c) ?? "", _tp),
                kick = Lives(KickOpen, c => _kw?.Status(c) ?? "", _kp),
                twitchStopped = _tp.Stopped,
                kickStopped = _kp.Stopped,
                pubsub = _ps?.Error,
            },
        };
    }

    public void Dispose() => Stop();
}
