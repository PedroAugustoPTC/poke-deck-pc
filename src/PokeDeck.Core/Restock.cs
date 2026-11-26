using System.Text.Json;
using System.Text.Json.Nodes;

namespace PokeDeck.Core;

/// <summary>
/// A recompra de uma conta (regras do recompra.js do PokeIdleMulti, com os nossos tempos). Abaixo de "Below", começa um
/// lote de "Buy", uma vez; o jogo aceita até 9.999 por pedido, então o lote sai em pedidos com intervalo aleatório.
/// Sem ouro para o lote inteiro, espera juntar; a exceção é poção ou revive zerado, que compra o que o ouro deixar.
/// Prioridade: poção, revive, pokebola. <see cref="Tick"/> devolve as mensagens para o socket do jogo.
/// </summary>
public sealed class Restock
{
    public const int MaxQty = 9999;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RefusedWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailPause = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AutoRepeat = TimeSpan.FromSeconds(30);

    private sealed class Lot { public required Item Item; public long Left; }
    private sealed record Pending(string Kind, Item Item, long Qty, long Start, DateTime At);
    private sealed class Item { public required string Id; public required string Name; public double Price; public double Heal; public double HealPct; public double Cost = double.PositiveInfinity; public double Useful; }

    private readonly Func<DateTime> _now;
    private readonly Random _rng;
    private readonly Dictionary<string, Lot?> _lots = new() { ["potions"] = null, ["revives"] = null, ["balls"] = null };
    private readonly Dictionary<string, double> _paid = new();
    private readonly HashSet<string> _once = new();
    private Pending? _pending;
    private DateTime _nextAt, _pauseUntil;
    private int _failures;
    private (string Sig, DateTime At) _lastAuto;
    private RestockConfig _config = new();
    private Timing _timing = new();

    public Restock(Func<DateTime>? now = null, Random? rng = null)
    {
        _now = now ?? (() => DateTime.Now);
        _rng = rng ?? Random.Shared;
    }

    public event Action<string>? Note;
    public bool Paused => _pauseUntil > _now();
    public string? PotionPick { get; private set; }

    public void Configure(RestockConfig config, Timing timing)
    {
        foreach (var kind in _lots.Keys.ToList())
            if (!config.On || JsonSerializer.Serialize(RuleOf(config, kind)) != JsonSerializer.Serialize(RuleOf(_config, kind)))
                _lots[kind] = null;
        if (config.On != _config.On)
            Say(config.On ? "recompra ligada" : "recompra desligada");
        _config = config;
        _timing = timing;
        _once.Clear();
        _pauseUntil = default;
        _failures = 0;
    }

    private static Rule RuleOf(RestockConfig c, string kind) => kind switch { "potions" => c.Potions, "revives" => c.Revives, _ => c.Balls };

    private void Say(string text) => Note?.Invoke(text);

    private void SayOnce(string key, string text)
    {
        if (_once.Add(key))
            Say(text);
    }

