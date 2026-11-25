using XYN_Trader.Models;

namespace XYN_Trader.Services;

/// <summary>
/// Calculates short horizon price ranges and standalone trading levels from M5 OHLC data.
/// </summary>
public sealed class PricePredictionService
{
    private const int ForecastSampleSize = 96;
    private const int LevelSampleSize = 120;

    public PricePredictionSnapshot Analyze(MarketSnapshot market)
    {
        ArgumentNullException.ThrowIfNull(market);

        return new PricePredictionSnapshot(
            market.Symbol,
            market.Price,
            market.UpdatedAtUtc,
            BuildForecasts(market.Price, market.Candles),
            CalculateIndicators(market.Candles),
            CalculateLevels(market.Price, market.Candles));
    }

    private static IReadOnlyList<PriceForecast> BuildForecasts(decimal current, IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0 || current <= 0m)
            return [];

        return
        [
            BuildForecast("30m", "Следващите 30 мин", current, candles, 6),
            BuildForecast("1h", "Следващият 1 час", current, candles, 12),
            BuildForecast("4h", "Следващите 4 часа", current, candles, 48)
        ];
    }

    private static PriceForecast BuildForecast(
        string horizon,
        string label,
        decimal current,
        IReadOnlyList<Candle> candles,
        int barsAhead)
    {
        var sample = candles
            .Where(candle => candle.Close > 0m)
            .TakeLast(ForecastSampleSize)
            .ToArray();
        if (sample.Length == 0)
            return new PriceForecast(horizon, label, current, current, current, 0m);

        var first = sample[0].Close;
        var last = sample[^1].Close;
        var driftPerBar = sample.Length < 2 || first <= 0m
            ? 0m
            : (last - first) / first / (sample.Length - 1);
        var drift = Math.Clamp(driftPerBar * barsAhead, -0.025m, 0.025m);

        var returns = sample
            .Skip(1)
            .Select((candle, index) =>
            {
                var previous = sample[index].Close;
                return previous <= 0m ? 0m : Math.Abs((candle.Close - previous) / previous);
            })
            .ToArray();
        var volatility = returns.Length == 0
            ? current * 0.001m
            : current * returns.Average() * (decimal)Math.Sqrt(barsAhead);
        var center = current * (1m + drift);
        var halfRange = Math.Max(current * 0.0008m, volatility * 1.96m);

        return new PriceForecast(
            horizon,
            label,
            Math.Max(0m, center - halfRange),
            center + halfRange,
            center,
            (center - current) / current * 100m);
    }

    private static PredictionIndicators CalculateIndicators(IReadOnlyList<Candle> candles)
    {
        var closes = candles.Where(candle => candle.Close > 0m).Select(candle => candle.Close).ToArray();
        if (closes.Length == 0)
            return new PredictionIndicators(null, null, null, null, null, null);

        decimal? sma20 = closes.Length >= 20 ? closes.TakeLast(20).Average() : null;
        decimal? rsi14 = closes.Length >= 15 ? CalculateRsi(closes, 14) : null;
        var macdSeries = CalculateMacdSeries(closes, fast: 12, slow: 26, signal: 9);
        decimal? macd = macdSeries.Count > 0 ? macdSeries[^1].Macd : null;
        decimal? macdSignal = macdSeries.Count >= 9 ? macdSeries[^1].Signal : null;

        decimal? bollingerLower = null;
        decimal? bollingerUpper = null;
        if (closes.Length >= 20)
        {
            var window = closes.TakeLast(20).ToArray();
            var mean = window.Average();
            var variance = window.Select(value => (value - mean) * (value - mean)).Average();
            var deviation = (decimal)Math.Sqrt((double)variance);
            bollingerUpper = mean + 2m * deviation;
            bollingerLower = mean - 2m * deviation;
        }

        return new PredictionIndicators(sma20, rsi14, macd, macdSignal, bollingerLower, bollingerUpper);
    }

    private static decimal CalculateRsi(IReadOnlyList<decimal> closes, int period)
    {
        var changes = closes
            .Zip(closes.Skip(1), (previous, current) => current - previous)
            .TakeLast(period)
            .ToArray();
        var gains = changes.Where(change => change > 0m).DefaultIfEmpty().Average();
        var losses = changes.Where(change => change < 0m).Select(Math.Abs).DefaultIfEmpty().Average();
        if (losses == 0m)
            return gains == 0m ? 50m : 100m;

        return 100m - 100m / (1m + gains / losses);
    }

    private static List<(decimal Macd, decimal Signal)> CalculateMacdSeries(
        IReadOnlyList<decimal> closes,
        int fast,
        int slow,
        int signal)
    {
        if (closes.Count < slow)
            return [];

        var fastEma = closes[0];
        var slowEma = closes[0];
        var signalEma = 0m;
        var fastMultiplier = 2m / (fast + 1);
        var slowMultiplier = 2m / (slow + 1);
        var signalMultiplier = 2m / (signal + 1);
        var values = new List<(decimal Macd, decimal Signal)>();

        for (var index = 0; index < closes.Count; index++)
        {
            fastEma = index == 0 ? closes[index] : (closes[index] - fastEma) * fastMultiplier + fastEma;
            slowEma = index == 0 ? closes[index] : (closes[index] - slowEma) * slowMultiplier + slowEma;
            var macd = fastEma - slowEma;
            signalEma = values.Count == 0 ? macd : (macd - signalEma) * signalMultiplier + signalEma;
            if (index >= slow - 1)
                values.Add((macd, signalEma));
        }

        return values;
    }

    private static TradeLevelSnapshot CalculateLevels(decimal currentPrice, IReadOnlyList<Candle> candles)
    {
        if (currentPrice <= 0m)
            return new TradeLevelSnapshot(0m, 0m, [], [], [], []);

        var valid = candles
            .Where(candle => candle.High >= candle.Low && candle.Low > 0m && candle.Close > 0m)
            .TakeLast(LevelSampleSize)
            .ToArray();
        if (valid.Length < 3)
            return FallbackLevels(currentPrice);

        var highest = valid.Max(candle => candle.High);
        var lowest = valid.Min(candle => candle.Low);
        var range = Math.Max(highest - lowest, currentPrice * 0.002m);
        var atr = AverageTrueRange(valid);
        var pivot = (valid[^1].High + valid[^1].Low + valid[^1].Close) / 3m;

        var resistance = new[]
        {
            pivot + range * 0.236m, pivot + range * 0.382m, pivot + range * 0.618m,
            pivot + range * 0.786m, pivot + range
        };
        var support = new[]
        {
            pivot - range * 0.236m, pivot - range * 0.382m, pivot - range * 0.618m,
            pivot - range * 0.786m, pivot - range
        };

        var step = Math.Max(atr, currentPrice * 0.002m);
        var buyTargets = Enumerable.Range(1, 5)
            .Select(index => currentPrice + step * (0.618m + index * 0.618m))
            .Select(Round)
            .ToArray();
        var sellTargets = Enumerable.Range(1, 5)
            .Select(index => currentPrice - step * (0.618m + index * 0.618m))
            .Select(value => Round(Math.Max(0.01m, value)))
            .ToArray();
        var triggerGap = Math.Max(step * 0.32m, currentPrice * 0.0008m);

        return new TradeLevelSnapshot(
            Round(currentPrice + triggerGap),
            Round(Math.Max(0.01m, currentPrice - triggerGap)),
            NormalizeAbove(resistance, currentPrice),
            NormalizeBelow(support, currentPrice),
            buyTargets,
            sellTargets);
    }

    private static TradeLevelSnapshot FallbackLevels(decimal price)
    {
        var step = Math.Max(1.0m, Math.Round(price * 0.0055m, 2));
        var gap = Math.Max(1.0m, Math.Round(step * 0.32m, 2));
        return new TradeLevelSnapshot(
            Round(price + gap),
            Round(Math.Max(0.01m, price - gap)),
            Enumerable.Range(1, 5).Select(index => Round(price + step * index)).ToArray(),
            Enumerable.Range(1, 5).Select(index => Round(Math.Max(0.01m, price - step * index))).ToArray(),
            Enumerable.Range(1, 5).Select(index => Round(price + step * (index + 0.55m))).ToArray(),
            Enumerable.Range(1, 5).Select(index => Round(Math.Max(0.01m, price - step * (index + 0.55m)))).ToArray());
    }

    private static decimal AverageTrueRange(IReadOnlyList<Candle> candles)
    {
        var trueRanges = new List<decimal>();
        for (var index = 1; index < candles.Count; index++)
        {
            var current = candles[index];
            var previousClose = candles[index - 1].Close;
            trueRanges.Add(Math.Max(
                current.High - current.Low,
                Math.Max(Math.Abs(current.High - previousClose), Math.Abs(current.Low - previousClose))));
        }

        return trueRanges.Count == 0 ? 0m : trueRanges.TakeLast(14).Average();
    }

    private static decimal[] NormalizeAbove(IEnumerable<decimal> values, decimal price)
    {
        var result = values.Select(Round).Where(value => value > price).Distinct().Take(5).ToArray();
        return result.Length == 5
            ? result
            : Enumerable.Range(1, 5)
                .Select(index => Round(price + Math.Max(price * 0.002m, price * 0.0055m) * index))
                .ToArray();
    }

    private static decimal[] NormalizeBelow(IEnumerable<decimal> values, decimal price)
    {
        var result = values
            .Select(value => Round(Math.Max(0.01m, value)))
            .Where(value => value < price)
            .Distinct()
            .OrderByDescending(value => value)
            .Take(5)
            .ToArray();
        return result.Length == 5
            ? result
            : Enumerable.Range(1, 5)
                .Select(index => Round(Math.Max(0.01m, price - Math.Max(price * 0.002m, price * 0.0055m) * index)))
                .ToArray();
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
