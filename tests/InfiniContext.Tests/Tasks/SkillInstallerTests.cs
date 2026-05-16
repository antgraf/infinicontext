using InfiniContext.Core.Tasks.Skill;
using FluentAssertions;

namespace InfiniContext.Tests.Tasks;

public class SkillInstallerTests
{
    [Fact]
    public async Task Install_creates_file_and_directory_when_missing()
    {
        using var home = new TempHome();

        var result = await SkillInstaller.InstallIfMissingOrOutdatedAsync(home.Path);

        result.Should().Be(SkillInstaller.InstallResult.Installed);
        File.Exists(InfiniContextTaskSkill.GetSkillFilePath(home.Path)).Should().BeTrue();
        var content = await File.ReadAllTextAsync(InfiniContextTaskSkill.GetSkillFilePath(home.Path));
        content.Should().Be(InfiniContextTaskSkill.SkillMarkdown);
    }

    [Fact]
    public async Task Second_install_at_same_version_is_a_noop()
    {
        using var home = new TempHome();
        await SkillInstaller.InstallIfMissingOrOutdatedAsync(home.Path);
        var skillPath = InfiniContextTaskSkill.GetSkillFilePath(home.Path);
        var firstWriteUtc = File.GetLastWriteTimeUtc(skillPath);
        // Step the timestamp back so a rewrite is observable.
        File.SetLastWriteTimeUtc(skillPath, firstWriteUtc.AddHours(-1));
        var pretendLastWrite = File.GetLastWriteTimeUtc(skillPath);

        var result = await SkillInstaller.InstallIfMissingOrOutdatedAsync(home.Path);

        result.Should().Be(SkillInstaller.InstallResult.UpToDate);
        File.GetLastWriteTimeUtc(skillPath).Should().Be(pretendLastWrite); // unchanged
    }

    [Fact]
    public async Task File_with_older_embedded_version_is_overwritten()
    {
        using var home = new TempHome();
        var skillDir = InfiniContextTaskSkill.GetInstallDir(home.Path);
        var skillPath = InfiniContextTaskSkill.GetSkillFilePath(home.Path);
        Directory.CreateDirectory(skillDir);
        // Write an older version so the installer sees v0 < current.
        var older = $"# old content\n{InfiniContextTaskSkill.VersionMarkerPrefix}0{InfiniContextTaskSkill.VersionMarkerSuffix}\n";
        await File.WriteAllTextAsync(skillPath, older);

        var result = await SkillInstaller.InstallIfMissingOrOutdatedAsync(home.Path);

        result.Should().Be(SkillInstaller.InstallResult.Updated);
        var content = await File.ReadAllTextAsync(skillPath);
        content.Should().Be(InfiniContextTaskSkill.SkillMarkdown);
    }

    [Fact]
    public async Task Overwrite_writes_previous_content_to_SKILL_md_bak()
    {
        // Simulate a user who customised SKILL.md with no version marker — the installer treats
        // that as outdated and overwrites. The previous content must land in SKILL.md.bak so the
        // user can recover their edits.
        using var home = new TempHome();
        var skillDir = InfiniContextTaskSkill.GetInstallDir(home.Path);
        var skillPath = InfiniContextTaskSkill.GetSkillFilePath(home.Path);
        Directory.CreateDirectory(skillDir);
        const string customContent = "# my custom edits\n\nlots of careful work here";
        await File.WriteAllTextAsync(skillPath, customContent);

        var result = await SkillInstaller.InstallIfMissingOrOutdatedAsync(home.Path);

        result.Should().Be(SkillInstaller.InstallResult.Updated);
        var backupPath = skillPath + SkillInstaller.BackupSuffix;
        File.Exists(backupPath).Should().BeTrue("the prior content must be preserved at SKILL.md.bak");
        (await File.ReadAllTextAsync(backupPath)).Should().Be(customContent);
        (await File.ReadAllTextAsync(skillPath)).Should().Be(InfiniContextTaskSkill.SkillMarkdown);
    }

    [Fact]
    public async Task File_without_version_marker_is_treated_as_outdated()
    {
        using var home = new TempHome();
        var skillDir = InfiniContextTaskSkill.GetInstallDir(home.Path);
        var skillPath = InfiniContextTaskSkill.GetSkillFilePath(home.Path);
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(skillPath, "# arbitrary user-edited content with no marker");

        var result = await SkillInstaller.InstallIfMissingOrOutdatedAsync(home.Path);

        result.Should().Be(SkillInstaller.InstallResult.Updated);
        (await File.ReadAllTextAsync(skillPath)).Should().Be(InfiniContextTaskSkill.SkillMarkdown);
    }

    [Theory]
    [InlineData("<!-- infinicontext-task-skill-version: 1 -->", true, 1)]
    [InlineData("body\n\n<!-- infinicontext-task-skill-version: 42 -->\n", true, 42)]
    [InlineData("body without marker", false, 0)]
    [InlineData("<!-- infinicontext-task-skill-version: NaN -->", false, 0)]
    [InlineData("", false, 0)]
    public void TryParseVersion_extracts_marker_correctly(string content, bool expectedFound, int expectedVersion)
    {
        var found = SkillInstaller.TryParseVersion(content, out var version);

        found.Should().Be(expectedFound);
        version.Should().Be(expectedVersion);
    }

    [Fact]
    public void Skill_markdown_contains_required_metadata_and_version_marker()
    {
        var md = InfiniContextTaskSkill.SkillMarkdown;

        md.Should().StartWith("---\nname: infinicontext-task\n");
        md.Should().Contain("description: Use when working with InfiniContext tasks");
        md.Should().Contain("# InfiniContext task management");
        md.Should().Contain(".task/");
        md.Should().Contain("index.md");
        md.Should().Contain("progress.md");
        md.Should().EndWith($"{InfiniContextTaskSkill.VersionMarkerPrefix}{InfiniContextTaskSkill.Version}{InfiniContextTaskSkill.VersionMarkerSuffix}");
    }

    [Fact]
    public void Install_dir_resolves_under_user_profile()
    {
        var system = InfiniContextTaskSkill.GetInstallDir();
        var withOverride = InfiniContextTaskSkill.GetInstallDir("/tmp/fake-home");

        system.Should().EndWith(Path.Combine(".claude", "skills", "infinicontext-task"));
        withOverride.Should().Be(Path.Combine("/tmp/fake-home", ".claude", "skills", "infinicontext-task"));
    }

    private sealed class TempHome : IDisposable
    {
        public string Path { get; }

        public TempHome()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cp-skill-home-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
