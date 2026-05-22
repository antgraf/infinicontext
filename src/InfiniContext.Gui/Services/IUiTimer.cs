namespace InfiniContext.Gui.Services;

/// <summary>
/// Abstraction over <c>DispatcherTimer</c> so the banner-countdown logic can be exercised in
/// tests without spinning up Avalonia. The real impl (<see cref="AvaloniaUiTimer"/>) wraps a
/// <c>DispatcherTimer</c>; the test impl exposes a <c>FireTick()</c> method that fires the
/// handler synchronously so tests don't have to wait wall-clock seconds.
/// </summary>
public interface IUiTimer
{
    TimeSpan Interval { get; set; }
    bool IsEnabled { get; }
    void Start();
    void Stop();
    event EventHandler? Tick;
}

/// <summary>Factory for <see cref="IUiTimer"/> so VMs can request a timer without depending on
/// the concrete impl. One <see cref="IUiTimerFactory"/> is shared across the whole app; each call
/// to <see cref="Create"/> returns a fresh timer.</summary>
public interface IUiTimerFactory
{
    IUiTimer Create(TimeSpan interval);
}
