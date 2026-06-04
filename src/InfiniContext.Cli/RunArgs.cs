using InfiniContext.Core.Configuration;

namespace InfiniContext.Cli;

/// <summary>
/// Parsed argv for <c>infinicontext run …</c>. A bare <c>--</c> separator routes everything
/// after it into <see cref="ExtraArgs"/>, which is forwarded verbatim to <c>claude</c>.
/// </summary>
internal sealed record RunArgs(
    string Cwd,
    string? Model,
    string? ResumeSessionId,
    bool IncludePartialMessages,
    string? ClaudeExecutable,
    IReadOnlyList<string> ExtraArgs,
    string? SettingsPath,
    ApprovalMode? ApprovalMode)
{
    public static bool TryParse(IReadOnlyList<string> tokens, out RunArgs result, out string? error)
    {
        result = null!;
        error = null;

        string? cwd = null;
        string? model = null;
        string? resumeId = null;
        var includePartial = false;
        string? claudeExe = null;
        string? settingsPath = null;
        ApprovalMode? approvalMode = null;
        IReadOnlyList<string> extra = Array.Empty<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            if (t == "--")
            {
                var rest = new List<string>(tokens.Count - i - 1);
                for (var j = i + 1; j < tokens.Count; j++) rest.Add(tokens[j]);
                extra = rest;
                break;
            }

            switch (t)
            {
                case "--resume":
                    if (i + 1 >= tokens.Count) { error = "--resume requires a session id"; return false; }
                    resumeId = tokens[++i];
                    break;
                case "--model":
                    if (i + 1 >= tokens.Count) { error = "--model requires a name"; return false; }
                    model = tokens[++i];
                    break;
                case "--include-partial-messages":
                    includePartial = true;
                    break;
                case "--claude-exe":
                    if (i + 1 >= tokens.Count) { error = "--claude-exe requires a path"; return false; }
                    claudeExe = tokens[++i];
                    break;
                case "--settings":
                    if (i + 1 >= tokens.Count) { error = "--settings requires a path"; return false; }
                    settingsPath = tokens[++i];
                    break;
                case "--approval-mode":
                    if (i + 1 >= tokens.Count) { error = "--approval-mode requires one of: ask, deny, allow, auto"; return false; }
                    if (!TryParseApprovalMode(tokens[++i], out var mode))
                    {
                        error = $"--approval-mode: '{tokens[i]}' is not one of ask, deny, allow, auto";
                        return false;
                    }
                    approvalMode = mode;
                    break;
                default:
                    if (t.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"unknown option {t}";
                        return false;
                    }
                    if (cwd is not null)
                    {
                        error = $"unexpected positional argument '{t}' (cwd already set to '{cwd}')";
                        return false;
                    }
                    cwd = t;
                    break;
            }
        }

        cwd ??= Directory.GetCurrentDirectory();
        cwd = Path.GetFullPath(cwd);
        if (!Directory.Exists(cwd))
        {
            error = $"cwd does not exist or is not a directory: {cwd}";
            return false;
        }
        result = new RunArgs(cwd, model, resumeId, includePartial, claudeExe, extra, settingsPath, approvalMode);
        return true;
    }

    public static bool TryParseApprovalMode(string s, out ApprovalMode mode) =>
        InfiniContext.Core.Configuration.ApprovalModeParser.TryParse(s, out mode);
}
