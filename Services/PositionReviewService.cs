using XYN_Trader.Models;

namespace XYN_Trader.Services;

/// <summary>
/// Builds transparent, indicator-based reviews for live MT5 positions.
/// Recommendations are advisory and are never sent to MT5 without an explicit user action.
/// </summary>
public sealed class PositionReviewService(
    Mt5MarketDataProvider marketProvider,
    TechnicalAnalysisService technicalAnalysis)
{
    private const int MinimumCandles = 60;
    private const decimal StopTrailAtrMultiple = 1.5m;
    private const decimal ProfitBeforeTrailingAtrMultiple = 1.25m;

    public async Task<PositionReviewsResult> GetReviewsAsync(CancellationToken cancellationToken = default)
    {
        var positionsResult = await marketProvider.GetOpenPositionsAsync(cancellationToken);
        if (!positionsResult.Available)
            return PositionReviewsResult.Unavailable(positionsResult.Message ?? "MT5 не върна отворените позиции.");

        if (positionsResult.Positions.Count == 0)
        {
            return new PositionReviewsResult(
                true,
                [],
                positionsResult.AccountCurrency,
                positionsResult.RetrievedAtUtc,
                null);
        }

        var symbols = positionsResult.Positions
            .Select(position => position.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var marketTasks = symbols.Select(async symbol =>
        {
            var result = await marketProvider.GetSymbolSnapshotAsync(symbol, "H1", cancellationToken);
            return (Symbol: symbol, Result: result);
        });
        var marketResults = await Task.WhenAll(marketTasks);
        var marketsBySymbol = marketResults.ToDictionary(
            item => item.Symbol,
            item => item.Result,
            StringComparer.OrdinalIgnoreCase);

        var reviews = new List<OpenPositionReview>(positionsResult.Positions.Count);
        foreach (var position in positionsResult.Positions)
        {
            var market = marketsBySymbol[position.Symbol];
            if (market.Snapshot is not { } snapshot)
            {
                reviews.Add(new OpenPositionReview(
                    position,
                    null,
                    MarketFreshness.Unavailable,
                    null,
                    null,
                    UnavailableAdvice(market.Message ?? "Няма пазарни данни за този символ."),
                    market.Message));
                continue;
            }

            var freshness = Mt5MarketDataProvider.GetFreshness(snapshot.UpdatedAtUtc, DateTimeOffset.UtcNow);
            var analysis = technicalAnalysis.Analyze(snapshot, "H1");
            var atr = CalculateAtr(snapshot.Candles);
            var advice = BuildAdvice(position, analysis, atr, snapshot, freshness);
            reviews.Add(new OpenPositionReview(
                position,
                analysis,
                freshness,
                snapshot.UpdatedAtUtc,
                atr,
                advice,
                freshness == MarketFreshness.Stale
                    ? "Последната котировка за този символ е остаряла. Препоръките са спрени до свежи данни."
                    : null));
        }

        return new PositionReviewsResult(
            true,
            reviews,
            positionsResult.AccountCurrency,
            positionsResult.RetrievedAtUtc,
            null);
    }

    public async Task<PositionActionResult> ApplySuggestedStopLossAsync(
        long ticket,
        decimal previouslySuggestedStopLoss,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetReviewsAsync(cancellationToken);
        if (!latest.Available)
            return new PositionActionResult(false, latest.Message ?? "Не успях да обновя позициите от MT5.");

        var review = latest.Reviews.FirstOrDefault(item => item.Position.Ticket == ticket);
        if (review is null)
            return new PositionActionResult(false, "Позицията вече не е отворена. Обнови таблото.");
        if (review.Advice.Kind != PositionAdviceKind.TightenStop ||
            review.Advice.SuggestedStopLoss != previouslySuggestedStopLoss)
        {
            return new PositionActionResult(
                false,
                "Пазарът или позицията се промени. Обнови анализа и потвърди актуалното предложение.");
        }

        return await marketProvider.ModifyPositionStopLossAsync(
            ticket,
            previouslySuggestedStopLoss,
            cancellationToken);
    }

    public async Task<PositionActionResult> ClosePositionAfterReviewAsync(
        long ticket,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetReviewsAsync(cancellationToken);
        if (!latest.Available)
            return new PositionActionResult(false, latest.Message ?? "Не успях да обновя позициите от MT5.");

        var review = latest.Reviews.FirstOrDefault(item => item.Position.Ticket == ticket);
        if (review is null)
            return new PositionActionResult(false, "Позицията вече не е отворена.");
        if (review.Advice.Kind != PositionAdviceKind.ConsiderClose)
        {
            return new PositionActionResult(
                false,
                "Последният анализ вече не дава силен сигнал срещу позицията. Обнови таблото.");
        }

        return await marketProvider.ClosePositionAsync(ticket, cancellationToken);
    }

    private static PositionAdvice BuildAdvice(
        Mt5OpenPosition position,
        AnalysisSnapshot analysis,
        decimal? atr,
        MarketSnapshot market,
        MarketFreshness freshness)
    {
        if (freshness != MarketFreshness.Fresh)
            return UnavailableAdvice("Котировката е остаряла или недостъпна; изчакай актуални MT5 данни.");
        if (market.Candles.Count < MinimumCandles || atr is null or <= 0m)
            return UnavailableAdvice("Няма достатъчно H1 история за надежден анализ и Stop Loss предложение.");
        if (position.Type is not ("BUY" or "SELL"))
            return UnavailableAdvice("Типът на позицията не е разпознат.");

        var isBuy = position.Type == "BUY";
        var strongOpposingSignal = isBuy
            ? analysis.TechnicalScore <= 35
            : analysis.TechnicalScore >= 65;
        if (strongOpposingSignal)
        {
            var technicalDirection = isBuy ? "низходящ" : "възходящ";
            return new PositionAdvice(
                PositionAdviceKind.ConsiderClose,
                "Силен сигнал срещу позицията",
                $"H1 техническият баланс е {analysis.TechnicalScore}/100 с {technicalDirection} импулс ({analysis.Regime}). Прегледай риска и структурата преди решение.",
                null);
        }

        var currentPrice = position.PriceCurrent > 0m ? position.PriceCurrent : market.Price;
        var favorableMove = isBuy
            ? currentPrice - position.PriceOpen
            : position.PriceOpen - currentPrice;
        if (favorableMove >= atr.Value * ProfitBeforeTrailingAtrMultiple)
        {
            if (!position.HasSymbolPrecisionMetadata)
            {
                return new PositionAdvice(
                    PositionAdviceKind.Hold,
                    "Изчакайте точността от MT5",
                    "Bridge-ът не върна точността и минималната стъпка на цената за този символ; затвори и стартирай отново mt5-bridge, преди да задаваш SL от таблото.",
                    null);
            }

            var candidate = isBuy
                ? Math.Max(position.PriceOpen + atr.Value * 0.1m, currentPrice - atr.Value * StopTrailAtrMultiple)
                : Math.Min(position.PriceOpen - atr.Value * 0.1m, currentPrice + atr.Value * StopTrailAtrMultiple);
            candidate = Math.Round(candidate, position.Digits, MidpointRounding.AwayFromZero);

            var minimumDistance = position.Point * Math.Max(2L, (long)position.StopsLevelPoints + 2L);
            var isValidSideOfMarket = isBuy
                ? candidate > 0m && currentPrice - candidate >= minimumDistance
                : candidate > currentPrice && candidate - currentPrice >= minimumDistance;
            var improvesExistingStop = position.StopLoss <= 0m || (isBuy
                ? candidate > position.StopLoss + position.Point
                : candidate < position.StopLoss - position.Point);

            if (isValidSideOfMarket && improvesExistingStop)
            {
                return new PositionAdvice(
                    PositionAdviceKind.TightenStop,
                    "Предложение за по-защитен Stop Loss",
                    $"Позицията се движи поне {ProfitBeforeTrailingAtrMultiple:0.##} ATR в твоя полза. Предложеното ниво следва H1 волатилността и само затяга текущия риск.",
                    candidate);
            }
        }

        if ((isBuy && analysis.TechnicalScore < 45) || (!isBuy && analysis.TechnicalScore > 55))
        {
            return new PositionAdvice(
                PositionAdviceKind.Hold,
                "Наблюдавай посоката",
                $"Има технически натиск срещу позицията ({analysis.TechnicalScore}/100), но той не достига прага за силен сигнал за затваряне. Провери отново при следващите H1 данни.",
                null);
        }

        var alignment = (isBuy && analysis.TechnicalScore >= 55) || (!isBuy && analysis.TechnicalScore <= 45)
            ? "H1 техническата картина подкрепя посоката на позицията"
            : "H1 техническата картина е смесена";
        return new PositionAdvice(
            PositionAdviceKind.Hold,
            "Задръж и наблюдавай",
            $"{alignment} ({analysis.TechnicalScore}/100; {analysis.Regime}). Текущият Stop Loss не се променя автоматично.",
            null);
    }

    private static PositionAdvice UnavailableAdvice(string reason) =>
        new(PositionAdviceKind.Unavailable, "Няма надеждна препоръка", reason, null);

    private static decimal? CalculateAtr(IReadOnlyList<Candle> candles)
    {
        const int period = 14;
        var ordered = candles.OrderBy(candle => candle.TimeUtc).ToArray();
        if (ordered.Length < period)
            return null;

        var ranges = new decimal[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            if (index == 0)
            {
                ranges[index] = ordered[index].High - ordered[index].Low;
                continue;
            }

            var previousClose = ordered[index - 1].Close;
            ranges[index] = Math.Max(
                ordered[index].High - ordered[index].Low,
                Math.Max(Math.Abs(ordered[index].High - previousClose), Math.Abs(ordered[index].Low - previousClose)));
        }

        var atr = ranges.Take(period).Average();
        for (var index = period; index < ranges.Length; index++)
            atr = (atr * (period - 1) + ranges[index]) / period;
        return atr;
    }
}
