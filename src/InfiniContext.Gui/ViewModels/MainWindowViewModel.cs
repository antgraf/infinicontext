using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Tasks;
using InfiniContext.Gui.Services;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// Root view-model. Owns the <see cref="SessionHost"/>, a tree of <see cref="TaskNodeViewModel"/>
/// (left column), and the focused-session pointer. The view injects callbacks (<see cref="FolderPicker"/>,
/// <see cref="ShowError"/>) so the VM stays free of <see cref="Window"/> references and testable.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SessionHost? _host;
    private readonly TaskNodeViewModel _unattached = new(null);

    /// <summary>Top-level nodes in the left tree: real tasks (sorted by id desc) plus the
    /// (unattached) bucket at the bottom for sessions without an <c>ActiveTaskId</c>.</summary>
    public ObservableCollection<TaskNodeViewModel> TaskNodes { get; } = new();

    [ObservableProperty]
    private SessionRowViewModel? _focusedSession;

    [ObservableProperty]
    private string _statusText = "Ready.";

    [ObservableProperty]
    private string _logFilePath = "(host not initialised)";

    /// <summary>Called by the view to launch the OS folder picker. Returns the chosen path or null.</summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>Used by the view to surface fatal startup errors as a status line.</summary>
    public Action<string>? ShowError { get; set; }

    [ObservableProperty]
    private string _promptText = "";

    public IAsyncRelayCommand NewSessionCommand { get; }
    public IAsyncRelayCommand SendPromptCommand { get; }
    public IAsyncRelayCommand InterruptCommand { get; }

    /// <summary>Design-time constructor; runtime path uses <see cref="CreateAsync"/>.</summary>
    public MainWindowViewModel()
    {
        NewSessionCommand = new AsyncRelayCommand(NewSessionAsync);
        SendPromptCommand = new AsyncRelayCommand(SendPromptAsync, CanSendPrompt);
        InterruptCommand = new AsyncRelayCommand(InterruptAsync, CanInterrupt);
        TaskNodes.Add(_unattached);
    }

    private MainWindowViewModel(SessionHost host) : this()
    {
        _host = host;
        LogFilePath = host.LogFilePath;
        host.SessionClosed += OnSessionClosed;
    }

    public static async Task<MainWindowViewModel> CreateAsync()
    {
        var host = await SessionHost.StartAsync().ConfigureAwait(false);
        var vm = new MainWindowViewModel(host);
        await vm.LoadTasksAsync().ConfigureAwait(false);
        return vm;
    }

    private async Task LoadTasksAsync()
    {
        if (_host is null) return;
        var records = await _host.ListTasksAsync().ConfigureAwait(true);

        void Apply()
        {
            // Tasks newest-first; unattached stays at the bottom.
            TaskNodes.Clear();
            foreach (var rec in records.OrderByDescending(r => r.Id))
                TaskNodes.Add(new TaskNodeViewModel(rec));
            TaskNodes.Add(_unattached);
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private TaskNodeViewModel ResolveParentNode(int? taskId)
    {
        if (taskId is null) return _unattached;
        foreach (var node in TaskNodes)
            if (node.TaskId == taskId) return node;
        // Task referenced by a session but missing from the registry - shouldn't happen during a
        // single host run, but fall back to unattached to avoid losing the row.
        return _unattached;
    }

    private async Task NewSessionAsync()
    {
        if (_host is null)
        {
            ShowError?.Invoke("Host is not initialised - settings load failed at startup.");
            return;
        }

        var cwd = FolderPicker is null ? null : await FolderPicker().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(cwd))
        {
            StatusText = "New session cancelled.";
            return;
        }

        try
        {
            StatusText = $"Spawning claude in {cwd}...";
            var ctx = await _host.CreateSessionAsync(cwd).ConfigureAwait(true);
            var row = new SessionRowViewModel(ctx);
            var parent = ResolveParentNode(ctx.ActiveTaskId);
            parent.Sessions.Add(row);
            parent.IsExpanded = true;
            FocusedSession = row;
            row.IsFocused = true;
            StatusText = $"Spawned {row.ShortId} in {row.Cwd}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Spawn failed: {ex.Message}";
            ShowError?.Invoke($"Failed to spawn session:\n\n{ex}");
        }
    }

    private bool CanSendPrompt() => _host is not null && FocusedSession is not null && !string.IsNullOrWhiteSpace(PromptText);
    private bool CanInterrupt() => FocusedSession is not null;

    private async Task SendPromptAsync()
    {
        var target = FocusedSession;
        var text = PromptText;
        if (target is null || string.IsNullOrWhiteSpace(text)) return;

        // Append a pending TurnNodeViewModel before sending so the central tree shows
        // "thinking..." immediately. The raw flat transcript still gets the echo for the
        // fallback view / debugging.
        target.BeginUserTurn(text);
        target.Context.Transcript.Append($"\n> {text}\n");
        PromptText = "";

        try
        {
            await target.Context.Session.SendUserMessageAsync(text).ConfigureAwait(true);
            StatusText = $"Sent to {target.ShortId}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Send failed: {ex.Message}";
            target.Context.Transcript.Append($"\n[send failed: {ex.Message}]\n");
        }
    }

    private async Task InterruptAsync()
    {
        var target = FocusedSession;
        if (target is null) return;
        try
        {
            await target.Context.Session.InterruptAsync().ConfigureAwait(true);
            StatusText = $"Interrupt sent to {target.ShortId}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Interrupt failed: {ex.Message}";
        }
    }

    partial void OnPromptTextChanged(string value) => SendPromptCommand.NotifyCanExecuteChanged();

    private void OnSessionClosed(Guid id)
    {
        void Apply()
        {
            foreach (var node in TaskNodes)
            {
                for (var i = node.Sessions.Count - 1; i >= 0; i--)
                {
                    if (node.Sessions[i].Id == id)
                    {
                        node.Sessions[i].Dispose();
                        node.Sessions.RemoveAt(i);
                    }
                }
            }
            if (FocusedSession?.Id == id)
                FocusedSession = TaskNodes.SelectMany(n => n.Sessions).FirstOrDefault();
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    partial void OnFocusedSessionChanged(SessionRowViewModel? oldValue, SessionRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsFocused = false;
        if (newValue is not null) newValue.IsFocused = true;
        SendPromptCommand.NotifyCanExecuteChanged();
        InterruptCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in TaskNodes)
            foreach (var s in node.Sessions) s.Dispose();
        TaskNodes.Clear();
        if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false);
    }
}
