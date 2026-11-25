namespace XYN_Trader.Models;

public sealed record PositionProfitLimits(
    long Ticket,
    decimal? TakeProfitAmount,
    decimal? StopLossAmount,
    DateTimeOffset UpdatedAtUtc);
