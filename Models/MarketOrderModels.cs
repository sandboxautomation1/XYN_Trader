using System.Text.Json.Serialization;

namespace XYN_Trader.Models;

public sealed record MarketOrderRequest(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("volume")] decimal Volume,
    [property: JsonPropertyName("sl")] decimal StopLoss,
    [property: JsonPropertyName("tp")] decimal TakeProfit,
    [property: JsonPropertyName("comment")] string Comment,
    [property: JsonPropertyName("magic")] long Magic = 123456);

public sealed record MarketOrderReceipt(
    [property: JsonPropertyName("ticket")] long Ticket);

public sealed record MarketOrderResult(bool Succeeded, long? Ticket, string Message);
