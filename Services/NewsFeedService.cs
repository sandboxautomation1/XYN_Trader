using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

public sealed class NewsFeedService
{
    public const string DefaultBaseAddress = "https://api.gdeltproject.org/api/v2/doc/doc";
    public const string SourceName = "GDELT DOC 2.0";
    private static readonly string DefaultQuery =
        "(gold OR XAUUSD OR \"precious metals\" OR \"Federal Reserve\" OR FOMC OR inflation OR CPI OR PCE OR payroll OR \"US dollar\") sourcelang:english";
    private static readonly string[] HighImpactTerms =
    [
        "fomc", "rate decision", "interest rate", "inflation", "consumer price", "cpi", "pce",
        "nonfarm", "payroll", "unemployment", "powell", "fed chair", "war", "sanction",
        "invasion", "missile", "attack", "tariff", "default", "bank failure", "recession", "crisis"
    ];
    private static readonly string[] MediumImpactTerms =
    [
        "federal reserve", "central bank", "treasury yield", "bond yield", "us dollar", "gold price",
        "employment", "gross domestic product", "gdp", "trade policy", "geopolitical"
    ];
    private static readonly (string Phrase, double Weight, string Theme)[] GoldSentimentTerms =
    [
        ("gold surges", 1.0, "Ценово движение"), ("gold rallies", 1.0, "Ценово движение"),
        ("gold gains", 0.8, "Ценово движение"), ("gold rises", 0.8, "Ценово движение"),
        ("gold climbs", 0.8, "Ценово движение"), ("gold advances", 0.7, "Ценово движение"),
        ("gold hits record", 1.0, "Ценово движение"), ("bullion gains", 0.8, "Ценово движение"),
        ("gold demand", 0.6, "Търсене"), ("gold etf inflows", 0.8, "Потоци"),
        ("central bank buying", 0.7, "Потоци"), ("dovish", 0.6, "Лихви и доходност"),
        ("rate cut", 0.6, "Лихви и доходност"), ("rate cuts", 0.6, "Лихви и доходност"),
        ("cut rates", 0.7, "Лихви и доходност"), ("cuts rates", 0.7, "Лихви и доходност"),
        ("lower interest rates", 0.7, "Лихви и доходност"), ("yields fall", 0.7, "Лихви и доходност"),
        ("yields decline", 0.7, "Лихви и доходност"), ("lower yields", 0.6, "Лихви и доходност"),
        ("weaker dollar", 0.7, "Щатски долар"), ("dollar falls", 0.7, "Щатски долар"),
        ("dollar weakens", 0.7, "Щатски долар"), ("safe haven", 0.5, "Геополитика и риск"),
        ("investors seek safety", 0.5, "Геополитика и риск"), ("geopolitical tensions", 0.3, "Геополитика и риск"),
        ("war escalates", 0.4, "Геополитика и риск"), ("risk aversion", 0.4, "Геополитика и риск"),
        ("gold plunges", -1.0, "Ценово движение"), ("gold slumps", -0.9, "Ценово движение"),
        ("gold drops", -0.8, "Ценово движение"), ("gold falls", -0.8, "Ценово движение"),
        ("gold slides", -0.8, "Ценово движение"), ("gold slips", -0.7, "Ценово движение"),
        ("gold declines", -0.8, "Ценово движение"), ("gold lower", -0.6, "Ценово движение"),
        ("gold retreats", -0.7, "Ценово движение"), ("bullion falls", -0.8, "Ценово движение"),
        ("gold etf outflows", -0.8, "Потоци"), ("hawkish", -0.6, "Лихви и доходност"),
        ("rate hike", -0.7, "Лихви и доходност"), ("raises rates", -0.7, "Лихви и доходност"),
        ("higher interest rates", -0.7, "Лихви и доходност"), ("yields rise", -0.7, "Лихви и доходност"),
        ("yields soar", -0.8, "Лихви и доходност"), ("higher yields", -0.7, "Лихви и доходност"),
        ("stronger dollar", -0.7, "Щатски долар"), ("dollar rises", -0.7, "Щатски долар"),
        ("dollar strengthens", -0.7, "Щатски долар"), ("risk appetite improves", -0.4, "Геополитика и риск"),
        ("safe haven demand fades", -0.5, "Геополитика и риск")
    ];
    private static readonly Regex SentimentNegation = new(
        @"\b(no|not|without|never|fails? to|denies?|denied|doesn t|isn t|wasn t|can t|won t)\b\s+(?:[a-z]+\s+){0,3}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NewsFeedService> _logger;
    private readonly string _query;
    private readonly string _timespan;
    private readonly int _maxArticles;
    private readonly int _refreshMinutes;
    private readonly bool _enabled;
    private readonly string _cachePath;
    private readonly string _rateLimitStatePath;
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private NewsFeedSnapshot? _cachedSnapshot;
    private DateTimeOffset _cacheExpiresAtUtc;
    private DateTimeOffset _retryNotBeforeUtc;
    private int _consecutiveRateLimitCount;

    public NewsFeedService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<NewsFeedService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _query = configuration["NewsFeed:Query"] ?? DefaultQuery;
        _timespan = configuration["NewsFeed:Timespan"] ?? "1d";
        _maxArticles = ReadBoundedInt(configuration["NewsFeed:MaxArticles"], 12, 1, 50);
        _refreshMinutes = ReadBoundedInt(configuration["NewsFeed:RefreshMinutes"], 5, 1, 60);
        _enabled = !bool.TryParse(configuration["NewsFeed:Enabled"], out var enabled) || enabled;
        _cachePath = configuration["NewsFeed:CachePath"] ??
            Path.Combine(environment.ContentRootPath, "data", "news-cache.json");
        _rateLimitStatePath = _cachePath + ".ratelimit.json";
        _cachedSnapshot = LoadPersistedCache(_cachePath);
        _cacheExpiresAtUtc = _cachedSnapshot?.UpdatedAtUtc?.Add(RefreshInterval)
            ?? DateTimeOffset.MinValue;
        var rateLimitState = LoadRateLimitState(_rateLimitStatePath);
        _retryNotBeforeUtc = rateLimitState.RetryNotBeforeUtc > DateTimeOffset.UtcNow
            ? rateLimitState.RetryNotBeforeUtc
            : DateTimeOffset.MinValue;
        _consecutiveRateLimitCount = rateLimitState.ConsecutiveRateLimitCount;
    }

    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(_refreshMinutes);