    private static string Fmt(double n) => Math.Round(n).ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));

    private List<Item> Shop(GameState s, string category) =>
        s.Shop.Where(i => i.Category == category && i.Price > 0)
            .Select(i => new Item { Id = i.Id, Name = i.Name, Price = _paid.GetValueOrDefault(i.Id, i.Price), Heal = i.Heal, HealPct = i.HealPct })
            .OrderBy(i => i.Price).ToList();

    private static long Total(GameState s, IEnumerable<Item> items) => items.Sum(i => s.Have(i.Id));

    private static double Threshold(GameState s) =>
        Math.Clamp(s.Automation?["hpLimiar"]?.GetValueKind() == JsonValueKind.Number ? s.Automation["hpLimiar"]!.GetValue<double>() : 0.3, 0.1, 1);

    /// <summary>Custo-benefício: ouro por HP que a poção cura de verdade no ativo (a automação usa abaixo do limiar).</summary>
    private Item? PickPotion(GameState s, List<Item> potions)
    {
        if (_config.Potions.Id > 0)
            return potions.FirstOrDefault(p => p.Id == _config.Potions.Id.ToString());
        if (s.MaxHp is not double hp)
            return null;
        double room = Math.Max(0.1, 1 - Threshold(s)) * hp;
        foreach (var p in potions)
        {
            p.Useful = Math.Min(p.HealPct > 0 ? p.HealPct * hp : p.Heal, room);
            p.Cost = p.Useful > 0 ? p.Price / p.Useful : double.PositiveInfinity;
        }
        return potions.Where(p => p.Cost < double.PositiveInfinity).OrderBy(p => p.Cost).ThenByDescending(p => p.Useful).FirstOrDefault();
    }

    private long Stock(GameState s, string kind, Item item) => kind == "balls" ? s.Balls.GetValueOrDefault(item.Id) : s.Have(item.Id);

    private const string Wait = "wait";

    private object? Plan(GameState s, string kind, Rule rule, long count, Item? item, string? waiting)
    {
        if (!rule.On || rule.Buy <= 0)
        {
            _lots[kind] = null;
            return null;
        }
        double spare = s.Gold - _config.GoldFloor;
        var lot = _lots[kind];
        if (lot is null)
        {
            if (count >= rule.Below || item is null || item.Price <= 0)
                return null;
            long qty = rule.Buy;
            if (waiting is not null || spare < rule.Buy * item.Price)
            {
                if (kind == "balls" || count >= 1)
                {
                    SayOnce("ouro:" + kind, waiting is not null
                        ? $"{item.Name}: abaixo de {Fmt(rule.Below)}, esperando {waiting} (o ouro vai primeiro para lá)"
                        : $"{item.Name}: abaixo de {Fmt(rule.Below)}, esperando juntar {Fmt(_config.GoldFloor + rule.Buy * item.Price)} de ouro");
                    return Wait;
                }
                qty = Math.Min(rule.Buy, (long)Math.Floor(spare / item.Price));
                if (qty < 1)
                {
                    SayOnce("ouro:" + kind, $"{item.Name}: acabou e o ouro está no mínimo ({Fmt(_config.GoldFloor)})");
                    return Wait;
                }
            }
            _once.Remove("ouro:" + kind);
            lot = _lots[kind] = new Lot { Item = item, Left = qty };
            Say($"{item.Name}: {Fmt(count)} na mochila, abaixo de {Fmt(rule.Below)}: compra {Fmt(qty)}");
        }
        long q = Math.Min(Math.Min(MaxQty, lot.Left), (long)Math.Floor(spare / lot.Item.Price));
        if (q < 1)
        {
            _lots[kind] = null;
            Say($"{lot.Item.Name}: o ouro chegou no mínimo no meio do lote; ficaram {Fmt(lot.Left)} sem comprar");
            return null;
        }
        return new Pending(kind, lot.Item, q, Stock(s, kind, lot.Item), default);
    }

    /// <summary>Uma volta (o app chama ~1x/s). Devolve as mensagens para mandar no socket do jogo (0, 1 ou 2).</summary>
    public List<string> Tick(GameState s)
    {
        var send = new List<string>();
        if (!_config.On || !s.Connected || !s.ShopLoaded)
            return send;
        var now = _now();
        var potions = Shop(s, "heal");
        var revives = Shop(s, "revive");
        var potion = PickPotion(s, potions);
        PotionPick = potion?.Name;
        if (FixAutomation(s, potion, potions, revives, now) is string auto)
            send.Add(auto);
        if (_pending is Pending p)
        {
            if (Stock(s, p.Kind, p.Item) - p.Start >= p.Qty / 2.0)
                Done($"comprou {p.Item.Name} ×{Fmt(p.Qty)}");
            else if (now - p.At < Timeout)
                return send;
            else
                Failed($"{p.Item.Name}: o jogo não respondeu à compra");
        }
        if (now < _nextAt || now < _pauseUntil)
            return send;

        var b = s.BallCatalog.FirstOrDefault(x => x.Id == _config.Balls.Id && x.Buyable && x.Price > 0);
        var ball = b is null ? null : new Item { Id = b.Id.ToString(), Name = b.Name, Price = _paid.GetValueOrDefault("bola" + b.Id, b.Price) };
        string? waiting = null;
        foreach (var (kind, count, item, name) in new (string, long, Item?, string)[]
                 {
                     ("potions", Total(s, potions), potion, "as poções"),
                     ("revives", Total(s, revives), revives.FirstOrDefault(), "os revives"),
                     ("balls", ball is null ? 0 : s.Balls.GetValueOrDefault(ball.Id), ball, "a pokebola"),
                 })
        {
            var step = Plan(s, kind, RuleOf(_config, kind), count, item, waiting);
            if (step is Wait)
                waiting ??= name;
            else if (step is Pending todo)
            {
                _pending = todo with { At = now };
                _nextAt = now + Jitter.Between(TimeSpan.FromSeconds(_timing.BuyGapMinSec), TimeSpan.FromSeconds(_timing.BuyGapMaxSec), _rng);
                send.Add(JsonSerializer.Serialize(new
                {
                    t = "shop.buy", kind = kind == "balls" ? "ball" : "item",
                    id = int.TryParse(todo.Item.Id, out var n) ? (object)n : todo.Item.Id, qty = todo.Qty,
                }));
                break;
            }
        }
        return send;
    }

    /// <summary>As Automações do jogo para o que a recompra compra (um "auto.set" igual ao do painel do jogo).</summary>
    private string? FixAutomation(GameState s, Item? potion, List<Item> potions, List<Item> revives, DateTime now)
    {
        if (s.Automation is not JsonObject auto)
            return null;
        var patch = new JsonObject();
        var turned = new List<string>();
        static List<JsonNode?> Ids(JsonObject o, string key) => o[key] is JsonArray a ? a.Select(x => x?.DeepClone()).ToList() : new();
        static string Key(JsonNode? n) => n?.ToJsonString().Trim('"') ?? "";
        static JsonNode Id(string id) => int.TryParse(id, out var n) ? JsonValue.Create(n) : JsonValue.Create(id);
        static bool True(JsonObject o, string key) => o[key]?.GetValueKind() == JsonValueKind.True;

        if (_config.Potions.On)
        {
            if (!True(auto, "autoPotion"))
            {
                patch["autoPotion"] = true;
                turned.Add("Usar Poções");
            }
            var queue = Ids(auto, "potionIds");
            if (_config.PotionFirst && potion is not null && Key(queue.FirstOrDefault()) != potion.Id)
            {
                var rest = queue.Count > 0
                    ? queue.Where(x => Key(x) != potion.Id).ToList()
                    : potions.Where(x => x.Id != potion.Id).OrderByDescending(x => x.Price).Select(x => (JsonNode?)Id(x.Id)).ToList();
                patch["potionIds"] = new JsonArray(new[] { Id(potion.Id) }.Concat(rest).ToArray());
                turned.Add($"{potion.Name} em 1º no \"Usar Poções\"");
            }
        }
        if (_config.Revives.On)
        {
            if (!True(auto, "autoRevive"))
            {
                patch["autoRevive"] = true;
                turned.Add("Usar Revive");
            }
            var chosen = Ids(auto, "reviveIds");
            if (revives.FirstOrDefault() is Item r && chosen.Count > 0 && chosen.All(x => Key(x) != r.Id))
            {
                patch["reviveIds"] = new JsonArray(chosen.Append(Id(r.Id)).ToArray());
                turned.Add($"{r.Name} no \"Usar Revive\"");
            }
        }
        int ballId = _config.Balls.Id;
        if (_config.Balls.On && ballId > 0 && s.Vip)
        {
            if (!True(auto, "autoBallSemParar") || True(auto, "autoBallAteCapturar"))
            {
                patch["autoBallSemParar"] = true;
                patch["autoBallAteCapturar"] = false;
                turned.Add("Lançar sem parar");
            }
            var balls = Ids(auto, "ballIds");
            if (Key(balls.FirstOrDefault()) != ballId.ToString())
            {
                patch["ballIds"] = new JsonArray(new JsonNode?[] { ballId }.Concat(balls.Where(x => Key(x) != ballId.ToString())).ToArray());
                turned.Add("pokebola da recompra em 1º");
            }
        }
        if (turned.Count == 0)
            return null;
        var sig = patch.ToJsonString();
        if (sig == _lastAuto.Sig && now < _lastAuto.At + AutoRepeat)
            return null;
        _lastAuto = (sig, now);
        var message = (JsonObject)auto.DeepClone();
        foreach (var (k, v) in patch)
            message[k] = v?.DeepClone();
        message["t"] = "auto.set";
        Say("Automações do jogo: " + string.Join(", ", turned));
        return message.ToJsonString();
    }

    /// <summary>O jogo avisou uma compra (evento "compra" da batalha): qtd e o ouro gasto.</summary>
    public void Bought(long qty, long spent)
    {
        if (_pending is not Pending p || qty != p.Qty)
            return; // compra feita na mão
        if (spent > 0)
        {
            _paid[p.Kind == "balls" ? "bola" + p.Item.Id : p.Item.Id] = (double)spent / p.Qty;
            p.Item.Price = (double)spent / p.Qty;
        }
        Done($"comprou {p.Item.Name} ×{Fmt(p.Qty)}" + (spent > 0 ? $" (−{Fmt(spent)} ouro)" : ""));
    }

    public void Refused(string reason)
    {
        if (_pending is not Pending p || _now() - p.At > TimeSpan.FromSeconds(5))
            return;
        Failed($"{p.Item.Name}: o jogo recusou a compra ({reason})");
        _nextAt = _now() + RefusedWait;
    }

    private void Done(string text)
    {
        var p = _pending!;
        var lot = _lots[p.Kind];
        string rest = "";
        if (lot is not null && ReferenceEquals(lot.Item, p.Item))
        {
            lot.Left -= p.Qty;
            if (lot.Left > 0)
                rest = $" (faltam {Fmt(lot.Left)} do lote)";
            else
                _lots[p.Kind] = null;
        }
        Say(text + rest);
        _pending = null;
        _failures = 0;
    }

    private void Failed(string text)
    {
        Say(text);
        _pending = null;
        if (++_failures >= 3)
        {
            _failures = 0;
            _pauseUntil = _now() + FailPause;
            Say("3 compras seguidas sem dar certo: pausa de 2 min");
        }
    }
}
