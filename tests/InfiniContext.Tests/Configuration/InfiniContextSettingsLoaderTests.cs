using InfiniContext.Core.Configuration;
using FluentAssertions;

namespace InfiniContext.Tests.Configuration;

/// <summary>
/// Tests for <see cref="InfiniContextSettingsLoader"/>. Every test owns a temp directory and passes it
/// as <see cref="InfiniContextSettingsLoader.LoadOptions.BaseDirectory"/> so the dev's real
/// <c>settings.user.json</c> can never leak in. Tests that exercise the env-var overlay live in a
/// non-parallel collection and restore the variable in <c>finally</c>.
/// </summary>
public class InfiniContextSettingsLoaderTests
{
    private static InfiniContextSettingsLoader.LoadResult LoadIsolated(
        string baseDir,
        bool includeUserOverrides = true,
        bool includeEnvironmentVariables = false,
        string? explicitPath = null)
        => InfiniContextSettingsLoader.Load(new InfiniContextSettingsLoader.LoadOptions
        {
            BaseDirectory = baseDir,
            IncludeUserOverrides = includeUserOverrides,
            IncludeEnvironmentVariables = includeEnvironmentVariables,
            ExplicitPath = explicitPath,
        });

    [Fact]
    public void Empty_directory_yields_compile_time_defaults()
    {
        using var temp = new TempDir();

        var result = LoadIsolated(temp.Path);

        result.Settings.Should().BeEquivalentTo(InfiniContextSettings.Defaults);
        result.SourcesLoaded.Should().BeEmpty();
    }

