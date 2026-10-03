using IsleBar.Core.Configuration;
using IsleBar.Core.Launch;
using IsleBar.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace IsleBar.App.Options;

/// <summary>
/// One launch-option row (title + toggle or segmented buttons). Shared by the quick options window and the settings window.
/// Port of the Python version's <c>option_row</c>, <c>make_seg</c>, <c>make_toggle</c> to stock WinUI controls.
/// </summary>
internal static class OptionControls
{
    /// <summary>Whether the current agent uses this option (if not, it's removed from the screen).</summary>
    public static bool IsShown(IsleBarSettings settings, string key)
        => AgentProfiles.Get(settings.Agent).Supports(key);

    /// <summary>Builds one option row. Calls <paramref name="changed"/> when the value changes.</summary>
    public static FrameworkElement Row(IsleBarSettings settings, LanguageStrings text, string key, bool compact, Action changed)
    {
        // The title takes its own width (Auto) so it isn't clipped; the control sticks to the right edge — window width is the sum of the two
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = text.TitleFor(key),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(title);

        FrameworkElement control = key == LaunchOptionDefs.Rc
            ? Toggle(settings, changed)
            : Segments(settings, text, key, compact, changed);
        control.HorizontalAlignment = HorizontalAlignment.Right;
        // Name the switch after its row so screen readers (and UI Automation) say what it is — it had no name at all (10-01)
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, title.Text);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static FrameworkElement Toggle(IsleBarSettings settings, Action changed)
    {
        var values = settings.ValuesFor(settings.Agent);
        var toggle = new ToggleSwitch
        {
            IsOn = values.Rc,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        toggle.Toggled += (_, _) =>
        {
            values.Rc = toggle.IsOn;
            changed();
        };
        return toggle;
    }

    /// <summary>Windows 11-style segmented buttons. Only the selected one is pressed in the accent color.</summary>
    private static FrameworkElement Segments(IsleBarSettings settings, LanguageStrings text, string key, bool compact, Action changed)
    {
        var values = settings.ValuesFor(settings.Agent);
        var (choices, labels) = ChoicesFor(settings, text, key);
        if (choices.Count > 5)
        {
            // With many choices (Codex model IDs, etc.) the button row overflows the window → use a dropdown
            var combo = new ComboBox { MinWidth = 150 };
            foreach (var label in labels)
            {
                combo.Items.Add(label);
            }

            combo.SelectedIndex = Math.Max(0, choices.ToList().IndexOf(Get(values, key)));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0)
                {
                    Set(values, key, choices[combo.SelectedIndex]);
                    changed();
                }
            };
            return combo;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var buttons = new List<ToggleButton>();

        void Paint()
        {
            var current = Get(values, key);
            for (var i = 0; i < buttons.Count; i++)
            {
                buttons[i].IsChecked = choices[i] == current;
            }
        }

        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var button = new ToggleButton
            {
                Content = labels[i],
                FontSize = compact ? 12 : 13,
                Padding = compact ? new Thickness(7, 2, 7, 3) : new Thickness(9, 3, 9, 4),
                MinWidth = 0,
                MinHeight = 0,
            };
            button.Click += (_, _) =>
            {
                Set(values, key, choice);
                Paint();
                changed();
            };
            buttons.Add(button);
            panel.Children.Add(button);
        }

        Paint();
        return panel;
    }

    private static (IReadOnlyList<string> Choices, IReadOnlyList<string> Labels) ChoicesFor(
        IsleBarSettings settings, LanguageStrings text, string key)
    {
        switch (key)
        {
            case LaunchOptionDefs.Model:
            {
                var models = settings.ModelsFor(settings.Agent);
                var choices = LaunchOptionDefs.ModelChoices(models);
                // Codex model IDs (gpt-5.6-sol etc.) look odd with just the first letter capitalized, so use them as-is
                IReadOnlyList<string> labels = settings.Agent == AgentKind.Codex
                    ? [text.Model.Choices[0], .. models]
                    : text.ModelFor(models).Choices;
                return (choices, labels);
            }

            case LaunchOptionDefs.Session:
                return (LaunchOptionDefs.SessionChoices, text.Session.Choices);
            case LaunchOptionDefs.Effort:
                return (LaunchOptionDefs.EffortChoices, text.Effort.Choices);
            default:
                return (LaunchOptionDefs.PermChoices, text.Perm.Choices);
        }
    }

    private static string Get(LaunchOptions values, string key) => key switch
    {
        LaunchOptionDefs.Session => values.Session,
        LaunchOptionDefs.Model => values.Model,
        LaunchOptionDefs.Effort => values.Effort,
        _ => values.Perm,
    };

    private static void Set(LaunchOptions values, string key, string value)
    {
        switch (key)
        {
            case LaunchOptionDefs.Session: values.Session = value; break;
            case LaunchOptionDefs.Model: values.Model = value; break;
            case LaunchOptionDefs.Effort: values.Effort = value; break;
            default: values.Perm = value; break;
        }
    }
}
