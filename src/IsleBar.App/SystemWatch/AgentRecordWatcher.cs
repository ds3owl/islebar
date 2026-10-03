using IsleBar.Core.Island;
using IsleBar.Core.Launch;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Follows the session record of each running agent session, for what no hook reports:
/// <list type="bullet">
/// <item>Codex fires no hook when a turn fails (user 10-03) — on the usage limit the pill used to keep saying "working" — but its
/// record says so (<c>task_complete</c> with an error) and records the plan's usage windows (<c>token_count</c>). A failed turn
/// replaces the session's item with the red usage-limit or "stopped" notice, and the first time a usage window passes 90% a
/// short heads-up goes up.</item>
/// <item>Claude Code fires no Stop hook when the user interrupts it (Esc) — the pill said "working" for hours (review 10-03). Its
/// transcript gets "[Request interrupted by user]"; the session's item is then cleared.</item>
/// </list>
/// A session is followed while its item is "working" or "needs you". Only new bytes are read (records only grow); the first
/// read takes the end of the record, and from it only lines written after the current turn began count — an old failure or
/// interrupt further up used to come back on the next prompt (review 10-03). No slack: the turn's start, the state file and
/// the record lines all come from this PC's clock, and an interrupt is followed by the next prompt within a tenth of a second.
/// Before the item is changed the state file is checked again, so a hook that wrote meanwhile isn't overwritten.
/// </summary>
internal sealed class AgentRecordWatcher : NoticeWatcher
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeadsUpFor = TimeSpan.FromSeconds(10);
    private const int FirstReadBytes = 256 * 1024;   // when a session is first seen: enough of the end to find the latest usage

    private readonly object _gate = new();
    private readonly Dictionary<string, Followed> _followed = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;

    private sealed class Followed
    {
        public long Offset;
        public byte[] Pending = [];          // the end of the last read that isn't a whole line yet (bytes: a UTF-8 character may be split)
        public bool DropFirstLine;           // started mid-file: the first piece is the tail of a line
        public IReadOnlyList<UsageWindow>? Windows;
    }

    public AgentRecordWatcher(Func<Core.Localization.LanguageStrings> strings)
        : base(strings)
    {
    }

    public override void Start() => Guard(() =>
    {
        _timer ??= new Timer(_ => Guard(Scan), null, Interval, Interval);
    });

    protected override void Stop() => _timer?.Dispose();

    private void Scan()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            var store = new ActivityStore(Interop.AppPaths.StateDirectory);
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(Interop.AppPaths.StateDirectory, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                var written = File.GetLastWriteTimeUtc(file);   // before reading it — a hook rewriting it after this is noticed below
                if (!(id.StartsWith("codex_", StringComparison.OrdinalIgnoreCase) || id.StartsWith("claude_", StringComparison.OrdinalIgnoreCase))
                    || store.ReadById(id) is not { Transcript: { Length: > 0 } record } state
                    || state.Agent is not (AgentKind.Codex or AgentKind.Claude)
                    || state.Kind is not (ActivityKind.AgentWorking or ActivityKind.AgentPermission)
                    || !File.Exists(record))
                {
                    continue;
                }

                live.Add(record);
                try
                {
                    // the turn's start: its own time, else when the state file was written (a permission state carries no t0)
                    var since = state.T0 is { } t0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(t0 * 1000)) : new DateTimeOffset(written, TimeSpan.Zero);
                    Follow(store, id, record, state.Agent, since, () => File.GetLastWriteTimeUtc(file) == written);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // one unreadable record must not stop the others (review 10-03); tried again next scan
                }
            }

            foreach (var gone in _followed.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _followed.Remove(gone);
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private void Follow(ActivityStore store, string id, string record, string agent, DateTimeOffset since, Func<bool> unchanged)
    {
        using var stream = new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var end = stream.Length;
        if (!_followed.TryGetValue(record, out var followed))
        {
            followed = new Followed { Offset = Math.Max(0, end - FirstReadBytes) };
            followed.DropFirstLine = followed.Offset > 0;
            _followed[record] = followed;
        }

        if (end < followed.Offset)
        {
            followed.Offset = 0;   // a new file under the same name
            followed.Pending = [];
            followed.DropFirstLine = false;
        }

        if (end == followed.Offset)
        {
            return;
        }

        // read exactly up to the length seen now — bytes appended meanwhile are read next time, not skipped (review 10-03)
        var fresh = new byte[end - followed.Offset];
        stream.Seek(followed.Offset, SeekOrigin.Begin);
        stream.ReadExactly(fresh);
        followed.Offset = end;

        var bytes = followed.Pending.Length == 0 ? fresh : [.. followed.Pending, .. fresh];
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        if (lastNewline < 0)
        {
            followed.Pending = bytes;
            return;
        }

        followed.Pending = bytes[(lastNewline + 1)..];
        var lines = System.Text.Encoding.UTF8.GetString(bytes, 0, lastNewline).Split('\n');
        var first = followed.DropFirstLine ? 1 : 0;
        followed.DropFirstLine = false;
        var now = DateTimeOffset.UtcNow;
        if (agent == AgentKind.Claude)
        {
            // the last interrupt of this turn with no prompt after it: stopped by the user, nothing is running any more
            var interrupted = false;
            for (var i = first; i < lines.Length; i++)
            {
                if (AgentRecord.IsClaudeInterrupt(lines[i]))
                {
                    interrupted = AgentRecord.LineTime(lines[i]) is { } at && at > since;
                }
                else if (interrupted && AgentRecord.IsClaudePrompt(lines[i]))
                {
                    interrupted = false;   // a new prompt right after — the interrupt belonged to the turn before
                }
            }

            if (interrupted && unchanged())
            {
                store.Remove(id);
                Interop.TaskbarHost.Log("claude: turn interrupted");
            }

            return;
        }

        for (var i = first; i < lines.Length; i++)
        {
            var line = lines[i];

            if (AgentUsage.FromCodexLine(line) is { } windows)
            {
                followed.Windows = windows;
                if (AgentUsage.ToWarnAbout(windows) is { } high
                    && AgentUsage.FirstWarning(Path.Combine(Interop.AppPaths.DataDirectory, "usage_warned.txt"), AgentKind.Codex, high))
                {
                    Board.Flash("codex-usage", AgentNotices.UsageHigh(AgentKind.Codex, high.Percent, high.ResetsAt, Text, now), HeadsUpFor, now);
                }
            }
            else if (AgentUsage.CodexFailureFrom(line) is { } failure && (AgentRecord.LineTime(line) is not { } at || at > since) && unchanged())
            {
                var notice = failure.UsageLimit
                    ? AgentNotices.UsageLimit(AgentKind.Codex, AgentUsage.ResetOfFullest(followed.Windows), Text, now)
                    : AgentNotices.Stopped(AgentKind.Codex, failure.Reason, Text, now);
                store.Write(id, notice);   // replaces "working" — the next prompt turns it back into "working"
                Interop.TaskbarHost.Log($"codex: turn failed ({(failure.UsageLimit ? "usage limit" : failure.Reason.ToString())})");
                return;
            }
        }
    }
}
