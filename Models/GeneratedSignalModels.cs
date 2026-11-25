using System.Text.Json.Serialization;

namespace XYN_Trader.Models;

public sealed record GeneratedSignalCandidate(
    string SignalId,
    string Symbol,
    string Timeframe,
    DateTimeOffset TimestampUtc,
    string Direction,
    string Status,
    int HorizonHours,
    decimal EntryLow,
    decimal EntryHigh,
    decimal StopLoss,
    decimal Tp1,
    decimal Tp2,
    decimal Tp3,
    decimal Tp1RiskReward,
    decimal Tp2RiskReward,
    decimal Tp3RiskReward,
    int ZoneTouchCount,
    decimal Adx14,
    string PolicyVersion,
    string PolicySha256,
    double? ModelProbabilityPositiveOutcome = null,
    double? ModelRawProbabilityPositiveOutcome = null,
    int? ModelOutcomeHorizonHours = null,
    string? ModelVersion = null,
    string? ModelCalibrationMethod = null,
    bool? ModelIsCalibrated = null,
    string? EntryOrderType = null,
    decimal? EntryPrice = null);

public sealed record CandidateGeneratorResponse(
    string Symbol,
    string Timeframe,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset AsOfUtc,
    int HorizonHours,
    string PolicyVersion,
    string PolicySha256,
    string ModelStatus,
    bool IsStale,
    IReadOnlyList<GeneratedSignalCandidate> Candidates,
    string Message,
    int? ModelOutcomeHorizonHours = null);

public sealed record CandidateGenerationResult(
    bool Available,
    string? Symbol,
    string? Timeframe,
    DateTimeOffset? GeneratedAtUtc,
    DateTimeOffset? AsOfUtc,
    int? HorizonHours,
    string? PolicyVersion,
    string? PolicySha256,
    string? ModelStatus,
    int? ModelOutcomeHorizonHours,
    bool IsStale,
    IReadOnlyList<GeneratedSignalCandidate> Candidates,
    string Message)
{
    public static CandidateGenerationResult Unavailable(string message) =>
        new(false, null, null, null, null, null, null, null, null, null, false, [], message);

    public static CandidateGenerationResult From(CandidateGeneratorResponse response) =>
        new(
            true,
            response.Symbol,
            response.Timeframe,
            response.GeneratedAtUtc,
            response.AsOfUtc,
            response.HorizonHours,
            response.PolicyVersion,
            response.PolicySha256,
            response.ModelStatus,
            response.ModelOutcomeHorizonHours,
            response.IsStale,
            response.Candidates,
            response.Message);
}

public sealed record LearningIngestResponse(
    [property: JsonPropertyName("accepted_candles")] int AcceptedCandles,
    [property: JsonPropertyName("changed_candles")] int ChangedCandles,
    [property: JsonPropertyName("skipped_open_candles")] int SkippedOpenCandles,
    [property: JsonPropertyName("stored_candles")] int StoredCandles,
    [property: JsonPropertyName("training_scheduled")] bool TrainingScheduled);

public sealed record LearningHorizonStatus(
    [property: JsonPropertyName("training_state")] string TrainingState,
    [property: JsonPropertyName("candidate_rows")] int CandidateRows,
    [property: JsonPropertyName("matured_labels")] int MaturedLabels,
    [property: JsonPropertyName("positive_labels")] int PositiveLabels,
    [property: JsonPropertyName("minimum_labels")] int MinimumLabels,
    [property: JsonPropertyName("dataset_sha256")] string? DatasetSha256,
    [property: JsonPropertyName("candidate_artifact")] string? CandidateArtifact,
    [property: JsonPropertyName("model_version")] string? ModelVersion,
    [property: JsonPropertyName("holdout_auc")] double? HoldoutAuc,
    [property: JsonPropertyName("holdout_brier_score")] double? HoldoutBrierScore,
    [property: JsonPropertyName("baseline_brier_score")] double? BaselineBrierScore,
    [property: JsonPropertyName("beats_prevalence_baseline_brier")] bool? BeatsPrevalenceBaselineBrier,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("calibration_method")] string CalibrationMethod = "none",
    [property: JsonPropertyName("is_calibrated")] bool IsCalibrated = false,
    [property: JsonPropertyName("calibration_rows")] int CalibrationRows = 0,
    [property: JsonPropertyName("raw_holdout_brier_score")] double? RawHoldoutBrierScore = null,
    [property: JsonPropertyName("raw_holdout_expected_calibration_error")] double? RawHoldoutExpectedCalibrationError = null,
    [property: JsonPropertyName("holdout_expected_calibration_error")] double? HoldoutExpectedCalibrationError = null,
    [property: JsonPropertyName("activation_eligible")] bool ActivationEligible = false,
    [property: JsonPropertyName("activation_reason")] string? ActivationReason = null,
    [property: JsonPropertyName("active_model_version")] string? ActiveModelVersion = null);

