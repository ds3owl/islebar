using System.Text.Json;
using System.Text.Json.Nodes;
using IsleBar.Core.History;
using IsleBar.Core.Launch;
using IsleBar.Core.Localization;

namespace IsleBar.Core.Configuration;

/// <summary>
/// JSON ↔ <see cref="IsleBarSettings"/>. Shape checks are <b>per key</b> — if one key is bad,
/// the rest are kept and only the bad key is reset to default.
/// </summary>
public static class SettingsCodec
{
    // JSON key names (same as the Python version's claude_bar.json)
    private const string KeyLang = "lang";
    private const string KeyValues = "values";
    private const string KeyCodexValues = "codex_values";
    private const string KeyRecentFolders = "recent_folders";
    private const string KeyQuick = "quick";
    private const string KeyFolder = "folder";
    private const string KeyFiles = "files";
    private const string KeyWebEngine = "web_engine";
    private const string KeyModeOrder = "mode_order";
    private const string KeyModesOff = "modes_off";
    private const string KeyPomoFocus = "pomo_focus";
    private const string KeyPomoBreak = "pomo_break";
    private const string KeyPomoLong = "pomo_long";
    private const string KeyQuickTimers = "quick_timers";
    private const string KeyNativeSearchBox = "native_searchbox";
    private const string KeyAudioBars = "audio_bars";
    private const string KeyHistory = "history";
    private const string KeyAgent = "agent";
    private const string KeyModels = "models";
    private const string KeyCodexModels = "codex_models";
    private const string KeyModelsChecked = "models_checked";
    private const string KeyFlags = "flags";     // very old version
    private const string KeyNotifyPower = "notify_power";
    private const string KeyNotifyBluetooth = "notify_bluetooth";
    private const string KeyNotifyNetwork = "notify_network";
    private const string KeyNotifyFocus = "notify_focus";
    private const string KeyNotifyClipboard = "notify_clipboard";
    private const string KeyNotifyLoad = "notify_load";
    private const string KeyNotifyPrivacy = "notify_privacy";
    private const string KeyNotifyToasts = "notify_toasts";
    private const string KeyHideToastBanners = "hide_toast_banners";
    private const string KeyCheckUpdates = "check_updates";
    private const string KeyMuteChimeFullscreen = "mute_chime_fullscreen";
    private const string KeyCalendarIcs = "calendar_ics";
    private const string KeyDropCommand = "drop_command";
    private const string KeyStyleGlass = "style_glass";

