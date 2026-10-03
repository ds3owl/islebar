using System.Diagnostics;
using System.Runtime.InteropServices;
using IsleBar.Core.SystemWatch;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// CPU/memory overload. Every 2 s reads only total CPU (GetSystemTimes) and memory usage (GlobalMemoryStatusEx) — both very cheap.
/// If ≥90% (memory 92%) persists for 30 s, shows "CPU 96% · chrome" for 10 s (no border; click opens Task Manager); it shows again only after the load has dropped.
/// Rule is <see cref="SustainedThreshold"/>. Finding the busiest program (scanning all processes) is expensive, so
/// it's done <b>only while over the threshold</b> — normally the process list isn't touched.
/// Runs on a dedicated timer thread — no UI thread needed.
/// </summary>
internal sealed class LoadWatcher : NoticeWatcher
{
    private const string CpuKey = "cpu";
    private const string MemoryKey = "memory";
    private const string TaskManager = "taskmgr";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly SustainedThreshold _cpu = SustainedThreshold.Cpu();
    private readonly SustainedThreshold _memory = SustainedThreshold.Memory();
    private Timer? _timer;
    private (long Idle, long Kernel, long User)? _lastTimes;
    private Dictionary<int, TimeSpan>? _lastProcessTimes;
    private DateTimeOffset _lastProcessSample;
    private string? _topProcess;
    private DateTimeOffset _cpuSince;
    private DateTimeOffset _memorySince;
    private DateTimeOffset _cpuUntil;
    private DateTimeOffset _memoryUntil;

    /// <summary>
    /// How long an overload notice stays once it starts (user 10-01: it sat on the pill with an orange border for as long as a
    /// render ran). Shown again only after the load has dropped and come back. Not urgent: orange means "you need to answer".
    /// </summary>
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(10);

    public LoadWatcher(Func<Core.Localization.LanguageStrings> strings)
        : base(strings)
    {
    }

    public override void Start() => Guard(() =>
    {
        _timer ??= new Timer(_ => Guard(Sample), null, TimeSpan.Zero, Interval);
    });

    protected override void Stop() => _timer?.Dispose();

    private void Sample()
    {
        // Keep Timer callbacks from overlapping (a process scan may exceed 2 s)
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var s = Text;

            if (ReadCpu() is { } cpu)
            {
                var wasActive = _cpu.Active;
                var active = _cpu.Update(cpu, now);
                if (_cpu.Elevated)
                {
                    _topProcess = TopProcess(now) ?? _topProcess;
                }
                else
                {
                    _lastProcessTimes = null;   // below threshold = release the process list
                    _topProcess = null;
                }

                if (active)
                {
                    if (!wasActive)
                    {
                        _cpuSince = now;
                        _cpuUntil = now + ShowFor;
                    }

                    if (now < _cpuUntil)   // live percent while it shows, then it steps aside
                    {
                        var percent = (int)Math.Round(cpu);
                        Board.Flash(CpuKey, SystemNotice.Make(
                            NoticeText.Cpu(s, percent, _topProcess), NoticeGlyphs.Load, _cpuSince,
                            open: TaskManager), _cpuUntil - now, now);   // no gauge — the percent is in the text (user 10-02)
                    }
                }
                else
                {
                    Board.Clear(CpuKey);
                }
            }

            if (ReadMemoryLoad() is { } memory)
            {
                var wasActive = _memory.Active;
                if (_memory.Update(memory, now))
                {
                    if (!wasActive)
                    {
                        _memorySince = now;
                        _memoryUntil = now + ShowFor;
                    }

                    if (now < _memoryUntil)
                    {
                        Board.Flash(MemoryKey, SystemNotice.Make(
                            NoticeText.Memory(s, memory), NoticeGlyphs.Load, _memorySince,
                            open: TaskManager), _memoryUntil - now, now);
                    }
                }
                else
                {
                    Board.Clear(MemoryKey);
                }
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Total CPU % from the difference since last time. Null on the first call (nothing to compare).</summary>
    private double? ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var current = (idle, kernel, user);
        var last = _lastTimes;
        _lastTimes = current;
        return last is { } l ? CpuMath.TotalPercent(idle - l.Idle, kernel - l.Kernel, user - l.User) : null;
    }

    private static int? ReadMemoryLoad()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (int)status.dwMemoryLoad : null;
    }

    /// <summary>
    /// Name of the program that used the most CPU since the last sample. The first call only collects a sample (null).
    /// Processes whose times can't be read for lack of permission (system services, etc.) are skipped.
    /// </summary>
    private string? TopProcess(DateTimeOffset now)
    {
        var times = new Dictionary<int, TimeSpan>();
        var names = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4 || process.Id == Environment.ProcessId)
                {
                    continue;   // Idle, System, ourselves
                }

                try
                {
                    times[process.Id] = process.TotalProcessorTime;
                    names[process.Id] = process.ProcessName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                }
            }
        }

        var last = _lastProcessTimes;
        var wall = now - _lastProcessSample;
        _lastProcessTimes = times;
        _lastProcessSample = now;
        if (last is null)
        {
            return null;
        }

        string? top = null;
        var best = 0.0;
        foreach (var (pid, cpuTime) in times)
        {
            if (!last.TryGetValue(pid, out var before))
            {
                continue;
            }

            var percent = CpuMath.ProcessPercent(cpuTime - before, wall, Environment.ProcessorCount);
            if (percent > best)
            {
                best = percent;
                top = names[pid];
            }
        }

        return top;
    }

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
