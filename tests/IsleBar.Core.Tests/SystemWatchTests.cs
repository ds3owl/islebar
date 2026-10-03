using IsleBar.Core.Configuration;
using IsleBar.Core.Island;
using IsleBar.Core.Localization;
using IsleBar.Core.SystemWatch;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class SystemWatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly LanguageStrings Ko = LanguageCatalog.For("ko");
    private static readonly LanguageStrings En = LanguageCatalog.For("en");

    // ---------------- Notice board ----------------

    [Fact]
    public void Flash_notices_expire_while_held_notices_remain()
    {
        var board = new NoticeBoard();
        board.Flash("a", SystemNotice.Make("잠깐", NoticeGlyphs.Copy, T0), TimeSpan.FromSeconds(2), T0);
        board.Hold("b", SystemNotice.Make("고정", NoticeGlyphs.Wifi, T0, urgent: true));

        Assert.Equal(2, board.Snapshot(T0.AddSeconds(1)).Count);
        var later = board.Snapshot(T0.AddSeconds(2));
        Assert.Equal(["고정"], later.Select(n => n.Title));

        board.Clear("b");
        Assert.Empty(board.Snapshot(T0.AddSeconds(3)));
    }

    [Fact]
    public void Posting_again_with_same_key_replaces()
    {
        var board = new NoticeBoard();
        board.Flash("power", SystemNotice.Make("첫째", NoticeGlyphs.Charging, T0), TimeSpan.FromSeconds(2), T0);
        board.Flash("power", SystemNotice.Make("둘째", NoticeGlyphs.Charging, T0), TimeSpan.FromSeconds(2), T0);

        var items = board.Snapshot(T0);
        Assert.Single(items);
        Assert.Equal("둘째", items[0].Title);
        Assert.Equal("system:power", items[0].SourcePath);
        Assert.True(board.Has("power", T0));
        Assert.False(board.Has("power", T0.AddSeconds(5)));
    }

    [Fact]
    public void Replace_all_at_once()
    {
        var board = new NoticeBoard();
        board.Flash("old", SystemNotice.Make("옛것", NoticeGlyphs.Calendar, T0), TimeSpan.FromMinutes(1), T0);
        board.ReplaceAll(
        [
            new("cal:1", SystemNotice.Make("하나", NoticeGlyphs.Calendar, T0)),
            new("cal:2", SystemNotice.Make("둘", NoticeGlyphs.Calendar, T0)),
        ]);

        Assert.Equal(["하나", "둘"], board.Snapshot(T0.AddHours(1)).Select(n => n.Title));
        board.ReplaceAll([]);
        Assert.Empty(board.Snapshot(T0));
    }

    [Fact]
    public void Notice_shape_is_notice_kind_and_error_when_urgent()
    {
        var calm = SystemNotice.Make("충전 중 · 78%", NoticeGlyphs.Charging, T0, done: 78, total: 100, open: "ms-settings:batterysaver");
        Assert.Equal(ActivityKind.Notice, calm.Kind);
        Assert.Equal("run", calm.State);
        Assert.Equal(0.78, calm.Fraction!.Value, 3);
        Assert.Equal(T0.ToUnixTimeSeconds(), calm.T0);
        Assert.Null(calm.Msg);

        var urgent = SystemNotice.Make("인터넷 끊김", NoticeGlyphs.NoInternet, T0, msg: " ", urgent: true);
        Assert.Equal(ActivityRunState.Error, urgent.RunState);
        Assert.Null(urgent.Msg);   // a blank message counts as none
    }

    [Theory]
    [InlineData(0, '\uEBA0')]
    [InlineData(4, '\uEBA0')]
    [InlineData(78, '\uEBA8')]
    [InlineData(100, '\uEBAA')]
    [InlineData(150, '\uEBAA')]
    public void Battery_icon_steps_by_10_percent(int percent, char glyph)
        => Assert.Equal(glyph.ToString(), NoticeGlyphs.Battery(percent));

    // ---------------- Text ----------------

    [Fact]
    public void Korean_text()
    {
        Assert.Equal("충전 중 · 78%", NoticeText.Charging(Ko, 78));
        Assert.Equal("배터리 · 76%", NoticeText.OnBattery(Ko, 76));
        Assert.Equal("배터리 20% · 약 45분", NoticeText.BatteryLow(Ko, 20, TimeSpan.FromMinutes(45)));
        Assert.Equal("배터리 10% · 약 1시간 20분", NoticeText.BatteryLow(Ko, 10, TimeSpan.FromMinutes(80.5)));
        Assert.Equal("에어팟 · 연결됨", NoticeText.Connected(Ko, "에어팟"));
        Assert.Equal("에어팟 · 연결 끊김", NoticeText.Disconnected(Ko, "에어팟"));
        Assert.Equal("복사됨 · 안녕", NoticeText.Copied(Ko, "안녕"));
        Assert.Equal("파일 복사됨", NoticeText.CopiedFiles(Ko, 1));
        Assert.Equal("파일 3개 복사됨", NoticeText.CopiedFiles(Ko, 3));
        Assert.Equal("CPU 96% · chrome", NoticeText.Cpu(Ko, 96, "chrome"));
        Assert.Equal("CPU 96%", NoticeText.Cpu(Ko, 96, null));
        Assert.Equal("메모리 93%", NoticeText.Memory(Ko, 93));
        Assert.Equal("10분 후 · 팀 회의", NoticeText.EventSoon(Ko, 10, "팀 회의"));
        Assert.Equal("팀 회의 · 지금", NoticeText.EventNow(Ko, "팀 회의"));
        Assert.Equal("마이크 사용 중 · Zoom", NoticeText.InUse(Ko, camera: false, "Zoom"));
        Assert.Equal("카메라 사용 중", NoticeText.InUse(Ko, camera: true, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.5)]            // under 1 minute
    [InlineData(60 * 24 + 1.0)]  // over a day = Windows doesn't know yet
    public void Unknown_remaining_time_shows_only_the_prefix(double? minutes)
    {
        TimeSpan? remaining = minutes is { } m ? TimeSpan.FromMinutes(m) : null;
        Assert.Equal("Battery 20%", NoticeText.BatteryLow(En, 20, remaining));
    }

    [Fact]
    public void Notice_text_in_every_language_has_placeholders()
    {
        foreach (var info in LanguageCatalog.Languages)
        {
            var s = LanguageCatalog.For(info.Code);
            Assert.Contains("{p}", s.NoticeCharging);
            Assert.Contains("{p}", s.NoticeOnBattery);
            Assert.Contains("{p}", s.NoticeBatteryLevel);
            Assert.Contains("{p}", s.NoticeCpu);
            Assert.Contains("{p}", s.NoticeMemory);
            Assert.Contains("{m}", s.NoticeAboutMin);
            Assert.Contains("{h}", s.NoticeAboutHourMin);
            Assert.Contains("{m}", s.NoticeAboutHourMin);
            Assert.Contains("{}", s.NoticeConnected);
            Assert.Contains("{}", s.NoticeDisconnected);
            Assert.Contains("{}", s.NoticeCopied);
            Assert.Contains("{n}", s.NoticeCopiedFiles);
            Assert.Contains("{m}", s.NoticeEventSoon);

            // No placeholders remain after filling
            Assert.DoesNotContain("{", NoticeText.BatteryLow(s, 20, TimeSpan.FromMinutes(95)));
            Assert.DoesNotContain("{", NoticeText.CopiedFiles(s, 4));
            Assert.DoesNotContain("{", NoticeText.EventSoon(s, 3, "x"));
        }
    }

    // ---------------- Power ----------------

    [Fact]
    public void Nothing_on_startup_only_plug_and_unplug_are_announced()
    {
        var logic = new PowerLogic();
        Assert.Equal(PowerChange.None, logic.Observe(new(true, false, 80)));
        Assert.Equal(PowerChange.None, logic.Observe(new(true, false, 79)));     // only the percentage changed
        Assert.Equal(PowerChange.PluggedIn, logic.Observe(new(true, true, 79)));
        Assert.Equal(PowerChange.None, logic.Observe(new(true, true, 80)));      // duplicate event
        Assert.Equal(PowerChange.Unplugged, logic.Observe(new(true, false, 80)));
    }

    [Fact]
    public void Without_battery_nothing_is_announced()
    {
        var logic = new PowerLogic();
        Assert.Equal(PowerChange.None, logic.Observe(new(false, true, 100)));
        Assert.Equal(PowerChange.None, logic.Observe(new(false, false, 100)));
        Assert.Null(PowerLogic.LowLevel(new(false, false, 5)));
    }

    [Theory]
    [InlineData(false, 25, null)]
    [InlineData(false, 21, null)]
    [InlineData(false, 20, 20)]
    [InlineData(false, 15, 20)]
    [InlineData(false, 10, 10)]
    [InlineData(false, 3, 10)]
    [InlineData(true, 3, null)]      // no warning while charging
    public void Low_battery_thresholds(bool plugged, int percent, int? expected)
        => Assert.Equal(expected, PowerLogic.LowLevel(new(true, plugged, percent)));

    // ---------------- Internet ----------------

    [Fact]
    public void Internet_startup_value_is_baseline_and_changes_confirm_after_3_seconds()
    {
        var logic = new ConnectivityLogic();
        logic.Report(true, T0);
        Assert.Equal(ConnectivityChange.None, logic.Tick(T0.AddSeconds(3)));   // baseline

        logic.Report(false, T0.AddSeconds(10));
        Assert.Equal(ConnectivityChange.None, logic.Tick(T0.AddSeconds(11)));
        logic.Report(true, T0.AddSeconds(12));                                  // back after 2 seconds = a blip
        Assert.Equal(ConnectivityChange.None, logic.Tick(T0.AddSeconds(16)));

        logic.Report(false, T0.AddSeconds(20));
        Assert.Equal(ConnectivityChange.Lost, logic.Tick(T0.AddSeconds(23)));
        Assert.True(logic.LostShown);
        Assert.Equal(ConnectivityChange.None, logic.Tick(T0.AddSeconds(30)));

        logic.Report(true, T0.AddSeconds(40));
        Assert.Equal(ConnectivityChange.Restored, logic.Tick(T0.AddSeconds(43)));
        Assert.False(logic.LostShown);
    }

    [Fact]
    public void Offline_at_startup_then_connecting_passes_silently()
    {
        var logic = new ConnectivityLogic();
        logic.Report(false, T0);
        Assert.Equal(ConnectivityChange.None, logic.Tick(T0.AddSeconds(3)));
        logic.Report(true, T0.AddSeconds(5));
        Assert.Equal(ConnectivityChange.None, logic.Tick(T0.AddSeconds(8)));
    }

    // ---------------- Bluetooth ----------------

    [Fact]
    public void Initial_list_is_not_announced_only_later_changes()
    {
        var tracker = new ConnectionTracker();
        Assert.Null(tracker.Update("mouse", "MX Master", true, T0));   // already connected at startup
        tracker.CompleteEnumeration();

        var connected = tracker.Update("buds", "Galaxy Buds", true, T0.AddSeconds(1));
        Assert.Equal(new ConnectionChange("buds", "Galaxy Buds", true), connected);

        Assert.Null(tracker.Update("buds", "Galaxy Buds", true, T0.AddSeconds(2)));   // unchanged
        Assert.Null(tracker.Update("buds", null, null, T0.AddSeconds(3)));             // unknown values keep the previous state

        var gone = tracker.Update("buds", null, false, T0.AddSeconds(10));
        Assert.Equal(new ConnectionChange("buds", "Galaxy Buds", false), gone);
        Assert.Equal(1, tracker.ConnectedCount);
    }

    [Fact]
    public void Classic_and_LE_twins_are_announced_once()
    {
        var tracker = new ConnectionTracker();
        tracker.CompleteEnumeration();
        Assert.NotNull(tracker.Update("classic", "WH-1000XM5", true, T0));
        Assert.Null(tracker.Update("le", "WH-1000XM5", true, T0.AddSeconds(1)));
        Assert.NotNull(tracker.Update("classic", "WH-1000XM5", true, T0.AddSeconds(1)) ?? tracker.Update("classic", null, false, T0.AddSeconds(2)));
        Assert.Null(tracker.Update("le", null, false, T0.AddSeconds(2)));
    }

    [Fact]
    public void Connected_device_removed_from_list_means_disconnected()
    {
        var tracker = new ConnectionTracker();
        tracker.CompleteEnumeration();
        tracker.Update("kb", "Keyboard", true, T0);
        Assert.Equal(new ConnectionChange("kb", "Keyboard", false), tracker.Remove("kb", T0.AddSeconds(30)));
        Assert.Null(tracker.Remove("kb", T0.AddSeconds(31)));
    }

    // ---------------- Clipboard ----------------

    [Theory]
    [InlineData("Tr0ub4dor&3", true)]
    [InlineData("hunter2Hunter", true)]          // lower, upper, digits
    [InlineData("k9$mq!zp", true)]               // lower, digits, symbols, 8 chars
    [InlineData("password", false)]              // one class only
    [InlineData("Abc12!", false)]                // fewer than 8 chars
    [InlineData("안녕하세요 반갑습니다", false)]
    [InlineData("hello world 123 ABC!", false)]  // contains spaces
    [InlineData("https://example.com/A1?b=2", false)]
    [InlineData(@"C:\Users\Me\Doc1.txt", false)]
    [InlineData("John.Doe1@example.com", false)]
    [InlineData("ghp_aB3dE5fG7hI9jK1lM3nO5pQ7rS9tU1vW3xY5", true)]   // token
    // review 10-03: these used to show on the pill
    [InlineData("sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-AbCdEfGh", true)]   // > 64 chars
    [InlineData("3f2a9c4e8b7d6a5f4e3d2c1b0a9f8e7d", true)]          // hex token: lowercase + digits
    [InlineData("hunter2024", true)]                                 // letters + digits, 10
    [InlineData("API_KEY=sk-abc123", true)]
    [InlineData("password: Xy9 secret phrase", true)]
    [InlineData("비밀번호: 1234abcd", true)]
    [InlineData("4111111111111111", true)]                           // card number
    [InlineData("P@ssw0rd.1", true)]                                 // not an email address
    [InlineData("C:\\Users\\me\\OneDrive - Personal\\Documents\\report2026.pdf", false)]   // a path with spaces
    [InlineData("see github.com/ds3owl/islebar/blob/main/x2.cs", false)]
    [InlineData("Kim@1990.06", true)]
    [InlineData("me.kim+news@mail.example.co.kr", false)]            // an email address
    [InlineData("postgres://admin:hunter2@db.local/app", true)]      // credentials in a URL
    [InlineData("https://github.com/ds3owl/islebar", false)]
    [InlineData("sk-proj-AbCdEf1234567890XyZ (expires 30d)", true)]   // a key followed by words
    [InlineData("Windows 11 build 26200 is out", false)]             // an ordinary sentence
    [InlineData("see C:\\Users\\me\\Downloads\\report2026.pdf now", false)]
    [InlineData("4111-1111-1111-1111", true)]
    [InlineData("4111 1111 1111 1111", true)]
    [InlineData("1791014422000", false)]                             // a millisecond timestamp (fails Luhn)
    [InlineData("Hunter2.pw", true)]                                 // not a known file extension
    [InlineData("/home/me/projects/app", false)]                     // a Unix path
    [InlineData("150000", false)]                                    // a price
    [InlineData("20261003", false)]                                  // a date
    [InlineData("IMG_20261003.jpg", false)]                          // a file name
    [InlineData("compass:north", false)]
    [InlineData("AKIAIOSFODNN7EXAMPLE", true)]                       // AWS access key
    [InlineData("\"api_key\": \"abc\"", true)]
    [InlineData("01012345678", false)]                               // a phone number stays readable
    [InlineData("2026", false)]
    [InlineData("report_final", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Looks_like_a_password(string? text, bool expected)
        => Assert.Equal(expected, ClipboardRules.LooksSecret(text));

    [Fact]
    public void Preview_collapses_to_one_line_and_masks_passwords()
    {
        Assert.Equal("첫 줄 둘째 줄", ClipboardRules.Preview("  첫 줄\r\n\t둘째 줄  "));
        Assert.Equal("abcdefghijklmnopqrstuvwx…", ClipboardRules.Preview("abcdefghijklmnopqrstuvwxyz and more"));
        Assert.Equal("••••••", ClipboardRules.Preview("Tr0ub4dor&3"));
        Assert.Equal("••••••", ClipboardRules.Preview("그냥 글", fromPasswordManager: true));
        Assert.Equal(string.Empty, ClipboardRules.Preview("   \n "));

        // Don't cut an emoji (surrogate pair) in half
        var emoji = new string('a', 23) + "😀😀";
        var preview = ClipboardRules.Preview(emoji);
        Assert.EndsWith("…", preview);
        Assert.False(char.IsHighSurrogate(preview[^2]));
    }

    [Theory]
    [InlineData("KeePass.exe", true)]
    [InlineData("KeePassXC", true)]
    [InlineData("1Password.exe", true)]
    [InlineData("Bitwarden.exe", true)]
    [InlineData("chrome.exe", false)]
    [InlineData("keyboard.exe", false)]
    [InlineData(null, false)]
    public void Detects_password_manager(string? process, bool expected)
        => Assert.Equal(expected, ClipboardRules.IsPasswordManager(process));

    // ---------------- CPU / memory ----------------

    [Fact]
    public void Turns_on_after_30_seconds_sustained_and_off_after_10_seconds_low()
    {
        var rule = SustainedThreshold.Cpu();
        Assert.False(rule.Update(95, T0));
        Assert.True(rule.Elevated);
        Assert.False(rule.Update(96, T0.AddSeconds(28)));
        Assert.True(rule.Update(97, T0.AddSeconds(30)));

        Assert.True(rule.Update(70, T0.AddSeconds(32)));     // dips briefly
        Assert.True(rule.Update(80, T0.AddSeconds(36)));     // between 75 and 90 = hold, low timer resets
        Assert.True(rule.Update(70, T0.AddSeconds(38)));
        Assert.False(rule.Update(60, T0.AddSeconds(48)));
        Assert.False(rule.Elevated);
    }

    [Fact]
    public void Ignores_brief_spikes()
    {
        var rule = SustainedThreshold.Memory();
        rule.Update(95, T0);
        rule.Update(80, T0.AddSeconds(20));                   // interrupted → start over
        Assert.False(rule.Update(95, T0.AddSeconds(40)));
        Assert.False(rule.Update(95, T0.AddSeconds(60)));
        Assert.True(rule.Update(95, T0.AddSeconds(70)));
    }

    [Fact]
    public void Cpu_usage_calculation()
    {
        // Kernel time includes idle time: kernel 600 (of which idle 500) + user 400 = total 1000, busy 500
        Assert.Equal(50, CpuMath.TotalPercent(500, 600, 400), 3);
        Assert.Equal(0, CpuMath.TotalPercent(0, 0, 0));
        Assert.Equal(25, CpuMath.ProcessPercent(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2), 8), 3);
        Assert.Equal(0, CpuMath.ProcessPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 8));
    }

    // ---------------- Microphone / camera ----------------

    [Theory]
    [InlineData(133000000000000000L, 0L, true)]
    [InlineData(133000000000000000L, 133000000000000100L, false)]
    [InlineData(0L, 0L, false)]
    [InlineData(null, null, false)]
    public void Detects_in_use(long? start, long? stop, bool expected)
        => Assert.Equal(expected, PrivacyRules.IsInUse(start, stop));

    [Fact]
    public void Builds_app_names()
    {
        Assert.Equal("Zoom", PrivacyRules.FriendlyName("C:#Program Files#Zoom#bin#Zoom.exe", packaged: false));
        Assert.Equal(@"C:\Program Files\Zoom\bin\Zoom.exe", PrivacyRules.PathFromNonPackagedKey("C:#Program Files#Zoom#bin#Zoom.exe"));
        Assert.Equal("WindowsCamera", PrivacyRules.FriendlyName("Microsoft.WindowsCamera_8wekyb3d8bbwe", packaged: true));
        Assert.Equal("WhatsAppDesktop", PrivacyRules.FriendlyName("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", packaged: true));
        Assert.True(PrivacyRules.IsSelf("C:#Tools#IsleBar#IsleBar.App.exe", @"c:\tools\islebar\IsleBar.App.exe"));
        Assert.False(PrivacyRules.IsSelf("C:#Tools#IsleBar#IsleBar.App.exe", null));
    }

    // ---------------- ICS ----------------

    private static readonly TimeZoneInfo Seoul = IcsParser.ResolveZone("Asia/Seoul")!;

    private static string Calendar(params string[] events)
        => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + string.Join("\r\n", events) + "\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void Time_zone_names_accept_both_IANA_and_Windows()
    {
        Assert.NotNull(Seoul);
        Assert.Equal(TimeSpan.FromHours(9), Seoul.BaseUtcOffset);
        Assert.Equal(TimeSpan.FromHours(9), IcsParser.ResolveZone("Korea Standard Time")!.BaseUtcOffset);
        Assert.Equal(TimeSpan.FromHours(-5), IcsParser.ResolveZone("\"/mozilla.org/20050126_1/America/New_York\"")!.BaseUtcOffset);
        Assert.Null(IcsParser.ResolveZone("Nowhere/Atlantis"));
        Assert.Null(IcsParser.ResolveZone(""));
    }

    [Fact]
    public void Reads_each_time_format()
    {
        var ics = Calendar(
            "BEGIN:VEVENT", "UID:1", "SUMMARY:UTC 일정", "DTSTART:20260930T050000Z", "END:VEVENT",
            "BEGIN:VEVENT", "UID:2", "SUMMARY:서울 일정", "DTSTART;TZID=Asia/Seoul:20260930T140000", "END:VEVENT",
            "BEGIN:VEVENT", "UID:3", "SUMMARY:떠 있는 시각", "DTSTART:20260930T140000", "END:VEVENT",
            "BEGIN:VEVENT", "UID:4", "SUMMARY:종일", "DTSTART;VALUE=DATE:20260930", "END:VEVENT",
            "BEGIN:VEVENT", "UID:5", "SUMMARY:시작 없음", "END:VEVENT");

        var events = IcsParser.Parse(ics, Seoul);
        Assert.Equal(4, events.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 5, 0, 0, TimeSpan.Zero), events[0].StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 5, 0, 0, TimeSpan.Zero), events[1].StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 5, 0, 0, TimeSpan.Zero), events[2].StartUtc);   // interpreted in Seoul time
        Assert.True(events[3].AllDay);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero), events[3].StartUtc);
    }

    [Fact]
    public void Folded_lines_escapes_and_nested_blocks()
    {
        var ics = Calendar(
            "BEGIN:VEVENT",
            "UID:x",
            "SUMMARY:아주 긴 회의\\, 그리고",
            " 이어지는 제목\\; 끝",
            "DTSTART:20260930T050000Z",
            "BEGIN:VALARM",
            "SUMMARY:알람 제목은 무시",
            "END:VALARM",
            "END:VEVENT");

        var e = Assert.Single(IcsParser.Parse(ics, Seoul));
        Assert.Equal("아주 긴 회의, 그리고이어지는 제목; 끝", e.Summary);
    }

    [Fact]
    public void Weekly_recurrence_follows_BYDAY_and_EXDATE()
    {
        // From 2026-09-28 (Mon), Mon and Wed at 10:00 (Seoul), excluding Wed 9/30
        var ics = Calendar(
            "BEGIN:VEVENT", "UID:w", "SUMMARY:스탠드업",
            "DTSTART;TZID=Asia/Seoul:20260928T100000",
            "RRULE:FREQ=WEEKLY;BYDAY=MO,WE",
            "EXDATE;TZID=Asia/Seoul:20260930T100000",
            "END:VEVENT");

        var from = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.FromHours(9));
        var to = from.AddDays(14);
        var starts = IcsSchedule.Occurrences(IcsParser.Parse(ics, Seoul), from, to)
            .Select(o => o.Start.ToOffset(TimeSpan.FromHours(9)).ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(["09-28 10:00", "10-05 10:00", "10-07 10:00"], starts);
    }

    [Fact]
    public void Daily_recurrence_COUNT_and_UNTIL()
    {
        var counted = Calendar("BEGIN:VEVENT", "UID:c", "SUMMARY:c", "DTSTART:20260901T000000Z", "RRULE:FREQ=DAILY;COUNT=3", "END:VEVENT");
        var until = Calendar("BEGIN:VEVENT", "UID:u", "SUMMARY:u", "DTSTART:20260901T000000Z", "RRULE:FREQ=DAILY;INTERVAL=2;UNTIL=20260907T000000Z", "END:VEVENT");
        var from = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(90);

        Assert.Equal(3, IcsSchedule.Occurrences(IcsParser.Parse(counted, Seoul), from, to).Count);
        Assert.Equal([1, 3, 5, 7], IcsSchedule.Occurrences(IcsParser.Parse(until, Seoul), from, to).Select(o => o.Start.Day));
    }

    [Fact]
    public void Weekday_recurrence_DAILY_BYDAY()
    {
        var ics = Calendar("BEGIN:VEVENT", "UID:d", "SUMMARY:d", "DTSTART:20260928T000000Z", "RRULE:FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR", "END:VEVENT");
        var from = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        var days = IcsSchedule.Occurrences(IcsParser.Parse(ics, Seoul), from, from.AddDays(7).AddSeconds(-1)).Select(o => o.Start.DayOfWeek).ToArray();
        Assert.Equal(5, days.Length);
        Assert.DoesNotContain(DayOfWeek.Saturday, days);
    }

    [Fact]
    public void Daily_event_started_long_ago_expands_quickly()
    {
        var ics = Calendar("BEGIN:VEVENT", "UID:old", "SUMMARY:old", "DTSTART:20100101T090000Z", "RRULE:FREQ=DAILY", "END:VEVENT");
        var from = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        var o = Assert.Single(IcsSchedule.Occurrences(IcsParser.Parse(ics, Seoul), from, from.AddDays(1).AddSeconds(-1)));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero), o.Start);
    }

    [Fact]
    public void Keeps_wall_clock_time_across_DST()
    {
        // New York every Monday 09:00 — stays 09:00 after DST ends on the first Sunday of November (UTC goes 13 → 14)
        var ics = Calendar("BEGIN:VEVENT", "UID:ny", "SUMMARY:ny", "DTSTART;TZID=America/New_York:20261026T090000", "RRULE:FREQ=WEEKLY", "END:VEVENT");
        var from = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var hours = IcsSchedule.Occurrences(IcsParser.Parse(ics, Seoul), from, from.AddDays(14)).Select(o => o.Start.UtcDateTime.Hour).ToArray();
        Assert.Equal([13, 14], hours);
    }

    [Fact]
    public void Modified_occurrence_and_cancelled_event()
    {
        var ics = Calendar(
            "BEGIN:VEVENT", "UID:r", "SUMMARY:주간회의", "DTSTART:20261001T010000Z", "RRULE:FREQ=WEEKLY;COUNT=3", "END:VEVENT",
            "BEGIN:VEVENT", "UID:r", "SUMMARY:주간회의(옮김)", "RECURRENCE-ID:20261008T010000Z", "DTSTART:20261008T050000Z", "END:VEVENT",
            "BEGIN:VEVENT", "UID:gone", "SUMMARY:취소됨", "STATUS:CANCELLED", "DTSTART:20261002T010000Z", "END:VEVENT");

        var from = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        var list = IcsSchedule.Occurrences(IcsParser.Parse(ics, Seoul), from, from.AddDays(30));
        Assert.Equal(["주간회의", "주간회의(옮김)", "주간회의"], list.Select(o => o.Summary));
        Assert.Equal(5, list[1].Start.Hour);
    }

    [Fact]
    public void Unsupported_rule_yields_first_occurrence_only()
    {
        var ics = Calendar("BEGIN:VEVENT", "UID:m", "SUMMARY:m", "DTSTART:20260901T000000Z", "RRULE:FREQ=MONTHLY;BYDAY=1MO", "END:VEVENT");
        var e = Assert.Single(IcsParser.Parse(ics, Seoul));
        Assert.True(e.Rule!.Unsupported);
        var from = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Single(IcsSchedule.Occurrences([e], from, from.AddDays(365)));
    }

    [Fact]
    public void Broken_ICS_does_not_throw()
    {
        Assert.Empty(IcsParser.Parse(null));
        Assert.Empty(IcsParser.Parse("<html>로그인이 필요합니다</html>"));
        Assert.Empty(IcsParser.Parse("BEGIN:VEVENT\nDTSTART:어제\nEND:VEVENT"));
    }

    [Fact]
    public void Event_alerts_run_from_10_minutes_before_to_1_minute_after_start()
    {
        var start = T0.AddMinutes(30);
        IcsOccurrence[] occ = [new("회의", start, false), new("휴가", start, true)];

        Assert.Empty(CalendarLogic.Alerts(occ, start.AddMinutes(-11)));

        var soon = Assert.Single(CalendarLogic.Alerts(occ, start.AddMinutes(-10)));
        Assert.Equal(CalendarPhase.Soon, soon.Phase);
        Assert.Equal(10, soon.MinutesLeft);
        Assert.Equal(1, CalendarLogic.Alerts(occ, start.AddSeconds(-20))[0].MinutesLeft);   // rounded up

        var now = Assert.Single(CalendarLogic.Alerts(occ, start.AddSeconds(30)));
        Assert.Equal(CalendarPhase.Now, now.Phase);
        Assert.Empty(CalendarLogic.Alerts(occ, start.AddMinutes(1)));
    }

    // ---------------- Settings ----------------

    [Fact]
    public void Notice_settings_default_to_all_on_and_calendar_blank()
    {
        using var dir = new TempDir();
        var settings = new ConfigStore(dir.File("islebar.json")).Load();
        Assert.True(settings.NotifyPower);
        Assert.True(settings.NotifyBluetooth);
        Assert.True(settings.NotifyNetwork);
        Assert.True(settings.NotifyFocus);
        Assert.True(settings.NotifyClipboard);
        Assert.True(settings.NotifyLoad);
        Assert.True(settings.NotifyPrivacy);
        Assert.Equal("", settings.CalendarIcs);

        var options = SystemWatchOptions.From(settings);
        Assert.Equal(new SystemWatchOptions(), options);
        Assert.False(options.Calendar);
    }

    [Fact]
    public void Notice_settings_save_and_read_back()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("islebar.json"));
        var settings = store.Load();
        settings.NotifyClipboard = false;
        settings.NotifyLoad = false;
        settings.CalendarIcs = "https://calendar.example.com/private/basic.ics";
        Assert.True(store.Save(settings));

        var text = File.ReadAllText(store.Path);
        Assert.Contains("\"notify_clipboard\": false", text);
        Assert.Contains("\"calendar_ics\"", text);

        var loaded = store.Load();
        Assert.False(loaded.NotifyClipboard);
        Assert.False(loaded.NotifyLoad);
        Assert.True(loaded.NotifyPower);
        Assert.Equal("https://calendar.example.com/private/basic.ics", loaded.CalendarIcs);
        Assert.True(SystemWatchOptions.From(loaded).Calendar);

        var clone = loaded.Clone();
        Assert.False(clone.NotifyClipboard);
        Assert.Equal(loaded.CalendarIcs, clone.CalendarIcs);
    }

    [Fact]
    public void Notice_settings_with_wrong_type_fall_back_to_default()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        File.WriteAllText(path, """{ "notify_power": "no", "notify_focus": false, "calendar_ics": 5 }""");
        var settings = new ConfigStore(path).Load();
        Assert.True(settings.NotifyPower);
        Assert.False(settings.NotifyFocus);
        Assert.Equal("", settings.CalendarIcs);
    }
}
