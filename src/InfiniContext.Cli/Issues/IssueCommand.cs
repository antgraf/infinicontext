using InfiniContext.Cli.Tasks;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Cli.Issues;

/// <summary>
/// Pure parser for <c>/issue ...</c> REPL input. Returns a discriminated record the handler can
/// dispatch on. Linked into the test assembly via <c>&lt;Compile Include&gt;</c> so parsing edge
/// cases can be exercised without spawning the CLI.
///
/// <para>
/// Recognized forms (from the v1 design):
/// </para>
/// <list type="bullet">
///   <item><c>/issue</c> or <c>/issue help</c> -> <see cref="Help"/></item>
///   <item><c>/issue "&lt;title&gt;" [--kind &lt;k&gt;] [--where &lt;ref&gt;]</c> -> <see cref="Add"/></item>
///   <item><c>/issue list</c> -> <see cref="List"/></item>
///   <item><c>/issue resolve &lt;n&gt; [--as &lt;status&gt;] [--note "&lt;text&gt;" | &lt;trailing note...&gt;]</c> -> <see cref="Resolve"/></item>
/// </list>
///
/// <para>
/// Subcommand keywords (<c>list</c>, <c>resolve</c>, <c>help</c>) are reserved - if the first
/// non-flag token is one of them, that subcommand is dispatched. Otherwise the input is treated
/// as a capture (<see cref="Add"/>) with the leading tokens (up to the first <c>--flag</c>) as
/// the title.
/// </para>
/// </summary>
internal abstract record IssueCommand
{
    public sealed record Add(string Title, IssueKind Kind, string? Where) : IssueCommand;
    public sealed record List : IssueCommand;
    public sealed record Resolve(int LocalId, IssueStatus Status, string? Note) : IssueCommand;
    public sealed record Help : IssueCommand;

    /// <summary>
    /// Parse a full REPL line. Accepts both <c>"/issue ..."</c> (the form Repl passes in) and
    /// bare <c>"..."</c> (handy in tests / the <c>/task issue</c> alias). Returns
    /// <see cref="Help"/> for the bare command; <c>false</c> with an error for malformed input.
    /// </summary>
    public static bool TryParse(string raw, out IssueCommand command, out string? error)
    {
        command = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            command = new Help();
            return true;
        }

        var tokens = TaskCommand.Tokenize(raw.Trim());
        var i = 0;
        if (tokens.Count > 0 && tokens[0].Equals("/issue", StringComparison.OrdinalIgnoreCase))
            i = 1;

        if (i >= tokens.Count)
        {
            command = new Help();
            return true;
        }

        var first = tokens[i].ToLowerInvariant();
        switch (first)
        {
            case "help":
            case "?":
            case "-h":
            case "--help":
                command = new Help();
                return true;

            case "list":
            case "ls":
                if (i + 1 < tokens.Count)
                {
                    error = "/issue list takes no arguments.";
                    return false;
                }
                command = new List();
                return true;

            case "resolve":
                return ParseResolve(tokens, i + 1, out command, out error);

            default:
                return ParseAdd(tokens, i, out command, out error);
        }
    }

    private static bool ParseAdd(IReadOnlyList<string> tokens, int i, out IssueCommand command, out string? error)
    {
        command = null!;
        error = null;

        // Title tokens run from i up to the first '--<flag>' token. The tokenizer already groups
        // quoted strings into one token, so a quoted title is just tokens[i] alone; an unquoted
        // multi-word title is multiple tokens that we join with single spaces.
        var titleStart = i;
        while (i < tokens.Count && !IsFlag(tokens[i])) i++;
        var titleEnd = i;

        if (titleStart >= titleEnd)
        {
            error = "/issue capture requires a title. Usage: /issue \"<title>\" [--kind <k>] [--where <ref>]";
            return false;
        }

        var title = string.Join(' ', tokens.Skip(titleStart).Take(titleEnd - titleStart)).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            error = "/issue capture requires a non-empty title.";
            return false;
        }

        var kind = IssueKind.Followup;
        string? where = null;

        while (i < tokens.Count)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "--kind":
                    if (++i >= tokens.Count) { error = "--kind requires a value (bug | smell | followup | question)."; return false; }
                    var parsedKind = IssueMarkdown.TryParseKind(tokens[i]);
                    if (parsedKind is null) { error = $"--kind: '{tokens[i]}' is not one of bug | smell | followup | question."; return false; }
                    kind = parsedKind.Value;
                    break;

                case "--where":
                    if (++i >= tokens.Count) { error = "--where requires a value (e.g. path/to/file.cs:42)."; return false; }
                    where = tokens[i];
                    break;

                default:
                    error = $"unknown option '{tokens[i]}' for /issue capture.";
                    return false;
            }
            i++;
        }

        command = new Add(title, kind, where);
        return true;
    }

    private static bool ParseResolve(IReadOnlyList<string> tokens, int i, out IssueCommand command, out string? error)
    {
        command = null!;
        error = null;

        if (i >= tokens.Count)
        {
            error = "/issue resolve requires a local id. Usage: /issue resolve <n> [--as <status>] [<note...>]";
            return false;
        }

        if (!int.TryParse(tokens[i], out var localId) || localId <= 0)
        {
            error = $"/issue resolve: '{tokens[i]}' is not a valid positive local id.";
            return false;
        }
        i++;

        var status = IssueStatus.Resolved;
        string? explicitNote = null;
        var trailing = new List<string>();

        while (i < tokens.Count)
        {
            var lower = tokens[i].ToLowerInvariant();
            switch (lower)
            {
                case "--as":
                    if (++i >= tokens.Count) { error = "--as requires a value (resolved | wont-fix | deferred)."; return false; }
                    var parsedStatus = IssueMarkdown.TryParseStatus(tokens[i]);
                    if (parsedStatus is null || parsedStatus == IssueStatus.Open)
                    {
                        error = $"--as: '{tokens[i]}' is not one of resolved | wont-fix | deferred.";
                        return false;
                    }
                    status = parsedStatus.Value;
                    i++;
                    break;

                case "--note":
                    if (++i >= tokens.Count) { error = "--note requires a value."; return false; }
                    explicitNote = tokens[i];
                    i++;
                    break;

                default:
                    // Trailing text is treated as a note. Accumulate every remaining token (even
                    // ones that look like flags) so a hyphenated word in the middle of a note
                    // doesn't get misparsed - the user can always use --note "<text>" when they
                    // need that level of precision.
                    trailing.Add(tokens[i]);
                    i++;
                    break;
            }
        }

        // --note takes precedence over trailing text (lets a scripted caller pin a known string).
        var note = explicitNote ?? (trailing.Count > 0 ? string.Join(' ', trailing) : null);
        if (note is not null) note = note.Trim();
        if (string.IsNullOrWhiteSpace(note)) note = null;

        command = new Resolve(localId, status, note);
        return true;
    }

    private static bool IsFlag(string token) =>
        token.Length >= 2 && token.StartsWith("--", StringComparison.Ordinal);
}
