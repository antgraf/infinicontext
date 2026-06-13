using Serilog;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// C1 (audit <c>docs/architecture-review-2026-06-11.md</c>): enforces the documented single-host
/// contract. At startup every host process — CLI or GUI — acquires an exclusive OS lock on a
/// sentinel file under <c>~/.infinicontext/</c>. A second host fails fast instead of silently
/// corrupting the shared SQLite registry and <c>.task/</c> markdown that the in-memory
/// <c>LockRegistry</c> / <c>TaskActivityCoordinator</c> only coordinate <i>per-process</i> (D9
/// "one session per task" is enforced per-process, so two hosts editing the same task race into
/// lost updates).
///
/// <para>
/// This is the cheap path the audit recommends. The full multi-host design (SQLite-backed task
/// claims with heartbeats, WAL mode, a cross-process lock mechanism) is a deferred Stage 3 design
/// item tracked in <c>docs/todo.md</c>.
/// </para>
///
/// <para>
/// <b>Mechanism.</b> The guard is the OS share lock held on the open file handle, not the file's
/// existence: while one process holds the handle with <see cref="FileShare.None"/>, any other
/// process's open fails with a sharing violation. <see cref="FileOptions.DeleteOnClose"/> removes
/// the sentinel when the handle closes — including on a hard crash — so a stale file can never
/// wedge the next host.
/// </para>
/// </summary>
public sealed class HostInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private HostInstanceLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    /// <summary>Environment override for the sentinel path. Read directly (not through the settings
    /// layering) so test harnesses can isolate each spawned host to a unique temp file — mirrors the
    /// GUI's <c>INFINICONTEXT__GUI__SETTINGSPATH</c> idiom. Without it, every concurrent CLI smoke
    /// test (and a developer running tests with the GUI open) would collide on the one real
    /// sentinel and fail to start.</summary>
    public const string PathEnvVar = "INFINICONTEXT__HOSTLOCKPATH";

    /// <summary>Sentinel path: the <see cref="PathEnvVar"/> override when set, otherwise
    /// <c>~/.infinicontext/host.lock</c>. Pure function — does no I/O. Mirrors
    /// <see cref="SessionIndex.DefaultPath"/>'s resolution.</summary>
    public static string DefaultPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".infinicontext",
            "host.lock");
    }

    /// <summary>Absolute path of the held sentinel file.</summary>
    public string Path { get; }

    /// <summary>
    /// Acquire the exclusive host lock. Throws <see cref="HostAlreadyRunningException"/> when another
    /// host already holds it. The lock is released when this instance is <see cref="Dispose"/>d or
    /// the process exits (the OS drops the handle either way).
    /// </summary>
    public static HostInstanceLock Acquire(string? path = null, ILogger? logger = null)
    {
        var resolved = path ?? DefaultPath();
        var dir = System.IO.Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        FileStream stream;
        try
        {
            stream = new FileStream(
                resolved,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            // Sharing violation (another live host holds the handle) is the expected failure; any
            // other IOException (path unwritable, etc.) is also a hard startup blocker, so surface
            // them the same way with the underlying error attached.
            throw new HostAlreadyRunningException(resolved, ex);
        }

        logger?.Information("host-lock: acquired {Path} (pid {Pid})", resolved, Environment.ProcessId);
        return new HostInstanceLock(stream, resolved);
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// Thrown by <see cref="HostInstanceLock.Acquire"/> when the single-host sentinel is already held.
/// The host treats this as a fatal startup condition (CLI exits non-zero; the GUI surfaces the
/// message in its startup banner).
/// </summary>
public sealed class HostAlreadyRunningException : Exception
{
    public HostAlreadyRunningException(string lockPath, Exception inner)
        : base($"Another InfiniContext host is already running (lock: {lockPath}). " +
               "Only one host may run at a time — close the other instance and retry.", inner)
    {
        LockPath = lockPath;
    }

    /// <summary>The sentinel path that could not be exclusively acquired.</summary>
    public string LockPath { get; }
}
