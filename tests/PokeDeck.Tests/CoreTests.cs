using System.Text.Json;
using PokeDeck.Core;
using Xunit;

public class RestockTests
{
    private DateTime _now = new(2026, 11, 26, 12, 0, 0);

    private (Restock R, List<string> Notes) Make(Action<RestockConfig>? edit = null)
    {
        var r = new Restock(() => _now, new Random(1));
        var notes = new List<string>();
        r.Note += notes.Add;
        var c = new RestockConfig { On = true, GoldFloor = 1000 };
        edit?.Invoke(c);
        r.Configure(c, new Timing());
        return (r, notes);
    }

    private static GameState State(long gold, long balls = 500, long potion = 50, long revive = 5, double? maxHp = 100) =>
        GameState.Parse(JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            connected = true, shopLoaded = true, gold, maxHp,
            balls = new Dictionary<string, long> { ["1"] = balls },
            items = new Dictionary<string, long> { ["10"] = potion, ["20"] = revive },
            ballCatalog = new[] { new { id = 1, nome = "Poke Ball", priceGold = 10, compravel = true } },
            shop = new object[]
            {
                new { id = "10", name = "Potion", category = "heal", npcPrice = 20, healAmount = 20, healPct = 0 },
                new { id = "11", name = "Super Potion", category = "heal", npcPrice = 100, healAmount = 60, healPct = 0 },
                new { id = "20", name = "Revive", category = "revive", npcPrice = 500, healAmount = 0, healPct = 0 },
            },
        })).RootElement);

    private static JsonElement Msg(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Nothing_to_buy_when_stocked()
    {
        var (r, _) = Make();
        Assert.Empty(r.Tick(State(100_000)));
    }

    [Fact]
    public void Buys_ball_lot_when_below()
    {
        var (r, _) = Make();
        var sent = r.Tick(State(100_000, balls: 50));
        var m = Msg(Assert.Single(sent));
        Assert.Equal("shop.buy", m.GetProperty("t").GetString());
        Assert.Equal("ball", m.GetProperty("kind").GetString());
        Assert.Equal(500, m.GetProperty("qty").GetInt32());
    }

    [Fact]
    public void Waits_for_gold_instead_of_buying_partial_balls()
    {
        var (r, notes) = Make();
        Assert.Empty(r.Tick(State(3000, balls: 50))); // 500×10 + piso 1000 > 3000
        Assert.Contains(notes, n => n.Contains("esperando juntar"));
    }

    [Fact]
    public void Empty_potions_buy_what_gold_allows()
    {
        var (r, _) = Make();
        var m = Msg(Assert.Single(r.Tick(State(1500, potion: 0))));
        Assert.Equal(25, m.GetProperty("qty").GetInt32()); // (1500 − 1000) / 20
    }

    [Fact]
    public void Potions_come_before_balls()
    {
        var (r, _) = Make();
        var m = Msg(Assert.Single(r.Tick(State(100_000, balls: 0, potion: 0))));
        Assert.Equal("item", m.GetProperty("kind").GetString());
    }

    [Fact]
    public void Best_value_potion_uses_hp_room()
    {
        var (r, _) = Make();
        r.Tick(State(100_000, maxHp: 1000)); // espaço 700 HP: Potion 20/20 = 1/HP, Super 100/60 ≈ 1,67/HP
        Assert.Equal("Potion", r.PotionPick);
    }

    [Fact]
    public void Waits_gap_then_confirms_purchase()
    {
        var (r, notes) = Make(c => c.Balls.Buy = 20000);
        Assert.Single(r.Tick(State(1_000_000, balls: 50)));
        r.Bought(Restock.MaxQty, Restock.MaxQty * 10);
        Assert.Contains(notes, n => n.StartsWith("comprou") && n.Contains("faltam"));
        Assert.Empty(r.Tick(State(1_000_000, balls: 50))); // ainda no intervalo (4–8 s)
        _now += TimeSpan.FromSeconds(9);
        Assert.Single(r.Tick(State(1_000_000, balls: 50)));
    }

    [Fact]
    public void Three_timeouts_pause_two_minutes()
    {
        var (r, notes) = Make();
        int buys = 0;
        for (int i = 0; i < 200 && !r.Paused; i++, _now += TimeSpan.FromSeconds(1))
            buys += r.Tick(State(100_000, balls: 50)).Count; // o estoque nunca sobe: cada pedido vence em 15 s
        Assert.True(r.Paused);
        Assert.Equal(3, buys);
        Assert.Contains(notes, n => n.Contains("pausa de 2 min"));
    }
}

