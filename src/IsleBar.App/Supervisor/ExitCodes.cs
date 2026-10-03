namespace IsleBar.App.Supervisor;

/// <summary>
/// Exit codes exchanged with the supervising launcher.
///
/// <b>Values and meanings live in <see cref="IsleBar.Core.Supervisor.ExitCodes"/> (Core)</b> —
/// moved there to test without a window (09-29). This is an alias kept so the App side can refer to them briefly.
/// </summary>
internal static class ExitCodes
{
    public const int UserQuit = IsleBar.Core.Supervisor.ExitCodes.UserQuit;
    public const int Abnormal = IsleBar.Core.Supervisor.ExitCodes.Abnormal;
    public const int RestartRequested = IsleBar.Core.Supervisor.ExitCodes.RestartRequested;
    public const int AlreadyRunning = IsleBar.Core.Supervisor.ExitCodes.AlreadyRunning;

    public static bool ShouldRelaunch(int code) => IsleBar.Core.Supervisor.ExitCodes.ShouldRelaunch(code);
}
