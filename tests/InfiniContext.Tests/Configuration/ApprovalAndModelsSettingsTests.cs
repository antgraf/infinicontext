using InfiniContext.Core.Configuration;
using FluentAssertions;

namespace InfiniContext.Tests.Configuration;

/// <summary>
/// Validation tests for the settings sections introduced with the light-model + approval-mode
/// feature. Same isolation contract as <see cref="InfiniContextSettingsLoaderTests"/>:
/// every test owns a temp dir and explicitly opts out of user overrides + env vars.
/// </summary>
public class ApprovalAndModelsSettingsTests
{
    private static InfiniContextSettingsLoader.LoadResult Load(string baseDir) =>
        InfiniContextSettingsLoader.Load(new InfiniContextSettingsLoader.LoadOptions
        {
            BaseDirectory = baseDir,
            IncludeUserOverrides = false,
            IncludeEnvironmentVariables = false,
        });

    [Fact]
    public void Defaults_are_consistent_with_in_code_defaults()
    {
        var d = InfiniContextSettings.Defaults;
        // Default is Auto so users get safety review out of the box. Without ANTHROPIC_API_KEY,
        // the factory still proceeds — using the CLI back end (the OAuth fallback) per the §6 recipe.
        d.Approval.Mode.Should().Be(ApprovalMode.Auto);
        d.Approval.Auto.OnLowConfidence.Should().Be(LowConfidenceFallback.Deny);
        d.Approval.Auto.TimeoutSeconds.Should().Be(15);
        d.Approval.Auto.Effort.Should().Be(ApprovalAutoEffort.Low,
            "low effort matches the §6 recipe — fast/cheap, fine for most calls");
        d.Approval.Auto.Mode.Should().Be(CliLightModelMode.SingleShot,
            "auto-mode safety review needs real isolation between unrelated tool calls (single-shot)");
        d.Models.Main.Id.Should().Be("claude-opus-4-8");
        d.Models.Light.Id.Should().Be("claude-haiku-4-5");
        d.Models.Light.MaxTokens.Should().Be(1024);
        d.Models.Light.TimeoutSeconds.Should().Be(30);

        // General CLI back-end default is LongLived — for non-auto-mode consumers (title gen,
        // classification, summarization) where amortized startup wins and leakage is fine.
        d.Models.Light.Cli.Mode.Should().Be(CliLightModelMode.LongLived);
        d.Models.Light.Cli.MaxOutputTokens.Should().Be(128);
        d.Models.Light.Cli.ThinkingMaxOutputTokens.Should().Be(1024);
    }

