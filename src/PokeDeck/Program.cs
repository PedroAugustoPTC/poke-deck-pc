using Microsoft.Web.WebView2.Core;

namespace PokeDeck;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Duas instâncias no mesmo perfil disputam os logins e o config.json: só uma por vez.
        using var single = new Mutex(true, "PokeDeck.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("O PokeDeck já está aberto.", "PokeDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
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
}
