using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using XYN_Trader.Models;

namespace XYN_Trader.Services;

public sealed class EconomicCalendarService
{
    public const string BlsCalendarUrl = "https://www.bls.gov/schedule/news_release/bls.ics";
    public const string BlsScheduleUrl = "https://www.bls.gov/schedule/";
    public const string BeaScheduleUrl = "https://www.bea.gov/news/schedule/full";
    public const string BeaNextYearScheduleUrl = "https://www.bea.gov/news/schedule/next-year";
    public const string CensusCalendarUrl = "https://www.census.gov/economic-indicators/calendar-listview.html";
    public const string FomcCalendarUrl = "https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm";
    public const string SourceName = "BLS · BEA · Federal Reserve";
    private const string EasternTimeZoneId = "Eastern Standard Time";
    private const int CalendarHorizonDays = 366;
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex IcsLineBreaks = new("\\r?\\n[ \\t]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IcsStart = new("^DTSTART(?<parameters>(?:;[^:]*)?):(?<value>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IcsTimeZone = new("(?:^|;)TZID=\\\"?(?<zone>[^;\\\"]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTags = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex TableRows = new("<tr\\b[^>]*>(?<row>.*?)</tr>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TableCells = new("<t[dh]\\b[^>]*>(?<cell>.*?)</t[dh]>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Anchor = new("<a\\b[^>]*href=[\\\"'](?<href>[^\\\"']+)[\\\"'][^>]*>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex YearHeader = new("\\bYear\\s+(?<year>20\\d{2})\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BeaDate = new("\\b(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\\s+(?<day>\\d{1,2})(?:,?\\s+(?<year>20\\d{2}))?\\s+(?<time>\\d{1,2}:\\d{2}\\s*[AP]M)\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BlsDateTime = new("(?:(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),?\\s+)?(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\\s+(?<day>\\d{1,2}),?\\s+(?<year>20\\d{2})\\s+(?<time>\\d{1,2}:\\d{2}\\s*[AP]M)\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BlsScheduleDate = new("(?:(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),?\\s+)?(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\\s+(?<day>\\d{1,2}),?\\s+(?<year>20\\d{2})\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BlsScheduleTime = new("\\d{1,2}:\\d{2}\\s*[AP]M\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FedYearHeading = new("(?im)^\\s*(?<year>20\\d{2})\\s+FOMC\\s+Meetings\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MonthLine = new("(?im)^\\s*(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MeetingDayLine = new("(?m)^\\s*(?<first>\\d{1,2})(?:\\s*-\\s*(?<last>\\d{1,2}))?\\s*\\*?\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EconomicCalendarService> _logger;
    private readonly TimeZoneInfo _easternTimeZone;
    private readonly string _cachePath;
    private readonly int _refreshMinutes;
    private readonly bool _enabled;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private EconomicCalendarSnapshot? _cachedSnapshot;
    private DateTimeOffset _cacheExpiresAtUtc;

    public EconomicCalendarService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<EconomicCalendarService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _easternTimeZone = FindEasternTimeZone();
        _enabled = !bool.TryParse(configuration["EconomicCalendar:Enabled"], out var enabled) || enabled;
        _refreshMinutes = ReadBoundedInt(configuration["EconomicCalendar:RefreshMinutes"], 30, 5, 240);
        _cachePath = configuration["EconomicCalendar:CachePath"] ??
            Path.Combine(environment.ContentRootPath, "data", "economic-calendar-cache.json");
        _cachedSnapshot = LoadCache(_cachePath);
        _cacheExpiresAtUtc = _cachedSnapshot?.UpdatedAtUtc?.Add(RefreshInterval) ?? DateTimeOffset.MinValue;
    }

    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(_refreshMinutes);

    public EconomicCalendarSnapshot GetCachedSnapshot(int days = 7) =>
        FilterEvents(_cachedSnapshot ?? EconomicCalendarSnapshot.Unavailable(
            _enabled ? "Изчакване на официалните графици…" : "Икономическият календар е изключен."), days);

    public async Task<EconomicCalendarSnapshot> GetLatestAsync(
        int days = 7,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled)
            return EconomicCalendarSnapshot.Unavailable("Икономическият календар е изключен в EconomicCalendar:Enabled.");

        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh && _cachedSnapshot is not null && now < _cacheExpiresAtUtc)
            return FilterEvents(_cachedSnapshot, days);

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!forceRefresh && _cachedSnapshot is not null && now < _cacheExpiresAtUtc)
                return FilterEvents(_cachedSnapshot, days);

            var nextYear = now.Year + 1;
            var results = await Task.WhenAll(
                FetchSourceAsync("BLS", FetchBlsEventsAsync),
                FetchSourceAsync("BEA", () => FetchBeaEventsAsync(BeaScheduleUrl, now.Year, "BEA")),
                FetchSourceAsync($"BEA {nextYear}", () => FetchBeaEventsAsync(BeaNextYearScheduleUrl, nextYear, $"BEA {nextYear}")),
                FetchSourceAsync("US Census Bureau", FetchCensusEventsAsync),
                FetchSourceAsync("Federal Reserve", FetchFedEventsAsync));

            var previous = _cachedSnapshot;
            var successfulSources = results.Where(result => result.Status.Available).Select(result => result.Status.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var events = results.SelectMany(result => result.Events).ToList();
            var statuses = results.Select(result => result.Status).ToList();

            if (previous is not null)
            {
                foreach (var failedSource in results.Where(result => !result.Status.Available))
                {
                    var previousEvents = previous.Events.Where(item =>
                        string.Equals(item.Source, failedSource.Status.Name, StringComparison.OrdinalIgnoreCase));
                    events.AddRange(previousEvents);
                }
            }

            var easternToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _easternTimeZone).DateTime);
            events = events
                .Where(item => item.EventDate >= easternToday && item.EventDate <= easternToday.AddDays(CalendarHorizonDays))
                .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.EventDate)
                .ThenBy(item => item.ScheduledAtUtc)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var anyAvailable = statuses.Any(status => status.Available) || previous?.Available == true;
            var isStale = statuses.Any(status => !status.Available);
            var failedNames = statuses.Where(status => !status.Available).Select(status => status.Name).ToArray();
            var message = failedNames.Length == 0
                ? null
                : $"Няма актуален отговор от: {string.Join(", ", failedNames)}. Показани са запазените официални графици, когато са налични.";
            var snapshot = new EconomicCalendarSnapshot(
                anyAvailable,
                now,
                isStale,
                statuses,
                events,
                message);

            _cachedSnapshot = snapshot;
            _cacheExpiresAtUtc = now.Add(RefreshInterval);
            if (successfulSources.Count > 0)
                PersistCache(snapshot);
            return FilterEvents(snapshot, days);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<SourceFetchResult> FetchSourceAsync(
        string sourceName,
        Func<Task<IReadOnlyList<EconomicCalendarEvent>>> fetch)
    {
        try
        {
            var events = await fetch();
            return new SourceFetchResult(
                new EconomicCalendarSourceStatus(sourceName, true, DateTimeOffset.UtcNow),
                events);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            _logger.LogWarning(exception, "Economic calendar source {Source} refresh failed.", sourceName);
            var oldStatus = _cachedSnapshot?.Sources.FirstOrDefault(status =>
                string.Equals(status.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            var message = exception switch
            {
                HttpRequestException { StatusCode: { } statusCode } => $"HTTP {(int)statusCode} ({statusCode})",
                TaskCanceledException => "Timeout при връзката с официалния график",
                InvalidDataException => "Източникът върна неочакван формат",
                _ => "Неуспешно четене на отговора"
            };
            return new SourceFetchResult(
                new EconomicCalendarSourceStatus(sourceName, false, oldStatus?.UpdatedAtUtc, message),
                []);
        }
    }

    private async Task<IReadOnlyList<EconomicCalendarEvent>> FetchBlsEventsAsync()
    {
        var client = _httpClientFactory.CreateClient("BlsCalendar");
        var currentYear = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _easternTimeZone).Year;
        IReadOnlyList<EconomicCalendarEvent> events = [];
        try
        {
            var content = await client.GetStringAsync(string.Empty);
            events = ParseBlsCalendar(content);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogDebug(exception, "BLS iCalendar feed could not be read; trying the official release schedules.");
        }

        if (events.Count > 0)
            return events;

        var yearResults = await Task.WhenAll(
            FetchBlsYearAsync(client, currentYear),
            FetchBlsYearAsync(client, currentYear + 1));
        events = yearResults.SelectMany(result => result.Events).ToArray();
        if (events.Count == 0)
        {
            var fetchFailure = yearResults.Select(result => result.Error).FirstOrDefault(error => error is not null);
            if (fetchFailure is not null)
                throw fetchFailure;
            throw new InvalidDataException("BLS iCalendar and release schedules contain no recognized economic releases.");
        }
        return events;
    }

    private async Task<(IReadOnlyList<EconomicCalendarEvent> Events, Exception? Error)> FetchBlsYearAsync(HttpClient client, int year)
    {
        var url = $"{BlsScheduleUrl}{year}/home.htm";
        try
        {
            var html = await client.GetStringAsync(url);
            return (ParseBlsSchedule(html, year, url), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            _logger.LogDebug(exception, "BLS release schedule for {Year} could not be read.", year);
            return ([], exception);
        }
    }

    private async Task<IReadOnlyList<EconomicCalendarEvent>> FetchBeaEventsAsync(string path, int fallbackYear, string sourceName)
    {
        var client = _httpClientFactory.CreateClient("BeaCalendar");
        var html = await client.GetStringAsync(path);
        var events = ParseBeaSchedule(html, fallbackYear, path, sourceName);
        if (events.Count == 0)
            throw new InvalidDataException("BEA schedule contains no recognized macroeconomic releases.");
        return events;
    }

    private async Task<IReadOnlyList<EconomicCalendarEvent>> FetchFedEventsAsync()
    {
        var client = _httpClientFactory.CreateClient("FedCalendar");
        var html = await client.GetStringAsync(string.Empty);
        var events = ParseFedSchedule(html);
        if (events.Count == 0)
            throw new InvalidDataException("Federal Reserve page contains no recognized FOMC dates.");
        return events;
    }

    private async Task<IReadOnlyList<EconomicCalendarEvent>> FetchCensusEventsAsync()
    {
        var client = _httpClientFactory.CreateClient("CensusCalendar");
        var currentYear = DateTimeOffset.UtcNow.Year;
        var yearResults = await Task.WhenAll(
            FetchCensusYearAsync(client, currentYear),
            FetchCensusYearAsync(client, currentYear + 1));
        var events = yearResults.SelectMany(result => result).ToList();
        if (events.Count == 0)
            throw new InvalidDataException("Census calendar contains no recognized macroeconomic releases.");
        return events;
    }

    private async Task<IReadOnlyList<EconomicCalendarEvent>> FetchCensusYearAsync(HttpClient client, int year)
    {
        // Census archives years with the year after "calendar-listview". The
        // unqualified URL serves the latest calendar (often next year's) before
        // that year's archive URL is available.
        foreach (var path in new[] { $"calendar-listview-{year}.html", string.Empty })
        {
            try
            {
                var html = await client.GetStringAsync(path);
                var events = ParseCensusSchedule(html, year)
                    .Where(calendarEvent => calendarEvent.EventDate.Year == year)
                    .ToArray();
                if (events.Length > 0)
                    return events;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
            {
                _logger.LogDebug(exception, "Census calendar schedule at {Path} could not be read for {Year}.", path, year);
            }
        }

        return [];
    }

    private IReadOnlyList<EconomicCalendarEvent> ParseBlsCalendar(string content)
    {
        var lines = IcsLineBreaks.Replace(content, string.Empty)
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);
        var events = new List<EconomicCalendarEvent>();
        Dictionary<string, string>? current = null;

        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null && TryCreateBlsEvent(current, out var calendarEvent))
                    events.Add(calendarEvent);
                current = null;
                continue;
            }
            if (current is null)
                continue;

            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            var property = line[..separator];
            var name = property.Split(';', 2)[0];
            current[name] = line[(separator + 1)..];
            if (name.Equals("DTSTART", StringComparison.OrdinalIgnoreCase))
                current["DTSTART_PROPERTY"] = property;
        }

        return events;
    }

    private IReadOnlyList<EconomicCalendarEvent> ParseBlsSchedule(string html, int fallbackYear, string sourceUrl)
    {
        var events = new List<EconomicCalendarEvent>();
        foreach (Match rowMatch in TableRows.Matches(html))
        {
            var rowHtml = rowMatch.Groups["row"].Value;
            var cells = TableCells.Matches(rowHtml)
                .Select(cell => PlainHtmlText(cell.Groups["cell"].Value))
                .Where(cell => cell.Length > 0)
                .ToArray();
            if (cells.Length < 3)
                continue;

            var title = cells[^1];
            if (!IsRelevantRelease(title))
                continue;

            var dateMatch = BlsDateTime.Match(string.Join(" ", cells));
            if (!dateMatch.Success || !int.TryParse(dateMatch.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
                continue;
            var month = dateMatch.Groups["month"].Value;
            var day = dateMatch.Groups["day"].Value;
            var time = Regex.Replace(dateMatch.Groups["time"].Value, "\\s+", string.Empty, RegexOptions.CultureInvariant);
            if (!DateTime.TryParseExact(
                $"{month} {day} {year} {time}",
                ["MMMM d yyyy h:mmtt", "MMMM d yyyy hh:mmtt", "MMMM dd yyyy h:mmtt", "MMMM dd yyyy hh:mmtt"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTime))
            {
                continue;
            }

            var anchorMatch = Anchor.Match(rowHtml);
            var eventUrl = anchorMatch.Success && Uri.TryCreate(new Uri(sourceUrl), WebUtility.HtmlDecode(anchorMatch.Groups["href"].Value), out var articleUri)
                ? articleUri.ToString()
                : sourceUrl;
            var eventDate = DateOnly.FromDateTime(localTime);
            events.Add(new EconomicCalendarEvent(
                $"bls-{eventDate:yyyyMMdd}-{Slug(title)}",
                title,
                eventDate,
                new DateTimeOffset(localTime, _easternTimeZone.GetUtcOffset(localTime)).ToUniversalTime(),
                EasternTimeZoneId,
                false,
                "US",
                GetCategory(title),
                GetImpact(title),
                "BLS",
                eventUrl));
        }

        if (events.Count > 0)
            return events.Where(calendarEvent => calendarEvent.EventDate.Year == fallbackYear).ToArray();

        return ParseBlsScheduleText(html, fallbackYear, sourceUrl);
    }

    private IReadOnlyList<EconomicCalendarEvent> ParseBlsScheduleText(string html, int year, string sourceUrl)
    {
        var lines = HtmlToLines(html)
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line, "\\s+", " ", RegexOptions.CultureInvariant).Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var events = new List<EconomicCalendarEvent>();

        for (var index = 0; index < lines.Length; index++)
        {
            var dateMatch = BlsScheduleDate.Match(lines[index]);
            if (!dateMatch.Success || !int.TryParse(dateMatch.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var eventYear) || eventYear != year)
                continue;

            var eventText = lines[index][(dateMatch.Index + dateMatch.Length)..];
            if (!TryGetBlsTimeAndTitle(eventText, out var time, out var title))
            {
                var searchEnd = Math.Min(lines.Length, index + 5);
                for (var following = index + 1; following < searchEnd; following++)
                {
                    if (BlsScheduleDate.IsMatch(lines[following]))
                        break;
                    if (!TryGetBlsTimeAndTitle(lines[following], out time, out title))
                        continue;

                    if (string.IsNullOrWhiteSpace(title))
                    {
                        for (var titleLine = following + 1; titleLine < searchEnd; titleLine++)
                        {
                            if (BlsScheduleDate.IsMatch(lines[titleLine]))
                                break;
                            if (BlsScheduleTime.IsMatch(lines[titleLine]))
                                continue;
                            title = lines[titleLine];
                            break;
                        }
                    }
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(time) || string.IsNullOrWhiteSpace(title) || !IsRelevantRelease(title))
                continue;

            var normalizedTime = Regex.Replace(time, "\\s+", string.Empty, RegexOptions.CultureInvariant);
            if (!DateTime.TryParseExact(
                $"{dateMatch.Groups["month"].Value} {dateMatch.Groups["day"].Value} {eventYear} {normalizedTime}",
                ["MMMM d yyyy h:mmtt", "MMMM d yyyy hh:mmtt", "MMMM dd yyyy h:mmtt", "MMMM dd yyyy hh:mmtt"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTime))
            {
                continue;
            }

            var eventDate = DateOnly.FromDateTime(localTime);
            events.Add(new EconomicCalendarEvent(
                $"bls-{eventDate:yyyyMMdd}-{Slug(title)}",
                title,
                eventDate,
                new DateTimeOffset(localTime, _easternTimeZone.GetUtcOffset(localTime)).ToUniversalTime(),
                EasternTimeZoneId,
                false,
                "US",
                GetCategory(title),
                GetImpact(title),
                "BLS",
                sourceUrl));
        }

        return events;
    }

    private static bool TryGetBlsTimeAndTitle(string value, out string time, out string title)
    {
        var match = BlsScheduleTime.Match(value);
        if (!match.Success)
        {
            time = string.Empty;
            title = string.Empty;
            return false;
        }

        time = match.Value;
        title = value[(match.Index + match.Length)..].Trim(' ', '\t', '-', '–', '—');
        return true;
    }

    private bool TryCreateBlsEvent(IReadOnlyDictionary<string, string> value, out EconomicCalendarEvent calendarEvent)
    {
        calendarEvent = default!;
        if (!value.TryGetValue("SUMMARY", out var rawTitle))
            return false;
        var title = UnescapeIcsText(rawTitle).Trim();
        if (!IsRelevantRelease(title))
            return false;

        if (!value.TryGetValue("DTSTART", out var rawDate) || !TryParseIcsStart(value.GetValueOrDefault("DTSTART_PROPERTY"), rawDate, out var localTime, out var allDay))
            return false;

        var timezone = _easternTimeZone;
        DateTimeOffset? scheduledAtUtc = allDay
            ? null
            : new DateTimeOffset(localTime, timezone.GetUtcOffset(localTime)).ToUniversalTime();
        var eventDate = DateOnly.FromDateTime(localTime);
        var url = value.TryGetValue("URL", out var rawUrl) && Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsedUrl)
            ? parsedUrl.ToString()
            : "https://www.bls.gov/schedule/";
        var id = value.GetValueOrDefault("UID") ?? $"bls-{eventDate:yyyyMMdd}-{Slug(title)}";
        var category = GetCategory(title);
        calendarEvent = new EconomicCalendarEvent(
            id,
            title,
            eventDate,
            scheduledAtUtc,
            EasternTimeZoneId,
            allDay,
            "US",
            category,
            GetImpact(title),
            "BLS",
            url);
        return true;
    }

    private IReadOnlyList<EconomicCalendarEvent> ParseBeaSchedule(string html, int fallbackYear, string path, string sourceName)
    {
        var year = YearHeader.Match(WebUtility.HtmlDecode(HtmlTags.Replace(html, " ")));
        var scheduleYear = year.Success && int.TryParse(year.Groups["year"].Value, out var parsedYear)
            ? parsedYear
            : fallbackYear;
        var sourceUrl = new Uri(new Uri("https://www.bea.gov/"), path).ToString();
        var events = new List<EconomicCalendarEvent>();

        foreach (Match rowMatch in TableRows.Matches(html))
        {
            var rowHtml = rowMatch.Groups["row"].Value;
            var cells = TableCells.Matches(rowHtml)
                .Select(cell => PlainHtmlText(cell.Groups["cell"].Value))
                .Where(cell => cell.Length > 0)
                .ToArray();
            if (cells.Length < 2)
                continue;

            var title = cells[^1];
            if (!IsRelevantRelease(title))
                continue;
            var dateMatch = BeaDate.Match(string.Join(" ", cells));
            if (!dateMatch.Success)
                continue;

            var releaseYear = int.TryParse(dateMatch.Groups["year"].Value, out var explicitYear)
                ? explicitYear
                : scheduleYear;
            if (!DateTime.TryParseExact(
                $"{dateMatch.Groups["month"].Value} {dateMatch.Groups["day"].Value} {releaseYear} {dateMatch.Groups["time"].Value}",
                ["MMMM d yyyy h:mm tt", "MMMM d yyyy h:mmtt"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTime))
            {
                continue;
            }

            var anchorMatch = Anchor.Match(rowHtml);
            var eventUrl = anchorMatch.Success && Uri.TryCreate(new Uri(sourceUrl), WebUtility.HtmlDecode(anchorMatch.Groups["href"].Value), out var articleUri)
                ? articleUri.ToString()
                : sourceUrl;
            var eventDate = DateOnly.FromDateTime(localTime);
            events.Add(new EconomicCalendarEvent(
                $"bea-{eventDate:yyyyMMdd}-{Slug(title)}",
                title,
                eventDate,
                new DateTimeOffset(localTime, _easternTimeZone.GetUtcOffset(localTime)).ToUniversalTime(),
                EasternTimeZoneId,
                false,
                "US",
                GetCategory(title),
                GetImpact(title),
                sourceName,
                eventUrl));
        }

        return events;
    }

    private IReadOnlyList<EconomicCalendarEvent> ParseFedSchedule(string html)
    {
        var text = HtmlToLines(html);
        var yearMatches = FedYearHeading.Matches(text);
        var events = new List<EconomicCalendarEvent>();
        var nowEastern = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _easternTimeZone);
        var tentativeSchedule = text.Contains("tentative until confirmed", StringComparison.OrdinalIgnoreCase);

        for (var index = 0; index < yearMatches.Count; index++)
        {
            var heading = yearMatches[index];
            if (!int.TryParse(heading.Groups["year"].Value, out var year))
                continue;
            var start = heading.Index + heading.Length;
            var end = index + 1 < yearMatches.Count ? yearMatches[index + 1].Index : text.Length;
            var section = text[start..end];
            var months = MonthLine.Matches(section);
            for (var monthIndex = 0; monthIndex < months.Count; monthIndex++)
            {
                var monthMatch = months[monthIndex];
                if (!DateTime.TryParseExact(monthMatch.Groups["month"].Value, "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
                    continue;
                var monthStart = monthMatch.Index + monthMatch.Length;
                var monthEnd = monthIndex + 1 < months.Count ? months[monthIndex + 1].Index : section.Length;
                var dayMatch = MeetingDayLine.Match(section[monthStart..monthEnd]);
                if (!dayMatch.Success || !int.TryParse(dayMatch.Groups["first"].Value, out var firstDay))
                    continue;
                var lastDay = int.TryParse(dayMatch.Groups["last"].Value, out var parsedLastDay) ? parsedLastDay : firstDay;
                DateOnly eventDate;
                try
                {
                    eventDate = new DateOnly(year, month.Month, Math.Max(firstDay, lastDay));
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                if (eventDate < DateOnly.FromDateTime(nowEastern.DateTime))
                    continue;
                var meetingRange = firstDay == lastDay ? firstDay.ToString(CultureInfo.InvariantCulture) : $"{firstDay}-{lastDay}";
                events.Add(new EconomicCalendarEvent(
                    $"fomc-{year}{month.Month:00}{lastDay:00}",
                    $"FOMC meeting ({meetingRange})",
                    eventDate,
                    null,
                    EasternTimeZoneId,
                    true,
                    "US",
                    "Монетарна политика",
                    "Високо",
                    "Federal Reserve",
                    FomcCalendarUrl,
                    tentativeSchedule && year > nowEastern.Year));
            }
        }

        return events;
    }

    private IReadOnlyList<EconomicCalendarEvent> ParseCensusSchedule(string html, int fallbackYear)
    {
        var events = new List<EconomicCalendarEvent>();
        var sourceUrl = CensusCalendarUrl;
        foreach (Match rowMatch in TableRows.Matches(html))
        {
            var rowHtml = rowMatch.Groups["row"].Value;
            var cells = TableCells.Matches(rowHtml)
                .Select(cell => PlainHtmlText(cell.Groups["cell"].Value))
                .Where(cell => cell.Length > 0)
                .ToArray();
            if (cells.Length < 3)
                continue;

            var title = cells[0];
            if (!IsRelevantRelease(title))
                continue;

            var releaseDate = cells
                .Select(cell => TryParseCensusDate(cell, fallbackYear))
                .FirstOrDefault(date => date is not null);
            var releaseTime = cells
                .Select(cell => DateTime.TryParseExact(cell.Replace(" ", string.Empty, StringComparison.Ordinal), ["h:mmtt", "hh:mmtt"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? (DateTime?)parsed : null)
                .FirstOrDefault(time => time is not null);
            if (releaseDate is null || releaseTime is null)
                continue;

            var localTime = releaseDate.Value.Date.Add(releaseTime.Value.TimeOfDay);
            var eventDate = DateOnly.FromDateTime(localTime);
            var anchorMatch = Anchor.Match(rowHtml);
            var eventUrl = anchorMatch.Success && Uri.TryCreate(new Uri(sourceUrl), WebUtility.HtmlDecode(anchorMatch.Groups["href"].Value), out var articleUri)
                ? articleUri.ToString()
                : sourceUrl;
            events.Add(new EconomicCalendarEvent(
                $"census-{eventDate:yyyyMMdd}-{Slug(title)}",
                title,
                eventDate,
                new DateTimeOffset(localTime, _easternTimeZone.GetUtcOffset(localTime)).ToUniversalTime(),
                EasternTimeZoneId,
                false,
                "US",
                GetCategory(title),
                GetImpact(title),
                "US Census Bureau",
                eventUrl));
        }
        return events;
    }

    private static DateTime? TryParseCensusDate(string value, int fallbackYear)
    {
        if (DateTime.TryParseExact(value, ["MMMM d, yyyy", "MMM d, yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;
        return DateTime.TryParseExact(
            $"{value} {fallbackYear}",
            ["MMMM d yyyy", "MMM d yyyy"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out parsed)
            ? parsed
            : null;
    }

    private bool TryParseIcsStart(string? property, string value, out DateTime localTime, out bool allDay)
    {
        localTime = default;
        allDay = false;
        var parameters = property is null ? string.Empty : IcsStart.Match($"{property}:{value}").Groups["parameters"].Value;
        var zoneMatch = IcsTimeZone.Match(parameters);
        var zone = zoneMatch.Success ? FindTimeZone(zoneMatch.Groups["zone"].Value) : _easternTimeZone;
        var dateValue = value.Trim();
        if (dateValue.Length == 8 && DateTime.TryParseExact(dateValue, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime))
        {
            allDay = true;
            return true;
        }

        if (dateValue.EndsWith('Z') && DateTime.TryParseExact(dateValue, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
        {
            localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            return true;
        }

        return DateTime.TryParseExact(dateValue, ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime);
    }

    private static string PlainHtmlText(string html)
    {
        var withSpacing = Regex.Replace(html, "<br\\b[^>]*>|</(?:p|div|li|span|a)>|</h[1-6]>", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(WebUtility.HtmlDecode(HtmlTags.Replace(withSpacing, " ")), "\\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static string HtmlToLines(string html)
    {
        var withoutScripts = Regex.Replace(html, "<(script|style)\\b[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var spaced = Regex.Replace(withoutScripts, "<br\\b[^>]*>|</(?:p|div|li|tr|td|th)>|</h[1-6]>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return WebUtility.HtmlDecode(HtmlTags.Replace(spaced, " "));
    }

    private EconomicCalendarSnapshot FilterEvents(EconomicCalendarSnapshot snapshot, int days)
    {
        var boundedDays = Math.Clamp(days, 1, 90);
        var nowEastern = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _easternTimeZone);
        var today = DateOnly.FromDateTime(nowEastern.DateTime);
        return snapshot with
        {
            Events = snapshot.Events
                .Where(item => item.EventDate >= today && item.EventDate <= today.AddDays(boundedDays))
                .OrderBy(item => item.EventDate)
                .ThenBy(item => item.ScheduledAtUtc)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static bool IsRelevantRelease(string title) =>
        title.Contains("CPI", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("consumer price", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("inflation", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("producer price", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("PPI", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("employment situation", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("payroll", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("unemployment", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("job openings", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("JOLTS", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("employment cost", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("GDP", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("gross domestic product", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("personal income and outlays", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("personal consumption expenditures", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("PCE", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("retail sales", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("retail", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("consumer confidence", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("initial claims", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("jobless claims", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("FOMC", StringComparison.OrdinalIgnoreCase);

    private static string GetCategory(string title)
    {
        if (title.Contains("CPI", StringComparison.OrdinalIgnoreCase) || title.Contains("inflation", StringComparison.OrdinalIgnoreCase) || title.Contains("consumer price", StringComparison.OrdinalIgnoreCase) || title.Contains("PPI", StringComparison.OrdinalIgnoreCase) || title.Contains("producer price", StringComparison.OrdinalIgnoreCase) || title.Contains("PCE", StringComparison.OrdinalIgnoreCase))
            return "Инфлация";
        if (title.Contains("employment", StringComparison.OrdinalIgnoreCase) || title.Contains("payroll", StringComparison.OrdinalIgnoreCase) || title.Contains("unemployment", StringComparison.OrdinalIgnoreCase) || title.Contains("JOLTS", StringComparison.OrdinalIgnoreCase) || title.Contains("job openings", StringComparison.OrdinalIgnoreCase) || title.Contains("claims", StringComparison.OrdinalIgnoreCase))
            return "Трудов пазар";
        if (title.Contains("GDP", StringComparison.OrdinalIgnoreCase) || title.Contains("gross domestic product", StringComparison.OrdinalIgnoreCase))
            return "Растеж";
        if (title.Contains("FOMC", StringComparison.OrdinalIgnoreCase))
            return "Монетарна политика";
        if (title.Contains("retail", StringComparison.OrdinalIgnoreCase))
            return "Потребление";
        return "Макроикономика";
    }

    private static string GetImpact(string title) =>
        title.Contains("CPI", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("consumer price", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("employment situation", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("payroll", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("GDP", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("FOMC", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("personal income and outlays", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("PCE", StringComparison.OrdinalIgnoreCase)
            ? "Високо"
            : "Средно";

    private TimeZoneInfo FindTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return _easternTimeZone;
        }
        catch (InvalidTimeZoneException)
        {
            return _easternTimeZone;
        }
    }

    private static TimeZoneInfo FindEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(EasternTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
    }

    private static string UnescapeIcsText(string value) => value
        .Replace("\\n", " ", StringComparison.OrdinalIgnoreCase)
        .Replace("\\,", ",", StringComparison.Ordinal)
        .Replace("\\;", ";", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static string Slug(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static int ReadBoundedInt(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    private static EconomicCalendarSnapshot? LoadCache(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<EconomicCalendarSnapshot>(File.ReadAllText(path), CacheJsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void PersistCache(EconomicCalendarSnapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot, CacheJsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not persist the economic calendar cache.");
        }
    }

    private sealed record SourceFetchResult(
        EconomicCalendarSourceStatus Status,
        IReadOnlyList<EconomicCalendarEvent> Events);
}
