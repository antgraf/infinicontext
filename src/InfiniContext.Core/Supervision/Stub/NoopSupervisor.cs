using System.Diagnostics;
using Serilog;

namespace InfiniContext.Core.Supervision.Stub;

/// <summary>
/// Fallback <see cref="IProcessSupervisor"/> for non-Windows hosts. Emits a single startup
/// warning so the operator knows children won't be reaped automatically; per-attach calls are
/// silent. Honors the same <see cref="Attach"/> contract as the real supervisor (must be called
/// after <c>Process.Start()</c>) so unit tests can validate the pre-start guard cross-platform.
///
/// <para>Stage 2 will replace this on macOS with a signal-handler / <c>ProcessExit</c> backend.</para>
/// </summary>
public sealed class NoopSupervisor : IProcessSupervisor
{
    private readonly ILogger _logger;
    private bool _warned;
    private bool _disposed;

    public NoopSupervisor(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    public void Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try { _ = process.Id; }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "Cannot attach a process that has not been started.", ex);
        }

        if (!_warned)
        {
            _logger.Warning(
                "supervisor: NoopSupervisor active — children will NOT be reaped on host exit");
            _warned = true;
        }
    }

    public void DetachAll() { }

    public void Dispose() { _disposed = true; }
}