    [Fact]
    public void Settings_json_is_picked_up_from_base_directory()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            {
              "approval": { "timeoutSeconds": 90 },
              "session": { "eventChannelCapacity": 4096 }
            }
            """);

        var result = LoadIsolated(temp.Path);

        result.Settings.Approval.TimeoutSeconds.Should().Be(90);
        result.Settings.Session.EventChannelCapacity.Should().Be(4096);
        result.Settings.Logging.Level.Should().Be(InfiniContextSettings.Defaults.Logging.Level); // unchanged
        result.SourcesLoaded.Should().ContainSingle().Which.Should().EndWith("settings.json");
    }

    [Fact]
    public void User_overrides_layer_on_top_of_committed_defaults()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "approval": { "timeoutSeconds": 60 }, "session": { "eventChannelCapacity": 1024 } }
            """);
        File.WriteAllText(Path.Combine(temp.Path, "settings.user.json"), """
            { "approval": { "timeoutSeconds": 120 } }
            """);

        var result = LoadIsolated(temp.Path);

        result.Settings.Approval.TimeoutSeconds.Should().Be(120);                  // user wins
        result.Settings.Session.EventChannelCapacity.Should().Be(1024);            // committed survives
        result.SourcesLoaded.Should().HaveCount(2);
        result.SourcesLoaded[0].Should().EndWith("settings.json");
        result.SourcesLoaded[1].Should().EndWith("settings.user.json");
    }

    [Fact]
    public void IncludeUserOverrides_false_ignores_settings_user_json_even_when_present()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "approval": { "timeoutSeconds": 60 } }
            """);
        File.WriteAllText(Path.Combine(temp.Path, "settings.user.json"), """
            { "approval": { "timeoutSeconds": 120 } }
            """);

        var result = LoadIsolated(temp.Path, includeUserOverrides: false);

        result.Settings.Approval.TimeoutSeconds.Should().Be(60);
        result.SourcesLoaded.Should().ContainSingle().Which.Should().EndWith("settings.json");
        result.SourcesLoaded.Should().NotContain(s => s.EndsWith("settings.user.json"));
    }

    [Fact]
    public void Explicit_path_bypasses_discovery_of_committed_and_user_files()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "approval": { "timeoutSeconds": 60 } }
            """);
        File.WriteAllText(Path.Combine(temp.Path, "settings.user.json"), """
            { "approval": { "timeoutSeconds": 120 } }
            """);
        var explicitPath = Path.Combine(temp.Path, "alt.json");
        File.WriteAllText(explicitPath, """
            { "approval": { "timeoutSeconds": 200 } }
            """);

        var result = LoadIsolated(temp.Path, explicitPath: explicitPath);

        result.Settings.Approval.TimeoutSeconds.Should().Be(200);
        result.SourcesLoaded.Should().ContainSingle().Which.Should().Be(explicitPath);
    }

    [Fact]
    public void Explicit_path_that_does_not_exist_throws()
    {
        using var temp = new TempDir();
        var missing = Path.Combine(temp.Path, "does-not-exist.json");

        var act = () => LoadIsolated(temp.Path, explicitPath: missing);

        act.Should().Throw<FileNotFoundException>().Which.FileName.Should().Be(missing);
    }

    [Fact]
    public void Validation_error_collects_all_failures_into_one_exception()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            {
              "logging": { "level": "Bogus" },
              "approval": { "timeoutSeconds": 1 },
              "session": { "eventChannelCapacity": 1 }
            }
            """);

        var act = () => LoadIsolated(temp.Path);

        var ex = act.Should().Throw<InfiniContextSettingsException>().Which;
        ex.Errors.Should().HaveCount(3);
        ex.Errors.Should().Contain(e => e.Contains("logging.level"));
        ex.Errors.Should().Contain(e => e.Contains("approval.timeoutSeconds"));
        ex.Errors.Should().Contain(e => e.Contains("session.eventChannelCapacity"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Tasks_lockTtlSeconds_outside_bounds_fails_validation(int value)
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), $$"""
            { "tasks": { "lockTtlSeconds": {{value}} } }
            """);

        var act = () => LoadIsolated(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
            .Which.Errors.Should().ContainSingle(e => e.Contains("tasks.lockTtlSeconds"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Tasks_lockMaxWaitSeconds_outside_bounds_fails_validation(int value)
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), $$"""
            { "tasks": { "lockMaxWaitSeconds": {{value}} } }
            """);

        var act = () => LoadIsolated(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
            .Which.Errors.Should().ContainSingle(e => e.Contains("tasks.lockMaxWaitSeconds"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(101)]
    public void Tasks_softThresholdPct_outside_bounds_fails_validation(int value)
    {
        using var temp = new TempDir();
        // hardThresholdPct stays at default 85; we only want the soft-bounds rule to fire.
        // Pick a hard value that is strictly above any valid soft to avoid the soft<hard rule
        // also firing for soft=100/101.
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), $$"""
            { "tasks": { "softThresholdPct": {{value}}, "hardThresholdPct": 99 } }
            """);

        var act = () => LoadIsolated(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
            .Which.Errors.Should().Contain(e => e.Contains("tasks.softThresholdPct must be in (0, 100)"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(101)]
    public void Tasks_hardThresholdPct_outside_bounds_fails_validation(int value)
    {
        using var temp = new TempDir();
        // softThresholdPct stays at default 60; we only want the hard-bounds rule to fire.
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), $$"""
            { "tasks": { "softThresholdPct": 1, "hardThresholdPct": {{value}} } }
            """);

        var act = () => LoadIsolated(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
            .Which.Errors.Should().Contain(e => e.Contains("tasks.hardThresholdPct must be in (0, 100)"));
    }

    [Theory]
    [InlineData(85, 60)]   // strictly greater
    [InlineData(60, 60)]   // equal — must still fail because rule is strict <
    public void Tasks_softThreshold_must_be_strictly_less_than_hardThreshold(int soft, int hard)
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), $$"""
            { "tasks": { "softThresholdPct": {{soft}}, "hardThresholdPct": {{hard}} } }
            """);

        var act = () => LoadIsolated(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
            .Which.Errors.Should().Contain(e => e.Contains("strictly less than"));
    }

    [Fact]
    public void Tasks_modelContextWindows_blank_id_fails_validation()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "tasks": { "modelContextWindows": { "   ": 200000 } } }
            """);

        var act = () => LoadIsolated(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
            .Which.Errors.Should().Contain(e => e.Contains("tasks.modelContextWindows contains a blank model id"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Tasks_modelContextWindows_non_positive_window_fails_validation(int window)
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), $$"""
            { "tasks": { "modelContextWindows": { "claude-test-model": {{window}} } } }
            """);

        var act = () => LoadIsolated(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
            .Which.Errors.Should().Contain(e => e.Contains("tasks.modelContextWindows['claude-test-model']"));
    }

    [Fact]
    public void Tasks_lockTtlSeconds_must_not_exceed_approval_timeoutSeconds()
    {
        // ApprovalChannel serialises pipe traffic, so a long approval blocks lock-release
        // messages. TTL > timeout would let peers see real starvation.
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            {
              "approval": { "timeoutSeconds": 30 },
              "tasks": { "lockTtlSeconds": 60 }
            }
            """);

        var act = () => LoadIsolated(temp.Path);

        act.Should().Throw<InfiniContextSettingsException>()
            .Which.Errors.Should().Contain(e => e.Contains("must be <= approval.timeoutSeconds"));
    }

    [Fact]
    public void Tasks_validation_accepts_boundary_values()
    {
        // Pin the inclusive ends of every Phase-7+8 range so a typo flipping `<= 0` to `< 0`
        // (etc.) on the production rule fails this test.
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            {
              "tasks": {
                "lockTtlSeconds": 1,
                "lockMaxWaitSeconds": 3600,
                "softThresholdPct": 1,
                "hardThresholdPct": 99
              }
            }
            """);

        var act = () => LoadIsolated(temp.Path);

        act.Should().NotThrow();
    }

    [Fact]
    public void Effective_json_round_trips_back_into_equivalent_settings()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            {
              "approval": { "timeoutSeconds": 75 },
              "session": { "model": "claude-sonnet-4-6", "extraArgs": ["--bare"] },
              "cosmetics": { "repl": { "prompt": "$> " } }
            }
            """);

        var first = LoadIsolated(temp.Path);

        // Write the EffectiveJson back out, treat it as the new defaults file, reload.
        using var temp2 = new TempDir();
        File.WriteAllText(Path.Combine(temp2.Path, "settings.json"), first.EffectiveJson);
        var second = LoadIsolated(temp2.Path);

        second.Settings.Should().BeEquivalentTo(first.Settings);
    }

    // ---------- ReviewSettings ----------

    [Fact]
    public void ReviewSettings_defaults_match_the_design()
    {
        var review = new ReviewSettings();
        review.ReviewerModel.Should().BeNull();
        review.CoordinatorModel.Should().BeNull();
        review.ConfidenceFloor.Should().Be(0.9);
        review.AutoApplyMaxLinesPerFinding.Should().Be(50);
        review.AutoApplyMaxFilesPerFinding.Should().Be(3);
        review.PerFileByteCap.Should().Be(204_800);
        review.InvocationTimeoutSeconds.Should().Be(600);
        review.SkipBuildTestGate.Should().BeFalse();
    }

    [Fact]
    public void ReviewSettings_binds_from_settings_json()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            {
              "review": {
                "reviewerModel": "claude-opus-4-7",
                "coordinatorModel": "claude-sonnet-4-6",
                "confidenceFloor": 0.85,
                "autoApplyMaxLinesPerFinding": 30,
                "autoApplyMaxFilesPerFinding": 2,
                "perFileByteCap": 102400,
                "invocationTimeoutSeconds": 300,
                "skipBuildTestGate": true
              }
            }
            """);

        var result = LoadIsolated(temp.Path);
        result.Settings.Review.ReviewerModel.Should().Be("claude-opus-4-7");
        result.Settings.Review.CoordinatorModel.Should().Be("claude-sonnet-4-6");
        result.Settings.Review.ConfidenceFloor.Should().Be(0.85);
        result.Settings.Review.AutoApplyMaxLinesPerFinding.Should().Be(30);
        result.Settings.Review.AutoApplyMaxFilesPerFinding.Should().Be(2);
        result.Settings.Review.PerFileByteCap.Should().Be(102_400);
        result.Settings.Review.InvocationTimeoutSeconds.Should().Be(300);
        result.Settings.Review.SkipBuildTestGate.Should().BeTrue();
    }

    [Fact]
    public void ReviewSettings_omitted_section_keeps_compile_time_defaults()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "approval": { "timeoutSeconds": 60 } }
            """);

        var result = LoadIsolated(temp.Path);
        result.Settings.Review.ConfidenceFloor.Should().Be(0.9);
        result.Settings.Review.PerFileByteCap.Should().Be(204_800);
    }

    [Collection(nameof(EnvironmentVariableCollection))]
    public class EnvVarBehavior
    {
        [Fact]
        public void Env_vars_overlay_above_files_when_enabled()
        {
            using var temp = new TempDir();
            File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
                { "approval": { "timeoutSeconds": 60 } }
                """);
            File.WriteAllText(Path.Combine(temp.Path, "settings.user.json"), """
                { "approval": { "timeoutSeconds": 90 } }
                """);

            using var env = new EnvVar("INFINICONTEXT__APPROVAL__TIMEOUTSECONDS", "150");

            var result = InfiniContextSettingsLoader.Load(new InfiniContextSettingsLoader.LoadOptions
            {
                BaseDirectory = temp.Path,
                IncludeUserOverrides = true,
                IncludeEnvironmentVariables = true,
            });

            result.Settings.Approval.TimeoutSeconds.Should().Be(150);
        }

        [Fact]
        public void Env_vars_are_ignored_when_IncludeEnvironmentVariables_is_false()
        {
            using var temp = new TempDir();
            File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
                { "approval": { "timeoutSeconds": 60 } }
                """);

            using var env = new EnvVar("INFINICONTEXT__APPROVAL__TIMEOUTSECONDS", "150");

            var result = InfiniContextSettingsLoader.Load(new InfiniContextSettingsLoader.LoadOptions
            {
                BaseDirectory = temp.Path,
                IncludeUserOverrides = false,
                IncludeEnvironmentVariables = false,
            });

            result.Settings.Approval.TimeoutSeconds.Should().Be(60);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cp-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class EnvVar : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVar(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}

[CollectionDefinition(nameof(EnvironmentVariableCollection), DisableParallelization = true)]
public sealed class EnvironmentVariableCollection { }
