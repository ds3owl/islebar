using IsleBar.Core.Configuration;
using IsleBar.Core.SystemWatch;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class ToastBannerPlanTests
{
    [Fact]
    public void Hides_apps_that_still_show_banners_and_remembers_their_value()
    {
        var current = new Dictionary<string, int?> { ["Chrome"] = null, ["KakaoTalk"] = 1, ["Discord"] = 0 };
        var hide = ToastBannerPlan.ToHide(current, new Dictionary<string, int?>());
        Assert.Equal(2, hide.Count);
        Assert.Null(hide["Chrome"]);
        Assert.Equal(1, hide["KakaoTalk"]);
        Assert.False(hide.ContainsKey("Discord"));   // already silenced by the user — not ours to restore
    }

    [Fact]
    public void Apps_already_in_the_ledger_are_not_recorded_again()
    {
        // after we set Chrome to 0 its current value is 0 anyway, but a ledger entry must never be overwritten with our own 0
        var current = new Dictionary<string, int?> { ["Chrome"] = 1 };
        var ledger = new Dictionary<string, int?> { ["chrome"] = null };
        Assert.Empty(ToastBannerPlan.ToHide(current, ledger));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    public void Restores_only_while_still_silenced(int? now, bool expected)
        => Assert.Equal(expected, ToastBannerPlan.ShouldRestore(now));

    [Fact]
    public void Ledger_round_trips_including_unset_values()
    {
        var ledger = new Dictionary<string, int?> { ["Chrome"] = null, ["KakaoTalk"] = 1 };
        var back = ToastBannerPlan.Parse(ToastBannerPlan.Serialize(ledger));
        Assert.Equal(2, back.Count);
        Assert.Null(back["chrome"]);
        Assert.Equal(1, back["KakaoTalk"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{broken")]
    public void Unreadable_ledger_is_empty(string? json)
        => Assert.Empty(ToastBannerPlan.Parse(json));

    [Fact]
    public void Banners_are_hidden_only_when_toasts_are_pulled_onto_the_bar()
    {
        // hiding Windows banners without showing them on the bar would lose notifications entirely
        var settings = new IsleBarSettings { NotifyToasts = false, HideToastBanners = true };
        Assert.False(SystemWatchOptions.From(settings).HideBanners);
        settings.NotifyToasts = true;
        Assert.True(SystemWatchOptions.From(settings).HideBanners);
    }

    [Fact]
    public void Option_is_off_by_default_and_saved()
    {
        Assert.False(new IsleBarSettings().HideToastBanners);
        var raw = new System.Text.Json.Nodes.JsonObject();
        SettingsCodec.Apply(new IsleBarSettings { HideToastBanners = true }, raw);
        Assert.True(raw["hide_toast_banners"]!.GetValue<bool>());
        Assert.True(SettingsCodec.FromJson(raw).HideToastBanners);
    }
}
