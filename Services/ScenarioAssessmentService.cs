using System.Globalization;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

/// <summary>
/// Evaluates the workbook's XAUUSD scenarios against recent MT5 candles.
/// Signals here are informational; this service never submits orders.
/// </summary>
public sealed class ScenarioAssessmentService(
    Mt5MarketDataProvider marketProvider,
    IConfiguration configuration)
{
    private const int CandleCount = 300;
    private const int EmaShortPeriod = 20;
    private const int EmaLongPeriod = 50;
    private const int EmaTrendPeriod = 200;
    private const int IndicatorPeriod = 14;

    private static readonly ScenarioDefinition[] GeneralScenarios =
    [
        new("bull-trend-continuation", "Продължение на възходящ тренд"),
        new("bear-trend-continuation", "Продължение на низходящ тренд"),
        new("consolidation-breakout", "Пробив от консолидация"),
        new("false-breakout-range", "Фалшив пробив и връщане в диапазона"),
        new("overextended-trend", "Тренд, но прекомерно разтеглен"),
        new("rsi-macd-divergence", "Дивергенция между цена и RSI/MACD"),
        new("range-mean-reversion", "Връщане към средната стойност в диапазон"),
        new("dxy-yield-confirmed-breakout", "Пробив с потвърждение от DXY и доходността"),
        new("news-spike-stabilization", "Новина, spike и стабилизиране")
    ];

    private static readonly ScenarioDefinition[] ShortTermScenarios =
    [
        new("m5-upside-retest", "Пробив нагоре и повторен тест — 5 минути"),
        new("m5-downside-retest", "Пробив надолу и повторен тест — 5 минути"),
        new("m5-false-breakout", "Фалшив пробив — 5 минути")
    ];

    public async Task<ScenarioAssessmentReport> AssessAsync(CancellationToken cancellationToken = default)
    {
        var symbol = GetSetting("Scenarios:Symbol", configuration["MT5Bridge:Symbol"] ?? "XAUUSD");
        var generalTimeframe = NormalizeTimeframe(configuration["Scenarios:GeneralTimeframe"], "H1");
        var shortTimeframe = NormalizeTimeframe(configuration["Scenarios:ShortTermTimeframe"], "M5");
        var dxySymbol = GetSetting("Scenarios:DxySymbol", "DXY");
        var yieldSymbol = GetSetting("Scenarios:YieldSymbol", "US10Y");

        var generalTask = marketProvider.GetSignalSnapshotAsync(symbol, generalTimeframe, CandleCount, cancellationToken);
        var shortTask = marketProvider.GetSignalSnapshotAsync(symbol, shortTimeframe, CandleCount, cancellationToken);
        var dxyTask = marketProvider.GetSignalSnapshotAsync(dxySymbol, generalTimeframe, CandleCount, cancellationToken);
        var yieldTask = marketProvider.GetSignalSnapshotAsync(yieldSymbol, generalTimeframe, CandleCount, cancellationToken);
        await Task.WhenAll(generalTask, shortTask, dxyTask, yieldTask);

        var generalResult = await generalTask;
        var shortResult = await shortTask;
        var dxyResult = await dxyTask;
        var yieldResult = await yieldTask;

        var general = TryCreateContext(generalResult, generalTimeframe, out var generalMessage);
        var shortTerm = TryCreateContext(shortResult, shortTimeframe, out var shortMessage);
        var dxy = TryCreateContext(dxyResult, generalTimeframe, out var dxyMessage);
        var yield = TryCreateContext(yieldResult, generalTimeframe, out var yieldMessage);

        var assessments = new List<ScenarioAssessmentResult>(12);
        if (general is null)
        {
            assessments.AddRange(GeneralScenarios.Select(definition => NoData(definition, generalTimeframe, generalMessage)));
        }
        else
        {
            assessments.Add(EvaluateTrendContinuation(GeneralScenarios[0], general, bullish: true));
            assessments.Add(EvaluateTrendContinuation(GeneralScenarios[1], general, bullish: false));
            assessments.Add(EvaluateConsolidationBreakout(GeneralScenarios[2], general));
            assessments.Add(EvaluateFalseBreakout(GeneralScenarios[3], general, generalTimeframe));
            assessments.Add(EvaluateOverextendedTrend(GeneralScenarios[4], general));
            assessments.Add(EvaluateDivergence(GeneralScenarios[5], general));
            assessments.Add(EvaluateMeanReversion(GeneralScenarios[6], general));
            assessments.Add(EvaluateCrossMarketBreakout(
                GeneralScenarios[7], general, dxy, dxyMessage, yield, yieldMessage));
            assessments.Add(EvaluateSpikeStabilization(GeneralScenarios[8], general));
        }

        if (shortTerm is null)
        {
            assessments.AddRange(ShortTermScenarios.Select(definition => NoData(definition, shortTimeframe, shortMessage)));
        }
        else
        {
            assessments.Add(EvaluateShortTermRetest(ShortTermScenarios[0], shortTerm, bullish: true));
            assessments.Add(EvaluateShortTermRetest(ShortTermScenarios[1], shortTerm, bullish: false));
            assessments.Add(EvaluateFalseBreakout(ShortTermScenarios[2], shortTerm, shortTimeframe));
        }

        return new ScenarioAssessmentReport(DateTimeOffset.UtcNow, symbol, assessments);
    }

    private static ScenarioAssessmentResult EvaluateTrendContinuation(
        ScenarioDefinition definition,
        MarketContext context,
        bool bullish)
    {
        var direction = bullish ? "BUY" : "SELL";
        var trendAligned = bullish
            ? context.Price > context.Latest(context.Ema200) &&
              context.Latest(context.Ema20) > context.Latest(context.Ema50) &&
              context.Slope(context.Ema20, 5) > 0 && context.Slope(context.Ema50, 5) > 0 &&
              context.Latest(context.Adx) >= 20 && context.Latest(context.Rsi) > 50
            : context.Price < context.Latest(context.Ema200) &&
              context.Latest(context.Ema20) < context.Latest(context.Ema50) &&
              context.Slope(context.Ema20, 5) < 0 && context.Slope(context.Ema50, 5) < 0 &&
              context.Latest(context.Adx) >= 20 && context.Latest(context.Rsi) < 50;

        if (!trendAligned)
            return Pending(definition, context, "Изчаква условията", "WAIT",
                $"Не са потвърдени EMA 200/20/50, наклонът, ADX ≥ 20 и RSI {(bullish ? "> 50" : "< 50")} за възходящо/низходящо продължение.");

        if (!HasPullbackToEma(context, 8))
            return Pending(definition, context, "Изчаква връщане", "WAIT",
                "Трендовите филтри са изпълнени; цената още не е тествала EMA 20/50 в последните 8 затворени свещи.");

        var confirmed = bullish ? HasBullishStructureBreak(context, 3) : HasBearishStructureBreak(context, 3);
        return confirmed
            ? Confirmed(definition, context, direction,
                $"Трендът, връщането към EMA 20/50 и затварянето над/под локалната структура са потвърдени на {context.Timeframe}.")
            : Pending(definition, context, "Изчаква потвърждение", "WAIT",
                "Има връщане към EMA 20/50, но няма затваряне отвъд краткосрочната структура.");
    }

    private static ScenarioAssessmentResult EvaluateConsolidationBreakout(
        ScenarioDefinition definition,
        MarketContext context)
    {
        var range = context.RangeBeforeLast(20);
        var atr = context.Latest(context.Atr);
        var atrRatio = context.AtrRatio(20);
        var compressed = range.Width <= atr * 6 &&
            context.Latest(context.Adx) < 25 &&
            (double.IsFinite(atrRatio) ? atrRatio <= 1.05 : false) &&
            Math.Abs(context.Slope(context.Ema20, 5)) <= atr * 0.6;

        if (!compressed)
            return Pending(definition, context, "Изчаква консолидация", "WAIT",
                "MT5 не показва едновременно тесен диапазон, почти хоризонтална EMA 20, ADX под 25 и свиващ се ATR.");

        if (TryFindBreakoutRetest(context, 20, 8, 1.2, out var direction, out var reason))
            return Confirmed(definition, context, direction, reason);

        return Pending(definition, context, "Изчаква пробив и ретест", "WAIT",
            "Има свита консолидация; липсва затваряне извън диапазона с повишена tick активност и последващ успешен ретест.");
    }

    private static ScenarioAssessmentResult EvaluateFalseBreakout(
        ScenarioDefinition definition,
        MarketContext context,
        string timeframe)
    {
        var bars = context.Bars;
        var firstCandidate = Math.Max(25, bars.Length - 8);
        var foundSetup = false;

        for (var index = firstCandidate; index < bars.Length - 1; index++)
        {
            var reference = context.RangeBefore(index, 20);
            var candle = bars[index];
            var body = Math.Max(Math.Abs((double)(candle.Close - candle.Open)), context.Latest(context.Atr) * 0.05);
            var upperWick = (double)(candle.High - Math.Max(candle.Open, candle.Close));
            var lowerWick = (double)(Math.Min(candle.Open, candle.Close) - candle.Low);
            var falseUp = (double)candle.High > reference.High && (double)candle.Close < reference.High && upperWick >= body;
            var falseDown = (double)candle.Low < reference.Low && (double)candle.Close > reference.Low && lowerWick >= body;
            if (!falseUp && !falseDown)
                continue;

            foundSetup = true;
            var hasStructureBreak = falseUp
                ? HasBearishStructureBreak(context, 3, fromIndex: index + 1)
                : HasBullishStructureBreak(context, 3, fromIndex: index + 1);
            var hasMomentumWeakness = falseUp
                ? context.MacdDeclining(index)
                : context.MacdRising(index);
            if (hasStructureBreak && hasMomentumWeakness)
            {
                var signal = falseUp ? "SELL" : "BUY";
                return Confirmed(definition, context, signal,
                    $"Фалшивият пробив е върнат в диапазона; има отхвърляне, отслабващ MACD и обратен структурен пробив на {timeframe}.");
            }
        }

        return foundSetup
            ? Pending(definition, context, "Изчаква структурно потвърждение", "WAIT",
                "Цената се е върнала в диапазона след фитил извън границата; чака се отслабване на импулса и пробив на вътрешната структура.")
            : Pending(definition, context, "Изчаква фалшив пробив", "WAIT",
                "В последните затворени свещи няма фитил отвъд 20-свещния диапазон със затваряне обратно вътре.");
    }

    private static ScenarioAssessmentResult EvaluateOverextendedTrend(
        ScenarioDefinition definition,
        MarketContext context)
    {
        var atr = context.Latest(context.Atr);
        var distance = context.Price - context.Latest(context.Ema20);
        var extendedUp = distance > atr * 1.5 && context.Latest(context.Rsi) > 70;
        var extendedDown = distance < -atr * 1.5 && context.Latest(context.Rsi) < 30;
        var volatilityExpanded = context.AtrRatio(20) >= 1.2;
        var weakening = context.MacdContracting();

        if ((extendedUp || extendedDown) && volatilityExpanded && weakening)
        {
            var reversal = extendedUp && (double)context.Bars[^1].Close < context.Latest(context.Ema20) &&
                           HasBearishStructureBreak(context, 3)
                ? "SELL"
                : extendedDown && (double)context.Bars[^1].Close > context.Latest(context.Ema20) &&
                  HasBullishStructureBreak(context, 3)
                    ? "BUY"
                    : "WAIT";
            var reason = $"Цената е над 1.5 ATR от EMA 20, RSI е в крайна зона, ATR е повишен и MACD отслабва.{(reversal == "WAIT" ? " Не е потвърден обратен структурен пробив." : " Има затваряне през EMA 20 и обратен структурен пробив.")}";
            return reversal == "WAIT"
                ? Pending(definition, context, "Разтеглен тренд — повишен риск", reversal, reason)
                : Confirmed(definition, context, reversal, reason);
        }

        return Pending(definition, context, "Изчаква прекомерно разтягане", "WAIT",
            "Не са изпълнени заедно отклонение над 1.5 ATR от EMA 20, RSI над 70/под 30, повишен ATR и отслабване на MACD.");
    }

    private static ScenarioAssessmentResult EvaluateDivergence(
        ScenarioDefinition definition,
        MarketContext context)
    {
        var swingHighs = FindPivots(context.Bars, high: true, lookback: 60);
        var swingLows = FindPivots(context.Bars, high: false, lookback: 60);
        var bearish = HasBearishDivergence(context, swingHighs);
        var bullish = HasBullishDivergence(context, swingLows);

        if (bearish && HasBearishStructureBreak(context, 3))
            return Confirmed(definition, context, "SELL",
                "Цената е направила по-висок връх, RSI и MACD са отслабнали, а последната затворена свещ е пробила локалната подкрепа.");
        if (bullish && HasBullishStructureBreak(context, 3))
            return Confirmed(definition, context, "BUY",
                "Цената е направила по-ниско дъно, RSI и MACD са се повишили, а последната затворена свещ е пробила локалната съпротива.");
        if (bearish || bullish)
            return Pending(definition, context, "Изчаква структурно потвърждение", "WAIT",
                $"Открита е {(bearish ? "меча" : "бича")} дивергенция; последната затворена свещ още не е пробила обратната локална структура.");

        return Pending(definition, context, "Изчаква дивергенция", "WAIT",
            "Сред последните локални върхове/дъна няма едновременно несъответствие в цената, RSI и MACD.");
    }

    private static ScenarioAssessmentResult EvaluateMeanReversion(
        ScenarioDefinition definition,
        MarketContext context)
    {
        var range = context.RangeBeforeLast(40);
        var atr = context.Latest(context.Atr);
        var rangeMode = context.Latest(context.Adx) < 20 &&
            Math.Abs(context.Latest(context.Ema20) - context.Latest(context.Ema50)) <= atr * 0.5 &&
            range.Width <= atr * 10 && context.Latest(context.Rsi) is >= 30 and <= 70;
        if (!rangeMode)
            return Pending(definition, context, "Изчаква диапазон", "WAIT",
                "Не са налице едновременно ADX под 20, близки EMA 20/50, RSI между 30–70 и ясно ограничен диапазон.");

        var last = context.Bars[^1];
        var tolerance = atr * 0.5;
        if ((double)last.Low <= range.Low + tolerance && (double)last.Close > range.Low && last.Close > last.Open)
            return Confirmed(definition, context, "BUY",
                "Цената е реагирала бичо от долната граница на диапазона; начална цел е средата на диапазона.");
        if ((double)last.High >= range.High - tolerance && (double)last.Close < range.High && last.Close < last.Open)
            return Confirmed(definition, context, "SELL",
                "Цената е реагирала мечо от горната граница на диапазона; начална цел е средата на диапазона.");

        return Pending(definition, context, "Изчаква реакция от граница", "WAIT",
            "Диапазонният режим е налице, но последната затворена свещ не показва потвърдена реакция от горната или долната му граница.");
    }

    private static ScenarioAssessmentResult EvaluateCrossMarketBreakout(
        ScenarioDefinition definition,
        MarketContext gold,
        MarketContext? dxy,
        string dxyMessage,
        MarketContext? yield,
        string yieldMessage)
    {
        if (dxy is null || yield is null)
            return Pending(definition, gold, "Няма MT5 данни за DXY/US10Y", "WAIT",
                $"Този сценарий изисква XAUUSD, DXY и US10Y от MT5. DXY: {dxyMessage} US10Y: {yieldMessage}");

        var range = gold.RangeBeforeLast(20);
        var last = gold.Bars[^1];
        var bullish = (double)last.Close > range.High &&
            dxy.Price < dxy.Latest(dxy.Ema20) && dxy.Latest(dxy.Ema20) <= dxy.Latest(dxy.Ema50) &&
            yield.Price < yield.Latest(yield.Ema20) && yield.Latest(yield.Ema20) <= yield.Latest(yield.Ema50);
        var bearish = (double)last.Close < range.Low &&
            dxy.Price > dxy.Latest(dxy.Ema20) && dxy.Latest(dxy.Ema20) >= dxy.Latest(dxy.Ema50) &&
            yield.Price > yield.Latest(yield.Ema20) && yield.Latest(yield.Ema20) >= yield.Latest(yield.Ema50);

        if (bullish)
            return Confirmed(definition, gold, "BUY",
                "XAUUSD затваря над 20-свещната съпротива; DXY и US10Y са под EMA 20 и EMA 20 е под EMA 50.");
        if (bearish)
            return Confirmed(definition, gold, "SELL",
                "XAUUSD затваря под 20-свещната подкрепа; DXY и US10Y са над EMA 20 и EMA 20 е над EMA 50.");

        return Pending(definition, gold, "Изчаква външно потвърждение", "WAIT",
            "XAUUSD няма пробив, подкрепен едновременно от обратната посока на DXY и US10Y.");
    }

    private static ScenarioAssessmentResult EvaluateSpikeStabilization(
        ScenarioDefinition definition,
        MarketContext context)
    {
        var atrRatio = context.AtrRatio(20);
        var lastRange = (double)(context.Bars[^1].High - context.Bars[^1].Low);
        var spikeNow = atrRatio >= 1.6 || lastRange >= context.Latest(context.Atr) * 2.2;
        if (spikeNow)
            return Pending(definition, context, "Няма позиция около spike", "WAIT",
                "MT5 показва рязко увеличение на ATR/диапазона. Изчаква се първоначалният импулс да приключи и да се оформи нов диапазон.");

        var recentSpike = context.Atr.Skip(Math.Max(0, context.Atr.Length - 9)).Any(value =>
            double.IsFinite(value) && value >= context.AverageRecentAtr(20) * 1.6);
        if (recentSpike && atrRatio <= 1.4 && TryFindBreakoutRetest(context, 12, 7, 1.0, out var direction, out var reason))
            return Confirmed(definition, context, direction,
                $"След скорошна волатилност има по-стабилен ATR и потвърден пробив/ретест. {reason}");

        return Pending(definition, context, "Изчаква стабилизация", "WAIT",
            "Няма скорошен spike със стабилизиран ATR и потвърдено задържане/повторен тест на новото ниво.");
    }

    private static ScenarioAssessmentResult EvaluateShortTermRetest(
        ScenarioDefinition definition,
        MarketContext context,
        bool bullish)
    {
        var trend = bullish
            ? context.Latest(context.Ema20) > context.Latest(context.Ema50) && context.Latest(context.Rsi) > 50
            : context.Latest(context.Ema20) < context.Latest(context.Ema50) && context.Latest(context.Rsi) < 50;
        if (!trend)
            return Pending(definition, context, "Изчаква M5 посока", "WAIT",
                $"На M5 EMA 20/50 и RSI не потвърждават {(bullish ? "възход" : "спад")}.");

        if (TryFindBreakoutRetest(context, 20, 8, 1.0, out var direction, out var reason) &&
            direction == (bullish ? "BUY" : "SELL"))
            return Confirmed(definition, context, direction,
                $"M5 пробивът, повторният тест и затварянето в посоката са потвърдени. {reason}");

        return Pending(definition, context, "Изчаква пробив и M5 ретест", "WAIT",
            "Няма последователност от затваряне отвъд нивото, успешен повторен тест и последващо потвърждаващо затваряне.");
    }

    private static bool TryFindBreakoutRetest(
        MarketContext context,
        int lookback,
        int maxAge,
        double minimumVolumeRatio,
        out string direction,
        out string reason)
    {
        direction = "WAIT";
        reason = string.Empty;
        var bars = context.Bars;
        var first = Math.Max(lookback, bars.Length - maxAge - 2);
        var tolerance = context.Latest(context.Atr) * 0.25;

        for (var breakoutIndex = first; breakoutIndex < bars.Length - 2; breakoutIndex++)
        {
            var prior = context.RangeBefore(breakoutIndex, lookback);
            var volumeRatio = context.VolumeRatio(breakoutIndex, 20);
            if (volumeRatio is null || volumeRatio.Value < minimumVolumeRatio)
                continue;

            var bullishBreakout = (double)bars[breakoutIndex].Close > prior.High;
            var bearishBreakout = (double)bars[breakoutIndex].Close < prior.Low;
            if (!bullishBreakout && !bearishBreakout)
                continue;

            for (var retestIndex = breakoutIndex + 1; retestIndex < bars.Length - 1; retestIndex++)
            {
                var retest = bars[retestIndex];
                var held = bullishBreakout
                    ? (double)retest.Low <= prior.High + tolerance && (double)retest.Close >= prior.High
                    : (double)retest.High >= prior.Low - tolerance && (double)retest.Close <= prior.Low;
                if (!held)
                    continue;

                var confirmation = bars[^1];
                var confirmed = bullishBreakout
                    ? confirmation.Close > retest.High
                    : confirmation.Close < retest.Low;
                if (!confirmed)
                    continue;

                direction = bullishBreakout ? "BUY" : "SELL";
                reason = $"Пробивът е потвърден с tick volume {volumeRatio.Value.ToString("0.00", CultureInfo.InvariantCulture)}× спрямо средното и ретестът е задържал нивото.";
                return true;
            }
        }

        return false;
    }

    private static bool HasPullbackToEma(MarketContext context, int lookback)
    {
        var tolerance = context.Latest(context.Atr) * 0.35;
        var start = Math.Max(0, context.Bars.Length - lookback);
        for (var index = start; index < context.Bars.Length; index++)
        {
            var candle = context.Bars[index];
            if (Touches(candle, context.Ema20[index], tolerance) || Touches(candle, context.Ema50[index], tolerance))
                return true;
        }
        return false;
    }

    private static bool HasBullishStructureBreak(MarketContext context, int lookback, int fromIndex = -1)
    {
        var lastIndex = context.Bars.Length - 1;
        var start = Math.Max(0, fromIndex >= 0 ? fromIndex : lastIndex - lookback);
        if (start >= lastIndex)
            return false;
        var priorHigh = context.Bars.Skip(start).Take(lastIndex - start).Max(candle => candle.High);
        return context.Bars[lastIndex].Close > priorHigh && context.Bars[lastIndex].Close > context.Bars[lastIndex].Open;
    }

    private static bool HasBearishStructureBreak(MarketContext context, int lookback, int fromIndex = -1)
    {
        var lastIndex = context.Bars.Length - 1;
        var start = Math.Max(0, fromIndex >= 0 ? fromIndex : lastIndex - lookback);
        if (start >= lastIndex)
            return false;
        var priorLow = context.Bars.Skip(start).Take(lastIndex - start).Min(candle => candle.Low);
        return context.Bars[lastIndex].Close < priorLow && context.Bars[lastIndex].Close < context.Bars[lastIndex].Open;
    }

    private static bool HasBearishDivergence(MarketContext context, IReadOnlyList<int> pivots)
    {
        if (pivots.Count < 2)
            return false;
        var older = pivots[^2];
        var newer = pivots[^1];
        return context.Bars[newer].High > context.Bars[older].High &&
            context.Rsi[newer] < context.Rsi[older] - 2 &&
            context.MacdHistogram[newer] < context.MacdHistogram[older];
    }

    private static bool HasBullishDivergence(MarketContext context, IReadOnlyList<int> pivots)
    {
        if (pivots.Count < 2)
            return false;
        var older = pivots[^2];
        var newer = pivots[^1];
        return context.Bars[newer].Low < context.Bars[older].Low &&
            context.Rsi[newer] > context.Rsi[older] + 2 &&
            context.MacdHistogram[newer] > context.MacdHistogram[older];
    }

    private static IReadOnlyList<int> FindPivots(IReadOnlyList<Candle> bars, bool high, int lookback)
    {
        var start = Math.Max(2, bars.Count - lookback);
        var pivots = new List<int>();
        for (var index = start; index < bars.Count - 2; index++)
        {
            var value = high ? bars[index].High : bars[index].Low;
            var isPivot = high
                ? value > bars[index - 1].High && value > bars[index - 2].High &&
                  value >= bars[index + 1].High && value >= bars[index + 2].High
                : value < bars[index - 1].Low && value < bars[index - 2].Low &&
                  value <= bars[index + 1].Low && value <= bars[index + 2].Low;
            if (isPivot)
                pivots.Add(index);
        }
        return pivots;
    }

    private static bool Touches(Candle candle, double level, double tolerance) =>
        double.IsFinite(level) && (double)candle.Low <= level + tolerance && (double)candle.High >= level - tolerance;

    private static MarketContext? TryCreateContext(
        MarketDataResult result,
        string timeframe,
        out string message)
    {
        message = result.Message ?? "MT5 не върна пазарни данни.";
        if (result.Snapshot is null)
            return null;
        if (result.Freshness != MarketFreshness.Fresh)
        {
            message = result.Message ?? "Последната котировка от MT5 е остаряла.";
            return null;
        }

        var duration = Duration(timeframe);
        var now = DateTimeOffset.UtcNow;
        var quoteAge = now - result.Snapshot.UpdatedAtUtc;
        var maxQuoteAge = timeframe.Equals("M5", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMinutes(10)
            : TimeSpan.FromMinutes(15);
        if (quoteAge > maxQuoteAge || quoteAge < TimeSpan.FromMinutes(-1))
        {
            message = "Последната котировка от MT5 е остаряла или има невалиден времеви печат.";
            return null;
        }
        var bars = result.Snapshot.Candles
            .Where(IsValidCandle)
            .Where(candle => candle.TimeUtc + duration <= now)
            .OrderBy(candle => candle.TimeUtc)
            .ToArray();
        if (bars.Length < EmaTrendPeriod)
        {
            message = $"Налични са {bars.Length} затворени свещи; нужни са поне {EmaTrendPeriod} за EMA 200.";
            return null;
        }

        var closes = bars.Select(candle => (double)candle.Close).ToArray();
        var context = new MarketContext(
            result.Snapshot,
            timeframe,
            bars,
            EmaSeries(closes, EmaShortPeriod),
            EmaSeries(closes, EmaLongPeriod),
            EmaSeries(closes, EmaTrendPeriod),
            RsiSeries(closes, IndicatorPeriod),
            AtrSeries(bars, IndicatorPeriod),
            AdxSeries(bars, IndicatorPeriod),
            MacdHistogramSeries(closes, 12, 26, 9));
        if (!double.IsFinite(context.Latest(context.Ema200)) || !double.IsFinite(context.Latest(context.Adx)) ||
            !double.IsFinite(context.Latest(context.Rsi)) || !double.IsFinite(context.Latest(context.Atr)))
        {
            message = "Няма достатъчно валидни MT5 индикаторни стойности за оценката.";
            return null;
        }

        message = string.Empty;
        return context;
    }

    private static ScenarioAssessmentResult NoData(ScenarioDefinition definition, string timeframe, string reason) =>
        new(definition.Id, definition.Name, timeframe, "Няма данни от MT5", "WAIT", reason, null, null);

    private static ScenarioAssessmentResult Pending(
        ScenarioDefinition definition,
        MarketContext context,
        string status,
        string signal,
        string reason) =>
        new(definition.Id, definition.Name, context.Timeframe, status, signal, reason,
            context.Market.Price, context.Market.UpdatedAtUtc);

    private static ScenarioAssessmentResult Confirmed(
        ScenarioDefinition definition,
        MarketContext context,
        string signal,
        string reason)
    {
        var levels = CalculateTradeLevels(context, signal);
        return new ScenarioAssessmentResult(
            definition.Id,
            definition.Name,
            context.Timeframe,
            "Потвърден сигнал",
            signal,
            reason,
            context.Market.Price,
            context.Market.UpdatedAtUtc,
            levels?.Entry,
            levels?.StopLoss,
            levels?.TakeProfit);
    }

    private static TradeLevels? CalculateTradeLevels(MarketContext context, string signal)
    {
        var atrValue = context.Latest(context.Atr);
        if (!double.IsFinite(atrValue) || atrValue <= 0 || context.Market.Price <= 0)
            return null;

        var atr = (decimal)atrValue;
        var isBuy = signal.Equals("BUY", StringComparison.OrdinalIgnoreCase);
        if (!isBuy && !signal.Equals("SELL", StringComparison.OrdinalIgnoreCase))
            return null;

        var entry = isBuy
            ? context.Market.Ask.GetValueOrDefault() > 0m ? context.Market.Ask!.Value : context.Market.Price
            : context.Market.Bid.GetValueOrDefault() > 0m ? context.Market.Bid!.Value : context.Market.Price;
        var lookback = context.Timeframe.Equals("M5", StringComparison.OrdinalIgnoreCase) ? 12 : 10;
        var recentBars = context.Bars.TakeLast(lookback).ToArray();
        if (recentBars.Length == 0)
            return null;

        var swingBuffer = atr * 0.2m;
        var minimumRisk = atr * 1.5m;
        decimal stopLoss;
        if (isBuy)
        {
            var swingStop = recentBars.Min(candle => candle.Low) - swingBuffer;
            stopLoss = Math.Min(entry - minimumRisk, swingStop);
        }
        else
        {
            var swingStop = recentBars.Max(candle => candle.High) + swingBuffer;
            stopLoss = Math.Max(entry + minimumRisk, swingStop);
        }

        var risk = Math.Abs(entry - stopLoss);
        if (risk <= 0m)
            return null;

        var takeProfit = isBuy ? entry + risk * 2m : entry - risk * 2m;
        return new TradeLevels(entry, stopLoss, takeProfit);
    }

    private static bool IsValidCandle(Candle candle) =>
        candle.Low <= Math.Min(candle.Open, candle.Close) &&
        candle.High >= Math.Max(candle.Open, candle.Close) &&
        candle.Low <= candle.High;

    private string GetSetting(string key, string fallback) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? fallback.Trim() : configuration[key]!.Trim();

    private static string NormalizeTimeframe(string? value, string fallback)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized is "M1" or "M5" or "M15" or "M30" or "H1" or "H4" or "D1"
            ? normalized
            : fallback;
    }

    private static TimeSpan Duration(string timeframe) => timeframe.ToUpperInvariant() switch
    {
        "M1" => TimeSpan.FromMinutes(1),
        "M5" => TimeSpan.FromMinutes(5),
        "M15" => TimeSpan.FromMinutes(15),
        "M30" => TimeSpan.FromMinutes(30),
        "H4" => TimeSpan.FromHours(4),
        "D1" => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(1)
    };

    private static double[] EmaSeries(IReadOnlyList<double> values, int period)
    {
        var result = Enumerable.Repeat(double.NaN, values.Count).ToArray();
        if (values.Count < period)
            return result;
        var current = values.Take(period).Average();
        result[period - 1] = current;
        var multiplier = 2d / (period + 1d);
        for (var index = period; index < values.Count; index++)
        {
            current = (values[index] - current) * multiplier + current;
            result[index] = current;
        }
        return result;
    }

    private static double[] RsiSeries(IReadOnlyList<double> closes, int period)
    {
        var result = Enumerable.Repeat(double.NaN, closes.Count).ToArray();
        if (closes.Count <= period)
            return result;
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
        result[period] = RsiValue(averageGain, averageLoss);
        for (var index = period + 1; index < closes.Count; index++)
        {
            var change = closes[index] - closes[index - 1];
            averageGain = (averageGain * (period - 1) + Math.Max(change, 0d)) / period;
            averageLoss = (averageLoss * (period - 1) + Math.Max(-change, 0d)) / period;
            result[index] = RsiValue(averageGain, averageLoss);
        }
        return result;
    }

    private static double RsiValue(double averageGain, double averageLoss) => averageLoss <= 0
        ? averageGain <= 0 ? 50 : 100
        : 100 - 100 / (1 + averageGain / averageLoss);

    private static double[] AtrSeries(IReadOnlyList<Candle> bars, int period)
    {
        var result = Enumerable.Repeat(double.NaN, bars.Count).ToArray();
        if (bars.Count < period)
            return result;
        var ranges = TrueRanges(bars);
        var current = ranges.Take(period).Average();
        result[period - 1] = current;
        for (var index = period; index < bars.Count; index++)
        {
            current = (current * (period - 1) + ranges[index]) / period;
            result[index] = current;
        }
        return result;
    }

    private static double[] AdxSeries(IReadOnlyList<Candle> bars, int period)
    {
        var adx = Enumerable.Repeat(double.NaN, bars.Count).ToArray();
        if (bars.Count < period * 2)
            return adx;

        var trueRanges = new double[bars.Count];
        var positiveMovement = new double[bars.Count];
        var negativeMovement = new double[bars.Count];
        for (var index = 1; index < bars.Count; index++)
        {
            var upMove = (double)(bars[index].High - bars[index - 1].High);
            var downMove = (double)(bars[index - 1].Low - bars[index].Low);
            positiveMovement[index] = upMove > downMove && upMove > 0 ? upMove : 0;
            negativeMovement[index] = downMove > upMove && downMove > 0 ? downMove : 0;
            trueRanges[index] = Math.Max((double)(bars[index].High - bars[index].Low), Math.Max(
                Math.Abs((double)(bars[index].High - bars[index - 1].Close)),
                Math.Abs((double)(bars[index].Low - bars[index - 1].Close))));
        }

        var smoothedRange = trueRanges.Skip(1).Take(period).Sum();
        var smoothedPositive = positiveMovement.Skip(1).Take(period).Sum();
        var smoothedNegative = negativeMovement.Skip(1).Take(period).Sum();
        var dx = Enumerable.Repeat(double.NaN, bars.Count).ToArray();
        for (var index = period; index < bars.Count; index++)
        {
            if (index > period)
            {
                smoothedRange = smoothedRange - smoothedRange / period + trueRanges[index];
                smoothedPositive = smoothedPositive - smoothedPositive / period + positiveMovement[index];
                smoothedNegative = smoothedNegative - smoothedNegative / period + negativeMovement[index];
            }
            if (smoothedRange <= 0)
                continue;
            var plusDi = 100 * smoothedPositive / smoothedRange;
            var minusDi = 100 * smoothedNegative / smoothedRange;
            var total = plusDi + minusDi;
            dx[index] = total <= 0 ? 0 : 100 * Math.Abs(plusDi - minusDi) / total;
        }

        var firstAdxIndex = period * 2 - 1;
        var initial = dx.Skip(period).Take(period).Where(double.IsFinite).ToArray();
        if (initial.Length < period)
            return adx;
        var currentAdx = initial.Average();
        adx[firstAdxIndex] = currentAdx;
        for (var index = firstAdxIndex + 1; index < bars.Count; index++)
        {
            if (double.IsFinite(dx[index]))
            {
                currentAdx = (currentAdx * (period - 1) + dx[index]) / period;
                adx[index] = currentAdx;
            }
        }
        return adx;
    }

    private static double[] MacdHistogramSeries(IReadOnlyList<double> closes, int fast, int slow, int signal)
    {
        var result = Enumerable.Repeat(double.NaN, closes.Count).ToArray();
        var fastEma = EmaSeries(closes, fast);
        var slowEma = EmaSeries(closes, slow);
        var macd = Enumerable.Repeat(double.NaN, closes.Count).ToArray();
        for (var index = slow - 1; index < closes.Count; index++)
            macd[index] = fastEma[index] - slowEma[index];

        var firstSignalIndex = slow + signal - 2;
        if (closes.Count <= firstSignalIndex)
            return result;
        var currentSignal = macd.Skip(slow - 1).Take(signal).Average();
        result[firstSignalIndex] = macd[firstSignalIndex] - currentSignal;
        var multiplier = 2d / (signal + 1d);
        for (var index = firstSignalIndex + 1; index < closes.Count; index++)
        {
            currentSignal = (macd[index] - currentSignal) * multiplier + currentSignal;
            result[index] = macd[index] - currentSignal;
        }
        return result;
    }

    private static double[] TrueRanges(IReadOnlyList<Candle> bars)
    {
        var ranges = new double[bars.Count];
        for (var index = 0; index < bars.Count; index++)
        {
            ranges[index] = index == 0
                ? (double)(bars[index].High - bars[index].Low)
                : Math.Max((double)(bars[index].High - bars[index].Low), Math.Max(
                    Math.Abs((double)(bars[index].High - bars[index - 1].Close)),
                    Math.Abs((double)(bars[index].Low - bars[index - 1].Close))));
        }
        return ranges;
    }

    private sealed record ScenarioDefinition(string Id, string Name);
    private sealed record TradeLevels(decimal Entry, decimal StopLoss, decimal TakeProfit);

    private sealed class MarketContext(
        MarketSnapshot market,
        string timeframe,
        Candle[] bars,
        double[] ema20,
        double[] ema50,
        double[] ema200,
        double[] rsi,
        double[] atr,
        double[] adx,
        double[] macdHistogram)
    {
        public MarketSnapshot Market { get; } = market;
        public string Timeframe { get; } = timeframe;
        public Candle[] Bars { get; } = bars;
        public double[] Ema20 { get; } = ema20;
        public double[] Ema50 { get; } = ema50;
        public double[] Ema200 { get; } = ema200;
        public double[] Rsi { get; } = rsi;
        public double[] Atr { get; } = atr;
        public double[] Adx { get; } = adx;
        public double[] MacdHistogram { get; } = macdHistogram;
        public double Price => (double)Market.Price;

        public double Latest(IReadOnlyList<double> values) => values.Count == 0 ? double.NaN : values[^1];

        public double Slope(IReadOnlyList<double> values, int barsBack)
        {
            var previousIndex = Math.Max(0, values.Count - 1 - barsBack);
            return values.Count == 0 ? double.NaN : values[^1] - values[previousIndex];
        }

        public (double High, double Low, double Width) RangeBeforeLast(int lookback) =>
            RangeBefore(Bars.Length - 1, lookback);

        public (double High, double Low, double Width) RangeBefore(int index, int lookback)
        {
            var start = Math.Max(0, index - lookback);
            var count = Math.Max(0, index - start);
            if (count == 0)
                return (double.NaN, double.NaN, double.NaN);
            var high = Bars.Skip(start).Take(count).Max(candle => (double)candle.High);
            var low = Bars.Skip(start).Take(count).Min(candle => (double)candle.Low);
            return (high, low, high - low);
        }

        public double AtrRatio(int lookback)
        {
            var average = AverageRecentAtr(lookback, excludeLatest: true);
            return average <= 0 ? double.NaN : Latest(Atr) / average;
        }

        public double AverageRecentAtr(int lookback, bool excludeLatest = false)
        {
            var end = Atr.Length - (excludeLatest ? 1 : 0);
            var values = Atr.Take(end).Where(double.IsFinite).TakeLast(lookback).ToArray();
            return values.Length == 0 ? double.NaN : values.Average();
        }

        public double? VolumeRatio(int index, int lookback)
        {
            if (index < lookback || Bars[index].TickVolume <= 0)
                return null;
            var baseline = Bars.Skip(index - lookback).Take(lookback)
                .Select(candle => candle.TickVolume).Where(volume => volume > 0).ToArray();
            if (baseline.Length < Math.Max(5, lookback / 2))
                return null;
            return Bars[index].TickVolume / baseline.Average();
        }

        public bool MacdDeclining(int index) => index >= 2 &&
            double.IsFinite(MacdHistogram[index]) && double.IsFinite(MacdHistogram[index - 2]) &&
            MacdHistogram[index] < MacdHistogram[index - 2];

        public bool MacdRising(int index) => index >= 2 &&
            double.IsFinite(MacdHistogram[index]) && double.IsFinite(MacdHistogram[index - 2]) &&
            MacdHistogram[index] > MacdHistogram[index - 2];

        public bool MacdContracting() => MacdHistogram.Length >= 4 &&
            double.IsFinite(MacdHistogram[^1]) && double.IsFinite(MacdHistogram[^2]) &&
            Math.Abs(MacdHistogram[^1]) < Math.Abs(MacdHistogram[^2]);
    }
}
