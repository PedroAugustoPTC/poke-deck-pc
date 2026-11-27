using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PokeDeck.Core;

namespace PokeDeck;

/// <summary>
/// A janela: a interface (Svelte) ocupa tudo e diz onde fica cada conta (os retângulos das vagas); as páginas do jogo são
/// WebView2 nativos postos por cima dessas vagas. Também confere quem está ao vivo e repassa às contas.
/// </summary>
internal sealed class MainForm : Form
{
    // Mantém o jogo e os timers rodando com a janela escondida/minimizada (o jogo é idle: precisa seguir em segundo plano).
    private const string BrowserArgs =
        "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --process-per-site";

    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokeDeck");
    private readonly CancellationTokenSource _stop = new();
    private readonly Account[] _accounts;
    private CoreWebView2Environment? _env;
    private WebView2? _ui;
    private Dictionary<string, bool?> _twitchLive = new(), _kickLive = new();
    private bool _checking;
    private readonly Form?[] _voteWindows = new Form?[Config.MaxAccounts];
    private readonly LivesWindow?[] _livesWindows = new LivesWindow?[Config.MaxAccounts];
    private Release? _update;
    private bool _updating;
    private int _updatePct;
    private string? _updateError;

    public MainForm()
    {
        Icon = Program.AppIcon;
        Text = $"PokeDeck {typeof(MainForm).Assembly.GetName().Version?.ToString(3)}";
        BackColor = System.Drawing.Color.FromArgb(18, 20, 26);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new System.Drawing.Size(1500, 950);
        Directory.CreateDirectory(_root);
        Log.FilePath = Path.Combine(_root, "pokedeck.log");
        Log.RedeemPath = Path.Combine(_root, "resgates.log");
        Log.LoadRedeems();
        Log.Write(null, "abrindo o PokeDeck");
        Config = Config.Load(ConfigPath);
        foreach (var a in Config.Accounts)
            a.On = false; // as contas só ligam quando o usuário pede (uma a uma ou "Ligar todas")
        _accounts = Enumerable.Range(0, Config.MaxAccounts).Select(i => new Account(i, this)).ToArray();
    }

