using System.Text;
using InfiniContext.Core.Logging;
using FluentAssertions;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;

namespace InfiniContext.Tests.Logging;

public class ScrubberTests
{
    [Theory]
    [InlineData("ANTHROPIC_API_KEY", true)]
    [InlineData("AccessToken", true)]
    [InlineData("ApiSecret", true)]
    [InlineData("MY_KEY", true)]
    [InlineData("password_token", true)]
    [InlineData("Cwd", false)]
    [InlineData("argv", false)]
    [InlineData("SessionId", false)]
    public void IsSecretPropertyName_matches_design_doc_pattern(string name, bool expected)
    {
        SecretScrubbingEnricher.IsSecretPropertyName(name).Should().Be(expected);
    }

    [Fact]
    public void Scrubs_top_level_secret_scalar()
    {
        var rendered = RenderWithScrubber("api={ApiKey}", new ScalarValue("sk-ant-secret-123"), "ApiKey");
        rendered.Should().Contain("<redacted>").And.NotContain("sk-ant-secret-123");
    }

    [Fact]
    public void Leaves_non_secret_scalar_untouched()
    {
        var rendered = RenderWithScrubber("cwd={Cwd}", new ScalarValue("C:\\src\\proj"), "Cwd");
        rendered.Should().Contain("C:\\src\\proj");
    }

    [Fact]
    public void Truncates_oversized_string_with_byte_count_marker()
    {
        const int max = 256;
        var big = new string('x', max * 4); // ~1024 bytes UTF-8
        var rendered = RenderWithScrubber(
            "payload={Payload}",
            new ScalarValue(big),
            "Payload",
            maxFieldBytes: max);

        rendered.Should().Contain($"<… {Encoding.UTF8.GetByteCount(big)} bytes elided>");
        rendered.Length.Should().BeLessThan(max + 256, "the rendered tail must stay near the truncation budget");
    }

    [Fact]
    public void Recursively_redacts_dictionary_values_with_secret_keys()
    {
        var dict = new DictionaryValue(new[]
        {
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("ANTHROPIC_API_KEY"), new ScalarValue("sk-ant-real")),
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("PATH"), new ScalarValue("/usr/bin")),
        });

        var rendered = RenderWithScrubber("env={@Env}", dict, "Env");
        rendered.Should().NotContain("sk-ant-real");
        rendered.Should().Contain("<redacted>");
        rendered.Should().Contain("/usr/bin");
    }

    [Fact]
    public void Recursively_redacts_structure_with_secret_property()
    {
        var structure = new StructureValue(
            new[]
            {
                new LogEventProperty("AccessToken", new ScalarValue("ghp-real-token")),
                new LogEventProperty("User",        new ScalarValue("alice")),
            },
            typeTag: "GitHubCreds");

        var rendered = RenderWithScrubber("creds={@Creds}", structure, "Creds");
        rendered.Should().NotContain("ghp-real-token");
        rendered.Should().Contain("<redacted>");
        rendered.Should().Contain("alice");
    }

    [Fact]
    public void Sequence_inside_secret_named_property_redacts_all_elements()
    {
        var seq = new SequenceValue(new[]
        {
            new ScalarValue("primary-secret"),
            new ScalarValue("rotation-secret"),
        });

        var rendered = RenderWithScrubber("rotated={@PreviousAuthToken}", seq, "PreviousAuthToken");
        rendered.Should().NotContain("primary-secret").And.NotContain("rotation-secret");
        rendered.Should().Contain("<redacted>");
    }

    private static string RenderWithScrubber(
        string messageTemplate,
        LogEventPropertyValue value,
        string propertyName,
        int? maxFieldBytes = null)
    {
        var enricher = new SecretScrubbingEnricher(
            maxFieldBytes ?? SecretScrubbingEnricher.DefaultMaxFieldBytes);

        var parser = new MessageTemplateParser();
        var template = parser.Parse(messageTemplate);

        var ev = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: template,
            properties: new[] { new LogEventProperty(propertyName, value) });

        enricher.Enrich(ev, new NoOpFactory());

        using var sw = new StringWriter();
        var formatter = new Serilog.Formatting.Display.MessageTemplateTextFormatter(
            "{Message:lj}",
            formatProvider: System.Globalization.CultureInfo.InvariantCulture);
        formatter.Format(ev, sw);
        return sw.ToString();
    }

    private sealed class NoOpFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, value is LogEventPropertyValue v ? v : new ScalarValue(value));
    }
}
