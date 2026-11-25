using System.Text.Json.Serialization;

namespace XYN_Trader.Models;

public sealed record UserSignalJournalEntry(
    [property: JsonPropertyName("signal_id")] string SignalId,
    [property: JsonPropertyName("is_favorite")] bool IsFavorite,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc);

public sealed record UserSignalJournalSaveRequest(
    bool IsFavorite,
    string Note);

public sealed record UserProfileSnapshot(
    [property: JsonPropertyName("profile_id")] string ProfileId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("signal_journal")] IReadOnlyList<UserSignalJournalEntry> SignalJournal);

public sealed record UserProfileResult(
    bool Available,
    UserProfileSnapshot? Profile,
    string Message)
{
    public static UserProfileResult Unavailable(string message) => new(false, null, message);

    public static UserProfileResult From(UserProfileSnapshot profile) =>
        new(true, profile, "Личният профил е зареден.");
}

public sealed record UserSignalJournalResult(
    bool Available,
    UserSignalJournalEntry? Entry,
    string Message)
{
    public static UserSignalJournalResult Unavailable(string message) => new(false, null, message);

    public static UserSignalJournalResult From(UserSignalJournalEntry? entry) =>
        new(true, entry, "Личният дневник е запазен.");
}
