namespace IsleBar.App.Interop;

/// <summary>Where settings, history and state files live.</summary>
internal static class AppPaths
{
    /// <summary>Process app ID so the taskbar does not group us with another app's icon.</summary>
    public const string AppUserModelId = "ds3owl.IsleBar";

    /// <summary>Mutex name used to check whether we are already running.</summary>
    public const string SingleInstanceMutex = @"Local\islebar_ds3owl";

    public static string DataDirectory { get; } = EnsureDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsleBar"));

    public static string LogDirectory { get; } = EnsureDirectory(Path.Combine(DataDirectory, "logs"));

    /// <summary>State JSON folder (new wiring rule). Must match Core's ActivityStore default.</summary>
    public static string StateDirectory { get; } = EnsureDirectory(Path.Combine(DataDirectory, "state"));

    public static string SettingsFile => Path.Combine(DataDirectory, "islebar.json");

    /// <summary>Everything SDK DLL. Kept inside the app folder.</summary>
    public static string EverythingDll => Path.Combine(AppContext.BaseDirectory, "everything_sdk", "Everything64.dll");

    /// <summary>
    /// Running as the Microsoft Store (MSIX) build. The Store updates the app itself (our own updater stays off — Store policy)
    /// and there is no setup program, so connecting Claude Code / Codex happens from the app.
    /// </summary>
    public static bool IsPackaged { get; } = DetectPackage();

    /// <summary>
    /// The CLI that Claude Code / Codex hooks call. Setup build: <c>%LOCALAPPDATA%\IsleBar\bin\islebar.exe</c>. Store build: the
    /// app execution alias in <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>, which runs the packaged CLI.
    /// </summary>
    public static string CliPath => IsPackaged
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "islebar.exe")
        : Path.Combine(DataDirectory, "bin", "islebar.exe");

    /// <summary>The script that adds or removes IsleBar's hooks (shipped with both builds).</summary>
    public static string HooksScript => Path.Combine(AppContext.BaseDirectory, "setup", "hooks.ps1");

    private static bool DetectPackage()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    private const int AppModelErrorNoPackage = 15700;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

    private static string EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (IOException)
        {
            // If it can't be created, the consumer fails at that point
        }

        return path;
    }
}