    public static IsleBarSettings FromJson(JsonObject? raw)
    {
        var s = new IsleBarSettings();
        if (raw is null)
        {
            return s;
        }

        if (AsString(raw[KeyLang]) is { } lang && (lang == "auto" || LanguageCatalog.IsKnown(lang)))
        {
            s.Lang = lang;
        }

        if (AsString(raw[KeyAgent]) is { } agent && AgentKind.IsKnown(agent))
        {
            s.Agent = agent;
        }

        if (raw[KeyCodexModels] is JsonArray codexArr)
        {
            var codexModels = StringList(codexArr);
            s.CodexModels = codexModels.Count > 0 ? codexModels : [.. CodexProfile.Models];
        }

        if (AsString(raw[KeyFolder]) is { } folder)
        {
            s.Folder = folder;
        }

        if (AsString(raw[KeyWebEngine]) is { } engine && Search.WebSearch.IsKnown(engine))
        {
            s.WebEngine = engine;
        }

        if (AsBool(raw[KeyFiles]) is { } files)
        {
            s.Files = files;
        }

        s.NotifyPower = AsBool(raw[KeyNotifyPower]) ?? s.NotifyPower;
        s.NotifyBluetooth = AsBool(raw[KeyNotifyBluetooth]) ?? s.NotifyBluetooth;
        s.NotifyNetwork = AsBool(raw[KeyNotifyNetwork]) ?? s.NotifyNetwork;
        s.NotifyFocus = AsBool(raw[KeyNotifyFocus]) ?? s.NotifyFocus;
        s.NotifyClipboard = AsBool(raw[KeyNotifyClipboard]) ?? s.NotifyClipboard;
        s.NotifyLoad = AsBool(raw[KeyNotifyLoad]) ?? s.NotifyLoad;
        s.NotifyPrivacy = AsBool(raw[KeyNotifyPrivacy]) ?? s.NotifyPrivacy;
        s.NotifyToasts = AsBool(raw[KeyNotifyToasts]) ?? s.NotifyToasts;
        s.HideToastBanners = AsBool(raw[KeyHideToastBanners]) ?? s.HideToastBanners;
        s.CheckUpdates = AsBool(raw[KeyCheckUpdates]) ?? s.CheckUpdates;
        s.MuteChimeFullscreen = AsBool(raw[KeyMuteChimeFullscreen]) ?? s.MuteChimeFullscreen;
        if (AsString(raw[KeyCalendarIcs]) is { } ics)
        {
            s.CalendarIcs = ics.Trim();
        }

        if (AsString(raw[KeyDropCommand]) is { } drop)
        {
            s.DropCommand = drop.Trim();
        }

        s.StyleGlass = AsBool(raw[KeyStyleGlass]) ?? s.StyleGlass;

        if (AsDouble(raw[KeyModelsChecked]) is { } checkedAt && checkedAt >= 0 && !double.IsNaN(checkedAt))
        {
            s.ModelsChecked = checkedAt;
        }

        if (raw[KeyModels] is JsonArray modelArr)
        {
            var models = StringList(modelArr);
            s.Models = models.Count > 0 ? models : [.. LaunchOptionDefs.DefaultModels];
        }

        if (raw[KeyHistory] is JsonArray historyArr)
        {
            var history = StringList(historyArr);
            // even if an old file holds more than 51, keep only the most recent
            s.History = history.Count > QuestionHistory.Max
                ? history.GetRange(history.Count - QuestionHistory.Max, QuestionHistory.Max)
                : history;
        }

        s.NativeSearchBox = AsBool(raw[KeyNativeSearchBox]) ?? s.NativeSearchBox;
        s.AudioBars = AsBool(raw[KeyAudioBars]) ?? s.AudioBars;
        s.PomodoroFocus = Minutes(raw[KeyPomoFocus]) ?? s.PomodoroFocus;
        s.PomodoroBreak = Minutes(raw[KeyPomoBreak]) ?? s.PomodoroBreak;
        s.PomodoroLong = Minutes(raw[KeyPomoLong]) ?? s.PomodoroLong;
        if (raw[KeyQuickTimers] is JsonArray quickTimerArr)
        {
            s.QuickTimers = [.. quickTimerArr.Select(Minutes).OfType<int>().Distinct().Take(6)];
        }

        if (raw[KeyModesOff] is JsonArray offArr)
        {
            s.ModesOff = [.. StringList(offArr).Where(k => Localization.BarModes.Parse(k) is { } m && m != Localization.BarMode.Files).Distinct()];
        }

        if (raw[KeyModeOrder] is JsonArray orderArr)
        {
            s.ModeOrder = [.. Localization.BarModes.Normalize(StringList(orderArr)).Select(Localization.BarModes.Key)];
        }

        if (raw[KeyQuick] is JsonArray quickArr)
        {
            var quick = new List<string>();
            foreach (var name in StringList(quickArr))
            {
                if (LaunchOptionDefs.All.Contains(name) && !quick.Contains(name))
                {
                    quick.Add(name);
                }
            }

            s.Quick = quick;   // an empty list is meaningful too (all pins removed)
        }

        if (raw[KeyRecentFolders] is JsonArray recentArr)
        {
            s.RecentFolders = RecentFolders.Normalize(StringList(recentArr));
        }

        if (raw[KeyValues] is JsonObject values)
        {
            ReadValues(values, s.Values, s.Models);
        }

        if (raw[KeyCodexValues] is JsonObject codexValues)
        {
            ReadValues(codexValues, s.CodexValues, s.CodexModels);
        }

        // migrate the old "flags" (filled in here rather than read first, so that values win)
        if (raw[KeyValues] is not JsonObject && raw[KeyFlags] is JsonArray flags)
        {
            foreach (var flag in StringList(flags))
            {
                if (flag == "--remote-control") s.Values.Rc = true;
                if (flag == "--resume") s.Values.Session = "resume";
            }
        }

        return s;
    }

    private static void ReadValues(JsonObject values, LaunchOptions target, IReadOnlyList<string> models)
    {
        if (Choice(values[LaunchOptionDefs.Session], LaunchOptionDefs.SessionChoices) is { } session)
        {
            target.Session = session;
        }

        if (Choice(values[LaunchOptionDefs.Effort], LaunchOptionDefs.EffortChoices) is { } effort)
        {
            target.Effort = effort;
        }

        if (Choice(values[LaunchOptionDefs.Perm], LaunchOptionDefs.PermChoices) is { } perm)
        {
            target.Perm = perm;
        }

        // the alias list changes, so allow "default" + the current list + aliases not yet in the list (any string will do)
        if (AsString(values[LaunchOptionDefs.Model]) is { Length: > 0 } model)
        {
            target.Model = model == "default" || models.Contains(model) || IsPlausibleAlias(model)
                ? model
                : "default";
        }

        if (AsBool(values[LaunchOptionDefs.Rc]) is { } rc)
        {
            target.Rc = rc;
        }
    }

