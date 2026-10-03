namespace IsleBar.Core.Localization;

/// <summary>Title and choice names of a choice-type launch option. No description text (extra verbiage) — user's decision.</summary>
/// <param name="Title">Option title.</param>
/// <param name="Choices">Choice names. Same order as the values in <see cref="Configuration.LaunchOptionDefs"/>.</param>
public sealed record OptionText(string Title, IReadOnlyList<string> Choices);
