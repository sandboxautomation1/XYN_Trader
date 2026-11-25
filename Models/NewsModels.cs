namespace XYN_Trader.Models;

public sealed record NewsArticle(
    string Title,
    string Url,
    string Source,
    DateTimeOffset PublishedAtUtc,
    string Impact,
    string ImpactTone,
    double SentimentScore = 0,
    string SentimentLabel = "Неутрално",
    double SentimentConfidence = 0,
    IReadOnlyList<string>? SentimentThemes = null);

public sealed record NewsFeedSnapshot(
    bool Available,
    string Source,
    DateTimeOffset? UpdatedAtUtc,
    string RiskLevel,
    int RiskScore,
    string RiskSummary,
    bool IsStale,
    IReadOnlyList<NewsArticle> Articles,
    string? Message,
    string SentimentLabel = "Неизвестен",
    double SentimentScore = 0,
    double SentimentConfidence = 0,
    int BullishCount = 0,
    int BearishCount = 0,
    int NeutralCount = 0)
{
    public static NewsFeedSnapshot Unavailable(string source, string message) =>
        new(false, source, null, "Неизвестен", 0, "Новинарският риск не може да бъде изчислен.", true, [], message);
}
