using System.Text.Json;
using System.Text.Json.Nodes;
using IsleBar.Core.Launch;

namespace IsleBar.Core.Island;

/// <summary>An instruction to write or clear an island state. If <see cref="Write"/> is null, clear it.</summary>
/// <param name="OnlyOver">If set, apply only while the session's current state is of this kind (otherwise leave it alone).</param>
/// <param name="FallbackName">Name to use when <see cref="Write"/> has none and the session's current state has none either.</param>
public sealed record HookAction(string Id, ActivityState? Write, ActivityKind? OnlyOver = null, string? FallbackName = null,
    ActivityKind? OrOver = null);

/// <summary>
/// Turns notifications sent by agents into island items. Used by <c>islebar hook</c>.
/// <list type="bullet">
/// <item>Claude Code hooks (JSON on stdin): <c>Notification</c> = permission prompt / waiting for input → 🔔,
/// <c>Stop</c> = answer finished → ✅, <c>SessionEnd</c> = clear.</item>
/// <item>Codex <c>notify</c> (JSON as the last argument): <c>agent-turn-complete</c> → ✅.</item>
/// </list>
/// One item per session (<c>agent_first8charsOfSession</c>) — done overwrites the permission prompt so both never linger together.
/// Anything unknown gives null (do nothing). <b>A hook must never block the agent</b>, so no exceptions are thrown.
/// </summary>
public static class AgentHook
{
    /// <param name="projectDir">
    /// <c>CLAUDE_PROJECT_DIR</c> that Claude Code passes to hooks (the folder the session started in). Used if present —
    /// <c>cwd</c> changes when the folder is switched within the session, which showed odd things like "Claude done · memory" (user feedback 09-30).
    /// </param>
    /// <param name="titleOf">
    /// Transcript path → session name (terminal tab name). Used instead of the folder name if present (user feedback 09-30: show the session name instead).
    /// Done notices show no folder either when there is no name (user feedback: no need to show the folder name on completion).
    /// </param>
    /// <param name="text">Strings for the usage-limit notice (the bar's language); English if not given.</param>
    public static HookAction? FromClaude(string? json, string? projectDir = null, Func<string?, string?>? titleOf = null,
        Localization.LanguageStrings? text = null)
    {
        if (Parse(json) is not { } root)
        {
            return null;
        }

        var id = IdFor(AgentKind.Claude, Text(root, "session_id"));
        var folder = FolderName(string.IsNullOrWhiteSpace(projectDir) ? Text(root, "cwd") : projectDir);
        var eventName = Text(root, "hook_event_name");
        // The session title comes from the transcript (a read of its tail) — skip it for PostToolUse, which fires on every tool
        // call and only matters over a pending permission state (the CLI then keeps that state's name). (code review 10-01)
        var title = eventName is "PostToolUse" or "PostToolUseFailure" ? null : titleOf?.Invoke(Text(root, "transcript_path"));
        return eventName switch
        {
            // ignore "waiting for input" (after a period without input) — the done mark (✓) is already up, and the long English sentence
            // was truncated like "Claude Claude is waitin…" (user feedback 09-29). Only permission prompts are raised.
            "Notification" when IsIdle(root) || !NeedsAnswer(root) => null,
            "Notification" => new HookAction(id, new ActivityState
            {
                RawKind = ActivityState.KindAgentPermission,
                Agent = AgentKind.Claude,
                Name = title ?? folder,   // the window needing an answer must be identifiable, so fall back to the folder if there is no name
                State = "run",
                Transcript = Text(root, "transcript_path"),   // the bar watches it for an Esc (no hook fires then — review 10-03)
            }),
            "UserPromptSubmit" => new HookAction(id, new ActivityState
            {
                RawKind = ActivityState.KindAgentWorking,
                Agent = AgentKind.Claude,
                Name = title,
                State = "run",
                T0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                Transcript = Text(root, "transcript_path"),
            }),
            // Esc while a tool ran: Claude Code reports the tool as failed with is_interrupt — the whole turn has stopped, so the
            // pill clears. Turning it into "working" instead left it spinning until the session ended (review 10-03).
            "PostToolUseFailure" when IsInterrupt(root) => new HookAction(id, null, OnlyOver: ActivityKind.AgentWorking, OrOver: ActivityKind.AgentPermission),
            // A permission prompt / question was answered: the tool then runs and PostToolUse (or PostToolUseFailure) arrives —
            // turn the orange "needs an answer" back into "working" so the border doesn't linger until the whole turn ends
            // (user 10-01). Only over a permission state, so the many ordinary tool calls change nothing.
            "PostToolUse" or "PostToolUseFailure" => new HookAction(id, new ActivityState
            {
                RawKind = ActivityState.KindAgentWorking,
                Agent = AgentKind.Claude,
                Name = title,
                State = "run",
                T0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                Transcript = Text(root, "transcript_path"),
            }, OnlyOver: ActivityKind.AgentPermission),
            "Stop" => new HookAction(id, Done(AgentKind.Claude, title)),
            // the turn failed: the usage limit (red until it resets) or another error (login, billing, busy servers…) — user 10-03
            "StopFailure" => new HookAction(id, AgentNotices.ClaudeReason(Text(root, "error_type")) is { } reason
                ? AgentNotices.Stopped(AgentKind.Claude, reason, text, DateTimeOffset.UtcNow)
                : AgentNotices.UsageLimit(AgentKind.Claude, ResetTime(root), text, DateTimeOffset.UtcNow)),
            "SessionEnd" => new HookAction(id, null),
            _ => null,
        };
    }

