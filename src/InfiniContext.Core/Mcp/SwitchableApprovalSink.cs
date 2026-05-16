namespace InfiniContext.Core.Mcp;

/// <summary>
/// Mutable wrapper around an <see cref="IApprovalSink"/>. The wired sink (passed to
/// <see cref="Sessions.ClaudeSession"/> and the <see cref="ApprovalChannel"/> via the sidecar)
/// stays constant for the life of the session, while <see cref="Switch"/> hot-swaps the underlying
/// implementation when the user changes mode via <c>/approval</c>. Stage 2B Phase 5 moves this
/// type from <c>InfiniContext.Cli</c> into <c>Core</c> so each <see cref="Sessions.SessionContext"/>
/// can expose its own wrapper directly (one effective mode across all sessions per D12, but each
/// session swaps its own inner against its own approval slot).
/// Thread-safe for the swap-vs-request race: a request in flight at the moment of switch keeps
/// using the previous inner sink (volatile read).
/// </summary>
public sealed class SwitchableApprovalSink : IApprovalSink
{
    private volatile IApprovalSink _inner;
    public SwitchableApprovalSink(IApprovalSink initial) { _inner = initial ?? throw new ArgumentNullException(nameof(initial)); }

    public IApprovalSink Inner => _inner;

    public void Switch(IApprovalSink replacement) =>
        _inner = replacement ?? throw new ArgumentNullException(nameof(replacement));

    public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        _inner.RequestAsync(request, cancellationToken);
}