    /// <summary>Whether it looks like a model alias (lowercase, digits, dots, hyphens, brackets). Keeps aliases saved before the list was refreshed.</summary>
    private static bool IsPlausibleAlias(string value)
        => value.Length <= 40 && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '[' or ']');

    /// <summary>Overwrites only known keys. Unknown keys are left untouched and thus preserved.</summary>
    public static void Apply(IsleBarSettings s, JsonObject raw)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(raw);

        raw[KeyLang] = s.Lang;
        raw[KeyAgent] = s.Agent;
        raw[KeyFolder] = s.Folder;
        raw[KeyFiles] = s.Files;
        raw[KeyWebEngine] = s.WebEngine;
        raw[KeyModeOrder] = ToArray(s.ModeOrder);
        raw[KeyModesOff] = ToArray(s.ModesOff);
        raw[KeyNativeSearchBox] = s.NativeSearchBox;
        raw[KeyAudioBars] = s.AudioBars;
        raw[KeyPomoFocus] = s.PomodoroFocus;
        raw[KeyPomoBreak] = s.PomodoroBreak;
        raw[KeyPomoLong] = s.PomodoroLong;
        raw[KeyQuickTimers] = new JsonArray([.. s.QuickTimers.Select(m => (JsonNode)m)]);
        raw[KeyModelsChecked] = s.ModelsChecked;
        raw[KeyQuick] = ToArray(s.Quick);
        raw[KeyHistory] = ToArray(s.History);
        raw[KeyModels] = ToArray(s.Models);
        raw[KeyCodexModels] = ToArray(s.CodexModels);
        raw[KeyRecentFolders] = ToArray(s.RecentFolders);
        raw[KeyValues] = ValuesJson(s.Values);
        raw[KeyCodexValues] = ValuesJson(s.CodexValues);
        raw[KeyNotifyPower] = s.NotifyPower;
        raw[KeyNotifyBluetooth] = s.NotifyBluetooth;
        raw[KeyNotifyNetwork] = s.NotifyNetwork;
        raw[KeyNotifyFocus] = s.NotifyFocus;
        raw[KeyNotifyClipboard] = s.NotifyClipboard;
        raw[KeyNotifyLoad] = s.NotifyLoad;
        raw[KeyNotifyPrivacy] = s.NotifyPrivacy;
        raw[KeyNotifyToasts] = s.NotifyToasts;
        raw[KeyHideToastBanners] = s.HideToastBanners;
        raw[KeyCheckUpdates] = s.CheckUpdates;
        raw[KeyMuteChimeFullscreen] = s.MuteChimeFullscreen;
        raw[KeyCalendarIcs] = s.CalendarIcs;
        raw[KeyDropCommand] = s.DropCommand;
        raw[KeyStyleGlass] = s.StyleGlass;
        raw.Remove("style_black");     // removed setting (09-30)
        raw.Remove("advanced");        // removed setting (10-01)
        raw.Remove("drop_transfer");   // removed setting (10-01, the attach/transfer toggle — behaviour now decided by whether drop_command is set)
        raw.Remove(KeyFlags);   // migrated, so remove it
    }

    private static JsonObject ValuesJson(LaunchOptions v) => new()
    {
        [LaunchOptionDefs.Session] = v.Session,
        [LaunchOptionDefs.Model] = v.Model,
        [LaunchOptionDefs.Effort] = v.Effort,
        [LaunchOptionDefs.Perm] = v.Perm,
        [LaunchOptionDefs.Rc] = v.Rc,
    };

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
        {
            arr.Add((JsonNode)item);
        }

        return arr;
    }

    private static List<string> StringList(JsonArray arr)
    {
        var list = new List<string>(arr.Count);
        foreach (var node in arr)
        {
            if (AsString(node) is { Length: > 0 } text)
            {
                list.Add(text);
            }
        }

        return list;
    }

    private static string? Choice(JsonNode? node, IReadOnlyList<string> choices)
        => AsString(node) is { } text && choices.Contains(text) ? text : null;

    // ---- Return a value only when the type matches exactly. "true" (a string) or 1 is not a bool. ----

    private static string? AsString(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? AsBool(JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == JsonValueKind.True ? true
            : node is JsonValue v2 && v2.GetValueKind() == JsonValueKind.False ? false
            : null;

    private static double? AsDouble(JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) ? d : null;

    /// <summary>Minute value (1–600). Null if not a number or out of range.</summary>
    private static int? Minutes(JsonNode? node)
    {
        try
        {
            var value = node?.GetValue<int>();
            return value is >= 1 and <= 600 ? value : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
