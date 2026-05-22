using InfiniContext.Gui.Services;

namespace InfiniContext.Tests.Gui;

/// <summary>Test impl of <see cref="IUiDispatcher"/> that runs everything synchronously on
/// the calling thread. Tests using this dispatcher don't have to spin up Avalonia and can
/// assert against VM state immediately after raising events.</summary>
internal sealed class SynchronousUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public Task InvokeAsync(Func<Task> action) => action();
}

/// <summary>Tracking variant of <see cref="IUiDispatcher"/>: counts <c>CheckAccess</c> and
/// <c>Post</c> calls so tests can assert that a code path correctly marshals to the UI thread.
/// Setting <see cref="CheckAccessReturns"/> to <c>false</c> simulates "called from a non-UI
/// thread" - the code under test should then take the <c>Post</c> branch. Posted actions still
/// run synchronously so tests can inspect state immediately afterwards.</summary>
internal sealed class TrackingUiDispatcher : IUiDispatcher
{
    public int CheckAccessCount;
    public int PostCount;
    public bool CheckAccessReturns { get; set; } = true;

    public bool CheckAccess() { CheckAccessCount++; return CheckAccessReturns; }
    public void Post(Action action) { PostCount++; action(); }
    public Task InvokeAsync(Func<Task> action) => action();
}
