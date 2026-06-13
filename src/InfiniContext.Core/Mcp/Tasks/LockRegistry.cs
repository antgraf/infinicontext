using InfiniContext.Core.Tasks;
using Serilog;

namespace InfiniContext.Core.Mcp.Tasks;

/// <summary>
/// Stage 2A Phase 7. Host-singleton lock registry that backs the MCP <c>task_acquire_lock</c>
/// / <c>task_release_lock</c> tools. One lock per file path per host process; no readers/writers
/// distinction (single-mutator semantics, see design.md §7).
///
/// <para>
/// Lifetime is tied to the host process. Per-session locks are reclaimed via
/// <see cref="ReleaseAllOwnedBy"/> when the owning session's <c>ApprovalChannel</c> disposes —
/// no separate process-supervisor hook is needed because the channel mirrors the session lifetime.
/// </para>
///
/// <para>
/// TTL semantics: a granted lock auto-expires after <c>ttl</c>. Waiters poll and observe the
/// expiry deterministically via the injected clock function; in production the clock is the
/// system clock (<c>() => DateTimeOffset.UtcNow</c>) and <see cref="AcquireAsync"/> sleeps in
/// short increments while waiting.
/// </para>
/// </summary>
public sealed class LockRegistry
{
    private readonly Dictionary<string, LockEntry> _byFile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _filesByToken = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;

    public LockRegistry(Func<DateTimeOffset>? clock = null, ILogger? logger = null, TimeSpan? pollInterval = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? Log.Logger;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(50);
    }

    /// <summary>
    /// Try to acquire <paramref name="filePath"/>. If another session holds a non-expired lock,
    /// poll until <paramref name="maxWait"/> elapses. On success the returned token is stable for
    /// the lifetime of the lock and is what callers pass to <see cref="Release"/>.
    /// </summary>
    public async Task<LockAcquireOutcome> AcquireAsync(
        string filePath,
        string ownerId,
        TimeSpan ttl,
        TimeSpan maxWait,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl), "ttl must be positive.");
        if (maxWait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxWait), "maxWait must be non-negative.");

        var deadline = _clock() + maxWait;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var contended = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (!_byFile.TryGetValue(filePath, out var existing) || existing.ExpiresAt <= _clock())
                {
                    if (existing is not null)
                    {
                        _filesByToken.Remove(existing.Token);
                        _logger.Information(
                            "lock: expired previous holder file={File} prev_owner={PrevOwner}",
                            filePath, existing.OwnerId);
                    }

                    var token = NewToken();
                    var expiresAt = _clock() + ttl;
                    _byFile[filePath] = new LockEntry(token, expiresAt, ownerId, filePath);
                    _filesByToken[token] = filePath;

                    _logger.Information(
                        "lock: acquired file={File} owner={Owner} ttl={Ttl}s contended={Contended} wait={WaitMs}ms",
                        filePath, ownerId, (int)ttl.TotalSeconds, contended, sw.ElapsedMilliseconds);

                    return LockAcquireOutcome.Granted(token, ttl);
                }
            }

            contended = true;
            var remaining = deadline - _clock();
            if (remaining <= TimeSpan.Zero)
            {
                _logger.Warning(
                    "lock: timeout file={File} owner={Owner} max_wait={MaxWait}s",
                    filePath, ownerId, (int)maxWait.TotalSeconds);
                return LockAcquireOutcome.Timeout();
            }

            var sleep = remaining < _pollInterval ? remaining : _pollInterval;
            try
            {
                await Task.Delay(sleep, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Release a previously granted lock with no ownership check. For trusted in-process callers
    /// (the host's own <c>TaskCommandHandler</c> / <c>IssueService</c>, which acquire and release
    /// under their own owner id in the same call). Returns <c>false</c> if the token is unknown —
    /// either because the caller never held the lock or because the TTL already expired and
    /// another session has since taken over.
    /// </summary>
    public bool Release(string token) => ReleaseCore(token, expectedOwner: null);

    /// <summary>
    /// Release a lock only if it is currently held under <paramref name="expectedOwner"/> (M2).
    /// The MCP <see cref="ApprovalChannel"/> calls this with its own pipe name so a connection
    /// cannot release a lock another session holds even if it learns or guesses the token. Returns
    /// <c>false</c> when the token is unknown, stale, or held under a different owner.
    /// </summary>
    public bool Release(string token, string expectedOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwner);
        return ReleaseCore(token, expectedOwner);
    }

    private bool ReleaseCore(string token, string? expectedOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (_sync)
        {
            if (!_filesByToken.TryGetValue(token, out var file)) return false;
            if (!_byFile.TryGetValue(file, out var entry) || entry.Token != token)
            {
                // Stale mapping — the byFile entry has moved on to another holder.
                _filesByToken.Remove(token);
                return false;
            }
            if (expectedOwner is not null && !string.Equals(entry.OwnerId, expectedOwner, StringComparison.Ordinal))
            {
                // Token is valid but held by a different owner — refuse (M2). Do NOT remove the
                // token mapping: that would let a hostile caller evict the real holder's bookkeeping.
                _logger.Warning(
                    "lock: release denied — file={File} held by owner={Owner}, caller claimed owner={Caller}",
                    file, entry.OwnerId, expectedOwner);
                return false;
            }
            _byFile.Remove(file);
            _filesByToken.Remove(token);
            _logger.Information(
                "lock: released file={File} owner={Owner}",
                file, entry.OwnerId);
            return true;
        }
    }

    /// <summary>
    /// Drop every lock currently owned by <paramref name="ownerId"/>. Returns the count released.
    /// Called from <c>ApprovalChannel.DisposeAsync</c> so a crashing/exiting session can never
    /// strand a lock past its own lifetime.
    /// </summary>
    public int ReleaseAllOwnedBy(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return 0;
        lock (_sync)
        {
            var toRemove = new List<string>();
            foreach (var kv in _byFile)
                if (string.Equals(kv.Value.OwnerId, ownerId, StringComparison.Ordinal))
                    toRemove.Add(kv.Key);

            foreach (var file in toRemove)
            {
                if (_byFile.TryGetValue(file, out var entry))
                    _filesByToken.Remove(entry.Token);
                _byFile.Remove(file);
            }

            if (toRemove.Count > 0)
                _logger.Information("lock: released {Count} lock(s) on owner-exit owner={Owner}", toRemove.Count, ownerId);
            return toRemove.Count;
        }
    }

    /// <summary>Snapshot of currently held locks. Test/debug only.</summary>
    public IReadOnlyList<LockEntry> Snapshot()
    {
        lock (_sync) return _byFile.Values.ToList();
    }

    private static string NewToken() => "lt_" + Guid.NewGuid().ToString("N")[..16];
}

/// <summary>One row in <see cref="LockRegistry"/>.</summary>
public sealed record LockEntry(string Token, DateTimeOffset ExpiresAt, string OwnerId, string FilePath);

/// <summary>Outcome of <see cref="LockRegistry.AcquireAsync"/>.</summary>
public sealed record LockAcquireOutcome
{
    public bool Ok { get; init; }
    public string? Token { get; init; }
    public TimeSpan TtlGranted { get; init; }
    public string? Error { get; init; }

    public static LockAcquireOutcome Granted(string token, TimeSpan ttl) =>
        new() { Ok = true, Token = token, TtlGranted = ttl };

    public static LockAcquireOutcome Timeout() =>
        new() { Ok = false, Error = "timeout waiting for lock" };
}
