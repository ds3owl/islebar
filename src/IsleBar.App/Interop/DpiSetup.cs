using static IsleBar.App.Interop.NativeMethods;

namespace IsleBar.App.Interop;

/// <summary>
/// DPI mode. **Attaching to the taskbar requires PER_MONITOR_AWARE_V2, the same as Explorer** —
/// otherwise SetParent is silently refused (lost a lot of time on this on 09-29).
/// app.manifest already sets it, but at startup we check and log that it actually took effect.
/// </summary>
internal static class DpiSetup
{
    /// <summary>Awareness value for PER_MONITOR_AWARE_V2.</summary>
    private const int AwarenessPerMonitorAwareV2 = 3;

    /// <summary>Display scale (1.0 = 100%, 2.0 = 200%).</summary>
    public static double Scale { get; private set; } = 1.0;

    /// <summary>
    /// Does nothing if the manifest already set it (in that case this call failing is expected).
    /// Reads the actual mode into <see cref="IsCorrect"/>.
    /// </summary>
    public static void Ensure()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Scale = GetDpiForSystem() / 96.0;
        IsCorrect = CurrentAwareness() == AwarenessPerMonitorAwareV2;
    }

    /// <summary>Whether the DPI mode matches Explorer. If false, we cannot attach to the taskbar.</summary>
    public static bool IsCorrect { get; private set; }

    public static int CurrentAwareness() => GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());

    /// <summary>Pixels multiplied by the scale. Same as the Python version's <c>px()</c>.</summary>
    public static int Px(double value) => (int)(value * Scale);
}
