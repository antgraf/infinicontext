using System.Text.Json.Nodes;

namespace InfiniContext.Core.Mcp;

/// <summary>
/// Per-session bridge between an <see cref="IApprovalSink"/> implementation (producer of approval
/// requests) and the host UI (consumer that surfaces them to the user). Stage 2B Phase 5 replaces
/// the single-session, stdin-driven <c>ConsoleApprovalSink</c> with a per-session slot so two
/// sessions firing approvals concurrently no longer compete for the same console reader.
///
/// <para>
/// Contract: at most one pending request per slot at a time. The MCP sidecar reader is
/// single-threaded per session — it writes one request line, waits for the response, then reads
/// the next — so two-in-flight is unreachable in production. A defensive
/// <see cref="InvalidOperationException"/> guards against future protocol changes.
/// </para>
///
/// <para>
/// Cancellation: <see cref="RequestAsync"/> resolves to <c>Deny</c> when the timeout elapses,
/// throws <see cref="OperationCanceledException"/> when the request-scoped token cancels (sidecar
/// teardown), and resolves to the host's verdict when <see cref="Resolve"/> is invoked. The slot
/// is cleared on every exit path so the next request finds it empty.
/// </para>
/// </summary>
public sealed class PendingApprovalSlot
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private TaskCompletionSource<ApprovalResponse>? _tcs;
    private PendingApproval? _current;
    private CancellationTokenSource? _timeoutCts;

    public PendingApprovalSlot(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Fires when <see cref="Current"/> transitions — new pending request, or null on
    /// clear. UI subscribes to redraw the focused session's approval banner.</summary>
    public event Action<PendingApproval?>? Changed;

    /// <summary>Snapshot of the current pending request, or null if the slot is empty.</summary>
    public PendingApproval? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// Park a request in the slot until the host calls <see cref="Resolve"/>, the timeout fires,
    /// or <paramref name="cancellationToken"/> cancels. The default sink implementation
    /// (<c>SlotApprovalSink</c> in the CLI assembly) is what calls this; the channel reader awaits
    /// the returned task as it would any other <see cref="IApprovalSink"/>.
    /// </summary>
    public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tcs = new TaskCompletionSource<ApprovalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = _clock() + timeout;
        var pending = new PendingApproval(request, deadline);

        CancellationTokenSource timeoutCts;
        lock (_gate)
        {
            if (_tcs is not null)
                throw new InvalidOperationException("PendingApprovalSlot already has an in-flight request.");
            _tcs = tcs;
            _current = pending;
            timeoutCts = new CancellationTokenSource(timeout);
            _timeoutCts = timeoutCts;
        }

        // Notify outside the lock so handlers can re-enter without deadlocking.
        Changed?.Invoke(pending);

        // Timeout: deny with the standard message; CT cancel: throw cancelled so the channel can
        // surface a host-error response. Both clear the slot.
        var timeoutReg = timeoutCts.Token.Register(() =>
        {
            if (TryClear(tcs, out _))
                tcs.TrySetResult(ApprovalResponse.Deny("user did not respond within timeout"));
        });
        var ctReg = cancellationToken.Register(() =>
        {
            if (TryClear(tcs, out _))
                tcs.TrySetCanceled(cancellationToken);
        });

        return tcs.Task.ContinueWith(t =>
        {
            timeoutReg.Dispose();
            ctReg.Dispose();
            try { timeoutCts.Dispose(); } catch { }
            return t.GetAwaiter().GetResult();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Host-side resolution. Returns true if a request was pending and got resolved; false if the
    /// slot was empty (the resolve raced timeout or cancellation). <paramref name="updatedInput"/>
    /// is only meaningful when <paramref name="behavior"/> is <see cref="ApprovalBehavior.Allow"/>.
    /// </summary>
    public bool Resolve(ApprovalBehavior behavior, JsonObject? updatedInput = null, string? message = null)
    {
        TaskCompletionSource<ApprovalResponse>? tcs;
        PendingApproval? pending;
        lock (_gate)
        {
            if (_tcs is null) return false;
            tcs = _tcs;
            pending = _current;
            _tcs = null;
            _current = null;
            try { _timeoutCts?.Cancel(); } catch { }
            _timeoutCts = null;
        }

        Changed?.Invoke(null);

        var response = behavior switch
        {
            ApprovalBehavior.Allow => ApprovalResponse.Allow(updatedInput ?? pending!.Request.Input),
            ApprovalBehavior.Deny => ApprovalResponse.Deny(message ?? "user denied"),
            _ => throw new ArgumentOutOfRangeException(nameof(behavior)),
        };
        return tcs.TrySetResult(response);
    }

    private bool TryClear(TaskCompletionSource<ApprovalResponse> expected, out PendingApproval? wasPending)
    {
        wasPending = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_tcs, expected)) return false;
            wasPending = _current;
            _tcs = null;
            _current = null;
            _timeoutCts = null;
        }
        Changed?.Invoke(null);
        return true;
    }
}
