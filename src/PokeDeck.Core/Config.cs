using System.Text.Json;

namespace PokeDeck.Core;

/// <summary>Tudo o que o usuário define, salvo em %LOCALAPPDATA%\PokeDeck\config.json.</summary>
public sealed class Config
{
    public const int MaxAccounts = 4;

    public AccountConfig[] Accounts { get; set; } =
        Enumerable.Range(1, MaxAccounts).Select(i => new AccountConfig { Name = $"Conta {i}" }).ToArray();

    /// <summary>"grid", "cols" ou "focus" (com <see cref="Focus"/>).</summary>
    public string Layout { get; set; } = "grid";
    public int Focus { get; set; }

    // Os streamers oficiais do painel de bônus do jogo (lista do PokeIdleMulti, out/2026), em ordem de prioridade.
    public List<string> TwitchChannels { get; set; } = new()
    {
        "tftoddy", "clovaobengala", "axtlol", "patopapao", "matta", "guaxilol1", "rodil",
        "cogu", "luccatv", "faasii", "biscoitao", "techzz", "dersin2k", "denissnidertv",
    };
    public List<string> KickChannels { get; set; } = new() { "biscoitao", "yetz", "surskity" };
    public int TwitchLives { get; set; } = 2;
    public int KickLives { get; set; } = 2; // a Kick só conta pontos de 2 lives por conta
    public bool ClaimChest { get; set; } = true;

    /// <summary>
    /// Horário de bloqueio ("HH:mm") de todas as contas, ou null: cada conta, antes de resgatar, confere se o buff que
    /// ela já tem passa desse horário e, se passa, desliga o próprio resgate daquele site.
    /// </summary>
    public string? RedeemBlockAt { get; set; }

    public Timing Timing { get; set; } = new();

    /// <summary>
    /// O tour de primeiro acesso já foi visto (ou pulado). Começa true para quem atualiza (o config.json antigo não tem o
    /// campo); só o <see cref="Load"/> sem arquivo nenhum, o primeiro acesso, devolve false.
    /// </summary>
    public bool TourDone { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Config Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) is Config c)
                return c.Fix();
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".bad", true); // não perde o arquivo que não abriu
            return new Config();
        }
        return new Config { TourDone = File.Exists(path) };
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }

    /// <summary>Corrige o que veio fora do esperado (do arquivo ou da interface).</summary>
    public Config Fix()
    {
        var list = (Accounts ?? Array.Empty<AccountConfig>()).Take(MaxAccounts).Select((a, i) => a ?? new AccountConfig { Name = $"Conta {i + 1}" }).ToList();
        while (list.Count < MaxAccounts)
            list.Add(new AccountConfig { Name = $"Conta {list.Count + 1}" });
        Accounts = list.ToArray();
        TwitchChannels = Clean(TwitchChannels);
        KickChannels = Clean(KickChannels);
        TwitchLives = Math.Clamp(TwitchLives, 0, 5);
        KickLives = Math.Clamp(KickLives, 0, 5);
        Focus = Math.Clamp(Focus, 0, MaxAccounts - 1);
        Timing ??= new();
        Timing.Fix();
        foreach (var a in Accounts)
            a.Restock ??= new();
        if (RedeemBlock.Next(RedeemBlockAt, DateTime.Now) is null)
            RedeemBlockAt = null;
        return this;
    }

    private static List<string> Clean(List<string>? channels) =>
        (channels ?? new()).Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct().ToList();
}

public sealed class AccountConfig
{
    public string Name { get; set; } = "";
    public bool On { get; set; } = true;
    public bool Ultra { get; set; }
    public bool Economy { get; set; }
    public bool ReduceMotion { get; set; } = true;
    public bool Muted { get; set; } = true;
    public double Zoom { get; set; } = 1;

    /// <summary>Esta conta assiste às lives (a conta do jogo vinculada à Twitch/Kick logada nela).</summary>
    public bool Lives { get; set; }

    /// <summary>O bônus do jogo resgatado com os pontos de cada site: null, "xp", "captura" ou "shiny".</summary>
    public string? RedeemTwitch { get; set; }
    public string? RedeemKick { get; set; }

    /// <summary>O resgate de cada site ligado (ícone 🎁 no cabeçalho). O horário de bloqueio desliga sozinho.</summary>
    public bool RedeemTwitchOn { get; set; } = true;
    public bool RedeemKickOn { get; set; } = true;

    /// <summary>Guarda do bônus: perto de vencer, resgata num canal com pontos parados.</summary>
    public bool Guard { get; set; } = true;

    public RestockConfig Restock { get; set; } = new();
}

public sealed class RestockConfig
{
    public bool On { get; set; }
    public long GoldFloor { get; set; } = 10_000;
    public bool PotionFirst { get; set; }

    /// <summary>Pokebola: Id do catálogo do jogo (1 = Poke Ball).</summary>
    public Rule Balls { get; set; } = new() { On = true, Id = 1, Buy = 500, Below = 100 };
    public Rule Revives { get; set; } = new() { On = true, Buy = 10, Below = 3 };

    /// <summary>Poções: Id 0 = a de melhor custo-benefício.</summary>
    public Rule Potions { get; set; } = new() { On = true, Buy = 100, Below = 30 };
}

public sealed class Rule
{
    public bool On { get; set; }
    public int Id { get; set; }
    public int Buy { get; set; }
    public int Below { get; set; }
}

/// <summary>Os tempos. Todos passam pelo <see cref="Jitter"/> (± <see cref="JitterPct"/>).</summary>
public sealed class Timing
{
    public double JitterPct { get; set; } = 0.2;
    public int LiveCheckSec { get; set; } = 120;
    public int TwitchPointsSec { get; set; } = 120;
    public int KickPointsSec { get; set; } = 300;
    public int GuardSec { get; set; } = 300;
    public int GuardThresholdMin { get; set; } = 30;
    public int BuyGapMinSec { get; set; } = 4;
    public int BuyGapMaxSec { get; set; } = 8;
    public int RedeemRetryMin { get; set; } = 30;

    /// <summary>Quadros por segundo de todas as contas (60 = sem limite) e ritmo da animação dos sprites (220 = o do jogo).</summary>
    public int Fps { get; set; } = 30;
    public int SpriteMs { get; set; } = 220;

    public static readonly int[] FpsOptions = { 10, 15, 20, 30, 60 };
    public static readonly int[] SpriteMsOptions = { 220, 440, 660, 880 };

    private static int Nearest(int[] options, int v) => options.MinBy(o => Math.Abs(o - v));

    public void Fix()
    {
        Fps = Nearest(FpsOptions, Fps);
        SpriteMs = Nearest(SpriteMsOptions, SpriteMs);
        JitterPct = Math.Clamp(JitterPct, 0, 0.5);
        LiveCheckSec = Math.Max(60, LiveCheckSec);
        TwitchPointsSec = Math.Max(60, TwitchPointsSec);
        KickPointsSec = Math.Max(60, KickPointsSec);
        GuardSec = Math.Max(60, GuardSec);
        GuardThresholdMin = Math.Clamp(GuardThresholdMin, 5, 600);
        BuyGapMinSec = Math.Max(2, BuyGapMinSec);
        BuyGapMaxSec = Math.Max(BuyGapMinSec, BuyGapMaxSec);
        RedeemRetryMin = Math.Max(5, RedeemRetryMin);
    }
}
