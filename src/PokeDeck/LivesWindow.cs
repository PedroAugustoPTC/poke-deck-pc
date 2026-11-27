using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PokeDeck;

/// <summary>
/// As lives que uma conta está assistindo, para ver: no modo chat, o chat de cada live lado a lado (o chat em janela,
/// sem vídeo); em "Tela cheia", a página do canal de uma live por vez, com uma aba para cada. Só existe aberta: fechar
/// solta todos os navegadores. Quem rende os pontos são os watchers da conta, não esta janela.
/// </summary>
internal sealed class LivesWindow : Form
{
    // As cores de ui/src/app.css (Madeira & Noite).
    private static readonly Color Bg = Color.FromArgb(0x14, 0x0a, 0x0f), PanelBg = Color.FromArgb(0x1e, 0x12, 0x19),
        Well = Color.FromArgb(0x33, 0x20, 0x2b), Line = Color.FromArgb(0x5c, 0x39, 0x48), Ink = Color.FromArgb(0xff, 0xf6, 0xef),
        Muted = Color.FromArgb(0xc9, 0xa9, 0xb4), Accent = Color.FromArgb(0xe2, 0xa9, 0x3f),
        TwitchColor = Color.FromArgb(0xbf, 0x94, 0xff), KickColor = Color.FromArgb(0x53, 0xfc, 0x18);

    private const int ChatWidth = 340;

    private readonly CoreWebView2Environment _env;
    private readonly string _profile;
    private readonly FlowLayoutPanel _tabs = new() { Dock = DockStyle.Fill, BackColor = PanelBg, Padding = new Padding(6, 5, 0, 0), WrapContents = false };
    private readonly Button _full = Flat("Tela cheia", Ink);
    private readonly Panel _stack = new() { Dock = DockStyle.Fill, BackColor = Bg };
    private readonly Label _empty = new() { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleCenter, Text = "Nenhuma live aberta agora." };
    private List<(string Site, string Channel)> _lives = new();
    private int _selected;
    private bool _fullMode, _built;

    public LivesWindow(CoreWebView2Environment env, string profile, string accountName)
    {
        _env = env;
        _profile = profile;
        Text = $"Lives — {accountName}";
        Icon = Program.AppIcon;
        BackColor = Bg;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        var bar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = PanelBg, Padding = new Padding(0, 5, 6, 5) };
        _full.Dock = DockStyle.Right;
        _full.Click += (_, _) => SetMode(!_fullMode);
        bar.Controls.Add(_tabs);
        bar.Controls.Add(_full);
        _stack.Resize += (_, _) => Lay();
        Controls.Add(_stack);
        Controls.Add(bar);
    }

    /// <summary>Troca as lives mostradas (as que a conta assiste agora); a mesma lista não recarrega nada.</summary>
    public void SetLives(List<(string Site, string Channel)> lives)
    {
        if (_built && lives.SequenceEqual(_lives))
            return;
        _built = true;
        _lives = lives;
        _selected = Math.Clamp(_selected, 0, Math.Max(0, lives.Count - 1));
        Rebuild();
    }

    private static string ChatUrl(string site, string c) => site == "kick"
        ? $"https://kick.com/popout/{Uri.EscapeDataString(c)}/chat"
        : $"https://www.twitch.tv/popout/{Uri.EscapeDataString(c)}/chat?popout=";

    private static string PageUrl(string site, string c) =>
        (site == "kick" ? "https://kick.com/" : "https://www.twitch.tv/") + Uri.EscapeDataString(c);

    private void SetMode(bool full)
    {
        _fullMode = full;
        WindowState = full ? FormWindowState.Maximized : FormWindowState.Normal;
        Rebuild();
        if (!full)
            FitColumns();
    }

    /// <summary>Monta as abas e os navegadores do modo atual (os de antes fecham).</summary>
    private void Rebuild()
    {
        foreach (Control old in _stack.Controls.Cast<Control>().ToList())
        {
            _stack.Controls.Remove(old);
            if (old != _empty)
                old.Dispose();
        }
        _tabs.Controls.Clear();
        _full.Text = _fullMode ? "Voltar ao chat" : "Tela cheia";
        _full.Visible = _lives.Count > 0;
        for (int i = 0; i < _lives.Count; i++)
        {
            var (site, channel) = _lives[i];
            var tab = Flat($"{(site == "kick" ? "Kick" : "Twitch")} · {channel}", site == "kick" ? KickColor : TwitchColor);
            tab.Height = 26;
            if (_fullMode && i == _selected)
            {
                tab.BackColor = Well;
                tab.FlatAppearance.BorderColor = Accent;
            }
            int index = i;
            // No chat, a aba abre essa live em tela cheia; na tela cheia, troca de live.
            tab.Click += (_, _) =>
            {
                _selected = index;
                if (_fullMode)
                    Rebuild();
                else
                    SetMode(true);
            };
            _tabs.Controls.Add(tab);
        }
        if (_lives.Count == 0)
            _stack.Controls.Add(_empty);
        else if (_fullMode)
            AddView(PageUrl(_lives[_selected].Site, _lives[_selected].Channel), muted: false);
        else
            foreach (var (site, channel) in _lives)
                AddView(ChatUrl(site, channel), muted: true);
        Lay();
    }

    private void AddView(string url, bool muted)
    {
        var view = new WebView2 { DefaultBackgroundColor = Bg };
        _stack.Controls.Add(view);
        _ = StartAsync(view, url, muted);
    }

    private async Task StartAsync(WebView2 view, string url, bool muted)
    {
        try
        {
            var options = _env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = _profile; // o perfil da conta: o chat já entra logado
            await view.EnsureCoreWebView2Async(_env, options);
            if (view.IsDisposed)
                return;
            var core = view.CoreWebView2;
            core.IsMuted = muted;
            core.Settings.IsStatusBarEnabled = false;
            // Link que abriria outra janela (entrar, perfil…) abre no mesmo lugar.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            };
            core.Navigate(url);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // a janela fechou (ou trocou de modo) enquanto o navegador abria
        }
    }

    /// <summary>As colunas dividem a largura por igual (na tela cheia é uma só).</summary>
    private void Lay()
    {
        var all = _stack.Controls;
        int w = _stack.ClientSize.Width, h = _stack.ClientSize.Height;
        for (int i = 0; i < all.Count; i++)
            all[i].Bounds = new Rectangle(i * w / all.Count, 0, (i + 1) * w / all.Count - i * w / all.Count, h);
    }

    /// <summary>No chat, a janela fica da largura das colunas (a tela limita).</summary>
    public void FitColumns()
    {
        var area = Screen.FromControl(this).WorkingArea;
        int width = Math.Min(area.Width, Math.Max(2, _lives.Count) * LogicalToDeviceUnits(ChatWidth) + Width - ClientSize.Width);
        int height = Math.Min(area.Height, LogicalToDeviceUnits(760));
        Bounds = new Rectangle(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    private static Button Flat(string text, Color color)
    {
        var b = new Button
        {
            Text = text, ForeColor = color, BackColor = PanelBg, FlatStyle = FlatStyle.Flat, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 0, 6, 0), Margin = new Padding(0, 0, 6, 0),
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = Line;
        b.FlatAppearance.MouseOverBackColor = Well;
        return b;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Barra de título escura, como a interface.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
