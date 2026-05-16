using InfiniContext.Core.Parser;
using FluentAssertions;

namespace InfiniContext.Tests.Parser;

public class MalformedLineTests
{
    [Fact]
    public async Task TruncatedJson_IsCounted_AndDoesNotThrow()
    {
        var input = "{\"type\":\"system\",\"subtype\":\"init\"" + "\n" + "{\"type\":\"result\",\"subtype\":\"success\"}";
        var parser = new StreamJsonParser();
        var warnings = new List<MalformedLineWarning>();
        parser.MalformedLine += w => warnings.Add(w);

        var events = await CollectAsync(parser, input);

        events.Should().HaveCount(1, "the truncated line is dropped; the well-formed one yields an event");
        parser.MalformedLineCount.Should().Be(1);
        warnings.Should().HaveCount(1);
        warnings[0].LineNumber.Should().Be(1);
    }

    [Fact]
    public async Task BlankAndWhitespaceOnlyLines_AreCounted_AndSkipped()
    {
        var input = "\n   \n\t\n{\"type\":\"result\",\"subtype\":\"success\"}\n";
        var parser = new StreamJsonParser();

        var events = await CollectAsync(parser, input);

        events.Should().HaveCount(1);
        parser.MalformedLineCount.Should().Be(3);
    }

    [Fact]
    public async Task LeadingBomOnALine_IsStripped_AndTheLineParses()
    {
        // Defensive: real claude doesn't emit BOMs today, but a future upgrade might.
        var input = "\uFEFF{\"type\":\"result\",\"subtype\":\"success\"}";
        var parser = new StreamJsonParser();

        var events = await CollectAsync(parser, input);

        events.Should().HaveCount(1);
        parser.MalformedLineCount.Should().Be(0);
    }

    [Fact]
    public async Task NonObjectTopLevelJson_IsCounted_AndDropped()
    {
        // A bare array or scalar isn't a valid claude event — the parser should treat it as malformed.
        var input = "[1,2,3]\n\"just a string\"\n42\n{\"type\":\"result\"}";
        var parser = new StreamJsonParser();

        var events = await CollectAsync(parser, input);

        events.Should().HaveCount(1);
        parser.MalformedLineCount.Should().Be(3);
    }

    private static async Task<List<Core.Events.ClaudeEvent>> CollectAsync(StreamJsonParser parser, string input)
    {
        using var reader = new StringReader(input);
        var events = new List<Core.Events.ClaudeEvent>();
        await foreach (var ev in parser.ParseAsync(reader)) events.Add(ev);
        return events;
    }
}
