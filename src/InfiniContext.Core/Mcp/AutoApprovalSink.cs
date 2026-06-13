using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using Serilog;

namespace InfiniContext.Core.Mcp;

/// <summary>
/// Approval sink for the <c>auto</c> mode: delegates the decision to <see cref="ILightModelService.ReviewToolCallSafetyAsync"/>.
///
/// <para>Decision flow:</para>
/// <list type="number">
///   <item>Call the light model with the tool name + input + cwd.</item>
///   <item>If the model says <c>deny</c> -> deny with the model's reason and axis.</item>
///   <item>If the model says <c>allow</c> with high/medium confidence -> allow.</item>
///   <item>If the model says <c>allow</c> with <c>low</c> confidence, OR the call times out, OR
///   the call throws -- apply <see cref="AutoApprovalOptions.OnLowConfidence"/>: either deny
///   (fail closed, default) or fall back to the wrapped <see cref="IApprovalSink"/>
///   (the slot-based ask sink in multi-session, e.g. <see cref="SlotApprovalSink"/>).</item>
/// </list>
/// </summary>
public sealed class AutoApprovalSink : IApprovalSink
{
    private readonly ILightModelService _lightModel;
    private readonly IApprovalSink _fallback;
    private readonly AutoApprovalOptions _options;
    private readonly string _cwd;
    private readonly bool _underVersionControl;
    private readonly ILogger _logger;

    public AutoApprovalSink(
        ILightModelService lightModel,
        IApprovalSink fallback,
        AutoApprovalOptions options,
        string cwd,
        ILogger logger)
    {
        _lightModel = lightModel;
        _fallback = fallback;
        _options = options;
        _cwd = cwd;
        // M4: verify the version-control fact host-side once (cwd is fixed per sink) and pass it to
        // the safety review, instead of letting the prompt assume the cwd is under VCS.
        _underVersionControl = IsUnderVersionControl(cwd);
        _logger = logger;
    }

    /// <summary>Test-only accessor -- lets factory tests assert which back end was wired.</summary>
    internal ILightModelService LightModel => _lightModel;

    public async Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        SafetyReview review;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            review = await _lightModel.ReviewToolCallSafetyAsync(
                new SafetyReviewRequest(request.ToolName, request.Input, _cwd, UnderVersionControl: _underVersionControl),
                linked.Token).ConfigureAwait(false);
            sw.Stop();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.Warning(
                ex,
                "auto-approval: light-model call failed for tool={Tool} after {Latency}ms",
                request.ToolName, sw.ElapsedMilliseconds);
            return await ApplyFallbackAsync(request, cancellationToken,
                $"auto: light-model error ({ex.GetType().Name})").ConfigureAwait(false);
        }

        _logger.Information(
            "auto-approval: tool={Tool} decision={Decision} confidence={Confidence} axis={Axis} latency={Latency}ms reason={Reason}",
            request.ToolName, review.Decision, review.Confidence, review.Axis, sw.ElapsedMilliseconds, review.Reason);

        if (review.Decision == SafetyDecision.Deny)
            return ApprovalResponse.Deny($"auto-deny [{review.Axis}]: {review.Reason}");

        // M3: an "allow" that still flags prompt_injection is self-contradictory — the model saw
        // injection-shaped text yet cleared the call. Don't trust it: a single cheap model judging
        // adversarial text it must also obey is a weak gate, so escalate to the human fallback
        // rather than auto-allowing.
        if (review.Axis == SafetyAxis.PromptInjection)
            return await ApplyFallbackAsync(request, cancellationToken,
                $"auto: allow flagged prompt_injection — escalating ({review.Reason})").ConfigureAwait(false);

        if (review.Confidence == SafetyConfidence.Low)
            return await ApplyFallbackAsync(request, cancellationToken,
                $"auto: low-confidence allow ({review.Reason})").ConfigureAwait(false);

        return ApprovalResponse.Allow(request.Input);
    }

    private Task<ApprovalResponse> ApplyFallbackAsync(ApprovalRequest request, CancellationToken ct, string denyReason) =>
        _options.OnLowConfidence switch
        {
            LowConfidenceFallback.FallbackToAsk => _fallback.RequestAsync(request, ct),
            _ => Task.FromResult(ApprovalResponse.Deny(denyReason)),
        };

    /// <summary>
    /// M4: does <paramref name="cwd"/> sit inside a git working tree? Walks up the directory chain
    /// looking for a <c>.git</c> directory (normal clone) or file (a worktree/submodule gitlink).
    /// Conservative: any error or unreadable path returns false so the safety gate falls back to its
    /// strict "treat writes as destructive" default rather than over-trusting an unverified cwd.
    /// </summary>
    internal static bool IsUnderVersionControl(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return false;
        try
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(cwd)); dir is not null; dir = dir.Parent)
            {
                var gitPath = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath)) return true;
            }
        }
        catch
        {
            // Malformed path, permission error, etc. — fail closed to the strict default.
        }
        return false;
    }
}
