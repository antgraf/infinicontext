using FluentAssertions;
using InfiniContext.Core.Sessions;

namespace InfiniContext.Tests.Sessions;

public class HostInstanceLockTests
{
    [Fact]
    public void Acquire_then_second_Acquire_throws_HostAlreadyRunning()
    {
        var path = TempLockPath();

        using var first = HostInstanceLock.Acquire(path);

        var act = () => HostInstanceLock.Acquire(path);

        act.Should().Throw<HostAlreadyRunningException>()
            .Which.LockPath.Should().Be(path);
    }

    [Fact]
    public void Dispose_releases_the_lock_so_a_fresh_host_can_acquire()
    {
        var path = TempLockPath();

        var first = HostInstanceLock.Acquire(path);
        first.Dispose();

        // A second host starting after the first tore down must succeed.
        using var second = HostInstanceLock.Acquire(path);
        second.Path.Should().Be(path);
    }

    [Fact]
    public void Dispose_removes_the_sentinel_file_via_DeleteOnClose()
    {
        var path = TempLockPath();

        var hostLock = HostInstanceLock.Acquire(path);
        File.Exists(path).Should().BeTrue("the sentinel exists while held");

        hostLock.Dispose();

        File.Exists(path).Should().BeFalse(
            "DeleteOnClose removes the sentinel so a stale file never wedges the next host");
    }

    [Fact]
    public void Acquire_creates_the_parent_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"icx-hostlock-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "host.lock");
        try
        {
            Directory.Exists(dir).Should().BeFalse();

            using var hostLock = HostInstanceLock.Acquire(path);

            Directory.Exists(dir).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void DefaultPath_honors_the_environment_override()
    {
        var custom = TempLockPath();
        var previous = Environment.GetEnvironmentVariable(HostInstanceLock.PathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(HostInstanceLock.PathEnvVar, custom);

            HostInstanceLock.DefaultPath().Should().Be(custom);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostInstanceLock.PathEnvVar, previous);
        }
    }

    [Fact]
    public void DefaultPath_falls_back_to_user_profile_when_override_absent()
    {
        var previous = Environment.GetEnvironmentVariable(HostInstanceLock.PathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(HostInstanceLock.PathEnvVar, null);

            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".infinicontext",
                "host.lock");

            HostInstanceLock.DefaultPath().Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostInstanceLock.PathEnvVar, previous);
        }
    }

    private static string TempLockPath() =>
        Path.Combine(Path.GetTempPath(), $"icx-hostlock-{Guid.NewGuid():N}.lock");
}
