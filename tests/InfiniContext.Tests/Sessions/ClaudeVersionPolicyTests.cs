using InfiniContext.Core.Sessions;
using FluentAssertions;
using Out = InfiniContext.Core.Sessions.ClaudeVersionPolicy.Outcome;

namespace InfiniContext.Tests.Sessions;

/// <summary>
/// Unit tests for <see cref="ClaudeVersionPolicy"/>. The policy is advisory — these tests pin the
/// classification thresholds so the runtime warning/error messaging stays consistent across
/// future MinClaudeVersion / TestedAgainstVersion bumps.
/// </summary>
public class ClaudeVersionPolicyTests
{
    [Theory]
    [InlineData("2.1.112", Out.ExactMatch)]
    [InlineData("v2.1.112", Out.ExactMatch)]
    [InlineData("2.1.111", Out.DriftBelow)]
    [InlineData("2.1.0", Out.DriftBelow)]
    [InlineData("2.0.0", Out.DriftBelow)]
    [InlineData("2.2.0", Out.DriftAbove)]
    [InlineData("3.0.0", Out.DriftAbove)]
    [InlineData("2.1.112-beta.4", Out.ExactMatch)]
    [InlineData("2.1.112+build.7", Out.ExactMatch)]
    [InlineData("1.99.0", Out.BelowMinimum)]
    [InlineData("0.0.1", Out.BelowMinimum)]
    [InlineData("not-a-version", Out.Unrecognised)]
    [InlineData("", Out.Unrecognised)]
    [InlineData(null, Out.Unrecognised)]
    public void Check_classifies_versions(string? input, Out expected)
    {
        ClaudeVersionPolicy.Check(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("2.1.112", 2, 1, 112)]
    [InlineData("v2.1.112", 2, 1, 112)]
    [InlineData("3.0.0-rc.1", 3, 0, 0)]
    [InlineData("2.1", 2, 1, 0)]
    [InlineData("4", 4, 0, 0)]
    public void TryParse_extracts_leading_triplet(string input, int major, int minor, int patch)
    {
        ClaudeVersionPolicy.TryParse(input, out var parsed).Should().BeTrue();
        parsed.Should().Be((major, minor, patch));
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("")]
    [InlineData("v")]
    public void TryParse_rejects_garbage(string input)
    {
        ClaudeVersionPolicy.TryParse(input, out _).Should().BeFalse();
    }
}
