using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

public sealed class CandidateGeneratorClient(HttpClient httpClient)
{
    public const string DefaultBaseAddress = "http://127.0.0.1:8001/";

    public async Task<CandidateGenerationResult> GenerateAsync(
        IReadOnlyList<Candle> candles,
        CancellationToken cancellationToken = default)
    {
        var request = new CandidateGeneratorRequest(
            "XAUUSD",
            "1h",
            candles.Select(candle => new CandidateGeneratorCandle(
                candle.TimeUtc,
                candle.Open,
                candle.High,
                candle.Low,
                candle.Close)).ToArray());

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "api/v1/candidates/generate",
                request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                return CandidateGenerationResult.Unavailable(
                    detail ?? $"Генераторът върна HTTP {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<CandidateGeneratorResponse>(
                cancellationToken: cancellationToken);
            return result is null
                ? CandidateGenerationResult.Unavailable("Генераторът върна празен отговор.")
                : CandidateGenerationResult.From(result);
        }
        catch (HttpRequestException)
        {
            return CandidateGenerationResult.Unavailable(
                "Python генераторът не е достъпен. Стартирайте AI услугата на адреса от AIService:BaseAddress.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CandidateGenerationResult.Unavailable("Изчакването на Python генератора изтече.");
        }
        catch (JsonException)
        {
            return CandidateGenerationResult.Unavailable("Python генераторът върна невалиден отговор.");
        }
    }

