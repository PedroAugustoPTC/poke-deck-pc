using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PokeDeck;

/// <summary>
/// Uma página escondida de um site (o player da Twitch ou da Kick, parado e mudo) no perfil da conta. Só existe para
/// rodar os pedidos de pontos DE DENTRO do site real: o fetch sai do Edge com os cookies, o Cloudflare e a checagem
/// anti-robô do próprio site. Uma por site por conta (os scripts recebem o canal como parâmetro).
/// </summary>
internal sealed class AgentPage : IDisposable
{
    private readonly CoreWebView2Controller _controller;
    private DateTime _readyAt = DateTime.MaxValue;

    private AgentPage(CoreWebView2Controller controller) => _controller = controller;

    public string? Url { get; private set; }

    public static async Task<AgentPage> CreateAsync(CoreWebView2Environment env, string profile, IntPtr host, string url)
    {
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = profile;
        var controller = await env.CreateCoreWebView2ControllerAsync(host, options);
        controller.IsVisible = false;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 480, 270);
        var core = controller.CoreWebView2;
        core.IsMuted = true;
        core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        core.Settings.AreDevToolsEnabled = false;
        core.NewWindowRequested += (_, e) => e.Handled = true; // nada de popups escondidos
        var page = new AgentPage(controller);
        // O player carrega os scripts do site (inclusive o anti-robô) depois do load: dá um tempo antes do 1º pedido.
        core.NavigationCompleted += (_, e) => page._readyAt = e.IsSuccess ? DateTime.Now.AddSeconds(5) : DateTime.MaxValue;
        page.Navigate(url);
        return page;
    }

    public void Navigate(string url)
    {
        Url = url;
        _readyAt = DateTime.MaxValue;
        _controller.CoreWebView2.Navigate(url);
    }

    /// <summary>Roda o script (que devolve uma Promise de texto) e devolve o texto; null se a página não está pronta.</summary>
    public async Task<string?> EvalAsync(string script, TimeSpan? wait = null)
    {
        var until = DateTime.Now + (wait ?? TimeSpan.FromSeconds(30));
        while (DateTime.Now < _readyAt)
        {
            if (DateTime.Now > until)
                return null;
            await Task.Delay(500);
        }
        try
        {
            var raw = await _controller.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true }));
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // a página recarregou no meio; tenta na próxima volta
        }
    }

    public void Dispose() => _controller.Close();
}
