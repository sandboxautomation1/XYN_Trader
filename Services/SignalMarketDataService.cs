using XYN_Trader.Models;

namespace XYN_Trader.Services;

/// <summary>
/// Loads the fresh OHLC series used by the manual signal button, preferring Twelve Data.
/// </summary>
public sealed class SignalMarketDataService(
    TwelveDataMarketDataProvider twelveData,
    Mt5MarketDataProvider mt5,
    IConfiguration configuration)
{
    public const int SignalCandleCount = 288;

    private string ConfiguredSource => configuration["MarketData:Source"]?.Trim() ?? "Twelve Data";
    private string ConfiguredInterval => configuration["MarketData:Interval"]?.Trim() ?? "5min";
    public bool PrefersTwelveData => !IsMt5Source(ConfiguredSource);

    public string IntervalLabel => PrefersTwelveData
        ? ToTwelveDataInterval(ConfiguredInterval)
        : ToMt5Timeframe(ConfiguredInterval);

    public async Task<MarketDataResult> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!PrefersTwelveData)
            return await GetMt5SnapshotAsync(cancellationToken);

        var symbol = configuration["MarketData:Symbol"]?.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
            symbol = configuration["TwelveData:Symbol"]?.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
            symbol = "XAU/USD";

        var twelveDataResult = await twelveData.GetSignalSnapshotAsync(
            symbol,
            ToTwelveDataInterval(ConfiguredInterval),
            SignalCandleCount,
            cancellationToken);
        if (twelveDataResult.Snapshot is not null)
            return twelveDataResult;

        var mt5Result = await GetMt5SnapshotAsync(cancellationToken);
        if (mt5Result.Snapshot is not null)
        {
            return mt5Result with
            {
                Message = $"Twelve Data не е наличен ({twelveDataResult.Message ?? "няма данни"}); използван е резервен MT5 източник."
            };
        }

        return mt5Result with
        {
            Message = $"Twelve Data: {twelveDataResult.Message ?? "няма данни"} MT5 резерв: {mt5Result.Message ?? "няма данни"}"
        };
    }

    private Task<MarketDataResult> GetMt5SnapshotAsync(CancellationToken cancellationToken)
    {
        var symbol = configuration["MarketData:Symbol"]?.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
            symbol = configuration["MT5Bridge:Symbol"]?.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
            symbol = "XAUUSD";

        return mt5.GetSignalSnapshotAsync(
            symbol,
            ToMt5Timeframe(ConfiguredInterval),
            SignalCandleCount,
            cancellationToken);
    }

    private static bool IsMt5Source(string source) =>
        source.Equals("MT5", StringComparison.OrdinalIgnoreCase) ||
        source.Equals("MT5Bridge", StringComparison.OrdinalIgnoreCase) ||
        source.Equals("mt5-bridge", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("MetaTrader 5", StringComparison.OrdinalIgnoreCase) ||
        source.Equals("MetaTrader5", StringComparison.OrdinalIgnoreCase);

    private static string ToTwelveDataInterval(string interval) => interval.Trim().ToLowerInvariant() switch
    {
        "m1" or "1m" => "1min",
        "m5" or "5m" => "5min",
        "m15" or "15m" => "15min",
        "m30" or "30m" => "30min",
        "h1" or "1h" => "1h",
        "h4" or "4h" => "4h",
        "d1" or "1d" or "1day" => "1day",
        _ => interval.Trim().ToLowerInvariant()
    };

    private static string ToMt5Timeframe(string interval) => interval.Trim().ToLowerInvariant() switch
    {
        "m1" or "1m" or "1min" => "M1",
        "m5" or "5m" or "5min" => "M5",
        "m15" or "15m" or "15min" => "M15",
        "m30" or "30m" or "30min" => "M30",
        "h4" or "4h" => "H4",
        "d1" or "1d" or "1day" => "D1",
        _ => "H1"
    };
}
