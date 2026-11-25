using System.Text.Json.Serialization;

namespace XYN_Trader.Models;

public sealed record SignalRiskCalculatorRequest(
    [property: JsonPropertyName("risk_percent")] decimal RiskPercent,
    [property: JsonPropertyName("signals")] IReadOnlyList<SignalRiskCalculatorSignal> Signals);

public sealed record SignalRiskCalculatorSignal(
    [property: JsonPropertyName("signal_id")] string SignalId,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("entry")] decimal Entry,
    [property: JsonPropertyName("stop_loss")] decimal StopLoss,
    [property: JsonPropertyName("tp1")] decimal Tp1,
    [property: JsonPropertyName("tp2")] decimal Tp2,
    [property: JsonPropertyName("tp3")] decimal Tp3);

public sealed record SignalRiskCalculatorResponse(
    [property: JsonPropertyName("account_balance")] decimal AccountBalance,
    [property: JsonPropertyName("account_equity")] decimal AccountEquity,
    [property: JsonPropertyName("account_currency")] string AccountCurrency,
    [property: JsonPropertyName("risk_percent")] decimal RiskPercent,
    [property: JsonPropertyName("calculated_at_utc")] DateTimeOffset CalculatedAtUtc,
    [property: JsonPropertyName("results")] IReadOnlyList<SignalRiskCalculatorItem> Results);

public sealed record SignalRiskCalculatorItem(
    [property: JsonPropertyName("signal_id")] string SignalId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("volume_lots")] decimal VolumeLots,
    [property: JsonPropertyName("target_risk_amount")] decimal TargetRiskAmount,
    [property: JsonPropertyName("stop_loss_pnl")] decimal? StopLossPnl,
    [property: JsonPropertyName("tp1_pnl")] decimal? Tp1Pnl,
    [property: JsonPropertyName("tp2_pnl")] decimal? Tp2Pnl,
    [property: JsonPropertyName("tp3_pnl")] decimal? Tp3Pnl);

public sealed record SignalRiskCalculationResult(
    bool Available,
    SignalRiskCalculatorResponse? Calculation,
    string Message)
{
    public static SignalRiskCalculationResult Unavailable(string message) => new(false, null, message);

    public static SignalRiskCalculationResult From(SignalRiskCalculatorResponse calculation) =>
        new(true, calculation, "Изчисленията от MT5 са актуални.");
}
