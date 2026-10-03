using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsleBar.Core.Launch;

/// <summary>
/// Pre-accepts Claude Code's "Do you trust this folder?" prompt (Security guide).
///
/// Why: pressing Enter in the bar should run the question right away, but for an unseen folder this prompt appears first and blocks it.
/// In particular <b>Claude Code does not store trust for the home folder, so it appears every time</b> (checked 09-29:
/// the home entry in <c>~/.claude.json</c> stays false). So the default working folder is a dedicated folder under home,
/// and right before launching that folder is recorded as <c>projects.&lt;path&gt;.hasTrustDialogAccepted = true</c>.
///
/// Claude Code keeps rewriting this file, so <b>if it is already true it is left untouched</b> (written just once per folder);
/// all other keys are kept as-is and it is written via temp file → swap. Failures are ignored silently (the prompt just shows once).
/// </summary>
public static class FolderTrust
{
    /// <summary>Default working folder name (under home). Same as the Python version claude_bar.py.</summary>
    public const string DefaultFolderName = "ClaudeBar";

    /// <summary>Key in the shape <c>~/.claude.json</c> uses: backslash → slash.</summary>
    public static string KeyFor(string folder) =>
        Path.GetFullPath(folder).Replace(@"\", "/").TrimEnd('/');

    /// <summary>Records the folder as trusted. True if newly recorded.</summary>
    public static bool Ensure(string claudeJsonPath, string folder)
    {
        try
        {
            if (!File.Exists(claudeJsonPath))
            {
                return false;   // Claude Code has never been launched — do not create the file
            }

            var root = JsonNode.Parse(File.ReadAllText(claudeJsonPath)) as JsonObject;
            if (root is null)
            {
                return false;
            }

            if (root["projects"] is not JsonObject projects)
            {
                projects = new JsonObject();
                root["projects"] = projects;
            }

            var key = KeyFor(folder);
            if (projects[key] is not JsonObject project)
            {
                project = new JsonObject();
                projects[key] = project;
            }

            if (project["hasTrustDialogAccepted"]?.GetValueKind() == JsonValueKind.True)
            {
                return false;   // already trusted — do not touch the file
            }

            project["hasTrustDialogAccepted"] = true;

            var temp = claudeJsonPath + ".islebar.tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            File.Move(temp, claudeJsonPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Codex's key for a folder: full path, lower-cased (what Codex itself writes on Windows).</summary>
    public static string CodexKeyFor(string folder) =>
        Path.GetFullPath(folder).TrimEnd('\\', '/').ToLowerInvariant();

    /// <summary>
    /// Same for Codex: its "Do you trust the contents of this directory?" prompt stopped the first question from the bar in the
    /// default folder, and the pill showed nothing while it waited (found 10-01). Codex keeps trust in <c>~/.codex/config.toml</c>
    /// as <c>[projects.'c:\path'] trust_level = "trusted"</c>; that table is appended unless the folder already has one.
    /// Nothing else in the file is touched. True if newly recorded.
    /// </summary>
    public static bool EnsureCodex(string configTomlPath, string folder)
    {
        try
        {
            if (!File.Exists(configTomlPath))
            {
                return false;   // Codex has never been set up — do not create the file
            }

            var key = CodexKeyFor(folder);
            if (key.Contains('\''))
            {
                return false;   // can't be written as a TOML literal string — let Codex ask
            }

            var text = File.ReadAllText(configTomlPath);
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?m)^\s*projects\s*="))
            {
                return false;   // `projects = { ... }` inline — adding a [projects.'x'] table would break the file (code review 10-01)
            }

            var header = $"[projects.'{key}']";
            if (text.Contains(header, StringComparison.OrdinalIgnoreCase)
                || text.Contains($"[projects.\"{key.Replace(@"\", @"\\")}\"]", StringComparison.OrdinalIgnoreCase))
            {
                return false;   // already listed (trusted or not — the person's choice stands)
            }

            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var separator = text.Length == 0 || text.EndsWith('\n') ? string.Empty : newline;
            var updated = text + separator + newline + header + newline + "trust_level = \"trusted\"" + newline;

            var temp = configTomlPath + ".islebar.tmp";
            File.WriteAllText(temp, updated, new System.Text.UTF8Encoding(false));
            File.Move(temp, configTomlPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
