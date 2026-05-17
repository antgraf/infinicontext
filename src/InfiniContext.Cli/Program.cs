using InfiniContext.Cli;
using InfiniContext.Cli.Issues;
using InfiniContext.Cli.Mcp;
using InfiniContext.Cli.Tasks;
using InfiniContext.Cli.Ui;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Logging;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Mcp.Tasks;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Supervision;
using InfiniContext.Core.Tasks;
using InfiniContext.Core.Tasks.Skill;
using Serilog;
using Spectre.Console;

// Sidecar mode: claude spawned us as the MCP approval server. This MUST short-circuit before any
// other startup so stdout stays reserved for JSON-RPC. Logs go to a sidecar-specific log file.
if (args.Length >= 2 && args[0] == "--mcp-approval-server")
{
    var pipeName = args[1];
    // Sidecar reads settings from the same auto-discovered location as the host so logging level
    // and the connect timeout stay in sync. Bad config → fall back to defaults to avoid blocking
    // claude's startup; the host log will already have flagged the validation error.
    InfiniContextSettings sidecarSettings;
    try { sidecarSettings = InfiniContextSettingsLoader.Load().Settings; }
    catch { sidecarSettings = InfiniContextSettings.Defaults; }

    await using var sidecarLogging = InfiniContextLogger.ConfigureFromHostStartup(sidecarSettings.Logging, prefix: "sidecar");
    sidecarLogging.Logger.Information("sidecar invoked pipe={PipeName}", pipeName);
    return await ApprovalSidecarHost.RunAsync(pipeName, sidecarLogging.Logger, connectTimeoutSeconds: sidecarSettings.Mcp.SidecarConnectTimeoutSeconds);
}

if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
{
    PrintUsage(Console.Out);
    return 0;
}

// No args → behave as `run` with defaults (cwd = current directory).
// Explicit `run` is still accepted for forward-compat with future subcommands.
// A leading `--flag` is treated as `run --flag …` so users can pass options without
// remembering the subcommand.
string[] subArgs;
if (args.Length == 0)
{
    subArgs = Array.Empty<string>();
}
else if (args[0] == "run")
{
    subArgs = args[1..];
}
else if (args[0].StartsWith("--", StringComparison.Ordinal))
{
    subArgs = args;
}
else
{
    Console.Error.WriteLine($"infinicontext: unknown subcommand '{args[0]}'");
    PrintUsage(Console.Error);
    return 2;
}
if (!RunArgs.TryParse(subArgs, out var parsed, out var error))
{
    Console.Error.WriteLine($"infinicontext: {error}");
    PrintUsage(Console.Error);
    return 2;
}

// Load settings: file layering (settings.json + settings.user.json) + INFINICONTEXT__ env vars.
// --settings <path> bypasses discovery and loads exactly the named file.
InfiniContextSettingsLoader.LoadResult loaded;
try
{
    loaded = InfiniContextSettingsLoader.Load(new InfiniContextSettingsLoader.LoadOptions
    {
        ExplicitPath = parsed.SettingsPath,
    });
}
catch (InfiniContextSettingsException ex)
{
    Console.Error.WriteLine("infinicontext: invalid settings:");
    foreach (var e in ex.Errors) Console.Error.WriteLine($"  - {e}");
    return 3;
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"infinicontext: {ex.Message}");
    return 3;
}
var settings = loaded.Settings;

await using var logging = InfiniContextLogger.ConfigureFromHostStartup(settings.Logging);
Console.WriteLine($"[log] {logging.LogFilePath}");
if (loaded.SourcesLoaded.Count > 0)
{
    Console.WriteLine($"[settings] {string.Join(", ", loaded.SourcesLoaded)}");
}
logging.Logger.Information(
    "InfiniContext.Cli starting cwd={Cwd} model={Model} resume={Resume} partial={Partial} settings_sources={Sources}",
    parsed.Cwd, parsed.Model ?? settings.Session.Model ?? "<default>", parsed.ResumeSessionId ?? "<none>",
    parsed.IncludePartialMessages || settings.Session.IncludePartialMessages, loaded.SourcesLoaded);

// Install the user-level infinicontext-task skill so spawned `claude` sessions auto-discover it.
// Cheap and idempotent — see InfiniContext.Core.Tasks.Skill.SkillInstaller. Failure here is a soft
// warning: the skill is a UX nicety, not a correctness requirement, so we don't fail startup.
try
{
    var skillResult = await SkillInstaller.InstallIfMissingOrOutdatedAsync();
    logging.Logger.Information("skill: infinicontext-task v{Version} {Result}", InfiniContextTaskSkill.Version, skillResult);
}
catch (Exception skillEx)
{
    logging.Logger.Warning(skillEx, "skill: failed to install infinicontext-task — continuing without");
}