public sealed record LearningCandidateRecommendation(
    [property: JsonPropertyName("horizon")] string Horizon,
    [property: JsonPropertyName("model_version")] string ModelVersion,
    [property: JsonPropertyName("holdout_auc")] double HoldoutAuc,
    [property: JsonPropertyName("holdout_brier_score")] double HoldoutBrierScore,
    [property: JsonPropertyName("baseline_brier_score")] double BaselineBrierScore,
    [property: JsonPropertyName("brier_skill_score")] double BrierSkillScore,
    [property: JsonPropertyName("calibration_method")] string CalibrationMethod,
    [property: JsonPropertyName("is_calibrated")] bool IsCalibrated,
    [property: JsonPropertyName("matured_labels")] int MaturedLabels,
    [property: JsonPropertyName("positive_labels")] int PositiveLabels);

public sealed record LearningCandidateActivationRequest(
    [property: JsonPropertyName("approvedBy")] string ApprovedBy);

public sealed record LearningCandidateActivationReceipt(
    [property: JsonPropertyName("horizon")] string Horizon,
    [property: JsonPropertyName("modelVersion")] string ModelVersion,
    [property: JsonPropertyName("approvedBy")] string ApprovedBy,
    [property: JsonPropertyName("promotedAtUtc")] DateTimeOffset PromotedAtUtc,
    [property: JsonPropertyName("archivedPreviousArtifact")] string? ArchivedPreviousArtifact);

public sealed record LearningStatusSnapshot(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("candle_interval")] string CandleInterval,
    [property: JsonPropertyName("stored_candles")] int StoredCandles,
    [property: JsonPropertyName("maximum_stored_candles")] int MaximumStoredCandles,
    [property: JsonPropertyName("last_ingest_utc")] DateTimeOffset? LastIngestUtc,
    [property: JsonPropertyName("last_cycle_utc")] DateTimeOffset? LastCycleUtc,
    [property: JsonPropertyName("last_error")] string? LastError,
    [property: JsonPropertyName("active_models")] IReadOnlyList<string> ActiveModels,
    [property: JsonPropertyName("approval_required")] bool ApprovalRequired,
    [property: JsonPropertyName("candidate_count")] int? CandidateCount,
    [property: JsonPropertyName("horizons")] IReadOnlyDictionary<string, LearningHorizonStatus> Horizons,
    [property: JsonPropertyName("recommended_candidate")] LearningCandidateRecommendation? RecommendedCandidate = null);

public sealed record LearningStatusResult(
    bool Available,
    LearningStatusSnapshot? Status,
    string Message)
{
    public static LearningStatusResult Unavailable(string message) => new(false, null, message);

    public static LearningStatusResult From(LearningStatusSnapshot status) =>
        new(true, status, "Обучителният статус е наличен.");
}

public sealed record LearningCandidateActivationResult(
    bool Available,
    LearningCandidateActivationReceipt? Activation,
    string Message)
{
    public static LearningCandidateActivationResult Unavailable(string message) => new(false, null, message);

    public static LearningCandidateActivationResult From(LearningCandidateActivationReceipt activation) =>
        new(true, activation, "Кандидатът е активиран.");
}
