using InfiniContext.Core.Configuration;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;

namespace InfiniContext.Cli;

/// <summary>
/// Stage 2B Phase 5 approval sink for the <c>Ask</c> mode. Routes each request into the session's
/// <see cref="PendingApprovalSlot"/> instead of reading <c>Console.In</c> directly — the host UI
/// is the only thing that owns stdin, and it routes the user's <c>y</c>/<c>n</c> to the focused
/// session's slot. This is what removes the W-16 race entirely (the legacy
/// <c>ConsoleApprovalSink</c> with its <c>Task.Run(Console.In.ReadLine)</c> is deleted in this
/// phase, not patched): the offending background read no longer exists.
///
/// <para>
/// Banner/result text is appended to the session's <see cref="TranscriptModel"/> so it lives in
/// the same fragment pipeline as transcript content — the focused pane echoes it live; off-screen
/// sessions accumulate the banner so a later <c>/focus</c> replay shows the pending request.
/// </para>
/// </summary>
internal sealed class SlotApprovalSink : IApprovalSink
{
    private readonly PendingApprovalSlot _slot;
    private readonly TranscriptModel _transcript;
    private readonly TimeSpan _timeout;
    private readonly ApprovalCosmetics _cosmetics;

    public SlotApprovalSink(
        PendingApprovalSlot slot,
        TranscriptModel transcript,
        TimeSpan? timeout = null,
        ApprovalCosmetics? cosmetics = null)
    {
        _slot = slot ?? throw new ArgumentNullException(nameof(slot));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        _cosmetics = cosmetics ?? new ApprovalCosmetics();
    }

    public async Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var summary = request.Input.ToJsonString();
        if (summary.Length > 240) summary = summary[..240] + "…";

        _transcript.Append($"\n[approval] tool={request.ToolName} input={summary}\n");
        _transcript.Append($"[approval] approve? (y/N, {_timeout.TotalSeconds:F0}s timeout)\n");

        ApprovalResponse response;
        try
        {
            response = await _slot.RequestAsync(request, _timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _transcript.Append(_cosmetics.DeniedMessage + "\n");
            throw;
        }

        var line = response.Behavior == ApprovalBehavior.Allow
            ? _cosmetics.ApprovedMessage
            : response.Message == "user did not respond within timeout"
                ? _cosmetics.TimeoutMessage
                : _cosmetics.DeniedMessage;
        _transcript.Append(line + "\n");
        return response;
    }
}
