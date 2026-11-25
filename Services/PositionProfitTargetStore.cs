using System.Text.Json;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

/// <summary>
/// Persists per-position floating P/L thresholds so they survive dashboard and app restarts.
/// </summary>
public sealed class PositionProfitTargetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<PositionProfitTargetStore> _logger;
    private Dictionary<long, PositionProfitLimits> _limits;

    public PositionProfitTargetStore(
        IWebHostEnvironment environment,
        ILogger<PositionProfitTargetStore> logger)
    {
        _logger = logger;
        var dataDirectory = Path.Combine(environment.ContentRootPath, "data");
        _filePath = Path.Combine(dataDirectory, "position-profit-limits.json");
        _limits = Load();
    }

    public IReadOnlyDictionary<long, PositionProfitLimits> GetAll()
    {
        lock (_gate)
            return new Dictionary<long, PositionProfitLimits>(_limits);
    }

    public PositionProfitLimits? Get(long ticket)
    {
        lock (_gate)
            return _limits.GetValueOrDefault(ticket);
    }

    public void Set(long ticket, decimal? takeProfitAmount, decimal? stopLossAmount)
    {
        if (ticket <= 0)
            throw new ArgumentOutOfRangeException(nameof(ticket));
        if (takeProfitAmount is <= 0m || stopLossAmount is <= 0m)
            throw new ArgumentOutOfRangeException(nameof(takeProfitAmount), "Зададените прагове трябва да са положителни суми.");

        lock (_gate)
        {
            var updatedLimits = new Dictionary<long, PositionProfitLimits>(_limits);
            if (takeProfitAmount is null && stopLossAmount is null)
            {
                if (!updatedLimits.Remove(ticket))
                    return;
            }
            else
            {
                updatedLimits[ticket] = new PositionProfitLimits(
                    ticket,
                    takeProfitAmount,
                    stopLossAmount,
                    DateTimeOffset.UtcNow);
            }

            Save(updatedLimits);
            _limits = updatedLimits;
        }
    }

    public void RemoveClosedPositions(IReadOnlySet<long> openTickets)
    {
        lock (_gate)
        {
            var closedTickets = _limits.Keys.Where(ticket => !openTickets.Contains(ticket)).ToArray();
            if (closedTickets.Length == 0)
                return;

            var updatedLimits = new Dictionary<long, PositionProfitLimits>(_limits);
            foreach (var ticket in closedTickets)
                updatedLimits.Remove(ticket);
            Save(updatedLimits);
            _limits = updatedLimits;
        }
    }

    private Dictionary<long, PositionProfitLimits> Load()
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            var savedLimits = JsonSerializer.Deserialize<List<PositionProfitLimits>>(
                File.ReadAllText(_filePath), JsonOptions);
            return savedLimits?
                .Where(limit => limit.Ticket > 0 &&
                    (limit.TakeProfitAmount is null or > 0m) &&
                    (limit.StopLossAmount is null or > 0m) &&
                    (limit.TakeProfitAmount is not null || limit.StopLossAmount is not null))
                .GroupBy(limit => limit.Ticket)
                .Select(group => group.OrderByDescending(limit => limit.UpdatedAtUtc).First())
                .ToDictionary(limit => limit.Ticket) ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(error, "Could not load saved position profit limits from {Path}.", _filePath);
            return [];
        }
    }

    private void Save(IReadOnlyDictionary<long, PositionProfitLimits> limits)
    {
        var temporaryPath = $"{_filePath}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(limits.Values.OrderBy(limit => limit.Ticket), JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(error, "Could not save position profit limits to {Path}.", _filePath);
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(cleanupError, "Could not remove temporary position limits file {Path}.", temporaryPath);
            }

            throw;
        }
    }
}
