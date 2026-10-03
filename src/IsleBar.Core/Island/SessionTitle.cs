using System.Text.Json;

namespace IsleBar.Core.Island;

/// <summary>
/// Claude Code session name (shown on the terminal tab). The transcript (JSONL) accumulates <c>{"type":"custom-title","customTitle":…}</c>
/// (a name set with <c>/rename</c>) or <c>{"type":"ai-title","aiTitle":…}</c> (auto name).
/// Used in island notices instead of the folder name (user feedback 09-30: rather show the session name).
/// Transcript files grow to tens of MB, so only <b>the tail</b> is read.
/// </summary>
public static class SessionTitle
{
    /// <summary>Read only this much from the end (the name line is rewritten almost every turn, so this is enough).</summary>
    public const int TailBytes = 512 * 1024;

    /// <summary>Finds the session name at the end of the transcript. A human-given name wins over the auto name. Null if none.</summary>
    public static string? FromTranscript(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return FromTail(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Finds the name in a transcript chunk (lines). The first line may be cut mid-way, so broken lines are skipped.</summary>
    public static string? FromTail(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string? custom = null, ai = null;
        foreach (var line in text.Split('\n'))
        {
            // quick filter — most lines are conversation content and need not be parsed
            if (!line.Contains("-title\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                switch (type.GetString())
                {
                    case "custom-title" when Text(root, "customTitle") is { } c:
                        custom = c;
                        break;
                    case "ai-title" when Text(root, "aiTitle") is { } a:
                        ai = a;
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }

        return custom ?? ai;
    }

    private static string? Text(JsonElement root, string key)
        => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } s ? s.Trim() : null;
}
