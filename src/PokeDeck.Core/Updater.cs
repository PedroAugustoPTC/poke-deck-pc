using System.Text.Json;

namespace PokeDeck.Core;

/// <summary>Uma release do GitHub com o PokeDeck.exe anexado e o SHA-256 que o GitHub publica dele.</summary>
public sealed record Release(Version Version, string Tag, string Page, string Notes, string ExeUrl, string Sha256);

/// <summary>
/// Atualização: ao abrir, o app lê a release mais nova do repositório e, se for mais nova que ele, oferece baixar.
/// Só aceita links do próprio repositório e só troca o exe se o SHA-256 do arquivo baixado bater com o da release.
/// </summary>
public static class Updater
{
    public const string Repo = "https://github.com/PedroAugustoPTC/poke-deck-pc";
    public const string LatestUrl = "https://api.github.com/repos/PedroAugustoPTC/poke-deck-pc/releases/latest";

    /// <summary>A resposta de /releases/latest; null se faltar o exe, o SHA-256 ou a tag não for vX.Y.Z.</summary>
    public static Release? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = Str(root, "tag_name") ?? "";
            var page = Str(root, "html_url") ?? "";
            if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || !page.StartsWith(Repo + "/releases/")
                || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var a in assets.EnumerateArray())
            {
                var url = Str(a, "browser_download_url") ?? "";
                var digest = Str(a, "digest") ?? "";
                if (Str(a, "name") == "PokeDeck.exe" && url.StartsWith(Repo + "/releases/download/")
                    && digest.StartsWith("sha256:") && digest.Length == 7 + 64)
                    return new Release(Plain(version), tag, page, Str(root, "body") ?? "", url, digest[7..]);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A release é mais nova que o exe atual. O build local (0.0.0) nunca se atualiza.</summary>
    public static bool IsNewer(Version? current, Release release) =>
        current is not null && Plain(current) > new Version(0, 0, 0) && release.Version > Plain(current);

    // Só major.minor.patch: o exe diz 0.2.0.0 e a tag diz 0.2.0.
    private static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
