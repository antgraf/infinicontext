using InfiniContext.Core.Sessions;
using FluentAssertions;
using Serilog;

namespace InfiniContext.Tests.Sessions;

public class SessionIndexTests
{
    [Fact]
    public async Task LoadAsync_missing_file_returns_empty_index()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cp-index-missing-{Guid.NewGuid():N}.json");

        var index = await SessionIndex.LoadAsync(path, Log.Logger);

        index.Version.Should().Be(1);
        index.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips_entries()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "index.json");
        var entry = new SessionIndexEntry
        {
            SessionId = "11111111-1111-1111-1111-111111111111",
            Cwd = "/tmp/repo",
            Model = "claude-sonnet-4-6",
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            LastActive = DateTimeOffset.FromUnixTimeSeconds(1_700_000_500),
            Title = null,
            LastState = "Exited",
            ExitCode = 0,
        };
        var original = new SessionIndex { Sessions = { entry } };

        await original.SaveAsync(path);
        var roundtrip = await SessionIndex.LoadAsync(path, Log.Logger);

        roundtrip.Version.Should().Be(1);
        roundtrip.Sessions.Should().ContainSingle().Which.Should().Be(entry);
    }

    [Fact]
    public async Task LoadAsync_malformed_file_returns_empty_index_without_throwing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "index.json");
        await File.WriteAllTextAsync(path, "{not valid json");

        var index = await SessionIndex.LoadAsync(path, Log.Logger);

        index.Sessions.Should().BeEmpty("malformed contents must not crash startup");
    }

    [Fact]
    public async Task LoadAsync_unsupported_version_returns_empty_index()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "index.json");
        await File.WriteAllTextAsync(path, """{ "version": 99, "sessions": [] }""");

        var index = await SessionIndex.LoadAsync(path, Log.Logger);

        index.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_creates_parent_directory_if_missing()
    {
        using var tmp = new TempDir();
        var nested = Path.Combine(tmp.Path, "deeper", "nested");
        var path = Path.Combine(nested, "index.json");
        var index = new SessionIndex();

        await index.SaveAsync(path);

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_overwrites_existing_file_atomically()
    {
        // Two successive Saves should yield the second's content with no .tmp residue.
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "index.json");
        var first = new SessionIndex { Sessions = { NewEntry("aaaa") } };
        var second = new SessionIndex { Sessions = { NewEntry("bbbb"), NewEntry("cccc") } };

        await first.SaveAsync(path);
        await second.SaveAsync(path);

        var loaded = await SessionIndex.LoadAsync(path, Log.Logger);
        loaded.Sessions.Select(e => e.SessionId).Should().Equal(new[] { "bbbb", "cccc" });
        Directory.GetFiles(tmp.Path).Should().NotContain(p => p.Contains(".tmp."),
            "temp files must be cleaned up after a successful atomic rename");
    }

    [Fact]
    public void DefaultPath_resolves_under_user_profile()
    {
        var path = SessionIndex.DefaultPath();

        path.Should().EndWith(Path.Combine(".infinicontext", "index.json"));
        Path.IsPathFullyQualified(path).Should().BeTrue();
    }

    private static SessionIndexEntry NewEntry(string id) => new()
    {
        SessionId = id,
        Cwd = "/tmp",
        CreatedAt = DateTimeOffset.UnixEpoch,
        LastActive = DateTimeOffset.UnixEpoch,
    };

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cp-sessidx-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
