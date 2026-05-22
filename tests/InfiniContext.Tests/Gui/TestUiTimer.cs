using InfiniContext.Gui.Services;

namespace InfiniContext.Tests.Gui;

/// <summary>Test impl of <see cref="IUiTimer"/> that never advances real time. Tests call
/// <see cref="FireTick"/> to manually invoke the Tick handler.</summary>
internal sealed class TestUiTimer : IUiTimer
{
    public TimeSpan Interval { get; set; }
    public bool IsEnabled { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public void Start()
    {
        IsEnabled = true;
        StartCount++;
    }

    public void Stop()
    {
        IsEnabled = false;
        StopCount++;
    }

    public event EventHandler? Tick;

    /// <summary>Synchronously invoke the Tick handler. Used by tests to advance the banner
    /// countdown without waiting wall-clock seconds.</summary>
    public void FireTick() => Tick?.Invoke(this, EventArgs.Empty);
}

internal sealed class TestUiTimerFactory : IUiTimerFactory
{
    public List<TestUiTimer> Created { get; } = new();

    public IUiTimer Create(TimeSpan interval)
    {
        var t = new TestUiTimer { Interval = interval };
        Created.Add(t);
        return t;
    }
}
