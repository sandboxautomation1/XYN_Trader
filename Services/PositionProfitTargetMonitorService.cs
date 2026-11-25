using XYN_Trader.Models;

namespace XYN_Trader.Services;

/// <summary>
/// Closes positions whose floating P/L reaches their configured cash thresholds.
/// </summary>
public sealed class PositionProfitTargetMonitorService(
    IServiceScopeFactory scopeFactory,
    PositionProfitTargetStore targetStore,
    ILogger<PositionProfitTargetMonitorService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CloseRetryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnavailableWarningInterval = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastUnavailableWarningUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var closeRequestsSent = new HashSet<long>();
        var closeRetryAfter = new Dictionary<long, DateTimeOffset>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckTargetsAsync(closeRequestsSent, closeRetryAfter, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Automatic position profit target check failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task CheckTargetsAsync(
        HashSet<long> closeRequestsSent,
        Dictionary<long, DateTimeOffset> closeRetryAfter,
        CancellationToken cancellationToken)
    {
        if (targetStore.GetAll().Count == 0)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var marketProvider = scope.ServiceProvider.GetRequiredService<Mt5MarketDataProvider>();
        var result = await marketProvider.GetOpenPositionsAsync(cancellationToken);
        if (!result.Available)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastUnavailableWarningUtc >= UnavailableWarningInterval)
            {
                logger.LogWarning("Automatic position profit targets paused: {Message}", result.Message);
                _lastUnavailableWarningUtc = now;
            }
            return;
        }

        var openTickets = result.Positions.Select(position => position.Ticket).ToHashSet();
        targetStore.RemoveClosedPositions(openTickets);
        closeRequestsSent.RemoveWhere(ticket => !openTickets.Contains(ticket));
        foreach (var ticket in closeRetryAfter.Keys.Where(ticket => !openTickets.Contains(ticket)).ToArray())
            closeRetryAfter.Remove(ticket);

        var limitsByTicket = targetStore.GetAll();
        var currentTime = DateTimeOffset.UtcNow;
        foreach (var position in result.Positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (closeRequestsSent.Contains(position.Ticket) ||
                (closeRetryAfter.TryGetValue(position.Ticket, out var retryTime) && retryTime > currentTime) ||
                !limitsByTicket.TryGetValue(position.Ticket, out var limits))
                continue;

            var trigger = GetTrigger(position, limits);
            if (trigger is null)
                continue;

            var closeResult = await marketProvider.ClosePositionAsync(position.Ticket, cancellationToken);
            if (!closeResult.Succeeded)
            {
                closeRetryAfter[position.Ticket] = currentTime.Add(CloseRetryInterval);
                logger.LogWarning(
                    "Automatic close for position {Ticket} was rejected after reaching {Trigger}: {Message}",
                    position.Ticket,
                    trigger,
                    closeResult.Message);
                continue;
            }

            closeRetryAfter.Remove(position.Ticket);
            closeRequestsSent.Add(position.Ticket);
            logger.LogWarning(
                "Sent automatic close for position {Ticket}, symbol {Symbol}, after reaching {Trigger}. Current floating P/L: {Profit} {Currency}.",
                position.Ticket,
                position.Symbol,
                trigger,
                position.Profit,
                result.AccountCurrency ?? "account currency");
        }
    }

    private static string? GetTrigger(Mt5OpenPosition position, PositionProfitLimits limits)
    {
        if (limits.TakeProfitAmount is { } takeProfit && position.Profit >= takeProfit)
            return $"Take Profit {takeProfit:N2}";
        if (limits.StopLossAmount is { } stopLoss && position.Profit <= -stopLoss)
            return $"Stop Loss -{stopLoss:N2}";
        return null;
    }
}
