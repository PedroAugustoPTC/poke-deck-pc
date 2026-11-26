namespace PokeDeck.Core;

/// <summary>Todo intervalo do app passa por aqui: nada em ritmo exato, que é o que mais denuncia um robô.</summary>
public static class Jitter
{
    public static TimeSpan Of(TimeSpan baseTime, double pct, Random? rng = null)
    {
        double f = 1 + ((rng ?? Random.Shared).NextDouble() * 2 - 1) * pct;
        return TimeSpan.FromMilliseconds(Math.Max(1, baseTime.TotalMilliseconds * f));
    }

    public static TimeSpan Between(TimeSpan min, TimeSpan max, Random? rng = null) =>
        min + TimeSpan.FromMilliseconds((rng ?? Random.Shared).NextDouble() * (max - min).TotalMilliseconds);

    /// <summary>Roda <paramref name="work"/> para sempre, com a espera de <paramref name="every"/> (relido a cada volta) ± jitter.</summary>
    public static async Task Every(Func<TimeSpan> every, Func<double> pct, Func<Task> work, CancellationToken stop, TimeSpan? firstDelay = null)
    {
        try
        {
            await Task.Delay(firstDelay ?? Of(TimeSpan.FromSeconds(5), 0.5), stop);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await work();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Write(null, "erro: " + ex.Message);
                }
                await Task.Delay(Of(every(), pct()), stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
