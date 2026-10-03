using IsleBar.Core.SystemWatch;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class ToastTextTests
{
    [Fact]
    public void Extracts_text_from_toast_XML()
    {
        const string xml = "<toast><visual><binding template='ToastGeneric'><text>엄마</text><text> 저녁  뭐 먹어? </text><text></text></binding></visual></toast>";
        Assert.Equal(["엄마", "저녁 뭐 먹어?"], ToastText.Texts(xml));
    }

    [Fact]
    public void Broken_XML_gives_empty_list()
        => Assert.Empty(ToastText.Texts("<toast><text>"));

    [Theory]
    [InlineData("SAMSUNGELECTRONICSCO.LTD.SamsungSettings1.5_3c1yjt4zspk6g!App", "SamsungSettings")]
    [InlineData("Windows.Defender.SecurityCenter", "SecurityCenter")]
    [InlineData("폰 → PC", "폰 → PC")]
    [InlineData("", "Windows")]
    public void Guesses_app_name(string id, string expected)
        => Assert.Equal(expected, ToastText.GuessAppName(id));

    [Fact]
    public void Long_line_is_truncated()
        => Assert.EndsWith("…", ToastText.Line([new string('가', 200)]));

    [Fact]
    public void Protocol_toast_gives_its_deep_link()
        => Assert.Equal(
            "whatsapp://chat/?id=123",
            ToastText.LaunchProtocol("<toast activationType='protocol' launch='whatsapp://chat/?id=123'><visual/></toast>"));

    [Theory]
    [InlineData("<toast launch='conversation/42'><visual/></toast>")]                                  // no protocol activation → opaque app data
    [InlineData("<toast activationType='foreground' launch='conversation/42'><visual/></toast>")]      // foreground activation → app's own activator only
    [InlineData("<toast activationType='protocol' launch='C:\\secret.txt'><visual/></toast>")]         // a file path is not a deep link
    [InlineData("<toast activationType='protocol'><visual/></toast>")]                                 // no launch at all
    [InlineData("<toast><text>")]                                                                       // broken XML
    public void No_deep_link_returns_null(string payload)
        => Assert.Null(ToastText.LaunchProtocol(payload));
    [Theory]
    [InlineData("https://example.com/chat/1", "https://example.com/chat/1")]
    [InlineData("discord://channels/1/2", "discord://channels/1/2")]
    [InlineData(@"search-ms:query=x&crumb=location:\\evil\share", null)]   // a remote share in Explorer
    [InlineData("ms-msdt:/id PCWDiagnostic", null)]
    [InlineData("ms-word:ofe|u|https://evil/x.docx", null)]
    [InlineData("shell:startup", null)]
    public void Launch_links_from_toasts_skip_dangerous_schemes(string launch, string? expected)
    {
        var xml = $"<toast launch=\"{System.Security.SecurityElement.Escape(launch)}\" activationType=\"protocol\"><visual><binding><text>a</text></binding></visual></toast>";
        Assert.Equal(expected, ToastText.LaunchProtocol(xml));
    }
}
