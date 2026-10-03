using System.Numerics;
using IsleBar.Core.Ui;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace IsleBar.App.IslandUi;

/// <summary>
/// Button press feedback (09-30 design M5): sinks in on press (0.86, 0.08 s) and springs back with a slight overshoot on release.
/// "What can be pressed responds to the hand". The button consumes the press first, so we subscribe with handledEventsToo.
/// </summary>
internal static class PressFeedback
{
    private const float Pressed = 0.86f;

    /// <summary>The button currently pressed (during animation Visual.Scale differs from the on-screen value, so we track it separately).</summary>
    private static readonly HashSet<UIElement> Down = [];

    public static void Attach(UIElement element)
    {
        if (element is Microsoft.UI.Xaml.Controls.Button button)
        {
            // Motion signals the press — turn off the default pressed background (gray box). Leave the hover background as-is
            var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Resources["ButtonBackgroundPressed"] = clear;
            button.Resources["ButtonBorderBrushPressed"] = clear;
        }

        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Press(element)), handledEventsToo: true);
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Release(element)), handledEventsToo: true);
        element.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => Release(element)), handledEventsToo: true);
        element.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => Release(element)), handledEventsToo: true);
    }

    private static Visual Prepare(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (element is FrameworkElement fe)
        {
            visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2, (float)fe.ActualHeight / 2, 0f);
        }

        return visual;
    }

    private static void Press(UIElement element)
    {
        Down.Add(element);
        var visual = Prepare(element);
        var c = visual.Compositor;
        var down = c.CreateVector3KeyFrameAnimation();
        down.InsertKeyFrame(1f, new Vector3(Pressed, Pressed, 1f), c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f)));
        down.Duration = TimeSpan.FromMilliseconds(80);
        visual.StartAnimation(nameof(Visual.Scale), down);
    }

    private static void Release(UIElement element)
    {
        if (!Down.Remove(element))
        {
            return;   // never pressed (just passed over)
        }

        var visual = Prepare(element);
        var c = visual.Compositor;
        const float from = Pressed;
        var linear = c.CreateLinearEasingFunction();
        var up = c.CreateVector3KeyFrameAnimation();
        foreach (var (fraction, progress) in SpringCurve.Sample(0.35, 0.55))
        {
            var v = from + ((1f - from) * progress);
            up.InsertKeyFrame(fraction, new Vector3(v, v, 1f), linear);
        }

        up.Duration = TimeSpan.FromSeconds(SpringCurve.SettleTime(0.35, 0.55));
        visual.StartAnimation(nameof(Visual.Scale), up);
    }
}
