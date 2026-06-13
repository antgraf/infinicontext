using InfiniContext.Core.Configuration;
using InfiniContext.Core.Supervision;
using Serilog;

namespace InfiniContext.Core.LightModel;

/// <summary>
/// CLI-backed light-model service that keeps a single <c>claude</c> child alive across calls,
/// amortizing process startup. After every real turn the service sends a <c>/clear</c> message
/// and waits for the model's confirmation reply, then returns the real turn's result to the
/// caller.
///
/// <para>
/// <b>About <c>/clear</c>.</b> Per D2/D7 in the prior research, <c>/clear</c> over stream-json
/// input is forwarded to the model as plain text — it does not reset conversation context the way
/// the REPL slash-command does. <c>input_tokens</c> measurably grows across consecutive
/// <c>/clear</c> cycles. The user's explicit direction (overriding D2/D7) is to use it anyway:
/// for simple non-isolation-sensitive tasks the model behaves *as if* cleared, and the prompt-cache
/// TTL (~5 minutes) bounds the drift in practice. Callers who need real isolation should use
/// <see cref="SingleShotCliLightModelService"/> instead.
/// </para>
///
/// <para>Crash and prompt-change handling: if the child has exited when a call arrives, respawn
/// transparently. If the caller switches the system prompt (different ILightModelService method →
/// different prompt baked in via <c>--system-prompt</c>), respawn before the call. If the child
/// crashes <em>during</em> a call, the in-flight call surfaces the error and the next call
/// respawns. No automatic mid-call retry.</para>
/// </summary>
public sealed class LongLivedCliLightModelService : CliLightModelServiceBase, IAsyncDisposable
{
    private const string ClearCommand = "/clear";

    private readonly IProcessSupervisor? _supervisor;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    // Serializes a whole call — spawn + real turn + the trailing /clear turn — against any other
    // caller (M25). _lifecycleLock only guards the _child field; without this, two concurrent
    // ExecuteAsync callers would run competing ReadLineAsync loops on the one child's stdout and
    // attribute each other's answers (or one's /clear would swallow the other's result). The
    // factory hands this instance to general consumers, so the single-caller assumption can't hold.
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private CliLightModelChild? _child;
    private string? _pinnedSystemPrompt;
    private int _disposed;

    public LongLivedCliLightModelService(
        LightModelOptions? options = null,
        bool thinking = false,
        IProcessSupervisor? supervisor = null,
        ILogger? logger = null)
        : base(options ?? new LightModelOptions(), thinking, logger ?? Log.Logger)
    {
        _supervisor = supervisor;
    }

    protected override async Task<string> ExecuteAsync(string systemPrompt, string userPayload, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(LongLivedCliLightModelService));

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Options.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Serialize the entire call (M25). The wait counts against the per-call timeout so a stuck
        // prior turn can't block this one indefinitely.
        await _turnLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var child = await GetOrSpawnChildAsync(systemPrompt, linked.Token).ConfigureAwait(false);

            string text;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                text = await child.RunTurnAsync(userPayload, linked.Token).ConfigureAwait(false);
                sw.Stop();
                Logger.Debug("cli-light-model: long-lived turn ok latency={LatencyMs}ms", sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await KillCurrentChildAsync().ConfigureAwait(false);
                throw new TimeoutException($"cli-light-model: long-lived call exceeded {Options.TimeoutSeconds}s deadline");
            }
            catch
            {
                // Real-turn failure leaves the stream in an indeterminate state. Kill so the next
                // caller gets a fresh child rather than a half-broken one.
                await KillCurrentChildAsync().ConfigureAwait(false);
                throw;
            }

            // /clear after the real turn, per user direction. We wait for the model's reply (one
            // turn's worth of stream events terminating in a `result`) so subsequent calls don't see
            // overlapping streams. The reply text is discarded — its content doesn't matter; the
            // arrival of `result` is the "confirmation" we wait for.
            try
            {
                await child.RunTurnAsync(ClearCommand, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // /clear failures don't fail the caller's call — they got their answer above. But the
                // child is now in an indeterminate state (mid-stream of a /clear that didn't finish),
                // so kill it; the next call will respawn cleanly.
                Logger.Warning(ex, "cli-light-model: /clear after turn failed; killing child for fresh respawn next call");
                await KillCurrentChildAsync().ConfigureAwait(false);
            }

            return text;
        }
        finally
        {
            _turnLock.Release();
        }
    }

    private async Task<CliLightModelChild> GetOrSpawnChildAsync(string systemPrompt, CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Respawn if: (a) no child yet, (b) child has exited (crash), (c) the caller's system
            // prompt differs from the one baked into the live child via --system-prompt. Switching
            // the prompt mid-stream would lie to the model.
            var needsSpawn =
                _child is null
                || _child.HasExited
                || !string.Equals(_pinnedSystemPrompt, systemPrompt, StringComparison.Ordinal);

            if (needsSpawn)
            {
                if (_child is not null)
                {
                    var reason = _child.HasExited ? "child-exited"
                        : !string.Equals(_pinnedSystemPrompt, systemPrompt, StringComparison.Ordinal) ? "prompt-changed"
                        : "unknown";
                    Logger.Information("cli-light-model: long-lived respawn reason={Reason}", reason);
                    var old = _child;
                    _child = null;
                    try { await old.DisposeAsync().ConfigureAwait(false); } catch { }
                }

                _child = CliLightModelChild.Spawn(Options, Thinking, systemPrompt, _supervisor, Logger);
                _pinnedSystemPrompt = systemPrompt;
            }

            return _child!;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task KillCurrentChildAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_child is not null)
            {
                var old = _child;
                _child = null;
                try { await old.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await KillCurrentChildAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
        _turnLock.Dispose();
    }
}
