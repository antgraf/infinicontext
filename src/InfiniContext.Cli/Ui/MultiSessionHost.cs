using InfiniContext.Cli.Issues;
using InfiniContext.Cli.SlashCommands;
using InfiniContext.Cli.Tasks;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using InfiniContext.Core.Tasks;
using Serilog;
using Spectre.Console;

namespace InfiniContext.Cli.Ui;

/// <summary>
/// Stage 2B Phase 4a/4b host UI driver. Replaces the deleted <c>Repl</c> class with the same
/// input + slash-dispatch + state-tracking semantics, plumbed through <see cref="IAnsiConsole"/>
/// and reading transcript fragments from <see cref="TranscriptModel.FragmentAppended"/>.
///
/// <para>
/// Phase 4b: supports multiple concurrent sessions via the supplied
/// <see cref="SessionManager"/>. One session is focused at a time — its transcript echoes to the
/// console; the others' fragments still flow into their <see cref="TranscriptModel"/> so a
/// <c>/focus</c> swap can replay the recent history. <c>/n</c>, <c>/focus</c>, <c>/next</c>,
/// <c>/prev</c>, and the non-destructive <c>/resume</c> work through this surface; <c>/task
/// switch</c> still exits with a <see cref="HostResult.SwitchPlan"/> for the outer loop to
/// respawn against (Phase 4b keeps that contract — fully non-destructive task switch is
/// Phase 4c+ territory).
/// </para>
/// </summary>
internal sealed class MultiSessionHost : IReplActions
{
    private readonly SessionManager? _manager;
    private readonly TextReader _input;
    private readonly IAnsiConsole _console;
    private readonly ILogger _logger;
    private readonly object _writeLock;
    private readonly InfiniContextSettings _settings;
    private readonly SlashCommandRegistry _registry;
    private readonly string? _effectiveSettingsJson;
    private readonly Func<ApprovalMode>? _getApprovalMode;
    private readonly Func<ApprovalMode, ApprovalMode>? _switchApprovalMode;
    private readonly TaskCommandHandler? _taskHandler;
    private readonly IssueCommandHandler? _issueHandler;
    private readonly Func<int, CancellationToken, Task<TaskSwitchPlan?>>? _prepareTaskSwitch;
    private readonly Action<string> _onFragment;
    private readonly Action<StateTransition> _onTransition;

    private TaskCompletionSource _idleSignal = NewSignal();
    private readonly TaskCompletionSource _exitSignal = NewSignal();

    private SessionContext _focused;
    private int _interruptCount;
    private volatile bool _exitRequested;
    private string? _resumeSessionId;
    private TaskSwitchPlan? _pendingSwitch;

