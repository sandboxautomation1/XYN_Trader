using System.Globalization;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

/// <summary>
/// Generates a directional signal from SMA20, MACD and RSI only. News and sentiment are not read here.
/// </summary>
public sealed class TechnicalTradeSignalService
{
    public const int MinimumCandleCount = 26;

    public GeneratedTechnicalSignal Generate(MarketSnapshot market, DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(market);

        var candles = market.Candles
            .Where(IsValidCandle)
            .OrderBy(candle => candle.TimeUtc)
            .ToArray();
        var entry = market.Price;

        if (candles.Length < MinimumCandleCount)
        {
            return new GeneratedTechnicalSignal(
                "WAIT",
                entry,
                null,
                null,
                generatedAtUtc,
                candles.Length,
                0,
                0,
                $"Изчакване: нужни са поне {MinimumCandleCount} валидни свещи, налични са {candles.Length}. Сигналът не е изчислен.");
        }

        var closes = candles.Select(candle => candle.Close).ToArray();
        var sma20 = closes.TakeLast(20).Average();
        var rsi14 = CalculateRsi14(closes);
        var (macd, macdSignal) = CalculateMacd(closes);
        var atr14 = CalculateAtr14(candles);

        var priceVote = entry > sma20 ? 1 : entry < sma20 ? -1 : 0;
        var macdVote = macd > macdSignal ? 1 : macd < macdSignal ? -1 : 0;
        var rsiVote = rsi14 > 50m && rsi14 <= 70m ? 1
            : rsi14 >= 30m && rsi14 < 50m ? -1
            : 0;
        var bullishVotes = new[] { priceVote, macdVote, rsiVote }.Count(vote => vote > 0);
        var bearishVotes = new[] { priceVote, macdVote, rsiVote }.Count(vote => vote < 0);

        var direction = bullishVotes >= 2 && bullishVotes > bearishVotes ? "BUY"
            : bearishVotes >= 2 && bearishVotes > bullishVotes ? "SELL"
            : "WAIT";
        var indicatorSummary = string.Join("; ",
        [
            DescribeVote("Цена/SMA20", priceVote, $"{Format(entry)} спрямо {Format(sma20)}"),
            DescribeVote("MACD", macdVote, $"{Format(macd, 3)} спрямо сигнал {Format(macdSignal, 3)}"),
            DescribeVote("RSI14", rsiVote, $"{Format(rsi14, 1)}")
        ]);

        if (direction == "WAIT")
        {
            return new GeneratedTechnicalSignal(
                direction,
                entry,
                null,
                null,
                generatedAtUtc,
                candles.Length,
                bullishVotes,
                bearishVotes,
                $"Изчакване: няма ясно мнозинство от поне 2 от 3 гласа с по-силна страна. Бичи {bullishVotes}, мечи {bearishVotes}. {indicatorSummary}.");
        }

        var riskDistance = Math.Max(atr14 * 1.5m, entry * 0.001m);
        var stopLoss = direction == "BUY" ? entry - riskDistance : entry + riskDistance;
        var takeProfit = direction == "BUY" ? entry + riskDistance * 2m : entry - riskDistance * 2m;
        var explanation = direction == "BUY"
            ? $"BUY: бичите гласове са по-силни ({bullishVotes} срещу {bearishVotes}). {indicatorSummary}. Stop loss е на 1.5 × ATR14 (минимум 0,1% от входа), take profit е 2R."
            : $"SELL: мечите гласове са по-силни ({bearishVotes} срещу {bullishVotes}). {indicatorSummary}. Stop loss е на 1.5 × ATR14 (минимум 0,1% от входа), take profit е 2R.";

        return new GeneratedTechnicalSignal(
            direction,
            entry,
            stopLoss,
            takeProfit,
            generatedAtUtc,
            candles.Length,
            bullishVotes,
            bearishVotes,
            explanation);
    }

    private static bool IsValidCandle(Candle candle) =>
        candle.Open > 0m && candle.High > 0m && candle.Low > 0m && candle.Close > 0m &&
        candle.High >= Math.Max(candle.Open, candle.Close) &&
        candle.Low <= Math.Min(candle.Open, candle.Close) &&
        candle.High >= candle.Low;

    private static decimal CalculateRsi14(IReadOnlyList<decimal> closes)
    {
        var gains = 0m;
        var losses = 0m;
        for (var index = 1; index <= 14; index++)
        {
            var change = closes[index] - closes[index - 1];
            gains += Math.Max(change, 0m);
            losses += Math.Max(-change, 0m);
        }

        var averageGain = gains / 14m;
        var averageLoss = losses / 14m;
        for (var index = 15; index < closes.Count; index++)
        {
            var change = closes[index] - closes[index - 1];
            averageGain = (averageGain * 13m + Math.Max(change, 0m)) / 14m;
            averageLoss = (averageLoss * 13m + Math.Max(-change, 0m)) / 14m;
        }

        if (averageLoss == 0m)
            return averageGain == 0m ? 50m : 100m;

        return 100m - 100m / (1m + averageGain / averageLoss);
    }

    private static (decimal Macd, decimal Signal) CalculateMacd(IReadOnlyList<decimal> closes)
    {
        var fast = CalculateEmaSeries(closes, 12);
        var slow = CalculateEmaSeries(closes, 26);
        var macdSeries = Enumerable.Range(0, closes.Count)
            .Where(index => index >= 25)
            .Select(index => fast[index] - slow[index])
            .ToArray();
        var signalSeries = CalculateEmaSeries(macdSeries, 9);
        return (macdSeries[^1], signalSeries[^1]);
    }

    private static decimal[] CalculateEmaSeries(IReadOnlyList<decimal> values, int period)
    {
        if (values.Count == 0)
            return [];

        var result = new decimal[values.Count];
        var multiplier = 2m / (period + 1m);
        result[0] = values[0];
        for (var index = 1; index < values.Count; index++)
            result[index] = (values[index] - result[index - 1]) * multiplier + result[index - 1];
        return result;
    }

    private static decimal CalculateAtr14(IReadOnlyList<Candle> candles)
    {
        var trueRanges = new decimal[candles.Count];
        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index];
            trueRanges[index] = index == 0
                ? candle.High - candle.Low
                : Math.Max(
                    candle.High - candle.Low,
                    Math.Max(
                        Math.Abs(candle.High - candles[index - 1].Close),
                        Math.Abs(candle.Low - candles[index - 1].Close)));
        }

        var atr = trueRanges.Take(14).Average();
        for (var index = 14; index < trueRanges.Length; index++)
            atr = (atr * 13m + trueRanges[index]) / 14m;
        return atr;
    }

    private static string DescribeVote(string label, int vote, string value)
    {
        var side = vote > 0 ? "бичи" : vote < 0 ? "мечи" : "неутрален";
        return $"{label} {value} · {side} глас";
    }

    private static string Format(decimal value, int decimals = 2) =>
        value.ToString(decimals == 3 ? "N3" : decimals == 1 ? "N1" : "N2", CultureInfo.CurrentCulture);
}
