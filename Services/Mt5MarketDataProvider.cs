using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

public sealed class Mt5MarketDataProvider
{
    public const string DefaultBaseAddress = "http://127.0.0.1:8000/";
    public const string SourceName = "MetaTrader 5 · mt5-bridge";
    private const int DefaultCandleCount = 200;
    private const int SignalCandleCount = 300;
    public const int FreshnessThresholdMinutes = 15;
    private static readonly HashSet<string> SupportedTimeframes =
        new(StringComparer.OrdinalIgnoreCase) { "M1", "M5", "M15", "M30", "H1", "H4", "D1" };

    private readonly HttpClient _httpClient;
    private readonly string _symbol;

    public Mt5MarketDataProvider(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _symbol = string.IsNullOrWhiteSpace(configuration["MT5Bridge:Symbol"])
            ? "XAUUSD"
            : configuration["MT5Bridge:Symbol"]!.Trim();
    }

    public async Task<MarketDataResult> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        await GetSnapshotAsync("H1", cancellationToken);

    public Task<MarketDataResult> GetSnapshotAsync(
        string timeframe,
        CancellationToken cancellationToken = default) =>
        GetSymbolSnapshotAsync(_symbol, timeframe, cancellationToken);

    public async Task<MarketDataResult> GetSymbolSnapshotAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default) =>
        await GetSymbolSnapshotAsync(
            symbol,
            timeframe,
            timeframe.Equals("H1", StringComparison.OrdinalIgnoreCase) ? SignalCandleCount : DefaultCandleCount,
            cancellationToken);

    public Task<MarketDataResult> GetSignalSnapshotAsync(
        string symbol,
        string timeframe,
        int candleCount = 288,
        CancellationToken cancellationToken = default) =>
        GetSymbolSnapshotAsync(symbol, timeframe, candleCount, cancellationToken);

    private async Task<MarketDataResult> GetSymbolSnapshotAsync(
        string symbol,
        string timeframe,
        int candleCount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Failure(MarketConnectionStatus.Error, "Символът за пазарния анализ е празен.");
        if (!SupportedTimeframes.Contains(timeframe))
            return Failure(MarketConnectionStatus.Error, "Невалидна времева рамка за MT5.");
        if (candleCount < 1)
            return Failure(MarketConnectionStatus.Error, "Броят на свещите за MT5 трябва да е положителен.");

        symbol = symbol.Trim();

        var healthResponse = await GetJsonAsync("health", cancellationToken);
        if (healthResponse.Error is not null)
            return Failure(MarketConnectionStatus.Disconnected, healthResponse.Error);

        try
        {
            using var healthDocument = JsonDocument.Parse(healthResponse.Body!);
            if (!healthDocument.RootElement.TryGetProperty("mt5_connected", out var connected) ||
                connected.ValueKind != JsonValueKind.True)
            {
                return Failure(
                    MarketConnectionStatus.Disconnected,
                    "MT5 bridge е достъпен, но няма активна връзка с терминала MetaTrader 5. Отворете терминала и влезте в сметката.");
            }
        }
        catch (JsonException)
        {
            return Failure(MarketConnectionStatus.Error, "MT5 bridge върна невалиден health отговор.");
        }

        var tickTask = GetLatestTickAsync(symbol, cancellationToken);
        var ratesTask = GetJsonAsync(
            $"rates/{Uri.EscapeDataString(symbol)}?timeframe={Uri.EscapeDataString(timeframe.ToUpperInvariant())}&count={candleCount}",
            cancellationToken);
        await Task.WhenAll(tickTask, ratesTask);

        var tickResult = await tickTask;
        if (!tickResult.Available || tickResult.Tick is null)
            return Failure(MarketConnectionStatus.Error, tickResult.Message ?? "MT5 няма котировка за символа.");

        var ratesResponse = await ratesTask;
        if (ratesResponse.Error is not null)
            return Failure(MarketConnectionStatus.Error, ratesResponse.Error);

        try
        {
            using var ratesDocument = JsonDocument.Parse(ratesResponse.Body!);
            var candles = ReadCandles(ratesDocument.RootElement);
            if (candles.Count == 0)
                return Failure(MarketConnectionStatus.Error, $"MT5 не върна свещи за символа {symbol} и рамката {timeframe}.");

            var tick = tickResult.Tick;
            var previousClose = candles.Count > 1 ? candles[^2].Close : candles[^1].Open;
            var changePercent = previousClose == 0m
                ? 0m
                : (tick.Price - previousClose) / previousClose * 100m;
            var freshness = GetFreshness(tick.UpdatedAtUtc, DateTimeOffset.UtcNow);
            var snapshot = new MarketSnapshot(
                Symbol: symbol,
                Currency: "USD",
                Price: tick.Price,
                ChangePercent: changePercent,
                UpdatedAtUtc: tick.UpdatedAtUtc,
                Regime: "Not calculated",
                NewsRisk: "Not available",
                ModelVersion: "No AI model configured",
                Candles: candles);

            return new MarketDataResult(
                MarketConnectionStatus.Connected,
                freshness,
                SourceName,
                DateTimeOffset.UtcNow,
                snapshot,
                freshness == MarketFreshness.Stale
                    ? "Последната MT5 котировка е по-стара от 15 минути."
                    : null);
        }
        catch (JsonException)
        {
            return Failure(MarketConnectionStatus.Error, "MT5 bridge върна невалиден списък със свещи.");
        }
        catch (InvalidOperationException)
        {
            return Failure(MarketConnectionStatus.Error, "MT5 bridge върна неочакван формат на свещите.");
        }
        catch (FormatException)
        {
            return Failure(MarketConnectionStatus.Error, "MT5 bridge върна невалидна цена или времеви печат.");
        }
        catch (ArgumentOutOfRangeException)
        {
            return Failure(MarketConnectionStatus.Error, "MT5 bridge върна невалиден времеви печат.");
        }
    }

    public Task<MarketTickResult> GetLatestTickAsync(CancellationToken cancellationToken = default) =>
        GetLatestTickAsync(_symbol, cancellationToken);

    public async Task<MarketTickResult> GetLatestTickAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return MarketTickResult.Unavailable("Символът за котировката е празен.");

        var response = await GetJsonAsync($"tick/{Uri.EscapeDataString(symbol.Trim())}", cancellationToken);
        if (response.Error is not null)
            return MarketTickResult.Unavailable(response.Error);

        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement;
            var bid = ReadDecimal(root, "bid") ?? 0m;
            var ask = ReadDecimal(root, "ask") ?? 0m;
            var last = ReadDecimal(root, "last") ?? 0m;
            var seconds = ReadInt64(root, "time") ?? 0L;
            var milliseconds = ReadInt64(root, "time_msc") ?? 0L;
            if (seconds <= 0 && milliseconds <= 0)
                return MarketTickResult.Unavailable("MT5 bridge не върна валиден времеви печат за котировката.");

            var price = last > 0m
                ? last
                : bid > 0m && ask > 0m
                    ? (bid + ask) / 2m
                    : Math.Max(bid, ask);
            if (price <= 0m)
                return MarketTickResult.Unavailable("MT5 bridge не върна валидна bid/ask/last цена.");

            var updatedAtUtc = milliseconds > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : DateTimeOffset.FromUnixTimeSeconds(seconds);
            var tick = new MarketTick(price, bid, ask, updatedAtUtc);
            var freshness = GetFreshness(updatedAtUtc, DateTimeOffset.UtcNow);
            return MarketTickResult.Success(
                tick,
                freshness,
                freshness == MarketFreshness.Stale ? "Последната MT5 котировка е остаряла." : null);
        }
        catch (JsonException)
        {
            return MarketTickResult.Unavailable("MT5 bridge върна невалидна котировка.");
        }
        catch (InvalidOperationException)
        {
            return MarketTickResult.Unavailable("MT5 bridge върна неочакван формат на котировката.");
        }
        catch (ArgumentOutOfRangeException)
        {
            return MarketTickResult.Unavailable("MT5 bridge върна невалиден времеви печат за котировката.");
        }
    }

    public async Task<OpenPositionsResult> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
    {
        var positionsTask = GetJsonAsync("positions", cancellationToken);
        var accountTask = GetJsonAsync("account", cancellationToken);
        await Task.WhenAll(positionsTask, accountTask);

        var positionsResponse = await positionsTask;
        if (positionsResponse.Error is not null)
            return OpenPositionsResult.Unavailable(positionsResponse.Error);

        string? accountCurrency = null;
        var accountResponse = await accountTask;
        if (accountResponse.Error is null)
        {
            try
            {
                using var accountDocument = JsonDocument.Parse(accountResponse.Body!);
                if (accountDocument.RootElement.TryGetProperty("currency", out var currencyValue))
                    accountCurrency = currencyValue.GetString();
            }
            catch (JsonException)
            {
                // Position data remains useful when optional account metadata is unavailable.
            }
        }

        try
        {
            using var positionsDocument = JsonDocument.Parse(positionsResponse.Body!);
            if (positionsDocument.RootElement.ValueKind != JsonValueKind.Array)
                return OpenPositionsResult.Unavailable("MT5 bridge върна невалиден списък с отворени позиции.");

            var positions = new List<Mt5OpenPosition>();
            foreach (var value in positionsDocument.RootElement.EnumerateArray())
            {
                var ticket = ReadInt64(value, "ticket");
                var symbol = value.TryGetProperty("symbol", out var symbolValue) ? symbolValue.GetString() : null;
                var type = value.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                var volume = ReadDecimal(value, "volume");
                var priceOpen = ReadDecimal(value, "price_open");
                var priceCurrent = ReadDecimal(value, "price_current");
                var profit = ReadDecimal(value, "profit");
                var openedAtSeconds = ReadInt64(value, "time") ?? 0L;
                if (ticket is null || string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(type) ||
                    volume is null || priceOpen is null || priceCurrent is null || profit is null || openedAtSeconds <= 0)
                    continue;

                var openedAtMilliseconds = ReadInt64(value, "time_msc") ?? 0L;
                var openedAtUtc = openedAtMilliseconds > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(openedAtMilliseconds)
                    : DateTimeOffset.FromUnixTimeSeconds(openedAtSeconds);
                var digitsValue = ReadInt64(value, "digits");
                var stopsLevelValue = ReadInt64(value, "stops_level");
                var digits = (int)Math.Clamp(digitsValue ?? 2L, 0L, 8L);
                var pointValue = ReadDecimal(value, "point");
                var point = pointValue ?? 0m;
                if (point <= 0m)
                    point = (decimal)Math.Pow(10, -digits);
                var hasPrecisionMetadata = digitsValue is >= 0 and <= 8 &&
                    pointValue is > 0m && stopsLevelValue is >= 0;
                var comment = value.TryGetProperty("comment", out var commentValue) ? commentValue.GetString() ?? string.Empty : string.Empty;

                positions.Add(new Mt5OpenPosition(
                    Ticket: ticket.Value,
                    Symbol: symbol.Trim(),
                    Type: type.Trim().ToUpperInvariant(),
                    Volume: volume.Value,
                    PriceOpen: priceOpen.Value,
                    StopLoss: ReadDecimal(value, "sl") ?? 0m,
                    TakeProfit: ReadDecimal(value, "tp") ?? 0m,
                    PriceCurrent: priceCurrent.Value,
                    Profit: profit.Value,
                    OpenedAtUtc: openedAtUtc,
                    Comment: comment,
                    Magic: ReadInt64(value, "magic") ?? 0L,
                    Digits: digits,
                    Point: point,
                    StopsLevelPoints: (int)Math.Clamp(stopsLevelValue ?? 0L, 0L, int.MaxValue),
                    HasSymbolPrecisionMetadata: hasPrecisionMetadata));
            }

            return new OpenPositionsResult(true, positions, accountCurrency, DateTimeOffset.UtcNow, null);
        }
        catch (JsonException)
        {
            return OpenPositionsResult.Unavailable("MT5 bridge върна невалиден JSON за отворените позиции.");
        }
        catch (InvalidOperationException)
        {
            return OpenPositionsResult.Unavailable("MT5 bridge върна неочакван формат за отворените позиции.");
        }
        catch (ArgumentOutOfRangeException)
        {
            return OpenPositionsResult.Unavailable("MT5 bridge върна невалидно време за отворена позиция.");
        }
    }

    public async Task<SignalRiskCalculationResult> CalculateSignalRiskAsync(
        IReadOnlyList<GeneratedSignalCandidate> signals,
        decimal riskPercent,
        decimal pnlLots,
        CancellationToken cancellationToken = default)
    {
        var request = new SignalRiskCalculatorRequest(
            riskPercent,
            pnlLots,
            signals.Select(signal => new SignalRiskCalculatorSignal(
                signal.SignalId,
                signal.Symbol,
                signal.Direction,
                signal.EntryPrice ?? (signal.EntryLow + signal.EntryHigh) / 2m,
                signal.StopLoss,
                signal.Tp1,
                signal.Tp2,
                signal.Tp3)).ToArray());

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("signal-calculator", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return SignalRiskCalculationResult.Unavailable(
                    $"MT5 не успя да изчисли риска ({(int)response.StatusCode}).");
            }

            var calculation = await response.Content.ReadFromJsonAsync<SignalRiskCalculatorResponse>(
                cancellationToken: cancellationToken);
            if (calculation is null)
                return SignalRiskCalculationResult.Unavailable("MT5 bridge върна празен отговор за калкулатора.");
            if (calculation.PnlLots != pnlLots)
                return SignalRiskCalculationResult.Unavailable("MT5 Bridge е с остаряла версия на калкулатора. Рестартирай MT5 Bridge, за да приложи избрания lot.");
            return SignalRiskCalculationResult.From(calculation);
        }
        catch (HttpRequestException)
        {
            return SignalRiskCalculationResult.Unavailable("MT5 bridge не е достъпен за изчисляване на риска.");
        }
        catch (JsonException)
        {
            return SignalRiskCalculationResult.Unavailable("MT5 bridge върна невалиден отговор за калкулатора.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SignalRiskCalculationResult.Unavailable("Изтече времето за отговор от MT5 bridge.");
        }
    }

    public async Task<MarketOrderResult> OpenMarketPositionAsync(
        string symbol,
        string type,
        decimal volume,
        decimal stopLoss,
        decimal takeProfit,
        string comment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol) || type is not ("BUY" or "SELL") || volume <= 0m)
            return new MarketOrderResult(false, null, "Невалидни параметри за пазарна поръчка.");

        var request = new MarketOrderRequest(
            symbol.Trim(),
            type,
            volume,
            stopLoss,
            takeProfit,
            string.IsNullOrWhiteSpace(comment) ? "XYN signal" : comment[..Math.Min(comment.Length, 31)]);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("order", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                return new MarketOrderResult(
                    false,
                    null,
                    detail ?? $"MT5 bridge върна HTTP {(int)response.StatusCode} при изпълнение на поръчката.");
            }

            var receipt = await response.Content.ReadFromJsonAsync<MarketOrderReceipt>(
                cancellationToken: cancellationToken);
            return receipt is null || receipt.Ticket <= 0
                ? new MarketOrderResult(false, null, "MT5 bridge не върна номер на изпълнената поръчка.")
                : new MarketOrderResult(true, receipt.Ticket, $"Позицията е отворена. Билет № {receipt.Ticket}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MarketOrderResult(false, null, "Изтече времето за изпълнение на поръчката към MT5 bridge.");
        }
        catch (HttpRequestException)
        {
            return new MarketOrderResult(false, null, "MT5 bridge не е достъпен; позицията не беше отворена.");
        }
        catch (JsonException)
        {
            return new MarketOrderResult(false, null, "MT5 bridge върна невалиден отговор за поръчката.");
        }
    }

    public Task<PositionActionResult> ModifyPositionStopLossAsync(
        long ticket,
        decimal stopLoss,
        CancellationToken cancellationToken = default) =>
        SendPositionActionAsync(
            "modify",
            new { ticket, sl = stopLoss, tp = (decimal?)null, update_sl = true, update_tp = false },
            "MT5 промени Stop Loss-а.",
            cancellationToken);

    public Task<PositionActionResult> ClosePositionAsync(
        long ticket,
        CancellationToken cancellationToken = default) =>
        SendPositionActionAsync(
            "close",
            new { ticket },
            "MT5 затвори позицията.",
            cancellationToken);

    private async Task<PositionActionResult> SendPositionActionAsync<TRequest>(
        string relativeUri,
        TRequest request,
        string successMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(relativeUri, request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new PositionActionResult(true, successMessage);

            var detail = await ReadErrorDetailAsync(response, cancellationToken);
            return new PositionActionResult(
                false,
                detail ?? $"MT5 bridge върна HTTP {(int)response.StatusCode} при {relativeUri}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PositionActionResult(false, "Изтече времето за изпълнение на заявката към MT5 bridge.");
        }
        catch (HttpRequestException)
        {
            return new PositionActionResult(false, "MT5 bridge не е достъпен; позицията не беше променена.");
        }
    }

    private async Task<(string? Body, string? Error)> GetJsonAsync(
        string relativeUri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(relativeUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                return (null, detail ?? $"MT5 bridge върна HTTP {(int)response.StatusCode} при {relativeUri.Split('?')[0]}.");
            }

            return (await response.Content.ReadAsStringAsync(cancellationToken), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "Изтече времето за отговор от MT5 bridge.");
        }
        catch (HttpRequestException)
        {
            return (null, "MT5 bridge не е достъпен. Проверете MT5Bridge:BaseAddress и стартирайте bridge сървъра.");
        }
    }

    private static async Task<string?> ReadErrorDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("detail", out var detail))
                return null;
            return detail.ValueKind == JsonValueKind.String ? detail.GetString() : detail.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<Candle> ReadCandles(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return [];

        var candles = new List<Candle>();
        foreach (var value in root.EnumerateArray())
        {
            var timestamp = ReadInt64(value, "time");
            var open = ReadDecimal(value, "open");
            var high = ReadDecimal(value, "high");
            var low = ReadDecimal(value, "low");
            var close = ReadDecimal(value, "close");
            if (timestamp is null || timestamp <= 0 || open is null || high is null || low is null || close is null)
                continue;
            if (low > Math.Min(open.Value, close.Value) || high < Math.Max(open.Value, close.Value) || low > high)
                continue;

            candles.Add(new Candle(
                open.Value,
                high.Value,
                low.Value,
                close.Value,
                DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)));
        }

        return candles.OrderBy(candle => candle.TimeUtc).ToArray();
    }

    private static decimal? ReadDecimal(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static long? ReadInt64(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return null;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static MarketDataResult Failure(MarketConnectionStatus status, string message) =>
        new(status, MarketFreshness.Unavailable, SourceName, null, null, message);

    public static MarketFreshness GetFreshness(DateTimeOffset quoteTimeUtc, DateTimeOffset nowUtc)
    {
        var age = nowUtc - quoteTimeUtc;
        return age <= TimeSpan.FromMinutes(FreshnessThresholdMinutes) && age >= TimeSpan.FromMinutes(-1)
            ? MarketFreshness.Fresh
            : MarketFreshness.Stale;
    }
}
