using InfiniContext.Core.Events;
using InfiniContext.Core.Parser;
using FluentAssertions;

namespace InfiniContext.Tests.Parser;

public class ReplayTests
{
    [Theory]
    [InlineData("simple-prompt")]
    [InlineData("single-tool")]
    [InlineData("single-tool-with-approval")]
    [InlineData("nested-tool")]
    [InlineData("multi-turn")]
    [InlineData("partial-messages")]
    public async Task HappyFixture_ParsesEveryLine_WithNoUnknowns(string scenario)
    {
        var (events, parser) = await ParseFixtureAsync(scenario);

        events.Should().NotBeEmpty();
        parser.MalformedLineCount.Should().Be(0, "happy fixtures contain no blank or invalid lines");
        parser.UnknownEventCount.Should().Be(0,
            "discovered unknowns: {0}",
            string.Join(", ", parser.UnknownTypeHistogram.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    [Fact]
    public async Task UnknownEventFixture_DriftsCounterByExactly_OneTopLevelAndOneBlock()
    {
        var (events, parser) = await ParseFixtureAsync("unknown-event");

        events.Should().HaveCount(4);

        // Line 2: top-level "some_future_top_level_event" → UnknownEvent.
        var topLevelUnknown = events.OfType<UnknownEvent>().Single();
        topLevelUnknown.Discriminator.Should().Be("some_future_top_level_event");

        // Line 3: assistant event with one TextBlock and one UnknownBlock("some_future_block").
        var assistant = events.OfType<AssistantEvent>().Single();
        assistant.Content.Should().HaveCount(2);
        assistant.Content[0].Should().BeOfType<TextBlock>().Which.Text.Should().Be("hello");
        assistant.Content[1].Should().BeOfType<UnknownBlock>().Which.Type.Should().Be("some_future_block");

        // Counter accounts for: top-level unknown event (1) + unknown content block (1).
        parser.UnknownEventCount.Should().Be(2);
        parser.UnknownTypeHistogram.Should().ContainKey("some_future_top_level_event").WhoseValue.Should().Be(1);
        parser.UnknownTypeHistogram.Should().ContainKey("content-block:some_future_block").WhoseValue.Should().Be(1);
    }

    [Theory]
    [InlineData("simple-prompt")]
    [InlineData("single-tool")]
    [InlineData("nested-tool")]
    public async Task KnownEventTypes_AreClassifiedCorrectly(string scenario)
    {
        var (events, _) = await ParseFixtureAsync(scenario);

        // Every fixture is bracketed by system/init … result.
        events.OfType<SystemInitEvent>().Should().NotBeEmpty();
        events.OfType<ResultEvent>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task NestedToolFixture_PreservesParentToolUseId_ForSubAgent()
    {
        var (events, _) = await ParseFixtureAsync("nested-tool");

        // Pick any tool_use whose id is referenced by a later event's parent_tool_use_id —
        // that is the sub-agent's parent call. Tool name varies across claude versions
        // (Task / Agent / future), so don't pin to one.
        var parentToolUseIds = events
            .Where(e => e.ParentToolUseId is not null)
            .Select(e => e.ParentToolUseId!)
            .Distinct()
            .ToList();

        parentToolUseIds.Should().NotBeEmpty(
            "the nested-tool fixture must include events with parent_tool_use_id set");

        var allToolUseIds = events.OfType<AssistantEvent>()
            .SelectMany(e => e.Content.OfType<ToolUseBlock>())
            .Select(b => b.Id)
            .ToHashSet();

        parentToolUseIds.Should().OnlyContain(id => allToolUseIds.Contains(id),
            "every parent_tool_use_id should reference a tool_use block emitted by an assistant event");
    }

    [Fact]
    public async Task PartialMessagesFixture_ContainsStreamEvents_WithInnerType()
    {
        var (events, _) = await ParseFixtureAsync("partial-messages");

        var streamEvents = events.OfType<PartialMessageEvent>().ToList();
        streamEvents.Should().NotBeEmpty();
        streamEvents.Select(e => e.InnerType).Should().Contain(new[]
        {
            "message_start", "content_block_start", "content_block_delta", "message_stop",
        });
    }

    private static async Task<(List<ClaudeEvent> events, StreamJsonParser parser)> ParseFixtureAsync(string scenario)
    {
        var path = TestPaths.Fixture(scenario);
        File.Exists(path).Should().BeTrue($"fixture should exist at {path}");

        var parser = new StreamJsonParser();
        using var reader = new StreamReader(path);
        var events = new List<ClaudeEvent>();
        await foreach (var ev in parser.ParseAsync(reader)) events.Add(ev);
        return (events, parser);
    }
}
