using System.Text;
using InfiniContext.Core.Parser;
using InfiniContext.Core.State;
using FluentAssertions;

namespace InfiniContext.Tests.State;

/// <summary>
/// Pins the per-fixture state-machine trace (state transitions + tool-stack open/close lines).
/// Trace shape mirrors <c>TraceFormatTests</c>: one line per transition, diff-friendly.
/// Set <c>GENERATE_TRACES=1</c> to (re)write the expected files instead of asserting.
/// </summary>
public class StateMachineTraceTests
{
    [Theory]
    [InlineData("simple-prompt")]
    [InlineData("single-tool")]
    [InlineData("nested-tool")]
    public async Task SessionTrace_MatchesPinnedExpected(string scenario)
    {
        var actual = await BuildTraceAsync(scenario);
        var expectedPath = Path.Combine(TestPaths.FixturesDir, $"{scenario}.expected-state-trace.txt");

        if (Environment.GetEnvironmentVariable("GENERATE_TRACES") == "1" || !File.Exists(expectedPath))
        {
            await File.WriteAllTextAsync(expectedPath, actual, TestContext.Current.CancellationToken);
            return;
        }

        var expected = await File.ReadAllTextAsync(expectedPath, TestContext.Current.CancellationToken);
        Normalise(actual).Should().Be(Normalise(expected),
            $"state-machine trace for {scenario} should match {expectedPath}");
    }

    private static async Task<string> BuildTraceAsync(string scenario)
    {
        var sb = new StringBuilder();
        var openCount = 0;

        // Fixed clock so timestamps don't leak into the trace.
        var sm = new SessionStateMachine(clock: () => DateTimeOffset.UnixEpoch);
        sm.Transitioned += t => sb.Append(t.From).Append(" -> ").Append(t.To).Append(" (").Append(t.Reason).Append(")\n");
        sm.ToolStackChanged += c =>
        {
            if (c.Kind == ToolStackChangeKind.Opened)
            {
                openCount++;
                sb.Append("+tool ").Append(c.Slot.Name).Append('#').Append(c.Slot.Id)
                  .Append(" (parent=").Append(c.Slot.ParentToolUseId ?? "").Append(')')
                  .Append(" (open=").Append(openCount).Append(")\n");
            }
            else
            {
                openCount--;
                sb.Append("-tool ").Append(c.Slot.Name).Append('#').Append(c.Slot.Id)
                  .Append(" (open=").Append(openCount).Append(")\n");
            }
        };

        sm.BeginSend();

        var parser = new StreamJsonParser();
        using var reader = new StreamReader(TestPaths.Fixture(scenario));
        await foreach (var ev in parser.ParseAsync(reader))
        {
            sm.Apply(ev);
        }

        return sb.ToString();
    }

    private static string Normalise(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');
}
