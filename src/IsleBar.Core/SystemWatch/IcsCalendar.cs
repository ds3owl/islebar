using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace IsleBar.Core.SystemWatch;

/// <summary>
/// The part of a recurrence rule (RRULE) we handle. Rules we cannot handle are <see cref="Unsupported"/> — only the first occurrence (DTSTART) of that event is used.
/// </summary>
public sealed record IcsRule(
    string Freq,
    int Interval,
    int? Count,
    DateTimeOffset? Until,
    IReadOnlyList<DayOfWeek> ByDay,
    bool Unsupported,
    DayOfWeek WeekStart = DayOfWeek.Monday);

/// <summary>One VEVENT from an ICS file.</summary>
public sealed class IcsEvent
{
    public string Uid { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    /// <summary>Start "wall clock" time (relative to <see cref="Zone"/>). For all-day events, midnight that day.</summary>
    public DateTime Start { get; init; }

    /// <summary>Time zone of the start time. UTC if Z, that zone if TZID, and this PC's zone if neither (floating time).</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;

    public bool AllDay { get; init; }

    public IcsRule? Rule { get; init; }

    /// <summary>Occurrences excluded from the recurrence (UTC).</summary>
    public IReadOnlyList<DateTimeOffset> ExDates { get; init; } = [];

    /// <summary>If this VEVENT modifies one occurrence of a recurring event, that occurrence's original time (UTC).</summary>
    public DateTimeOffset? RecurrenceId { get; init; }

    /// <summary>STATUS:CANCELLED.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Start time (UTC).</summary>
    public DateTimeOffset StartUtc => IcsParser.ToUtc(Start, Zone);
}

/// <summary>One expanded occurrence.</summary>
public readonly record struct IcsOccurrence(string Summary, DateTimeOffset Start, bool AllDay);

/// <summary>
/// ICS (iCalendar) reading — just enough to read Google/Outlook calendars' "secret iCal address".
/// <list type="bullet">
/// <item>Times: <c>…Z</c> (UTC) · <c>TZID=</c> (IANA name <c>Asia/Seoul</c> or Windows name <c>Korea Standard Time</c>) ·
/// no time zone (this PC's) · all-day (<c>VALUE=DATE</c>).</item>
/// <item>Recurrence: INTERVAL, COUNT, UNTIL for DAILY, WEEKLY (+BYDAY), MONTHLY, YEARLY; EXDATE; modified occurrences (RECURRENCE-ID).</item>
/// <item><b>Not supported</b>: rules like BYMONTHDAY, BYSETPOS, "first Monday" (1MO) (only the first occurrence is used), (WKST is honoured — weeks start on Monday unless it says otherwise),
/// definitions inside VTIMEZONE (only the TZID name is trusted), RDATE.</item>
/// </list>
/// </summary>
public static class IcsParser
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> ZoneCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ICS text to a VEVENT list. Broken lines and unknown properties are skipped (never throws).</summary>
    /// <param name="local">Time zone for floating times and all-day events. Defaults to this PC's.</param>
    public static IReadOnlyList<IcsEvent> Parse(string? text, TimeZoneInfo? local = null)
    {
        local ??= TimeZoneInfo.Local;
        var events = new List<IcsEvent>();
        Builder? current = null;
        var depth = 0;   // sub-blocks inside a VEVENT, such as VALARM

        foreach (var line in Unfold(text ?? string.Empty))
        {
            if (!TrySplit(line, out var name, out var parameters, out var value))
            {
                continue;
            }

            if (name == "BEGIN")
            {
                if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && current is null)
                {
                    current = new Builder();
                }
                else if (current is not null)
                {
                    depth++;
                }

                continue;
            }

            if (name == "END")
            {
                if (current is not null && depth > 0)
                {
                    depth--;
                }
                else if (current is not null && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    if (current.Build() is { } built)
                    {
                        events.Add(built);
                    }

                    current = null;
                }

                continue;
            }

            if (current is null || depth > 0)
            {
                continue;
            }

            switch (name)
            {
                case "UID":
                    current.Uid = value;
                    break;
                case "SUMMARY":
                    current.Summary = Unescape(value);
                    break;
                case "STATUS":
                    current.Cancelled = value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
                    break;
                case "DTSTART":
                    if (ParseTime(value, parameters, local) is { } start)
                    {
                        current.Start = start;
                    }

                    break;
                case "RRULE":
                    current.RuleText = value;
                    break;
                case "EXDATE":
                    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (ParseTime(part, parameters, local) is { } ex)
                        {
                            current.ExDates.Add(ToUtc(ex.Wall, ex.Zone));
                        }
                    }

                    break;
                case "RECURRENCE-ID":
                    if (ParseTime(value, parameters, local) is { } rid)
                    {
                        current.RecurrenceId = ToUtc(rid.Wall, rid.Zone);
                    }

                    break;
            }
        }

        return events;
    }

    /// <summary>
    /// Time zone name → time zone. Accepts both IANA (<c>Asia/Seoul</c>) and Windows (<c>Korea Standard Time</c>) names.
    /// Null for unknown names (the caller then uses this PC's time zone).
    /// </summary>
    public static TimeZoneInfo? ResolveZone(string? tzid)
    {
        if (string.IsNullOrWhiteSpace(tzid))
        {
            return null;
        }

        return ZoneCache.GetOrAdd(tzid.Trim().Trim('"'), static id =>
        {
            foreach (var candidate in Candidates(id))
            {
                if (Find(candidate) is { } zone)
                {
                    return zone;
                }

                if (TimeZoneInfo.TryConvertIanaIdToWindowsId(candidate, out var windowsId) && Find(windowsId) is { } byWindows)
                {
                    return byWindows;
                }

                if (TimeZoneInfo.TryConvertWindowsIdToIanaId(candidate, out var ianaId) && Find(ianaId) is { } byIana)
                {
                    return byIana;
                }
            }

            return null;
        });

        // for prefixes like Thunderbird's "/mozilla.org/20050126_1/America/New_York", only the last two segments are used
        static IEnumerable<string> Candidates(string id)
        {
            yield return id;
            var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2)
            {
                yield return string.Join('/', parts[^2..]);
            }
        }

        static TimeZoneInfo? Find(string id)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Wall clock time → UTC. Times that do not exist due to DST (the hour skipped in spring) are pushed one hour later
    /// — throwing would make one event break reading the whole calendar.
    /// </summary>
    public static DateTimeOffset ToUtc(DateTime wall, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }

    private static (DateTime Wall, TimeZoneInfo Zone, bool AllDay)? ParseTime(
        string value, IReadOnlyDictionary<string, string> parameters, TimeZoneInfo local)
    {
        value = value.Trim();
        var dateOnly = value.Length == 8
                       || (parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase));
        if (dateOnly)
        {
            return DateTime.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
                ? (date, local, true)
                : null;
        }

        var utc = value.EndsWith('Z');
        var core = utc ? value[..^1] : value;
        if (!DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
        {
            return null;
        }

        if (utc)
        {
            return (wall, TimeZoneInfo.Utc, false);
        }

        var zone = parameters.TryGetValue("TZID", out var tzid) ? ResolveZone(tzid) ?? local : local;
        return (wall, zone, false);
    }

    /// <summary>RFC 5545 line unfolding: a line starting with a space or tab is appended to the previous line.</summary>
    private static IEnumerable<string> Unfold(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && raw[0] is ' ' or '\t')
            {
                sb.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (sb.Length > 0)
            {
                yield return sb.ToString();
            }

            sb.Clear().Append(raw.TrimEnd('\r'));
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    /// <summary>"DTSTART;TZID=Asia/Seoul:20260930T140000" → name, parameters, value. ':' and ';' inside quotes do not split.</summary>
    private static bool TrySplit(string line, out string name, out Dictionary<string, string> parameters, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }

        if (colon <= 0)
        {
            return false;
        }

        value = line[(colon + 1)..];
        var head = SplitOutsideQuotes(line[..colon], ';');
        name = head[0].Trim().ToUpperInvariant();
        foreach (var p in head.Skip(1))
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                parameters[p[..eq].Trim()] = p[(eq + 1)..].Trim().Trim('"');
            }
        }

        return name.Length > 0;
    }

    private static List<string> SplitOutsideQuotes(string text, char separator)
    {
        var parts = new List<string>();
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                quoted = !quoted;
            }
            else if (text[i] == separator && !quoted)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>Unescapes TEXT values (\n \, \; \\); for multi-line values the island shortens it based on the first line.</summary>
    private static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next is 'n' or 'N' ? ' ' : next);
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString().Trim();
    }

    internal static IcsRule ParseRule(string text, TimeZoneInfo local)
    {
        string freq = string.Empty;
        var interval = 1;
        int? count = null;
        DateTimeOffset? until = null;
        var byDay = new List<DayOfWeek>();
        var unsupported = false;
        var weekStart = DayOfWeek.Monday;

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq].ToUpperInvariant();
            var val = part[(eq + 1)..];
            switch (key)
            {
                case "FREQ":
                    freq = val.ToUpperInvariant();
                    break;
                case "INTERVAL":
                    interval = int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;
                    break;
                case "COUNT":
                    count = int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : null;
                    break;
                case "UNTIL":
                    if (ParseTime(val, new Dictionary<string, string>(), local) is { } u)
                    {
                        // a date alone includes the whole of that day
                        // (UNTIL=99991231 has no "next day": that threw and ended the calendar refresh — review 10-03)
                        until = ToUtc(u.AllDay ? (u.Wall.Date == DateTime.MaxValue.Date ? DateTime.MaxValue : u.Wall.AddDays(1).AddTicks(-1)) : u.Wall, u.Zone);
                    }

                    break;
                case "BYDAY":
                    foreach (var day in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (ParseDay(day) is { } d)
                        {
                            byDay.Add(d);
                        }
                        else
                        {
                            unsupported = true;   // ordinal weekdays like "1MO" or "-1FR"
                        }
                    }

                    break;
                case "WKST":
                    // the week a biweekly rule counts in: with WKST=SU a "every 2 weeks on MO,SU" Sunday falls in another week
                    // than with Monday-start weeks — it showed a week off (review 10-03)
                    weekStart = ParseDay(val) ?? DayOfWeek.Monday;
                    break;
                default:
                    if (key.StartsWith("BY", StringComparison.Ordinal))
                    {
                        unsupported = true;
                    }

                    break;
            }
        }

        if (freq is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
        {
            unsupported = true;
        }

        if (byDay.Count > 0 && freq is "MONTHLY" or "YEARLY")
        {
            unsupported = true;
        }

        return new IcsRule(freq, interval, count, until, byDay, unsupported, weekStart);
    }

    private static DayOfWeek? ParseDay(string token) => token.ToUpperInvariant() switch
    {
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        "SU" => DayOfWeek.Sunday,
        _ => null,
    };

    private sealed class Builder
    {
        public string Uid { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public (DateTime Wall, TimeZoneInfo Zone, bool AllDay)? Start { get; set; }

        public string? RuleText { get; set; }

        public List<DateTimeOffset> ExDates { get; } = [];

        public DateTimeOffset? RecurrenceId { get; set; }

        public bool Cancelled { get; set; }

        public IcsEvent? Build()
        {
            if (Start is not { } start)
            {
                return null;   // an event without a start time cannot be announced
            }

            return new IcsEvent
            {
                Uid = Uid,
                Summary = Summary,
                Start = start.Wall,
                Zone = start.Zone,
                AllDay = start.AllDay,
                Rule = RuleText is null || RecurrenceId is not null ? null : ParseRule(RuleText, start.Zone),
                ExDates = ExDates,
                RecurrenceId = RecurrenceId,
                Cancelled = Cancelled,
            };
        }
    }
}

