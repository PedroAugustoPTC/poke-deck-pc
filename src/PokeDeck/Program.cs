using Microsoft.Web.WebView2.Core;

namespace PokeDeck;

internal static class Program
{
    /// <summary>O ícone do app (o mesmo do exe) para a janela, a barra de tarefas e as janelas auxiliares.</summary>
    public static readonly Icon AppIcon = new(typeof(Program).Assembly.GetManifestResourceStream("icon.ico")!);

    /// <summary>O exe antigo que a atualização deixa de lado (renomeado: o Windows não apaga um exe em uso).</summary>
    public static string OldExe => Path.ChangeExtension(Environment.ProcessPath!, ".old.exe");

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Duas instâncias no mesmo perfil disputam os logins e o config.json: só uma por vez.
        using var single = new Mutex(true, "PokeDeck.SingleInstance", out bool first);
        // Aberto pela atualização: espera o exe antigo terminar de fechar (ele sai sem soltar o mutex: "abandonado").
        if (!first && args.Contains("--updated"))
        {
            try
            {
                first = single.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                first = true;
            }
            Thread.Sleep(2000); // shortcut: dá tempo ao navegador do exe antigo de fechar; se o WebView2 reclamar, aumentar
        }
        if (!first)
        {
            MessageBox.Show("O PokeDeck já está aberto.\nSe a janela sumiu, ele está na bandeja (perto do relógio): clique no ícone para abrir.", "PokeDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _ = DeleteOldExeAsync();
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("Falta o Microsoft Edge WebView2 Runtime.\nBaixe em https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "PokeDeck", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new MainForm());
    }

    /// <summary>
    /// O exe antigo solta o mutex antes de o processo terminar de sair, e até lá o Windows não deixa apagar o arquivo:
    /// tenta de novo por até 2 minutos.
    /// </summary>
    private static async Task DeleteOldExeAsync()
    {
        for (int i = 0; i < 120 && File.Exists(OldExe); i++)
        {
            try
            {
                File.Delete(OldExe);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(1000);
            }
        }
    }
}
