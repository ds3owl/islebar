using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using IsleBar.App.Interop;
using static IsleBar.App.Interop.NativeMethods;

namespace IsleBar.App.Tracking;

/// <summary>Where the real Windows search box is.</summary>
/// <param name="Mode">Whether to cover it, sit to its left, or hide.</param>
/// <param name="X">Screen x where our window goes (physical pixels).</param>
/// <param name="Y">Screen y where our window goes.</param>
internal sealed record BarPlacement(PlacementMode Mode, int X, int Y)
{
    public static readonly BarPlacement Hidden = new(PlacementMode.Hide, 0, 0);
}

internal enum PlacementMode
{
    /// <summary>Cover exactly over the real search box (the best case).</summary>
    OverRealSearchBox,

    /// <summary>Search box off and taskbar left-aligned → we would cover icons, so hide.</summary>
    Hide,

    /// <summary>Search box off and center-aligned → sit in the empty space at the left edge.</summary>
    TaskbarLeftEdge,
}

/// <summary>
/// Follows the position of the real search box (Settings: "Search box", SearchboxTaskbarMode=2, UIA AutomationId <c>SearchButton</c>).
/// Windows reserves that spot, so when icons are added Windows moves them aside itself,
/// and we just keep covering it and follow along.
///
/// <b>Always runs on a background MTA thread.</b> Querying Explorer via UIA from the UI thread
/// makes both wait on each other and Explorer hangs — actually happened on 09-29 as AppHangXProcB1.
/// The UI thread only reads <see cref="Current"/>.
///
/// UIA uses <b>COM <c>IUIAutomation</c></b>, not WPF's <c>System.Windows.Automation</c>
/// (WinUI has no WPF side; same API as the Python version: CUIAutomation · TreeScope_Descendants ·
/// UIA_AutomationIdPropertyId). NuGet <c>Interop.UIAutomationClient</c>.
///
/// <b>This file has never been built. Needs phase-0 verification on PC.</b>
/// </summary>
internal sealed class SearchBoxTracker : IDisposable
{
    private const string SearchBoxAutomationId = "SearchButton";
    private const string StartButtonAutomationId = "StartButton";
    private const string WidgetsButtonAutomationId = "WidgetsButton";

    /// <summary>Wait before searching again when the search box wasn't found (searching often keeps Explorer busy).</summary>
    private static readonly TimeSpan NotFoundDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(30);      // while the taskbar is reflowing, track closely
    private static readonly TimeSpan FastWindow = TimeSpan.FromMilliseconds(650);   // ...for this long after the last change, then back to the idle poll

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly int _barWidth;
    private readonly int _barHeight;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private PlacementMode? _loggedMode;

    public SearchBoxTracker(int barWidth, int barHeight)
    {
        _barWidth = barWidth;
        _barHeight = barHeight;
        Current = BarPlacement.Hidden;
    }

    /// <summary>
    /// Latest position found by the background thread. The UI thread reads only this.
    /// (Reference assignment is atomic, so torn values are never seen.)
    /// </summary>
    public BarPlacement Current { get; private set; }

    /// <summary>Fired (on the tracker thread) the moment the position changes, so the UI can move the pill without waiting for its next poll — no lag when the taskbar reflows.</summary>
    public Action? OnChanged { get; set; }

    private DateTime _lastChangeUtc = DateTime.MinValue;

    private void SetCurrent(BarPlacement placement)
    {
        if (placement != Current)
        {
            Current = placement;
            _lastChangeUtc = DateTime.UtcNow;   // start a short fast-poll burst so a taskbar reflow is tracked smoothly, not in one 250ms jump
            OnChanged?.Invoke();
        }
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "IsleBar UIA tracker",
        };

