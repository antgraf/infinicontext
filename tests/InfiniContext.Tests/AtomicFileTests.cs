using System.Text;
using InfiniContext.Core;
using FluentAssertions;

namespace InfiniContext.Tests;

public class AtomicFileTests
{
    [Fact]
    public async Task Writes_new_file_with_utf8_no_bom()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "out.md");

        await AtomicFile.WriteAllTextAsync(path, "héllo\nworld", TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        // No UTF-8 BOM (EF BB BF) prefix.
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        Encoding.UTF8.GetString(bytes).Should().Be("héllo\nworld");
    }

    [Fact]
    public async Task Overwrites_existing_file()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "out.md");
        await File.WriteAllTextAsync(path, "original-content", TestContext.Current.CancellationToken);

        await AtomicFile.WriteAllTextAsync(path, "replacement", TestContext.Current.CancellationToken);

        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be("replacement");
    }

    [Fact]
    public async Task Leaves_no_temp_files_behind()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "out.md");

        await AtomicFile.WriteAllTextAsync(path, "content", TestContext.Current.CancellationToken);

        Directory.EnumerateFiles(tmp.Path).Should().ContainSingle().Which.Should().Be(path);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cp-atomic-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
