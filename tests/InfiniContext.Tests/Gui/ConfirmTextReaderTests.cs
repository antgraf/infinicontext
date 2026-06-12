using FluentAssertions;
using InfiniContext.Gui.Services;

namespace InfiniContext.Tests.Gui;

/// <summary>
/// Task 0017: the GUI had no stdin reader, so <c>/task review</c> (without <c>--yes</c>) bailed at
/// the cost guard - it wrote "no input reader available; pass --yes" and never ran the review. The
/// fix supplies <see cref="ConfirmTextReader"/>, which turns the handler's "Continue? [y/N]" read
/// into a modal yes/no. These tests pin the line the handler sees for each operator choice.
/// </summary>
public class ConfirmTextReaderTests
{
    [Fact]
    public async Task ReadLineAsync_returns_y_when_operator_confirms()
    {
        var reader = new ConfirmTextReader(_ => Task.FromResult(true));

        var line = await reader.ReadLineAsync(CancellationToken.None);

        line.Should().Be("y", "the handler's cost guard treats 'y'/'yes' as proceed.");
    }

    [Fact]
    public async Task ReadLineAsync_returns_n_when_operator_cancels()
    {
        var reader = new ConfirmTextReader(_ => Task.FromResult(false));

        var line = await reader.ReadLineAsync(CancellationToken.None);

        line.Should().Be("n", "anything other than y/yes cancels the review - no agents spawn.");
    }

    [Fact]
    public async Task ReadLineAsync_propagates_the_cancellation_token_to_the_confirm_callback()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;
        var reader = new ConfirmTextReader(ct => { seen = ct; return Task.FromResult(true); });

        await reader.ReadLineAsync(cts.Token);

        seen.Should().Be(cts.Token, "the handler's read token must reach the dialog so an interrupt cancels it.");
    }
}
