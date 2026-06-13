using System.Text;

namespace InfiniContext.Gui.Services;

/// <summary>
/// M22: coalesces host slash-command output and flushes it on an <see cref="IUiTimer"/> tick instead
/// of forwarding every write straight through. The old path was O(n²): a command's
/// <c>TranscriptTextWriter.Write(char)</c> produced one dispatcher post per character, and each post
/// rebuilt the entire <c>Detail</c> string (<c>Detail += oneChar</c>). Buffering into a
/// <see cref="StringBuilder"/> and flushing the accumulated text a few times a second collapses that
/// to one post + one concat per tick.
///
/// <para>
/// <see cref="Append"/> is safe to call from any thread (the command runs with
/// <c>ConfigureAwait(false)</c>); the timer ticks on the UI thread and the flush target marshals as
/// needed. Disposing flushes any tail and stops the timer.
/// </para>
/// </summary>
public sealed class BatchingOutputSink : IDisposable
{
    private readonly Action<string> _flushTarget;
    private readonly IUiTimer _timer;
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Default flush cadence — responsive enough to read streaming output, coarse enough to
    /// keep the per-tick concat count low even for a minutes-long command.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(75);

    public BatchingOutputSink(Action<string> flushTarget, IUiTimerFactory timerFactory, TimeSpan? interval = null)
    {
        _flushTarget = flushTarget ?? throw new ArgumentNullException(nameof(flushTarget));
        ArgumentNullException.ThrowIfNull(timerFactory);
        _timer = timerFactory.Create(interval ?? DefaultInterval);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Buffer <paramref name="text"/> for the next flush. Thread-safe.</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate) _buffer.Append(text);
    }

    private void OnTick(object? sender, EventArgs e) => Flush();

    /// <summary>Forward any buffered text to the flush target in one call. No-op when empty.</summary>
    public void Flush()
    {
        string pending;
        lock (_gate)
        {
            if (_buffer.Length == 0) return;
            pending = _buffer.ToString();
            _buffer.Clear();
        }
        _flushTarget(pending);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        Flush();
    }
}
