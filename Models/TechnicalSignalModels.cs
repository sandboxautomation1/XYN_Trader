namespace XYN_Trader.Models;

public sealed record SignalFactor(
    string Label,
    string Explanation,
    int Strength);

public sealed record TechnicalSignalSnapshot(
    string Symbol,
    string Timeframe,
    DateTimeOffset GeneratedAtUtc,
    string Direction,
    int TechnicalScore,
    int TechnicalStrengthPercent,
    decimal Entry,
    decimal? StopLoss,
    decimal? TakeProfit,
    decimal? RewardRisk,
    string Summary,
    int CandleCount,
    IReadOnlyList<SignalFactor> BullishFactors,
    IReadOnlyList<SignalFactor> BearishFactors,
    IReadOnlyList<SignalFactor> RiskFactors,
    bool IsStale = false,
    string? DataWarning = null);