    public NewsFeedSnapshot GetCachedSnapshot()
    {
        if (!_enabled)
            return NewsFeedSnapshot.Unavailable(SourceName, "Новинарският feed е изключен в NewsFeed:Enabled.");

        if (DateTimeOffset.UtcNow < _retryNotBeforeUtc)
            return GetRateLimitedSnapshot();

        if (_cachedSnapshot is null)
            return NewsFeedSnapshot.Unavailable(SourceName, "Зареждане на новините от GDELT…");

        if (!_cachedSnapshot.Available)
            return _cachedSnapshot;

        if (DateTimeOffset.UtcNow < _cacheExpiresAtUtc && !_cachedSnapshot.IsStale)
            return _cachedSnapshot;

        return _cachedSnapshot with
        {
            IsStale = true,
            Message = "Показани са последните запазени новини, докато feed-ът се обновява."
        };
    }

    public async Task<NewsFeedSnapshot> GetLatestAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled)
            return NewsFeedSnapshot.Unavailable(SourceName, "Новинарският feed е изключен в NewsFeed:Enabled.");

        if (DateTimeOffset.UtcNow < _retryNotBeforeUtc)
            return GetRateLimitedSnapshot();

        if (!forceRefresh && _cachedSnapshot is { IsStale: false } && DateTimeOffset.UtcNow < _cacheExpiresAtUtc)
            return _cachedSnapshot;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // GDELT may rate limit callers. A manual refresh must not bypass the
            // cooldown, otherwise the button can turn one 429 into a request loop.
            if (DateTimeOffset.UtcNow < _retryNotBeforeUtc)
                return GetRateLimitedSnapshot();

            if (!forceRefresh && _cachedSnapshot is { IsStale: false } && DateTimeOffset.UtcNow < _cacheExpiresAtUtc)
                return _cachedSnapshot;

            try
            {
                var snapshot = await FetchAsync(cancellationToken);
                _cachedSnapshot = snapshot;
                _cacheExpiresAtUtc = DateTimeOffset.UtcNow.Add(RefreshInterval);
                _retryNotBeforeUtc = DateTimeOffset.MinValue;
                _consecutiveRateLimitCount = 0;
                PersistCache(snapshot);
                ClearRateLimitState();
                return snapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                _logger.LogWarning(exception, "GDELT news refresh failed.");
                var isRateLimited = exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
                if (isRateLimited)
                {
                    var retryAfterUtc = exception is GdeltRateLimitException rateLimitException
                        ? rateLimitException.RetryAfterUtc
                        : null;
                    _consecutiveRateLimitCount++;
                    _retryNotBeforeUtc = GetRateLimitRetryAtUtc(retryAfterUtc, DateTimeOffset.UtcNow);
                    _cacheExpiresAtUtc = _retryNotBeforeUtc;
                    PersistRateLimitState();
                    if (_cachedSnapshot is not null)
                        _cachedSnapshot = _cachedSnapshot with { IsStale = true };
                    return GetRateLimitedSnapshot();
                }

                _cacheExpiresAtUtc = DateTimeOffset.UtcNow.Add(RefreshInterval);
                if (_cachedSnapshot is not null)
                {
                    _cachedSnapshot = _cachedSnapshot with
                    {
                        IsStale = true,
                        Message = $"{DescribeFailure(exception)} Показани са последните запазени новини."
                    };
                    return _cachedSnapshot;
                }

                var unavailable = NewsFeedSnapshot.Unavailable(SourceName, DescribeFailure(exception));
                _cachedSnapshot = unavailable;
                return unavailable;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private NewsFeedSnapshot GetRateLimitedSnapshot()
    {
        var remaining = Math.Max(1, (int)Math.Ceiling((_retryNotBeforeUtc - DateTimeOffset.UtcNow).TotalMinutes));
        if (_cachedSnapshot is null)
            return NewsFeedSnapshot.Unavailable(SourceName,
                $"GDELT временно ограничи заявките (HTTP 429). Следващ опит след около {remaining} мин.");

        return _cachedSnapshot with
        {
            IsStale = true,
            Message = $"GDELT временно ограничи заявките (HTTP 429). Показани са последните запазени новини; следващ опит след около {remaining} мин."
        };
    }

    private async Task<NewsFeedSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("GdeltNews");
        var requestUri = $"?query={Uri.EscapeDataString(_query)}&mode=ArtList&format=json&maxrecords={_maxArticles}&timespan={Uri.EscapeDataString(_timespan)}&sort=DateDesc";
        using var response = await client.GetAsync(requestUri, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var retryAfterUtc = response.Headers.RetryAfter?.Date;
            if (retryAfterUtc is null && response.Headers.RetryAfter?.Delta is { } retryDelay && retryDelay > TimeSpan.Zero)
                retryAfterUtc = DateTimeOffset.UtcNow.Add(retryDelay);
            throw new GdeltRateLimitException(retryAfterUtc);
        }
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("articles", out var articleArray) ||
            articleArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GDELT returned an unexpected article-list response.");
        }

        var now = DateTimeOffset.UtcNow;
        var articles = articleArray.EnumerateArray()
            .Select(ReadArticle)
            .Where(article => article is not null)
            .Select(article => article!)
            .Where(article => article.PublishedAtUtc <= now.AddMinutes(5) && article.PublishedAtUtc >= now.AddDays(-1))
            .DistinctBy(article => article.Url, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(article => article.PublishedAtUtc)
            .Take(_maxArticles)
            .ToArray();

        var risk = CalculateRisk(articles, now);
        var sentiment = CalculateSentiment(articles, now);
        return new NewsFeedSnapshot(
            Available: true,
            Source: SourceName,
            UpdatedAtUtc: now,
            RiskLevel: risk.Level,
            RiskScore: risk.Score,
            RiskSummary: risk.Summary,
            IsStale: false,
            Articles: articles,
            Message: null,
            SentimentLabel: sentiment.Label,
            SentimentScore: sentiment.Score,
            SentimentConfidence: sentiment.Confidence,
            BullishCount: sentiment.Bullish,
            BearishCount: sentiment.Bearish,
            NeutralCount: sentiment.Neutral);
    }

    private static NewsArticle? ReadArticle(JsonElement value)
    {
        var title = ReadString(value, "title");
        var url = ReadString(value, "url");
        var date = ReadString(value, "seendate");
        if (string.IsNullOrWhiteSpace(title) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var articleUri) ||
            (articleUri.Scheme != Uri.UriSchemeHttp && articleUri.Scheme != Uri.UriSchemeHttps) ||
            !TryReadDate(date, out var publishedAt))
        {
            return null;
        }

        var source = ReadString(value, "domain");
        if (string.IsNullOrWhiteSpace(source))
            source = articleUri.Host;

        var impact = GetArticleImpact(title);
        var sentiment = AnalyzeGoldSentiment(title);
        return new NewsArticle(
            title.Trim(), articleUri.ToString(), source.Trim(), publishedAt, impact.Label, impact.Tone,
            sentiment.Score, sentiment.Label, sentiment.Confidence, sentiment.Themes);
    }

    private static (double Score, string Label, double Confidence, IReadOnlyList<string> Themes) AnalyzeGoldSentiment(string title)
    {
        var normalized = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        var candidates = GoldSentimentTerms
            .Select(term => (Term: term, Index: normalized.IndexOf(term.Phrase, StringComparison.Ordinal)))
            .Where(match => match.Index >= 0 && !IsNegated(normalized, match.Term.Phrase))
            .ToArray();
        var matches = candidates
            .Where(match => !candidates.Any(other =>
                other.Term.Phrase.Length > match.Term.Phrase.Length &&
                other.Index <= match.Index &&
                other.Index + other.Term.Phrase.Length >= match.Index + match.Term.Phrase.Length))
            .Select(match => match.Term)
            .ToArray();
        if (matches.Length == 0)
            return (0, "Неутрално", 0.15, []);

        var totalWeight = matches.Sum(term => Math.Abs(term.Weight));
        var score = Math.Clamp(matches.Sum(term => term.Weight) / totalWeight, -1, 1);
        var label = score >= 0.15 ? "Възходящо" : score <= -0.15 ? "Низходящо" : "Неутрално";
        var confidence = Math.Clamp(0.35 + Math.Min(matches.Length, 4) * 0.08 + Math.Abs(score) * 0.25, 0.35, 0.85);
        return (score, label, confidence, matches.Select(term => term.Theme).Distinct().ToArray());
    }

    private static bool IsNegated(string normalized, string phrase)
    {
        var index = normalized.IndexOf(phrase, StringComparison.Ordinal);
        if (index <= 0)
            return false;
        var prefix = normalized[..index];
        var context = prefix.Length > 40 ? prefix[^40..] : prefix;
        return SentimentNegation.IsMatch(context);
    }

    private static (string Label, double Score, double Confidence, int Bullish, int Bearish, int Neutral)
        CalculateSentiment(IReadOnlyList<NewsArticle> articles, DateTimeOffset now)
    {
        var bullish = articles.Count(article => article.SentimentLabel == "Възходящо");
        var bearish = articles.Count(article => article.SentimentLabel == "Низходящо");
        var neutral = articles.Count - bullish - bearish;
        if (articles.Count == 0)
            return ("Неизвестен", 0, 0, 0, 0, 0);

        var weighted = articles.Select(article =>
        {
            var ageHours = Math.Max(0, (now - article.PublishedAtUtc).TotalHours);
            var recencyWeight = Math.Pow(0.5, ageHours / 6d);
            var impactWeight = article.Impact switch { "Високо" => 1.4, "Средно" => 1.15, _ => 1.0 };
            var confidenceWeight = 0.35 + article.SentimentConfidence;
            return (Article: article, Weight: recencyWeight * impactWeight * confidenceWeight);
        }).ToArray();
        var weightSum = weighted.Sum(item => item.Weight);
        var score = weightSum <= 0 ? 0 : weighted.Sum(item => item.Article.SentimentScore * item.Weight) / weightSum;
        var coverage = Math.Min(1, articles.Count / 5d);
        var confidence = weighted.Sum(item => item.Article.SentimentConfidence * item.Weight) / weightSum * coverage;
        var label = score >= 0.12 ? "Възходящо" : score <= -0.12 ? "Низходящо" : "Неутрално";
        return (label, Math.Clamp(score, -1, 1), Math.Clamp(confidence, 0, 1), bullish, bearish, neutral);
    }

    private static string? ReadString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) && propertyValue.ValueKind == JsonValueKind.String
            ? propertyValue.GetString()
            : null;

    private static bool TryReadDate(string? value, out DateTimeOffset date)
    {
        if (DateTimeOffset.TryParseExact(
            value,
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date))
        {
            return true;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
    }

    private static (string Label, string Tone) GetArticleImpact(string title)
    {
        var normalized = title.ToLowerInvariant();
        if (HighImpactTerms.Any(normalized.Contains))
            return ("Високо", "negative");
        if (MediumImpactTerms.Any(normalized.Contains))
            return ("Средно", "neutral");
        return ("Ниско", "positive");
    }

    private static (string Level, int Score, string Summary) CalculateRisk(
        IReadOnlyList<NewsArticle> articles,
        DateTimeOffset now)
    {
        if (articles.Count == 0)
        {
            return ("Неизвестен", 0,
                "Няма релевантни заглавия за последните 24 часа; feed-ът не може да оцени новинарския риск.");
        }

        var recent = articles.Where(article => article.PublishedAtUtc >= now.AddHours(-6)).ToArray();
        var highImpactCount = recent.Count(article => article.Impact == "Високо");
        var score = Math.Clamp(recent.Length * 7 + highImpactCount * 35, 0, 100);
        var level = score >= 75 ? "Висок" : score >= 35 ? "Умерен" : "Нисък";
        var summary = recent.Length == 0
            ? "Няма актуални заглавия от последните 6 часа; това не изключва непубликувани или календарни събития."
            : $"Евристична оценка от {recent.Length} заглавия за 6 часа, от които {highImpactCount} съдържат високорискови теми.";
        return (level, score, summary);
    }

    private static int ReadBoundedInt(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    private DateTimeOffset GetRateLimitRetryAtUtc(DateTimeOffset? providerRetryAtUtc, DateTimeOffset nowUtc)
    {
        if (providerRetryAtUtc is { } providerDate && providerDate > nowUtc)
            return providerDate;

        var backoffMinutes = Math.Min(360, 15 * Math.Pow(2, Math.Clamp(_consecutiveRateLimitCount - 1, 0, 5)));
        return nowUtc.AddMinutes(backoffMinutes);
    }

    private void PersistRateLimitState()
    {
        try
        {
            var directory = Path.GetDirectoryName(_rateLimitStatePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var state = new PersistedRateLimitState(_retryNotBeforeUtc, _consecutiveRateLimitCount);
            File.WriteAllText(_rateLimitStatePath, JsonSerializer.Serialize(state, CacheJsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not persist GDELT rate-limit cooldown.");
        }
    }

    private void ClearRateLimitState()
    {
        try
        {
            if (File.Exists(_rateLimitStatePath))
                File.Delete(_rateLimitStatePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not clear GDELT rate-limit cooldown after a successful refresh.");
        }
    }

    private static PersistedRateLimitState LoadRateLimitState(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new PersistedRateLimitState(DateTimeOffset.MinValue, 0);
            return JsonSerializer.Deserialize<PersistedRateLimitState>(File.ReadAllText(path), CacheJsonOptions)
                ?? new PersistedRateLimitState(DateTimeOffset.MinValue, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PersistedRateLimitState(DateTimeOffset.MinValue, 0);
        }
    }

    private static string DescribeFailure(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } statusCode } =>
            $"GDELT върна HTTP {(int)statusCode} ({statusCode}). Проверете достъпа и лимита на доставчика.",
        HttpRequestException requestError =>
            $"Неуспешна DNS/мрежова връзка с api.gdeltproject.org: {requestError.Message}",
        TaskCanceledException =>
            "GDELT не отговори в рамките на зададения мрежов timeout.",
        JsonException =>
            "GDELT отговори, но върна невалиден JSON формат.",
        InvalidDataException =>
            "GDELT отговори с неочаквана структура на новинарския feed.",
        _ => "GDELT новинарският feed не е достъпен."
    };

    private static NewsFeedSnapshot? LoadPersistedCache(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var snapshot = JsonSerializer.Deserialize<NewsFeedSnapshot>(File.ReadAllText(path), CacheJsonOptions);
            if (snapshot is null)
                return null;

            var articles = snapshot.Articles.Select(article =>
            {
                var sentiment = AnalyzeGoldSentiment(article.Title);
                return article with
                {
                    SentimentScore = sentiment.Score,
                    SentimentLabel = sentiment.Label,
                    SentimentConfidence = sentiment.Confidence,
                    SentimentThemes = sentiment.Themes
                };
            }).ToArray();
            var sentimentSummary = CalculateSentiment(articles, DateTimeOffset.UtcNow);
            return snapshot with
            {
                Articles = articles,
                SentimentLabel = sentimentSummary.Label,
                SentimentScore = sentimentSummary.Score,
                SentimentConfidence = sentimentSummary.Confidence,
                BullishCount = sentimentSummary.Bullish,
                BearishCount = sentimentSummary.Bearish,
                NeutralCount = sentimentSummary.Neutral
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void PersistCache(NewsFeedSnapshot snapshot)
    {
        if (!snapshot.Available || snapshot.IsStale)
            return;

        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot, CacheJsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The in-memory cache still serves the current app process.
        }
    }

    private sealed record PersistedRateLimitState(
        DateTimeOffset RetryNotBeforeUtc,
        int ConsecutiveRateLimitCount);

    private sealed class GdeltRateLimitException(DateTimeOffset? retryAfterUtc)
        : HttpRequestException("GDELT rate limited the request.", null, System.Net.HttpStatusCode.TooManyRequests)
    {
        public DateTimeOffset? RetryAfterUtc { get; } = retryAfterUtc;
    }
}
