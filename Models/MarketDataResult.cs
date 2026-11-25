namespace XYN_Trader.Models;

public enum MarketConnectionStatus
{
    Connected,
    Disconnected,
    Error
}

public enum MarketFreshness
{
    Fresh,
    Stale,
    Unavailable
}

public sealed record MarketDataResult(
    MarketConnectionStatus ConnectionStatus,
    MarketFreshness Freshness,
    string Source,
    DateTimeOffset? ReceivedAtUtc,
    MarketSnapshot? Snapshot,
    string? Message);

public sealed record MarketTickResult(
    bool Available,
    MarketTick? Tick,
    MarketFreshness Freshness,
    string? Message)
{
    public static MarketTickResult Success(MarketTick tick, MarketFreshness freshness, string? message) =>
        new(true, tick, freshness, message);

    public static MarketTickResult Unavailable(string message) =>
        new(false, null, MarketFreshness.Unavailable, message);
}

public sealed record Mt5OpenPosition(
    long Ticket,
    string Symbol,
    string Type,
    decimal Volume,
    decimal PriceOpen,
    decimal StopLoss,
    decimal TakeProfit,
    decimal PriceCurrent,
    decimal Profit,
    DateTimeOffset OpenedAtUtc,
    string Comment,
    long Magic,
    int Digits,
    decimal Point,
    int StopsLevelPoints,
    bool HasSymbolPrecisionMetadata);

public sealed record OpenPositionsResult(
    bool Available,
    IReadOnlyList<Mt5OpenPosition> Positions,
    string? AccountCurrency,
    DateTimeOffset? RetrievedAtUtc,
    string? Message)
{
    public static OpenPositionsResult Unavailable(string message) =>
        new(false, [], null, null, message);
}

public enum PositionAdviceKind
{
    Hold,
    TightenStop,
    ConsiderClose,
    Unavailable
}

public sealed record PositionAdvice(
    PositionAdviceKind Kind,
    string Title,
    string Reason,
    decimal? SuggestedStopLoss);

public sealed record OpenPositionReview(
    Mt5OpenPosition Position,
    AnalysisSnapshot? Analysis,
    MarketFreshness Freshness,
    DateTimeOffset? MarketUpdatedAtUtc,
    decimal? Atr,
    PositionAdvice Advice,
    string? DataMessage);

public sealed record PositionReviewsResult(
    bool Available,
    IReadOnlyList<OpenPositionReview> Reviews,
    string? AccountCurrency,
    DateTimeOffset? RetrievedAtUtc,
    string? Message)
{
    public static PositionReviewsResult Unavailable(string message) =>
        new(false, [], null, null, message);
}

public sealed record PositionActionResult(bool Succeeded, string Message);
