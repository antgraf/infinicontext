using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// One logical user-turn-and-claude-response cycle in the central transcript tree. Rendered as
/// a single card with two stacked sub-blocks:
///   - Operator block (visible when <see cref="HasOperatorMessage"/> is true): label =
///     "Operator", body = <see cref="UserPrompt"/>.
///   - Agent block: label = "Agent", body = <see cref="Headline"/> (the most recent assistant
///     text block, always present). Tool-use / tool-result / thinking / system events live in
///     <see cref="Children"/> behind the Details expander.
///
/// "Synthetic" prompts ("(session start)", "(claude-initiated)") are markers, not real user
/// input; <see cref="HasOperatorMessage"/> is false for them so the Operator block stays
/// hidden. Synthetic headlines ("(session ready)", "thinking...") are also filtered out of the
/// Agent body so technical status doesn't masquerade as an assistant reply.
///
/// <see cref="IsInProgress"/> is true between the user's prompt send and the arrival of the
/// turn's <c>result</c> event; the view renders a spinner glyph during that window.
/// </summary>
public sealed partial class TurnNodeViewModel : ObservableObject
{
    public ObservableCollection<TurnContentItemViewModel> Children { get; } = new();

    /// <summary>Synthetic placeholders used by <c>SessionRowViewModel</c> to mark turn provenance.
    /// Treated as "not a real user message" when computing <see cref="HasOperatorMessage"/>.</summary>
    public static readonly IReadOnlyList<string> SyntheticPrompts = new[]
    {
        "(claude-initiated)", "(session start)",
    };

    /// <summary>Synthetic placeholders that can land in <see cref="Headline"/>. Filtered out of
    /// the Agent body via <see cref="HasHeadline"/> so they don't render as assistant text.</summary>
    public static readonly IReadOnlyList<string> SyntheticHeadlines = new[]
    {
        "thinking...", "(session ready)", "(claude-initiated)", "(session start)",
    };

    [ObservableProperty]
    private string _userPrompt = "";

    [ObservableProperty]
    private string _headline = "thinking...";

    [ObservableProperty]
    private bool _isInProgress = true;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _footerSummary = "";

    public string OperatorLabel => "Operator";
    public string AgentLabel => "Agent";

    public bool HasOperatorMessage =>
        !string.IsNullOrWhiteSpace(UserPrompt) && !SyntheticPrompts.Contains(UserPrompt);

    public bool HasHeadline =>
        !string.IsNullOrWhiteSpace(Headline) && !SyntheticHeadlines.Contains(Headline);

    public TurnNodeViewModel(string userPrompt)
    {
        _userPrompt = userPrompt;
    }

    partial void OnUserPromptChanged(string value) => OnPropertyChanged(nameof(HasOperatorMessage));
    partial void OnHeadlineChanged(string value) => OnPropertyChanged(nameof(HasHeadline));
}
