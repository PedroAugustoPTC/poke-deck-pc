using System.Globalization;

namespace PokeDeck.Core;

/// <summary>
/// Horário de bloqueio do resgate ("HH:mm", por conta): se o buff que a conta já tem passa desse horário, resgatar mais
/// não serve para nada, e o resgate daquele site é desligado.
/// </summary>
public static class RedeemBlock
{
    /// <summary>A próxima vez que o relógio marca <paramref name="block"/> a partir de agora; null = sem bloqueio.</summary>
    public static DateTime? Next(string? block, DateTime now)
    {
        if (!TimeOnly.TryParseExact(block, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return null;
        var at = now.Date + t.ToTimeSpan();
        return at > now ? at : at.AddDays(1);
    }

    /// <summary>O buff que falta (<paramref name="leftSec"/>) já chega ao próximo horário de bloqueio.</summary>
    public static bool Covers(string? block, int leftSec, DateTime now) =>
        Next(block, now) is DateTime at && now.AddSeconds(leftSec) >= at;
}