    public Config Config { get; private set; }
    private string ConfigPath => Path.Combine(_root, "config.json");

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await InitAsync();
        }
        catch (Exception ex)
        {
            Log.Write(null, "falha ao abrir: " + ex);
            MessageBox.Show(this, ex.Message, "PokeDeck", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task InitAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_root, "WebView2"), new CoreWebView2EnvironmentOptions(BrowserArgs) { AreBrowserExtensionsEnabled = true });
        _ui = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_ui);
        var options = _env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = "ui";
        await _ui.EnsureCoreWebView2Async(_env, options);
        var core = _ui.CoreWebView2;
        TwitchApi.UserAgent = core.Settings.UserAgent; // os pedidos de fora da página dizem ser este mesmo Edge
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        ServeUi(core);
        core.WebMessageReceived += (_, e) => OnUi(e.WebMessageAsJson);
        core.NavigationCompleted += (_, e) => Log.Write(null, $"interface pronta em {clock.Elapsed.TotalSeconds:0.0} s{(e.IsSuccess ? "" : $" (erro {e.WebErrorStatus})")}");
        core.Navigate("https://app.local/index.html");
        Log.Write(null, $"navegador pronto em {clock.Elapsed.TotalSeconds:0.0} s");
        _ = RefreshAllExtensionsAsync();

        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => PushStatus();
        timer.Start();
        _ = Jitter.Every(() => TimeSpan.FromSeconds(Config.Timing.LiveCheckSec), () => Config.Timing.JitterPct, CheckLivesAsync, _stop.Token,
            TimeSpan.FromSeconds(15));
        _ = CheckUpdateAsync();
    }

    // ---------------- atualização ----------------

    private static HttpClient GitHub(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PokeDeck-updater"); // o GitHub exige um User-Agent
        return http;
    }

    /// <summary>Ao abrir: a release mais nova do GitHub é mais nova que este exe? Então mostra o botão de atualizar.</summary>
    private async Task CheckUpdateAsync()
    {
        var current = typeof(MainForm).Assembly.GetName().Version;
        try
        {
            await Task.Delay(Jitter.Between(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)), _stop.Token);
            using var http = GitHub(TimeSpan.FromSeconds(30));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            if (Updater.Parse(await http.GetStringAsync(Updater.LatestUrl, _stop.Token)) is Release r && Updater.IsNewer(current, r))
            {
                _update = r;
                Log.Write(null, $"nova versão disponível: {r.Tag}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Write(null, "não consegui conferir se há versão nova: " + ex.Message);
        }
    }

    /// <summary>
    /// Baixa o exe da release, confere o SHA-256, troca o exe atual (renomeado para .old.exe, que o próximo início
    /// apaga) e abre o novo, que espera este fechar.
    /// </summary>
    private async Task UpdateNowAsync()
    {
        if (_update is not Release r || _updating)
            return;
        (_updating, _updatePct, _updateError) = (true, 0, null);
        var exe = Environment.ProcessPath!;
        var fresh = exe + ".new";
        try
        {
            using (var http = GitHub(TimeSpan.FromMinutes(15)))
            using (var response = await http.GetAsync(r.ExeUrl, HttpCompletionOption.ResponseHeadersRead, _stop.Token))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0, done = 0;
                await using var source = await response.Content.ReadAsStreamAsync(_stop.Token);
                await using var file = File.Create(fresh);
                var buffer = new byte[1 << 16];
                for (int n; (n = await source.ReadAsync(buffer, _stop.Token)) > 0;)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
                    done += n;
                    _updatePct = total > 0 ? (int)(done * 100 / total) : 0;
                }
            }
            string hash;
            await using (var file = File.OpenRead(fresh))
                hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, _stop.Token));
            if (!hash.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("o arquivo baixado não confere com o da release (SHA-256 diferente)");
            File.Delete(Program.OldExe);
            File.Move(exe, Program.OldExe);
            try
            {
                File.Move(fresh, exe);
            }
            catch
            {
                File.Move(Program.OldExe, exe); // volta o exe atual: nada fica pela metade
                throw;
            }
            Log.Write(null, $"atualizado para {r.Tag}; abrindo a versão nova");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--updated") { UseShellExecute = false });
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException
                                       or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            try
            {
                File.Delete(fresh);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
            _updateError = ex is UnauthorizedAccessException
                ? "sem permissão para trocar o exe nesta pasta; baixe pela página da release"
                : ex.Message;
            Log.Write(null, "atualização falhou: " + ex.Message);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>A interface sai dos recursos embutidos no exe, em https://app.local/.</summary>
    private static void ServeUi(CoreWebView2 core)
    {
        var asm = typeof(MainForm).Assembly;
        var names = asm.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n);
        core.AddWebResourceRequestedFilter("https://app.local/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
            if (!names.TryGetValue("ui/" + (path.Length == 0 ? "index.html" : path), out var name) || asm.GetManifestResourceStream(name) is not Stream s)
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            var type = Path.GetExtension(path) switch
            {
                ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png",
                ".ico" => "image/x-icon", ".woff2" => "font/woff2", _ => "text/html",
            };
            e.Response = core.Environment.CreateWebResourceResponse(s, 200, "OK", $"Content-Type: {type}; charset=utf-8");
        };
    }

    public void AddGameView(WebView2 view)
    {
        Controls.Add(view);
        view.BringToFront();
    }

    public void RemoveGameView(WebView2 view) => Controls.Remove(view);

    private (FormWindowState State, System.Drawing.Rectangle Bounds)? _windowed;

    /// <summary>Tela cheia (botão da barra ou F11, também de dentro do jogo): sem borda, cobrindo o monitor inteiro.</summary>
    public void ToggleFullscreen()
    {
        if (_windowed is { } w)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            Bounds = w.Bounds;
            WindowState = w.State;
            _windowed = null;
        }
        else
        {
            _windowed = (WindowState, WindowState == FormWindowState.Normal ? Bounds : RestoreBounds);
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
        }
        Post(new { t = "fullscreen", on = _windowed is not null });
    }

    private void Post(object message)
    {
        if (_ui?.CoreWebView2 is CoreWebView2 core)
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void PushStatus() => Post(new
    {
        t = "status",
        accounts = _accounts.Select(a => a.Snapshot()),
        log = Log.Lines().Take(150),
        redeems = Log.RedeemLines(),
        twitchLive = _twitchLive,
        kickLive = _kickLive,
        update = _update is null ? null : new
        {
            version = _update.Version.ToString(3),
            notes = _update.Notes.Length > 1500 ? _update.Notes[..1500] + "…" : _update.Notes,
            busy = _updating,
            pct = _updatePct,
            error = _updateError,
        },
    });

    private async void OnUi(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            switch (m.GetProperty("t").GetString())
            {
                case "ready":
                    Post(new { t = "config", config = Config });
                    break;
                case "layout":
                    PlaceViews(m);
                    break;
                case "config":
                    await SetConfigAsync(m.GetProperty("config").Deserialize<Config>()!);
                    break;
                case "reload":
                    _accounts[m.GetProperty("i").GetInt32()].Reload();
                    break;
                case "login":
                    OpenLogin(m.GetProperty("i").GetInt32(), m.GetProperty("site").GetString() ?? "twitch");
                    break;
                case "fullscreen":
                    ToggleFullscreen();
                    break;
                case "lives":
                    OpenLives(m.GetProperty("i").GetInt32());
                    break;
                case "vote":
                    OpenVote(m.GetProperty("i").GetInt32());
                    break;
                case "updateNow":
                    await UpdateNowAsync();
                    break;
                case "updatePage":
                    if (_update is not null) // a página vem da release, já conferida como do próprio repositório
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_update.Page) { UseShellExecute = true });
                    break;
                case "checkLives":
                    await CheckLivesAsync();
                    break;
                case "extInstall":
                    await InstallExtensionAsync(m.GetProperty("link").GetString() ?? "",
                        m.GetProperty("accounts").EnumerateArray().Select(x => x.GetInt32()).Where(i => i >= 0 && i < _accounts.Length).ToList());
                    break;
                case "extToggle":
                case "extRemove":
                    await ChangeExtensionAsync(m.GetProperty("i").GetInt32(), m.GetProperty("id").GetString() ?? "",
                        m.GetProperty("t").GetString() == "extRemove" ? null : m.GetProperty("on").GetBoolean());
                    break;
                case "extPopup":
                    OpenExtension(m.GetProperty("i").GetInt32(), m.GetProperty("id").GetString() ?? "");
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            Log.Write(null, "mensagem da interface inválida: " + ex.Message);
        }
    }

    /// <summary>A interface mandou onde fica cada vaga (em px CSS); conta sem vaga fica escondida (rodando).</summary>
    private void PlaceViews(JsonElement m)
    {
        double dpr = m.GetProperty("dpr").GetDouble();
        var seen = new HashSet<int>();
        foreach (var slot in m.GetProperty("slots").EnumerateArray())
        {
            int i = slot.GetProperty("i").GetInt32();
            if (i < 0 || i >= _accounts.Length || _accounts[i].Game is not WebView2 view)
                continue;
            seen.Add(i);
            int P(string k) => (int)Math.Round(slot.GetProperty(k).GetDouble() * dpr);
            view.Bounds = new System.Drawing.Rectangle(P("x"), P("y"), Math.Max(1, P("w")), Math.Max(1, P("h")));
            view.Visible = true;
            view.BringToFront();
        }
        foreach (var a in _accounts.Where(a => !seen.Contains(a.Index) && a.Game is not null))
            a.Game!.Visible = false;
    }

    /// <summary>Uma conta mudou a própria config (o bloqueio desligou um resgate): salva e mostra na interface.</summary>
    public void SaveConfig()
    {
        Config.Save(ConfigPath);
        Post(new { t = "config", config = Config });
    }

    private async Task SetConfigAsync(Config next)
    {
        var old = Config;
        Config = next.Fix();
        Config.Save(ConfigPath);
        bool started = false;
        foreach (var a in _accounts)
        {
            bool on = Config.Accounts[a.Index].On;
            if (on && a.Game is null && _env is not null)
            {
                await a.StartAsync(_env);
                started = true;
            }
            else if (!on && a.Game is not null)
                a.Stop();
            else
                await a.ApplyPrefsAsync();
        }
        Post(new { t = "config", config = Config });
        // conta recém-ligada abre as lives já, sem esperar a próxima conferência (~2 min)
        bool livesChanged = started || !old.TwitchChannels.SequenceEqual(Config.TwitchChannels) || !old.KickChannels.SequenceEqual(Config.KickChannels)
                            || old.TwitchLives != Config.TwitchLives || old.KickLives != Config.KickLives
                            || old.Accounts.Select(a => a.Lives).SequenceEqual(Config.Accounts.Select(a => a.Lives)) == false;
        if (livesChanged)
            await CheckLivesAsync();
    }

    /// <summary>Quem está ao vivo (Twitch numa consulta só; Kick canal a canal, com pausas) e repassa às contas.</summary>
    private async Task CheckLivesAsync()
    {
        if (_checking || !Config.Accounts.Any(a => a.On && a.Lives))
            return;
        _checking = true;
        try
        {
            _twitchLive = await TwitchApi.LiveAsync(Config.TwitchChannels);
            var kick = new Dictionary<string, bool?>();
            foreach (var c in Config.KickChannels)
            {
                kick[c] = await KickApi.IsLiveAsync(c);
                await Task.Delay(Jitter.Between(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1500)));
            }
            _kickLive = kick;
            foreach (var a in _accounts)
            {
                await a.UpdateLivesAsync(_twitchLive, _kickLive);
                _livesWindows[a.Index]?.SetLives(LivesOf(a));
            }
        }
        finally
        {
            _checking = false;
        }
    }

    // ---------------- janela das lives ----------------

    private static List<(string, string)> LivesOf(Account a) =>
        a.TwitchOpen.Select(c => ("twitch", c)).Concat(a.KickOpen.Select(c => ("kick", c))).ToList();

    /// <summary>As lives que a conta assiste agora (chat lado a lado; "Tela cheia" mostra a live). Uma janela por conta.</summary>
    private void OpenLives(int index)
    {
        if (_env is null || index < 0 || index >= _accounts.Length)
            return;
        if (_livesWindows[index] is { IsDisposed: false } open)
        {
            if (open.WindowState == FormWindowState.Minimized)
                open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }
        var form = new LivesWindow(_env, _accounts[index].Profile, Config.Accounts[index].Name);
        form.FormClosed += (_, _) => _livesWindows[index] = null;
        _livesWindows[index] = form;
        form.SetLives(LivesOf(_accounts[index]));
        form.Show(this);
        form.FitColumns();
    }

    // ---------------- voto no IdleHub ----------------

    /// <summary>
    /// A página de voto do IdleHub no perfil da conta (onde fica o login do IdleHub dela), já com o nick. Quem vota é
    /// você; o app lê a resposta do voto e atualiza o botão.
    /// </summary>
    public async void OpenVote(int index, string? url = null)
    {
        var account = _accounts[index];
        if (_env is null || account.Game is null)
            return;
        url ??= account.Vote?.Url ?? IdleHub.VoteUrl(account.State?.Nick);
        if (_voteWindows[index] is { IsDisposed: false } open)
        {
            if (open.Controls[0] is WebView2 { CoreWebView2: CoreWebView2 c })
                c.Navigate(url);
            if (open.WindowState == FormWindowState.Minimized)
                open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }
        var name = Config.Accounts[index].Name;
        var form = new Form { Text = $"Votar com {name} · quem vota é você, nesta página do IdleHub.io", Size = new System.Drawing.Size(1100, 820), StartPosition = FormStartPosition.CenterParent, Icon = Program.AppIcon };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.FormClosed += (_, _) => _voteWindows[index] = null;
        _voteWindows[index] = form;
        form.Show(this);
        var options = _env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = account.Profile;
        await view.EnsureCoreWebView2Async(_env, options);
        view.CoreWebView2.WebResourceResponseReceived += async (_, e) =>
        {
            if (e.Response is null || !IdleHub.IsVoteResponse(e.Request.Method, e.Request.Uri, e.Response.StatusCode))
                return;
            string body;
            try
            {
                using var stream = await e.Response.GetContentAsync();
                if (stream is null)
                    return;
                body = await new StreamReader(stream).ReadToEndAsync();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or InvalidOperationException)
            {
                return;
            }
            if (IdleHub.TryParseVoted(body, out var next) && !form.IsDisposed)
            {
                form.Text = $"✓ Voto registrado com {name}! Pode fechar esta janela";
                account.MarkVoted(next);
                Log.Write(index, "voto registrado no IdleHub.io");
            }
        };
        view.CoreWebView2.Navigate(url);
    }

    // ---------------- extensões (uma lista por conta: cada conta tem o seu perfil) ----------------

    private string ExtensionsRoot => Path.Combine(_root, "extensoes");

    /// <summary>Roda <paramref name="work"/> no perfil da conta; com a conta desligada, abre o perfil só para isso.</summary>
    private async Task WithProfileAsync(int index, Func<CoreWebView2Profile, Task> work)
    {
        if (_accounts[index].Game?.CoreWebView2?.Profile is CoreWebView2Profile running)
        {
            await work(running);
            return;
        }
        var options = _env!.CreateCoreWebView2ControllerOptions();
        options.ProfileName = _accounts[index].Profile;
        var controller = await _env.CreateCoreWebView2ControllerAsync(Handle, options);
        try
        {
            controller.IsVisible = false;
            await work(controller.CoreWebView2.Profile);
        }
        finally
        {
            controller.Close();
        }
    }

    private async Task RefreshExtensionsAsync(int index) => await WithProfileAsync(index, async profile =>
    {
        var list = new List<Account.ExtInfo>();
        foreach (var e in await profile.GetBrowserExtensionsAsync())
        {
            var folder = Crx.FolderOf(ExtensionsRoot, e.Id);
            var (page, window) = folder is null ? (null, false) : Crx.Popup(folder);
            list.Add(new Account.ExtInfo(e.Id, e.Name, e.IsEnabled, page, window));
        }
        _accounts[index].Extensions = list;
    });

    public async Task RefreshAllExtensionsAsync()
    {
        foreach (var a in _accounts)
            await RefreshExtensionsAsync(a.Index);
    }

    private async Task InstallExtensionAsync(string link, List<int> accounts)
    {
        if (Crx.IdFromLink(link) is not string id || accounts.Count == 0 || _env is null)
        {
            Post(new { t = "extResult", ok = false, msg = "Cole o link da Chrome Web Store e marque ao menos uma conta." });
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(TwitchApi.UserAgent);
            var crx = await http.GetByteArrayAsync(Crx.DownloadUrl(id));
            Directory.CreateDirectory(ExtensionsRoot);
            var folder = Crx.Unpack(crx, id, ExtensionsRoot);
            foreach (var i in accounts)
            {
                await WithProfileAsync(i, async p => await p.AddBrowserExtensionAsync(folder));
                await RefreshExtensionsAsync(i);
                _accounts[i].Reload(); // a extensão só entra na página depois de recarregar
                Log.Write(i, $"extensão instalada: {_accounts[i].Extensions.FirstOrDefault(e => e.Id == id)?.Name ?? id}");
            }
            Post(new { t = "extResult", ok = true, msg = $"Instalada em {accounts.Count} conta(s)." });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Runtime.InteropServices.COMException
                                       or ArgumentException or TaskCanceledException or UnauthorizedAccessException)
        {
            Log.Write(null, "extensão: " + ex.Message);
            Post(new { t = "extResult", ok = false, msg = "Não instalou: " + ex.Message });
        }
    }

    /// <summary>Liga/desliga (<paramref name="on"/>) ou remove (null) uma extensão de uma conta.</summary>
    private async Task ChangeExtensionAsync(int index, string id, bool? on)
    {
        await WithProfileAsync(index, async p =>
        {
            var ext = (await p.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Id == id);
            if (ext is null)
                return;
            if (on is bool enable)
                await ext.EnableAsync(enable);
            else
                await ext.RemoveAsync();
            Log.Write(index, $"extensão {ext.Name} {(on is null ? "removida" : on.Value ? "ligada" : "desligada")}");
        });
        await RefreshExtensionsAsync(index);
        _accounts[index].Reload();
    }

    private async void OpenExtension(int index, string id)
    {
        if (_env is null || _accounts[index].Extensions.FirstOrDefault(e => e.Id == id) is not { Page: string page } ext)
            return;
        var form = new Form
        {
            Text = $"{ext.Name} — {Config.Accounts[index].Name}",
            Size = ext.Window ? new System.Drawing.Size(1160, 760) : new System.Drawing.Size(440, 620),
            StartPosition = FormStartPosition.CenterParent,
            Icon = Program.AppIcon,
        };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Show(this);
        var options = _env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = _accounts[index].Profile;
        await view.EnsureCoreWebView2Async(_env, options);
        view.CoreWebView2.Navigate($"chrome-extension://{id}/{page}");
    }

    /// <summary>Uma janela no perfil da conta para entrar na Twitch ou na Kick (os cookies ficam no perfil).</summary>
    private async void OpenLogin(int index, string site)
    {
        if (_env is null)
            return;
        var form = new Form { Text = $"Entrar na {(site == "kick" ? "Kick" : "Twitch")} — {Config.Accounts[index].Name}", Size = new System.Drawing.Size(1000, 800), StartPosition = FormStartPosition.CenterParent, Icon = Program.AppIcon };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.FormClosed += async (_, _) => await CheckLivesAsync();
        form.Show(this);
        var options = _env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = _accounts[index].Profile;
        await view.EnsureCoreWebView2Async(_env, options);
        view.CoreWebView2.Navigate(site == "kick" ? "https://kick.com/" : "https://www.twitch.tv/login");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _stop.Cancel();
        foreach (var a in _accounts)
            a.Dispose();
        base.OnFormClosing(e);
    }
}
