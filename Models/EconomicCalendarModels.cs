namespace XYN_Trader.Models;

public sealed record EconomicCalendarEvent(
    string Id,
    string Title,
    DateOnly EventDate,
    DateTimeOffset? ScheduledAtUtc,
    string TimeZone,
    bool IsAllDay,
    string Country,
    string Category,
    string Impact,
    string Source,
    string SourceUrl,
    bool IsTentative = false);

public sealed record EconomicCalendarSourceStatus(
    string Name,
    bool Available,
    DateTimeOffset? UpdatedAtUtc,
    string? Message = null);

public sealed record EconomicCalendarSnapshot(
    bool Available,
    DateTimeOffset? UpdatedAtUtc,
    bool IsStale,
    IReadOnlyList<EconomicCalendarSourceStatus> Sources,
    IReadOnlyList<EconomicCalendarEvent> Events,
    string? Message)
{
    public static EconomicCalendarSnapshot Unavailable(string message) =>
        new(false, null, true, [], [], message);
}
