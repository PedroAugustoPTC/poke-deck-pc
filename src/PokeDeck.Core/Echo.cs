using System.Text.Json;

namespace PokeDeck.Core;

/// <summary>
/// Eco: o que você faz numa conta se repete nas outras. Em vez de repetir o clique (clique sintético, que o jogo
/// percebe), repete a mensagem que o jogo mandou ao servidor por causa do seu clique, no socket do jogo de cada conta.
/// Fica de fora o que só vale para a conta clicada: texto (chat, DM, comércio) e o que aponta para algo dela
/// (pokemonId, amigoId, conversaId…: o pokémon de uma conta não existe nas outras).
/// </summary>
public static class Echo
{
    private static readonly string[] Personal = { "chat.", "amigo.", "comercio.", "ticket.", "perfil.sobre.", "orbs.", "passe.comprar" };

    public static bool ShouldRepeat(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t) || t.GetString() is not string type
                || type == "ping" || Personal.Any(type.StartsWith))
                return false;
            foreach (var p in root.EnumerateObject())
                if (p.Name.Length > 2 && p.Name.EndsWith("Id") || p.Name == "texto")
                    return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
