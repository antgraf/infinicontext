namespace InfiniContext.Core.Sessions;

/// <summary>
/// Bounded per-session ring of rendered transcript fragments. Stage 2B Phase 2 introduces this
/// as part of the <see cref="SessionContext"/> bundle so the shape is in place when Phase 4 wires
/// a per-session reader to populate it for off-screen sessions (D3/D4). Until then the model is
/// constructed but not actively appended to — the single-pane REPL still renders straight from
/// the <see cref="ClaudeSession.Events"/> channel.
///
/// <para>
/// Thread-safe: <see cref="Append"/> and <see cref="Snapshot"/> may be called from different
/// threads. Reads return a point-in-time copy so callers can iterate without holding the lock.
/// </para>
/// </summary>
public sealed class TranscriptModel
{
    public const int DefaultCapacity = 10_000;

    private readonly object _gate = new();
    private readonly Queue<string> _fragments;
    private readonly int _capacity;

    public TranscriptModel(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Must be positive.");
        _capacity = capacity;
        _fragments = new Queue<string>(capacity);
    }

    /// <summary>Current capacity (oldest entries are dropped on overflow).</summary>
    public int Capacity => _capacity;

    /// <summary>Number of fragments currently held.</summary>
    public int Count
    {
        get { lock (_gate) return _fragments.Count; }
    }

    /// <summary>Fired after a fragment is appended. Listeners (e.g. the focused-pane echo in
    /// <c>MultiSessionHost</c>) get the same string that was just enqueued. Subscribers must not
    /// block — handlers run on the appending thread.</summary>
    public event Action<string>? FragmentAppended;

    /// <summary>Append a rendered fragment. Drops the oldest when capacity is reached.</summary>
    public void Append(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        lock (_gate)
        {
            if (_fragments.Count == _capacity) _fragments.Dequeue();
            _fragments.Enqueue(fragment);
        }
        FragmentAppended?.Invoke(fragment);
    }

    /// <summary>Point-in-time snapshot of held fragments, oldest first.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) return _fragments.ToArray();
    }
}
