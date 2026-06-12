using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace InfiniContext.Core.Configuration;

/// <summary>
/// Loads <see cref="InfiniContextSettings"/> by layering, in precedence order:
/// <list type="number">
///   <item><c>settings.json</c> (committed defaults) — auto-discovered next to the host's working dir</item>
///   <item><c>settings.user.json</c> (gitignored user overrides) — same directory</item>
///   <item><c>INFINICONTEXT__SECTION__KEY</c> environment variables — final overlay</item>
/// </list>
/// Pass <see cref="LoadOptions.ExplicitPath"/> to skip discovery and load a single file (useful for
/// profile switching via the <c>--settings</c> CLI flag).
///
/// <para>
/// <b>Tests must never load the developer's <c>settings.user.json</c> or pick up
/// <c>INFINICONTEXT__</c> env vars from the host machine.</b> The strong rule is to construct
/// fakes against <see cref="InfiniContextSettings.Defaults"/> and not call <see cref="Load"/>
/// at all. If a test genuinely needs to exercise the loader (e.g. validating bounds), it
/// must pass <see cref="LoadOptions.BaseDirectory"/> pointing at a test-owned temp dir
/// AND set <see cref="LoadOptions.IncludeUserOverrides"/> = false AND set
/// <see cref="LoadOptions.IncludeEnvironmentVariables"/> = false so the dev's machine
/// state can't leak in.
/// </para>
/// </summary>
public static class InfiniContextSettingsLoader
{
    public const string DefaultsFileName = "settings.json";
    public const string UserOverridesFileName = "settings.user.json";
    public const string EnvVarPrefix = "INFINICONTEXT__";

    public sealed record LoadOptions
    {
        public string? ExplicitPath { get; init; }
        public string? BaseDirectory { get; init; }
        public bool IncludeUserOverrides { get; init; } = true;
        public bool IncludeEnvironmentVariables { get; init; } = true;
    }

    public sealed record LoadResult(
        InfiniContextSettings Settings,
        IReadOnlyList<string> SourcesLoaded,
        string EffectiveJson);

    public static LoadResult Load(LoadOptions? options = null)
    {
        options ??= new LoadOptions();
        var baseDir = options.BaseDirectory ?? Directory.GetCurrentDirectory();
        var sourcesLoaded = new List<string>();

        var builder = new ConfigurationBuilder();

        if (!string.IsNullOrEmpty(options.ExplicitPath))
        {
            var resolved = Path.GetFullPath(options.ExplicitPath);
            if (!File.Exists(resolved))
                throw new FileNotFoundException($"settings file not found: {resolved}", resolved);
            builder.AddJsonFile(resolved, optional: false, reloadOnChange: false);
            sourcesLoaded.Add(resolved);
        }
        else
        {
            var defaultsPath = Path.Combine(baseDir, DefaultsFileName);
            builder.AddJsonFile(defaultsPath, optional: true, reloadOnChange: false);
            if (File.Exists(defaultsPath)) sourcesLoaded.Add(defaultsPath);

            if (options.IncludeUserOverrides)
            {
                var userPath = Path.Combine(baseDir, UserOverridesFileName);
                builder.AddJsonFile(userPath, optional: true, reloadOnChange: false);
                if (File.Exists(userPath)) sourcesLoaded.Add(userPath);
            }
        }

        if (options.IncludeEnvironmentVariables)
        {
            builder.AddEnvironmentVariables(EnvVarPrefix);
        }

        var configuration = builder.Build();
        var settings = new InfiniContextSettings();
        configuration.Bind(settings);

        // Microsoft.Extensions.Configuration APPENDS to (rather than replaces) a pre-populated
        // read-only collection when binding a JSON array. Binding `models.available` over the
        // in-code KnownModels.Catalog default would therefore concatenate the two вЂ” and because
        // the committed settings.json mirrors the catalog, a normal load would silently double it.
        // Re-bind that one section into a fresh list so a configured array REPLACES the default
        // whenever any layer supplies it. (Dictionaries like tasks.modelContextWindows merge by
        // key, which is the intended per-model override semantics, so they're deliberately left
        // alone вЂ” only the array needs this.)
        var availableSection = configuration.GetSection("models:available");
        if (availableSection.Exists())
        {
            var available = new List<ModelCatalogEntry>();
            availableSection.Bind(available);
            settings = settings with { Models = settings.Models with { Available = available } };
        }

        var errors = new List<string>();
        Validate(settings, errors);
        if (errors.Count > 0) throw new InfiniContextSettingsException(errors);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // The arrow in the default approval-timeout message etc. is fine in raw UTF-8;
            // disable the safe-encoder's HTML escaping so /settings output matches the file.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Microsoft.Extensions.Configuration binds JSON `null` to "" on string?, so writing
            // null fields would break round-trip. Skipping them keeps EffectiveJson reloadable
            // and yields cleaner /settings output too.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });

        return new LoadResult(settings, sourcesLoaded, json);
    }

