using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PokeDeck.Core;

public sealed class ShareException(string message) : Exception(message);

/// <summary>
/// Leva a config entre o PokeDeck do PC e o do celular (espelho do Share.kt do mobile), por código de texto ou arquivo
/// .json. Vai só o que é preferência: canais e lives, baú, horário de bloqueio e, por conta (pela posição), nome, ligada,
/// ultra, economia, lives, resgates, guarda e recompra. O que é do aparelho (layout, zoom, mudo, movimento, tempos, tour)
/// fica. Formato: JSON camelCase com "v"; o código é "PKD1:" + base64url(gzip(json)). Também abre o config.json dos dois.
/// </summary>
public static class Share
{
    public const int Version = 1;
    public const string Prefix = "PKD1:";
    private const string NotConfig = "Isto não é uma config do PokeDeck.";
    private const int MaxJson = 256 * 1024;

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions AnyCase = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] DeviceKeys = { "layout", "focus", "timing", "tourDone" };
    private static readonly string[] AccountDeviceKeys = { "reduceMotion", "muted", "zoom" };

    public static JsonObject Json(Config c)
    {
        var o = JsonSerializer.SerializeToNode(c, Camel)!.AsObject();
        foreach (var k in DeviceKeys)
            o.Remove(k);
        foreach (var a in o["accounts"]!.AsArray())
            foreach (var k in AccountDeviceKeys)
                a!.AsObject().Remove(k);
        var r = new JsonObject { ["v"] = Version, ["app"] = "pokedeck" };
        foreach (var (k, v) in o.ToArray())
        {
            o.Remove(k);
            r[k] = v;
        }
        return r;
    }

    public static string FileText(Config c) => Json(c).ToJsonString(Pretty);

    public static string Code(Config c)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize))
            gz.Write(Encoding.UTF8.GetBytes(Json(c).ToJsonString()));
        return Prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Um código ou um JSON aplicado sobre <paramref name="current"/>: devolve uma config nova, com o que é do aparelho mantido.</summary>
    public static Config Read(string text, Config current)
    {
        var o = Parse(text);
        if (o["v"] is JsonValue v && v.TryGetValue<int>(out int version) && version > Version)
            throw new ShareException("Esta config é de uma versão mais nova do PokeDeck. Atualize o app.");
        if (!o.ContainsKey("accounts") && !o.ContainsKey("twitchChannels") && !o.ContainsKey("kickChannels"))
            throw new ShareException(NotConfig);

        Config incoming;
        try
        {
            incoming = o.Deserialize<Config>(AnyCase) ?? throw new ShareException(NotConfig);
        }
        catch (JsonException)
        {
            throw new ShareException(NotConfig);
        }
        var output = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(current))!.Fix();
        if (o.ContainsKey("twitchChannels")) output.TwitchChannels = incoming.TwitchChannels;
        if (o.ContainsKey("kickChannels")) output.KickChannels = incoming.KickChannels;
        if (o.ContainsKey("twitchLives")) output.TwitchLives = incoming.TwitchLives;
        if (o.ContainsKey("kickLives")) output.KickLives = incoming.KickLives;
        if (o.ContainsKey("claimChest")) output.ClaimChest = incoming.ClaimChest;
        if (o.ContainsKey("redeemBlockAt")) output.RedeemBlockAt = incoming.RedeemBlockAt;
        if (o["accounts"] is JsonArray && incoming.Accounts is { } accounts)
            for (int i = 0; i < Math.Min(accounts.Length, output.Accounts.Length); i++)
            {
                if (accounts[i] is not { } a)
                    continue;
                var here = output.Accounts[i];
                a.ReduceMotion = here.ReduceMotion;
                a.Muted = here.Muted;
                a.Zoom = here.Zoom;
                output.Accounts[i] = a;
            }
        return output.Fix();
    }

    private static JsonObject Parse(string text)
    {
        var t = text.Trim().TrimStart('﻿').Trim();
        string json;
        if (t.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            var b64 = new string(t[Prefix.Length..].Where(ch => !char.IsWhiteSpace(ch)).ToArray()).Replace('-', '+').Replace('_', '/').TrimEnd('=');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            try
            {
                using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
                var buf = new byte[MaxJson + 1];
                int n = 0, r;
                while (n < buf.Length && (r = gz.Read(buf, n, buf.Length - n)) > 0)
                    n += r;
                if (n > MaxJson)
                    throw new ShareException(NotConfig);
                json = Encoding.UTF8.GetString(buf, 0, n);
            }
            catch (Exception e) when (e is FormatException or InvalidDataException or IOException)
            {
                throw new ShareException("Código incompleto ou corrompido. Copie de novo, inteiro.");
            }
        }
        else if (t.StartsWith('{'))
            json = t;
        else
            throw new ShareException($"Cole o código que começa com {Prefix} (ou abra o arquivo .json).");

        try
        {
            return JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject ?? throw new ShareException(NotConfig);
        }
        catch (JsonException)
        {
            throw new ShareException(NotConfig);
        }
    }
}
