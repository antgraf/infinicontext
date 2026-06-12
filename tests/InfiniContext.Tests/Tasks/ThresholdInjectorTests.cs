using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Tasks;

/// <summary>
/// Unit tests for <see cref="ThresholdInjector"/>. The injector takes a send delegate, so we
/// capture the messages it composes without spawning anything.
/// </summary>
public class ThresholdInjectorTests
{
    [Fact]
    public async Task Soft_message_includes_percentage_and_polite_phrasing()
    {
        var sent = new List<string>();
        var injector = new ThresholdInjector((msg, _) => { sent.Add(msg); return Task.CompletedTask; });

        await injector.OnSoftAsync(new ContextPressureCrossing(130_000, 200_000, 0.65, "claude-opus-4-7"), TestContext.Current.CancellationToken);

        sent.Should().ContainSingle();
        var msg = sent[0];
        msg.Should().Contain("65%");
        msg.Should().Contain("flush progress to artifacts");
        msg.Should().Contain("logical stopping point");
        msg.Should().Contain("hand off to a fresh session");
    }

    [Fact]
    public async Task Hard_message_signals_criticality_and_blocks_new_work()
    {
        var sent = new List<string>();
        var injector = new ThresholdInjector((msg, _) => { sent.Add(msg); return Task.CompletedTask; });

        await injector.OnHardAsync(new ContextPressureCrossing(180_000, 200_000, 0.90, "claude-opus-4-7"), TestContext.Current.CancellationToken);

        sent.Should().ContainSingle();
        var msg = sent[0];
        msg.Should().Contain("Context is critical");
        msg.Should().Contain("90%");
        msg.Should().Contain("Flush all relevant progress");
        msg.Should().Contain("Do not start any new work");
    }

    [Fact]
    public async Task Send_failure_is_swallowed_and_logged()
    {
        // The injector is fire-and-forget from the monitor's perspective; if the underlying send
        // throws we must not propagate the exception (it would tear down the stdout reader).
        var injector = new ThresholdInjector((_, _) => throw new InvalidOperationException("stdin closed"));

        await injector.Invoking(i => i.OnSoftAsync(new ContextPressureCrossing(120_000, 200_000, 0.6, null)))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task Send_observes_supplied_cancellation_token()
    {
        CancellationToken? observed = null;
        var injector = new ThresholdInjector((_, ct) => { observed = ct; return Task.CompletedTask; });
        using var cts = new CancellationTokenSource();

        await injector.OnHardAsync(new ContextPressureCrossing(170_000, 200_000, 0.85, "m"), cts.Token);

        observed.Should().NotBeNull();
        observed!.Value.Should().Be(cts.Token);
    }

    [Fact]
    public void Constructor_rejects_null_send()
    {
        FluentActions.Invoking(() => new ThresholdInjector(null!))
            .Should().Throw<ArgumentNullException>();
    }
}
