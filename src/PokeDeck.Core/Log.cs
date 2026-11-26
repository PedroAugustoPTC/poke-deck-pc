namespace PokeDeck.Core;

/// <summary>
/// Os registros: o geral (pokedeck.log) e o dos resgates (resgates.log: cada resgate, baú, recusa, devolução e ação da
/// guarda, com conta, site e canal), que sobrevive entre aberturas do app. Os dois guardam as últimas linhas para a interface.
/// </summary>
public static class Log
{
    public sealed record RedeemEntry(string At, int Account, string Site, string Channel, string Kind, string Text);

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static readonly Queue<RedeemEntry> Redeems = new();
    private const int Keep = 300;

    public static string? FilePath { get; set; }
    public static string? RedeemPath { get; set; }

    public static void Write(int? account, string text)
    {
        var line = $"{DateTime.Now:dd/MM HH:mm:ss} {(account is int a ? $"[conta {a + 1}] " : "")}{text}";
        lock (Gate)
        {
            Push(Recent, line);
            Append(FilePath, line);
        }
    }

    /// <summary>Um evento de pontos/resgate (também vai para o registro geral).</summary>
    public static void Redeem(int account, string site, string channel, string kind, string text)
    {
        var entry = new RedeemEntry($"{DateTime.Now:dd/MM HH:mm:ss}", account, site, channel, kind, text);
        lock (Gate)
        {
            Push(Redeems, entry);
            Append(RedeemPath, string.Join('\t', entry.At, account, site, channel, kind, text.Replace('\t', ' ')));
        }
        Write(account, $"{site} {channel}: {text}");
    }

    /// <summary>Relê o fim do resgates.log ao abrir o app.</summary>
    public static void LoadRedeems()
    {
        if (RedeemPath is null || !File.Exists(RedeemPath))
            return;
        try
        {
            lock (Gate)
                foreach (var line in File.ReadLines(RedeemPath).TakeLast(Keep))
                    if (Parse(line) is RedeemEntry e)
                        Push(Redeems, e);
        }
        catch (IOException)
        {
        }
    }

    public static RedeemEntry? Parse(string line)
    {
        var p = line.Split('\t');
        return p.Length == 6 && int.TryParse(p[1], out var account) ? new RedeemEntry(p[0], account, p[2], p[3], p[4], p[5]) : null;
    }

    public static string[] Lines()
    {
        lock (Gate)
            return Recent.Reverse().ToArray();
    }

    public static RedeemEntry[] RedeemLines()
    {
        lock (Gate)
            return Redeems.Reverse().ToArray();
    }

    private static void Push<T>(Queue<T> q, T item)
    {
        q.Enqueue(item);
        while (q.Count > Keep)
            q.Dequeue();
    }

    private static void Append(string? path, string line)
    {
        if (path is null)
            return;
        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }
}
