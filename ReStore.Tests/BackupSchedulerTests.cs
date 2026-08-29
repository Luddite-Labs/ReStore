using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Drives cycles with an injected clock instead of the one-minute timer.</summary>
public class BackupSchedulerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _watchDir;
    private readonly TestLogger _logger = new();
    private readonly Mock<IConfigManager> _configMock = new();
    private readonly SystemState _state;
    private readonly SizeAnalyzer _sizeAnalyzer = new();
    private readonly Mock<IStorage> _storageMock = new();

    // Anchored to real time, then advanced per test: SystemState stamps history with the
    // real UtcNow, so an arbitrary fake date makes every backup look hours old.
    private DateTime _now = DateTime.UtcNow;

    public BackupSchedulerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreSchedulerTests_" + Guid.NewGuid().ToString("N"));
        _watchDir = Path.Combine(_testRoot, "watch");
        Directory.CreateDirectory(_watchDir);
        File.WriteAllText(Path.Combine(_watchDir, "seed.txt"), "seed");

        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_testRoot, "state.json"));

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = null }
        ]);
        _configMock.SetupGet(c => c.BackupInterval).Returns(TimeSpan.FromHours(1));
        _configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        _configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        _configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        _configMock.SetupGet(c => c.SizeThresholdMB).Returns(500);
        _configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        _configMock.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        _configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        _configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig());
        _configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        // Off by default; Windows-only paths are opted into per test.
        _configMock.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig { Enabled = false });
        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(_storageMock.Object);

        _storageMock.Setup(s => s.ExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    private BackupScheduler CreateScheduler() => new(
        _configMock.Object,
        _logger,
        _state,
        _sizeAnalyzer,
        passwordProvider: null,
        utcNow: () => _now);

    private string WatchGroupKey() => Path.GetFullPath(_watchDir);

    [Fact]
    public async Task RunDueBackupsAsync_ShouldBackUpDirectory_WhenNeverBackedUpBefore()
    {
        using var scheduler = CreateScheduler();

        await scheduler.RunDueBackupsAsync();

        _state.GetLastBackupTimeForGroup(WatchGroupKey())
            .Should().NotBe(DateTime.MinValue, "a never-backed-up directory is immediately due");
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldSkipDirectory_WhenIntervalHasNotElapsed()
    {
        using var scheduler = CreateScheduler();

        await scheduler.RunDueBackupsAsync();
        var firstBackup = _state.GetLastBackupTimeForGroup(WatchGroupKey());

        // Only half the configured hour has passed.
        _now = _now.AddMinutes(30);
        await scheduler.RunDueBackupsAsync();

        _state.GetLastBackupTimeForGroup(WatchGroupKey()).Should().Be(firstBackup);
        _state.BackupHistory[WatchGroupKey()].Should().HaveCount(1);
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldBackUpAgain_OnceIntervalHasElapsed()
    {
        using var scheduler = CreateScheduler();

        await scheduler.RunDueBackupsAsync();

        _now = _now.AddHours(2);
        File.WriteAllText(Path.Combine(_watchDir, "changed.txt"), "new content");
        await scheduler.RunDueBackupsAsync();

        _state.BackupHistory[WatchGroupKey()].Should().HaveCount(2);
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldNotThrow_WhenWatchDirectoryIsMissing()
    {
        var missing = Path.Combine(_testRoot, "does-not-exist");
        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = missing, StorageType = null }
        ]);

        using var scheduler = CreateScheduler();

        var act = async () => await scheduler.RunDueBackupsAsync();

        await act.Should().NotThrowAsync();
        _state.BackupHistory.Should().NotContainKey(Path.GetFullPath(missing));
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldKeepGoing_WhenOneDirectoryFails()
    {
        var healthy = Path.Combine(_testRoot, "healthy");
        Directory.CreateDirectory(healthy);
        File.WriteAllText(Path.Combine(healthy, "file.txt"), "content");

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = "broken" },
            new WatchDirectoryConfig { Path = healthy, StorageType = null }
        ]);
        _configMock.Setup(c => c.CreateStorageAsync("broken"))
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));

        using var scheduler = CreateScheduler();
        await scheduler.RunDueBackupsAsync();

        _logger.Messages.Should().Contain(m => m.Contains("provider unavailable"));
        _state.GetLastBackupTimeForGroup(Path.GetFullPath(healthy))
            .Should().NotBe(DateTime.MinValue, "a failure on one directory must not stop the others");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RunDueBackupsAsync_ShouldDoNothing_WhenIntervalIsNotPositive(int hours)
    {
        _configMock.SetupGet(c => c.BackupInterval).Returns(TimeSpan.FromHours(hours));

        using var scheduler = CreateScheduler();
        await scheduler.RunDueBackupsAsync();

        _state.BackupHistory.Should().BeEmpty("a non-positive interval disables scheduling");
    }

    [Fact]
    public async Task StartAsync_ShouldNotStart_WhenIntervalIsNotPositive()
    {
        _configMock.SetupGet(c => c.BackupInterval).Returns(TimeSpan.Zero);

        using var scheduler = CreateScheduler();
        await scheduler.StartAsync();

        scheduler.IsRunning.Should().BeFalse();
        _logger.Messages.Should().Contain(m => m.Contains("not started"));
    }

    [Fact]
    public async Task StartAsync_ShouldBeIdempotent()
    {
        using var scheduler = CreateScheduler();

        await scheduler.StartAsync();
        await scheduler.StartAsync();

        _logger.Messages.Should().Contain(m => m.Contains("already running"));

        await scheduler.StopAsync();
    }

    [Fact]
    public async Task StopAsync_ShouldStopTheLoop()
    {
        using var scheduler = CreateScheduler();

        await scheduler.StartAsync();
        await scheduler.StopAsync();

        scheduler.IsRunning.Should().BeFalse();
        _logger.Messages.Should().Contain(m => m.Contains("scheduler stopped"));
    }

    [Fact]
    public async Task StopAsync_ShouldBeSafe_WhenNeverStarted()
    {
        using var scheduler = CreateScheduler();

        var act = async () => await scheduler.StopAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldRaiseBackupCycleCompleted_WhenWorkWasDone()
    {
        using var scheduler = CreateScheduler();
        var raised = 0;
        scheduler.BackupCycleCompleted += () => raised++;

        await scheduler.RunDueBackupsAsync();
        raised.Should().Be(1);

        // Nothing due this time, so no notification.
        _now = _now.AddMinutes(1);
        await scheduler.RunDueBackupsAsync();
        raised.Should().Be(1);
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldNotResweepEveryTick_WhenNothingChanged()
    {
        // Backup records no history when nothing changed; without attempt-tracking such a
        // directory would stay permanently "due" and be rescanned every tick.
        using var scheduler = CreateScheduler();

        await scheduler.RunDueBackupsAsync();
        var historyAfterFirst = _state.BackupHistory[WatchGroupKey()].Count;

        var dueLogsBefore = _logger.Messages.Count(m => m.Contains("Scheduled backup due"));

        // Advance well past the interval, but change nothing on disk.
        _now = _now.AddHours(2);
        await scheduler.RunDueBackupsAsync();

        // Allowed to sweep once more, since the interval did elapse...
        var dueLogsAfter = _logger.Messages.Count(m => m.Contains("Scheduled backup due"));
        dueLogsAfter.Should().Be(dueLogsBefore + 1);

        // ...but an immediately following tick must not sweep again.
        _now = _now.AddMinutes(1);
        await scheduler.RunDueBackupsAsync();

        _logger.Messages.Count(m => m.Contains("Scheduled backup due")).Should().Be(dueLogsAfter);
        _state.BackupHistory[WatchGroupKey()].Count.Should().Be(historyAfterFirst,
            "no files changed, so no new snapshot should have been recorded");
    }

    [Fact]
    public async Task RunDueBackupsAsync_ShouldNotRetryTightly_WhenBackupThrows()
    {
        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = "broken" }
        ]);
        _configMock.Setup(c => c.CreateStorageAsync("broken"))
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));

        using var scheduler = CreateScheduler();

        await scheduler.RunDueBackupsAsync();
        var attempts = _logger.Messages.Count(m => m.Contains("provider unavailable"));

        // A failure must still mark the attempt so the next tick backs off.
        _now = _now.AddMinutes(1);
        await scheduler.RunDueBackupsAsync();

        _logger.Messages.Count(m => m.Contains("provider unavailable")).Should().Be(attempts);
    }

    [Fact]
    public async Task Dispose_ShouldNotThrow_WhileRunning()
    {
        var scheduler = CreateScheduler();
        await scheduler.StartAsync();

        var act = scheduler.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task StartAsync_ShouldThrow_AfterDispose()
    {
        var scheduler = CreateScheduler();
        scheduler.Dispose();

        var act = async () => await scheduler.StartAsync();

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }
}
