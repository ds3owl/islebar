using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public class SessionTitleTests
{
    [Fact]
    public void Uses_last_auto_title()
    {
        var tail = """
            {"type":"user","message":"hi"}
            {"type":"ai-title","aiTitle":"Initial title","sessionId":"a"}
            {"type":"assistant","message":"..."}
            {"type":"ai-title","aiTitle":"커서 위치 문제","sessionId":"a"}
            """;
        Assert.Equal("커서 위치 문제", SessionTitle.FromTail(tail));
    }

    [Fact]
    public void Custom_title_takes_precedence()
    {
        var tail = """
            {"type":"custom-title","customTitle":"Search box work","sessionId":"a"}
            {"type":"ai-title","aiTitle":"Auto title","sessionId":"a"}
            """;
        Assert.Equal("Search box work", SessionTitle.FromTail(tail));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("""{"type":"user"}""")]
    [InlineData("""ai-title","aiTitle":"cut first line"}""")]   // first line cut off because only the tail was read
    public void No_title_gives_null(string? tail) => Assert.Null(SessionTitle.FromTail(tail));

    [Fact]
    public void Reads_only_the_tail_of_large_files()
    {
        using var dir = new TempDir();
        var path = dir.File("big.jsonl");
        using (var w = new StreamWriter(path))
        {
            w.WriteLine("""{"type":"ai-title","aiTitle":"Old title"}""");
            var filler = new string('x', 1000);
            for (var i = 0; i < 1200; i++)
            {
                w.WriteLine("{\"type\":\"user\",\"m\":\"" + filler + "\"}");   // about 1.2 MB
            }

            w.WriteLine("""{"type":"ai-title","aiTitle":"New title"}""");
        }

        Assert.Equal("New title", SessionTitle.FromTranscript(path));
        Assert.Null(SessionTitle.FromTranscript(dir.File("missing.jsonl")));
    }
}
