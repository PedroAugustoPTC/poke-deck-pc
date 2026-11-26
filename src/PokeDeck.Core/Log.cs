namespace PokeDeck.Core;

/// <summary>O registro: arquivo (%LOCALAPPDATA%\PokeDeck\pokedeck.log) e as últimas linhas para a interface.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    public static string? FilePath { get; set; }

    public static void Write(int? account, string text)
    {
        var line = $"{DateTime.Now:dd/MM HH:mm:ss} {(account is int a ? $"[conta {a + 1}] " : "")}{text}";
        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > 300)
                Recent.Dequeue();
            if (FilePath is null)
                return;
            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }

    public static string[] Lines()
    {
        lock (Gate)
            return Recent.Reverse().ToArray();
    }
}