    private static void Validate(InfiniContextSettings s, List<string> errors)
    {
        // Logging
        if (!IsKnownLogLevel(s.Logging.Level))
            errors.Add($"logging.level '{s.Logging.Level}' is not one of: Verbose, Debug, Information, Warning, Error, Fatal.");
        if (string.IsNullOrWhiteSpace(s.Logging.Directory))
            errors.Add("logging.directory must not be empty.");
        if (s.Logging.FlushIntervalMs is < 50 or > 5000)
            errors.Add($"logging.flushIntervalMs must be in [50, 5000], got {s.Logging.FlushIntervalMs}.");
        if (s.Logging.MaxFieldBytes < 64)
            errors.Add($"logging.maxFieldBytes must be >= 64, got {s.Logging.MaxFieldBytes}.");
        if (s.Logging.RxTruncationBytes < 256)
            errors.Add($"logging.rxTruncationBytes must be >= 256, got {s.Logging.RxTruncationBytes}.");

        // Session
        if (s.Session.EventChannelCapacity < 16)
            errors.Add($"session.eventChannelCapacity must be >= 16, got {s.Session.EventChannelCapacity}.");
        if (s.Session.StderrRingCapacity < 16)
            errors.Add($"session.stderrRingCapacity must be >= 16, got {s.Session.StderrRingCapacity}.");
        if (s.Session.StdoutDrainTimeoutSeconds is < 1 or > 60)
            errors.Add($"session.stdoutDrainTimeoutSeconds must be in [1, 60], got {s.Session.StdoutDrainTimeoutSeconds}.");
        if (s.Session.ExitDrainWaitSeconds is < 1 or > 60)
            errors.Add($"session.exitDrainWaitSeconds must be in [1, 60], got {s.Session.ExitDrainWaitSeconds}.");
        if (s.Session.InterruptTimeoutSeconds is < 1 or > 120)
            errors.Add($"session.interruptTimeoutSeconds must be in [1, 120], got {s.Session.InterruptTimeoutSeconds}.");

        // Approval — design doc §4.6 hard constraint: 60s default; allow 5s..600s for power users.
        if (s.Approval.TimeoutSeconds is < 5 or > 600)
            errors.Add($"approval.timeoutSeconds must be in [5, 600], got {s.Approval.TimeoutSeconds}.");
        if (s.Approval.Auto.TimeoutSeconds is < 1 or > 120)
            errors.Add($"approval.auto.timeoutSeconds must be in [1, 120], got {s.Approval.Auto.TimeoutSeconds}.");
        // Allow mode is genuinely dangerous; refuse it as a persisted default. CLI/REPL can still
        // opt in for one session at a time (those layers don't go through Load()).
        if (s.Approval.Mode == ApprovalMode.Allow)
            errors.Add("approval.mode 'Allow' is not allowed as a persisted default. Switch via CLI (--approval-mode allow) or REPL (/approval allow) for one session at a time.");

        // Models
        if (string.IsNullOrWhiteSpace(s.Models.Main.Id))
            errors.Add("models.main.id must not be empty.");
        if (string.IsNullOrWhiteSpace(s.Models.Light.Id))
            errors.Add("models.light.id must not be empty.");
        if (s.Models.Light.MaxTokens is < 16 or > 8192)
            errors.Add($"models.light.maxTokens must be in [16, 8192], got {s.Models.Light.MaxTokens}.");
        if (s.Models.Light.TimeoutSeconds is < 1 or > 300)
            errors.Add($"models.light.timeoutSeconds must be in [1, 300], got {s.Models.Light.TimeoutSeconds}.");
        if (s.Models.Light.Cli.MaxOutputTokens is < 16 or > 8192)
            errors.Add($"models.light.cli.maxOutputTokens must be in [16, 8192], got {s.Models.Light.Cli.MaxOutputTokens}.");
        if (s.Models.Light.Cli.ThinkingMaxOutputTokens is < 64 or > 16384)
            errors.Add($"models.light.cli.thinkingMaxOutputTokens must be in [64, 16384], got {s.Models.Light.Cli.ThinkingMaxOutputTokens}.");
        // Catalog: each offered model needs a usable id + label for the GUI dropdowns. A missing
        // ModelContextWindows key is deliberately NOT validated here вЂ” it falls back to the default
        // threshold pair (ResolveThresholds), which is a legitimate outcome for an operator-added
        // model, and there is no soft-warning channel in this loader.
        for (var i = 0; i < s.Models.Available.Count; i++)
        {
            var entry = s.Models.Available[i];
            if (string.IsNullOrWhiteSpace(entry.Id))
                errors.Add($"models.available[{i}].id must not be empty.");
            if (string.IsNullOrWhiteSpace(entry.DisplayName))
                errors.Add($"models.available[{i}].displayName must not be empty.");
            if (entry.ContextWindowTokens <= 0)
                errors.Add($"models.available[{i}].contextWindowTokens must be positive, got {entry.ContextWindowTokens}.");
        }

        // MCP
        if (s.Mcp.SidecarConnectTimeoutSeconds is < 1 or > 120)
            errors.Add($"mcp.sidecarConnectTimeoutSeconds must be in [1, 120], got {s.Mcp.SidecarConnectTimeoutSeconds}.");
        if (s.Mcp.ApprovalChannelDisposeTimeoutSeconds is < 1 or > 60)
            errors.Add($"mcp.approvalChannelDisposeTimeoutSeconds must be in [1, 60], got {s.Mcp.ApprovalChannelDisposeTimeoutSeconds}.");

        // Tasks — Phase 7 lock TTL/wait. Both must be positive; allow up to 1h for power users.
        if (s.Tasks.LockTtlSeconds is < 1 or > 3600)
            errors.Add($"tasks.lockTtlSeconds must be in [1, 3600], got {s.Tasks.LockTtlSeconds}.");
        if (s.Tasks.LockMaxWaitSeconds is < 1 or > 3600)
            errors.Add($"tasks.lockMaxWaitSeconds must be in [1, 3600], got {s.Tasks.LockMaxWaitSeconds}.");
        // Cross-rule: lock TTL must not exceed approval timeout. ApprovalChannel.RunServerAsync
        // serialises ALL pipe traffic per session, so a long-running approval blocks same-session
        // lock-release messages. As long as TTL <= approval timeout, peer sessions waiting on
        // the lock can rely on the TTL falling first; tuning TTL above approval.timeoutSeconds
        // would expose real lock starvation. Skip when either side is itself invalid — the
        // dedicated rule above will report the actual problem more clearly.
        var lockTtlInRange = s.Tasks.LockTtlSeconds is >= 1 and <= 3600;
        var approvalTimeoutInRange = s.Approval.TimeoutSeconds is >= 5 and <= 600;
        if (lockTtlInRange && approvalTimeoutInRange && s.Tasks.LockTtlSeconds > s.Approval.TimeoutSeconds)
            errors.Add($"tasks.lockTtlSeconds ({s.Tasks.LockTtlSeconds}) must be <= approval.timeoutSeconds ({s.Approval.TimeoutSeconds}); a long approval would otherwise starve same-session lock releases.");

        // Tasks — Phase 8 context thresholds. Both in (0, 100); soft strictly less than hard.
        if (s.Tasks.SoftThresholdPct is <= 0 or >= 100)
            errors.Add($"tasks.softThresholdPct must be in (0, 100), got {s.Tasks.SoftThresholdPct}.");
        if (s.Tasks.HardThresholdPct is <= 0 or >= 100)
            errors.Add($"tasks.hardThresholdPct must be in (0, 100), got {s.Tasks.HardThresholdPct}.");
        if (s.Tasks.SoftThresholdPct >= s.Tasks.HardThresholdPct)
            errors.Add($"tasks.softThresholdPct ({s.Tasks.SoftThresholdPct}) must be strictly less than tasks.hardThresholdPct ({s.Tasks.HardThresholdPct}).");

        // Task 0012 finding #2: large-context threshold pair has the same constraints.
        if (s.Tasks.SoftThresholdPctLargeContext is <= 0 or >= 100)
            errors.Add($"tasks.softThresholdPctLargeContext must be in (0, 100), got {s.Tasks.SoftThresholdPctLargeContext}.");
        if (s.Tasks.HardThresholdPctLargeContext is <= 0 or >= 100)
            errors.Add($"tasks.hardThresholdPctLargeContext must be in (0, 100), got {s.Tasks.HardThresholdPctLargeContext}.");
        if (s.Tasks.SoftThresholdPctLargeContext >= s.Tasks.HardThresholdPctLargeContext)
            errors.Add($"tasks.softThresholdPctLargeContext ({s.Tasks.SoftThresholdPctLargeContext}) must be strictly less than tasks.hardThresholdPctLargeContext ({s.Tasks.HardThresholdPctLargeContext}).");
        if (s.Tasks.LargeContextWindowTokens < 0)
            errors.Add($"tasks.largeContextWindowTokens must be >= 0, got {s.Tasks.LargeContextWindowTokens}.");

        foreach (var (model, window) in s.Tasks.ModelContextWindows)
        {
            if (string.IsNullOrWhiteSpace(model))
                errors.Add("tasks.modelContextWindows contains a blank model id.");
            if (window <= 0)
                errors.Add($"tasks.modelContextWindows['{model}'] must be positive, got {window}.");
        }

        // Versioning — design doc §7.3 hard floor: must be parseable; min must be <= tested.
        if (!Sessions.ClaudeVersionPolicy.TryParse(s.Versioning.MinClaudeVersion, out _))
            errors.Add($"versioning.minClaudeVersion '{s.Versioning.MinClaudeVersion}' is not a valid version triplet.");
        if (!Sessions.ClaudeVersionPolicy.TryParse(s.Versioning.TestedAgainstVersion, out _))
            errors.Add($"versioning.testedAgainstVersion '{s.Versioning.TestedAgainstVersion}' is not a valid version triplet.");
    }

    private static bool IsKnownLogLevel(string level) =>
        level.Equals("Verbose", StringComparison.OrdinalIgnoreCase)
        || level.Equals("Debug", StringComparison.OrdinalIgnoreCase)
        || level.Equals("Information", StringComparison.OrdinalIgnoreCase)
        || level.Equals("Warning", StringComparison.OrdinalIgnoreCase)
        || level.Equals("Error", StringComparison.OrdinalIgnoreCase)
        || level.Equals("Fatal", StringComparison.OrdinalIgnoreCase);
}
