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

    public static class Defaults
    {
        public const double LeftColumnWidth = 340;
        public const double LeftColumnMin = 220;
        public const double LeftColumnMax = 800;
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
            return new GuiSettings();
        }

        try
        {
            await using var stream = File.OpenRead(resolved);
            var loaded = await JsonSerializer.DeserializeAsync<GuiSettings>(stream, s_serializerOptions).ConfigureAwait(false);
            if (loaded is null)
            {
                log.Warning("gui-settings: {Path} deserialized to null; using defaults", resolved);
                return new GuiSettings();
            }
            loaded.Clamp();
            return loaded;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "gui-settings: {Path} could not be parsed; using defaults", resolved);
            return new GuiSettings();
        }
    }

    /// <summary>Atomic write (temp + rename), same shape as <c>SessionIndex.SaveAsync</c>.</summary>
    public async Task SaveAsync(string? path = null)
    {
        var resolved = path ?? DefaultPath();
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
    }
}
