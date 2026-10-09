using System.Runtime.InteropServices;
using IsleBar.Core.Launch;
using Xunit;

namespace IsleBar.Core.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData(@"C:\Program Files\WindowsApps\DS3OWL.IsleBar_0.1.2.0_x64__8t0efx9yebce8\IsleBar.App.exe")]
    [InlineData("say \"hi\"")]
    [InlineData(@"ends with backslash\")]
    [InlineData(@"two backslashes then quote \\""x")]
    [InlineData(@"C:\folder with space\")]
    [InlineData("line one\nline two\ttab")]
    [InlineData("한글 질문 & | % ^ < >")]
    [InlineData(@"\\server\share\")]
    public void Windows_splits_it_back_unchanged(string arg)
    {
        var line = CommandLine.Join(["first.exe", arg, "--child"]);
        Assert.Equal(new[] { "first.exe", arg, "--child" }, Split(line));
    }

    [Fact]
    public void Plain_arguments_stay_unquoted()
        => Assert.Equal(@"C:\a\IsleBar.App.exe --child", CommandLine.Join([@"C:\a\IsleBar.App.exe", "--child"]));

    private static string[] Split(string line)
    {
        var argv = CommandLineToArgvW(line, out var count);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
