using System.Globalization;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

public sealed class TechnicalAnalysisService
{
    private const int RsiPeriod = 14;
    private const int AtrPeriod = 14;
    private const int AdxPeriod = 14;
    private const int BollingerPeriod = 20;
    private const int EmaShortPeriod = 20;
    private const int EmaLongPeriod = 50;
    private const int MacdFastPeriod = 12;
    private const int MacdSlowPeriod = 26;
    private const int MacdSignalPeriod = 9;

    public AnalysisSnapshot Analyze(MarketSnapshot market, string timeframe)
    {
        ArgumentNullException.ThrowIfNull(market);

        var candles = market.Candles
            .Where(IsValidCandle)
            .OrderBy(candle => candle.TimeUtc)
            .ToArray();
        var closes = candles.Select(candle => (double)candle.Close).ToArray();
        var price = (double)market.Price;
        var readings = new List<IndicatorReading>(6);
        var votes = new List<int>(5);

        var rsi = CalculateRsi(closes, RsiPeriod);
        if (rsi is { } rsiValue)
        {
            var state = rsiValue >= 70d ? "Свръхкупен пазар"
                : rsiValue <= 30d ? "Свръхпродаден пазар"
                : rsiValue >= 55d ? "Възходящ импулс"
                : rsiValue <= 45d ? "Низходящ импулс"
                : "Неутрална зона";
            readings.Add(Reading("RSI (14)", Format(rsiValue), state,
                Percent(rsiValue), rsiValue >= 70d ? "negative" : rsiValue <= 30d ? "positive" : Tone(rsiValue >= 55d ? 1 : rsiValue <= 45d ? -1 : 0)));
            votes.Add(rsiValue >= 55d ? 1 : rsiValue <= 45d ? -1 : 0);
        }
        else
        {
            readings.Add(Unavailable("RSI (14)", RsiPeriod + 1, candles.Length));
        }

        var macd = CalculateMacd(closes, MacdFastPeriod, MacdSlowPeriod, MacdSignalPeriod);
        if (macd is { } macdValue)
        {
            var state = macdValue.Histogram >= 0d ? "MACD над сигналната линия" : "MACD под сигналната линия";
            readings.Add(Reading("MACD (12/26/9)", Signed(macdValue.Histogram), state,
                Percent(Math.Clamp(50d + macdValue.Histogram / Math.Max(Math.Abs(price) * 0.001d, 0.000001d) * 25d, 0d, 100d)),
                Tone(macdValue.Histogram >= 0d ? 1 : -1)));
            votes.Add(macdValue.Histogram >= 0d ? 1 : -1);
        }
        else
        {
            readings.Add(Unavailable("MACD (12/26/9)", MacdSlowPeriod + MacdSignalPeriod - 1, candles.Length));
        }

        var atrSeries = CalculateAtrSeries(candles, AtrPeriod);
        var atr = atrSeries.LastOrDefault(value => value.HasValue);
        if (atr is { } atrValue)
        {
            var recentAtr = atrSeries.Where(value => value.HasValue).TakeLast(50).Select(value => value!.Value).ToArray();
            var baseline = recentAtr.Length == 0 ? atrValue : recentAtr.Average();
            var relativeVolatility = baseline <= 0d ? 1d : atrValue / baseline;
            var state = relativeVolatility >= 1.2d ? "Волатилността е повишена"
                : relativeVolatility <= 0.8d ? "Волатилността е ниска"
                : "Обичайна волатилност";
            var strength = price <= 0d ? 0 : Percent(atrValue / price * 100d * 100d);
            readings.Add(Reading("ATR (14)", Format(atrValue), state, strength,
                relativeVolatility >= 1.2d ? "negative" : "neutral"));
        }
        else
        {
            readings.Add(Unavailable("ATR (14)", AtrPeriod, candles.Length));
        }

        var adx = CalculateAdx(candles, AdxPeriod);
        if (adx is { } adxValue)
        {
            var direction = adxValue.PlusDi >= adxValue.MinusDi ? 1 : -1;
            var state = adxValue.Adx < 20d ? "Слаб тренд · няма потвърждение"
                : adxValue.Adx < 25d ? $"Формиращ се {DirectionWord(direction)} тренд"
                : $"{DirectionWord(direction)} тренд · +DI {Format(adxValue.PlusDi)} / −DI {Format(adxValue.MinusDi)}";
            readings.Add(Reading("ADX (14)", Format(adxValue.Adx), state,
                Percent(adxValue.Adx), adxValue.Adx < 20d ? "neutral" : Tone(direction)));
            votes.Add(adxValue.Adx >= 20d ? direction : 0);
        }
        else
        {
            readings.Add(Unavailable("ADX (14)", AdxPeriod * 2, candles.Length));
        }

        var bands = CalculateBollinger(closes, BollingerPeriod, 2d);
        if (bands is { } bandValue)
        {
            var bandWidth = bandValue.Upper - bandValue.Lower;
            var position = bandWidth <= 0d ? 50d : Math.Clamp((price - bandValue.Lower) / bandWidth * 100d, 0d, 100d);
            var state = price > bandValue.Upper ? "Цената е над горната лента"
                : price < bandValue.Lower ? "Цената е под долната лента"
                : position >= 65d ? "Горна половина на лентите"
                : position <= 35d ? "Долна половина на лентите"
                : "Около средната лента";
            readings.Add(Reading("Bollinger (20, 2)", $"U {Format(bandValue.Upper)}",
                $"L {Format(bandValue.Lower)} · M {Format(bandValue.Middle)} · {state}",
                Percent(position), Tone(price >= bandValue.Middle ? 1 : -1)));
            votes.Add(price >= bandValue.Middle ? 1 : -1);
        }
        else
        {
            readings.Add(Unavailable("Bollinger (20, 2)", BollingerPeriod, candles.Length));
        }

        var ema20 = CalculateEma(closes, EmaShortPeriod);
        var ema50 = CalculateEma(closes, EmaLongPeriod);
        if (ema20.Length > 0 && ema50.Length > 0)
        {
            var shortValue = ema20[^1];
            var longValue = ema50[^1];
            var differencePercent = longValue == 0d ? 0d : (shortValue - longValue) / longValue * 100d;
            var direction = shortValue >= longValue ? 1 : -1;
            var state = direction > 0 ? "EMA20 над EMA50 · възходящ тренд" : "EMA20 под EMA50 · низходящ тренд";
            readings.Add(Reading("EMA (20 / 50)", $"{Signed(differencePercent)}%",
                $"EMA20 {Format(shortValue)} · EMA50 {Format(longValue)} · {state}",
                Percent(50d + differencePercent * 100d), Tone(direction)));
            votes.Add(direction);
        }
        else
        {
            readings.Add(Unavailable("EMA (20 / 50)", EmaLongPeriod, candles.Length));
        }

        var score = votes.Count == 0
            ? 50
            : Percent(50d + votes.Average() * 50d);
        var regime = score >= 65 ? "Възходящ импулс"
            : score >= 55 ? "Умерено възходящ"
            : score <= 35 ? "Низходящ импулс"
            : score <= 45 ? "Умерено низходящ"
            : "Страничен пазар";
        var atrForZones = atr ?? (price * 0.001d);

        return new AnalysisSnapshot(
            Timeframe: timeframe.ToUpperInvariant(),
            Regime: regime,
            RegimeConfidencePercent: Math.Clamp(Math.Abs(score - 50) * 2, 0, 100),
            TechnicalScore: score,
            Indicators: readings,
            Zones: CalculateZones(candles, price, atrForZones));
    }

