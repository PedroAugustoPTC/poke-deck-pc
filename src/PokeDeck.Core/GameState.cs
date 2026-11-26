using System.Text.Json;
using System.Text.Json.Nodes;

namespace PokeDeck.Core;

/// <summary>
/// O retrato da conta que o game-bridge.js manda (já remontado dos deltas do socket do jogo). Ids de item ficam como
/// texto (são chaves de objeto no JSON do jogo).
/// </summary>
public sealed class GameState
{
    public bool Connected { get; init; }
    public string? Nick { get; init; }
    public long Gold { get; init; }
    public bool Vip { get; init; }
    public Dictionary<string, long> Balls { get; init; } = new();
    public Dictionary<string, long> Items { get; init; } = new();
    public JsonObject? Automation { get; init; }
    public double? MaxHp { get; init; }
    public List<BallInfo> BallCatalog { get; init; } = new();
    public List<ShopItem> Shop { get; init; } = new();
    public BonusSite? Twitch { get; init; }
    public BonusSite? Kick { get; init; }
    public bool ShopLoaded { get; init; }

    public long Have(string id) => Items.GetValueOrDefault(id);
    public long BallsOf(int id) => Balls.GetValueOrDefault(id.ToString());

    public static GameState Parse(JsonElement m)
    {
        static Dictionary<string, long> Counts(JsonElement e) =>
            e.ValueKind == JsonValueKind.Object
                ? e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.TryGetInt64(out var v) ? v : 0)
                : new();
        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
        static double Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        var balls = new List<BallInfo>();
        if (m.TryGetProperty("ballCatalog", out var bc) && bc.ValueKind == JsonValueKind.Array)
            foreach (var b in bc.EnumerateArray())
                balls.Add(new BallInfo((int)Num(b, "id"), Str(b, "nome") ?? "?", (long)Num(b, "priceGold"),
                    b.TryGetProperty("compravel", out var c) && c.ValueKind == JsonValueKind.True));
        var shop = new List<ShopItem>();
        if (m.TryGetProperty("shop", out var sh) && sh.ValueKind == JsonValueKind.Array)
            foreach (var i in sh.EnumerateArray())
                if (Str(i, "id") is string id)
                    shop.Add(new ShopItem(id, Str(i, "name") ?? id, Str(i, "category") ?? "", Num(i, "npcPrice"),
                        Num(i, "healAmount"), Num(i, "healPct")));
        return new GameState
        {
            Connected = m.TryGetProperty("connected", out var cn) && cn.ValueKind == JsonValueKind.True,
            Nick = Str(m, "nick"),
            Gold = (long)Num(m, "gold"),
            Vip = m.TryGetProperty("vip", out var vip) && vip.ValueKind == JsonValueKind.True,
            Balls = m.TryGetProperty("balls", out var bl) ? Counts(bl) : new(),
            Items = m.TryGetProperty("items", out var it) ? Counts(it) : new(),
            Automation = m.TryGetProperty("automation", out var au) && au.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(au.GetRawText()) as JsonObject : null,
            MaxHp = Num(m, "maxHp") is > 0 and var hp ? hp : (double?)null,
            BallCatalog = balls,
            Shop = shop,
            ShopLoaded = m.TryGetProperty("shopLoaded", out var sl) && sl.ValueKind == JsonValueKind.True,
            Twitch = m.TryGetProperty("twitch", out var tw) ? BonusSite.Parse(tw) : null,
            Kick = m.TryGetProperty("kick", out var kk) ? BonusSite.Parse(kk) : null,
        };
    }
}

public sealed record BallInfo(int Id, string Name, long Price, bool Buyable);

public sealed record ShopItem(string Id, string Name, string Category, double Price, double Heal, double HealPct);

/// <summary>O bônus das lives num site, como o jogo mostra: segundos que faltam por bônus ("xp", "captura", "shiny").</summary>
public sealed record BonusSite(bool Linked, Dictionary<string, int> Left, List<(string Login, bool Live)> Channels)
{
    public static BonusSite? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        var left = new Dictionary<string, int>();
        if (e.TryGetProperty("left", out var l) && l.ValueKind == JsonValueKind.Object)
            foreach (var p in l.EnumerateObject())
                left[p.Name] = p.Value.TryGetInt32(out var v) ? v : 0;
        var channels = new List<(string, bool)>();
        if (e.TryGetProperty("channels", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var ch in c.EnumerateArray())
                if (ch.TryGetProperty("login", out var login) && login.GetString() is { Length: > 0 } name)
                    channels.Add((name.ToLowerInvariant(), ch.TryGetProperty("live", out var lv) && lv.ValueKind == JsonValueKind.True));
        return new BonusSite(e.TryGetProperty("linked", out var k) && k.ValueKind == JsonValueKind.True, left, channels);
    }
}