using var supervisor = ProcessSupervisor.CreateForCurrentPlatform(logging.Logger);

// One shared light-model factory so the SDK-vs-CLI back-end choice (and the OAuth fallback) is
// applied uniformly to both auto-mode safety review and task title generation. Title generation
// uses the general-purpose CLI lifecycle (models.light.cli.mode, default LongLived); auto-mode
// reads approval.auto.mode directly inside ApprovalSinkFactory.
var lightModelServiceFactory = new LightModelServiceFactory(settings, logging.Logger, supervisor);

// Approval flow uses a single shared lock so prompt output doesn't interleave with REPL renders.
var consoleWriteLock = new object();

// Open the task registry. Failure anywhere in this block is a soft warning — /task commands
// degrade to a "subsystem unavailable" message but the rest of the host (sessions, approvals)
// keeps working.
await using var taskStore = await TryOpenTaskStoreAsync(settings, logging.Logger).ConfigureAwait(false);
TaskCommandHandler? taskCommandHandler = null;
IssueCommandHandler? issueCommandHandler = null;
TaskService? taskService = null;
IssueService? issueService = null;
ILightModelService? lightModelForTasks = null;
if (taskStore is not null)
{
    try
    {
        lightModelForTasks = lightModelServiceFactory.Create(thinking: false, cliMode: settings.Models.Light.Cli.Mode);
        taskService = new TaskService(taskStore, lightModelForTasks);

        // Review subsystem wiring. Reviewer agent uses settings.review.reviewerModel (null falls
        // back to the user's main model id); auto-apply gates through dotnet build / dotnet test.
        var reviewSettings = settings.Review;
        var reviewerModelId = string.IsNullOrWhiteSpace(reviewSettings.ReviewerModel)
            ? settings.Models.Main.Id
            : reviewSettings.ReviewerModel!;
        var reviewerAgent = new InfiniContext.Core.Review.CliReviewerAgentService(
            new InfiniContext.Core.Review.ReviewerAgentOptions
            {
                Model = reviewerModelId,
                InvocationTimeoutSeconds = reviewSettings.InvocationTimeoutSeconds,
            },
            supervisor: supervisor,
            logger: logging.Logger);
        var autoApplyExecutor = new InfiniContext.Core.Review.AutoApplyExecutor(
            new InfiniContext.Core.Review.ProcessBuildTestRunner(),
            logger: logging.Logger);
        var reviewService = new InfiniContext.Core.Review.ReviewService(
            reviewerAgent,
            autoApplyExecutor,
            logger: logging.Logger);
        var scopeResolver = new InfiniContext.Core.Review.ScopeResolver(
            new InfiniContext.Core.Review.ProcessGitRunner(),
            byteCap: reviewSettings.PerFileByteCap);

        taskCommandHandler = new TaskCommandHandler(
            taskService,
            Console.Out,
            consoleWriteLock,
            reviewService: reviewService,
            scopeResolver: scopeResolver,
            input: Console.In);
        logging.Logger.Information("tasks: subsystem ready (db={DbPath}); review: ready", settings.Tasks.ResolvedDbPath());
    }
    catch (Exception ex)
    {
        logging.Logger.Warning(ex, "tasks: light-model or service init failed; /task commands will report 'unavailable'");
    }
}

// Take ownership of the task-side light model's disposal. The D11 default for title generation
// is the long-lived back end, which holds a `claude` child alive across calls and implements
// IAsyncDisposable. Today the Job Object reaps the child on host exit (Windows-only), but
// graceful disposal will matter for Stage 2 macOS supervision and is the right shape regardless.
// Single-shot and the SDK back ends don't implement IAsyncDisposable, so the cast yields null
// and the await using is a no-op.
await using var _lightModelForTasksDisposable = lightModelForTasks as IAsyncDisposable;
// Phase 7: one host-scope LockRegistry shared by every spawned session. ApprovalChannel uses
// the per-session pipe name as the lock owner id, so locks are reclaimed automatically when
// each session's channel disposes.
var lockRegistry = new LockRegistry(logger: logging.Logger);

