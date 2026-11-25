using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

public sealed class SignalLearningCollectorService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<SignalLearningCollectorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = int.TryParse(configuration["AILearning:CollectionIntervalMinutes"], out var configuredMinutes)
            ? Math.Clamp(configuredMinutes, 5, 1440)
            : 15;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        do
        {
            await CollectAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var marketProvider = scope.ServiceProvider.GetRequiredService<Mt5MarketDataProvider>();
            var generator = scope.ServiceProvider.GetRequiredService<CandidateGeneratorClient>();
            var learningStatus = await generator.GetLearningStatusAsync(cancellationToken);
            if (learningStatus.Available && learningStatus.Status?.StoredCandles == 0 &&
                await TryHistoricalBackfillAsync(generator, cancellationToken))
            {
                return;
            }

            var market = await marketProvider.GetSnapshotAsync("H1", cancellationToken);
            if (market.Snapshot is null)
            {
                logger.LogWarning("AI learning collector skipped this cycle: {Message}", market.Message);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var candles = market.Snapshot.Candles
                .Where(candle => candle.TimeUtc.AddHours(1) <= now)
                .OrderBy(candle => candle.TimeUtc)
                .TakeLast(1000)
                .ToArray();
            if (candles.Length == 0)
            {
                logger.LogWarning("AI learning collector received no closed H1 candles from MT5.");
                return;
            }

            var result = await generator.IngestClosedCandlesAsync(
                candles,
                cancellationToken: cancellationToken);
            if (!result.Available)
            {
                logger.LogWarning("AI learning candle intake failed: {Message}", result.Message);
                return;
            }

            if (result.Intake is { ChangedCandles: > 0 } intake)
            {
                logger.LogInformation(
                    "AI learning stored {ChangedCandles} changed H1 candles; {StoredCandles} total are retained.",
                    intake.ChangedCandles,
                    intake.StoredCandles);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown interrupted the current collection request.
        }
        catch (Exception error)
        {
            logger.LogError(error, "AI learning candle collection failed.");
        }
    }

    private async Task<bool> TryHistoricalBackfillAsync(
        CandidateGeneratorClient generator,
        CancellationToken cancellationToken)
    {
        var backfillDays = int.TryParse(configuration["AILearning:BackfillDays"], out var configuredDays)
            ? Math.Clamp(configuredDays, 30, 3650)
            : 365;
        var symbol = string.IsNullOrWhiteSpace(configuration["MT5Bridge:Symbol"])
            ? "XAUUSD"
            : configuration["MT5Bridge:Symbol"]!.Trim();
        var historyClient = httpClientFactory.CreateClient("MT5LearningHistory");
        var now = DateTimeOffset.UtcNow;
        var cursor = now.AddDays(-backfillDays);
        var candles = new Dictionary<DateTimeOffset, Candle>();

        try
        {
            while (cursor < now)
            {
                var chunkEnd = cursor.AddDays(14) < now ? cursor.AddDays(14) : now;
                var start = Uri.EscapeDataString(cursor.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                var end = Uri.EscapeDataString(chunkEnd.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                var uri = $"rates_range/{Uri.EscapeDataString(symbol)}?timeframe=H1&start={start}&end={end}";
                using var response = await historyClient.GetAsync(uri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "MT5 historical learning backfill returned HTTP {StatusCode}; using the latest bridge candles instead.",
                        (int)response.StatusCode);
                    return false;
                }

                var rates = await response.Content.ReadFromJsonAsync<List<HistoricalRate>>(
                    cancellationToken: cancellationToken);
                if (rates is null)
                {
                    logger.LogWarning("MT5 historical learning backfill returned an empty response body.");
                    return false;
                }

                foreach (var rate in rates)
                {
                    if (rate.Time <= 0 || rate.Open <= 0 || rate.High <= 0 || rate.Low <= 0 || rate.Close <= 0 ||
                        rate.Low > Math.Min(rate.Open, rate.Close) || rate.High < Math.Max(rate.Open, rate.Close) ||
                        rate.Low > rate.High)
                    {
                        continue;
                    }

                    var time = DateTimeOffset.FromUnixTimeSeconds(rate.Time);
                    if (time.AddHours(1) <= now)
                        candles[time] = new Candle(rate.Open, rate.High, rate.Low, rate.Close, time);
                }

                cursor = chunkEnd.AddSeconds(1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(error, "MT5 historical learning backfill failed; using the latest bridge candles instead.");
            return false;
        }

        var ordered = candles.Values.OrderBy(candle => candle.TimeUtc).ToArray();
        if (ordered.Length < 100)
        {
            logger.LogInformation(
                "MT5 returned {CandleCount} historical H1 candles; continuing with the current bridge window.",
                ordered.Length);
            return false;
        }

        for (var offset = 0; offset < ordered.Length; offset += 1000)
        {
            var batch = ordered.Skip(offset).Take(1000).ToArray();
            var isLastBatch = offset + batch.Length >= ordered.Length;
            var result = await generator.IngestClosedCandlesAsync(
                batch,
                runLearningCycle: isLastBatch,
                cancellationToken: cancellationToken);
            if (!result.Available)
            {
                logger.LogWarning("MT5 backfill candles could not be stored: {Message}", result.Message);
                return false;
            }
        }

        logger.LogInformation(
            "MT5 historical backfill seeded the learning database with {CandleCount} closed H1 candles from the last {BackfillDays} days.",
            ordered.Length,
            backfillDays);
        return true;
    }

    private sealed record HistoricalRate(
        [property: JsonPropertyName("time")] long Time,
        [property: JsonPropertyName("open")] decimal Open,
        [property: JsonPropertyName("high")] decimal High,
        [property: JsonPropertyName("low")] decimal Low,
        [property: JsonPropertyName("close")] decimal Close);
}
