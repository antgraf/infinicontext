using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Mcp;
using Serilog;

namespace InfiniContext.Cli;

/// <summary>
/// Approval sink for the <c>auto</c> mode: delegates the decision to <see cref="ILightModelService.ReviewToolCallSafetyAsync"/>.
///
/// <para>Decision flow:</para>
/// <list type="number">
///   <item>Call the light model with the tool name + input + cwd.</item>
///   <item>If the model says <c>deny</c> → deny with the model's reason and axis.</item>
///   <item>If the model says <c>allow</c> with high/medium confidence → allow.</item>
///   <item>If the model says <c>allow</c> with <c>low</c> confidence, OR the call times out, OR
///   the call throws — apply <see cref="AutoApprovalOptions.OnLowConfidence"/>: either deny
///   (fail closed, default) or fall back to the wrapped <see cref="IApprovalSink"/>
///   (the slot-based ask sink in multi-session, e.g. <see cref="SlotApprovalSink"/>).</item>
/// </list>
/// </summary>
internal sealed class AutoApprovalSink : IApprovalSink
{
    private readonly ILightModelService _lightModel;
    private readonly IApprovalSink _fallback;
    private readonly AutoApprovalOptions _options;
    private readonly string _cwd;
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
        _logger = logger;
    }

    /// <summary>Test-only accessor — lets factory tests assert which back end was wired.</summary>
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
                new SafetyReviewRequest(request.ToolName, request.Input, _cwd),
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
}
