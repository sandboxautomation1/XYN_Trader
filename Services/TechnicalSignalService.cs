using XYN_Trader.Models;

namespace XYN_Trader.Services;

/// <summary>
/// Builds an explainable, local technical signal from the selected MT5 timeframe.
/// The score is an indicator vote balance, not a calibrated probability.
/// </summary>
public sealed class TechnicalSignalService(TechnicalAnalysisService technicalAnalysis)
{
    private const int AtrPeriod = 14;

    public TechnicalSignalSnapshot Analyze(MarketSnapshot market, string timeframe)
    {
        ArgumentNullException.ThrowIfNull(market);

        var analysis = technicalAnalysis.Analyze(market, timeframe);
        var direction = analysis.TechnicalScore >= 55
            ? "BUY"
            : analysis.TechnicalScore <= 45
                ? "SELL"
                : "NEUTRAL";

        var bullish = new List<SignalFactor>();
        var bearish = new List<SignalFactor>();
        var risks = new List<SignalFactor>();

        foreach (var indicator in analysis.Indicators)
        {
            if (indicator.Value == "—")
                continue;

            var factor = new SignalFactor(
                indicator.Name,
                $"{indicator.Value} · {indicator.State}",
                ToStrength(indicator.StrengthPercent));

            if (indicator.Name.StartsWith("ATR", StringComparison.Ordinal))
            {
                if (indicator.State.Contains("повишена", StringComparison.OrdinalIgnoreCase))
                    risks.Add(factor with { Label = "Повишена волатилност" });
                continue;
            }

            if (indicator.Tone == "positive")
                bullish.Add(factor);
            else if (indicator.Tone == "negative")
                bearish.Add(factor);
            else
                risks.Add(factor with { Label = $"Неутрален фактор · {factor.Label}" });
        }

        IEnumerable<SignalFactor> opposingFactors = direction switch
        {
            "BUY" => bearish,
            "SELL" => bullish,
            _ => Array.Empty<SignalFactor>()
        };
        foreach (var factor in opposingFactors)
        {
            risks.Add(new SignalFactor(
                $"Противоречие · {factor.Label}",
                factor.Explanation,
                factor.Strength));
        }

        if (direction == "NEUTRAL")
        {
            risks.Add(new SignalFactor(
                "Липсва ясен превес",
                "Индикаторите не формират достатъчно силно съгласие за посока.",
                1));
        }

        var atr = CalculateAtr(market.Candles);
        decimal? stopLoss = null;
        decimal? takeProfit = null;
        decimal? rewardRisk = null;
        if (direction != "NEUTRAL" && market.Price > 0m)
        {
            var stopDistance = Math.Max(atr * 1.5m, market.Price * 0.001m);
            var targetDistance = stopDistance * 2m;
            stopLoss = Round(direction == "BUY"
                ? market.Price - stopDistance
                : market.Price + stopDistance);
            takeProfit = Round(direction == "BUY"
                ? market.Price + targetDistance
                : market.Price - targetDistance);
            rewardRisk = 2m;
        }

        var summary = direction switch
        {
            "BUY" => $"Индикаторният баланс е възходящ ({analysis.TechnicalScore}/100) за {DisplayTimeframe(timeframe)}.",
            "SELL" => $"Индикаторният баланс е низходящ ({analysis.TechnicalScore}/100) за {DisplayTimeframe(timeframe)}.",
            _ => $"Индикаторите са смесени ({analysis.TechnicalScore}/100) за {DisplayTimeframe(timeframe)}; няма ясно изразена посока."
        };

        return new TechnicalSignalSnapshot(
            market.Symbol,
            timeframe.ToUpperInvariant(),
            DateTimeOffset.UtcNow,
            direction,
            analysis.TechnicalScore,
            analysis.RegimeConfidencePercent,
            Round(market.Price),
            stopLoss,
            takeProfit,
            rewardRisk,
            summary,
            market.Candles.Count,
            bullish,
            bearish,
            risks);
    }

    private static decimal CalculateAtr(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < AtrPeriod)
            return 0m;

        var ordered = candles.OrderBy(candle => candle.TimeUtc).ToArray();
        var trueRanges = new decimal[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            var current = ordered[index];
            if (index == 0)
            {
                trueRanges[index] = current.High - current.Low;
                continue;
            }

            var previousClose = ordered[index - 1].Close;
            trueRanges[index] = Math.Max(
                current.High - current.Low,
                Math.Max(Math.Abs(current.High - previousClose), Math.Abs(current.Low - previousClose)));
        }

        var atr = trueRanges.Take(AtrPeriod).Average();
        for (var index = AtrPeriod; index < trueRanges.Length; index++)
            atr = (atr * (AtrPeriod - 1) + trueRanges[index]) / AtrPeriod;
        return atr;
    }

    private static int ToStrength(int percent) => percent >= 80 ? 3 : percent >= 55 ? 2 : 1;

    private static decimal Round(decimal price) => Math.Round(price, 2, MidpointRounding.AwayFromZero);

    private static string DisplayTimeframe(string timeframe) => timeframe.ToUpperInvariant() switch
    {
        "M5" => "5m",
        "M15" => "15m",
        "H1" => "1h",
        "D1" => "1d",
        _ => timeframe
    };
}
