namespace IsleBar.Core.Island;

/// <summary>
/// The one thing to show on the island now, plus a "there is more" marker.
/// </summary>
/// <param name="Primary">State to show. Null when nothing is going on.</param>
/// <param name="ExtraCount">How many are hidden behind it. 0 means no dot is drawn.</param>
public sealed record IslandSnapshot(ActivityState? Primary, int ExtraCount)
{
    /// <summary>Nothing going on (the usual search bar).</summary>
    public static readonly IslandSnapshot Empty = new(null, 0);

    /// <summary>Whether there is anything to show.</summary>
    public bool IsActive => Primary is not null;

    /// <summary>Whether the small dot should be drawn.</summary>
    public bool HasMore => ExtraCount > 0;
}

/// <summary>
/// Picks one when several things happen at once.
/// Priority follows the value order of <see cref="ActivityKind"/>; ties go to <b>the most recently changed</b>.
/// (decision 09-29: instead of opening several windows, show the single most urgent one + a small dot at the end.)
/// </summary>
public static class IslandSelector
{
    public static IslandSnapshot Select(IReadOnlyList<ActivityState> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        if (activities.Count == 0)
        {
            return IslandSnapshot.Empty;
        }

        var best = activities[0];
        for (var i = 1; i < activities.Count; i++)
        {
            if (IsMoreUrgent(activities[i], best))
            {
                best = activities[i];
            }
        }

        return new IslandSnapshot(best, activities.Count - 1);
    }

    private static bool IsMoreUrgent(ActivityState candidate, ActivityState current)
    {
        var byKind = candidate.Kind.CompareTo(current.Kind);
        if (byKind != 0)
        {
            return byKind < 0;                                  // smaller value = more urgent
        }

        return candidate.UpdatedAt > current.UpdatedAt;          // on a tie, the most recent
    }
}