    public MarketSnapshot ApplyTick(MarketSnapshot market, MarketTick tick, string timeframe)
    {
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(tick);
        if (market.Candles.Count == 0)
            return market with { Price = tick.Price, UpdatedAtUtc = tick.UpdatedAtUtc };

        var candles = market.Candles.OrderBy(candle => candle.TimeUtc).ToArray();
        var last = candles[^1];
        if (tick.UpdatedAtUtc < last.TimeUtc)
            return market with { Price = tick.Price, UpdatedAtUtc = tick.UpdatedAtUtc };

        var duration = GetTimeframeDuration(timeframe);
        var elapsed = tick.UpdatedAtUtc - last.TimeUtc;
        var barsSinceLast = duration <= TimeSpan.Zero ? 0 : elapsed.Ticks / duration.Ticks;
        if (barsSinceLast <= 0)
        {
            candles[^1] = last with
            {
                High = Math.Max(last.High, tick.Price),
                Low = Math.Min(last.Low, tick.Price),
                Close = tick.Price
            };
        }
        else
        {
            var barTime = last.TimeUtc.AddTicks(barsSinceLast * duration.Ticks);
            var open = last.Close;
            candles = [.. candles, new Candle(
                open,
                Math.Max(open, tick.Price),
                Math.Min(open, tick.Price),
                tick.Price,
                barTime)];
            if (candles.Length > 300)
                candles = candles[^300..];
        }

        return market with
        {
            Price = tick.Price,
            UpdatedAtUtc = tick.UpdatedAtUtc,
            Candles = candles
        };
    }

