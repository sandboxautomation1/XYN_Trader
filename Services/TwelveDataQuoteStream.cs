using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

public sealed record LiveQuote(decimal Price, DateTimeOffset UpdatedAtUtc);

public sealed class LiveQuoteStore
{
    private LiveQuote? _latest;

    public LiveQuote? Latest => Volatile.Read(ref _latest);

    public event Action<LiveQuote>? Updated;

    public void Publish(LiveQuote quote)
    {
        Volatile.Write(ref _latest, quote);
        Updated?.Invoke(quote);
    }
}

public sealed class TwelveDataQuoteStream : BackgroundService
{
    private static readonly Uri Endpoint = new("wss://ws.twelvedata.com/v1/quotes/price");
    private readonly string? _apiKey;
    private readonly LiveQuoteStore _store;
    private readonly ILogger<TwelveDataQuoteStream> _logger;

    public TwelveDataQuoteStream(
        IConfiguration configuration,
        LiveQuoteStore store,
        ILogger<TwelveDataQuoteStream> logger)
    {
        _apiKey = configuration["TwelveData:ApiKey"];
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogInformation("Twelve Data stream is disabled because no API key is configured.");
            return;
        }

        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                var endpoint = new UriBuilder(Endpoint)
                {
                    Query = $"apikey={Uri.EscapeDataString(_apiKey)}"
                }.Uri;
                await socket.ConnectAsync(endpoint, stoppingToken);
                await SendAsync(socket,
                    "{\"action\":\"subscribe\",\"params\":{\"symbols\":\"XAU/USD\"}}",
                    stoppingToken);
                _logger.LogInformation("Connected to Twelve Data price stream for XAU/USD.");
                retryDelay = TimeSpan.FromSeconds(2);
                await ReceiveAsync(socket, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is WebSocketException or HttpRequestException or IOException)
            {
                _logger.LogWarning("Twelve Data price stream disconnected: {Error}", exception.Message);
            }

            try
            {
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(10));
        using var message = new MemoryStream();
        var receiveTask = socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).AsTask();
        var heartbeatTask = heartbeat.WaitForNextTickAsync(cancellationToken).AsTask();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(receiveTask, heartbeatTask);
            if (completed == heartbeatTask)
            {
                if (!await heartbeatTask)
                    return;
                await SendAsync(socket, "{\"action\":\"heartbeat\"}", cancellationToken);
                heartbeatTask = heartbeat.WaitForNextTickAsync(cancellationToken).AsTask();
                continue;
            }

            var result = await receiveTask;
            if (result.MessageType == WebSocketMessageType.Close)
                return;
            message.Write(buffer, 0, result.Count);
            receiveTask = socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).AsTask();
            if (!result.EndOfMessage)
                continue;
            if (result.MessageType == WebSocketMessageType.Text)
                ParseAndPublish(Encoding.UTF8.GetString(message.ToArray()));
            message.SetLength(0);
        }
    }

    private void ParseAndPublish(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("event", out var eventName) || eventName.GetString() != "price")
                return;
            if (root.TryGetProperty("symbol", out var symbol) &&
                !string.Equals(symbol.GetString()?.Replace(" ", "", StringComparison.Ordinal), "XAU/USD", StringComparison.OrdinalIgnoreCase))
                return;
            if (!root.TryGetProperty("price", out var priceValue) ||
                !decimal.TryParse(priceValue.ToString(), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var price) || price <= 0 ||
                !root.TryGetProperty("timestamp", out var timestampValue) ||
                !long.TryParse(timestampValue.ToString(), out var unixTimestamp))
                return;

            _store.Publish(new LiveQuote(price, DateTimeOffset.FromUnixTimeSeconds(unixTimestamp)));
        }
        catch (JsonException)
        {
            _logger.LogDebug("Ignoring an invalid message from the Twelve Data price stream.");
        }
        catch (ArgumentOutOfRangeException)
        {
            _logger.LogDebug("Ignoring a price stream message with an invalid timestamp.");
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string message, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);
}
