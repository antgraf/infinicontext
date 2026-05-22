using Avalonia.Threading;

namespace InfiniContext.Gui.Services;

/// <summary>Real <see cref="IUiTimer"/> backed by <see cref="DispatcherTimer"/>. One per
/// consumer; the parameterless-priority ctor leaves the timer stopped until <see cref="Start"/>
/// is called (the three-arg ctor in Avalonia auto-starts, which we don't want).</summary>
public sealed class AvaloniaUiTimer : IUiTimer
{
    private readonly DispatcherTimer _inner;

    public AvaloniaUiTimer(TimeSpan interval)
    {
        _inner = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        _inner.Tick += OnInnerTick;
    }

    public TimeSpan Interval
    {
        get => _inner.Interval;
        set => _inner.Interval = value;
    }

    public bool IsEnabled => _inner.IsEnabled;

    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();

    public event EventHandler? Tick;

    private void OnInnerTick(object? sender, EventArgs e) => Tick?.Invoke(this, e);
}

/// <summary>Real factory. Constructed once in <c>App.OnFrameworkInitializationCompleted</c>.</summary>
public sealed class AvaloniaUiTimerFactory : IUiTimerFactory
{
    public static AvaloniaUiTimerFactory Instance { get; } = new();

    public IUiTimer Create(TimeSpan interval) => new AvaloniaUiTimer(interval);
}
