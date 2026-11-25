namespace XYN_Trader.Models;

public sealed record Candle(
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    DateTimeOffset TimeUtc);

public sealed record MarketTick(
    decimal Price,
    decimal Bid,
    decimal Ask,
    DateTimeOffset UpdatedAtUtc);

public sealed record MarketSnapshot(
    string Symbol,
    string Currency,
    decimal Price,
    decimal ChangePercent,
    DateTimeOffset UpdatedAtUtc,
    string Regime,
    string NewsRisk,
    string ModelVersion,
    IReadOnlyList<Candle> Candles);

public sealed record SignalRecord(
    string Id,
    string Direction,
    string Status,
    string Timeframe,
    string Horizon,
    decimal EntryLow,
    decimal EntryHigh,
    decimal StopLoss,
    decimal TakeProfit,
    int ProbabilityPercent,
    decimal RewardRisk,
    DateTimeOffset CreatedAtUtc,
    string DateGroup,
    string Reason);

public sealed record IndicatorReading(
    string Name,
    string Value,
    string State,
    int StrengthPercent,
    string Tone);

public sealed record PriceZone(
    string Name,
    string Description,
    decimal Low,
    decimal High,
    string Strength,
    string Tone);

public sealed record AnalysisSnapshot(
    string Timeframe,
    string Regime,
    int RegimeConfidencePercent,
    int TechnicalScore,
    IReadOnlyList<IndicatorReading> Indicators,
    IReadOnlyList<PriceZone> Zones);

public sealed record PriceForecast(
    string Horizon,
    string Label,
    decimal Low,
    decimal High,
    decimal Average,
    decimal ChangePercent);

public sealed record PredictionIndicators(
    decimal? Sma20,
    decimal? Rsi14,
    decimal? Macd,
    decimal? MacdSignal,
    decimal? BollingerLower,
    decimal? BollingerUpper);

public sealed record TradeLevelSnapshot(
    decimal BuyAbove,
    decimal SellBelow,
    IReadOnlyList<decimal> Resistance,
    IReadOnlyList<decimal> Support,
    IReadOnlyList<decimal> BuyTargets,
    IReadOnlyList<decimal> SellTargets);

public sealed record PricePredictionSnapshot(
    string Symbol,
    decimal CurrentPrice,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<PriceForecast> Forecasts,
    PredictionIndicators Indicators,
    TradeLevelSnapshot Levels);

public sealed record ApiEnvelope<T>(
    DateTimeOffset TimestampUtc,
    string TraceId,
    string SchemaVersion,
    T Data)
{
    public static ApiEnvelope<T> Create(T data, string traceId) =>
        new(DateTimeOffset.UtcNow, traceId, "1.0", data);
}
