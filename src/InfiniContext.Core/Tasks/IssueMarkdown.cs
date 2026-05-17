using System.Globalization;
using System.Text.RegularExpressions;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Parser + serializer helpers for the <c>issues.md</c> markdown format. Kept separate from
/// <see cref="IssueService"/> so the parser is testable on its own and the service stays focused
/// on I/O + lock orchestration.
///
/// <para>
/// Format reminder (one entry):
/// </para>
/// <code>
/// ## NNNN - &lt;title&gt;
/// - **Logged:** &lt;ISO 8601&gt; by &lt;source&gt;
/// - **Kind:** bug
/// - **Where:** path/to/file.cs:line
/// - **Status:** open
/// - **Body:** brief body text
/// </code>
///
/// <para>
/// The parser is intentionally permissive: missing optional fields (Where, Body, Resolution,
/// LoggedBy) yield <c>null</c>; unknown <c>- **Foo:**</c> lines are ignored. Unknown
/// <c>Kind:</c> / <c>Status:</c> values fall back to safe defaults (<see cref="IssueKind.Followup"/>
/// and <see cref="IssueStatus.Open"/>) so a hand-typed entry doesn't break List rendering.
/// </para>
/// </summary>
public static class IssueMarkdown
{
    public const string FileName = "issues.md";

    // Heading: "## 0003 - Some title". Tolerates 1+ digits (the 4-digit convention isn't
    // enforced). Separator is plain ASCII hyphen per the design doc - keep this strict so the
    // service round-trips deterministically.
    private static readonly Regex HeadingRegex = new(
        @"^##\s+(?<id>\d+)\s+-\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex FieldRegex = new(
        @"^-\s+\*\*(?<key>[A-Za-z]+):\*\*\s*(?<value>.*?)\s*$",
        RegexOptions.Compiled);

    /// <summary>Map enum to wire string for <see cref="IssueKind"/>.</summary>
    public static string KindString(IssueKind kind) => kind switch
    {
        IssueKind.Bug => "bug",
        IssueKind.Smell => "smell",
        IssueKind.Followup => "followup",
        IssueKind.Question => "question",
        _ => "followup",
    };

    /// <summary>Map wire string back to <see cref="IssueKind"/>. Returns null for unknown input.</summary>
    public static IssueKind? TryParseKind(string s) => s.Trim().ToLowerInvariant() switch
    {
        "bug" => IssueKind.Bug,
        "smell" => IssueKind.Smell,
        "followup" or "follow-up" or "follow up" => IssueKind.Followup,
        "question" => IssueKind.Question,
        _ => null,
    };

    public static string StatusString(IssueStatus status) => status switch
    {
        IssueStatus.Open => "open",
        IssueStatus.Resolved => "resolved",
        IssueStatus.WontFix => "wont-fix",
        IssueStatus.Deferred => "deferred",
        _ => "open",
    };

    public static IssueStatus? TryParseStatus(string s) => s.Trim().ToLowerInvariant() switch
    {
        "open" => IssueStatus.Open,
        "resolved" => IssueStatus.Resolved,
        "wont-fix" or "wontfix" or "won't fix" => IssueStatus.WontFix,
        "deferred" => IssueStatus.Deferred,
        _ => null,
    };

    /// <summary>
    /// Parse the local id out of a heading line. Returns null when the line isn't a heading.
    /// Public so <see cref="IssueService"/> can probe for max id without round-tripping a full
    /// parse.
    /// </summary>
    public static int? TryParseHeadingId(string line)
    {
        var m = HeadingRegex.Match(line);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;
        return id > 0 ? id : null;
    }