    /// <summary>When Claude Code's usage limit lifts (StopFailure's <c>rate_limit.reset_time</c>, Unix seconds). Null if not given.</summary>
    private static DateTimeOffset? ResetTime(JsonObject root)
        => (root["rate_limit"] as JsonObject)?["reset_time"] is JsonValue v && v.TryGetValue(out long epoch)
            ? AgentUsage.FromUnixTime(epoch)
            : null;

    /// <summary>
    /// Codex lifecycle hooks (Codex 0.15x+, JSON on stdin, same shape as Claude's): <c>UserPromptSubmit</c> = working,
    /// <c>PermissionRequest</c> = needs an answer (orange), <c>PostToolUse</c> = answered → working again, <c>Stop</c> = done,
    /// <c>SessionEnd</c> = clear. The legacy <c>notify</c> only ever said "done", so Codex had no working/orange light (user 10-01).
    /// </summary>
    public static HookAction? FromCodexHook(string? json)
    {
        if (Parse(json) is not { } root)
        {
            return null;
        }

        var id = IdFor(AgentKind.Codex, Text(root, "session_id"));
        var folder = FolderName(Text(root, "cwd"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        // Codex has no session title like Claude's, so the prompt is the name ("Codex working · Fix the login bug…"); the later
        // events carry no prompt and keep that name (the CLI copies it over), falling back to the folder (user 10-01: same feel as Claude)
        return Text(root, "hook_event_name") switch
        {
            "UserPromptSubmit" => new HookAction(id, new ActivityState
            {
                RawKind = ActivityState.KindAgentWorking, Agent = AgentKind.Codex, Name = PromptName(Text(root, "prompt")) ?? folder, State = "run", T0 = now,
                Transcript = Text(root, "transcript_path"),
            }),
            "PermissionRequest" => new HookAction(id, new ActivityState
            {
                RawKind = ActivityState.KindAgentPermission, Agent = AgentKind.Codex, State = "run",
            }, FallbackName: folder),
            "PostToolUse" => new HookAction(id, new ActivityState
            {
                RawKind = ActivityState.KindAgentWorking, Agent = AgentKind.Codex, State = "run", T0 = now,
            }, OnlyOver: ActivityKind.AgentPermission, FallbackName: folder),
            "Stop" => new HookAction(id, Done(AgentKind.Codex, null), FallbackName: folder),
            "SessionEnd" => new HookAction(id, null),
            _ => null,
        };
    }

    /// <summary>A prompt shortened to a pill-sized name: first line, collapsed spaces, at most 40 characters. Null if empty.</summary>
    public static string? PromptName(string? prompt)
    {
        var line = (prompt ?? string.Empty).Split('\n')[0].Trim();
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0)
        {
            return null;
        }

        if (line.Length <= 40)
        {
            return line;
        }

        var cut = char.IsHighSurrogate(line[38]) ? 38 : 39;   // never split an emoji in half
        return line[..cut].TrimEnd() + "…";
    }

    public static HookAction? FromCodex(string? json)
    {
        if (Parse(json) is not { } root || Text(root, "type") != "agent-turn-complete")
        {
            return null;
        }

        var id = IdFor(AgentKind.Codex, Text(root, "thread-id") ?? Text(root, "turn-id"));
        return new HookAction(id, Done(AgentKind.Codex, FolderName(Text(root, "cwd"))));
    }

    /// <summary>
    /// Whether a Notification asks the person something. Newer Claude Code versions say which kind it is — only a permission prompt or
    /// a question (elicitation) needs an answer; others (e.g. auth_success) would have left a 6-hour orange (code review 10-01).
    /// Without the field (older versions) every non-idle notification counts, as before.
    /// </summary>
    private static bool NeedsAnswer(JsonObject root)
        => Text(root, "notification_type") is not { } type || type is "permission_prompt" or "elicitation_dialog";

    private static bool IsInterrupt(JsonObject root)
        => root["is_interrupt"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Whether this is a waiting-for-input notice. Newer versions use notification_type, older ones the message text.</summary>
    private static bool IsIdle(JsonObject root)
        => Text(root, "notification_type") is { } type
            ? type == "idle_prompt"
            : (Text(root, "message") ?? string.Empty).Contains("waiting for your input", StringComparison.OrdinalIgnoreCase);

    private static ActivityState Done(string agent, string? folder) => new()
    {
        RawKind = ActivityState.KindAgentDone,
        Agent = agent,
        Name = folder,
        State = "done",
    };

    /// <summary>An id usable as a file name. Just the agent name if there is no session id.</summary>
    public static string IdFor(string agent, string? session)
    {
        var clean = new string((session ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
        return clean.Length == 0 ? $"{agent}_agent" : $"{agent}_{clean}";
    }

    /// <summary>Last component of the working folder (what the island shows). Null if absent.</summary>
    public static string? FolderName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        var trimmed = cwd.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length == 0 ? trimmed : name;
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject root, string key)
        => root[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
