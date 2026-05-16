using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace InfiniContext.Core.Sessions;

/// <summary>
/// Stage 2B Phase 3. Flat-JSON registry of recent sessions, persisted at
/// <c>~/.infinicontext/index.json</c> (per design doc §4.5 and decision D5). Read on host startup,
/// written when a session closes. Out of scope for Stage 2B (per the design doc Risk note on
/// Phase 3): cross-host-instance write coordination — concurrent hosts will clobber each other's
/// updates. Atomic temp+rename gives single-host safety only.
/// </summary>
public sealed class SessionIndex
{
    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Schema version. Bump alongside a breaking change to <see cref="SessionIndexEntry"/>;
    /// older versions are treated as empty by <see cref="LoadAsync"/>.</summary>
    public int Version { get; init; } = 1;

    public List<SessionIndexEntry> Sessions { get; init; } = new();

    /// <summary>
    /// Load the index from disk. Returns an empty index when the file is missing, the schema
    /// version doesn't match, or the JSON is malformed. Acceptance criterion: "Malformed index
    /// file does not crash startup."
    /// </summary>
    public static async Task<SessionIndex> LoadAsync(string path, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var log = logger ?? Log.Logger;

        if (!File.Exists(path))
        {
            return new SessionIndex();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var loaded = await JsonSerializer.DeserializeAsync<SessionIndex>(stream, s_serializerOptions).ConfigureAwait(false);
            if (loaded is null)
            {
                log.Warning("session-index: {Path} deserialized to null; treating as empty", path);
                return new SessionIndex();
            }
            if (loaded.Version != 1)
            {
                log.Warning("session-index: {Path} version {Version} is unsupported; treating as empty", path, loaded.Version);
                return new SessionIndex();
            }
            return loaded;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "session-index: {Path} could not be parsed; treating as empty", path);
            return new SessionIndex();
        }
    }

    /// <summary>
    /// Persist atomically: write to a sibling temp file then rename over the target. The
    /// parent directory is created if missing. A failed write leaves the existing file
    /// untouched.
    /// </summary>
    public async Task SaveAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, this, s_serializerOptions).ConfigureAwait(false);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>Default resolution path: <c>~/.infinicontext/index.json</c>. Pure function — does no I/O.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".infinicontext",
        "index.json");
}