// IssueService is constructed after the lock registry so issues.md writes flow through the
// same serialization the agent's MCP-side writes do. It's optional: when the task subsystem is
// unavailable we skip the issue subsystem too, and /issue reports 'unavailable'.
if (taskStore is not null)
{
    issueService = new IssueService(taskStore, lockRegistry);
    issueCommandHandler = new IssueCommandHandler(issueService, Console.Out, consoleWriteLock);
}

var approvalFactory = new ApprovalSinkFactory(settings, consoleWriteLock, Console.Out, logging.Logger, parsed.Cwd, supervisor: supervisor, lightModelServiceFactory: lightModelServiceFactory);
var initialMode = parsed.ApprovalMode ?? settings.Approval.Mode;
// Phase 5: the approval sink is per-session — each session has its own PendingApprovalSlot and
// SwitchableApprovalSink, built by the SessionApprovalSinkFactory closure below. `bannersEmitted`
// gates the user-visible mode banners (Allow warning, Auto back-end notice) so they fire once
// (at first-session create or at /approval mode-change), not N times per concurrent session.
// `currentApprovalMode` is the host-wide effective mode (D12 — one effective mode across all
// sessions); /approval iterates SessionManager.Active to swap each session in lockstep.
var currentApprovalMode = initialMode;
var bannersEmittedForCurrentMode = false;
SessionApprovalSinkFactory perSessionSinkFactory = (slot, transcript) =>
{
    var inner = approvalFactory.Build(currentApprovalMode, slot, transcript, out var effective, announce: !bannersEmittedForCurrentMode);
    bannersEmittedForCurrentMode = true;
    // Pin to the post-fallback mode (e.g. Auto → Ask if the light-model construct failed) so
    // subsequent sessions see the resolved mode, not the requested one.
    currentApprovalMode = effective;
    return inner;
};
logging.Logger.Information("approval-mode: initial={Mode} (requested={Requested})", currentApprovalMode, initialMode);

// CLI args take precedence over settings; settings provide the fallback.
var effectiveModel = parsed.Model ?? settings.Session.Model;
var effectivePartial = parsed.IncludePartialMessages || settings.Session.IncludePartialMessages;
IReadOnlyList<string> effectiveExtraArgs = parsed.ExtraArgs.Count > 0 ? parsed.ExtraArgs : settings.Session.ExtraArgs;

// Outer loop: each iteration opens a fresh session through the SessionManager. /resume in the
// REPL exits the current iteration with a new resume id, prompting a re-launch here.
// /task switch <id> exits with a TaskSwitchPlan, prompting a re-launch with FirstUserMessage
// set and the previous task (if any) flipped back to Idle.
//
// Stage 2B Phase 1: the four loop scalars (cwd, resume id, pending first-user-message, active
// task id) live in HostLoopState; InProgress/Idle flips route through TaskActivityCoordinator
// (one session per task — D9). The coordinator's clock is injected so tests can fake the
// last_active_at_unix_ms writes (closes 2A todo §B A4).
//
// Stage 2B Phase 2: per-session construction (pressure monitor + injector + ClaudeSession.Start)
// is owned by SessionManager.CreateAsync. The host's id minted in Phase 1 is replaced by the
// real per-session ctx.Id, which becomes the TaskActivityCoordinator key — Phase 4's multi-pane
// UI will mint one ctx per pane and key the coordinator the same way.
var hostLoopState = new HostLoopState(parsed.Cwd, parsed.ResumeSessionId);
var taskActivity = taskStore is not null
    ? new TaskActivityCoordinator(taskStore, () => DateTimeOffset.UtcNow, logging.Logger)
    : null;
await using var sessionManager = await SessionManager.OpenAsync(
    settings,
    logging.Logger,
    perSessionSinkFactory,
    supervisor: supervisor,
    lockRegistry: lockRegistry);
logging.Logger.Information("session-index: loaded {Path} with {Recent} recent row(s)",
    sessionManager.IndexPath, sessionManager.RecentSessions.Count);
