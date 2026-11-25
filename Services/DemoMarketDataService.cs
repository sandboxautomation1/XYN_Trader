using XYN_Trader.Models;

namespace XYN_Trader.Services;

public sealed class DemoMarketDataService
{
    private static readonly DateTimeOffset DemoTimestamp =
        new(2026, 11, 12, 16, 30, 0, TimeSpan.Zero);

    private readonly MarketSnapshot _market = new(
        Symbol: "XAUUSD",
        Currency: "USD",
        Price: 2683.40m,
        ChangePercent: 0.84m,
        UpdatedAtUtc: DemoTimestamp,
        Regime: "Възходящ импулс",
        NewsRisk: "Умерен",
        ModelVersion: "Baseline v0.1 · demo",
        Candles: CreateCandles());

    private readonly IReadOnlyList<SignalRecord> _signals =
    [
        new("XYN-2408", "BUY BELOW", "ACTIVE", "H1", "24H",
            2664m, 2672m, 2651m, 2704m, 68, 2.1m,
            DemoTimestamp.AddHours(-2), "today",
            "Връщане към подкрепа при запазена възходяща структура."),
        new("XYN-2407", "SELL BELOW", "TRIGGERED", "M15", "1H",
            2681m, 2685m, 2694m, 2663m, 61, 1.6m,
            DemoTimestamp.AddHours(-3), "today",
            "Отхвърляне от локална съпротива; рискът остава повишен."),
        new("XYN-2406", "BUY BELOW", "CLOSED", "H4", "48H",
            2638m, 2646m, 2624m, 2676m, 72, 2.4m,
            DemoTimestamp.AddDays(-1), "yesterday",
            "Целта е достигната в примерния набор от данни."),
        new("XYN-2405", "SELL BELOW", "EXPIRED", "M30", "24H",
            2698m, 2704m, 2716m, 2672m, 57, 1.7m,
            DemoTimestamp.AddDays(-2), "older",
            "Изтекъл хоризонт без активиране.")
    ];

    private readonly AnalysisSnapshot _analysis = new(
        Timeframe: "H1",
        Regime: "Възходящ импулс",
        RegimeConfidencePercent: 64,
        TechnicalScore: 64,
        Indicators:
        [
            new("RSI (14)", "58.4", "Неутрално", 58, "neutral"),
            new("MACD", "+3.82", "Възходящо", 69, "positive"),
            new("ATR (14)", "8.24", "Средна волатилност", 48, "neutral"),
            new("ADX (14)", "24.6", "Умерен тренд", 43, "neutral"),
            new("Bollinger (20)", "Горна 2,691", "Цена в средната половина", 62, "neutral"),
            new("EMA (20 / 50)", "Bullish", "Кратка над дългата", 73, "positive")
        ],
        Zones:
        [
            new("Съпротива", "Локален връх / предлагане", 2696m, 2704m, "Силна", "resistance"),
            new("Подкрепа", "Структурно търсене", 2664m, 2672m, "Умерена", "support")
        ]);

    public MarketSnapshot GetMarketSnapshot() => _market;

    public AnalysisSnapshot GetAnalysis() => _analysis;

    public IReadOnlyList<SignalRecord> GetSignals(
        string? status = null,
        string? direction = null,
        string? timeframe = null,
        string? horizon = null,
        string? date = null,
        string? search = null)
    {
        IEnumerable<SignalRecord> results = _signals;

        if (!string.IsNullOrWhiteSpace(status))
            results = results.Where(signal => Matches(signal.Status, status));
        if (!string.IsNullOrWhiteSpace(direction))
            results = results.Where(signal => Matches(signal.Direction, direction));
        if (!string.IsNullOrWhiteSpace(timeframe))
            results = results.Where(signal => Matches(signal.Timeframe, timeframe));
        if (!string.IsNullOrWhiteSpace(horizon))
            results = results.Where(signal => Matches(signal.Horizon, horizon));
        if (!string.IsNullOrWhiteSpace(date))
            results = results.Where(signal => Matches(signal.DateGroup, date));
        if (!string.IsNullOrWhiteSpace(search))
            results = results.Where(signal =>
                signal.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                signal.Direction.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                signal.Timeframe.Contains(search, StringComparison.OrdinalIgnoreCase));

        return results.ToArray();
    }

    private static bool Matches(string value, string filter) =>
        string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<Candle> CreateCandles()
    {
        decimal[] closes =
        [
            2634, 2638, 2632, 2641, 2644, 2639, 2646, 2642,
            2650, 2647, 2653, 2658, 2652, 2656, 2661, 2657,
            2665, 2662, 2668, 2664, 2671, 2667, 2675, 2672,
            2678, 2674, 2682, 2677, 2685, 2680, 2674, 2681,
            2678, 2686, 2682, 2690, 2685, 2692, 2687, 2681,
            2688, 2684, 2691, 2686, 2679, 2684, 2678, 2683
        ];
        decimal[] wicks = [3, 5, 4, 2, 6];
        var candles = new Candle[closes.Length];

        for (var index = 0; index < closes.Length; index++)
        {
            var open = index == 0 ? closes[index] - 3 : closes[index - 1];
            var wick = wicks[index % wicks.Length];
            candles[index] = new Candle(
                open,
                Math.Max(open, closes[index]) + wick,
                Math.Min(open, closes[index]) - wick * 0.8m,
                closes[index],
                DemoTimestamp.AddMinutes((index - closes.Length + 1) * 60));
        }

        return candles;
    }
}
