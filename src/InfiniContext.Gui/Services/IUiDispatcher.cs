namespace InfiniContext.Gui.Services;

/// <summary>
/// Abstraction over the UI dispatcher so VMs can be exercised without spinning up an Avalonia
/// dispatcher. The real impl (<see cref="AvaloniaUiDispatcher"/>) forwards to
/// <c>Dispatcher.UIThread</c>; the test impl (in InfiniContext.Tests) runs everything synchronously
/// so PropertyChanged + CollectionChanged + command CanExecute flips all observe atomically.
///
/// <para>Three methods cover every existing call site in the Gui project:
/// <list type="bullet">
///   <item><see cref="CheckAccess"/> - branch between "already on UI thread" and "need to Post".</item>
///   <item><see cref="Post"/> - fire-and-forget marshal onto the UI thread.</item>
///   <item><see cref="InvokeAsync"/> - schedule async work onto the UI thread, await completion.</item>
/// </list>
/// </para>
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True if the caller is already on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Schedule <paramref name="action"/> to run on the UI thread. Returns immediately.</summary>
    void Post(Action action);

    /// <summary>Schedule <paramref name="action"/> to run on the UI thread and return a Task that
    /// completes when it does. The action is invoked even if the caller is already on the UI thread.</summary>
    Task InvokeAsync(Func<Task> action);
}
