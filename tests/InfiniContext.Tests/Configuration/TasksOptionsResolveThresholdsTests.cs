using FluentAssertions;
using InfiniContext.Core.Configuration;

namespace InfiniContext.Tests.Configuration;

/// <summary>
/// Task 0012 finding #2: large-context models flush at tighter thresholds. Covers
/// <see cref="TasksOptions.ResolveThresholds"/>.
/// </summary>
public class TasksOptionsResolveThresholdsTests
{
    [Fact]
    public void Resolves_to_default_pair_for_small_context_model()
    {
        var opts = new TasksOptions();

        var (soft, hard) = opts.ResolveThresholds("claude-sonnet-4-6");

        soft.Should().Be(opts.SoftThresholdPct);
        hard.Should().Be(opts.HardThresholdPct);
    }

    [Fact]
    public void Resolves_to_large_pair_for_1M_context_model()
    {
        var opts = new TasksOptions();

        var (soft, hard) = opts.ResolveThresholds("claude-opus-4-8[1m]");

        soft.Should().Be(opts.SoftThresholdPctLargeContext);
        hard.Should().Be(opts.HardThresholdPctLargeContext);
    }

    [Fact]
    public void Resolves_to_default_pair_when_model_id_is_unknown()
    {
        var opts = new TasksOptions();

        var (soft, hard) = opts.ResolveThresholds("claude-unknown-1");

        soft.Should().Be(opts.SoftThresholdPct);
        hard.Should().Be(opts.HardThresholdPct);
    }

    [Fact]
    public void Resolves_to_default_pair_when_cutoff_is_disabled()
    {
        var opts = new TasksOptions
        {
            LargeContextWindowTokens = 0,
        };

        var (soft, hard) = opts.ResolveThresholds("claude-opus-4-8[1m]");

        soft.Should().Be(opts.SoftThresholdPct);
        hard.Should().Be(opts.HardThresholdPct);
    }

    [Fact]
    public void Resolves_to_default_pair_for_null_model_id()
    {
        var opts = new TasksOptions();

        var (soft, hard) = opts.ResolveThresholds(null);

        soft.Should().Be(opts.SoftThresholdPct);
        hard.Should().Be(opts.HardThresholdPct);
    }
}
