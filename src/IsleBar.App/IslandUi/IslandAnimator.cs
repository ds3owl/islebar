using System.Numerics;
using IsleBar.Core.Island;
using IsleBar.Core.Ui;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace IsleBar.App.IslandUi;

/// <summary>
/// Motion when the island changes — same as the desktop preview (search box B design decision card):
/// <list type="bullet">
/// <item>When the kind changes, the icon pops out on a spring (small → slight overshoot → settle).</item>
/// <item>New text rises from below slightly later, growing and sharpening (spring).</item>
/// <item>When a Claude/Codex task finishes, the pill's inner border blinks green twice, then stays lit while the notice is shown.</item>
/// </list>
/// Everything runs on the compositor (Composition), not the UI thread. Progress-only changes don't animate.
/// (09-30: the server draft put a ratio (0.5) into CenterPoint so it skewed to one side and clashed with the on/off indicator, so it was rebuilt)
/// </summary>
internal sealed class IslandAnimator
{
    private static readonly TimeSpan GlowTime = TimeSpan.FromMilliseconds(1920);
    private const float RiseFrom = 8f;

    private readonly FrameworkElement[] _bouncers;
    private readonly FrameworkElement _text;
    private readonly FrameworkElement _glow;
    private readonly FrameworkElement _pill;
    private readonly Compositor _compositor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    private ActivityKind? _lastKind;
    private string? _lastSource;

    /// <param name="bouncers">Things that bounce (the left icon slot, the island text slot).</param>
    /// <param name="text">The text slot that rises from below.</param>
    /// <param name="glow">The green border that flashes (transparent normally).</param>
    /// <param name="pill">The whole pill group (including background) — squeezes slightly and returns when a new item appears.</param>
    public IslandAnimator(FrameworkElement[] bouncers, FrameworkElement text, FrameworkElement glow, FrameworkElement pill)
    {
        _pill = pill;
        _bouncers = bouncers;
        _text = text;
        _glow = glow;
        _compositor = ElementCompositionPreview.GetElementVisual(text).Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(text, true);
        ElementCompositionPreview.GetElementVisual(glow).Opacity = 0f;
    }

    /// <summary>How the island changed (determined before drawing).</summary>
    public enum Change
    {
        /// <summary>Same item (only progress / remaining time changed) — no motion.</summary>
        None,

        /// <summary>An item appeared on an empty island — draw right away and pop out.</summary>
        Appeared,

        /// <summary>Changed to a different item — old content leaves first, then new content enters.</summary>
        Swapped,

        /// <summary>The item went away — after the content leaves, the input box quietly returns.</summary>
        Cleared,
    }

    /// <summary>
    /// Tells whether it's a new item (kind and source; for music, also the track title). Safe to call every tick — returns <see cref="Change.None"/> if unchanged.
    /// </summary>
    public Change Classify(IslandSnapshot snapshot)
    {
        var kind = snapshot.Primary?.Kind;
        // For music the source (app) stays the same when the track changes — must check the title so a new track also pops (measured on PC 09-30: skipping tracks didn't pop)
        var source = snapshot.Primary is { Kind: ActivityKind.Music } music
            ? music.SourcePath + "|" + music.Name
            : snapshot.Primary?.SourcePath;
        var hadThing = _lastKind is not null;
        var sameThing = kind == _lastKind && source == _lastSource;
        _lastKind = kind;
        _lastSource = source;
        return sameThing ? Change.None
            : kind is null ? Change.Cleared
            : hadThing ? Change.Swapped
            : Change.Appeared;
    }

    /// <summary>When new content enters: the icon pops out and the text rises slightly later.</summary>
    public void Enter()
    {
        Squish();
        Bounce();
        Rise();
    }

