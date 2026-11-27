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
    public void Notes_have_kinds_and_spent_elsewhere()
    {
        var p = new Points("Twitch");
        Assert.Contains(p.Apply("c", """{"points":400,"claimed":true}"""), n => n.Kind == "baú");
        Assert.Contains(p.Apply("c", """{"points":100}"""), n => n.Kind == "gasto fora");
        Assert.Equal(new Log.RedeemEntry("26/11 10:00:00", 2, "Kick", "yoda", "resgate", "resgatou X"),
            Log.Parse("26/11 10:00:00\t2\tKick\tyoda\tresgate\tresgatou X"));
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

public class IdleHubTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0);
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parses_the_game_vote_state()
    {
        var s = IdleHub.ParseState(J("""{"status":200,"nick":"ash","dados":{"ativo":true,"esperaMs":60000,"votos":7,"noPar":1,"porPar":5,"votosPorDiamante":2}}"""), Now)!;
        Assert.True(s.LoggedIn && s.Active);
        Assert.Equal(Now.AddMinutes(1), s.NextVoteAt);
        Assert.False(s.ReadyAt(Now));
        Assert.True(s.ReadyAt(Now.AddMinutes(2)));
        Assert.Equal("https://idlehub.io/votar/pokeidle-io?jogador=ash", s.Url);
        Assert.Equal((7, 1, 5, 2), (s.Votes, s.InPair, s.PerPair, s.VotesPerReward));
    }

    [Fact]
    public void Login_missing_hides_and_failures_retry()
    {
        Assert.False(IdleHub.ParseState(J("""{"erro":"login"}"""), Now)!.LoggedIn);
        Assert.False(IdleHub.ParseState(J("""{"status":401}"""), Now)!.LoggedIn);
        Assert.Null(IdleHub.ParseState(J("""{"erro":"pagina"}"""), Now));
        Assert.Null(IdleHub.ParseState(J("""{"status":500,"dados":null}"""), Now));
    }

    [Fact]
    public void Only_idlehub_links_and_safe_nicks()
    {
        Assert.Equal("https://idlehub.io/votar/pokeidle-io", IdleHub.VoteUrl("a b&c"));
        var s = IdleHub.ParseState(J("""{"status":200,"dados":{"ativo":true,"url":"https://evil.com/x","identificador":"misty"}}"""), Now)!;
        Assert.Equal("https://idlehub.io/votar/pokeidle-io?jogador=misty", s.Url);
        Assert.True(s.ReadyAt(Now));
    }

    [Fact]
    public void Detects_the_vote_response()
    {
        Assert.True(IdleHub.IsVoteResponse("POST", "https://idlehub.io/api/votar", 200));
        Assert.False(IdleHub.IsVoteResponse("POST", "https://idlehub.io/api/auth/session", 200));
        Assert.False(IdleHub.IsVoteResponse("GET", "https://idlehub.io/api/votar", 200));
        Assert.True(IdleHub.TryParseVoted("""{"ok":true,"proximoEm":1795000000000}""", out var next));
        Assert.NotNull(next);
        Assert.False(IdleHub.TryParseVoted("""{"ok":false}""", out _));
        Assert.False(IdleHub.TryParseVoted("<html>", out _));
    }
}

public class RedeemBlockTests
{
    private static readonly DateTime Noon = new(2026, 11, 26, 12, 0, 0);

    [Fact]
    public void Ten_hours_left_at_noon_covers_22h()
    {
        Assert.True(RedeemBlock.Covers("22:00", 10 * 3600, Noon));
        Assert.False(RedeemBlock.Covers("22:00", 9 * 3600, Noon));
    }

    [Fact]
    public void Block_time_already_passed_today_means_tomorrow()
    {
        Assert.Equal(Noon.Date.AddDays(1).AddHours(8), RedeemBlock.Next("08:00", Noon));
        Assert.False(RedeemBlock.Covers("08:00", 10 * 3600, Noon));
        Assert.True(RedeemBlock.Covers("08:00", 20 * 3600, Noon));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("25:00")]
    [InlineData("22h")]
    public void No_or_bad_block_never_blocks(string? block) =>
        Assert.False(RedeemBlock.Covers(block, 100 * 3600, Noon));

    [Fact]
    public void Old_config_keeps_redeem_on_and_bad_block_is_dropped()
    {
        var c = JsonSerializer.Deserialize<Config>("""{"RedeemBlockAt":"xx","Accounts":[{"Name":"A"}]}""")!.Fix();
        Assert.True(c.Accounts[0].RedeemTwitchOn && c.Accounts[0].RedeemKickOn);
        Assert.Null(c.RedeemBlockAt);
    }
}

public class UpdaterTests
{
    private static string Latest(string tag = "v0.3.0", string url = "https://github.com/PedroAugustoPTC/poke-deck-pc/releases/download/v0.3.0/PokeDeck.exe",
        string digest = "sha256:39a9548b6c5f1961e40a2477abe7e951b73664ed492d64f646a1ec48bc3aac99") =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = "https://github.com/PedroAugustoPTC/poke-deck-pc/releases/tag/" + tag,
            body = "novidades",
            assets = new[] { new { name = "PokeDeck.exe", browser_download_url = url, digest } },
        });

    [Fact]
    public void Reads_the_latest_release()
    {
        var r = Updater.Parse(Latest())!;
        Assert.Equal(new Version(0, 3, 0), r.Version);
        Assert.Equal("39a9548b6c5f1961e40a2477abe7e951b73664ed492d64f646a1ec48bc3aac99", r.Sha256);
        Assert.Equal("novidades", r.Notes);
    }

    [Fact]
    public void Refuses_foreign_links_missing_hash_or_bad_tag()
    {
        Assert.Null(Updater.Parse(Latest(url: "https://evil.example/PokeDeck.exe")));
        Assert.Null(Updater.Parse(Latest(digest: "")));
        Assert.Null(Updater.Parse(Latest(tag: "latest")));
        Assert.Null(Updater.Parse("not json"));
    }

    [Fact]
    public void Only_newer_releases_and_never_the_local_build()
    {
        var r = Updater.Parse(Latest())!;
        Assert.True(Updater.IsNewer(new Version(0, 2, 0, 0), r));
        Assert.False(Updater.IsNewer(new Version(0, 3, 0, 0), r));
        Assert.False(Updater.IsNewer(new Version(0, 4, 0, 0), r));
        Assert.False(Updater.IsNewer(new Version(0, 0, 0, 0), r));
    }
}
