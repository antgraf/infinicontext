namespace InfiniContext.Core.Configuration;

/// <summary>
/// Parses the user-facing approval-mode tokens (<c>ask</c>/<c>deny</c>/<c>allow</c>/<c>auto</c>)
/// into <see cref="ApprovalMode"/>. Lives in Core so both the CLI argument parser
/// (<c>RunArgs</c>) and the host-shared <c>/approval</c> slash-command resolve the same tokens.
/// </summary>
public static class ApprovalModeParser
{
    public static bool TryParse(string s, out ApprovalMode mode)
    {
        switch (s.ToLowerInvariant())
        {
            case "ask": mode = ApprovalMode.Ask; return true;
            case "deny": mode = ApprovalMode.Deny; return true;
            case "allow": mode = ApprovalMode.Allow; return true;
            case "auto": mode = ApprovalMode.Auto; return true;
            default: mode = default; return false;
        }
    }
}