while (true)
{
    var spawnRequest = new SpawnRequest
    {
        Cwd = hostLoopState.CurrentSessionCwd,
        Model = effectiveModel,
        ResumeSessionId = hostLoopState.ResumeSessionId,
        IncludePartialMessages = effectivePartial,
        ClaudeExecutable = parsed.ClaudeExecutable,
        ExtraArgs = effectiveExtraArgs,
        FirstUserMessage = hostLoopState.PendingFirstUserMessage,
        ActiveTaskId = hostLoopState.ActiveTaskId,
    };

    var ctx = await sessionManager.CreateAsync(spawnRequest).ConfigureAwait(false);

    if (hostLoopState.ActiveTaskId is { } enteringTaskId && taskActivity is not null)
    {
        var enter = await taskActivity.EnterAsync(ctx.Id, enteringTaskId).ConfigureAwait(false);
        if (enter is EnterTaskResult.RefusedResult refused)
        {
            // Unreachable in Phase 2 (single host session can't race itself), but surface the
            // contract so Phase 4's multi-pane path inherits a clean error message.
            Console.Error.WriteLine($"[task] {refused.TaskId:D4} is already active in session {refused.HolderSessionId}");
            await sessionManager.CloseAsync(ctx.Id).ConfigureAwait(false);
            return 4;
        }
    }

    var host = new MultiSessionHost(ctx, Console.In, AnsiConsole.Console, logging.Logger, consoleWriteLock, settings, loaded.EffectiveJson,
        () => currentApprovalMode,
        requested =>
        {
            try
            {
                // Phase 5: iterate every active session and swap each one's switchable inner so
                // all panes see the new effective mode in lockstep (D12 — host-wide). The first
                // swap announces (banners on); subsequent ones go quiet. If no sessions exist
                // yet (unreachable in practice — the host must be running at least the focused
                // one to handle /approval), `bannersEmittedForCurrentMode` flips so the next
                // session-create announces.
                ApprovalMode effective = requested;
                var first = true;
                foreach (var sctx in sessionManager.Active)
                {
                    var newInner = approvalFactory.Build(requested, sctx.ApprovalSlot, sctx.Transcript, out effective, announce: first);
                    sctx.ApprovalSink.Switch(newInner);
                    first = false;
                }
                currentApprovalMode = effective;
                bannersEmittedForCurrentMode = !first; // true iff we announced at least once
                return effective;
            }
            catch (Exception ex)
            {
                logging.Logger.Error(ex, "approval-mode: switch to {Mode} failed", requested);
                throw;
            }
        },
        taskHandler: taskCommandHandler,
        issueHandler: issueCommandHandler,
        prepareTaskSwitch: taskService is null
            ? null
            : (id, ct) => taskService.PrepareSwitchAsync(id, ct),
        sessionManager: sessionManager);
    HostResult result;
    try
    {
        result = await host.RunAsync();
    }
    finally
    {
        await sessionManager.CloseAsync(ctx.Id).ConfigureAwait(false);
    }

    // Flip the outgoing task back to Idle before deciding on the next iteration. This runs
    // for both clean exit and /task switch (the new task is marked InProgress at the top of
    // the next loop iteration).
    if (hostLoopState.ActiveTaskId is { } leavingTaskId && taskActivity is not null)
    {
        await taskActivity.LeaveAsync(ctx.Id, leavingTaskId).ConfigureAwait(false);
    }

    if (result.SwitchPlan is { } plan)
    {
        // /task switch: re-launch with the pointer pre-loaded AND the session cwd overridden to
        // the task's home. The task folder lives at <home_cwd>/.task/<id>-<slug>/, so spawning
        // the session there makes the relative pointer in FirstUserMessage resolve cleanly.
        hostLoopState.OnSwitchPlan(plan);
        Directory.CreateDirectory(hostLoopState.CurrentSessionCwd);
        Console.WriteLine($"[task] re-launching session for task {plan.Task.Id:D4}");
        continue;
    }

    if (string.IsNullOrEmpty(result.ResumeSessionId))
    {
        hostLoopState.OnExit();
        return result.ExitCode;
    }

    // /resume requested: tear down the current session (await using above) and loop. Drop the
    // first-user-message hint — /resume reuses the pre-existing transcript. Reset cwd to the
    // host's launch dir; /resume isn't bound to any task, so the prior task's home doesn't apply.
    hostLoopState.OnResume(result.ResumeSessionId);
    Console.WriteLine($"[resume] re-launching with --resume {hostLoopState.ResumeSessionId}");
}

static async Task<TaskStore?> TryOpenTaskStoreAsync(InfiniContextSettings settings, ILogger logger)
{
    try
    {
        return await TaskStore.OpenAsync(settings.Tasks.ResolvedDbPath()).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        logger.Warning(ex, "tasks: subsystem unavailable; /task commands will report 'unavailable'");
        return null;
    }
}

static void PrintUsage(TextWriter w)
{
    // Single source of truth: CliFlagCatalog. Adding a flag in RunArgs.TryParse without a
    // catalog entry trips the drift test in CliFlagCatalogTests.
    w.Write(CliFlagCatalog.PrintUsageText());
}
