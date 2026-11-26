using XYN_Trader.Components;
using XYN_Trader.Models;
using XYN_Trader.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<DemoMarketDataService>();
builder.Services.AddSingleton<TechnicalAnalysisService>();
builder.Services.AddSingleton<TechnicalSignalService>();
builder.Services.AddScoped<PositionReviewService>();
builder.Services.AddSingleton<PositionProfitTargetStore>();
builder.Services.AddSingleton<PricePredictionService>();
builder.Services.AddHttpClient("BlsCalendar", (serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var blsContact = configuration["EconomicCalendar:BlsContact"]?.Trim();
    var contactComment = string.IsNullOrWhiteSpace(blsContact) ? string.Empty : $" (+{blsContact})";
    client.BaseAddress = new Uri(EconomicCalendarService.BlsCalendarUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"XYN-Trader/0.1{contactComment}");
});
builder.Services.AddHttpClient("BeaCalendar", client =>
{
    client.BaseAddress = new Uri("https://www.bea.gov/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("XYN-Trader/0.1");
});
builder.Services.AddHttpClient("FedCalendar", client =>
{
    client.BaseAddress = new Uri(EconomicCalendarService.FomcCalendarUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("XYN-Trader/0.1");
});
builder.Services.AddHttpClient("CensusCalendar", client =>
{
    client.BaseAddress = new Uri(EconomicCalendarService.CensusCalendarUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("XYN-Trader/0.1");
});
builder.Services.AddSingleton<EconomicCalendarService>();
builder.Services.AddHttpClient("GdeltNews", (serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var baseAddress = configuration["NewsFeed:BaseAddress"] ?? NewsFeedService.DefaultBaseAddress;
    client.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(12);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("XYN-Trader/0.1");
});
builder.Services.AddSingleton<NewsFeedService>();
builder.Services.AddHttpClient<CandidateGeneratorClient>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var baseAddress = configuration["AIService:BaseAddress"] ?? CandidateGeneratorClient.DefaultBaseAddress;
    client.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddScoped<LiveSignalService>();
builder.Services.AddHttpClient<Mt5MarketDataProvider>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var baseAddress = configuration["MT5Bridge:BaseAddress"] ?? Mt5MarketDataProvider.DefaultBaseAddress;
    client.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Uvicorn keeps idle HTTP connections for 60 seconds. Expire pooled client connections first.
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15)
});
builder.Services.AddHttpClient("TwelveData", client =>
{
    client.BaseAddress = new Uri(TwelveDataMarketDataProvider.ApiBaseAddress, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped(serviceProvider =>
{
    var client = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("TwelveData");
    var apiKey = serviceProvider.GetRequiredService<IConfiguration>()["TwelveData:ApiKey"];
    return new TwelveDataMarketDataProvider(client, apiKey);
});
builder.Services.AddScoped<SignalMarketDataService>();
builder.Services.AddSingleton<TechnicalTradeSignalService>();
builder.Services.AddHttpClient("MT5LearningHistory", (serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var baseAddress = configuration["MT5Bridge:BaseAddress"] ?? Mt5MarketDataProvider.DefaultBaseAddress;
    client.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15)
});
builder.Services.AddHostedService<SignalLearningCollectorService>();
builder.Services.AddHostedService<PositionProfitTargetMonitorService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/health", () => Results.Ok(new { status = "ok", environment = app.Environment.EnvironmentName }));
app.MapGet("/api/market/snapshot", async (
    HttpContext context,
    Mt5MarketDataProvider provider,
    string? interval,
    CancellationToken cancellationToken) =>
{
    var intervalCode = interval?.ToLowerInvariant() switch
    {
        "m1" or "1min" => "M1",
        "m5" or "5min" => "M5",
        "m15" or "15min" => "M15",
        "m30" or "30min" => "M30",
        "h1" or "1h" or null or "" => "H1",
        "h4" or "4h" => "H4",
        "d1" or "1day" => "D1",
        _ => interval?.ToUpperInvariant() ?? "H1"
    };
    var result = await provider.GetSnapshotAsync(intervalCode, cancellationToken);
    var envelope = ApiEnvelope<MarketDataResult>.Create(result, context.TraceIdentifier);
    return result.ConnectionStatus == MarketConnectionStatus.Connected
        ? Results.Ok(envelope)
        : Results.Json(envelope, statusCode: result.ConnectionStatus == MarketConnectionStatus.Disconnected
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status502BadGateway);
});
app.MapGet("/api/analysis", async (
    HttpContext context,
    Mt5MarketDataProvider marketProvider,
    TechnicalAnalysisService analysisService,
    string? timeframe,
    CancellationToken cancellationToken) =>
{
    var timeframeCode = timeframe?.ToLowerInvariant() switch
    {
        "m1" or "1min" => "M1",
        "m5" or "5min" => "M5",
        "m15" or "15min" => "M15",
        "m30" or "30min" => "M30",
        "h4" or "4h" => "H4",
        "d1" or "1day" => "D1",
        "h1" or "1h" or null or "" => "H1",
        _ => timeframe!.ToUpperInvariant()
    };
    var market = await marketProvider.GetSnapshotAsync(timeframeCode, cancellationToken);
    if (market.Snapshot is null)
    {
        var statusCode = market.ConnectionStatus == MarketConnectionStatus.Disconnected
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status502BadGateway;
        return Results.Json(new { error = market.Message, source = market.Source, timeframe = timeframeCode }, statusCode: statusCode);
    }

    var analysis = analysisService.Analyze(market.Snapshot, timeframeCode);
    return Results.Ok(ApiEnvelope<AnalysisSnapshot>.Create(analysis, context.TraceIdentifier));
});
app.MapGet("/api/technical-signal", async (
    HttpContext context,
    Mt5MarketDataProvider marketProvider,
    TechnicalSignalService signalService,
    string? timeframe,
    CancellationToken cancellationToken) =>
{
    string? timeframeCode = timeframe?.Trim().ToLowerInvariant() switch
    {
        "5m" or "m5" => "M5",
        "15m" or "m15" => "M15",
        "1h" or "h1" => "H1",
        "1d" or "d1" => "D1",
        _ => null
    };
    if (timeframeCode is null)
    {
        return Results.BadRequest(new
        {
            error = "Невалидна времева рамка. Допустими стойности: 5m, 15m, 1h, 1d.",
            traceId = context.TraceIdentifier
        });
    }

    var market = await marketProvider.GetSnapshotAsync(timeframeCode, cancellationToken);
    if (market.Snapshot is null)
    {
        var statusCode = market.ConnectionStatus == MarketConnectionStatus.Disconnected
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status502BadGateway;
        return Results.Json(new
        {
            error = market.Message ?? "MT5 не върна данни за сигнала.",
            source = market.Source,
            timeframe = timeframeCode,
            traceId = context.TraceIdentifier
        }, statusCode: statusCode);
    }

    var signal = signalService.Analyze(market.Snapshot, timeframeCode) with
    {
        IsStale = market.Freshness != MarketFreshness.Fresh,
        DataWarning = market.Message
    };
    return Results.Ok(ApiEnvelope<TechnicalSignalSnapshot>.Create(signal, context.TraceIdentifier));
});
app.MapGet("/api/price-prediction", async (
    HttpContext context,
    Mt5MarketDataProvider marketProvider,
    PricePredictionService predictionService,
    CancellationToken cancellationToken) =>
{
    var market = await marketProvider.GetSnapshotAsync("M5", cancellationToken);
    if (market.Snapshot is null)
    {
        var statusCode = market.ConnectionStatus == MarketConnectionStatus.Disconnected
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status502BadGateway;
        return Results.Json(new { error = market.Message, source = market.Source, timeframe = "M5" }, statusCode: statusCode);
    }

    var prediction = predictionService.Analyze(market.Snapshot);
    return Results.Ok(ApiEnvelope<PricePredictionSnapshot>.Create(prediction, context.TraceIdentifier));
});
app.MapGet("/api/news", async (
    HttpContext context,
    NewsFeedService newsFeed,
    CancellationToken cancellationToken) =>
{
    var result = await newsFeed.GetLatestAsync(cancellationToken: cancellationToken);
    var envelope = ApiEnvelope<NewsFeedSnapshot>.Create(result, context.TraceIdentifier);
    return result.Available
        ? Results.Ok(envelope)
        : Results.Json(envelope, statusCode: StatusCodes.Status502BadGateway);
});
app.MapGet("/api/economic-calendar", async (
    HttpContext context,
    EconomicCalendarService calendar,
    int? days,
    bool? refresh,
    CancellationToken cancellationToken) =>
{
    var result = await calendar.GetLatestAsync(days ?? 7, refresh == true, cancellationToken);
    var envelope = ApiEnvelope<EconomicCalendarSnapshot>.Create(result, context.TraceIdentifier);
    return result.Available
        ? Results.Ok(envelope)
        : Results.Json(envelope, statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/learning/status", async (
    HttpContext context,
    CandidateGeneratorClient learningClient,
    CancellationToken cancellationToken) =>
{
    var result = await learningClient.GetLearningStatusAsync(cancellationToken);
    if (!result.Available || result.Status is null)
        return Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);

    return Results.Ok(ApiEnvelope<LearningStatusSnapshot>.Create(result.Status, context.TraceIdentifier));
});
app.MapGet("/api/user/profile", async (
    HttpContext context,
    CandidateGeneratorClient userDataClient,
    CancellationToken cancellationToken) =>
{
    var result = await userDataClient.GetUserProfileAsync(cancellationToken);
    if (!result.Available || result.Profile is null)
        return Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);

    return Results.Ok(ApiEnvelope<UserProfileSnapshot>.Create(result.Profile, context.TraceIdentifier));
});
app.MapPut("/api/user/signals/{signalId}", async (
    HttpContext context,
    CandidateGeneratorClient userDataClient,
    string signalId,
    UserSignalJournalSaveRequest request,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(signalId) || signalId.Length > 128 ||
        signalId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
    {
        return Results.BadRequest(new { error = "Невалиден идентификатор на сценарий.", traceId = context.TraceIdentifier });
    }
    if (request.Note is null || request.Note.Length > 4000)
    {
        return Results.BadRequest(new { error = "Бележката трябва да е до 4 000 знака.", traceId = context.TraceIdentifier });
    }

    var result = await userDataClient.SaveUserSignalEntryAsync(
        signalId,
        request.IsFavorite,
        request.Note,
        cancellationToken);
    var envelope = ApiEnvelope<UserSignalJournalEntry?>.Create(result.Entry, context.TraceIdentifier);
    return result.Available
        ? Results.Ok(envelope)
        : Results.Json(envelope, statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/backtesting", async (
    HttpContext context,
    CandidateGeneratorClient learningClient,
    string? horizon,
    string? start,
    string? end,
    CancellationToken cancellationToken) =>
{
    var selectedHorizon = horizon?.ToLowerInvariant() switch
    {
        "1h" or "24h" or "48h" => horizon!.ToLowerInvariant(),
        null or "" => "48h",
        _ => horizon!.ToLowerInvariant()
    };
    var result = await learningClient.GetBacktestAsync(selectedHorizon, start, end, cancellationToken);
    var envelope = ApiEnvelope<BacktestResultResponse>.Create(result, context.TraceIdentifier);
    return result.Available
        ? Results.Ok(envelope)
        : Results.Json(envelope, statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/signals", async (
    HttpContext context,
    LiveSignalService data,
    string? status,
    string? direction,
    string? timeframe,
    string? horizon,
    string? date,
    string? search,
    CancellationToken cancellationToken) =>
{
    var result = await data.GetCandidatesAsync(cancellationToken);
    if (result.Available)
    {
        int? requestedHorizon = int.TryParse(horizon?.TrimEnd('H', 'h'), out var hours) ? hours : null;
        var today = DateTime.UtcNow.Date;
        var candidates = result.Candidates
            .Where(candidate => string.IsNullOrWhiteSpace(status) ||
                string.Equals(candidate.Status, status, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => string.IsNullOrWhiteSpace(direction) ||
                string.Equals(candidate.Direction, direction, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => string.IsNullOrWhiteSpace(timeframe) ||
                string.Equals(candidate.Timeframe, timeframe, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => requestedHorizon is null || candidate.HorizonHours == requestedHorizon)
            .Where(candidate => date?.ToLowerInvariant() switch
            {
                "today" => candidate.TimestampUtc.UtcDateTime.Date == today,
                "yesterday" => candidate.TimestampUtc.UtcDateTime.Date == today.AddDays(-1),
                "older" => candidate.TimestampUtc.UtcDateTime.Date < today.AddDays(-1),
                _ => true
            })
            .Where(candidate => string.IsNullOrWhiteSpace(search) ||
                candidate.SignalId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                candidate.Direction.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                candidate.PolicyVersion.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        result = result with { Candidates = candidates };
    }

    var envelope = ApiEnvelope<CandidateGenerationResult>.Create(result, context.TraceIdentifier);
    return result.Available
        ? Results.Ok(envelope)
        : Results.Json(envelope, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
