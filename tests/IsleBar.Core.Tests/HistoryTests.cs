using IsleBar.Core.Configuration;
using IsleBar.Core.History;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class HistoryTests
{
    // ---------------- List ----------------

    [Fact]
    public void New_question_goes_last()
        => Assert.Equal(["첫째", "둘째"], QuestionHistory.Add(["첫째"], "둘째"));

    [Fact]
    public void Repeating_a_question_moves_it_to_the_end()
        => Assert.Equal(
            ["둘째", "셋째", "첫째"],
            QuestionHistory.Add(["첫째", "둘째", "셋째"], "첫째"));

    [Fact]
    public void Duplicates_do_not_accumulate()
    {
        var history = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            history = QuestionHistory.Add(history, "같은 질문");
        }

        Assert.Equal(["같은 질문"], history);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_question_is_not_added(string? question)
        => Assert.Equal(["첫째"], QuestionHistory.Add(["첫째"], question));

    [Fact]
    public void Remembers_at_most_fifty()
    {
        var history = new List<string>();
        for (var i = 0; i < 60; i++)
        {
            history = QuestionHistory.Add(history, $"질문 {i}");
        }

        Assert.Equal(50, history.Count);
        Assert.Equal("질문 10", history[0]);      // the oldest 10 were dropped
        Assert.Equal("질문 59", history[^1]);
        Assert.Equal(50, QuestionHistory.Max);
    }

    [Fact]
    public void Does_not_modify_the_original_list()
    {
        var original = new List<string> { "첫째" };
        QuestionHistory.Add(original, "둘째");
        Assert.Equal(["첫째"], original);
    }

    [Fact]
    public void Case_and_whitespace_make_different_questions()
        => Assert.Equal(
            ["hello", "Hello", "hello "],
            QuestionHistory.Add(QuestionHistory.Add(["hello"], "Hello"), "hello "));

    // ---------------- Browsing with Up/Down ----------------

    private static readonly string[] Sample = ["가장 오래된", "중간", "가장 최근"];

    [Fact]
    public void Up_starts_from_most_recent_question()
    {
        var nav = new HistoryNavigator(Sample);
        Assert.Equal("가장 최근", nav.Up(""));
        Assert.Equal("중간", nav.Up("가장 최근"));
        Assert.Equal("가장 오래된", nav.Up("중간"));
    }

    [Fact]
    public void Pressing_up_at_the_top_stays_put()
    {
        var nav = new HistoryNavigator(Sample);
        nav.Up(""); nav.Up(""); nav.Up("");
        Assert.Equal("가장 오래된", nav.Up(""));
        Assert.Equal("가장 오래된", nav.Up(""));
        Assert.Equal(0, nav.Position);
    }

    [Fact]
    public void Draft_is_restored_when_returning_to_the_bottom()
    {
        var nav = new HistoryNavigator(Sample);

        Assert.Equal("가장 최근", nav.Up("쓰다 만 질문"));   // pressing Up stashes the draft being typed
        Assert.Equal("중간", nav.Up("가장 최근"));
        Assert.Equal("가장 최근", nav.Down("중간"));
        Assert.Equal("쓰다 만 질문", nav.Down("가장 최근"));  // bottom = the draft
        Assert.Equal("쓰다 만 질문", nav.Draft);
    }

    [Fact]
    public void Pressing_down_at_the_bottom_keeps_the_draft()
    {
        var nav = new HistoryNavigator(Sample);
        nav.Up("초안");
        nav.Down("가장 최근");
        Assert.Equal("초안", nav.Down("초안"));
        Assert.Equal(Sample.Length, nav.Position);
    }

    [Fact]
    public void Down_first_gives_the_draft_immediately()
    {
        // Down without ever pressing Up → position lands at the bottom and the draft stays as is
        var nav = new HistoryNavigator(Sample);
        Assert.Equal("초안", nav.Down("초안"));
    }

    [Fact]
    public void Typing_forgets_the_position()
    {
        var nav = new HistoryNavigator(Sample);
        nav.Up("초안");
        nav.Up("가장 최근");
        Assert.True(nav.IsBrowsing);

        nav.Reset();                                  // typed a character in the input box
        Assert.False(nav.IsBrowsing);
        Assert.Null(nav.Position);
        Assert.Equal("", nav.Draft);

        Assert.Equal("가장 최근", nav.Up("새로 치던 글"));   // starts again from the bottom
        Assert.Equal("새로 치던 글", nav.Down("가장 최근"));
    }

    [Fact]
    public void Nothing_happens_without_history()
    {
        var nav = new HistoryNavigator([]);
        Assert.Null(nav.Up("초안"));
        Assert.Null(nav.Down("초안"));
        Assert.Null(nav.Position);
        Assert.False(nav.IsBrowsing);
    }

    [Fact]
    public void Works_without_a_draft()
    {
        var nav = new HistoryNavigator(Sample);
        Assert.Equal("가장 최근", nav.Up(null));
        Assert.Equal("", nav.Down("가장 최근"));
    }

    [Fact]
    public void Launching_records_history_and_saves_to_settings()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("islebar.json"));

        Assert.True(store.Update((IsleBarSettings s) =>
        {
            s.History = QuestionHistory.Add(s.History, "작업표시줄 검색창 만들어 줘");
            return true;
        }));
        Assert.True(store.Update((IsleBarSettings s) =>
        {
            s.History = QuestionHistory.Add(s.History, "테스트도 써 줘");
            return true;
        }));

        Assert.Equal(["작업표시줄 검색창 만들어 줘", "테스트도 써 줘"], store.Load().History);

        // The saved history can be browsed right away
        var nav = new HistoryNavigator(store.Load().History);
        Assert.Equal("테스트도 써 줘", nav.Up(""));
    }
}
