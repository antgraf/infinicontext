using FluentAssertions;
using InfiniContext.Gui.Services;

namespace InfiniContext.Tests.Gui;

/// <summary>
/// Tests for <see cref="GuiSettings"/> persistence - specifically the window-placement fields the
/// View round-trips through it. Each test owns a temp file path and passes it explicitly so the
/// dev's real <c>~/.infinicontext/gui-settings.json</c> is never touched. The screen-validation of
/// a restored position lives in the (Avalonia-bound) View, not here; what's testable in isolation
/// is the serialization contract: a saved position survives a reload, and an unset position stays
/// null rather than being coerced to a default (task 0021).
/// </summary>
public class GuiSettingsTests
{
    [Fact]
    public async Task Window_position_round_trips_through_save_and_load()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "gui-settings.json");

        var saved = new GuiSettings { WindowX = -1920, WindowY = 240 };
        await saved.SaveAsync(path);

        var loaded = await GuiSettings.LoadAsync(path);

        // Negative X is legitimate - a secondary monitor to the left of the primary lives in
        // negative PixelPoint space. The setting must not clamp it away.
        loaded.WindowX.Should().Be(-1920);
        loaded.WindowY.Should().Be(240);
    }

    [Fact]
    public async Task Unset_position_stays_null_after_round_trip()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "gui-settings.json");

        await new GuiSettings().SaveAsync(path);

        // Null means "never positioned" - the View falls back to OS placement. It must not become
        // 0,0 (a real on-screen coordinate that would wrongly pin the window to the corner).
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        json.Should().NotContain("windowX");
        json.Should().NotContain("windowY");

        var loaded = await GuiSettings.LoadAsync(path);
        loaded.WindowX.Should().BeNull();
        loaded.WindowY.Should().BeNull();
    }

    [Fact]
    public async Task Missing_position_fields_in_existing_file_load_as_null()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "gui-settings.json");
        // A file written by an older GUI build that predates position persistence: it carries
        // size/maximized but no windowX/windowY. The permissive schema must load it without error.
        await File.WriteAllTextAsync(path, """
            { "version": 1, "windowWidth": 1280, "windowHeight": 800, "windowMaximized": false }
            """, TestContext.Current.CancellationToken);

        var loaded = await GuiSettings.LoadAsync(path);

        loaded.WindowWidth.Should().Be(1280);
        loaded.WindowX.Should().BeNull();
        loaded.WindowY.Should().BeNull();
    }

    [Fact]
    public async Task SourcePath_from_load_makes_save_write_back_to_same_file()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "custom-gui-settings.json");
        await File.WriteAllTextAsync(path, """
            { "version": 1, "windowX": -1920, "windowY": 50 }
            """, TestContext.Current.CancellationToken);

        // Load from the explicit path, mutate, then save with NO path argument. The instance must
        // remember where it came from (SourcePath) and write back there - this is what lets the
        // INFINICONTEXT__GUI__SETTINGSPATH override survive the VM's path-less SaveLayoutAsync call.
        var loaded = await GuiSettings.LoadAsync(path);
        loaded.SourcePath.Should().Be(path);
        loaded.WindowX = -100;
        await loaded.SaveAsync();

        var reloaded = await GuiSettings.LoadAsync(path);
        reloaded.WindowX.Should().Be(-100);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cp-gui-settings-{Guid.NewGuid():N}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ } }
    }
}