    /// <summary>
    /// The pill itself reacts: squeezes slightly for 0.09 s (0.965 wide, 0.93 tall) then springs back (user feedback 09-30: when the track changes
    /// the pill doesn't pop, only its contents move). The pill can't grow beyond the window size (it'd be clipped), so it squeezes and returns instead of growing —
    /// overshoot on return would clip the edges, so damping (0.9) allows almost no overshoot.
    /// </summary>
    private void Squish()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_pill);
        visual.CenterPoint = new Vector3((float)_pill.ActualWidth / 2, (float)_pill.ActualHeight / 2, 0f);
        var linear = _compositor.CreateLinearEasingFunction();
        var press = new Vector3(0.965f, 0.93f, 1f);
        const double pressSeconds = 0.09, response = 0.38, damping = 0.9;
        var back = SpringCurve.SettleTime(response, damping);
        var total = pressSeconds + back;
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0f, Vector3.One);
        animation.InsertKeyFrame((float)(pressSeconds / total), press, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.6f, 1f)));
        foreach (var (fraction, progress) in SpringCurve.Sample(response, damping).Skip(1))
        {
            var f = (float)((pressSeconds + (fraction * back)) / total);
            animation.InsertKeyFrame(f, Vector3.Lerp(press, Vector3.One, progress), linear);
        }

        animation.Duration = TimeSpan.FromSeconds(total);
        visual.StartAnimation(nameof(Visual.Scale), animation);
    }

    /// <summary>
    /// Old content leaves: fades, shrinks (0.92) and moves up slightly (−5). 0.12 s — shorter than the entrance
    /// (research: exits fast, entrances relaxed — Atoll/NotchKit values). Calls <paramref name="done"/> when finished.
    /// Previously old content vanished abruptly and only the new content moved (09-30 design decision M3).
    /// </summary>
    public void Exit(Action done)
    {
        var finished = false;
        void Finish()
        {
            if (!finished)
            {
                finished = true;
                done();
            }
        }

        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        foreach (var element in _bouncers)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0f);
            visual.StartAnimation(nameof(Visual.Opacity), Scalar(1f, 0f, ExitTime, ease));
            var shrink = _compositor.CreateVector3KeyFrameAnimation();
            shrink.InsertKeyFrame(1f, new Vector3(ExitScale, ExitScale, 1f), ease);
            shrink.Duration = ExitTime;
            visual.StartAnimation(nameof(Visual.Scale), shrink);
            try { visual.StartAnimation("Translation.Y", Scalar(0f, -5f, ExitTime, ease)); }
            catch (ArgumentException) { }   // Translation not enabled on this element yet — skip its slide, but never abort the exit (that left the swap flag stuck and froze the island — 09-30)
        }

        batch.End();
        batch.Completed += (_, _) => _ui.TryEnqueue(Finish);

        // Don't get stuck if the completed signal never arrives (element collapsed, etc.)
        var fallback = _ui.CreateTimer();
        fallback.Interval = ExitTime + TimeSpan.FromMilliseconds(120);
        fallback.IsRepeating = false;
        fallback.Tick += (_, _) => Finish();
        fallback.Start();
    }

    /// <summary>After the item goes away, quietly return to the input box look (transparent → opaque over 0.15 s, no pop).</summary>
    public void Restore()
    {
        foreach (var element in _bouncers)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation(nameof(Visual.Scale));
            try { visual.StopAnimation("Translation.Y"); } catch (ArgumentException) { }
            visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.StartAnimation(nameof(Visual.Opacity), Scalar(0f, 1f, TimeSpan.FromMilliseconds(150), null));
        }
    }

    private static readonly TimeSpan ExitTime = TimeSpan.FromMilliseconds(120);
    private const float ExitScale = 0.92f;

    private ScalarKeyFrameAnimation Scalar(float from, float to, TimeSpan duration, CompositionEasingFunction? ease)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, from);
        if (ease is null)
        {
            animation.InsertKeyFrame(1f, to);
        }
        else
        {
            animation.InsertKeyFrame(1f, to, ease);
        }

        animation.Duration = duration;
        return animation;
    }

    // ---- Baking the spring curve (Core's SpringCurve) into keyframes ----
    // 09-30 research: previously a WinUI spring (damping 0.42, Period 85 ms) wobbled several times — an exaggerated bounce (0.58).
    // Values: icon 0.45 s, bounce 0.3 (damping 0.7) = a single slight overshoot; text 0.4 s, bounce 0.15 (damping 0.85).
    private const double PopResponse = 0.45, PopDamping = 0.7;
    private const double RiseResponse = 0.4, RiseDamping = 0.85;

    private Vector3KeyFrameAnimation SpringVector(float from, float to, double response, double damping)
    {
        var linear = _compositor.CreateLinearEasingFunction();
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        foreach (var (fraction, progress) in SpringCurve.Sample(response, damping))
        {
            var v = from + ((to - from) * progress);
            animation.InsertKeyFrame(fraction, new Vector3(v, v, 1f), linear);
        }

        animation.Duration = TimeSpan.FromSeconds(SpringCurve.SettleTime(response, damping));
        return animation;
    }

    private ScalarKeyFrameAnimation SpringScalar(float from, float to, double response, double damping)
    {
        var linear = _compositor.CreateLinearEasingFunction();
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        foreach (var (fraction, progress) in SpringCurve.Sample(response, damping))
        {
            animation.InsertKeyFrame(fraction, from + ((to - from) * progress), linear);
        }

        animation.Duration = TimeSpan.FromSeconds(SpringCurve.SettleTime(response, damping));
        return animation;
    }

    /// <summary>The icon (album art) starts small, overshoots slightly once, then settles.</summary>
    private void Bounce()
    {
        var icon = _bouncers[0];
        var visual = ElementCompositionPreview.GetElementVisual(icon);
        visual.CenterPoint = new Vector3((float)icon.ActualWidth / 2, (float)icon.ActualHeight / 2, 0f);
        try { visual.StopAnimation("Translation.Y"); } catch (ArgumentException) { }   // Translation may not be enabled on this element; never let it throw (it froze the island timer — 09-30)
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.StartAnimation(nameof(Visual.Scale), SpringVector(0.5f, 1f, PopResponse, PopDamping));
        // the pop's fade takes over the icon's opacity, which ended the "working" breath for good (review 10-03) — pick it up after
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation(nameof(Visual.Opacity), Scalar(0f, 1f, TimeSpan.FromMilliseconds(120), null));
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (_breathing)
            {
                StartBreath(visual);
            }
        };
    }

    private static readonly TimeSpan TextDelay = TimeSpan.FromMilliseconds(70);

    /// <summary>Text starts 0.07 s after the icon, rising from below while growing and sharpening.</summary>
    private void Rise()
    {
        // Re-enable Translation before animating Translation.Y: it can be lost when the text slot's backing visual is rebuilt,
        // and animating a disabled property THREW inside the island tick, which killed the whole island timer — the pill and the
        // message card then froze on the previous item (root cause of the 'stale'/'frozen' notices, found 09-30).
        ElementCompositionPreview.SetIsTranslationEnabled(_text, true);
        var visual = ElementCompositionPreview.GetElementVisual(_text);
        // Grow from the left-center (icon side) — feels like the text unfolds out of the icon
        visual.CenterPoint = new Vector3(0f, (float)_text.ActualHeight / 2, 0f);

        var move = SpringScalar(RiseFrom, 0f, RiseResponse, RiseDamping);
        move.DelayTime = TextDelay;
        move.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        try { visual.StartAnimation("Translation.Y", move); } catch (ArgumentException) { }

        var grow = SpringVector(0.88f, 1f, RiseResponse, RiseDamping);   // scale the incoming text in more visibly (research #2: content cross-fades + scales ~0.8→1)
        grow.DelayTime = TextDelay;
        grow.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(nameof(Visual.Scale), grow);

        var fade = Scalar(0f, 1f, TimeSpan.FromMilliseconds(220), _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f)));
        fade.DelayTime = TextDelay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(nameof(Visual.Opacity), fade);
    }

    private bool _breathing;

    /// <summary>
    /// "Working" breathes: the lead mark slowly grows and fades (1 → 1.08, opacity 1 → 0.65, 1.8 s) while an agent works —
    /// a calm "in progress" signal. Stops as soon as the work is done.
    /// </summary>
    public void Breathe(bool on)
    {
        if (on == _breathing)
        {
            return;
        }

        _breathing = on;
        var icon = _bouncers[0];
        var visual = ElementCompositionPreview.GetElementVisual(icon);
        if (!on)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = 1f;
            return;
        }

        visual.CenterPoint = new Vector3((float)icon.ActualWidth / 2, (float)icon.ActualHeight / 2, 0f);
        StartBreath(visual);
    }

    private void StartBreath(Visual visual)
    {
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.37f, 0f), new Vector2(0.63f, 1f));
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 1f);
        fade.InsertKeyFrame(0.5f, 0.55f, ease);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(1800);
        fade.IterationBehavior = AnimationIterationBehavior.Forever;
        fade.DelayTime = TimeSpan.FromMilliseconds(600);   // after the entrance pop settles
        visual.StartAnimation("Opacity", fade);
    }

    private string? _glowKey;

    /// <summary>
    /// Border: if there's something to notify (key), light it in that color. A new notice blinks twice then stays lit;
    /// the same notice stays as-is; when there's nothing to notify, fade it out. Safe to call on every draw.
    /// </summary>
    public void UpdateGlow(string? key, Windows.UI.Color color)
    {
        if (key == _glowKey)
        {
            return;
        }

        var wasOn = _glowKey is not null;
        _glowKey = key;
        if (key is null)
        {
            if (wasOn)
            {
                GlowOff();
            }

            return;
        }

        if (_glow is Microsoft.UI.Xaml.Controls.Border border)
        {
            border.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        }

        Glow();
    }

    private void Glow()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_glow);
        var flash = _compositor.CreateScalarKeyFrameAnimation();
        // Same blink as the preview (on → dim → on again), then stays lit (user feedback 09-30: where did the border coloring go?)
        // Timing also matches the preview (@keyframes glow): 0.48 s on → 0.8 s dim → 0.64 s on again.
        // The old timing (0.2/0.26/0.26 s) was so fast the blink was barely visible (user feedback 09-30: the preview blinks once).
        flash.InsertKeyFrame(0f, 0f);
        flash.InsertKeyFrame(0.25f, 1f);
        flash.InsertKeyFrame(0.667f, 0.2f);
        flash.InsertKeyFrame(1f, 0.9f);
        flash.Duration = GlowTime;
        visual.StartAnimation(nameof(Visual.Opacity), flash);
    }

    private void GlowOff()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_glow);
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(300);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
    }
}
