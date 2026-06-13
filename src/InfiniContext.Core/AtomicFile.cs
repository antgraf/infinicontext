using System.Text;

namespace InfiniContext.Core;

/// <summary>
/// Crash-safe text file writes. The task artifacts (<c>progress.md</c>, <c>index.md</c>,
/// <c>issues.md</c>) are append-only audit logs edited via full read-modify-rewrite; a plain
/// <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/> that is interrupted
/// mid-write (host crash, power loss) truncates the file and loses history (M18).
///
/// <para>
/// Strategy: write the full new content to a sibling temp file in the <em>same directory</em>
/// (so the subsequent move stays on one volume and is atomic), flush it to disk, then
/// <see cref="File.Move(string, string, bool)"/> over the destination. A crash before the move
/// leaves the original intact; a crash after it leaves the complete new content. The temp file
/// is best-effort cleaned up on the failure path.
/// </para>
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(dir)) dir = ".";
        // Temp name in the same directory. Guid suffix avoids collisions between concurrent
        // writers to different files in the same folder; the per-file lock contract serialises
        // writers to the SAME file, so the move target is never racing another atomic write.
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var bytes = Utf8NoBom.GetBytes(content);
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }
}
