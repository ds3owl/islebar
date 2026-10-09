using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using IsleBar.App.Supervisor;

namespace IsleBar.App.Interop;

/// <summary>
/// Store (MSIX) build only: starts the bar process so that it keeps the package identity but everything <b>it</b> starts runs as a
/// normal program, outside the package.
/// <para>
/// Without the unvirtualizedResources capability (refused by Store certification 10-07), Windows redirects new files under AppData
/// and every HKCU write of a packaged process into a private copy that is deleted with the app. Child processes inherit that, so
/// a Claude Code or Codex session asked from the bar, an app opened from file search, or a browser opened from a link would have
/// written into IsleBar's private copy (a tool installed from that session would vanish with IsleBar). The documented switch
/// <c>PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY</c> fixes that at one place: the supervisor starts the bar with
/// <c>BREAKAWAY_OVERRIDE</c> (the bar itself stays in the package — notifications, start-up task, shared state with the hook CLI)
/// and <c>BREAKAWAY_ENABLE_PROCESS_TREE</c> (whatever the bar starts runs outside).
/// </para>
/// </summary>
internal static partial class PackagedChild
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly IntPtr DesktopAppPolicyAttribute = (IntPtr)0x00020012;   // ProcThreadAttributeValue(18, FALSE, TRUE, FALSE)
    private const uint BreakawayEnableProcessTree = 0x1;
    private const uint BreakawayOverride = 0x4;

    /// <summary>Starts <paramref name="exe"/> with <paramref name="args"/> as described above, waits for it and returns its exit code.</summary>
    /// <exception cref="Win32Exception">The process could not be started.</exception>
    public static int RunAndWait(string exe, IReadOnlyList<string> args, string workingDirectory)
    {
        var commandLine = new StringBuilder();
        IsleBar.Core.Launch.CommandLine.Append(commandLine, exe);
        foreach (var arg in args)
        {
            commandLine.Append(' ');
            IsleBar.Core.Launch.CommandLine.Append(commandLine, arg);
        }

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        var policy = Marshal.AllocHGlobal(sizeof(uint));
        var listReady = false;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            listReady = true;
            Marshal.WriteInt32(policy, (int)(BreakawayEnableProcessTree | BreakawayOverride));
            if (!UpdateProcThreadAttribute(list, 0, DesktopAppPolicyAttribute, policy, sizeof(uint), IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startup = new StartupInfoEx { AttributeList = list };
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            if (!CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow, IntPtr.Zero, workingDirectory,
                    ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                WaitForSingleObject(info.Process, Infinite);
                return GetExitCodeProcess(info.Process, out var code) ? (int)code : ExitCodes.Abnormal;
            }
            finally
            {
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
            }
        }
        finally
        {
            if (listReady)
            {
                DeleteProcThreadAttributeList(list);
            }

            Marshal.FreeHGlobal(policy);
            Marshal.FreeHGlobal(list);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Count;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
