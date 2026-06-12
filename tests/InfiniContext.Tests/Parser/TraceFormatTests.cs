using System.Text;
using InfiniContext.Core.Events;
using InfiniContext.Core.Parser;
using FluentAssertions;

namespace InfiniContext.Tests.Parser;

/// <summary>
/// Generates and pins a one-line-per-event trace for selected fixtures. Step 1B's state-machine
/// tests consume these files. The trace shape is intentionally simple — <c>type/subtype [block-types]</c>
/// — so a human reviewer can scan it. Set environment variable <c>GENERATE_TRACES=1</c> to (re)write
/// the expected-trace files instead of asserting against them.
/// </summary>
public class TraceFormatTests
{
    private static readonly string[] _scenarios = { "simple-prompt", "single-tool", "nested-tool" };

    [Theory]
    [InlineData("simple-prompt")]
    [InlineData("single-tool")]
    [InlineData("nested-tool")]
    public async Task ParserTrace_MatchesPinnedExpected(string scenario)
    {
        var actual = await BuildTraceAsync(scenario);
        var expectedPath = Path.Combine(TestPaths.FixturesDir, $"{scenario}.expected-trace.txt");

        if (Environment.GetEnvironmentVariable("GENERATE_TRACES") == "1" || !File.Exists(expectedPath))
        {
            await File.WriteAllTextAsync(expectedPath, actual, TestContext.Current.CancellationToken);
            return;
        }

        var expected = await File.ReadAllTextAsync(expectedPath, TestContext.Current.CancellationToken);
        // Normalise line endings before comparing — git on Windows turns LF into CRLF.
        Normalise(actual).Should().Be(Normalise(expected),
            $"parser trace for {scenario} should match {expectedPath}");
    }

    private static async Task<string> BuildTraceAsync(string scenario)
    {
        var parser = new StreamJsonParser();
        using var reader = new StreamReader(TestPaths.Fixture(scenario));
        var sb = new StringBuilder();
        await foreach (var ev in parser.ParseAsync(reader))
        {
            var subtype = ev.Subtype is null ? "" : "/" + ev.Subtype;
            var blocks = ev switch
            {
                AssistantEvent a => string.Join(",", a.Content.Select(b => b.Type)),
                UserEvent u => string.Join(",", u.Content.Select(b => b.Type)),
                PartialMessageEvent s => s.InnerType ?? "",
                _ => "",
            };
            sb.Append(ev.Type).Append(subtype).Append(" [").Append(blocks).Append("]\n");
        }
        return sb.ToString();
    }

    private static string Normalise(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');
}
