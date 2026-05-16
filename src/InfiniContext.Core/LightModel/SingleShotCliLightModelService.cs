using InfiniContext.Core.Configuration;
using InfiniContext.Core.Supervision;
using Serilog;

namespace InfiniContext.Core.LightModel;

/// <summary>
/// CLI-backed light-model service: spawns a fresh <c>claude --print</c> per call. Implements the
/// recipe from <c>docs/research/claude-cli-light-model.md</c> §6 — MAX-stripped flags, recipe
/// env vars, managed empty cwd. With <c>thinking=false</c>, sets <c>CLAUDE_CODE_DISABLE_THINKING=1</c>
/// (D8): single-shot p50 ~2.3 s / $0.0007 per call. With <c>thinking=true</c>, omits that flag and
/// raises <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c> to give the model room to think before emitting JSON.
///
/// <para>Each call is a fresh process and is genuinely isolated — no cross-call leakage. This is the
/// OAuth-only fallback for <see cref="ApprovalMode.Auto"/> when <c>ANTHROPIC_API_KEY</c> is absent.</para>
/// </summary>
public sealed class SingleShotCliLightModelService : CliLightModelServiceBase
{
    private readonly IProcessSupervisor? _supervisor;

    public SingleShotCliLightModelService(
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
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Options.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var child = CliLightModelChild.Spawn(Options, Thinking, systemPrompt, _supervisor, Logger);
        try
        {
            var text = await child.RunTurnAsync(userPayload, linked.Token).ConfigureAwait(false);
            sw.Stop();
            Logger.Debug("cli-light-model: single-shot turn ok latency={LatencyMs}ms", sw.ElapsedMilliseconds);
            return text;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"cli-light-model: single-shot call exceeded {Options.TimeoutSeconds}s deadline");
        }
        finally
        {
            // Background disposal: don't make the caller wait for the child to wind down. Close stdin
            // / kill / reap costs ~440 ms even with a snug 500 ms grace, all of it strictly after we
            // already have the assistant text. The Job Object guarantees stragglers die on host exit.
            _ = Task.Run(async () =>
            {
                try { await child.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Logger.Warning(ex, "cli-light-model: background disposal failed"); }
            });
        }
    }
}
