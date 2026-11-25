using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

public sealed class TwelveDataMarketDataProvider : IDisposable
{
    public const string ApiBaseAddress = "https://api.twelvedata.com/";
    private const string Symbol = "XAU/USD";
    private const string SourceName = "Twelve Data";
    private const int DefaultCandleCount = 48;
    private const int SignalCandleCount = 300;
    private static readonly TimeSpan QuoteCacheDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CandleCacheDuration = TimeSpan.FromSeconds(15);
    public const int FreshnessThresholdMinutes = 15;
    private static readonly string[] SupportedIntervals =
        ["1min", "5min", "15min", "30min", "1h", "4h", "1day"];
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly ConcurrentDictionary<string, (string Body, DateTimeOffset FetchedAtUtc)> _responseCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestGates = new(StringComparer.Ordinal);

    public TwelveDataMarketDataProvider(HttpClient httpClient, string? apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public async Task<MarketDataResult> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => await GetSnapshotAsync("1h", cancellationToken);

    public async Task<MarketDataResult> GetSnapshotAsync(
        string interval,
        CancellationToken cancellationToken = default)
    {
        if (_apiKey is null)
        {
            return Failure(
                MarketConnectionStatus.Disconnected,
                "Twelve Data API key is not configured. Set TwelveData:ApiKey in the server's appsettings.json.");
        }

        if (!SupportedIntervals.Contains(interval, StringComparer.Ordinal))
            return Failure(MarketConnectionStatus.Error, "The requested candle interval is not supported.");

        var quoteTask = GetCachedJsonAsync(
            "quote:XAUUSD",
            $"quote?symbol={Uri.EscapeDataString(Symbol)}&timezone=UTC&apikey={Uri.EscapeDataString(_apiKey)}",
            QuoteCacheDuration,
            cancellationToken);
        var candleCount = interval == "1h" ? SignalCandleCount : DefaultCandleCount;
        var candlesTask = GetCachedJsonAsync(
            $"series:XAUUSD:{interval}:{candleCount}",
            $"time_series?symbol={Uri.EscapeDataString(Symbol)}&interval={Uri.EscapeDataString(interval)}&outputsize={candleCount}&timezone=UTC&apikey={Uri.EscapeDataString(_apiKey)}",
            CandleCacheDuration,
            cancellationToken);

        await Task.WhenAll(quoteTask, candlesTask);
        var quoteResponse = await quoteTask;
        if (quoteResponse.Error is not null)
            return quoteResponse.Error;

        var candlesResponse = await candlesTask;
        if (candlesResponse.Error is not null)
            return candlesResponse.Error;

        try
        {
            using var quoteDocument = JsonDocument.Parse(quoteResponse.Body!);
            using var candlesDocument = JsonDocument.Parse(candlesResponse.Body!);
            var quote = quoteDocument.RootElement;
            var series = candlesDocument.RootElement;

            if (IsProviderError(quote) || IsProviderError(series))
                return Failure(MarketConnectionStatus.Error, "Twelve Data rejected the market data request.");

            var price = ReadDecimal(quote, "close");
            var changePercent = ReadDecimal(quote, "percent_change");
            var quoteTime = ReadUtcDateTime(quote, "datetime");
            var candles = ReadCandles(series);

            if (price is null || quoteTime is null || candles.Count == 0)
                return Failure(MarketConnectionStatus.Error, "Twelve Data returned an incomplete quote or candle series.");

            var now = DateTimeOffset.UtcNow;
            var freshness = GetFreshness(quoteTime.Value, now);
            var snapshot = new MarketSnapshot(
                Symbol: "XAUUSD",
                Currency: "USD",
                Price: price.Value,
                ChangePercent: changePercent ?? 0m,
                UpdatedAtUtc: quoteTime.Value,
                Regime: "Not calculated",
                NewsRisk: "Not available",
                ModelVersion: "No AI model configured",
                Candles: candles);

            return new MarketDataResult(
                MarketConnectionStatus.Connected,
                freshness,
                SourceName,
                now,
                snapshot,
                freshness == MarketFreshness.Stale
                    ? "The latest provider quote is older than the 15-minute freshness window."
                    : null);
        }
        catch (JsonException)
        {
            return Failure(MarketConnectionStatus.Error, "Twelve Data returned an invalid response.");
        }
        catch (FormatException)
        {
            return Failure(MarketConnectionStatus.Error, "Twelve Data returned an invalid numeric or timestamp value.");
        }
        catch (InvalidOperationException)
        {
            return Failure(MarketConnectionStatus.Error, "Twelve Data returned an invalid response shape.");
        }
    }

    private async Task<(string? Body, MarketDataResult? Error)> GetCachedJsonAsync(
        string cacheKey,
        string relativeUri,
        TimeSpan cacheDuration,
        CancellationToken cancellationToken)
    {
        if (TryGetCachedResponse(cacheKey, cacheDuration, out var cachedBody))
            return (cachedBody, null);

        var gate = _requestGates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedResponse(cacheKey, cacheDuration, out cachedBody))
                return (cachedBody, null);

            var response = await GetJsonAsync(relativeUri, cancellationToken);
            if (response.Body is not null && IsCacheableResponse(response.Body))
                _responseCache[cacheKey] = (response.Body, DateTimeOffset.UtcNow);
            return response;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetCachedResponse(string cacheKey, TimeSpan cacheDuration, out string body)
    {
        if (_responseCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAtUtc < cacheDuration)
        {
            body = cached.Body;
            return true;
        }

        body = string.Empty;
        return false;
    }

    private static bool IsCacheableResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return !IsProviderError(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<(string? Body, MarketDataResult? Error)> GetJsonAsync(
        string relativeUri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(relativeUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var message = status == StatusCodes.Status429TooManyRequests
                    ? BuildRateLimitMessage(response.Headers.RetryAfter?.Delta)
                    : $"Twelve Data request failed with HTTP {status}. Check provider access and quota.";
                return (null, Failure(
                    MarketConnectionStatus.Error,
                    message));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return (body, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(MarketConnectionStatus.Error, "Twelve Data request timed out."));
        }
        catch (HttpRequestException)
        {
            return (null, Failure(MarketConnectionStatus.Error, "Twelve Data could not be reached."));
        }
    }

    private static string BuildRateLimitMessage(TimeSpan? retryAfter)
    {
        var retryHint = retryAfter is { } delay
            ? $" Опитайте отново след около {Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))} сек."
            : " Изчакайте нулирането на квотата и проверете използването в Twelve Data Dashboard.";
        return $"Twelve Data върна HTTP 429: достигнат е API кредитният или честотният лимит за този ключ.{retryHint}";
    }

    private static bool IsProviderError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return true;

        if (root.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String &&
            string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            return true;

        return root.TryGetProperty("code", out var code) &&
               code.ValueKind == JsonValueKind.Number &&
               code.TryGetInt32(out var statusCode) &&
               statusCode >= 400;
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

    private static DateTimeOffset? ReadUtcDateTime(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var result)
            ? result
            : null;
    }

    private static IReadOnlyList<Candle> ReadCandles(JsonElement root)
    {
        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            return [];

        var candles = new List<Candle>();
        foreach (var value in values.EnumerateArray())
        {
            var open = ReadDecimal(value, "open");
            var high = ReadDecimal(value, "high");
            var low = ReadDecimal(value, "low");
            var close = ReadDecimal(value, "close");
            var time = ReadUtcDateTime(value, "datetime");
            if (open is null || high is null || low is null || close is null || time is null)
                return [];

            candles.Add(new Candle(open.Value, high.Value, low.Value, close.Value, time.Value));
        }

        candles.Reverse();
        return candles;
    }

    private static MarketDataResult Failure(MarketConnectionStatus status, string message) =>
        new(status, MarketFreshness.Unavailable, SourceName, null, null, message);

    public static MarketFreshness GetFreshness(DateTimeOffset quoteTimeUtc, DateTimeOffset nowUtc)
    {
        var age = nowUtc - quoteTimeUtc;
        return age <= TimeSpan.FromMinutes(FreshnessThresholdMinutes) &&
               age >= TimeSpan.FromMinutes(-1)
            ? MarketFreshness.Fresh
            : MarketFreshness.Stale;
    }

    public void Dispose() => _httpClient.Dispose();
}
