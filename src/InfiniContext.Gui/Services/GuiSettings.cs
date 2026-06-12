using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace InfiniContext.Gui.Services;

/// <summary>
/// GUI-only, user-wide persisted settings (layout state, not behaviour). Persisted at
/// <c>~/.infinicontext/gui-settings.json</c>. Deliberately separate from
/// <c>InfiniContextSettings</c>: GUI layout doesn't belong in the shared host config that
/// CLI/sidecar/tests also load, and the CLI has no use for column widths.
///
/// Schema is intentionally permissive - missing fields fall back to defaults so a stale file
/// from an older GUI build can be loaded without losing the user's other prefs.
/// </summary>
public sealed class GuiSettings
{
    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Schema version. Bump on a breaking change; older versions load as defaults.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Width of the left (Tasks &gt; Sessions) column, in DIPs. Clamped to a sane range
    /// on load so a corrupt file can't render the column zero-pixels-wide.</summary>
    public double LeftColumnWidth { get; set; } = Defaults.LeftColumnWidth;

    /// <summary>Window width in DIPs. Clamped to a sane range on load so a corrupt file
    /// can't open the window off-screen.</summary>
    public double WindowWidth { get; set; } = Defaults.WindowWidth;

    /// <summary>Window height in DIPs. Clamped to a sane range on load.</summary>
    public double WindowHeight { get; set; } = Defaults.WindowHeight;

    /// <summary>True if the window was maximized at last close; the next launch restores that
    /// state. Stored as a bool rather than the full Avalonia <c>WindowState</c> enum so the JSON
    /// stays portable across Avalonia versions.</summary>
    public bool WindowMaximized { get; set; } = false;

    /// <summary>X coordinate of the window's top-left at last close, in physical screen pixels
    /// (Avalonia <c>PixelPoint</c> space), or null when never positioned. This is what makes the
    /// window reopen on the monitor it was last on instead of always the primary. Stored in
    /// physical pixels - not DIPs like <see cref="WindowWidth"/> - because it's a screen-space
    /// coordinate. Deliberately not clamped on load: validity is screen-relative, so the View
    /// checks the saved point against the currently connected displays before applying it (a
    /// disconnected monitor falls back to OS default placement). Null is omitted from the JSON.</summary>
    public int? WindowX { get; set; }

    /// <summary>Y coordinate of the window's top-left at last close, in physical screen pixels.
    /// See <see cref="WindowX"/>.</summary>
    public int? WindowY { get; set; }

    /// <summary>The on-disk path this instance was loaded from (set by <see cref="LoadAsync"/>), so
    /// <see cref="SaveAsync"/> writes back to the same file without the caller re-threading the
    /// path. Lets a non-default path (e.g. the <c>INFINICONTEXT__GUI__SETTINGSPATH</c> override for
    /// hermetic UIA verification) survive the load->save round-trip. Not serialized.</summary>
    [JsonIgnore]
    public string? SourcePath { get; set; }

    public static class Defaults
    {
        public const double LeftColumnWidth = 340;
        public const double LeftColumnMin = 220;
        public const double LeftColumnMax = 800;
        public const double WindowWidth = 1100;
        public const double WindowHeight = 700;
        public const double WindowMinWidth = 720;
        public const double WindowMinHeight = 480;
        public const double WindowMaxWidth = 7680;   // 8K horizontal - generous upper bound.
        public const double WindowMaxHeight = 4320;  // 8K vertical.
    }

    /// <summary>Default resolution path: <c>~/.infinicontext/gui-settings.json</c>. Pure function.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".infinicontext",
        "gui-settings.json");

    /// <summary>Load from disk, applying defaults for missing/out-of-range fields. Never throws
    /// - a corrupt file logs a warning and returns defaults so the GUI always opens.</summary>
    public static async Task<GuiSettings> LoadAsync(string? path = null, ILogger? logger = null)
    {
        var resolved = path ?? DefaultPath();
        var log = logger ?? Log.Logger;

        if (!File.Exists(resolved))
        {
            return new GuiSettings { SourcePath = resolved };
        }

        try
        {
            await using var stream = File.OpenRead(resolved);
            var loaded = await JsonSerializer.DeserializeAsync<GuiSettings>(stream, s_serializerOptions).ConfigureAwait(false);
            if (loaded is null)
            {
                log.Warning("gui-settings: {Path} deserialized to null; using defaults", resolved);
                return new GuiSettings { SourcePath = resolved };
            }
            loaded.Clamp();
            loaded.SourcePath = resolved;
            return loaded;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "gui-settings: {Path} could not be parsed; using defaults", resolved);
            return new GuiSettings { SourcePath = resolved };
        }
    }

    /// <summary>Atomic write (temp + rename), same shape as <c>SessionIndex.SaveAsync</c>.</summary>
    public async Task SaveAsync(string? path = null)
    {
        var resolved = path ?? SourcePath ?? DefaultPath();
        Clamp();

        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = resolved + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, this, s_serializerOptions).ConfigureAwait(false);
            }
            File.Move(tempPath, resolved, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
    }

    private void Clamp()
    {
        if (double.IsNaN(LeftColumnWidth) || double.IsInfinity(LeftColumnWidth))
            LeftColumnWidth = Defaults.LeftColumnWidth;
        if (LeftColumnWidth < Defaults.LeftColumnMin) LeftColumnWidth = Defaults.LeftColumnMin;
        if (LeftColumnWidth > Defaults.LeftColumnMax) LeftColumnWidth = Defaults.LeftColumnMax;

        if (double.IsNaN(WindowWidth) || double.IsInfinity(WindowWidth))
            WindowWidth = Defaults.WindowWidth;
        if (WindowWidth < Defaults.WindowMinWidth) WindowWidth = Defaults.WindowMinWidth;
        if (WindowWidth > Defaults.WindowMaxWidth) WindowWidth = Defaults.WindowMaxWidth;

        if (double.IsNaN(WindowHeight) || double.IsInfinity(WindowHeight))
            WindowHeight = Defaults.WindowHeight;
        if (WindowHeight < Defaults.WindowMinHeight) WindowHeight = Defaults.WindowMinHeight;
        if (WindowHeight > Defaults.WindowMaxHeight) WindowHeight = Defaults.WindowMaxHeight;
    }
}