    public static IReadOnlyList<double?> CalculateEma(IReadOnlyList<Candle> candles, int period)
    {
        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period));
        var values = CalculateEma(candles.Select(candle => (double)candle.Close).ToArray(), period);
        var result = new double?[candles.Count];
        for (var index = period - 1; index < candles.Count && index - period + 1 < values.Length; index++)
            result[index] = values[index - period + 1];
        return result;
    }

    private static bool IsValidCandle(Candle candle) =>
        candle.Low <= Math.Min(candle.Open, candle.Close) &&
        candle.High >= Math.Max(candle.Open, candle.Close) &&
        candle.Low <= candle.High;

    private static double[] CalculateEma(double[] values, int period)
    {
        if (values.Length < period || period <= 0)
            return [];

        var result = new double[values.Length];
        Array.Fill(result, double.NaN);
        var seed = values.Take(period).Average();
        result[period - 1] = seed;
        var multiplier = 2d / (period + 1d);
        for (var index = period; index < values.Length; index++)
            result[index] = (values[index] - result[index - 1]) * multiplier + result[index - 1];
        return result.Skip(period - 1).ToArray();
    }

    private static double? CalculateRsi(double[] closes, int period)
    {
        if (closes.Length <= period)
            return null;

        var gains = 0d;
        var losses = 0d;
        for (var index = 1; index <= period; index++)
        {
            var change = closes[index] - closes[index - 1];
            gains += Math.Max(change, 0d);
            losses += Math.Max(-change, 0d);
        }

        var averageGain = gains / period;
        var averageLoss = losses / period;
        for (var index = period + 1; index < closes.Length; index++)
        {
            var change = closes[index] - closes[index - 1];
            averageGain = (averageGain * (period - 1) + Math.Max(change, 0d)) / period;
            averageLoss = (averageLoss * (period - 1) + Math.Max(-change, 0d)) / period;
        }

        if (averageLoss == 0d)
            return averageGain == 0d ? 50d : 100d;
        var relativeStrength = averageGain / averageLoss;
        return 100d - 100d / (1d + relativeStrength);
    }

    private static MacdValue? CalculateMacd(double[] closes, int fastPeriod, int slowPeriod, int signalPeriod)
    {
        var minimumBars = slowPeriod + signalPeriod - 1;
        if (closes.Length < minimumBars)
            return null;

        var fast = CalculateEma(closes, fastPeriod);
        var slow = CalculateEma(closes, slowPeriod);
        var firstMacdIndex = slowPeriod - 1;
        var macd = new double[closes.Length];
        Array.Fill(macd, double.NaN);
        for (var index = firstMacdIndex; index < closes.Length; index++)
            macd[index] = fast[index - fastPeriod + 1] - slow[index - slowPeriod + 1];

        var firstSignalIndex = firstMacdIndex + signalPeriod - 1;
        var signal = macd.Skip(firstMacdIndex).Take(signalPeriod).Average();
        var multiplier = 2d / (signalPeriod + 1d);
        for (var index = firstSignalIndex + 1; index < closes.Length; index++)
        {
            var line = macd[index];
            signal = (line - signal) * multiplier + signal;
        }

        var latestMacd = macd[^1];
        return double.IsFinite(latestMacd) && double.IsFinite(signal)
            ? new MacdValue(latestMacd, signal, latestMacd - signal)
            : null;
    }

    private static double?[] CalculateAtrSeries(IReadOnlyList<Candle> candles, int period)
    {
        var result = new double?[candles.Count];
        if (candles.Count < period)
            return result;

        var trueRanges = new double[candles.Count];
        for (var index = 0; index < candles.Count; index++)
        {
            var high = (double)candles[index].High;
            var low = (double)candles[index].Low;
            trueRanges[index] = index == 0
                ? high - low
                : Math.Max(high - low, Math.Max(
                    Math.Abs(high - (double)candles[index - 1].Close),
                    Math.Abs(low - (double)candles[index - 1].Close)));
        }

        var atr = trueRanges.Take(period).Average();
        result[period - 1] = atr;
        for (var index = period; index < candles.Count; index++)
        {
            atr = (atr * (period - 1) + trueRanges[index]) / period;
            result[index] = atr;
        }
        return result;
    }

    private static AdxValue? CalculateAdx(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period * 2)
            return null;

        var trueRanges = new double[candles.Count];
        var positiveMovement = new double[candles.Count];
        var negativeMovement = new double[candles.Count];
        for (var index = 1; index < candles.Count; index++)
        {
            var high = (double)candles[index].High;
            var low = (double)candles[index].Low;
            var previousHigh = (double)candles[index - 1].High;
            var previousLow = (double)candles[index - 1].Low;
            var previousClose = (double)candles[index - 1].Close;
            var upMove = high - previousHigh;
            var downMove = previousLow - low;
            positiveMovement[index] = upMove > downMove && upMove > 0d ? upMove : 0d;
            negativeMovement[index] = downMove > upMove && downMove > 0d ? downMove : 0d;
            trueRanges[index] = Math.Max(high - low, Math.Max(
                Math.Abs(high - previousClose), Math.Abs(low - previousClose)));
        }

        var smoothedTrueRange = trueRanges.Skip(1).Take(period).Sum();
        var smoothedPositive = positiveMovement.Skip(1).Take(period).Sum();
        var smoothedNegative = negativeMovement.Skip(1).Take(period).Sum();
        var dxValues = new List<double>();
        var latestPlusDi = 0d;
        var latestMinusDi = 0d;
        for (var index = period; index < candles.Count; index++)
        {
            if (index > period)
            {
                smoothedTrueRange = smoothedTrueRange - smoothedTrueRange / period + trueRanges[index];
                smoothedPositive = smoothedPositive - smoothedPositive / period + positiveMovement[index];
                smoothedNegative = smoothedNegative - smoothedNegative / period + negativeMovement[index];
            }

            if (smoothedTrueRange <= 0d)
            {
                dxValues.Add(0d);
                continue;
            }

            latestPlusDi = 100d * smoothedPositive / smoothedTrueRange;
            latestMinusDi = 100d * smoothedNegative / smoothedTrueRange;
            var diTotal = latestPlusDi + latestMinusDi;
            dxValues.Add(diTotal <= 0d ? 0d : 100d * Math.Abs(latestPlusDi - latestMinusDi) / diTotal);
        }

        if (dxValues.Count < period)
            return null;

        var adx = dxValues.Take(period).Average();
        for (var index = period; index < dxValues.Count; index++)
            adx = (adx * (period - 1) + dxValues[index]) / period;
        return new AdxValue(adx, latestPlusDi, latestMinusDi);
    }

    private static BollingerValue? CalculateBollinger(double[] closes, int period, double deviations)
    {
        if (closes.Length < period)
            return null;
        var window = closes.TakeLast(period).ToArray();
        var middle = window.Average();
        var standardDeviation = Math.Sqrt(window.Average(value => Math.Pow(value - middle, 2d)));
        return new BollingerValue(middle, middle + deviations * standardDeviation, middle - deviations * standardDeviation);
    }

    private static IReadOnlyList<PriceZone> CalculateZones(IReadOnlyList<Candle> candles, double price, double atr)
    {
        if (candles.Count < 5)
            return [];

        var start = Math.Max(2, candles.Count - 120);
        var levels = new List<(double Price, bool IsSupport)>();
        for (var index = start; index < candles.Count - 2; index++)
        {
            var isSwingHigh = candles[index].High > candles[index - 1].High &&
                candles[index].High > candles[index - 2].High &&
                candles[index].High >= candles[index + 1].High &&
                candles[index].High >= candles[index + 2].High;
            var isSwingLow = candles[index].Low < candles[index - 1].Low &&
                candles[index].Low < candles[index - 2].Low &&
                candles[index].Low <= candles[index + 1].Low &&
                candles[index].Low <= candles[index + 2].Low;
            if (isSwingLow)
                levels.Add(((double)candles[index].Low, true));
            if (isSwingHigh)
                levels.Add(((double)candles[index].High, false));
        }

        var tolerance = Math.Max(atr * 0.25d, Math.Abs(price) * 0.00005d);
        var zones = new List<PriceZone>(2);
        AddNearestZone(levels.Where(level => level.IsSupport && level.Price <= price), true);
        AddNearestZone(levels.Where(level => !level.IsSupport && level.Price >= price), false);
        return zones;

        void AddNearestZone(IEnumerable<(double Price, bool IsSupport)> candidates, bool support)
        {
            var sorted = candidates.Select(level => level.Price).Order().ToArray();
            if (sorted.Length == 0)
                return;

            var clusters = new List<List<double>>();
            foreach (var level in sorted)
            {
                if (clusters.Count == 0 || level - clusters[^1][^1] > tolerance)
                    clusters.Add([level]);
                else
                    clusters[^1].Add(level);
            }

            var selected = support
                ? clusters[^1]
                : clusters[0];
            var low = selected.Min() - tolerance / 2d;
            var high = selected.Max() + tolerance / 2d;
            var touches = selected.Count;
            zones.Add(new PriceZone(
                support ? "Подкрепа" : "Съпротива",
                $"Swing зона · {touches} потвърдени докосвания",
                (decimal)low,
                (decimal)high,
                touches >= 3 ? "Силна" : touches == 2 ? "Умерена" : "Локална",
                support ? "support" : "resistance"));
        }
    }

    private static TimeSpan GetTimeframeDuration(string timeframe) => timeframe.ToUpperInvariant() switch
    {
        "M1" => TimeSpan.FromMinutes(1),
        "M5" => TimeSpan.FromMinutes(5),
        "M15" => TimeSpan.FromMinutes(15),
        "M30" => TimeSpan.FromMinutes(30),
        "H4" => TimeSpan.FromHours(4),
        "D1" => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(1)
    };

    private static IndicatorReading Unavailable(string name, int required, int actual) =>
        new(name, "—", $"Нужни са {required} затворени свещи · налични {actual}", 0, "neutral");

    private static IndicatorReading Reading(string name, string value, string state, int strength, string tone) =>
        new(name, value, state, Math.Clamp(strength, 0, 100), tone);

    private static string DirectionWord(int direction) => direction > 0 ? "възходящ" : "низходящ";

    private static string Tone(int direction) => direction > 0 ? "positive" : direction < 0 ? "negative" : "neutral";

    private static int Percent(double value) => double.IsFinite(value) ? (int)Math.Round(Math.Clamp(value, 0d, 100d)) : 0;

    private static string Format(double value) => value.ToString("N2", CultureInfo.CurrentCulture);

    private static string Signed(double value) =>
        (value >= 0d ? "+" : "") + value.ToString("N2", CultureInfo.CurrentCulture);

    private sealed record MacdValue(double Line, double Signal, double Histogram);
    private sealed record AdxValue(double Adx, double PlusDi, double MinusDi);
    private sealed record BollingerValue(double Middle, double Upper, double Lower);
}