/// <summary>Expands recurring events into the occurrences within a range.</summary>
public static class IcsSchedule
{
    /// <summary>Maximum occurrences to expand per event (so a broken rule cannot loop forever).</summary>
    private const int MaxSteps = 5000;

    /// <summary>
    /// Occurrences starting within [<paramref name="from"/>, <paramref name="to"/>] (in start order). Cancelled events, EXDATEs and
    /// occurrences modified by another VEVENT (RECURRENCE-ID) are excluded — a modified occurrence comes in separately as that VEVENT's single occurrence.
    /// </summary>
    public static IReadOnlyList<IcsOccurrence> Occurrences(IEnumerable<IcsEvent> events, DateTimeOffset from, DateTimeOffset to)
    {
        var list = events.ToList();
        var overridden = list
            .Where(e => e.RecurrenceId is not null && !string.IsNullOrEmpty(e.Uid))
            .Select(e => (e.Uid, e.RecurrenceId!.Value.UtcTicks))
            .ToHashSet();

        var result = new List<IcsOccurrence>();
        foreach (var e in list)
        {
            foreach (var start in Starts(e, from, to))
            {
                if (start < from || start > to)
                {
                    continue;
                }

                if (e.Cancelled
                    || e.ExDates.Any(x => x.UtcTicks == start.UtcTicks)
                    || (e.RecurrenceId is null && overridden.Contains((e.Uid, start.UtcTicks))))
                {
                    continue;
                }

                result.Add(new IcsOccurrence(e.Summary, start, e.AllDay));
            }
        }

        return [.. result.OrderBy(o => o.Start)];
    }

