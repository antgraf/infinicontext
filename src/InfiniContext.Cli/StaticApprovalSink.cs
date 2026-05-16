using InfiniContext.Core.Mcp;

namespace InfiniContext.Cli;

/// <summary>
/// Constant-decision sink used by the <c>deny</c> and <c>allow</c> approval modes. Returns the
/// configured <see cref="ApprovalBehavior"/> for every request without prompting or calling out.
/// <c>allow</c> is intentionally available only via CLI flag or REPL command — see
/// <see cref="Configuration.InfiniContextSettingsLoader"/> for why it isn't a persisted default.
/// </summary>
internal sealed class StaticApprovalSink : IApprovalSink
{
    private readonly ApprovalBehavior _behavior;
    private readonly string _denyMessage;

    public StaticApprovalSink(ApprovalBehavior behavior, string? denyMessage = null)
    {
        _behavior = behavior;
        _denyMessage = denyMessage ?? "auto-deny (approval mode=deny)";
    }

    public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(_behavior == ApprovalBehavior.Allow
            ? ApprovalResponse.Allow(request.Input)
            : ApprovalResponse.Deny(_denyMessage));
}
