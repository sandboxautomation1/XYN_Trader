using XYN_Trader.Models;

namespace XYN_Trader.Services;

public sealed class LiveSignalService(
    Mt5MarketDataProvider marketProvider,
    CandidateGeneratorClient generator)
{
    private const int MinimumClosedCandles = 100;
    private static readonly TimeSpan MaximumCandleAge = TimeSpan.FromHours(4);

    public async Task<CandidateGenerationResult> GetCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var market = await marketProvider.GetSnapshotAsync("H1", cancellationToken);
        return await GenerateFromSnapshotAsync(market, cancellationToken);
    }

    public Task<MarketTickResult> GetCurrentQuoteAsync(CancellationToken cancellationToken = default) =>
        marketProvider.GetLatestTickAsync(cancellationToken);

    public async Task<CandidateGenerationResult> GenerateFromSnapshotAsync(
        MarketDataResult market,
        CancellationToken cancellationToken = default)
    {
        if (market.ConnectionStatus != MarketConnectionStatus.Connected || market.Snapshot is null)
        {
            return await CachedOrUnavailableAsync(
                market.Message ?? "Свещите от MT5 не са налични.",
                cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var quoteTime = market.Snapshot.UpdatedAtUtc;
        if (Mt5MarketDataProvider.GetFreshness(quoteTime, now) != MarketFreshness.Fresh)
        {
            return await CachedOrUnavailableAsync(
                "MT5 котировката е по-стара от 15 минути.",
                cancellationToken);
        }

        var candles = market.Snapshot.Candles
            .OrderBy(candle => candle.TimeUtc)
            .Where(candle => candle.TimeUtc.AddHours(1) <= now)
            .ToArray();
        if (candles.Length < MinimumClosedCandles)
        {
            return await CachedOrUnavailableAsync(
                $"MT5 върна само {candles.Length} затворени H1 свещи; нужни са поне {MinimumClosedCandles}.",
                cancellationToken);
        }

        var latestClosedBar = candles[^1].TimeUtc.AddHours(1);
        if (latestClosedBar > now.AddMinutes(1))
        {
            return await CachedOrUnavailableAsync(
                "Времевият печат на последната H1 свещ е в бъдещето.",
                cancellationToken);
        }

        if (now - latestClosedBar > MaximumCandleAge)
        {
            return await CachedOrUnavailableAsync(
                "Последната затворена H1 свещ е по-стара от 4 часа.",
                cancellationToken);
        }

        var generated = await generator.GenerateAsync(candles, cancellationToken);
        return generated.Available
            ? generated
            : await CachedOrUnavailableAsync(generated.Message, cancellationToken);
    }

    private async Task<CandidateGenerationResult> CachedOrUnavailableAsync(
        string unavailableReason,
        CancellationToken cancellationToken)
    {
        var cached = await generator.GetLatestAsync(cancellationToken);
        if (!cached.Available)
            return CandidateGenerationResult.Unavailable(unavailableReason);

        return cached with
        {
            IsStale = true,
            Message = $"{unavailableReason} Показани са запазените сценарии от последното успешно генериране. {cached.Message}"
        };
    }
}
