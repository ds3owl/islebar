using System.Globalization;
using System.Net.Http;
using IsleBar.Core.SystemWatch;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Calendar notices. Fetches the ICS URL from settings (the "secret iCal address" of Google/Outlook calendars) every 10 minutes,
/// re-evaluates against the current time every 15 s: from 10 min before start "in 10 min · Team meeting" (until start), then "Team meeting · now" (1 min).
/// Parsing, recurrence expansion and notice rules are in Core (<see cref="IcsParser"/>, <see cref="IcsSchedule"/>, <see cref="CalendarLogic"/>) —
/// the limits noted in their summaries (rules like BYMONTHDAY or "first Monday" only yield the first occurrence; all-day events aren't notified) apply as-is.
/// If fetching fails (offline, bad URL), keeps notifying from the last fetched events.
/// The URL contains a secret token, so it is never logged anywhere. No UI thread needed.
/// </summary>
internal sealed class CalendarWatcher : NoticeWatcher
{
    private static readonly TimeSpan FetchInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Http = CreateClient();

    private readonly string _url;
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyList<IcsEvent> _events = [];
    private Timer? _tick;
    private bool _started;

    public CalendarWatcher(Func<Core.Localization.LanguageStrings> strings, string icsUrl)
        : base(strings)
    {
        _url = NormalizeUrl(icsUrl);
    }

    /// <summary>The URL this watcher uses (to compare whether the setting changed).</summary>
    public string Url => _url;

    public override void Start() => Guard(() =>
    {
        if (_started || _url.Length == 0)
        {
            return;
        }

        _started = true;
        _ = Task.Run(FetchLoopAsync);
        _tick = new Timer(_ => Guard(Tick), null, TickInterval, TickInterval);
    });

    protected override void Stop()
    {
        _stop.Cancel();
        _tick?.Dispose();
        _stop.Dispose();
    }

    private async Task FetchLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var text = await Http.GetStringAsync(_url, token).ConfigureAwait(false);
                var events = IcsParser.Parse(text);
                if (events.Count > 0 || text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                {
                    _events = events;   // keep the previous events if the response is bogus (e.g. a login page)
                }

                Guard(Tick);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
            {
                // Retry on the next tick (timeouts arrive as OperationCanceledException)
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // an unexpected parse failure (e.g. an RRULE UNTIL at year 9999) ended the loop silently — the calendar stopped
                // refreshing until a restart (review 10-03). Keep the last events and try again next time.
                Interop.AppLog.Write("calendar: refresh failed (" + ex.GetType().Name + ")");   // not the message: it can quote a calendar line
            }

            try
            {
                await Task.Delay(FetchInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        var occurrences = IcsSchedule.Occurrences(_events, now - CalendarLogic.NowLinger, now + CalendarLogic.Lead);
        var s = Text;
        var notices = new List<KeyValuePair<string, Core.Island.ActivityState>>();
        foreach (var alert in CalendarLogic.Alerts(occurrences, now))
        {
            var o = alert.Occurrence;
            var key = "calendar:" + o.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + ":" + o.Summary;
            var at = o.Start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
            var summary = string.IsNullOrWhiteSpace(o.Summary) ? at : o.Summary;
            if (alert.Phase == CalendarPhase.Soon)
            {
                // Make the 10-minute ring fill up: total = 10 min, elapsed = 10 min − remaining
                var lead = (long)CalendarLogic.Lead.TotalSeconds;
                var left = (long)Math.Max(0, (o.Start - now).TotalSeconds);
                notices.Add(new(key, SystemNotice.Make(
                    NoticeText.EventSoon(s, alert.MinutesLeft, summary), NoticeGlyphs.Calendar, o.Start - CalendarLogic.Lead,
                    msg: at, done: lead - Math.Min(lead, left), total: lead)));
            }
            else
            {
                notices.Add(new(key + ":now", SystemNotice.Make(
                    NoticeText.EventNow(s, summary), NoticeGlyphs.Calendar, o.Start, msg: at)));
            }
        }

        Board.ReplaceAll(notices);
    }

    /// <summary><c>webcal://</c> becomes https. Trims whitespace. Empty string (= off) if not http(s).</summary>
    internal static string NormalizeUrl(string? url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value["webcal://".Length..];
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? value
            : string.Empty;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 20 * 1024 * 1024,   // room even for calendars with years of events
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IsleBar/1.0");
        return client;
    }
}
