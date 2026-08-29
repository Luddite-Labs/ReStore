using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class ConfigLifecycleNoticeTests
{
    private const string ConfigPath = @"C:\Users\tester\ReStore\config.json";

    [Fact]
    public void ConfigCreatedIsReportedWhenNoWizardFollows()
    {
        var notice = ConfigLifecycleNotice.Build(
            new ConfigSetupResult { ConfigCreated = true },
            migrationResult: null,
            ConfigPath,
            suppressConfigCreatedNotice: false);

        notice.Should().NotBeNull();
        notice.Should().Contain("Created your initial configuration file.");
        notice.Should().Contain(ConfigPath);
    }

    [Fact]
    public void ConfigCreatedIsSilentWhenWizardFollows()
    {
        var notice = ConfigLifecycleNotice.Build(
            new ConfigSetupResult { ConfigCreated = true },
            migrationResult: null,
            ConfigPath,
            suppressConfigCreatedNotice: true);

        notice.Should().BeNull();
    }

    [Fact]
    public void MigrationIsStillReportedWhenCreationIsSuppressed()
    {
        // Suppression is only about the redundant creation notice; a schema upgrade is
        // information the wizard never covers.
        var migration = new ConfigMigrationResult(3, 4) { BackupPath = @"C:\backup\config.v3.json" };
        migration.AddMigration("Added notification configuration defaults.");

        var notice = ConfigLifecycleNotice.Build(
            new ConfigSetupResult { ConfigCreated = true },
            migration,
            ConfigPath,
            suppressConfigCreatedNotice: true);

        notice.Should().NotBeNull();
        notice.Should().Contain("Upgraded configuration schema from v3 to v4.");
        notice.Should().Contain(@"C:\backup\config.v3.json");
        notice.Should().NotContain("Created your initial configuration file.");
    }

    [Fact]
    public void NothingToReportReturnsNull()
    {
        var notice = ConfigLifecycleNotice.Build(
            new ConfigSetupResult(),
            migrationResult: null,
            ConfigPath,
            suppressConfigCreatedNotice: false);

        notice.Should().BeNull();
    }

    [Fact]
    public void MigrationWithoutBackupOmitsBackupLine()
    {
        var migration = new ConfigMigrationResult(3, 4);
        migration.AddMigration("Added scheduled verification configuration defaults (disabled).");

        var notice = ConfigLifecycleNotice.Build(
            new ConfigSetupResult(),
            migration,
            ConfigPath,
            suppressConfigCreatedNotice: false);

        notice.Should().NotBeNull();
        notice.Should().NotContain("Backup:");
    }

    [Fact]
    public void MigrationResultWithNoAppliedStepsIsNotReported()
    {
        var notice = ConfigLifecycleNotice.Build(
            new ConfigSetupResult(),
            new ConfigMigrationResult(4, 4),
            ConfigPath,
            suppressConfigCreatedNotice: false);

        notice.Should().BeNull();
    }

    [Fact]
    public void ReportedNoticeEndsWithSettingsHint()
    {
        var notice = ConfigLifecycleNotice.Build(
            new ConfigSetupResult { ConfigCreated = true },
            migrationResult: null,
            ConfigPath,
            suppressConfigCreatedNotice: false);

        notice.Should().EndWith("Open Settings to review or adjust your configuration.");
    }
}