public class JitterTests
{
    [Fact]
    public void Stays_within_band()
    {
        var rng = new Random(3);
        for (int i = 0; i < 1000; i++)
        {
            var t = Jitter.Of(TimeSpan.FromSeconds(100), 0.2, rng).TotalSeconds;
            Assert.InRange(t, 80, 120);
        }
    }

    [Fact]
    public void Between_respects_limits()
    {
        var rng = new Random(3);
        for (int i = 0; i < 1000; i++)
            Assert.InRange(Jitter.Between(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), rng).TotalSeconds, 4, 8);
    }
}

public class PointsTests
{
    [Fact]
    public void Detects_refund_and_stops()
    {
        var now = DateTime.Now;
        var p = new Points("twitch", () => now);
        p.Apply("canal", """{"points":400,"cost":300,"title":"+15% XP PokeIdle","redeemed":true}""");
        Assert.Equal(100, p.Channels["canal"].Points);
        now += TimeSpan.FromMinutes(5);
        p.Apply("canal", """{"points":410,"cost":300}""");
        Assert.NotNull(p.Stopped);
        Assert.False(p.CanSpend("canal"));
    }

    [Fact]
    public void Refused_waits_before_retry()
    {
        var now = DateTime.Now;
        var p = new Points("kick", () => now);
        p.Apply("canal", """{"points":400,"cost":300,"redeemed":false,"status":400}""");
        Assert.False(p.CanSpend("canal"));
        now += TimeSpan.FromMinutes(31);
        Assert.True(p.CanSpend("canal"));
    }

    [Fact]
    public void Guard_prefers_offline_then_most_points()
    {
        var g = new Guard();
        var now = DateTime.Now;
        g.Remember("a", """{"points":900,"cost":300}""", now);
        g.Remember("b", """{"points":400,"cost":300}""", now);
        g.Remember("c", """{"points":100,"cost":300}""", now);
        g.Remember("d", """{"points":5000,"cost":300,"paused":true}""", now);
        Assert.Equal("b", g.Pick(new[] { "a", "b", "c", "d" }, new HashSet<string> { "a" }));
        Assert.Equal("a", g.Pick(new[] { "a", "c" }, new HashSet<string> { "a" }));
        Assert.Null(g.Pick(new[] { "c", "d" }, new HashSet<string>()));
    }
}

public class ParseTests
{
    [Fact]
    public void Twitch_live_parse()
    {
        var data = JsonDocument.Parse("""{"users":[{"login":"Rodil","stream":{"id":"1"}},null,{"login":"cogu","stream":null}]}""").RootElement;
        var r = TwitchApi.ParseLive(new[] { "rodil", "x", "cogu" }, data);
        Assert.True(r["rodil"]);
        Assert.False(r["x"]);
        Assert.False(r["cogu"]);
    }

    [Fact]
    public void Kick_live_parse()
    {
        Assert.True(KickApi.ParseLive("""{"livestream":{"is_live":true}}"""));
        Assert.False(KickApi.ParseLive("""{"livestream":null}"""));
        Assert.Null(KickApi.ParseLive("""{}"""));
    }

    [Fact]
    public void Lowest_video_variant()
    {
        var m3u8 = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=1920x1080\nhi.m3u8\n" +
                   "#EXT-X-STREAM-INF:BANDWIDTH=160000,CODECS=\"mp4a\"\naudio.m3u8\n" +
                   "#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=284x160\nlow.m3u8\n";
        Assert.Equal("https://x.net/a/low.m3u8", TwitchWatcher.LowestVariant("https://x.net/a/master.m3u8", m3u8));
    }

    [Fact]
    public void PubSub_claim_notice()
    {
        var n = TwitchPubSub.ParseNotice(JsonDocument.Parse("""{"type":"claim-available","data":{"claim":{"id":"c1","channel_id":"99"}}}""").RootElement);
        Assert.Equal(("claim-available", "99", "c1"), (n.Type, n.ChannelId, n.ClaimId));
    }

    [Fact]
    public void Extension_id_from_store_link()
    {
        Assert.Equal("olccbpdjdkbfgondiiaicmlifhbcggmk",
            Crx.IdFromLink("https://chromewebstore.google.com/detail/rotom-sniper-assistente-p/olccbpdjdkbfgondiiaicmlifhbcggmk?hl=pt-BR"));
        Assert.Null(Crx.IdFromLink("https://example.com"));
        Assert.Matches("^[a-p]{32}$", Crx.IdFromKey(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Config_fix_clamps()
    {
        var c = new Config { Accounts = new AccountConfig[1], TwitchChannels = new() { " Rodil ", "rodil", "" } }.Fix();
        Assert.Equal(Config.MaxAccounts, c.Accounts.Length);
        Assert.Equal(new[] { "rodil" }, c.TwitchChannels);
    }
}