        // MTA: calling UIA from the same apartment as the UI thread (STA) makes them wait on each other
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Loop()
    {
        // an unexpected error used to end tracking for good — the pill stopped following the taskbar until a restart (review 10-03)
        while (true)
        {
            try
            {
                LoopCore();
                return;   // stopped
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                TaskbarHost.Log($"tracker error, starting over: {ex.GetType().Name} {ex.Message}");
                try
                {
                    if (_stop.IsCancellationRequested || _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
                    {
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    private void LoopCore()
    {
        var automation = (IUIAutomation)new CUIAutomation();
        IUIAutomationElement? searchBox = null;
        var (bandTop, bandHeight) = TaskbarBand();

        while (!_stop.IsCancellationRequested)
        {
            var delay = DateTime.UtcNow - _lastChangeUtc < FastWindow ? FastPoll : PollInterval;
            try
            {
                var tray = TaskbarHost.FindTaskbar();
                if (tray == IntPtr.Zero)
                {
                    searchBox = null;      // Explorer is restarting
                }
                else
                {
                    searchBox ??= FindDescendant(automation, tray, SearchBoxAutomationId);

                    var rect = searchBox is null ? default : searchBox.CurrentBoundingRectangle;
                    // "Search icon only" / "icon and label": the button is far narrower than the pill, and centring the pill on it
                    // covered the Start button and the icons beside it (review 10-03) — treat it like no search box
                    if (rect.right - rect.left > 0 && rect.right - rect.left >= _barWidth * 0.6)
                    {
                        SetCurrent(new BarPlacement(
                            PlacementMode.OverRealSearchBox,
                            rect.left + ((rect.right - rect.left - _barWidth) / 2),
                            rect.top + ((rect.bottom - rect.top - _barHeight) / 2)));
                    }
                    else
                    {
                        // No search box (turned off, or only an icon). Pick a spot that doesn't cover anything: with the icons
                        // centred, the free stretch left of the Start button — after the Widgets button when it's there (it sits at
                        // the left edge by default; the pill used to cover it — review 10-03). Not enough room → hide.
                        searchBox = null;
                        var start = FindDescendant(automation, tray, StartButtonAutomationId);
                        var startX = start?.CurrentBoundingRectangle.left ?? 0;
                        var widgets = FindDescendant(automation, tray, WidgetsButtonAutomationId);
                        var widgetsRect = widgets is null ? default : widgets.CurrentBoundingRectangle;
                        var left = DpiSetup.Px(12);
                        if (widgetsRect.right > widgetsRect.left && widgetsRect.left < startX)
                        {
                            left = widgetsRect.right + DpiSetup.Px(8);
                        }

                        (bandTop, bandHeight) = TaskbarBand();   // the taskbar may have moved or changed size since
                        SetCurrent(startX > DpiSetup.Px(300) && left + _barWidth <= startX - DpiSetup.Px(8)
                            ? new BarPlacement(
                                PlacementMode.TaskbarLeftEdge,
                                left,
                                bandTop + ((bandHeight - _barHeight) / 2))
                            : BarPlacement.Hidden);
                        delay = NotFoundDelay;
                    }
                }
            }
            catch (COMException ex)
            {
                TaskbarHost.Log($"UIA error: {ex.Message}");
                searchBox = null;     // Explorer restarted → find it again on the next pass
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                searchBox = null;
            }

            if (Current.Mode != _loggedMode)
            {
                _loggedMode = Current.Mode;
                TaskbarHost.Log($"placement: {Current.Mode} x={Current.X} y={Current.Y}");
            }

            if (_stop.Token.WaitHandle.WaitOne(delay))
            {
                return;
            }
        }
    }

    private static IUIAutomationElement? FindDescendant(IUIAutomation automation, IntPtr trayWindow, string automationId)
    {
        var tray = automation.ElementFromHandle(trayWindow);
        if (tray is null)
        {
            return null;
        }

        using var condition = new ConditionScope(
            automation.CreatePropertyCondition(UIA_PropertyIds.UIA_AutomationIdPropertyId, automationId));
        return tray.FindFirst(TreeScope.TreeScope_Descendants, condition.Condition);
    }

    /// <summary>Taskbar strip = the screen bottom minus the work area.</summary>
    private static (int Top, int Height) TaskbarBand()
    {
        var work = default(RECT);
        var screenHeight = GetSystemMetrics(SM_CYSCREEN);
        if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0) || work.Bottom >= screenHeight)
        {
            var guess = DpiSetup.Px(48);
            return (screenHeight - guess, guess);
        }

        return (work.Bottom, screenHeight - work.Bottom);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    /// <summary>To release condition objects reliably (piled-up COM references make Explorer sluggish).</summary>
    private readonly struct ConditionScope(IUIAutomationCondition condition) : IDisposable
    {
        public IUIAutomationCondition Condition { get; } = condition;

        public void Dispose()
        {
            if (Condition is not null && Marshal.IsComObject(Condition))
            {
                Marshal.ReleaseComObject(Condition);
            }
        }
    }
}
