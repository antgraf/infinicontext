using System.Text;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Cli.Tasks;

/// <summary>
/// Pure parser for <c>/task &lt;subcommand&gt; …</c> REPL input. Returns a discriminated record
/// the handler can dispatch on. Linked into the test assembly via <c>&lt;Compile Include&gt;</c>
/// so parsing edge cases can be exercised without spawning the CLI.
/// </summary>
internal abstract record TaskCommand
{
    public sealed record New(string Description, string? Home = null) : TaskCommand;
    public sealed record List(int? Limit, TaskItemStatus? Status) : TaskCommand;
    public sealed record Show(int Id) : TaskCommand;
    public sealed record Fork(int Id, string? OverrideTitle) : TaskCommand;
    public sealed record Merge(IReadOnlyList<int> Ids) : TaskCommand;
    public sealed record Switch(int Id) : TaskCommand;
    public sealed record Spawn(int Id) : TaskCommand;
    public sealed record Delete(int Id, bool RemoveFolder) : TaskCommand;
    public sealed record Review(int? Id, string? Scope, bool SkipConfirm) : TaskCommand;
    public sealed record Flush : TaskCommand;
    public sealed record Help : TaskCommand;

    /// <summary>
    /// Parse a full REPL line. Accepts both <c>"/task &lt;subcmd&gt; …"</c> (the form Repl passes
    /// in) and bare <c>"&lt;subcmd&gt; …"</c> (handy in tests). Returns <see cref="Help"/> for
    /// <c>/task</c> and <c>/task help</c>; returns <c>false</c> with an error for malformed input.
    /// </summary>
    public static bool TryParse(string raw, out TaskCommand command, out string? error)
    {
        command = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            command = new Help();
            return true;
        }

        var tokens = Tokenize(raw.Trim());
        var i = 0;
        if (tokens.Count > 0 && tokens[0].Equals("/task", StringComparison.OrdinalIgnoreCase))
            i = 1;

        if (i >= tokens.Count)
        {
            command = new Help();
            return true;
        }

        var sub = tokens[i].ToLowerInvariant();
        i++;

