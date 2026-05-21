using CommunityToolkit.Mvvm.ComponentModel;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// Row in the Sessions view's tree (child of a <see cref="TaskNodeViewModel"/>). Wraps a
/// <see cref="SessionContext"/> and exposes the fields the UI needs (short id, cwd, state badge,
/// drift, cost). Subscribes to <see cref="SessionStateMachine.Transitioned"/> so live state
/// changes raise PropertyChanged for State/StatusGlyph without the UI having to poll.
/// </summary>
public sealed partial class SessionRowViewModel : ObservableObject, IDisposable
{
    public SessionContext Context { get; }
    public Guid Id => Context.Id;
    public string ShortId => Context.Id.ToString("N").Substring(0, 8);
    public string Cwd => Context.Cwd ?? "<no cwd>";
    public TranscriptViewModel Transcript { get; }

    [ObservableProperty]
    private bool _isFocused;

    [ObservableProperty]
    private string _statusGlyph = ViewModels.StatusGlyph.Idle;

    private readonly Action<StateTransition> _onTransition;
    private bool _disposed;

    public SessionRowViewModel(SessionContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Transcript = new TranscriptViewModel(context.Transcript);
        _onTransition = OnStateTransition;
        context.Session.State.Transitioned += _onTransition;
        StatusGlyph = ViewModels.StatusGlyph.ForSession(context.Session.State.State, context.ApprovalSlot.Current is not null);
    }

    private void OnStateTransition(StateTransition t)
    {
        // Fired on the reader's loop thread; ObservableObject's PropertyChanged dispatches
        // synchronously, but XAML bindings will marshal to UI as needed.
        OnPropertyChanged(nameof(State));
        StatusGlyph = ViewModels.StatusGlyph.ForSession(t.To, Context.ApprovalSlot.Current is not null);
    }

    public string State => Context.Session.State.State.ToString();
    public int Drift => Context.Session.UnknownEventCount;
    public string CostDisplay => Context.LastCostUsd is { } c ? $"${c:0.0000}" : "тАФ";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Context.Session.State.Transitioned -= _onTransition; } catch { /* best-effort */ }
        Transcript.Dispose();
    }
}
