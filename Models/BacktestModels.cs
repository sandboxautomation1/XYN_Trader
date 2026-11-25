namespace XYN_Trader.Models;

public sealed record BacktestTrade(
    string SignalId,
    DateTimeOffset TimestampUtc,
    DateTimeOffset ExitTimeUtc,
    string Direction,
    string Status,
    double EntryPrice,
    double ExitPrice,
    double ResultR);

public sealed record BacktestResult(
    string Symbol,
    string Timeframe,
    string Horizon,
    string PolicyVersion,
    string PolicySha256,
    int Candles,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int CandidateCount,
    int ClosedTrades,
    int UntriggeredCount,
    int UnmaturedCount,
    int SkippedOverlapCount,
    double? WinRatePercent,
    double NetR,
    double? ExpectancyR,
    double? ProfitFactor,
    double MaxDrawdownPercent,
    double EquityStart,
    double EquityEnd,
    double RiskPerTradePercent,
    BacktestExecutionAssumptions ExecutionAssumptions,
    IReadOnlyList<BacktestTrade> Trades);

public sealed record BacktestExecutionAssumptions(
    string Entry,
    string Exit,
    string AmbiguousBar,
    string Portfolio,
    string DateFilter,
    string Costs);

public sealed record BacktestResultResponse(bool Available, BacktestResult? Result, string Message)
{
    public static BacktestResultResponse Unavailable(string message) => new(false, null, message);
    public static BacktestResultResponse From(BacktestResult result) => new(true, result, "Backtest report is available.");
}
