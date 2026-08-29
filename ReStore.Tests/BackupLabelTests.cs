using FluentAssertions;
using ReStore.Core.src.core;

namespace ReStore.Tests;

/// <summary>Covers restore-point labelling and its persistence across save/load.</summary>
public class BackupLabelTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger = new();

    public BackupLabelTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreLabelTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    private SystemState CreateState()
    {
        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_testRoot, $"state-{Guid.NewGuid():N}.json"));
        return state;
    }

    [Fact]
    public void SetBackupLabel_ShouldTagTheMatchingBackup()
    {
        var state = CreateState();
        state.AddSnapshotBackup("C:/Data", "snap-1", "snapshots/data/snap-1.manifest.json", "local", ["aa11"]);

        var applied = state.SetBackupLabel("C:/Data", "snapshots/data/snap-1.manifest.json", "before Windows reinstall");

        applied.Should().BeTrue();
        state.GetBackupsForGroup("C:/Data")[0].Label.Should().Be("before Windows reinstall");
    }

    [Fact]
    public void SetBackupLabel_ShouldReturnFalse_ForUnknownGroupOrPath()
    {
        var state = CreateState();
        state.AddSnapshotBackup("C:/Data", "snap-1", "snapshots/data/snap-1.manifest.json", "local", ["aa11"]);

        state.SetBackupLabel("C:/Missing", "snapshots/data/snap-1.manifest.json", "x").Should().BeFalse();
        state.SetBackupLabel("C:/Data", "snapshots/data/other.manifest.json", "x").Should().BeFalse();
    }

    [Fact]
    public void SetBackupLabel_ShouldClearLabel_WhenGivenBlank()
    {
        var state = CreateState();
        state.AddSnapshotBackup("C:/Data", "snap-1", "snapshots/data/snap-1.manifest.json", "local", ["aa11"]);
        state.SetBackupLabel("C:/Data", "snapshots/data/snap-1.manifest.json", "temporary");

        state.SetBackupLabel("C:/Data", "snapshots/data/snap-1.manifest.json", "   ").Should().BeTrue();

        state.GetBackupsForGroup("C:/Data")[0].Label.Should().BeNull();
    }

    [Fact]
    public async Task BackupLabel_ShouldSurviveSaveAndLoad()
    {
        var statePath = Path.Combine(_testRoot, "roundtrip-state.json");

        var original = new SystemState(_logger);
        original.SetStateFilePath(statePath);
        original.AddSnapshotBackup("C:/Data", "snap-1", "snapshots/data/snap-1.manifest.json", "local", ["aa11"]);
        original.SetBackupLabel("C:/Data", "snapshots/data/snap-1.manifest.json", "quarterly archive");
        await original.SaveStateAsync();

        var reloaded = new SystemState(_logger);
        reloaded.SetStateFilePath(statePath);
        await reloaded.LoadStateAsync();

        reloaded.GetBackupsForGroup("C:/Data")[0].Label.Should().Be("quarterly archive");
    }

    [Fact]
    public void SetBackupLabel_ShouldOnlyAffectTheTargetedSnapshot()
    {
        var state = CreateState();
        state.AddSnapshotBackup("C:/Data", "snap-1", "snapshots/data/snap-1.manifest.json", "local", ["aa11"]);
        state.AddSnapshotBackup("C:/Data", "snap-2", "snapshots/data/snap-2.manifest.json", "local", ["bb22"]);

        state.SetBackupLabel("C:/Data", "snapshots/data/snap-2.manifest.json", "tagged");

        var backups = state.GetBackupsForGroup("C:/Data");
        backups.Single(b => b.Path.EndsWith("snap-2.manifest.json")).Label.Should().Be("tagged");
        backups.Single(b => b.Path.EndsWith("snap-1.manifest.json")).Label.Should().BeNull();
    }
}
