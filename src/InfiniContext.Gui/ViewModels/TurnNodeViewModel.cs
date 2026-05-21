using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// One logical user-turn-and-claude-response cycle in the central transcript tree.
///
/// Headline = the most recent assistant text block (the "final answer", always visible).
/// Children = thinking blocks, tool-use blocks, tool-result attachments, system events, all
/// collapsed by default. <see cref="IsInProgress"/> is true between the user's prompt send
/// and the arrival of the turn's <c>result</c> event; the view renders a spinner glyph or
/// "thinking..." placeholder during that window.
/// </summary>
public sealed partial class TurnNodeViewModel : ObservableObject
{
    public ObservableCollection<TurnContentItemViewModel> Children { get; } = new();

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

    public TurnNodeViewModel(string userPrompt)
    {
        _userPrompt = userPrompt;
    }
}