    private static IEnumerable<DateTimeOffset> Starts(IcsEvent e, DateTimeOffset from, DateTimeOffset to)
    {
        if (e.Rule is not { Unsupported: false } rule)
        {
            yield return e.StartUtc;
            yield break;
        }

        var produced = 0;
        var first = FirstStep(e, rule, from);
        for (var step = first; step < first + MaxSteps; step++)
        {
            foreach (var wall in Candidates(e, rule, step))
            {
                if (wall < e.Start)
                {
                    continue;
                }

                var utc = IcsParser.ToUtc(wall, e.Zone);
                if ((rule.Until is { } until && utc > until) || (rule.Count is { } count && produced >= count))
                {
                    yield break;
                }

                produced++;
                yield return utc;
            }

            if (IcsParser.ToUtc(PeriodStart(e, rule, step), e.Zone) > to)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// So a daily event started years ago is not counted from the beginning, skip to the period just before the range when there is no COUNT
    /// (with COUNT, counting must start from the beginning to know which occurrence it is).
    /// </summary>
    private static int FirstStep(IcsEvent e, IcsRule rule, DateTimeOffset from)
    {
        if (rule.Count is not null)
        {
            return 0;
        }

        var span = from.UtcDateTime - e.StartUtc.UtcDateTime - TimeSpan.FromDays(2);
        if (span <= TimeSpan.Zero)
        {
            return 0;
        }

        var steps = rule.Freq switch
        {
            "DAILY" => span.TotalDays / rule.Interval,
            "WEEKLY" => span.TotalDays / (7.0 * rule.Interval),
            "MONTHLY" => span.TotalDays / (31.0 * rule.Interval),
            _ => span.TotalDays / (366.0 * rule.Interval),
        };
        return Math.Max(0, (int)steps - 2);
    }

    private static DateTime PeriodStart(IcsEvent e, IcsRule rule, int step) => rule.Freq switch
    {
        "DAILY" => e.Start.AddDays((double)step * rule.Interval),
        "WEEKLY" => WeekStart(e.Start, rule.WeekStart).AddDays(7.0 * step * rule.Interval),
        "MONTHLY" => e.Start.AddMonths(step * rule.Interval),
        _ => e.Start.AddYears(step * rule.Interval),
    };

    private static IEnumerable<DateTime> Candidates(IcsEvent e, IcsRule rule, int step)
    {
        switch (rule.Freq)
        {
            case "DAILY":
                var day = e.Start.AddDays((double)step * rule.Interval);
                if (rule.ByDay.Count == 0 || rule.ByDay.Contains(day.DayOfWeek))
                {
                    yield return day;
                }

                break;
            case "WEEKLY":
                var week = WeekStart(e.Start, rule.WeekStart).AddDays(7.0 * step * rule.Interval);
                IReadOnlyList<DayOfWeek> days = rule.ByDay.Count > 0 ? rule.ByDay : new[] { e.Start.DayOfWeek };
                foreach (var offset in days.Select(d => DaysAfter(d, rule.WeekStart)).Distinct().Order())
                {
                    yield return week.AddDays(offset);
                }

                break;
            case "MONTHLY":
                var month = e.Start.AddMonths(step * rule.Interval);
                if (month.Day == e.Start.Day)
                {
                    yield return month;   // events on the 31st skip months that end on the 30th (RFC 5545)
                }

                break;
            default:
                var year = e.Start.AddYears(step * rule.Interval);
                if (year.Day == e.Start.Day)
                {
                    yield return year;
                }

                break;
        }
    }

    /// <summary>The first day of that week (WKST, Monday by default), at the same time of day as the first occurrence.</summary>
    private static DateTime WeekStart(DateTime start, DayOfWeek weekStart) => start.AddDays(-DaysAfter(start.DayOfWeek, weekStart));

    private static int DaysAfter(DayOfWeek day, DayOfWeek weekStart) => ((int)day - (int)weekStart + 7) % 7;
}

/// <summary>Calendar notice stage.</summary>
public enum CalendarPhase
{
    /// <summary>Starting soon — "In 10 min · Meeting", until it starts.</summary>
    Soon,

    /// <summary>Started — "Meeting · Now", for 1 minute.</summary>
    Now,
}

/// <summary>One calendar notice to show now.</summary>
public readonly record struct CalendarAlert(IcsOccurrence Occurrence, CalendarPhase Phase, int MinutesLeft);

/// <summary>
/// Calendar notice rules: "In N min" from 10 minutes before the start until the start, then "Now" for 1 minute after it starts.
/// All-day events are not announced (there is no basis for when to announce them, and they must not occupy the island all day).
/// The state is determined by time alone, so nothing is remembered — the same answer comes out whenever the app is started.
/// </summary>
public static class CalendarLogic
{
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan NowLinger = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<CalendarAlert> Alerts(IEnumerable<IcsOccurrence> occurrences, DateTimeOffset now)
    {
        var alerts = new List<CalendarAlert>();
        foreach (var o in occurrences.Where(o => !o.AllDay).DistinctBy(o => (o.Summary, o.Start.UtcTicks)))
        {
            if (now >= o.Start - Lead && now < o.Start)
            {
                alerts.Add(new CalendarAlert(o, CalendarPhase.Soon, (int)Math.Ceiling((o.Start - now).TotalMinutes)));
            }
            else if (now >= o.Start && now < o.Start + NowLinger)
            {
                alerts.Add(new CalendarAlert(o, CalendarPhase.Now, 0));
            }
        }

        return [.. alerts.OrderBy(a => a.Occurrence.Start)];
    }
}
