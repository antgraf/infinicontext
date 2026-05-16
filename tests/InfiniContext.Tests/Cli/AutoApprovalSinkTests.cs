using System.Text.Json.Nodes;
using InfiniContext.Cli;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.LightModel;
using InfiniContext.Core.Mcp;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.Cli;

/// <summary>
/// Behavior tests for the <c>auto</c> approval mode. The light model is faked so the path is
/// driven deterministically — no network, no real Claude calls.
/// </summary>
public class AutoApprovalSinkTests
{
    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task High_confidence_allow_returns_allow()
    {
        var fake = new FakeLightModel(new SafetyReview(SafetyDecision.Allow, SafetyConfidence.High, SafetyAxis.None, "looks fine"));
        var sink = new AutoApprovalSink(fake, NeverCalled(), new AutoApprovalOptions(), "/tmp", Silent);

        var response = await sink.RequestAsync(Request("Bash", "{\"command\":\"ls\"}"), default);
        response.Behavior.Should().Be(ApprovalBehavior.Allow);
    }

    [Fact]
    public async Task Deny_decision_returns_deny_with_axis()
    {
        var review = new SafetyReview(SafetyDecision.Deny, SafetyConfidence.High, SafetyAxis.Destructive, "rm -rf /");
        var fake = new FakeLightModel(review);
        var sink = new AutoApprovalSink(fake, NeverCalled(), new AutoApprovalOptions(), "/tmp", Silent);

        var response = await sink.RequestAsync(Request("Bash", "{\"command\":\"rm -rf /\"}"), default);
        response.Behavior.Should().Be(ApprovalBehavior.Deny);
        response.Message.Should().Contain("Destructive");
        response.Message.Should().Contain("rm -rf /");
    }

    [Fact]
    public async Task Low_confidence_allow_with_default_options_denies()
    {
        var review = new SafetyReview(SafetyDecision.Allow, SafetyConfidence.Low, SafetyAxis.None, "ambiguous");
        var fake = new FakeLightModel(review);
        var options = new AutoApprovalOptions { OnLowConfidence = LowConfidenceFallback.Deny };
        var sink = new AutoApprovalSink(fake, NeverCalled(), options, "/tmp", Silent);

        var response = await sink.RequestAsync(Request("Bash", "{\"command\":\"curl example.com\"}"), default);
        response.Behavior.Should().Be(ApprovalBehavior.Deny);
        response.Message.Should().Contain("low-confidence");
    }

    [Fact]
    public async Task Low_confidence_with_FallbackToAsk_calls_through_to_fallback()
    {
        var review = new SafetyReview(SafetyDecision.Allow, SafetyConfidence.Low, SafetyAxis.None, "ambiguous");
        var fake = new FakeLightModel(review);
        var fallback = new RecordingApprovalSink(ApprovalResponse.Allow());
        var options = new AutoApprovalOptions { OnLowConfidence = LowConfidenceFallback.FallbackToAsk };
        var sink = new AutoApprovalSink(fake, fallback, options, "/tmp", Silent);

        var response = await sink.RequestAsync(Request("Bash", "{}"), default);
        response.Behavior.Should().Be(ApprovalBehavior.Allow);
        fallback.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Light_model_exception_with_default_options_denies()
    {
        var fake = new FakeLightModel(new InvalidOperationException("boom"));
        var sink = new AutoApprovalSink(fake, NeverCalled(), new AutoApprovalOptions(), "/tmp", Silent);

        var response = await sink.RequestAsync(Request("Bash", "{}"), default);
        response.Behavior.Should().Be(ApprovalBehavior.Deny);
        response.Message.Should().Contain("light-model error");
    }

    [Fact]
    public async Task Light_model_exception_with_FallbackToAsk_calls_through()
    {
        var fake = new FakeLightModel(new TimeoutException("slow"));
        var fallback = new RecordingApprovalSink(ApprovalResponse.Deny("user said no"));
        var options = new AutoApprovalOptions { OnLowConfidence = LowConfidenceFallback.FallbackToAsk };
        var sink = new AutoApprovalSink(fake, fallback, options, "/tmp", Silent);

        var response = await sink.RequestAsync(Request("Bash", "{}"), default);
        fallback.CallCount.Should().Be(1);
        response.Behavior.Should().Be(ApprovalBehavior.Deny);
        response.Message.Should().Be("user said no");
    }

    [Fact]
    public async Task Cwd_is_passed_through_to_safety_review_request()
    {
        var fake = new FakeLightModel(new SafetyReview(SafetyDecision.Allow, SafetyConfidence.High, SafetyAxis.None, ""));
        var sink = new AutoApprovalSink(fake, NeverCalled(), new AutoApprovalOptions(), "/work/dir", Silent);

        await sink.RequestAsync(Request("Bash", "{}"), default);

        fake.LastRequest.Should().NotBeNull();
        fake.LastRequest!.Cwd.Should().Be("/work/dir");
        fake.LastRequest.ToolName.Should().Be("Bash");
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_hitting_fallback()
    {
        var fake = new FakeLightModel(new OperationCanceledException());
        var fallback = new RecordingApprovalSink(ApprovalResponse.Allow());
        var options = new AutoApprovalOptions { OnLowConfidence = LowConfidenceFallback.FallbackToAsk };
        var sink = new AutoApprovalSink(fake, fallback, options, "/tmp", Silent);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sink.RequestAsync(Request("Bash", "{}"), cts.Token));
        fallback.CallCount.Should().Be(0);
    }

    private static ApprovalRequest Request(string tool, string inputJson) =>
        new(tool, (JsonObject)JsonNode.Parse(inputJson)!);

    private static IApprovalSink NeverCalled() =>
        new RecordingApprovalSink(ApprovalResponse.Deny("never-called sink should not have been invoked"))
        {
            ThrowOnInvoke = true,
        };

    private sealed class FakeLightModel : ILightModelService
    {
        private readonly SafetyReview? _review;
        private readonly Exception? _toThrow;
        public SafetyReviewRequest? LastRequest { get; private set; }

        public FakeLightModel(SafetyReview review) { _review = review; }
        public FakeLightModel(Exception toThrow) { _toThrow = toThrow; }

        public Task<SafetyReview> ReviewToolCallSafetyAsync(SafetyReviewRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (_toThrow is not null)
            {
                if (_toThrow is OperationCanceledException) cancellationToken.ThrowIfCancellationRequested();
                return Task.FromException<SafetyReview>(_toThrow);
            }
            return Task.FromResult(_review!);
        }

        public Task<string> GenerateShortTitleAsync(string description, int maxWords = 6, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<string> SummarizeAsync(string text, int maxSentences = 2, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<CategoryResult> CategorizeAsync(string text, IReadOnlyList<string> categories, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class RecordingApprovalSink : IApprovalSink
    {
        private readonly ApprovalResponse _response;
        public int CallCount { get; private set; }
        public bool ThrowOnInvoke { get; init; }

        public RecordingApprovalSink(ApprovalResponse response) { _response = response; }

        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowOnInvoke) throw new InvalidOperationException("fallback should not have been called");
            return Task.FromResult(_response);
        }
    }
}
