using IsleBar.Core.Configuration;
using IsleBar.Core.Models;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class ModelAliasTests
{
    /// <summary>
    /// A hand-written sample in the shape of the official docs page (the page itself is not copied into the repo — 10-01),
    /// so tests run without a network. Details in <c>Fixtures/model-aliases-sample.README.md</c>.
    /// </summary>
    private static string RealDocument()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "model-aliases-sample.md"));

    [Fact]
    public void Fixed_copy_is_copied_to_output()
    {
        var doc = RealDocument();
        Assert.Contains(ModelAliasParser.SectionHeading, doc, StringComparison.Ordinal);
        Assert.Contains("**`ignored-after`**", doc, StringComparison.Ordinal);   // the section's end is exercised too
    }

    [Fact]
    public void Extracts_only_family_aliases_from_sample_document()
    {
        var found = ModelAliasParser.Parse(RealDocument());

        // Bold entries in the doc table: default best fable sonnet opus haiku sonnet[1m] opus[1m] opusplan
        // → excluding special values (default·best·opusplan) and bracketed ones (context variants) leaves these four
        Assert.Equal(["fable", "sonnet", "opus", "haiku"], found);
    }

    [Fact]
    public void Real_document_mixes_special_values_and_bracket_variants()
    {
        // Confirms the expectation above isn't "right by coincidence" — the exclusion rules are actually doing work
        var doc = RealDocument();
        Assert.Contains("**`opusplan`**", doc, StringComparison.Ordinal);
        Assert.Contains("**`opus[1m]`**", doc, StringComparison.Ordinal);
        Assert.Contains("**`best`**", doc, StringComparison.Ordinal);
        Assert.Contains("**`default`**", doc, StringComparison.Ordinal);

        var found = ModelAliasParser.Parse(doc)!;
        Assert.DoesNotContain("opusplan", found);
        Assert.DoesNotContain("opus[1m]", found);
        Assert.DoesNotContain("sonnet[1m]", found);
        Assert.DoesNotContain("best", found);
        Assert.DoesNotContain("default", found);
    }

    [Fact]
    public void Merging_real_document_keeps_current_list()
    {
        // Same alias set as the default list, so the order must not change (buttons must not move before the user's eyes)
        var found = ModelAliasParser.Parse(RealDocument())!;
        Assert.Equal(
            LaunchOptionDefs.DefaultModels,
            ModelAliasParser.Merge(LaunchOptionDefs.DefaultModels, found));
    }

    [Fact]
    public void Stops_at_the_next_table()
    {
        const string doc = """
        ### Model aliases

        | Model alias | Behavior |
        | **`opus`** | ... |
        | **`sonnet`** | ... |
        | **`haiku`** | ... |

        ### Another section

        | **`절대로`** | 여기 것은 읽지 않는다 |
        | **`가져오면`** | 안 된다 |
        """;

        Assert.Equal(["opus", "sonnet", "haiku"], ModelAliasParser.Parse(doc));
    }

    [Fact]
    public void Duplicate_alias_is_listed_once()
    {
        const string doc = "### Model aliases\n**`opus`** **`sonnet`** **`opus`** **`haiku`**";
        Assert.Equal(["opus", "sonnet", "haiku"], ModelAliasParser.Parse(doc));
    }

    [Theory]
    [InlineData("표가 아예 없는 문서")]
    [InlineData("### Model aliases\n(표가 사라졌다)")]                        // 0 aliases
    [InlineData("### Model aliases\n**`opus`**")]                              // 1 alias
    [InlineData("### Model aliases\n**`opus`** **`sonnet`**")]                  // 2 → fewer than 3
    [InlineData("### Model aliases\n**`default`** **`best`** **`opusplan`**")]  // special values only
    [InlineData("### Model aliases\n**`opus[1m]`** **`sonnet[1m]`** **`opus[200k]`**")]  // variants only
    [InlineData("")]
    [InlineData(null)]
    public void Untrustworthy_document_gives_null(string? doc)
        => Assert.Null(ModelAliasParser.Parse(doc));

    [Fact]
    public void Exactly_three_is_trusted()
        => Assert.Equal(
            ["opus", "sonnet", "haiku"],
            ModelAliasParser.Parse("### Model aliases\n**`opus`** **`sonnet`** **`haiku`**"));

    [Fact]
    public void Aliases_not_in_bold_are_not_read()
        => Assert.Null(ModelAliasParser.Parse("### Model aliases\n`opus` `sonnet` `haiku` `fable`"));

    // ---------------- Merge rules ----------------

    [Fact]
    public void Merge_keeps_existing_order()
        => Assert.Equal(
            ["opus", "sonnet", "haiku", "fable"],
            ModelAliasParser.Merge(
                ["opus", "sonnet", "haiku", "fable"],
                ["fable", "haiku", "sonnet", "opus"]));      // buttons don't move even if the doc order differs

    [Fact]
    public void Merge_appends_new_ones()
        => Assert.Equal(
            ["opus", "sonnet", "haiku", "fable", "lyric", "verse"],
            ModelAliasParser.Merge(
                ["opus", "sonnet", "haiku", "fable"],
                ["lyric", "opus", "verse", "sonnet", "haiku", "fable"]));

    [Fact]
    public void Merge_drops_removed_ones()
        => Assert.Equal(
            ["opus", "haiku"],
            ModelAliasParser.Merge(["opus", "sonnet", "haiku", "fable"], ["opus", "haiku"]));

    [Fact]
    public void Merge_adds_and_removes_at_once()
        => Assert.Equal(
            ["sonnet", "fable", "lyric"],
            ModelAliasParser.Merge(
                ["opus", "sonnet", "haiku", "fable"],
                ["lyric", "fable", "sonnet"]));

    [Fact]
    public void Merge_removes_duplicates()
        => Assert.Equal(
            ["opus", "sonnet"],
            ModelAliasParser.Merge(["opus", "opus", "sonnet"], ["sonnet", "opus", "opus"]));

    [Fact]
    public void Merge_uses_document_order_when_current_list_is_empty()
        => Assert.Equal(
            ["fable", "sonnet", "opus"],
            ModelAliasParser.Merge([], ["fable", "sonnet", "opus"]));

    // ---------------- Updater ----------------

    private sealed class FakeSource(string? markdown) : IModelAliasSource
    {
        public int Calls { get; private set; }

        public Task<string?> FetchAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(markdown);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Checks_on_first_launch()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("islebar.json"));
        var source = new FakeSource(RealDocument());

        var result = await new ModelAliasUpdater(store, source).RunOnceAsync(Now);

        Assert.True(result.Checked);
        Assert.Equal(1, source.Calls);
        Assert.False(result.Changed);                       // same set as the default list
        Assert.Equal(Now.ToUnixTimeSeconds(), store.Load().ModelsChecked);
    }

    [Fact]
    public async Task Does_not_check_before_six_hours_pass()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("islebar.json"));
        store.Update((IsleBarSettings s) =>
        {
            s.ModelsChecked = Now.ToUnixTimeSeconds();
            return true;
        });
        var source = new FakeSource(RealDocument());

        var result = await new ModelAliasUpdater(store, source)
            .RunOnceAsync(Now.AddHours(5).AddMinutes(59));

        Assert.False(result.Checked);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Checks_after_six_hours_pass()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("islebar.json"));
        store.Update((IsleBarSettings s) =>
        {
            s.ModelsChecked = Now.ToUnixTimeSeconds();
            return true;
        });
        var source = new FakeSource(RealDocument());

        var result = await new ModelAliasUpdater(store, source).RunOnceAsync(Now.AddHours(6));

        Assert.True(result.Checked);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task New_family_is_reported_as_Changed()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("islebar.json"));
        var source = new FakeSource("### Model aliases\n**`opus`** **`sonnet`** **`haiku`** **`fable`** **`lyric`**");

        var result = await new ModelAliasUpdater(store, source).RunOnceAsync(Now);

        Assert.True(result.Changed);
        Assert.Equal(["opus", "sonnet", "haiku", "fable", "lyric"], result.Models);
        Assert.Equal(["opus", "sonnet", "haiku", "fable", "lyric"], store.Load().Models);
    }

    [Fact]
    public async Task Fetch_failure_leaves_list_untouched()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("islebar.json"));
        store.Update((IsleBarSettings s) =>
        {
            s.Models = ["opus", "sonnet"];
            return true;
        });

        var result = await new ModelAliasUpdater(store, new FakeSource(null)).RunOnceAsync(Now);

        Assert.True(result.Checked);
        Assert.False(result.Changed);
        Assert.Equal(["opus", "sonnet"], store.Load().Models);          // buttons don't disappear
        Assert.Equal(Now.ToUnixTimeSeconds(), store.Load().ModelsChecked);   // the check time is still recorded
    }

    [Fact]
    public async Task Untrustworthy_changed_document_leaves_list_untouched()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("islebar.json"));
        var result = await new ModelAliasUpdater(store, new FakeSource("### Model aliases\n**`opus`**"))
            .RunOnceAsync(Now);

        Assert.True(result.Checked);
        Assert.False(result.Changed);
        Assert.Equal(LaunchOptionDefs.DefaultModels, store.Load().Models);
    }

    [Fact]
    public async Task Force_ignores_the_interval()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("islebar.json"));
        store.Update((IsleBarSettings s) =>
        {
            s.ModelsChecked = Now.ToUnixTimeSeconds();
            return true;
        });
        var source = new FakeSource(RealDocument());

        Assert.True((await new ModelAliasUpdater(store, source).RunOnceAsync(Now, force: true)).Checked);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public void Is_it_due_for_a_check()
    {
        Assert.True(ModelAliasUpdater.IsDue(0, Now));                                    // never checked
        Assert.False(ModelAliasUpdater.IsDue(Now.ToUnixTimeSeconds(), Now));
        Assert.False(ModelAliasUpdater.IsDue(Now.ToUnixTimeSeconds(), Now.AddHours(5)));
        Assert.True(ModelAliasUpdater.IsDue(Now.ToUnixTimeSeconds(), Now.AddHours(6)));
        Assert.Equal(TimeSpan.FromHours(6), ModelAliasUpdater.Interval);
    }

    [Fact]
    public void Always_sends_User_Agent_header()
    {
        // Without it we get 403 (hit in the Python version)
        Assert.False(string.IsNullOrWhiteSpace(ModelAliasParser.UserAgent));
        Assert.Equal("https://code.claude.com/docs/en/model-config.md", ModelAliasParser.DocumentUrl);
    }
}
