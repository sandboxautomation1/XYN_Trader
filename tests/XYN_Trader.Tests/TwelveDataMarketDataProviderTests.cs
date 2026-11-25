using System.Net;
using System.Text;
using XYN_Trader.Models;
using XYN_Trader.Services;

namespace XYN_Trader.Tests;

public sealed class TwelveDataMarketDataProviderTests
{
    [Fact]
    public async Task MissingApiKeyReturnsDisconnectedWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler();
        using var provider = CreateProvider(handler, null);

        var result = await provider.GetSnapshotAsync();

        Assert.Equal(MarketConnectionStatus.Disconnected, result.ConnectionStatus);
        Assert.Null(result.Snapshot);
        Assert.Contains("TwelveData:ApiKey", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ValidQuoteAndCandlesProduceLiveSnapshotAndKeepKeyServerSide()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/quote", StringComparison.Ordinal)
                ? JsonResponse($$"""{"symbol":"XAU/USD","close":"2712.45","percent_change":"0.62","datetime":"{{now:yyyy-MM-dd HH:mm:ss}}"}""")
                : JsonResponse($$"""{"status":"ok","values":[{"datetime":"{{now:yyyy-MM-dd HH:mm:ss}}","open":"2712","high":"2716","low":"2711","close":"2712.45"},{"datetime":"{{now.AddHours(-1):yyyy-MM-dd HH:mm:ss}}","open":"2710","high":"2714","low":"2708","close":"2712"}]}""");
        });
        using var provider = CreateProvider(handler, "test-key-not-a-secret");

        var result = await provider.GetSnapshotAsync();

        Assert.Equal(MarketConnectionStatus.Connected, result.ConnectionStatus);
        Assert.Equal(MarketFreshness.Fresh, result.Freshness);
        Assert.Equal("Twelve Data", result.Source);
        Assert.Equal(2712.45m, result.Snapshot!.Price);
        Assert.Equal("XAUUSD", result.Snapshot.Symbol);
        Assert.Equal(2, result.Snapshot.Candles.Count);
        Assert.True(result.Snapshot.Candles[0].TimeUtc < result.Snapshot.Candles[1].TimeUtc);
        Assert.All(handler.Requests, request =>
        {
            Assert.Contains("symbol=XAU%2FUSD", request);
            Assert.Contains("apikey=test-key-not-a-secret", request);
        });
    }

    [Fact]
    public async Task ProviderErrorDoesNotReturnDemoOrPartialSnapshot()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"message\":\"quota reached\"}", Encoding.UTF8, "application/json")
            });
        using var provider = CreateProvider(handler, "test-key-not-a-secret");

        var result = await provider.GetSnapshotAsync();

        Assert.Equal(MarketConnectionStatus.Error, result.ConnectionStatus);
        Assert.Equal(MarketFreshness.Unavailable, result.Freshness);
        Assert.Null(result.Snapshot);
        Assert.Contains("HTTP 429", result.Message);
        Assert.DoesNotContain("test-key-not-a-secret", result.Message);
    }

    [Fact]
    public async Task MissingQuoteTimestampIsAnErrorRatherThanFreshData()
    {
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/quote", StringComparison.Ordinal)
                ? JsonResponse("""{"close":"2712.45","percent_change":"0.62"}""")
                : JsonResponse("""{"status":"ok","values":[{"datetime":"2026-11-12 16:00:00","open":"2710","high":"2714","low":"2708","close":"2712"}]}"""));
        using var provider = CreateProvider(handler, "test-key-not-a-secret");

        var result = await provider.GetSnapshotAsync();

        Assert.Equal(MarketConnectionStatus.Error, result.ConnectionStatus);
        Assert.Equal(MarketFreshness.Unavailable, result.Freshness);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task OldQuoteIsReturnedAsStaleAndNotMarkedFresh()
    {
        var old = DateTimeOffset.UtcNow.AddHours(-1);
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/quote", StringComparison.Ordinal)
                ? JsonResponse($$"""{"close":"2712.45","datetime":"{{old:yyyy-MM-dd HH:mm:ss}}"}""")
                : JsonResponse($$"""{"status":"ok","values":[{"datetime":"{{old:yyyy-MM-dd HH:mm:ss}}","open":"2710","high":"2714","low":"2708","close":"2712"}]}"""));
        using var provider = CreateProvider(handler, "test-key-not-a-secret");

        var result = await provider.GetSnapshotAsync();

        Assert.Equal(MarketConnectionStatus.Connected, result.ConnectionStatus);
        Assert.Equal(MarketFreshness.Stale, result.Freshness);
        Assert.NotNull(result.Snapshot);
        Assert.Contains("older than", result.Message);
    }

    [Fact]
    public async Task UnsupportedIntervalDoesNotCallProvider()
    {
        var handler = new FakeHttpMessageHandler();
        using var provider = CreateProvider(handler, "test-key-not-a-secret");

        var result = await provider.GetSnapshotAsync("15months");

        Assert.Equal(MarketConnectionStatus.Error, result.ConnectionStatus);
        Assert.Null(result.Snapshot);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void FreshnessBoundaryIsExplicitAndRejectsFutureTimestamps()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(
            MarketFreshness.Fresh,
            TwelveDataMarketDataProvider.GetFreshness(
                now.AddMinutes(-TwelveDataMarketDataProvider.FreshnessThresholdMinutes),
                now));
        Assert.Equal(
            MarketFreshness.Stale,
            TwelveDataMarketDataProvider.GetFreshness(
                now.AddMinutes(-TwelveDataMarketDataProvider.FreshnessThresholdMinutes - 1),
                now));
        Assert.Equal(
            MarketFreshness.Stale,
            TwelveDataMarketDataProvider.GetFreshness(now.AddMinutes(2), now));
    }

    private static TwelveDataMarketDataProvider CreateProvider(
        FakeHttpMessageHandler handler,
        string? apiKey)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(TwelveDataMarketDataProvider.ApiBaseAddress)
        };
        return new TwelveDataMarketDataProvider(client, apiKey);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? responseFactory = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(responseFactory?.Invoke(request) ?? JsonResponse("{}"));
        }
    }
}
