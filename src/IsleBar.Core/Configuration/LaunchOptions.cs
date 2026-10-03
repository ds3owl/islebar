namespace IsleBar.Core.Configuration;

/// <summary>
/// Launch option values. String values must be one of the choices in <see cref="LaunchOptionDefs"/>.
/// Defaults match the Python version's <c>DEFAULTS</c>, except permissions: a fresh install starts on "auto" (user 10-01 —
/// public builds must not skip every permission by default; the owner's own settings keep "bypass").
/// </summary>
public sealed class LaunchOptions
{
    public string Session { get; set; } = "new";
    public string Model { get; set; } = "default";
    public string Effort { get; set; } = "default";
    public string Perm { get; set; } = "auto";
    public bool Rc { get; set; }

    public LaunchOptions Clone() => new()
    {
        Session = Session, Model = Model, Effort = Effort, Perm = Perm, Rc = Rc,
    };
}

/// <summary>Option kinds and choices. Model choices grow with the alias list.</summary>
public static class LaunchOptionDefs
{
    public const string Session = "session";
    public const string Model = "model";
    public const string Effort = "effort";
    public const string Perm = "perm";
    public const string Rc = "rc";

    public static readonly IReadOnlyList<string> SessionChoices = ["new", "continue", "resume"];
    public static readonly IReadOnlyList<string> EffortChoices = ["default", "low", "medium", "high", "max"];
    public static readonly IReadOnlyList<string> PermChoices = ["bypass", "auto", "plan", "ask"];

    /// <summary>Default used while there is no model alias list yet.</summary>
    public static readonly IReadOnlyList<string> DefaultModels = ["opus", "sonnet", "haiku", "fable"];

    /// <summary>Options pinned to the quick-launch menu initially.</summary>
    public static readonly IReadOnlyList<string> DefaultQuick = [Session, Model, Rc];

    /// <summary>All pinnable options (order = menu order).</summary>
    public static readonly IReadOnlyList<string> All = [Session, Model, Effort, Perm, Rc];

    public static IReadOnlyList<string> ModelChoices(IReadOnlyList<string> models)
        => ["default", .. models];
}
