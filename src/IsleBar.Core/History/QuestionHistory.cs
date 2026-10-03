namespace IsleBar.Core.History;

/// <summary>Handles the recent-question list. The list is ordered <b>oldest → newest</b>.</summary>
public static class QuestionHistory
{
    /// <summary>How many to remember.</summary>
    public const int Max = 50;

    /// <summary>
    /// Adds a question. If the same question already exists it is <b>removed and appended at the end</b> (dedupe + recency order).
    /// Empty questions are not added. Always returns a new list.
    /// </summary>
    public static List<string> Add(IEnumerable<string> history, string? question)
    {
        ArgumentNullException.ThrowIfNull(history);
        var list = new List<string>(history);
        if (string.IsNullOrWhiteSpace(question))
        {
            return Trim(list);
        }

        list.RemoveAll(h => string.Equals(h, question, StringComparison.Ordinal));
        list.Add(question);
        return Trim(list);
    }

    private static List<string> Trim(List<string> list)
    {
        if (list.Count > Max)
        {
            list.RemoveRange(0, list.Count - Max);
        }

        return list;
    }
}
