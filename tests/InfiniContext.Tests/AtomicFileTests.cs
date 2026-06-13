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

    [Fact]
    public async Task Cancellation_leaves_existing_target_unchanged_and_no_temp_behind()
    {
        // The whole point of AtomicFile: an interrupted write must not lose the original. Drive the
        // failure branch with a pre-cancelled token (stands in for the host crash / power loss the
        // doc-comment describes) and assert the original survives and no .tmp turd remains.
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "out.md");
        await File.WriteAllTextAsync(path, "original-content", TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => AtomicFile.WriteAllTextAsync(path, "replacement-that-must-not-land", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))
            .Should().Be("original-content", "a cancelled atomic write must not clobber the original.");
        Directory.EnumerateFiles(tmp.Path).Should().ContainSingle().Which.Should().Be(path,
            "the temp file written before the move must be cleaned up on the failure path.");
    }

    [Fact]
    public async Task Failed_move_cleans_up_temp_and_does_not_destroy_an_existing_directory()
    {
        // Force the File.Move to fail by pointing the destination at an existing directory. The temp
        // write succeeds, the move throws, and the catch must delete the temp so no litter remains.
        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "sub"); // a directory, not a file
        Directory.CreateDirectory(target);

        var act = () => AtomicFile.WriteAllTextAsync(target, "content", TestContext.Current.CancellationToken);

        // The move over an existing directory surfaces as IOException or (on Windows)
        // UnauthorizedAccessException — both derive from SystemException.
        await act.Should().ThrowAsync<SystemException>();
        Directory.Exists(target).Should().BeTrue("the failed move must not have removed the directory.");
        Directory.EnumerateFiles(tmp.Path).Should().BeEmpty("the temp file must be cleaned up on the failure path.");
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
