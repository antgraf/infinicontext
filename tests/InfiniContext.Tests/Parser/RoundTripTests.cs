using System.Text.Json.Nodes;
using InfiniContext.Core.Parser;
using FluentAssertions;

namespace InfiniContext.Tests.Parser;

/// <summary>
/// The parser's tolerance contract requires that <c>parse → re-serialize</c> preserves the original
/// JSON exactly: every field, including unrecognized ones, round-trips. We compare via <see cref="JsonNode"/>
/// equality (semantic, ignoring whitespace and key order), which is the right granularity here — the
/// raw bytes can vary on numeric formatting (e.g. <c>0.0741</c> vs <c>0.07410</c>) without losing data.
/// </summary>
public class RoundTripTests
{
    [Theory]
    [InlineData("simple-prompt")]
    [InlineData("single-tool")]
    [InlineData("nested-tool")]
    [InlineData("multi-turn")]
    [InlineData("partial-messages")]
    [InlineData("unknown-event")]
    public async Task EveryFixtureLine_RoundTripsThroughTheParser(string scenario)
    {
        var path = TestPaths.Fixture(scenario);
        var originalLines = await File.ReadAllLinesAsync(path);

        var parser = new StreamJsonParser();
        using var reader = new StreamReader(path);
        var index = 0;
        await foreach (var ev in parser.ParseAsync(reader))
        {
            var originalNode = JsonNode.Parse(originalLines[index]);
            JsonNode.DeepEquals(originalNode, ev.Raw).Should().BeTrue(
                $"line {index + 1} of {scenario}.jsonl must round-trip through parse → ev.Raw without loss");
            index++;
        }

        index.Should().Be(originalLines.Length, "every line should produce one event");
    }
}
