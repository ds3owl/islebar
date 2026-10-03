using System.Runtime.InteropServices;

namespace IsleBar.Cli;

/// <summary>
/// Finds the process ID of the agent (claude.exe/codex.exe) that invoked the hook. Hooks are called agent → shell → islebar.exe,
/// so we walk up a few parent levels. Clicking the island uses this ID to bring that terminal to the front.
/// Null on non-Windows or if not found (the island still shows; clicking just starts input).
/// </summary>
internal static class AgentProcess
{
    public static int? FindAncestor(params string[] names)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var parents = Snapshot();
            var pid = Environment.ProcessId;
            for (var depth = 0; depth < 8 && parents.TryGetValue(pid, out var entry); depth++)
            {
                pid = entry.Parent;
                if (parents.TryGetValue(pid, out var parent)
                    && names.Any(n => string.Equals(parent.Name, n, StringComparison.OrdinalIgnoreCase)))
                {
                    return pid;
                }
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
        }

        return null;
    }

    private static Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var result = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);   // TH32CS_SNAPPROCESS
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
            {
                result[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
