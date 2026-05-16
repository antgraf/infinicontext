namespace InfiniContext.Core.Mcp;

/// <summary>
/// Host's pluggable approval handler. The CLI implements this by prompting the user; tests stub it.
/// </summary>
public interface IApprovalSink
{
    Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}
