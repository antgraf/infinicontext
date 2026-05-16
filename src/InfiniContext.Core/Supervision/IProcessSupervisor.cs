using System.Diagnostics;

namespace InfiniContext.Core.Supervision;

/// <summary>
/// Owns the host's "kill child processes when the host dies" guarantee. One supervisor per host;
/// inject the same instance into every <c>ClaudeSession</c>.
///
/// <para>
/// Windows backend: a Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. Non-Windows hosts
/// get <see cref="Stub.NoopSupervisor"/> until the Stage 2 macOS backend lands. Test code can pass
/// either explicitly.
/// </para>
///
/// <para>
/// Contract: <see cref="Attach(Process)"/> must be called <em>after</em> <c>Process.Start()</c>
/// (so the OS handle exists) but as soon as possible afterwards — descendants spawned before the
/// attach are not guaranteed to be in the job. The supervisor itself must exist before any child
/// is launched, so the Job Object's kill flag is in place when <see cref="Attach"/> happens.
/// </para>
/// </summary>
public interface IProcessSupervisor : IDisposable
{
    /// <summary>
    /// Attach a started child to the supervisor. Throws <see cref="InvalidOperationException"/>
    /// if the process has not been started, or <see cref="ObjectDisposedException"/> if the
    /// supervisor has been disposed.
    /// </summary>
    void Attach(Process process);

    /// <summary>
    /// Release all attached children so they survive a clean <see cref="IDisposable.Dispose"/>.
    /// On Windows this clears <c>KILL_ON_JOB_CLOSE</c>. Idempotent. Useful when the host is
    /// handing children off (e.g. a graceful shutdown) rather than crashing.
    /// </summary>
    void DetachAll();
}
