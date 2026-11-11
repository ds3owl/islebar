using System.Runtime.InteropServices;

namespace IsleBar.App.Interop;

/// <summary>
/// Brings forward the terminal window where an agent process (claude.exe etc.) runs (when the island's Claude notice is clicked —
/// decision card "clicking goes to that terminal"). Briefly attaches to that process's console to get the console window,
/// and if it's Windows Terminal, brings forward that console window's owner (the terminal window). Returns false if not found.
/// </summary>
internal static class AgentWindow
{
    private const uint GA_ROOTOWNER = 3;

    public static bool Bring(int pid)
    {
        var target = Find(pid);
        if (target == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(target))
        {
            ShowWindow(target, 9);   // SW_RESTORE
        }

        ForegroundHelper.Bring(target);
        return true;
    }

    /// <summary>
    /// The terminal window an agent process runs in (the Windows Terminal window that owns its console, or the console itself),
    /// or zero if the process is gone or has no console.
    /// </summary>
    public static IntPtr Find(int pid, bool log = true)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return IntPtr.Zero;
            }
        }
        catch (ArgumentException)
        {
            return IntPtr.Zero;   // session already ended
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // an agent running as administrator can't be inspected: its finished task froze the whole pill — the exception
            // escaped the tick every time (review 10-03). Its window just isn't found.
            return IntPtr.Zero;
        }

        if (!AttachConsole((uint)pid))
        {
            return IntPtr.Zero;
        }

        IntPtr console;
        try
        {
            console = GetConsoleWindow();
        }
        finally
        {
            FreeConsole();
        }

        if (console == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var owner = GetAncestor(console, GA_ROOTOWNER);
        var target = owner != IntPtr.Zero ? owner : console;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(target, cls, 64);
        var visible = IsWindowVisible(target);
        if (log)
        {
            TaskbarHost.Log($"find terminal: pid={pid} console={console:X} owner={owner:X} target={target:X} class={cls} visible={visible}");
        }

        return visible ? target : IntPtr.Zero;   // a hidden pseudo console (editor terminals) never gets clicks — don't watch it
    }

    /// <summary>
    /// The terminals a background Claude session is read from, or null when <paramref name="pid"/> isn't a background session.
    /// A session sent to the background (<c>claude --bg</c>, or moved there from the session list) runs under
    /// <c>claude --bg-pty-host</c> in a hidden pseudo console that never gets a click, so its green border never cleared (user 11-11).
    /// You read it through another Claude in a visible terminal — <c>claude attach</c>, or a running Claude's session list. Claude
    /// Code notes each attach in <c>~/.claude/daemon/attach-journal</c> (the viewer's pid and start time, not which session it opened),
    /// so the terminals of the live viewers there count; when none is noted, the terminals of the running interactive sessions do.
    /// </summary>
    public static IntPtr[]? FindViewers(int pid)
    {
        var dir = ClaudeDir();
        if (ReadSession(Path.Combine(dir, "sessions", $"{pid}.json")) is not { Kind: "bg" })
        {
            return null;
        }

        var viewers = new List<int>();
        foreach (var file in Files(Path.Combine(dir, "daemon", "attach-journal")))
        {
            if (ReadSession(file) is { Pid: { } viewer, ProcStart: { } start } && viewer != pid && Running(viewer, start))
            {
                viewers.Add(viewer);
            }
        }

        if (viewers.Count == 0)
        {
            foreach (var file in Files(Path.Combine(dir, "sessions")))
            {
                if (ReadSession(file) is { Kind: "interactive", Pid: { } other, ProcStart: { } start } && other != pid && Running(other, start))
                {
                    viewers.Add(other);
                }
            }
        }

        return viewers.Select(v => Find(v, log: false)).Where(w => w != IntPtr.Zero).Distinct().OrderBy(w => (long)w).ToArray();
    }

    private static string ClaudeDir() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static IEnumerable<string> Files(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed record SessionNote(int? Pid, string? Kind, string? ProcStart);

    /// <summary>pid, kind and process start time from one of Claude Code's session or attach notes; null if it can't be read.</summary>
    private static SessionNote? ReadSession(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            int? pid = root.TryGetProperty("pid", out var p) && p.TryGetInt32(out var n) ? n : null;
            string? kind = root.TryGetProperty("kind", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String ? k.GetString() : null;
            string? start = root.TryGetProperty("procStart", out var s)
                ? s.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => s.GetString(),
                    System.Text.Json.JsonValueKind.Number => s.GetRawText(),
                    _ => null,
                }
                : null;
            return new SessionNote(pid, kind, start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;   // being rewritten right now, or not ours to read — skip it this time
        }
    }

    /// <summary>
    /// <paramref name="pid"/> is still the process the note was written for (its start time, as a Windows file time) — not a later
    /// program that got the same number.
    /// </summary>
    private static bool Running(int pid, string procStart)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture) == procStart;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
