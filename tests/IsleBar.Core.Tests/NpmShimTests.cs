using IsleBar.Core.Launch;
using Xunit;

namespace IsleBar.Core.Tests;

public class NpmShimTests
{
    // what npm writes for a global CLI (codex.cmd, 10-03)
    private const string Wrapper = """
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0

        IF EXIST "%dp0%\node.exe" (
          SET "_prog=%dp0%\node.exe"
        ) ELSE (
          SET "_prog=node"
          SET PATHEXT=%PATHEXT:;.JS;=;%
        )

        endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\node_modules\@openai\codex\bin\codex.js" %*
        """;

    private const string Npm = @"C:\Users\me\AppData\Roaming\npm";

    [Fact]
    public void Node_on_path_and_the_script_replace_the_wrapper()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Npm + @"\codex.cmd", Npm + @"\node_modules\@openai\codex\bin\codex.js", @"C:\Program Files\nodejs\node.exe",
        };
        var result = NpmShim.Unwrap(Npm + @"\codex.cmd", files.Contains, _ => Wrapper, @"C:\Windows;C:\Program Files\nodejs");
        Assert.Equal([@"C:\Program Files\nodejs\node.exe", Npm + @"\node_modules\@openai\codex\bin\codex.js"], result);
    }

    [Fact]
    public void A_node_beside_the_wrapper_comes_first()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Npm + @"\codex.cmd", Npm + @"\node.exe", Npm + @"\node_modules\@openai\codex\bin\codex.js", @"C:\Program Files\nodejs\node.exe",
        };
        Assert.Equal(Npm + @"\node.exe", NpmShim.Unwrap(Npm + @"\codex.cmd", files.Contains, _ => Wrapper, @"C:\Program Files\nodejs")![0]);
    }

    [Fact]
    public void Npm6_and_pnpm_wrappers_use_tilde_dp0()
    {
        const string pnpm = "@SETLOCAL\r\n@IF EXIST \"%~dp0\\node.exe\" (\r\n  \"%~dp0\\node.exe\"  \"%~dp0\\global\\5\\node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n) ELSE (\r\n  node  \"%~dp0\\global\\5\\node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n)";
        const string Pnpm = @"C:\Users\me\AppData\Local\pnpm";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Pnpm + @"\codex.cmd", Pnpm + @"\global\5\node_modules\@openai\codex\bin\codex.js", @"C:\Program Files\nodejs\node.exe",
        };
        Assert.Equal([@"C:\Program Files\nodejs\node.exe", Pnpm + @"\global\5\node_modules\@openai\codex\bin\codex.js"],
            NpmShim.Unwrap(Pnpm + @"\codex.cmd", files.Contains, _ => pnpm, @"C:\Program Files\nodejs"));
    }

    [Fact]
    public void A_wrapper_that_calls_another_wrapper_is_followed_once()
    {
        const string Yarn = @"C:\Users\me\AppData\Local\Yarn\bin";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Yarn + @"\codex.cmd", Yarn + @"\..\Data\global\node_modules\.bin\codex.cmd",
            @"C:\Users\me\AppData\Local\Yarn\Data\global\node_modules\.bin\codex.cmd",
            @"C:\Users\me\AppData\Local\Yarn\Data\global\node_modules\@openai\codex\bin\codex.js", @"C:\nodejs\node.exe",
        };
        string Read(string p) => p.StartsWith(Yarn, StringComparison.OrdinalIgnoreCase)
            ? "@\"%~dp0\\..\\Data\\global\\node_modules\\.bin\\codex.cmd\" %*"
            : Wrapper.Replace(@"node_modules\@openai", @"..\@openai", StringComparison.Ordinal);
        Assert.Equal([@"C:\nodejs\node.exe", @"C:\Users\me\AppData\Local\Yarn\Data\global\node_modules\@openai\codex\bin\codex.js"],
            NpmShim.Unwrap(Yarn + @"\codex.cmd", files.Contains, Read, @"C:\nodejs"));
    }

    [Fact]
    public void Not_a_wrapper_or_nothing_found_gives_null()
    {
        Assert.Null(NpmShim.Unwrap(@"C:\tools\codex.exe", _ => true, _ => Wrapper, null));                 // a real exe
        Assert.Null(NpmShim.Unwrap(Npm + @"\codex.cmd", p => p.EndsWith(".cmd"), _ => Wrapper, null));     // script missing
        Assert.Null(NpmShim.Unwrap(Npm + @"\codex.cmd", _ => true, _ => "@echo off\r\nsomething else %*", null));
        var noNode = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Npm + @"\codex.cmd", Npm + @"\node_modules\@openai\codex\bin\codex.js" };
        Assert.Null(NpmShim.Unwrap(Npm + @"\codex.cmd", noNode.Contains, _ => Wrapper, @"C:\Windows"));
    }
}
