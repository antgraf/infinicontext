using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.Mcp;

/// <summary>
/// M20: the Core-owned mode→sink decision shared by the CLI's <c>ApprovalSinkFactory</c> and the
/// GUI's <c>SessionHost</c>. Locks the decision at the lowest layer so neither host can drift it
/// again. Host presentation (console banners, echo-wrap policy) is verified in the host suites.
/// </summary>
public class ApprovalSinkBuilderTests
{
    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    private static ApprovalSinkBuilder NewBuilder(Func<ApprovalAutoEffort, ILightModelService>? lightModelFactory = null) =>
        new(InfiniContextSettings.Defaults, Silent,
            lightModelFactory ?? (_ => new StubLightModel()),
            writeTranscriptBanner: false);

    [Fact]
    public void Ask_builds_slot_sink()
    {
        var r = NewBuilder().BuildInner(ApprovalMode.Ask, new PendingApprovalSlot(), new TranscriptModel(), "/tmp");
        r.EffectiveMode.Should().Be(ApprovalMode.Ask);
        r.Sink.Should().BeOfType<SlotApprovalSink>();
        r.LightModelError.Should().BeNull();
    }

    [Fact]
    public void Deny_builds_static_deny_sink()
    {
        var r = NewBuilder().BuildInner(ApprovalMode.Deny, new PendingApprovalSlot(), new TranscriptModel(), "/tmp");
        r.EffectiveMode.Should().Be(ApprovalMode.Deny);
        r.Sink.Should().BeOfType<StaticApprovalSink>();
    }

    [Fact]
    public void Allow_builds_static_allow_sink()
    {
        var r = NewBuilder().BuildInner(ApprovalMode.Allow, new PendingApprovalSlot(), new TranscriptModel(), "/tmp");
        r.EffectiveMode.Should().Be(ApprovalMode.Allow);
        r.Sink.Should().BeOfType<StaticApprovalSink>();
    }

    [Fact]
    public void Auto_builds_auto_sink_and_threads_effort_to_the_light_model_factory()
    {
        var settings = InfiniContextSettings.Defaults with
        {
            Approval = InfiniContextSettings.Defaults.Approval with
            {
                Auto = InfiniContextSettings.Defaults.Approval.Auto with { Effort = ApprovalAutoEffort.High },
            },
        };
        ApprovalAutoEffort? observed = null;
        var builder = new ApprovalSinkBuilder(settings, Silent,
            e => { observed = e; return new StubLightModel(); }, writeTranscriptBanner: false);

        var r = builder.BuildInner(ApprovalMode.Auto, new PendingApprovalSlot(), new TranscriptModel(), "/tmp");

        r.EffectiveMode.Should().Be(ApprovalMode.Auto);
        r.Sink.Should().BeOfType<AutoApprovalSink>();
        observed.Should().Be(ApprovalAutoEffort.High, "the configured effort flows through to the factory.");
    }

    [Fact]
    public void Auto_falls_back_to_ask_and_surfaces_the_construct_error_when_the_light_model_throws()
    {
        var boom = new InvalidOperationException("ctor exploded");
        var builder = NewBuilder(_ => throw boom);

        var r = builder.BuildInner(ApprovalMode.Auto, new PendingApprovalSlot(), new TranscriptModel(), "/tmp");

        r.EffectiveMode.Should().Be(ApprovalMode.Ask, "a construct failure degrades Auto to Ask.");
        r.Sink.Should().BeOfType<SlotApprovalSink>();
        r.LightModelError.Should().BeSameAs(boom, "the host needs the exception to surface it to the operator.");
    }

    private sealed class StubLightModel : ILightModelService
    {
        public Task<string> GenerateShortTitleAsync(string description, int maxWords = 6, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<string> SummarizeAsync(string text, int maxSentences = 2, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<CategoryResult> CategorizeAsync(string text, IReadOnlyList<string> categories, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<SafetyReview> ReviewToolCallSafetyAsync(SafetyReviewRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