        switch (sub)
        {
            case "help":
            case "?":
            case "-h":
            case "--help":
                command = new Help();
                return true;

            case "new":
                return ParseNew(tokens, i, raw, out command, out error);

            case "list":
            case "ls":
                return ParseList(tokens, i, out command, out error);

            case "show":
            case "get":
                return ParseSingleId(tokens, i, sub, id => new Show(id), out command, out error);

            case "fork":
                return ParseFork(tokens, i, out command, out error);

            case "merge":
                return ParseMerge(tokens, i, out command, out error);

            case "switch":
                return ParseSingleId(tokens, i, sub, id => new Switch(id), out command, out error);

            case "spawn":
                return ParseSingleId(tokens, i, sub, id => new Spawn(id), out command, out error);

            case "delete":
            case "rm":
                return ParseDelete(tokens, i, out command, out error);

            case "review":
                return ParseReview(tokens, i, out command, out error);

            case "flush":
                if (i < tokens.Count)
                {
                    error = "/task flush: unexpected arguments.";
                    return false;
                }
                command = new Flush();
                return true;

            default:
                error = $"unknown subcommand '{sub}'. Try /task help.";
                return false;
        }
    }

    private static bool ParseNew(IReadOnlyList<string> tokens, int i, string rawLine, out TaskCommand command, out string? error)
    {
        command = null!;
        error = null;

        // Walk the args separating the optional --home <path> flag from the description tokens.
        // The flag may appear either before or after the description so it isn't lost in a long
        // multi-paragraph description body.
        string? home = null;
        var descTokens = new List<string>();
        for (; i < tokens.Count; i++)
        {
            if (tokens[i].Equals("--home", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count) { error = "--home requires a path."; return false; }
                home = tokens[++i];
                if (string.IsNullOrWhiteSpace(home)) { error = "--home: path may not be empty."; return false; }
                continue;
            }
            descTokens.Add(tokens[i]);
        }

        if (descTokens.Count == 0)
        {
            error = "/task new requires a description.";
            return false;
        }

        // No flag → preserve byte-exact whitespace via the raw-line fallback (legacy behavior).
        // With flag → joining tokens with single spaces is acceptable; descriptions are free-form
        // text and the flag insertion already broke any whitespace claim.
        string description;
        if (descTokens.Count == 1)
        {
            description = descTokens[0];
        }
        else if (home is null)
        {
            description = ExtractDescriptionFromRaw(rawLine);
        }
        else
        {
            description = string.Join(' ', descTokens);
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            error = "/task new requires a non-empty description.";
            return false;
        }

        command = new New(description.Trim(), home);
        return true;
    }

    /// <summary>
    /// Pull the description from the raw line so multi-word unquoted input survives the
    /// tokenizer. Looks for "<c>new</c>" (case-insensitive) and returns the rest of the line.
    /// </summary>
    private static string ExtractDescriptionFromRaw(string rawLine)
    {
        // "/task new <stuff>" or "new <stuff>"
        var trimmed = rawLine.TrimStart();
        if (trimmed.StartsWith("/task ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[6..].TrimStart();
        if (trimmed.StartsWith("new ", StringComparison.OrdinalIgnoreCase))
            return trimmed[4..].Trim().Trim('"', '\'');
        return trimmed.Trim().Trim('"', '\'');
    }

    private static bool ParseList(IReadOnlyList<string> tokens, int i, out TaskCommand command, out string? error)
    {
        command = null!;
        error = null;

        int? limit = null;
        TaskItemStatus? status = null;

        while (i < tokens.Count)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "--limit":
                    if (++i >= tokens.Count) { error = "--limit requires a positive integer."; return false; }
                    if (!int.TryParse(tokens[i], out var n) || n <= 0) { error = $"--limit: '{tokens[i]}' is not a positive integer."; return false; }
                    limit = n;
                    break;

                case "--status":
                    if (++i >= tokens.Count) { error = "--status requires one of: inprogress, idle."; return false; }
                    if (!TryParseStatus(tokens[i], out var parsed)) { error = $"--status: '{tokens[i]}' is not one of inprogress, idle."; return false; }
                    status = parsed;
                    break;

                default:
                    error = $"unknown option '{tokens[i]}' for /task list.";
                    return false;
            }
            i++;
        }

        command = new List(limit, status);
        return true;
    }

    private static bool ParseSingleId(IReadOnlyList<string> tokens, int i, string subName, Func<int, TaskCommand> ctor, out TaskCommand command, out string? error)
    {
        command = null!;
        error = null;

        if (i >= tokens.Count) { error = $"/task {subName} requires a task id."; return false; }
        if (!TryParseId(tokens[i], out var id)) { error = $"/task {subName}: '{tokens[i]}' is not a valid task id."; return false; }
        if (i + 1 < tokens.Count) { error = $"/task {subName}: unexpected extra arguments after the id."; return false; }

        command = ctor(id);
        return true;
    }

    private static bool ParseFork(IReadOnlyList<string> tokens, int i, out TaskCommand command, out string? error)
    {
        command = null!;
        error = null;

        if (i >= tokens.Count) { error = "/task fork requires a source task id."; return false; }
        if (!TryParseId(tokens[i], out var id)) { error = $"/task fork: '{tokens[i]}' is not a valid task id."; return false; }
        i++;

        string? title = null;
        if (i < tokens.Count) title = tokens[i++];
        if (i < tokens.Count) { error = "/task fork: too many arguments. Usage: /task fork <id> [\"<title>\"]"; return false; }

        command = new Fork(id, title);
        return true;
    }

    private static bool ParseDelete(IReadOnlyList<string> tokens, int i, out TaskCommand command, out string? error)
    {
        command = null!;
        error = null;

        int? id = null;
        var removeFolder = false;

        // Accept --remove-folder either before or after the id, matching the /task new
        // before-or-after pattern. The id is the only positional argument.
        for (; i < tokens.Count; i++)
        {
            if (tokens[i].Equals("--remove-folder", StringComparison.OrdinalIgnoreCase))
            {
                removeFolder = true;
                continue;
            }
            if (id is not null) { error = "/task delete: unexpected extra arguments after the id."; return false; }
            if (!TryParseId(tokens[i], out var parsed)) { error = $"/task delete: '{tokens[i]}' is not a valid task id."; return false; }
            id = parsed;
        }

        if (id is null) { error = "/task delete requires a task id."; return false; }

        command = new Delete(id.Value, removeFolder);
        return true;
    }

    /// <summary>
    /// Parse <c>/task review [&lt;id&gt;] [--scope &lt;glob&gt;] [--yes]</c>. The id is optional тАФ
    /// the handler defaults it to the focused session's active task when omitted.
    /// </summary>
    private static bool ParseReview(IReadOnlyList<string> tokens, int i, out TaskCommand command, out string? error)
    {
        command = null!;
        error = null;

        int? id = null;
        string? scope = null;
        var skipConfirm = false;

        for (; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (tok.Equals("--yes", StringComparison.OrdinalIgnoreCase) || tok.Equals("-y", StringComparison.OrdinalIgnoreCase))
            {
                skipConfirm = true;
                continue;
            }
            if (tok.Equals("--scope", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count) { error = "--scope requires a glob expression."; return false; }
                scope = tokens[++i];
                if (string.IsNullOrWhiteSpace(scope)) { error = "--scope: glob expression may not be empty."; return false; }
                continue;
            }
            if (tok.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"/task review: unknown option '{tok}'.";
                return false;
            }
            if (id is not null) { error = "/task review: unexpected extra arguments after the id."; return false; }
            if (!TryParseId(tok, out var parsed)) { error = $"/task review: '{tok}' is not a valid task id."; return false; }
            id = parsed;
        }

        command = new Review(id, scope, skipConfirm);
        return true;
    }

    private static bool ParseMerge(IReadOnlyList<string> tokens, int i, out TaskCommand command, out string? error)
    {
        command = null!;
        error = null;

        var ids = new List<int>();
        for (; i < tokens.Count; i++)
        {
            if (!TryParseId(tokens[i], out var id)) { error = $"/task merge: '{tokens[i]}' is not a valid task id."; return false; }
            ids.Add(id);
        }
        if (ids.Count < 2) { error = "/task merge requires at least two source ids."; return false; }

        command = new Merge(ids);
        return true;
    }

    /// <summary>
    /// Accept either decimal (<c>42</c>) or zero-padded (<c>0042</c>) ids — the latter is the
    /// form humans see in <c>/task list</c> output.
    /// </summary>
    public static bool TryParseId(string s, out int id) =>
        int.TryParse(s, out id) && id > 0;

    private static bool TryParseStatus(string s, out TaskItemStatus status)
    {
        switch (s.ToLowerInvariant())
        {
            case "inprogress": status = TaskItemStatus.InProgress; return true;
            case "in-progress": status = TaskItemStatus.InProgress; return true;
            case "active": status = TaskItemStatus.InProgress; return true;
            case "idle": status = TaskItemStatus.Idle; return true;
            default: status = default; return false;
        }
    }

    /// <summary>
    /// Tokenize on whitespace, treating <c>"…"</c> and <c>'…'</c> as a single token whose
    /// surrounding quotes are stripped. Mismatched quotes consume to end-of-input.
    /// </summary>
    public static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        var quoteChar = '\0';

        foreach (var c in s)
        {
            if (inQuote)
            {
                if (c == quoteChar)
                {
                    inQuote = false;
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"' || c == '\'')
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                inQuote = true;
                quoteChar = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
