using InfiniContext.Cli;
using FluentAssertions;

namespace InfiniContext.Tests.Cli;

/// <summary>
/// Drift tests for <see cref="CliFlagCatalog"/>. The catalog is what <c>--help</c> renders,
/// and <see cref="RunArgs.TryParse"/> is what actually accepts flags. If those two ever
/// disagree, the user sees flags that don't work or works flags that aren't documented.
/// These tests pin the pairing.
///
/// The list of <c>case</c> labels in <see cref="RunArgs.TryParse"/> is mirrored here by hand
/// rather than parsed via Roslyn — adding a flag in one place forces a deliberate update here,
/// which is exactly the gate we want.
/// </summary>
public class CliFlagCatalogTests
{
    /// <summary>
    /// Every named flag accepted by <see cref="RunArgs.TryParse"/>. Update both this list AND
    /// the catalog when adding a flag — the test asserting the pairing will catch it otherwise.
    /// </summary>
    private static readonly string[] FlagsAcceptedByRunArgs = new[]
    {
        "--resume",
        "--model",
        "--include-partial-messages",
        "--claude-exe",
        "--settings",
        "--approval-mode",
        "--", // separator
    };

    [Fact]
    public void Every_flag_accepted_by_RunArgs_TryParse_has_a_catalog_entry()
    {
        var catalogTriggers = CliFlagCatalog.All.Select(e => e.Name).ToHashSet();
        foreach (var flag in FlagsAcceptedByRunArgs)
        {
            catalogTriggers.Should().Contain(flag,
                $"the catalog must document every flag the parser accepts (missing: {flag})");
        }
    }

    [Fact]
    public void Catalog_does_not_advertise_flags_the_parser_rejects()
    {
        // Filter to entries that look like real CLI options (lead with "--" and aren't the
        // composite "--help, -h, help" line). Everything in this filtered set must be a parser
        // case-label, otherwise we're advertising a flag that throws "unknown option".
        var advertised = CliFlagCatalog.All
            .Where(e => e.Name.StartsWith("--", StringComparison.Ordinal) && !e.Name.Contains(","))
            .Select(e => e.Name)
            .ToArray();
        var accepted = FlagsAcceptedByRunArgs.ToHashSet();

        foreach (var name in advertised)
        {
            accepted.Should().Contain(name,
                $"the catalog must not advertise flags the parser rejects (orphan: {name})");
        }
    }

    [Fact]
    public void PrintUsageText_lists_every_catalog_entry()
    {
        var text = CliFlagCatalog.PrintUsageText();
        foreach (var entry in CliFlagCatalog.All)
        {
            text.Should().Contain(entry.Name);
            text.Should().Contain(entry.Summary);
        }
    }

    [Fact]
    public void PrintUsageText_does_not_list_run_as_a_required_subcommand()
    {
        // Regression: the old usage said "Usage: infinicontext run …" implying `run` was required.
        // It isn't — the argv pre-router treats no-arg, leading-flag, and explicit `run`
        // identically. Make sure we're not lying about that anymore.
        var text = CliFlagCatalog.PrintUsageText();
        text.Should().Contain("Usage: infinicontext [<cwd>]",
            "the primary form must show that <cwd> and options are accepted directly without a subcommand");
    }

    [Fact]
    public void PrintUsageText_points_to_inline_help_for_REPL_commands()
    {
        var text = CliFlagCatalog.PrintUsageText();
        text.Should().Contain("/help inside the REPL",
            "drop the inline command list (decisions.md D4); point at the registry-driven /help instead");
    }
}