    public MultiSessionHost(
        SessionContext ctx,
        TextReader input,
        IAnsiConsole console,
        ILogger logger,
        object? writeLock = null,
        InfiniContextSettings? settings = null,
        string? effectiveSettingsJson = null,
        Func<ApprovalMode>? getApprovalMode = null,
        Func<ApprovalMode, ApprovalMode>? switchApprovalMode = null,
        TaskCommandHandler? taskHandler = null,
        IssueCommandHandler? issueHandler = null,
        Func<int, CancellationToken, Task<TaskSwitchPlan?>>? prepareTaskSwitch = null,
        SessionManager? sessionManager = null)
    {
        _focused = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _writeLock = writeLock ?? new object();
        _settings = settings ?? InfiniContextSettings.Defaults;
        _effectiveSettingsJson = effectiveSettingsJson;
        _getApprovalMode = getApprovalMode;
        _switchApprovalMode = switchApprovalMode;
        _taskHandler = taskHandler;
        _issueHandler = issueHandler;
        _prepareTaskSwitch = prepareTaskSwitch;
        _manager = sessionManager;

        _registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new QuitCommand(),
            new CostCommand(),
            new StateCommand(),
            new HelpCommand(),
            new SettingsCommand(),
            new ResumeCommand(),
            new ApprovalCommand(),
            new TaskSlashCommand(),
            new IssueSlashCommand(),
            new NewSessionCommand(),
            new ListSessionsCommand(),
            new FocusCommand(),
            new FocusNextCommand(),
            new FocusPrevCommand(),
            new BroadcastCommand(),
        });

        _onFragment = OnFragmentAppended;
        _onTransition = OnStateTransition;
    }

    public async Task<HostResult> RunAsync()
    {
        SubscribeFocused(_focused);
        ConsoleCancelEventHandler? cancelHandler = null;

        // Console.CancelKeyPress is only relevant when stdin is an actual console (interactive run).
        // When stdin is redirected (smoke test), there's no signal to attach to and this is harmless.
        try
        {
            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                OnInterrupt();
            };
            Console.CancelKeyPress += cancelHandler;
        }
        catch (IOException) { /* no console available */ }

        try
        {
            await InputLoopAsync().ConfigureAwait(false);

            // Give the focused session a moment to finish streaming its final result event so the
            // [turn done:] line lands before the exit summary. Bounded so a misbehaving session
            // can't hang shutdown. Must run BEFORE unsubscribing — otherwise the trailing
            // ResultEvent fragment appends to the transcript but never echoes to the console.
            //
            // Two-phase drain: first wait for the child process to exit (this is what closes the
            // events-channel writer in ClaudeSession's stdout-loop finally), then wait for the
            // SessionTranscriptReader's loop to fully terminate. The reader's loop only exits
            // *after* the synchronous Render→Emit→FragmentAppended chain has run for the last
            // queued event, which is the deterministic point at which [turn done:] has landed on
            // the console. Waiting just on Session.WaitForExitAsync, or on Events.Completion,
            // races the reader's render chain for that final event and intermittently drops the
            // marker (CI run #2 of Phase 8 was exactly this). The manager path covers production
            // and the smoke tests; in-process tests pass _manager=null and fall back to the bare
            // process-exit wait (their FakeSession has no reader to drain anyway).
            var drainBudget = TimeSpan.FromSeconds(_settings.Session.ExitDrainWaitSeconds);
            try { await Task.WhenAny(_focused.Session.WaitForExitAsync(), Task.Delay(drainBudget)).ConfigureAwait(false); }
            catch { }
            if (_manager is not null)
            {
                try { await _manager.DrainReaderAsync(_focused.Id, drainBudget).ConfigureAwait(false); }
                catch { }
            }
        }
        finally
        {
            UnsubscribeFocused(_focused);
            if (cancelHandler is not null)
            {
                try { Console.CancelKeyPress -= cancelHandler; } catch { }
            }
        }

        PrintExitSummary();
        return new HostResult(0, _resumeSessionId, _pendingSwitch);
    }

    private async Task InputLoopAsync()
    {
        while (!_exitRequested)
        {
            // Wait until the focused session is back at Idle (or has exited).
            while (!_exitRequested
                   && _focused.Session.State.State != SessionState.Idle
                   && _focused.Session.State.State != SessionState.Exited)
            {
                await Task.WhenAny(_idleSignal.Task, _exitSignal.Task).ConfigureAwait(false);
            }

            if (_exitRequested) break;
            if (_focused.Session.State.State == SessionState.Exited && _manager is null) break;
            if (_focused.Session.State.State == SessionState.Exited && (_manager?.ActiveCount ?? 0) == 0) break;

            // Prime a fresh signal so the next non-Idle → Idle transition can release the next turn.
            _idleSignal = NewSignal();

            Write(_settings.Cosmetics.Repl.Prompt);

            string? line;
            try
            {
                line = await _input.ReadLineAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                line = null;
            }

            if (line is null) break; // stdin EOF
            line = line.Trim();
            if (line.Length == 0) continue;

            // Phase 5: if the focused session has a pending approval, the next typed line is a
            // y/N answer for THAT session — not a prompt for claude. Non-focused sessions'
            // approvals are not blocked: their PendingApprovalSlot continues to wait, and the
            // user can /focus to resolve them. Auto/Allow/Deny modes never park requests in the
            // slot, so this branch is only relevant when the inner sink is SlotApprovalSink.
            if (_focused.ApprovalSlot.Current is not null)
            {
                ResolveFocusedApproval(line);
                continue;
            }

            if (line.StartsWith('/'))
            {
                await HandleSlashCommandAsync(line).ConfigureAwait(false);
                if (_exitRequested) break;
                continue;
            }

            try
            {
                await _focused.Session.SendUserMessageAsync(line).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Write($"\n[error] {ex.Message}\n");
                if (_focused.Session.State.State == SessionState.Exited) break;
            }
        }
    }

    private void ResolveFocusedApproval(string answer)
    {
        var normalized = answer.ToLowerInvariant();
        var allow = normalized is "y" or "yes";
        var resolved = _focused.ApprovalSlot.Resolve(
            allow ? ApprovalBehavior.Allow : ApprovalBehavior.Deny);
        if (!resolved)
        {
            // Raced — timeout or cancellation cleared the slot between read and resolve.
            Write("[approval] no longer pending\n");
        }
    }

    private async Task HandleSlashCommandAsync(string line)
    {
        var space = line.IndexOf(' ');
        var head = space < 0 ? line : line[..space];
        var argLine = space < 0 ? "" : line[(space + 1)..];

        var cmd = _registry.TryResolve(head);
        var ctx = BuildContext();
        if (cmd is not null)
        {
            try
            {
                await cmd.ExecuteAsync(ctx, argLine, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "slash-command {Cmd} threw", head);
                Write($"[{head.TrimStart('/')}] error: {ex.Message}\n");
            }
            return;
        }

        // Unknown slash-command: forward verbatim to claude (decisions.md D6). Claude has its
        // own slash-commands (/clear, /compact, …); the host should not intercept what it
        // doesn't own.
        try
        {
            await _focused.Session.SendUserMessageAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Write($"\n[error] {ex.Message}\n");
        }
    }

    private SlashCommandContext BuildContext() =>
        new(
            Session: _focused.Session,
            Write: Write,
            Settings: _settings,
            EffectiveSettingsJson: _effectiveSettingsJson,
            GetApprovalMode: _getApprovalMode,
            SwitchApprovalMode: _switchApprovalMode,
            TaskHandler: _taskHandler,
            PrepareTaskSwitch: _prepareTaskSwitch,
            GetLastCostUsd: () => _focused.LastCostUsd,
            GetLastNumTurns: () => _focused.LastNumTurns,
            Actions: this,
            Registry: _registry)
        {
            SessionManager = _manager,
            Console = _console,
            FocusedHostSessionId = _focused.Id,
            ActiveTaskId = _focused.ActiveTaskId,
            IssueHandler = _issueHandler,
        };

    public void RequestExit()
    {
        _exitRequested = true;
        _exitSignal.TrySetResult();
    }

    public void RequestResume(string sessionId)
    {
        _resumeSessionId = sessionId;
        _exitRequested = true;
        _exitSignal.TrySetResult();
    }

    public void RequestSwitch(TaskSwitchPlan plan)
    {
        _pendingSwitch = plan;
        _exitRequested = true;
        _exitSignal.TrySetResult();
    }

    public void RequestFocus(Guid hostSessionId)
    {
        if (_manager is null)
        {
            Write("[focus] multi-session not available in this host\n");
            return;
        }
        var target = _manager.TryGet(hostSessionId);
        if (target is null)
        {
            Write($"[focus] no active session {hostSessionId:D}\n");
            return;
        }
        if (target.Id == _focused.Id) return;

        var prior = _focused;
        UnsubscribeFocused(prior);

        _focused = target;
        Write($"\n[focus] switched to {target.Id:D}\n");
        // Replay the new session's accumulated transcript so the user sees recent context.
        foreach (var frag in target.Transcript.Snapshot()) Write(frag);
        SubscribeFocused(target);
        // Reset the idle signal: the new session's current state may already be Idle, in which
        // case the input loop returns straight to the prompt. If it's mid-turn we wait again.
        _idleSignal.TrySetResult();
        _idleSignal = NewSignal();
    }

    private void SubscribeFocused(SessionContext ctx)
    {
        ctx.Transcript.FragmentAppended += _onFragment;
        ctx.Session.State.Transitioned += _onTransition;
    }

    private void UnsubscribeFocused(SessionContext ctx)
    {
        try { ctx.Transcript.FragmentAppended -= _onFragment; } catch { }
        try { ctx.Session.State.Transitioned -= _onTransition; } catch { }
    }

    private async Task SafeInterruptAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.Session.InterruptTimeoutSeconds));
            await _focused.Session.InterruptAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "interrupt failed");
        }
    }

    private void OnFragmentAppended(string fragment) => Write(fragment);

    private void OnStateTransition(StateTransition t)
    {
        // The transcript reader already appends the [state: ...] marker; idle/exit signaling is
        // what this handler is responsible for.
        if (t.To == SessionState.Idle) _idleSignal.TrySetResult();
        if (t.To == SessionState.Exited) _exitSignal.TrySetResult();
    }

    private void OnInterrupt()
    {
        var n = Interlocked.Increment(ref _interruptCount);
        var state = _focused.Session.State.State;

        if (state is SessionState.Sending or SessionState.Streaming
                  or SessionState.Compacting or SessionState.Cancelling)
        {
            Write("\n[interrupt] cancelling current turn (press Ctrl+C again to exit)\n");
            _ = SafeInterruptAsync();
            return;
        }

        // Idle (or any non-active state): treat as exit request.
        Write("\n[interrupt] exiting\n");
        _exitRequested = true;
        _exitSignal.TrySetResult();

        // Second uninterruptible press: if we were already trying to exit, give up and let the OS kill us.
        if (n >= 2)
        {
            Environment.Exit(130);
        }
    }

    private void PrintExitSummary()
    {
        if (_focused.Session.SessionId is { } sid)
        {
            Write($"\n[done] session={sid:D}\n");
            Write($"        resume: infinicontext run --resume {sid:D}\n");
        }
        else
        {
            Write("\n[done] (no session_id captured)\n");
        }
    }

    private void Write(string s)
    {
        lock (_writeLock)
        {
            // AnsiConsole.Write writes verbatim text without parsing markup. Fragments already
            // contain plain strings; Phase 4c+ may switch to Markup() when transcript fragments
            // carry Spectre styling.
            _console.Write(s);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
