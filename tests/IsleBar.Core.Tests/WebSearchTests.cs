using IsleBar.Core.Configuration;
using IsleBar.Core.Localization;
using IsleBar.Core.Search;
using Xunit;

namespace IsleBar.Core.Tests;

public class WebSearchTests
{
    [Theory]
    [InlineData("auto", "ko", "google")]
    [InlineData("auto", "zh", "baidu")]        // Simplified Chinese defaults to Baidu
    [InlineData("auto", "ja", "google")]
    [InlineData("auto", "xx-unknown", "google")]
    [InlineData(null, "zh", "baidu")]
    [InlineData("no-such-engine", "ko", "google")]
    [InlineData("naver", "en", "naver")]       // an explicitly chosen engine stays when the language changes
    public void Auto_uses_language_default_and_explicit_choice_is_kept(string? setting, string language, string expected)
        => Assert.Equal(expected, WebSearch.Resolve(setting, language).Id);

    [Theory]
    [InlineData("ko", "google,naver,daum,bing")]
    [InlineData("ja", "google,yahoo_jp,bing")]
    [InlineData("zh", "baidu,bing_cn,sogou")]
    [InlineData("zht", "google,yahoo_tw,bing")]
    [InlineData("de", "google,ecosia,bing,duckduckgo")]
    [InlineData("xx", "google,bing,duckduckgo")]
    public void Engine_list_per_language(string language, string expected)
        => Assert.Equal(expected, string.Join(",", WebSearch.EnginesFor(language).Select(e => e.Id)));

    [Fact]
    public void Every_language_list_points_to_real_engines()
    {
        foreach (var info in LanguageCatalog.Languages)
        {
            var engines = WebSearch.EnginesFor(info.Code);
            Assert.NotEmpty(engines);
            Assert.All(engines, e => Assert.Contains("{0}", e.UrlTemplate));
        }
    }

    [Theory]
    [InlineData("google", "https://www.google.com/search?q=%EC%98%A4%EB%8A%98%20%EB%82%A0%EC%94%A8")]
    [InlineData("naver", "https://search.naver.com/search.naver?query=%EC%98%A4%EB%8A%98%20%EB%82%A0%EC%94%A8")]
    [InlineData("baidu", "https://www.baidu.com/s?wd=%EC%98%A4%EB%8A%98%20%EB%82%A0%EC%94%A8")]
    public void Builds_URL_per_engine(string engine, string expected)
        => Assert.Equal(expected, WebSearch.UrlFor(WebSearch.Resolve(engine, "ko"), "  오늘 날씨 ")!.AbsoluteUri);

    [Fact]
    public void Special_characters_do_not_break_URL()
    {
        var url = WebSearch.UrlFor(WebSearch.Resolve("bing", "en"), "c# & .net?#1")!.AbsoluteUri;
        Assert.Equal("https://www.bing.com/search?q=c%23%20%26%20.net%3F%231", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_query_gives_null(string? query) => Assert.Null(WebSearch.UrlFor(WebSearch.Engines[0], query));

    [Fact]
    public void Engine_ids_are_unique()
        => Assert.Equal(WebSearch.Engines.Count, WebSearch.Engines.Select(e => e.Id).Distinct().Count());

    [Theory]
    [InlineData(BarMode.Claude, true, BarMode.Files)]
    [InlineData(BarMode.Files, true, BarMode.Web)]
    [InlineData(BarMode.Web, true, BarMode.Claude)]
    [InlineData(BarMode.Claude, false, BarMode.Web)]   // skipped when file search is off
    [InlineData(BarMode.Web, false, BarMode.Claude)]
    public void Tab_cycles_Claude_files_web(BarMode now, bool filesOn, BarMode next)
        => Assert.Equal(next, BarModes.Next(now, filesOn));

    [Fact]
    public void Web_mode_placeholder() => Assert.Equal("Search the web", Placeholders.For(BarMode.Web, "claude"));

    [Fact]
    public void Settings_engine_is_saved_and_unknown_value_becomes_auto()
    {
        Assert.Equal(WebSearch.Auto, new IsleBarSettings().WebEngine);
        var json = new System.Text.Json.Nodes.JsonObject();
        SettingsCodec.Apply(new IsleBarSettings { WebEngine = "naver" }, json);
        Assert.Equal("naver", SettingsCodec.FromJson(json).WebEngine);

        json["web_engine"] = "no-such-engine";
        Assert.Equal(WebSearch.Auto, SettingsCodec.FromJson(json).WebEngine);
    }
}
