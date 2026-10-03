using IsleBar.Core.Island;
using IsleBar.Core.Localization;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class IslandTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static ActivityState State(
        ActivityKind kind, DateTimeOffset updated, string state = "run", string? name = null)
        => new()
        {
            RawKind = ActivityState.KindToString(kind),
            State = state,
            Name = name,
            UpdatedAt = updated,
        };

    // ---------------- State JSON read/write ----------------

    [Fact]
    public void Writes_and_reads_back()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        var path = store.Write("보내는중", new ActivityState
        {
            RawKind = ActivityState.KindTransfer,
            Title = "📥 폰 → PC",
            Name = "휴가 영상.mp4",
            Stage = "upload",
            Total = 81_920_000,
            Done = 4_096_000,
            State = "run",
            Msg = "받는 중",
            Open = @"C:\Users\me\Downloads\휴가 영상.mp4",
            T0 = Now.ToUnixTimeSeconds(),
        });

        Assert.True(File.Exists(path));
        Assert.Equal("보내는중.json", Path.GetFileName(path));

        var live = store.Read(DateTimeOffset.UtcNow);
        var one = Assert.Single(live);
        Assert.Equal("📥 폰 → PC", one.Title);
        Assert.Equal("휴가 영상.mp4", one.Name);
        Assert.Equal(81_920_000, one.Total);
        Assert.Equal(4_096_000, one.Done);
        Assert.Equal(ActivityKind.Transfer, one.Kind);
        Assert.Equal(ActivityRunState.Running, one.RunState);
        Assert.Equal(TransferDirection.Incoming, one.Direction);
        Assert.Equal(0.05, one.Fraction!.Value, 3);
        Assert.Equal(path, one.SourcePath);
    }

    [Fact]
    public void Korean_is_written_as_is_and_emoji_round_trips()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        var path = store.Write("t", new ActivityState { Title = "📤 PC → 폰", Name = "보고서.html" });
        var text = File.ReadAllText(path);

        // BMP characters (Hangul, arrows) are written as is → a person can open and read the file
        Assert.Contains("PC → 폰", text, StringComparison.Ordinal);
        Assert.Contains("보고서.html", text, StringComparison.Ordinal);

        // Emoji (outside the BMP) are escaped as \uD83D\uDCE4. That's valid JSON and Python's json.load reads it fine.
        var read = ActivityStore.ReadFile(path)!;
        Assert.Equal("📤 PC → 폰", read.Title);
        Assert.Equal("보고서.html", read.Name);
    }

    [Fact]
    public void Ignores_stale_files()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        var fresh = store.Write("새것", new ActivityState { Name = "새것" });
        var stale = store.Write("낡은것", new ActivityState { Name = "낡은것" });

        // Set it back 2 min 1 s → an abandoned transfer
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromSeconds(121));

        var live = store.Read(DateTimeOffset.UtcNow);
        Assert.Equal("새것", Assert.Single(live).Name);
        Assert.True(File.Exists(stale));        // only ignored, not deleted
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Just_under_2_minutes_old_is_still_alive()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        var path = store.Write("경계", new ActivityState { Name = "경계" });
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(path, (now - TimeSpan.FromSeconds(119)).UtcDateTime);

        Assert.Single(store.Read(now));
        Assert.Equal(TimeSpan.FromMinutes(2), ActivityStore.StaleAfter);
    }

    [Fact]
    public void Most_recently_changed_comes_first()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        var old = store.Write("가", new ActivityState { Name = "가" });
        var mid = store.Write("나", new ActivityState { Name = "나" });
        var recent = store.Write("다", new ActivityState { Name = "다" });

        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(old, (now - TimeSpan.FromSeconds(90)).UtcDateTime);
        File.SetLastWriteTimeUtc(mid, (now - TimeSpan.FromSeconds(45)).UtcDateTime);
        File.SetLastWriteTimeUtc(recent, (now - TimeSpan.FromSeconds(1)).UtcDateTime);

        Assert.Equal(["다", "나", "가"], store.Read(now).Select(s => s.Name));
    }

    [Fact]
    public void Silently_skips_broken_files()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        store.Write("좋은것", new ActivityState { Name = "좋은것" });
        File.WriteAllText(dir.File("깨진것.json"), "{ 반쯤 쓰다 죽은 파일");

        Assert.Equal("좋은것", Assert.Single(store.Read(DateTimeOffset.UtcNow)).Name);
    }

    [Fact]
    public void Missing_folder_does_not_crash()
        => Assert.Empty(new ActivityStore("/없는/폴더/여기").Read(DateTimeOffset.UtcNow));

    [Fact]
    public void Writing_same_id_overwrites()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        store.Write("한개", new ActivityState { Done = 10, Total = 100, State = "run" });
        store.Write("한개", new ActivityState { Done = 90, Total = 100, State = "run" });

        var one = Assert.Single(store.Read(DateTimeOffset.UtcNow));
        Assert.Equal(90, one.Done);
        Assert.Single(Directory.GetFiles(dir.Path, "*.json"));
    }

    [Fact]
    public void No_temp_files_remain_after_writing()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        for (var i = 0; i < 5; i++)
        {
            store.Write("한개", new ActivityState { Done = i });
        }

        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp-*"));
    }

    [Fact]
    public void Remove()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        store.Write("지울것", new ActivityState());
        Assert.True(store.Remove("지울것"));
        Assert.False(store.Remove("지울것"));
        Assert.Empty(store.Read(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Characters_invalid_in_file_names_are_replaced()
    {
        var name = ActivityStore.FileNameFor("a/b:c");
        Assert.DoesNotContain('/', name);
        Assert.EndsWith(".json", name, StringComparison.Ordinal);
        Assert.Equal("x.json", ActivityStore.FileNameFor("x.json"));   // not appended twice
    }

    [Fact]
    public void Watches_two_folders_together()
    {
        using var newDir = new TempDir();
        using var oldDir = new TempDir();
        // new-convention folder + Python version's tgprog folder
        var store = new ActivityStore([new ActivitySource(newDir.Path), new ActivitySource(oldDir.Path, "tgprog_*.json")]);

        store.Write("새규칙", new ActivityState { Name = "새규칙" });
        File.WriteAllText(Path.Combine(oldDir.Path, "tgprog_1234.json"),
            """{"title":"📥 폰 → PC","name":"예전 전송","total":100,"done":50,"state":"run"}""");
        File.WriteAllText(Path.Combine(oldDir.Path, "관계없는.json"), """{"name":"모양이 안 맞음"}""");

        var names = store.Read(DateTimeOffset.UtcNow).Select(s => s.Name).ToHashSet();
        Assert.Equal(["새규칙", "예전 전송"], names.Order());   // files that don't match the pattern aren't read
    }

    // ---------------- Legacy (tgprog) file compatibility ----------------

    [Fact]
    public void Legacy_file_without_kind_is_treated_as_transfer()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("tgprog_1.json"),
            """{"title":"📤 PC → 폰","name":"a.zip","stage":"upload","total":100,"done":25,"state":"run","t0":1759000000}""");

        var one = Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));
        Assert.Equal(ActivityKind.Transfer, one.Kind);
        Assert.Equal(TransferDirection.Outgoing, one.Direction);
        Assert.Equal(0.25, one.Fraction!.Value, 3);
    }

    [Fact]
    public void No_kind_and_state_error_is_treated_as_error()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("tgprog_1.json"), """{"name":"a.zip","state":"error"}""");

        var one = Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));
        Assert.Equal(ActivityKind.Error, one.Kind);
        Assert.Equal(ActivityRunState.Error, one.RunState);
    }

    [Theory]
    [InlineData("📥 폰 → PC", TransferDirection.Incoming)]
    [InlineData("📥 폰 → PC  ", TransferDirection.Incoming)]   // trailing whitespace is trimmed first
    [InlineData("📤 PC → 폰", TransferDirection.Outgoing)]
    [InlineData(null, TransferDirection.Outgoing)]
    [InlineData("", TransferDirection.Outgoing)]
    public void Direction_is_decided_by_whether_title_ends_with_PC(string? title, TransferDirection expected)
        => Assert.Equal(expected, new ActivityState { Title = title }.Direction);

    [Theory]
    [InlineData(null, null, false)]                 // total unknown → fraction unknown
    [InlineData(0L, 0L, false)]
    [InlineData(100L, null, false)]                 // done is null = server side processing
    [InlineData(100L, 50L, true)]
    public void Whether_fraction_is_known(long? total, long? done, bool known)
    {
        var state = new ActivityState { Total = total, Done = done, State = "run" };
        Assert.Equal(known, state.Fraction is not null);
        Assert.Equal(done is null, state.IsIndeterminate);
    }

    [Fact]
    public void Fraction_is_clamped_between_0_and_1()
    {
        Assert.Equal(1.0, new ActivityState { Total = 100, Done = 150 }.Fraction);
        Assert.Equal(0.0, new ActivityState { Total = 100, Done = -5 }.Fraction);
    }

    [Fact]
    public void Finish_stage_means_finishing()
    {
        Assert.True(new ActivityState { Stage = "finish" }.IsFinishing);
        Assert.False(new ActivityState { Stage = "upload" }.IsFinishing);
        Assert.Equal("마무리 중", LanguageCatalog.For("ko").Finishing);
    }

    // ---------------- Priority ----------------

    [Fact]
    public void Priority_value_order_is_as_specified()
        => Assert.Equal(
            [
                ActivityKind.AgentPermission, ActivityKind.Error, ActivityKind.Transfer, ActivityKind.Notice,
                ActivityKind.Timer, ActivityKind.Music, ActivityKind.AgentDone, ActivityKind.AgentWorking,
            ],
            Enum.GetValues<ActivityKind>().OrderBy(k => (int)k));

    [Fact]
    public void Nothing_going_on_means_empty_island()
    {
        var snapshot = IslandSelector.Select([]);
        Assert.False(snapshot.IsActive);
        Assert.False(snapshot.HasMore);
        Assert.Null(snapshot.Primary);
        Assert.Equal(0, snapshot.ExtraCount);
    }

    [Fact]
    public void Single_activity_is_shown_without_dot()
    {
        var snapshot = IslandSelector.Select([State(ActivityKind.Music, Now)]);
        Assert.True(snapshot.IsActive);
        Assert.False(snapshot.HasMore);
        Assert.Equal(ActivityKind.Music, snapshot.Primary!.Kind);
    }

    [Fact]
    public void Permission_request_beats_everything()
    {
        var snapshot = IslandSelector.Select([
            State(ActivityKind.Music, Now),
            State(ActivityKind.Timer, Now),
            State(ActivityKind.Transfer, Now),
            State(ActivityKind.AgentDone, Now),
            State(ActivityKind.Error, Now),
            State(ActivityKind.AgentPermission, Now.AddMinutes(-1)),   // wins even though it's the oldest
        ]);

        Assert.Equal(ActivityKind.AgentPermission, snapshot.Primary!.Kind);
        Assert.Equal(5, snapshot.ExtraCount);
        Assert.True(snapshot.HasMore);
    }

    [Theory]
    [InlineData(ActivityKind.Error, ActivityKind.Transfer, ActivityKind.Error)]
    [InlineData(ActivityKind.Transfer, ActivityKind.AgentDone, ActivityKind.Transfer)]
    [InlineData(ActivityKind.Timer, ActivityKind.AgentDone, ActivityKind.Timer)]
    [InlineData(ActivityKind.Timer, ActivityKind.Music, ActivityKind.Timer)]
    [InlineData(ActivityKind.Music, ActivityKind.AgentDone, ActivityKind.Music)]        // music isn't hidden by a finished/running task (10-01)
    [InlineData(ActivityKind.Music, ActivityKind.AgentWorking, ActivityKind.Music)]
    [InlineData(ActivityKind.AgentDone, ActivityKind.AgentWorking, ActivityKind.AgentDone)]
    [InlineData(ActivityKind.AgentPermission, ActivityKind.Error, ActivityKind.AgentPermission)]
    public void Pairwise_priority(ActivityKind a, ActivityKind b, ActivityKind winner)
    {
        // Reversing the insertion order must give the same result
        Assert.Equal(winner, IslandSelector.Select([State(a, Now), State(b, Now)]).Primary!.Kind);
        Assert.Equal(winner, IslandSelector.Select([State(b, Now), State(a, Now)]).Primary!.Kind);
    }

    [Fact]
    public void Same_kind_picks_most_recent()
    {
        var snapshot = IslandSelector.Select([
            State(ActivityKind.Transfer, Now.AddMinutes(-1), name: "예전 것"),
            State(ActivityKind.Transfer, Now, name: "최근 것"),
            State(ActivityKind.Transfer, Now.AddSeconds(-30), name: "중간 것"),
        ]);

        Assert.Equal("최근 것", snapshot.Primary!.Name);
        Assert.Equal(2, snapshot.ExtraCount);
    }

    [Fact]
    public void Read_from_folder_and_pick_directly()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        store.Write("음악", new ActivityState { RawKind = ActivityState.KindMusic, Name = "노래" });
        store.Write("권한", new ActivityState { RawKind = ActivityState.KindAgentPermission, Name = "파일을 지울까요?" });
        store.Write("타이머", new ActivityState { RawKind = ActivityState.KindTimer, Name = "뽀모도로" });

        var snapshot = IslandSelector.Select(store.Read(DateTimeOffset.UtcNow));
        Assert.Equal("파일을 지울까요?", snapshot.Primary!.Name);
        Assert.Equal(2, snapshot.ExtraCount);
    }

    // ---------------- Name truncation ----------------

    /// <summary>Fake measurer counting one character = 1, Hangul/emoji = 2.</summary>
    private sealed class FakeMeasure : ITextMeasure
    {
        public double Measure(string text)
            => text.EnumerateRunes().Sum(r => r.Value > 0x1100 ? 2 : 1);
    }

    [Fact]
    public void Title_alone_overflowing_truncates_title_and_drops_artist()
        => Assert.Equal(("MIDNIGHT…", ""), TextFit.FitParts("MIDNIGHT (long) · ", "Artist", 10, new FakeMeasure()));

    [Fact]
    public void Title_fits_so_only_artist_is_truncated()
        => Assert.Equal(("Deja Vu · ", "RE…"), TextFit.FitParts("Deja Vu · ", "RESCENE", 14, new FakeMeasure()));

    [Fact]
    public void Fits_entirely_left_as_is()
        => Assert.Equal("50%  a.zip", TextFit.Fit("50%  ", "a.zip", 100, new FakeMeasure()));

    [Fact]
    public void Overflow_truncates_end_with_ellipsis()
    {
        var fit = TextFit.Fit("50%  ", "아주아주긴파일이름.mp4", 15, new FakeMeasure());
        Assert.StartsWith("50%  ", fit, StringComparison.Ordinal);
        Assert.EndsWith(TextFit.Ellipsis, fit, StringComparison.Ordinal);
        Assert.True(new FakeMeasure().Measure(fit) <= 15, $"아직 넘친다: {fit} ({new FakeMeasure().Measure(fit)})");
    }

    [Fact]
    public void Longer_prefix_truncates_name_more()
    {
        // The prefix (progress, time) length differs by language and progress → measuring together lets the name shrink just right
        var measure = new FakeMeasure();
        const string name = "긴파일이름입니다.mp4";
        const double max = 26;   // width where both prefixes fit

        var shortPrefix = "5%  ";
        var longPrefix = "100% · 1분 30초  ";
        var withShort = TextFit.Fit(shortPrefix, name, max, measure);
        var withLong = TextFit.Fit(longPrefix, name, max, measure);

        // Compare only the remaining name part
        var nameAfterShort = withShort[shortPrefix.Length..];
        var nameAfterLong = withLong[longPrefix.Length..];
        Assert.True(
            nameAfterLong.Length < nameAfterShort.Length,
            $"'{nameAfterLong}' 이 '{nameAfterShort}' 보다 짧아야 한다");

        // As long as the prefix fits, the result fits too
        Assert.True(measure.Measure(withShort) <= max, withShort);
        Assert.True(measure.Measure(withLong) <= max, withLong);
    }

    [Fact]
    public void Prefix_alone_overflowing_still_keeps_one_name_character()
    {
        var fit = TextFit.Fit("아주아주긴앞글자들", "이름", 3, new FakeMeasure());
        Assert.Equal("아주아주긴앞글자들이" + TextFit.Ellipsis, fit);
    }

    [Fact]
    public void Does_not_split_emoji_in_half()
    {
        // Emoji made of surrogate pairs — cutting one in half produces a broken character
        var fit = TextFit.Fit("", "😀😀😀😀😀😀", 5, new FakeMeasure());
        Assert.DoesNotContain('\uFFFD', fit);
        foreach (var c in fit.Where(char.IsSurrogate))
        {
            Assert.True(char.IsHighSurrogate(c) || char.IsLowSurrogate(c));
        }

        Assert.EndsWith(TextFit.Ellipsis, fit, StringComparison.Ordinal);
        Assert.Equal(0, fit.Count(c => char.IsHighSurrogate(c)) - fit.Count(c => char.IsLowSurrogate(c)));
    }

    [Fact]
    public void Empty_name_is_fine()
    {
        Assert.Equal("50%  ", TextFit.Fit("50%  ", null, 100, new FakeMeasure()));
        Assert.Equal("50%  ", TextFit.Fit("50%  ", "", 100, new FakeMeasure()));
    }

    // ---------------- Timer input ----------------

    [Theory]
    [InlineData("25분", 25 * 60)]
    [InlineData("25 분", 25 * 60)]
    [InlineData("25m", 25 * 60)]
    [InlineData("25M", 25 * 60)]
    [InlineData("25min", 25 * 60)]
    [InlineData("25 mins", 25 * 60)]
    [InlineData("25 minutes", 25 * 60)]
    [InlineData("1시간 30분", 90 * 60)]
    [InlineData("1시간30분", 90 * 60)]
    [InlineData("1h30m", 90 * 60)]
    [InlineData("1h 30m", 90 * 60)]
    [InlineData("1시간", 60 * 60)]
    [InlineData("2시", 2 * 60 * 60)]
    [InlineData("90s", 90)]
    [InlineData("90초", 90)]
    [InlineData("90 sec", 90)]
    [InlineData("45seconds", 45)]
    [InlineData("1시간 30분 15초", 90 * 60 + 15)]
    [InlineData("0.5시간", 30 * 60)]
    [InlineData("0,5분", 30)]
    [InlineData("  25분  ", 25 * 60)]
    [InlineData("24h", 24 * 60 * 60)]
    public void Parses_as_duration(string input, int expectedSeconds)
    {
        Assert.True(TimerParser.TryParse(input, out var duration), $"'{input}'을 못 읽었다");
        Assert.Equal(expectedSeconds, (int)duration.TotalSeconds);
    }

    [Theory]
    [InlineData("25")]                          // no unit → could be a question
    [InlineData("25분 동안 뭘 할까?")]           // words follow → it's a question
    [InlineData("타이머 25분")]                  // words precede
    [InlineData("왜 25m 밖에 안 돼?")]
    [InlineData("0분")]
    [InlineData("0")]
    [InlineData("-5분")]                         // the leading '-' is left over → a question
    [InlineData("25시간")]                       // exceeds 24 hours
    [InlineData("10분 20분")]                    // same unit twice → a typo
    [InlineData("분")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("작업표시줄 검색창 만들어 줘")]
    [InlineData("25시간 30분")]
    [InlineData("99999999999m")]                 // used to throw OverflowException and crash the bar (review 10-03)
    [InlineData("300000000h")]
    [InlineData("1e300s")]
    public void Does_not_parse_as_duration(string? input)
        => Assert.False(TimerParser.TryParse(input, out _), $"'{input}'을 시간으로 읽어 버렸다");

    [Fact]
    public void Converts_pomodoro_25_minutes_to_state()
    {
        Assert.True(TimerParser.TryParse("25분", out var duration));
        var state = TimerParser.ToActivity(duration, Now, "뽀모도로");

        Assert.Equal(ActivityKind.Timer, state.Kind);
        Assert.Equal("뽀모도로", state.Name);
        Assert.Equal("run", state.State);
        Assert.Equal(Now.ToUnixTimeSeconds(), state.T0);
        Assert.Equal(Now.AddMinutes(25).ToUnixTimeSeconds(), state.Due);
        Assert.Equal(1500, state.Total);
        Assert.Equal(0, state.Done);
    }

    [Fact]
    public void Maximum_is_24_hours()
    {
        Assert.Equal(TimeSpan.FromHours(24), TimerParser.MaxDuration);
        Assert.True(TimerParser.TryParse("1440분", out var max));
        Assert.Equal(TimerParser.MaxDuration, max);
        Assert.False(TimerParser.TryParse("1441분", out _));
    }
}
