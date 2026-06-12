using FluentAssertions;
using InfiniContext.Core.Configuration;
using InfiniContext.Gui.Services;

namespace InfiniContext.Tests.Gui;

/// <summary>
/// Settings-to-<see cref="InfiniContext.Core.Sessions.SpawnRequest"/> mapping for GUI session
/// spawns (<see cref="SessionHost.BuildSpawnRequest"/>). Guards the task 0016 issues.md#0001
/// regression: the GUI dropped <c>session.extraArgs</c> while every CLI spawn path forwarded it.
/// </summary>
public class SessionHostSpawnRequestTests
{
    [Fact]
    public void BuildSpawnRequest_forwards_session_extraArgs_from_settings()
    {
        var settings = InfiniContextSettings.Defaults with
        {
            Session = InfiniContextSettings.Defaults.Session with
            {
                ExtraArgs = new[] { "--replay", "fixture.jsonl" },
            },
        };

        var request = SessionHost.BuildSpawnRequest(settings, cwd: "/repo/x");

        request.ExtraArgs.Should().Equal(new[] { "--replay", "fixture.jsonl" },
            "the CLI paths forward settings.Session.ExtraArgs into SpawnRequest and the GUI must match");
    }

    [Fact]
    public void BuildSpawnRequest_explicit_model_wins_over_settings_chain()
    {
        var settings = InfiniContextSettings.Defaults with
        {
            Session = InfiniContextSettings.Defaults.Session with { Model = "claude-sonnet-4-6" },
        };

        var request = SessionHost.BuildSpawnRequest(settings, cwd: "/repo/x", model: "claude-fable-5");

        request.Model.Should().Be("claude-fable-5");
    }

    [Fact]
    public void BuildSpawnRequest_blank_model_falls_through_to_session_setting_then_main()
    {
        var withSessionModel = InfiniContextSettings.Defaults with
        {
            Session = InfiniContextSettings.Defaults.Session with { Model = "claude-sonnet-4-6" },
        };
        SessionHost.BuildSpawnRequest(withSessionModel, cwd: "/repo/x", model: "  ")
            .Model.Should().Be("claude-sonnet-4-6", "blank explicit model means unset");

        SessionHost.BuildSpawnRequest(InfiniContextSettings.Defaults, cwd: "/repo/x")
            .Model.Should().Be(InfiniContextSettings.Defaults.Models.Main.Id,
                "with no explicit or session model the spawn pins the configured main model");
    }

    [Fact]
    public void BuildSpawnRequest_carries_task_binding_and_seed_prompt()
    {
        var request = SessionHost.BuildSpawnRequest(
            InfiniContextSettings.Defaults, cwd: "/repo/x", activeTaskId: 7, firstUserMessage: "pointer");

        request.Cwd.Should().Be("/repo/x");
        request.ActiveTaskId.Should().Be(7);
        request.FirstUserMessage.Should().Be("pointer");
    }
}
