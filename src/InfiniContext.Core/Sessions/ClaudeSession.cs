using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.Logging;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Mcp.Sessions;
using InfiniContext.Core.Mcp.Tasks;
using InfiniContext.Core.Parser;
using InfiniContext.Core.State;
using InfiniContext.Core.Supervision;
using Serilog;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// Concrete <see cref="IClaudeSession"/>: spawns one <c>claude</c> child, runs a stdout reader loop
/// that parses NDJSON into <see cref="ClaudeEvent"/>s, drives the <see cref="SessionStateMachine"/>,
/// and exposes parsed events through a bounded channel. stderr is drained into a small ring buffer.
/// All notable activity is logged through Serilog; pass <c>logger</c> = <c>null</c> to fall back to
/// the static <see cref="Log.Logger"/> (which is <c>SilentLogger</c> until the host configures it).
///
/// Composition (Stage 1 polish):
/// <list type="bullet">
///   <item><see cref="ApprovalSidecarLauncher"/> owns the per-session approval-MCP plumbing.</item>
///   <item><see cref="SessionLogger"/> owns the rx/state/tool-stack/malformed logging bundle.</item>
///   <item>This class is the orchestrator: process lifecycle, stdin write, stdout drain, dispose.</item>
/// </list>
/// </summary>
public sealed class ClaudeSession : IClaudeSession
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        // claude reads inputs as compact JSON; humans don't read this, so leave defaults.
    };

    private readonly ClaudeSessionOptions _options;
    private readonly InfiniContextSettings _settings;
    private readonly StreamJsonParser _parser;
    private readonly SessionStateMachine _state;
    private readonly Channel<ClaudeEvent> _events;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Queue<string> _stderrRing = new();
    private readonly object _stderrLock = new();
    private readonly TaskCompletionSource _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger _baseLogger;
    private readonly IProcessSupervisor? _supervisor;
    private readonly IApprovalSink? _approvalSink;
    private readonly LockRegistry? _lockRegistry;
    private readonly Action<ClaudeEvent>? _eventObserver;
    private readonly SessionLogger _sessionLogger;

    private volatile ILogger _logger;
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Guid? _sessionId;
    private int _started;
    private int _disposed;
    private int _interruptCounter;
    private ApprovalSidecarLauncher? _approvalLauncher;

    public ClaudeSession(
        ClaudeSessionOptions options,
        ILogger? logger = null,
        IProcessSupervisor? supervisor = null,
        Func<DateTimeOffset>? clock = null,
        IApprovalSink? approvalSink = null,
        InfiniContextSettings? settings = null,
        LockRegistry? lockRegistry = null,
        Action<ClaudeEvent>? eventObserver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _settings = settings ?? InfiniContextSettings.Defaults;
        _supervisor = supervisor;
        _approvalSink = approvalSink;
        _lockRegistry = lockRegistry;
        _eventObserver = eventObserver;
        _parser = new StreamJsonParser();
        _state = new SessionStateMachine(clock);
        // DropOldest under back-pressure: the state machine is updated BEFORE writing to the
        // channel (see StdoutLoopAsync), so dropping queued events doesn't lose state-machine
        // information. This avoids the deadlock where a slow renderer + bounded channel block
        // the stdout reader → child stdout fills → child won't read stdin → SendUserMessage
        // hangs holding _writeLock → InterruptAsync can no longer fire.
        _events = Channel.CreateBounded<ClaudeEvent>(new BoundedChannelOptions(_settings.Session.EventChannelCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _baseLogger = logger ?? Log.Logger;
        _logger = _baseLogger.ForContext(
            InfiniContextLogger.SessionContextPropertyName,
            InfiniContextLogger.FormatSessionContext(null, options.Cwd));
        _sessionLogger = new SessionLogger(_logger, _state, _parser, _settings.Logging.RxTruncationBytes);
    }

    /// <summary>Internal accessor for tests and adapter code in the same project family.</summary>
    internal StreamJsonParser Parser => _parser;

    public Guid? SessionId => _sessionId;
    public SessionStateMachine State => _state;
    public ChannelReader<ClaudeEvent> Events => _events.Reader;

    public int UnknownEventCount => _parser.UnknownEventCount;
    public int MalformedLineCount => _parser.MalformedLineCount;
    public IReadOnlyDictionary<string, int> UnknownTypeHistogram => _parser.UnknownTypeHistogram;

    public IReadOnlyList<string> StderrTail
    {
        get
        {
            lock (_stderrLock) return _stderrRing.ToArray();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("Session already started.");

        var executable = ClaudeArgvBuilder.ResolveExecutable(_options.ClaudeExecutable);
        var effectiveOptions = _options;

        if (_approvalSink is not null)
        {
            _approvalLauncher = new ApprovalSidecarLauncher(_logger);
            try
            {
                effectiveOptions = await _approvalLauncher.ConfigureAsync(
                    effectiveOptions,
                    _approvalSink,
                    _options.ApprovalSidecarExecutable,
                    _options.ApprovalSidecarArgs,
                    lockRegistry: _lockRegistry,
                    lockTtl: TimeSpan.FromSeconds(_settings.Tasks.LockTtlSeconds),
                    lockMaxWait: TimeSpan.FromSeconds(_settings.Tasks.LockMaxWaitSeconds),
                    onSessionExit: OnSessionExitRequestedAsync).ConfigureAwait(false);
            }
            catch
            {
                try { await _approvalLauncher.DisposeAsync().ConfigureAwait(false); } catch { }
                _approvalLauncher = null;
                throw;
            }
        }

        WarnIfMcpJsonPresent(_options.Cwd);

        var argv = ClaudeArgvBuilder.BuildArgs(effectiveOptions);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _options.Cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);

        _logger.Information("spawn: exe={Executable} argv={@Argv} cwd={Cwd}", executable, argv, _options.Cwd);

        // Only assign _process after Start() succeeds so DisposeAsync can rely on
        // _process being a fully started Process. A failed Start (e.g. exe not found)
        // throws Win32Exception, leaving _process null — DisposeAsync becomes a no-op.
        var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start claude executable '{psi.FileName}'.");

        if (_supervisor is not null)
        {
            try { _supervisor.Attach(proc); }
            catch
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { proc.Dispose(); } catch { }
                throw;
            }
        }

        _process = proc;
        _logger.Information("started pid={Pid}", proc.Id);

        _stdoutTask = Task.Run(() => StdoutLoopAsync(proc.StandardOutput), CancellationToken.None);
        _stderrTask = Task.Run(() => StderrLoopAsync(proc.StandardError), CancellationToken.None);

        // claude `--print --input-format stream-json` does NOT emit system/init until it reads
        // at least one user message from stdin (verified against claude 2.1.143 -- a free session
        // with no stdin sits silent until the operator types something, and a task-bound session
        // with a FirstUserMessage gated on system/init would deadlock forever waiting for an
        // event that never arrives). Send the pointer immediately after the loops are wired so
        // claude has something to consume; the resulting system/init then flows through the
        // stdout reader as normal. Fire-and-forget so StartAsync stays non-blocking for the
        // caller (SessionManager.CreateAsync) -- SendFirstUserMessageAsync logs failures rather
        // than throwing.
        if (!string.IsNullOrEmpty(_options.FirstUserMessage))
        {
            _ = SendFirstUserMessageAsync(_options.FirstUserMessage);
        }
    }

    public async Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_process is null) throw new InvalidOperationException("Session has not been started.");
        if (_process.HasExited) throw new InvalidOperationException("Child process has exited.");

        var payload = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = text },
        }, s_jsonOptions);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state.BeginSend();
            _logger.Debug("tx: {Tx}", payload);
            await _process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null) throw new InvalidOperationException("Session has not been started.");

        var requestId = $"req_{Interlocked.Increment(ref _interruptCounter)}_{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = requestId,
            request = new { subtype = "interrupt" },
        }, s_jsonOptions);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Mark Cancelling first so any text rendered between flush and the child's response
            // is correctly attributed to a cancelling turn.
            _state.Cancel();
            _logger.Information("interrupt request_id={RequestId}", requestId);

            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            // Stdin closed under us — the child is on its way out anyway.
            _logger.Warning("interrupt write failed: {Message}", ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Callback wired into the approval sidecar's <c>session.exit</c> path. The agent calls
    /// <c>mcp__infinicontext__session_exit</c> after a context-pressure flush; the channel posts this
    /// to the threadpool (fire-and-forget, so the tool result returns before stdin closes) and we
    /// close the child's stdin from here.
    /// </summary>
    private async Task OnSessionExitRequestedAsync(SessionExitRequest req, CancellationToken ct)
    {
        _logger.Information("session.exit requested by agent reason={Reason}", req.Reason ?? "<none>");
        try { await RequestGracefulExitAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Warning(ex, "session.exit: graceful close threw"); }
    }

    /// <summary>
    /// Closes the child's stdin without killing the process. Claude finishes the current turn (if
    /// any), then exits naturally via EOF. The state machine sees the standard <c>Exited</c>
    /// transition through the stdout drain loop's finally block — so <c>AutoRotationService</c> fires
    /// if a rotation reason was armed by a context-pressure nudge.
    ///
    /// <para>
    /// Distinct from <see cref="DisposeAsync"/>, which also kills the process and disposes the
    /// launcher / supervisor / write lock. Idempotent: a second call after the child has exited is a
    /// no-op. The <see cref="_writeLock"/> acquisition serializes against any in-flight
    /// <see cref="SendUserMessageAsync"/> so stdin isn't closed mid-write.
    /// </para>
    /// </summary>
    public async Task RequestGracefulExitAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not { } proc) return;
        if (proc.HasExited) return;

        _logger.Information("graceful-exit: closing stdin pid={Pid}", proc.Id);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { proc.StandardInput.Close(); }
        catch (Exception ex) { _logger.Warning(ex, "graceful-exit: stdin close threw"); }
        finally { _writeLock.Release(); }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => _exitTcs.Task.WaitAsync(cancellationToken);

    private async Task StdoutLoopAsync(StreamReader stdout)
    {
        try
        {
            await foreach (var ev in _parser.ParseAsync(stdout).ConfigureAwait(false))
            {
                // Per-event resilience: a single bad event must not silently kill the entire
                // stdout loop. Prior to this, an exception in any of these steps (Apply, the
                // version-check, LogReceivedEvent, the event observer, or the channel writer)
                // propagated out, the foreach terminated, and the finally awaited proc exit -
                // so the operator saw a state-transition log, then dead silence for minutes.
                // The "Agent busy never advances" symptom in task 0008 was exactly this.
                try { _state.Apply(ev); }
                catch (Exception ex) { _logger.Warning(ex, "stdout-loop: state.Apply threw for type={Type} subtype={Subtype}", ev.Type, ev.Subtype); }

                if (_sessionId is null && ev.SessionId is { } sid)
                {
                    _sessionId = sid;
                    _logger = _baseLogger.ForContext(
                        InfiniContextLogger.SessionContextPropertyName,
                        InfiniContextLogger.FormatSessionContext(sid, _options.Cwd));
                    _sessionLogger.UpdateLogger(_logger);
                    _logger.Information("session_id assigned");
                }

                if (ev is SystemInitEvent init)
                {
                    try { CheckClaudeVersion(init); }
                    catch (Exception ex) { _logger.Warning(ex, "stdout-loop: CheckClaudeVersion threw"); }
                }

                try { _sessionLogger.LogReceivedEvent(ev); }
                catch (Exception ex) { _logger.Warning(ex, "stdout-loop: LogReceivedEvent threw for type={Type}", ev.Type); }

                if (_eventObserver is not null)
                {
                    try { _eventObserver(ev); }
                    catch (Exception ex) { _logger.Warning(ex, "event observer threw"); }
                }

                try { await _events.Writer.WriteAsync(ev).ConfigureAwait(false); }
                catch (Exception ex) { _logger.Warning(ex, "stdout-loop: events.Writer.WriteAsync threw for type={Type}", ev.Type); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (IOException) { /* stdout closed under us */ }
        catch (Exception ex) { _logger.Error(ex, "stdout-loop: unhandled exception; loop terminating"); }
        finally
        {
            int? exitCode = null;
            if (_process is { } proc)
            {
                try { await proc.WaitForExitAsync().ConfigureAwait(false); } catch { /* best-effort */ }
                try { exitCode = proc.ExitCode; } catch { /* race */ }
                _state.HandleProcessExit(exitCode);
            }
            _logger.Information(
                "process exited code={ExitCode} unknown_events={UnknownEvents} malformed_lines={MalformedLines}",
                exitCode, _parser.UnknownEventCount, _parser.MalformedLineCount);
            _events.Writer.TryComplete();
            _exitTcs.TrySetResult();
        }
    }

    private async Task SendFirstUserMessageAsync(string message)
    {
        try
        {
            await SendUserMessageAsync(message).ConfigureAwait(false);
            _logger.Information("first user message sent ({Bytes} bytes)", message.Length);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "first user message send failed");
        }
    }

    private async Task StderrLoopAsync(StreamReader stderr)
    {
        try
        {
            string? line;
            while ((line = await stderr.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (_stderrLock)
                {
                    _stderrRing.Enqueue(line);
                    while (_stderrRing.Count > _settings.Session.StderrRingCapacity) _stderrRing.Dequeue();
                }
                _logger.Debug("stderr: {Line}", line);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private bool _versionChecked;
    private void CheckClaudeVersion(SystemInitEvent init)
    {
        if (_versionChecked) return;
        _versionChecked = true;

        var observed = init.ClaudeCodeVersion;
        var minVersion = _settings.Versioning.MinClaudeVersion;
        var testedVersion = _settings.Versioning.TestedAgainstVersion;
        var outcome = ClaudeVersionPolicy.Check(observed, minVersion, testedVersion);
        switch (outcome)
        {
            case ClaudeVersionPolicy.Outcome.ExactMatch:
                _logger.Information(
                    "claude version={Observed} (matches TestedAgainstVersion)", observed);
                break;
            case ClaudeVersionPolicy.Outcome.DriftAbove:
            case ClaudeVersionPolicy.Outcome.DriftBelow:
                _logger.Warning(
                    "claude version={Observed} differs from tested-against {Tested} — schema drift may affect parser; watch unknown_event/malformed_line counters",
                    observed, testedVersion);
                break;
            case ClaudeVersionPolicy.Outcome.BelowMinimum:
                _logger.Error(
                    "claude version={Observed} is below MinClaudeVersion {Min} — parser/state-machine assumptions are not guaranteed; consider upgrading",
                    observed, minVersion);
                break;
            case ClaudeVersionPolicy.Outcome.Unrecognised:
                _logger.Warning(
                    "claude version not parseable: {Observed}; treating as drift — watch unknown_event/malformed_line counters",
                    observed ?? "<absent>");
                break;
        }
    }

    private void WarnIfMcpJsonPresent(string cwd)
    {
        // Design doc §7.1: claude auto-loads MCP servers from a .mcp.json in the working directory.
        // Surface this to the user/log so they're aware before claude starts. We don't block —
        // future Stage 2 host index will track "trusted" cwds and gate this on first-time discovery.
        try
        {
            var path = Path.Combine(cwd, ".mcp.json");
            if (File.Exists(path))
            {
                _logger.Warning(
                    "found .mcp.json at {Path} — claude will auto-load these MCP servers; review before continuing if cwd is untrusted",
                    path);
            }
        }
        catch (Exception ex)
        {
            // IO errors here are non-fatal — log and continue.
            _logger.Debug("mcp-json check failed: {Reason}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        if (_process is { } proc)
        {
            if (!proc.HasExited)
            {
                try { proc.StandardInput.Close(); } catch { }
                try { proc.Kill(entireProcessTree: true); } catch { }
            }

            var drainTimeout = TimeSpan.FromSeconds(_settings.Session.StdoutDrainTimeoutSeconds);
            if (_stdoutTask is not null) { try { await _stdoutTask.WaitAsync(drainTimeout).ConfigureAwait(false); } catch { } }
            if (_stderrTask is not null) { try { await _stderrTask.WaitAsync(drainTimeout).ConfigureAwait(false); } catch { } }

            try { proc.Dispose(); } catch { }
        }

        if (_approvalLauncher is not null)
        {
            try { await _approvalLauncher.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        _writeLock.Dispose();
    }
}