    public async Task<CandidateGenerationResult> GetLatestAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                "api/v1/candidates/latest",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                return CandidateGenerationResult.Unavailable(
                    detail ?? $"Няма запазени сценарии (HTTP {(int)response.StatusCode}).");
            }

            var result = await response.Content.ReadFromJsonAsync<CandidateGeneratorResponse>(
                cancellationToken: cancellationToken);
            return result is null
                ? CandidateGenerationResult.Unavailable("Няма запазени сценарии.")
                : CandidateGenerationResult.From(result);
        }
        catch (HttpRequestException)
        {
            return CandidateGenerationResult.Unavailable("Python услугата за запазените сценарии не е достъпна.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CandidateGenerationResult.Unavailable("Изчакването на запазените сценарии изтече.");
        }
        catch (JsonException)
        {
            return CandidateGenerationResult.Unavailable("Python услугата върна невалидни запазени сценарии.");
        }
    }

    public async Task<LearningIngestResult> IngestClosedCandlesAsync(
        IReadOnlyList<Candle> candles,
        bool runLearningCycle = true,
        CancellationToken cancellationToken = default)
    {
        var request = new LearningIngestRequest(
            "XAUUSD",
            "1h",
            candles.Select(candle => new CandidateGeneratorCandle(
                candle.TimeUtc,
                candle.Open,
                candle.High,
                candle.Low,
                candle.Close)).ToArray(),
            runLearningCycle);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "api/v1/learning/ingest",
                request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                return LearningIngestResult.Unavailable(
                    detail ?? $"Приемът на данни за обучение върна HTTP {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<LearningIngestResponse>(
                cancellationToken: cancellationToken);
            return result is null
                ? LearningIngestResult.Unavailable("AI услугата върна празен отговор за обучителните данни.")
                : LearningIngestResult.From(result);
        }
        catch (HttpRequestException)
        {
            return LearningIngestResult.Unavailable("AI услугата не е достъпна за прием на обучителни данни.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LearningIngestResult.Unavailable("Изчакването за прием на обучителни данни изтече.");
        }
        catch (JsonException)
        {
            return LearningIngestResult.Unavailable("AI услугата върна невалиден отговор за обучителните данни.");
        }
    }

    public async Task<LearningStatusResult> GetLearningStatusAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("api/v1/learning/status", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                return LearningStatusResult.Unavailable(
                    detail ?? $"AI услугата върна HTTP {(int)response.StatusCode} за обучителния статус.");
            }

            var status = await response.Content.ReadFromJsonAsync<LearningStatusSnapshot>(
                cancellationToken: cancellationToken);
            return status is null
                ? LearningStatusResult.Unavailable("AI услугата върна празен обучителен статус.")
                : LearningStatusResult.From(status);
        }
        catch (HttpRequestException)
        {
            return LearningStatusResult.Unavailable("AI услугата не е достъпна за обучителния статус.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LearningStatusResult.Unavailable("Изчакването на обучителния статус изтече.");
        }
        catch (JsonException)
        {
            return LearningStatusResult.Unavailable("AI услугата върна невалиден обучителен статус.");
        }
    }

    public async Task<LearningCandidateActivationResult> ActivateBestCandidateAsync(
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "api/v1/learning/activate-best",
                new LearningCandidateActivationRequest(approvedBy),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                return LearningCandidateActivationResult.Unavailable(
                    detail ?? $"AI услугата върна HTTP {(int)response.StatusCode} при активиране на кандидат.");
            }

            var activation = await response.Content.ReadFromJsonAsync<LearningCandidateActivationReceipt>(
                cancellationToken: cancellationToken);
            return activation is null
                ? LearningCandidateActivationResult.Unavailable("AI услугата върна празен отговор за активирането.")
                : LearningCandidateActivationResult.From(activation);
        }
        catch (HttpRequestException)
        {
            return LearningCandidateActivationResult.Unavailable("AI услугата не е достъпна за активиране на кандидат.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LearningCandidateActivationResult.Unavailable("Изчакването за активиране на кандидат изтече.");
        }
        catch (JsonException)
        {
            return LearningCandidateActivationResult.Unavailable("AI услугата върна невалиден отговор за активирането.");
        }
    }

    public async Task<UserProfileResult> GetUserProfileAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("api/v1/user/profile", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                return UserProfileResult.Unavailable(
                    detail ?? $"AI услугата върна HTTP {(int)response.StatusCode} за личния профил.");
            }

            var profile = await response.Content.ReadFromJsonAsync<UserProfileSnapshot>(
                cancellationToken: cancellationToken);
            return profile is null
                ? UserProfileResult.Unavailable("AI услугата върна празен личен профил.")
                : UserProfileResult.From(profile);
        }
        catch (HttpRequestException)
        {
            return UserProfileResult.Unavailable("AI услугата не е достъпна за личния профил.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UserProfileResult.Unavailable("Изчакването за личния профил изтече.");
        }
        catch (JsonException)
        {
            return UserProfileResult.Unavailable("AI услугата върна невалиден личен профил.");
        }
    }

    public async Task<UserSignalJournalResult> SaveUserSignalEntryAsync(
        string signalId,
        bool isFavorite,
        string note,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PutAsJsonAsync(
                $"api/v1/user/signals/{Uri.EscapeDataString(signalId)}",
                new UserSignalJournalSaveRequest(isFavorite, note),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                return UserSignalJournalResult.Unavailable(
                    detail ?? $"AI услугата върна HTTP {(int)response.StatusCode} при запис на дневника.");
            }

            var result = await response.Content.ReadFromJsonAsync<UserSignalJournalResponse>(
                cancellationToken: cancellationToken);
            return result is null
                ? UserSignalJournalResult.Unavailable("AI услугата върна празен отговор за дневника.")
                : UserSignalJournalResult.From(result.Entry);
        }
        catch (HttpRequestException)
        {
            return UserSignalJournalResult.Unavailable("AI услугата не е достъпна за запис на дневника.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UserSignalJournalResult.Unavailable("Изчакването за запис на дневника изтече.");
        }
        catch (JsonException)
        {
            return UserSignalJournalResult.Unavailable("AI услугата върна невалиден отговор за дневника.");
        }
    }

    public async Task<BacktestResultResponse> GetBacktestAsync(
        string horizon,
        string? start = null,
        string? end = null,
        CancellationToken cancellationToken = default)
    {
        var query = $"?horizon={Uri.EscapeDataString(horizon)}";
        if (!string.IsNullOrWhiteSpace(start))
            query += $"&start={Uri.EscapeDataString(start)}";
        if (!string.IsNullOrWhiteSpace(end))
            query += $"&end={Uri.EscapeDataString(end)}";

        try
        {
            using var response = await httpClient.GetAsync($"api/v1/backtesting/run{query}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                return BacktestResultResponse.Unavailable(
                    detail ?? $"Backtesting engine returned HTTP {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<BacktestResult>(cancellationToken: cancellationToken);
            return result is null
                ? BacktestResultResponse.Unavailable("Backtesting engine returned an empty response.")
                : BacktestResultResponse.From(result);
        }
        catch (HttpRequestException)
        {
            return BacktestResultResponse.Unavailable("Python AI service is unavailable for backtesting.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BacktestResultResponse.Unavailable("Backtest request timed out.");
        }
        catch (JsonException)
        {
            return BacktestResultResponse.Unavailable("Python AI service returned an invalid backtest report.");
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
            return document.RootElement.TryGetProperty("detail", out var detail)
                ? detail.ValueKind == JsonValueKind.String ? detail.GetString() : detail.ToString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CandidateGeneratorRequest(
        string Symbol,
        string Interval,
        IReadOnlyList<CandidateGeneratorCandle> Candles);

    private sealed record LearningIngestRequest(
        string Symbol,
        string Interval,
        IReadOnlyList<CandidateGeneratorCandle> Candles,
        bool RunLearningCycle);

    private sealed record UserSignalJournalResponse(
        [property: JsonPropertyName("entry")] UserSignalJournalEntry? Entry);

    private sealed record CandidateGeneratorCandle(
        DateTimeOffset TimestampUtc,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close);
}

public sealed record LearningIngestResult(
    bool Available,
    LearningIngestResponse? Intake,
    string Message)
{
    public static LearningIngestResult Unavailable(string message) => new(false, null, message);

    public static LearningIngestResult From(LearningIngestResponse intake) =>
        new(true, intake, "Обучителните свещи са приети.");
}
