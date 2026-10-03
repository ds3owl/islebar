using System.Text.Json;

namespace IsleBar.Core.Island;

/// <summary>
/// Lines of an agent's session record (Claude Code's transcript, Codex's rollout — both JSON lines) that the bar acts on and
/// no hook reports.
/// </summary>
public static class AgentRecord
{
    /// <summary>
    /// The line's own time (the top-level <c>timestamp</c> both agents write), or null. Lets a record read from the middle be
    /// limited to what happened in the current turn: an old failure or interrupt further up must not count again (review 10-03).
    /// </summary>
    public static DateTimeOffset? LineTime(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.IndexOf("\"timestamp\"", StringComparison.Ordinal) < 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("timestamp", out var t)
                   && t.ValueKind == JsonValueKind.String
                   && DateTimeOffset.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Claude Code stopped by the user (Esc, or answering a permission prompt with Esc): it fires no Stop hook then, so the pill
    /// kept saying "working" for hours (review 10-03). The transcript gets a user line whose whole text is
    /// "[Request interrupted by user]" or "[Request interrupted by user for tool use]". Subagent (sidechain) lines don't count.
    /// </summary>
    public static bool IsClaudeInterrupt(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.IndexOf("[Request interrupted by user", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "user"
                || (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content))
            {
                return false;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return IsMarker(content.GetString());
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("type", out var pt) && pt.ValueKind == JsonValueKind.String && pt.GetString() == "text"
                    && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    && IsMarker(text.GetString()))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A prompt the user typed (a <c>user</c> line that is neither an interrupt marker nor a tool result). After an interrupt the
    /// next prompt follows within a tenth of a second — an interrupt with a prompt after it belongs to the turn before (review 10-03).
    /// </summary>
    public static bool IsClaudePrompt(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.IndexOf("\"user\"", StringComparison.Ordinal) < 0
            || line.IndexOf("tool_result", StringComparison.Ordinal) >= 0 || IsClaudeInterrupt(line)
            // what slash commands echo (/cost, /model …) and compact summaries are user lines too, but nobody typed a prompt
            || line.Contains("<command-name>", StringComparison.Ordinal) || line.Contains("<local-command-", StringComparison.Ordinal)
            || line.Contains("<bash-", StringComparison.Ordinal) || line.Contains("\"isCompactSummary\":true", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "user"
                   && !(root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
                   && !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
                   && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                   && message.TryGetProperty("content", out var content)
                   && content.ValueKind is JsonValueKind.String or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsMarker(string? text)
        => text is { Length: <= 64 } t && t.StartsWith("[Request interrupted by user", StringComparison.Ordinal) && t.EndsWith(']');
}
