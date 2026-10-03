using IsleBar.Core.Configuration;
using IsleBar.Core.History;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class HistoryTests
{
    // ---------------- List ----------------

    [Fact]
    public void New_question_goes_last()
        => Assert.Equal(["first", "second"], QuestionHistory.Add(["first"], "second"));

    [Fact]
    public void Repeating_a_question_moves_it_to_the_end()
        => Assert.Equal(
            ["second", "third", "first"],
            QuestionHistory.Add(["first", "second", "third"], "first"));

    [Fact]
    public void Duplicates_do_not_accumulate()
    {
        var history = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            history = QuestionHistory.Add(history, "same question");
        }

        Assert.Equal(["same question"], history);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_question_is_not_added(string? question)
        => Assert.Equal(["first"], QuestionHistory.Add(["first"], question));

    [Fact]
    public void Remembers_at_most_fifty()
    {
        var history = new List<string>();
        for (var i = 0; i < 60; i++)
        {
            history = QuestionHistory.Add(history, $"question {i}");
        }

        Assert.Equal(50, history.Count);
        Assert.Equal("question 10", history[0]);      // the oldest 10 were dropped
        Assert.Equal("question 59", history[^1]);
        Assert.Equal(50, QuestionHistory.Max);
    }

    [Fact]
    public void Does_not_modify_the_original_list()
    {
        var original = new List<string> { "first" };
        QuestionHistory.Add(original, "second");
        Assert.Equal(["first"], original);
    }

    [Fact]
    public void Case_and_whitespace_make_different_questions()
        => Assert.Equal(
            ["hello", "Hello", "hello "],
            QuestionHistory.Add(QuestionHistory.Add(["hello"], "Hello"), "hello "));

    // ---------------- Browsing with Up/Down ----------------

    private static readonly string[] Sample = ["oldest", "middle", "newest"];

    [Fact]
    public void Up_starts_from_most_recent_question()
    {
        var nav = new HistoryNavigator(Sample);
        Assert.Equal("newest", nav.Up(""));
        Assert.Equal("middle", nav.Up("newest"));
        Assert.Equal("oldest", nav.Up("middle"));
    }

    [Fact]
    public void Pressing_up_at_the_top_stays_put()
    {
        var nav = new HistoryNavigator(Sample);
        nav.Up(""); nav.Up(""); nav.Up("");
        Assert.Equal("oldest", nav.Up(""));
        Assert.Equal("oldest", nav.Up(""));
        Assert.Equal(0, nav.Position);
    }

    [Fact]
    public void Draft_is_restored_when_returning_to_the_bottom()
    {
        var nav = new HistoryNavigator(Sample);

        Assert.Equal("newest", nav.Up("half-typed question"));   // pressing Up stashes the draft being typed
        Assert.Equal("middle", nav.Up("newest"));
        Assert.Equal("newest", nav.Down("middle"));
        Assert.Equal("half-typed question", nav.Down("newest"));  // bottom = the draft
        Assert.Equal("half-typed question", nav.Draft);
    }

    [Fact]
    public void Pressing_down_at_the_bottom_keeps_the_draft()
    {
        var nav = new HistoryNavigator(Sample);
        nav.Up("draft");
        nav.Down("newest");
        Assert.Equal("draft", nav.Down("draft"));
        Assert.Equal(Sample.Length, nav.Position);
    }

    [Fact]
    public void Down_first_gives_the_draft_immediately()
    {
        // Down without ever pressing Up → position lands at the bottom and the draft stays as is
        var nav = new HistoryNavigator(Sample);
        Assert.Equal("draft", nav.Down("draft"));
    }

    [Fact]
    public void Typing_forgets_the_position()
    {
        var nav = new HistoryNavigator(Sample);
        nav.Up("draft");
        nav.Up("newest");
        Assert.True(nav.IsBrowsing);

        nav.Reset();                                  // typed a character in the input box
        Assert.False(nav.IsBrowsing);
        Assert.Null(nav.Position);
        Assert.Equal("", nav.Draft);

        Assert.Equal("newest", nav.Up("freshly typed text"));   // starts again from the bottom
        Assert.Equal("freshly typed text", nav.Down("newest"));
    }

    [Fact]
    public void Nothing_happens_without_history()
    {
        var nav = new HistoryNavigator([]);
        Assert.Null(nav.Up("draft"));
        Assert.Null(nav.Down("draft"));
        Assert.Null(nav.Position);
        Assert.False(nav.IsBrowsing);
    }

    [Fact]
    public void Works_without_a_draft()
    {
        var nav = new HistoryNavigator(Sample);
        Assert.Equal("newest", nav.Up(null));
        Assert.Equal("", nav.Down("newest"));
    }

    [Fact]
    public void Launching_records_history_and_saves_to_settings()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("islebar.json"));

        Assert.True(store.Update((IsleBarSettings s) =>
        {
            s.History = QuestionHistory.Add(s.History, "build a taskbar search box");
            return true;
        }));
        Assert.True(store.Update((IsleBarSettings s) =>
        {
            s.History = QuestionHistory.Add(s.History, "write tests too");
            return true;
        }));

        Assert.Equal(["build a taskbar search box", "write tests too"], store.Load().History);

        // The saved history can be browsed right away
        var nav = new HistoryNavigator(store.Load().History);
        Assert.Equal("write tests too", nav.Up(""));
    }
}