    /// <summary>True when <paramref name="line"/> is the field <c>- **{key}:** ...</c>.</summary>
    public static bool IsFieldLine(string line, string key)
    {
        var m = FieldRegex.Match(line);
        return m.Success && m.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse the full file content into entries. The header line and any free-form prose
    /// outside <c>## NNNN</c> blocks are ignored. Within a block, key-value lines (<c>- **Foo:**
    /// value</c>) are recognized; non-matching lines that follow a <c>- **Body:**</c> line are
    /// appended to the body so multi-line bodies survive round-trips.
    /// </summary>
    public static IReadOnlyList<IssueEntry> Parse(string content)
    {
        if (string.IsNullOrEmpty(content)) return Array.Empty<IssueEntry>();

        var entries = new List<IssueEntry>();
        var lines = content.Split('\n');

        EntryBuilder? current = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');

            var headingMatch = HeadingRegex.Match(raw);
            if (headingMatch.Success)
            {
                if (current is not null) entries.Add(current.Build());
                if (int.TryParse(headingMatch.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                {
                    current = new EntryBuilder(id, headingMatch.Groups["title"].Value);
                }
                else
                {
                    current = null;
                }
                continue;
            }

            if (current is null) continue;

            var fieldMatch = FieldRegex.Match(raw);
            if (fieldMatch.Success)
            {
                current.SetField(fieldMatch.Groups["key"].Value, fieldMatch.Groups["value"].Value);
            }
            else if (current.LastSetWasBody && !string.IsNullOrWhiteSpace(raw))
            {
                // Continuation line for a multi-line body: a non-empty line that's neither a
                // heading nor a field. Indented or not - we just concatenate.
                current.AppendBodyLine(raw.TrimStart());
            }
        }

        if (current is not null) entries.Add(current.Build());
        return entries;
    }

    private sealed class EntryBuilder
    {
        public int LocalId { get; }
        public string Title { get; }
        public DateTimeOffset LoggedAt { get; private set; } = DateTimeOffset.MinValue;
        public string? LoggedBy { get; private set; }
        public IssueKind Kind { get; private set; } = IssueKind.Followup;
        public string? Where { get; private set; }
        public IssueStatus Status { get; private set; } = IssueStatus.Open;
        public string? Body { get; private set; }
        public string? Resolution { get; private set; }
        public bool LastSetWasBody { get; private set; }

        public EntryBuilder(int localId, string title)
        {
            LocalId = localId;
            Title = title;
        }

        public void SetField(string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "logged":
                    (LoggedAt, LoggedBy) = ParseLogged(value);
                    LastSetWasBody = false;
                    break;
                case "kind":
                    Kind = TryParseKind(value) ?? IssueKind.Followup;
                    LastSetWasBody = false;
                    break;
                case "where":
                    Where = string.IsNullOrWhiteSpace(value) ? null : value;
                    LastSetWasBody = false;
                    break;
                case "status":
                    Status = TryParseStatus(value) ?? IssueStatus.Open;
                    LastSetWasBody = false;
                    break;
                case "body":
                    Body = string.IsNullOrWhiteSpace(value) ? null : value;
                    LastSetWasBody = true;
                    break;
                case "resolution":
                    Resolution = string.IsNullOrWhiteSpace(value) ? null : value;
                    LastSetWasBody = false;
                    break;
                default:
                    LastSetWasBody = false;
                    break;
            }
        }

        public void AppendBodyLine(string line)
        {
            Body = string.IsNullOrEmpty(Body) ? line : Body + "\n" + line;
        }

        public IssueEntry Build() =>
            new(LocalId, Title, LoggedAt, LoggedBy, Kind, Where, Status, Body, Resolution);

        private static (DateTimeOffset LoggedAt, string? LoggedBy) ParseLogged(string value)
        {
            // Format: "<ISO 8601> by <source>". The "by <source>" suffix is optional. We split on
            // " by " (case-insensitive) and parse the timestamp prefix; on parse failure the
            // timestamp falls back to MinValue and the whole field is kept verbatim as LoggedBy.
            var trimmed = value.Trim();
            var byIdx = trimmed.IndexOf(" by ", StringComparison.OrdinalIgnoreCase);
            string tsPart;
            string? loggedBy;
            if (byIdx >= 0)
            {
                tsPart = trimmed[..byIdx].Trim();
                loggedBy = trimmed[(byIdx + 4)..].Trim();
                if (loggedBy.Length == 0) loggedBy = null;
            }
            else
            {
                tsPart = trimmed;
                loggedBy = null;
            }

            if (DateTimeOffset.TryParse(tsPart, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
                return (ts, loggedBy);

            // Couldn't parse the timestamp - preserve the source label so List output still has
            // it, but signal "unknown timestamp" with MinValue.
            return (DateTimeOffset.MinValue, loggedBy ?? trimmed);
        }
    }
}
