using CommunityToolkit.Mvvm.ComponentModel;
using InfiniContext.Core.Sessions;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// Row in the Sessions view's list pane. Wraps a <see cref="SessionContext"/> and exposes the
/// fields the UI needs (short id, cwd, state badge, drift, cost). Phase 3B scaffold reads these
/// once at construction + on demand; live updates (state transitions, drift increments) land in
/// a later slice where we hook the state machine's change notifications.
/// </summary>
public sealed partial class SessionRowViewModel : ObservableObject
{
    public SessionContext Context { get; }
    public Guid Id => Context.Id;
    public string ShortId => Context.Id.ToString("N").Substring(0, 8);
    public string Cwd => Context.Cwd ?? "<no cwd>";
    public TranscriptViewModel Transcript { get; }

    [ObservableProperty]
    private bool _isFocused;

    public SessionRowViewModel(SessionContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Transcript = new TranscriptViewModel(context.Transcript);
    }

    public string State => Context.Session.State.State.ToString();
    public int Drift => Context.Session.UnknownEventCount;
    public string CostDisplay => Context.LastCostUsd is { } c ? $"${c:0.0000}" : "тАФ";
}