    [Fact]
    public void Tasks_defaults_snapshot()
    {
        // Pin every TasksOptions default so a refactor that accidentally drifts a value
        // (e.g. "I'll change the default lock TTL real quick") fails this test loudly. The
        // numbers themselves come from design.md §7+§8.
        var t = InfiniContextSettings.Defaults.Tasks;

        t.LockTtlSeconds.Should().Be(30);
        t.LockMaxWaitSeconds.Should().Be(60);
        t.SoftThresholdPct.Should().Be(60);
        t.HardThresholdPct.Should().Be(85);
        t.DbPath.Should().BeNull();
        t.ModelContextWindows.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["claude-sonnet-4-6"] = 200_000,
            ["claude-opus-4-8"] = 200_000,
            ["claude-opus-4-8[1m]"] = 1_000_000,
            ["claude-haiku-4-5"] = 200_000,
            ["claude-fable-5"] = 1_000_000,
        });
    }

    [Fact]
    public void Model_catalog_is_non_empty_and_contains_main()
    {
        KnownModels.Catalog.Should().NotBeEmpty();
        KnownModels.Catalog.Should().Contain(m => m.Id == KnownModels.Main,
            "the GUI dropdowns must always offer the shipped default model");
        KnownModels.Catalog.Should().OnlyContain(
            m => !string.IsNullOrWhiteSpace(m.Id)
              && !string.IsNullOrWhiteSpace(m.DisplayName)
              && m.ContextWindowTokens > 0);
    }

    [Fact]
    public void Every_catalog_id_resolves_a_context_window()
    {
        // A catalog entry whose id isn't in ModelContextWindows would silently fall back to the
        // default (small-context) threshold pair вЂ” wrong for a large-context model like Fable.
        // Keep the two in sync: the threshold map must cover every offered model.
        var windows = InfiniContextSettings.Defaults.Tasks.ModelContextWindows;
        foreach (var entry in KnownModels.Catalog)
        {
            windows.Should().ContainKey(entry.Id,
                $"catalog model '{entry.Id}' needs a ModelContextWindows entry for correct thresholds");
            windows[entry.Id].Should().Be(entry.ContextWindowTokens,
                $"catalog and threshold-map context windows for '{entry.Id}' must agree");
        }
    }

    [Fact]
    public void Available_models_default_to_the_catalog()
    {
        InfiniContextSettings.Defaults.Models.Available.Should().BeEquivalentTo(KnownModels.Catalog);
    }

    [Fact]
    public void Available_models_round_trip_from_json()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            {
              "models": {
                "available": [
                  { "id": "claude-opus-4-8", "displayName": "Opus 4.8", "contextWindowTokens": 200000 },
                  { "id": "claude-fable-5", "displayName": "Fable 5", "contextWindowTokens": 1000000 }
                ]
              }
            }
            """);

        var result = Load(temp.Path);

        result.Settings.Models.Available.Should().BeEquivalentTo(new[]
        {
            new ModelCatalogEntry("claude-opus-4-8", "Opus 4.8", 200_000),
            new ModelCatalogEntry("claude-fable-5", "Fable 5", 1_000_000),
        });
    }

    [Fact]
    public void Available_model_with_blank_id_is_rejected()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "models": { "available": [ { "id": "", "displayName": "X", "contextWindowTokens": 200000 } ] } }
            """);

        var act = () => Load(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
           .Which.Errors.Should().Contain(e => e.Contains("models.available[0].id"));
    }

    [Fact]
    public void Available_model_with_blank_display_name_is_rejected()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "models": { "available": [ { "id": "claude-opus-4-8", "displayName": "", "contextWindowTokens": 200000 } ] } }
            """);

        var act = () => Load(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
           .Which.Errors.Should().Contain(e => e.Contains("models.available[0].displayName"));
    }

    [Fact]
    public void Available_model_with_non_positive_context_window_is_rejected()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "models": { "available": [ { "id": "claude-opus-4-8", "displayName": "Opus 4.8", "contextWindowTokens": 0 } ] } }
            """);

        var act = () => Load(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
           .Which.Errors.Should().Contain(e => e.Contains("models.available[0].contextWindowTokens"));
    }

    [Fact]
    public void Allow_mode_as_persisted_default_is_rejected()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "approval": { "mode": "Allow" } }
            """);

        var act = () => Load(temp.Path);

        var ex = act.Should().Throw<InfiniContextSettingsException>().Which;
        ex.Errors.Should().Contain(e => e.Contains("approval.mode") && e.Contains("Allow"));
    }

    [Theory]
    [InlineData("Ask")]
    [InlineData("Deny")]
    [InlineData("Auto")]
    public void Other_modes_are_accepted_as_persisted_defaults(string mode)
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), $$"""
            { "approval": { "mode": "{{mode}}" } }
            """);

        var result = Load(temp.Path);

        result.Settings.Approval.Mode.ToString().Should().Be(mode);
    }

    [Fact]
    public void Light_model_max_tokens_out_of_range_is_rejected()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "models": { "light": { "maxTokens": 4 } } }
            """);

        var act = () => Load(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
           .Which.Errors.Should().Contain(e => e.Contains("models.light.maxTokens"));
    }

    [Fact]
    public void Light_model_id_empty_is_rejected()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "models": { "light": { "id": "" } } }
            """);

        var act = () => Load(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
           .Which.Errors.Should().Contain(e => e.Contains("models.light.id"));
    }

    [Fact]
    public void Auto_timeout_out_of_range_is_rejected()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "approval": { "auto": { "timeoutSeconds": 0 } } }
            """);

        var act = () => Load(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
           .Which.Errors.Should().Contain(e => e.Contains("approval.auto.timeoutSeconds"));
    }

    [Fact]
    public void Models_and_approval_values_bind_from_json_correctly()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            {
              "approval": { "mode": "Auto", "auto": { "onLowConfidence": "FallbackToAsk", "timeoutSeconds": 8, "effort": "High", "mode": "LongLived" } },
              "models": { "main": { "id": "claude-opus-4-7" }, "light": { "id": "claude-haiku-4-5", "maxTokens": 256, "timeoutSeconds": 5,
                "cli": { "mode": "SingleShot", "maxOutputTokens": 256, "thinkingMaxOutputTokens": 2048 } } }
            }
            """);

        var result = Load(temp.Path);

        result.Settings.Approval.Mode.Should().Be(ApprovalMode.Auto);
        result.Settings.Approval.Auto.OnLowConfidence.Should().Be(LowConfidenceFallback.FallbackToAsk);
        result.Settings.Approval.Auto.TimeoutSeconds.Should().Be(8);
        result.Settings.Approval.Auto.Effort.Should().Be(ApprovalAutoEffort.High);
        // Both Mode fields bind independently — auto's overrides cli's for the auto-mode path,
        // cli's is the default for general consumers.
        result.Settings.Approval.Auto.Mode.Should().Be(CliLightModelMode.LongLived);
        result.Settings.Models.Light.Id.Should().Be("claude-haiku-4-5");
        result.Settings.Models.Light.MaxTokens.Should().Be(256);
        result.Settings.Models.Light.TimeoutSeconds.Should().Be(5);
        result.Settings.Models.Light.Cli.Mode.Should().Be(CliLightModelMode.SingleShot);
        result.Settings.Models.Light.Cli.MaxOutputTokens.Should().Be(256);
        result.Settings.Models.Light.Cli.ThinkingMaxOutputTokens.Should().Be(2048);
    }

    [Fact]
    public void Cli_max_output_tokens_out_of_range_is_rejected()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "models": { "light": { "cli": { "maxOutputTokens": 4 } } } }
            """);

        var act = () => Load(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
           .Which.Errors.Should().Contain(e => e.Contains("models.light.cli.maxOutputTokens"));
    }

    [Fact]
    public void Cli_thinking_max_output_tokens_out_of_range_is_rejected()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "models": { "light": { "cli": { "thinkingMaxOutputTokens": 8 } } } }
            """);

        var act = () => Load(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
           .Which.Errors.Should().Contain(e => e.Contains("models.light.cli.thinkingMaxOutputTokens"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cp-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
