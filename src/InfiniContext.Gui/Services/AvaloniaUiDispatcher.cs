using Avalonia.Threading;

namespace InfiniContext.Gui.Services;

/// <summary>
/// Real <see cref="IUiDispatcher"/> impl backed by <see cref="Dispatcher.UIThread"/>. Constructed
/// once at app startup in <c>App.OnFrameworkInitializationCompleted</c> and passed down to the VMs.
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static AvaloniaUiDispatcher Instance { get; } = new();

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Func<Task> action) => Dispatcher.UIThread.InvokeAsync(action);
}
