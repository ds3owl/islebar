using System.Reflection;
using IsleBar.Core.Configuration;
using IsleBar.Core.Localization;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class LocalizationTests
{
    /// <summary>10 languages = the same list as Slack (owner's decision, 09-29).</summary>
    private static readonly string[] Expected =
        ["en", "zh", "zht", "fr", "de", "it", "ja", "ko", "pt", "es"];

    [Fact]
    public void There_are_ten_languages_with_fixed_order_and_names()
    {
        Assert.Equal(Expected, LanguageCatalog.Languages.Select(l => l.Code));
        Assert.Equal(
            ["English", "简体中文", "繁體中文", "Français", "Deutsch", "Italiano", "日本語", "한국어", "Português", "Español"],
            LanguageCatalog.Languages.Select(l => l.NativeName));
    }

    [Fact]
    public void Every_listed_language_has_strings()
    {
        foreach (var info in LanguageCatalog.Languages)
        {
            Assert.True(LanguageCatalog.IsKnown(info.Code), $"{info.Code} has no strings");
        }

        Assert.Equal(Expected.Length, LanguageCatalog.Codes.Count);
    }

    /// <summary>
    /// Every language must have every key. String properties must be non-empty,
    /// and choice options must have a title and the fixed number of choices.
    /// </summary>
    [Fact]
    public void Every_language_has_every_key()
    {
        var textProps = typeof(LanguageStrings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .ToArray();
        var optionProps = typeof(LanguageStrings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(OptionText))
            .ToArray();

        Assert.Equal(95, textProps.Length);     // notice if the number of string keys drops (includes rc·agent·dismiss·web_engine·tab_order·web_search·timer + 30 system notification keys + advanced_section, 10-01; advanced_section translated in all ten 10-02; + hide_toast_banners 10-01; + replace_timer_ask, needs_answer, agent_done, crash_reports, check_updates, update_available, update_menu, update_failed, connect_agents, connect_agents_card 10-01; + all_notifications, usage_limit, resets_at, usage_high, agent_stopped, stop_login, stop_billing, stop_busy, stop_error, mute_chime_fullscreen 10-03)
        Assert.Equal(4, optionProps.Length);    // session · model · effort · perm

        foreach (var code in Expected)
        {
            var strings = LanguageCatalog.For(code);
            foreach (var prop in textProps)
            {
                var value = (string?)prop.GetValue(strings);
                Assert.False(string.IsNullOrWhiteSpace(value), $"{code}.{prop.Name} is empty");
            }

            foreach (var prop in optionProps)
            {
                var option = (OptionText?)prop.GetValue(strings);
                Assert.NotNull(option);
                Assert.False(string.IsNullOrWhiteSpace(option!.Title), $"{code}.{prop.Name}.Title is empty");
                Assert.All(option.Choices, c => Assert.False(string.IsNullOrWhiteSpace(c), $"{code}.{prop.Name} has an empty choice"));
            }

            Assert.Equal(LaunchOptionDefs.SessionChoices.Count, strings.Session.Choices.Count);
            Assert.Equal(LaunchOptionDefs.EffortChoices.Count, strings.Effort.Choices.Count);
            Assert.Equal(LaunchOptionDefs.PermChoices.Count, strings.Perm.Choices.Count);
            Assert.Equal(5, strings.Model.Choices.Count);   // default + 4 aliases from reference
        }
    }

    [Fact]
    public void Every_language_duration_string_has_placeholders()
    {
        foreach (var code in Expected)
        {
            var strings = LanguageCatalog.For(code);
            Assert.Contains("{m}", strings.MinSec);
            Assert.Contains("{s:02d}", strings.MinSec);
            Assert.Contains("{s}", strings.Sec);
            Assert.Contains("{}", strings.LaunchFail);
        }
    }

    [Fact]
    public void Unknown_language_code_falls_back_to_english()
    {
        Assert.Same(LanguageCatalog.For("en"), LanguageCatalog.For("sv"));
        Assert.Same(LanguageCatalog.For("en"), LanguageCatalog.For(null));
        Assert.Same(LanguageCatalog.For("en"), LanguageCatalog.For("auto"));
    }

    [Fact]
    public void Finishing_string_exists_in_all_ten_languages()
    {
        // A key singled out in HANDOFF — easy to miss, so checked separately
        foreach (var code in Expected)
        {
            Assert.False(string.IsNullOrWhiteSpace(LanguageCatalog.For(code).Finishing));
        }

        Assert.Equal("마무리 중", LanguageCatalog.For("ko").Finishing);
        Assert.Equal("Finishing", LanguageCatalog.For("en").Finishing);
        Assert.Equal("仕上げ中", LanguageCatalog.For("ja").Finishing);
        Assert.Equal("收尾中", LanguageCatalog.For("zh").Finishing);
        Assert.Equal("收尾中", LanguageCatalog.For("zht").Finishing);
    }

    [Theory]
    [InlineData("ko", 0, "0초")]
    [InlineData("ko", 59, "59초")]
    [InlineData("ko", 60, "1분 00초")]
    [InlineData("ko", 125, "2분 05초")]
    [InlineData("ko", 3661, "61분 01초")]
    [InlineData("en", 5, "5s")]
    [InlineData("en", 65, "1m 05s")]
    [InlineData("ja", 90, "1分30秒")]
    [InlineData("fr", 90, "1 min 30 s")]
    [InlineData("de", 605, "10 Min. 05 s")]
    public void Duration_string_format(string code, int seconds, string expected)
        => Assert.Equal(expected, LanguageCatalog.For(code).FormatDuration(seconds));

    [Theory]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void Invalid_duration_becomes_zero_seconds(double seconds)
        => Assert.Equal("0초", LanguageCatalog.For("ko").FormatDuration(seconds));

    [Fact]
    public void Launch_failure_string_includes_the_error()
    {
        Assert.Equal("실행 실패: 파일을 찾을 수 없음", LanguageCatalog.For("ko").FormatLaunchFail("파일을 찾을 수 없음"));
        Assert.Equal("Launch failed: boom", LanguageCatalog.For("en").FormatLaunchFail("boom"));
    }

    [Fact]
    public void Model_choices_follow_the_alias_list()
    {
        var ko = LanguageCatalog.For("ko").ModelFor(["opus", "sonnet", "haiku", "fable"]);
        Assert.Equal("모델", ko.Title);
        Assert.Equal(["기본", "Opus", "Sonnet", "Haiku", "Fable"], ko.Choices);

        // When a new family appears, the buttons grow on their own
        var more = LanguageCatalog.For("en").ModelFor(["opus", "sonnet", "haiku", "fable", "lyric"]);
        Assert.Equal(["Default", "Opus", "Sonnet", "Haiku", "Fable", "Lyric"], more.Choices);

        // When one disappears, they shrink
        var fewer = LanguageCatalog.For("en").ModelFor(["opus"]);
        Assert.Equal(["Default", "Opus"], fewer.Choices);
    }

    [Fact]
    public void Option_title_lookup()
    {
        var ko = LanguageCatalog.For("ko");
        Assert.Equal("대화", ko.TitleFor(LaunchOptionDefs.Session));
        Assert.Equal("모델", ko.TitleFor(LaunchOptionDefs.Model));
        Assert.Equal("생각 강도", ko.TitleFor(LaunchOptionDefs.Effort));
        Assert.Equal("권한", ko.TitleFor(LaunchOptionDefs.Perm));
        Assert.Equal("원격 조종", ko.TitleFor(LaunchOptionDefs.Rc));   // rc has no choices
        Assert.Null(ko.OptionFor(LaunchOptionDefs.Rc));
    }

    [Fact]
    public void Strings_have_no_extra_explanation()
    {
        // "No explanatory text (padding)" — choice names must be short words
        foreach (var code in Expected)
        {
            var strings = LanguageCatalog.For(code);
            foreach (var option in new[] { strings.Session, strings.Model, strings.Effort, strings.Perm })
            {
                Assert.All(option.Choices, c => Assert.DoesNotContain('.', c.TrimEnd('.')));
                Assert.All(option.Choices, c => Assert.True(c.Length <= 12, $"{code} choice is too long: {c}"));
            }
        }
    }

    // ---------------- Automatic language selection ----------------

    [Theory]
    [InlineData(0x0412, "ko")]   // Korean
    [InlineData(0x0411, "ja")]   // Japanese
    [InlineData(0x040C, "fr")]   // French (France)
    [InlineData(0x0C0C, "fr")]   // French (Canada)
    [InlineData(0x0407, "de")]
    [InlineData(0x0410, "it")]
    [InlineData(0x0416, "pt")]   // Portuguese (Brazil)
    [InlineData(0x0816, "pt")]   // Portuguese (Portugal)
    [InlineData(0x0C0A, "es")]
    [InlineData(0x0409, "en")]   // English (US)
    [InlineData(0x0809, "en")]   // English (UK)
    [InlineData(0x041D, "en")]   // Swedish → unsupported → English
    [InlineData(0x0000, "en")]
    public void Language_from_LANGID(int langId, string expected)
        => Assert.Equal(expected, LanguageResolver.FromLangId(langId));

    [Theory]
    [InlineData(0x0404, "zht")]  // Taiwan
    [InlineData(0x0C04, "zht")]  // Hong Kong
    [InlineData(0x1404, "zht")]  // Macau
    [InlineData(0x0804, "zh")]   // Mainland China
    [InlineData(0x1004, "zh")]   // Singapore
    public void Chinese_is_zht_only_for_traditional_regions(int langId, string expected)
        => Assert.Equal(expected, LanguageResolver.FromLangId(langId));

    [Fact]
    public void Language_chosen_in_settings_wins_over_LANGID()
    {
        Assert.Equal("de", LanguageResolver.Resolve("de", 0x0412));
        Assert.Equal("ko", LanguageResolver.Resolve("auto", 0x0412));
        Assert.Equal("ko", LanguageResolver.Resolve(null, 0x0412));
        Assert.Equal("ko", LanguageResolver.Resolve("Swedish", 0x0412));   // unknown value → automatic
    }

    // ---------------- Fonts ----------------

    [Theory]
    [InlineData("ko", "Malgun Gothic")]
    [InlineData("ja", "Yu Gothic UI")]
    [InlineData("zh", "Microsoft YaHei UI")]
    [InlineData("zht", "Microsoft JhengHei UI")]
    [InlineData("en", "Segoe UI")]
    [InlineData("fr", "Segoe UI")]
    [InlineData("de", "Segoe UI")]
    [InlineData("it", "Segoe UI")]
    [InlineData("pt", "Segoe UI")]
    [InlineData("es", "Segoe UI")]
    public void UI_font_per_language(string code, string expected)
        => Assert.Equal(expected, UiFonts.Text(code));

    [Theory]
    [InlineData("ja", "Yu Gothic UI")]
    [InlineData("zh", "Microsoft YaHei UI")]
    [InlineData("zht", "Microsoft JhengHei UI")]
    [InlineData("ko", "Malgun Gothic")]
    [InlineData("en", "Malgun Gothic")]   // input can be in any language → a font that covers Hangul
    public void Input_box_font(string code, string expected)
        => Assert.Equal(expected, UiFonts.Entry(code));

    [Fact]
    public void Icon_font_is_Segoe_Fluent_Icons()
    {
        // third-party fonts are banned in the public build — only fonts shipped with Windows are used
        Assert.Equal("Segoe Fluent Icons", UiFonts.Icon);
        Assert.All(Expected, code => Assert.DoesNotContain("SF ", UiFonts.Text(code)));
    }

    // ---------------- Placeholder text ----------------

    [Fact]
    public void Search_box_placeholder_is_english_regardless_of_language()
    {
        Assert.Equal("Ask Claude", Placeholders.For(BarMode.Claude));
        Assert.Equal("Search files", Placeholders.For(BarMode.Files));
    }

    [Fact]
    public void Stack_puts_primary_first_and_picks_cjk_by_script()
    {
        Assert.StartsWith("Pretendard Variable, Malgun Gothic", UiFonts.Stack("Save Your Tears", "ko"));
        Assert.StartsWith("Pretendard Variable, Yu Gothic UI", UiFonts.Stack("ただ君に晴れ", "ko"));
        Assert.StartsWith("Pretendard Variable, Microsoft YaHei UI", UiFonts.Stack("烏", "zh"));
        Assert.StartsWith("x.ttf#P, ", UiFonts.Stack("a", "ko", "x.ttf#P"));
        var fonts = UiFonts.Stack("a", "en").Split(", ");
        Assert.Equal(fonts.Length, fonts.Distinct().Count());
    }
}
