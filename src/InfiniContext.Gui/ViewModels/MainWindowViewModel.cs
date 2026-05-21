using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniContext.Core.Sessions;
using InfiniContext.Gui.Services;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// Root view-model. Owns the <see cref="SessionHost"/> and the observable session list. The
/// folder picker is launched via a callback the view sets, which keeps the VM testable (no
/// dependency on a concrete <see cref="Window"/>). Phase 3B scaffold; broadcast, focus rotation,
/// prompt send, and approval surfaces land in later slices.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SessionHost? _host;

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    [ObservableProperty]
    private SessionRowViewModel? _focusedSession;

    [ObservableProperty]
    private string _statusText = "Ready.";

    [ObservableProperty]
    private string _logFilePath = "(host not initialised)";

    /// <summary>
    /// Called by the view to launch the OS folder picker. Returns the chosen folder path or null
    /// if the user cancelled. Wired in <see cref="Views.MainWindow"/>.
    /// </summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>Used by the view to surface fatal startup errors as a modal.</summary>
    public Action<string>? ShowError { get; set; }

    public IAsyncRelayCommand NewSessionCommand { get; }

    /// <summary>Design-time constructor; the runtime path uses <see cref="CreateAsync"/>.</summary>
    public MainWindowViewModel()
    {
        NewSessionCommand = new AsyncRelayCommand(NewSessionAsync);
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
        return new MainWindowViewModel(host);
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
            Sessions.Add(row);
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

    private void OnSessionClosed(Guid id)
    {
        void Apply()
        {
            for (var i = Sessions.Count - 1; i >= 0; i--)
            {
                if (Sessions[i].Id == id)
                {
                    Sessions[i].Transcript.Dispose();
                    Sessions.RemoveAt(i);
                }
            }
            if (FocusedSession?.Id == id) FocusedSession = Sessions.FirstOrDefault();
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    partial void OnFocusedSessionChanged(SessionRowViewModel? oldValue, SessionRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsFocused = false;
        if (newValue is not null) newValue.IsFocused = true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Sessions) s.Transcript.Dispose();
        Sessions.Clear();
        if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false);
    }
}
