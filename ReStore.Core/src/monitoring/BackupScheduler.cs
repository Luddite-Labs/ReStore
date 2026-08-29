using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.monitoring;

/// <summary>
/// Runs periodic backups per <c>backupInterval</c> and <c>systemBackup.backupInterval</c>.
/// Complements <see cref="FileWatcher"/>, which only sees changes made while it runs;
/// this catches anything changed while the machine was asleep or the app was closed.
/// Due times come from the last recorded backup, so restarting doesn't reset the clock.
/// </summary>
/// <summary>Outcome of one scheduler sweep, for hosts that surface failures to the user.</summary>
public sealed class BackupCycleResult
{
    public int DirectoriesBackedUp { get; set; }
    public int DirectoriesFailed { get; set; }
    public bool SystemBackupRan { get; set; }
    public bool SystemBackupFailed { get; set; }
    public int SnapshotsVerified { get; set; }
    public int VerificationsFailed { get; set; }

    public List<string> FailureMessages { get; } = [];
    public List<string> VerificationFailureMessages { get; } = [];

    public bool DidWork => DirectoriesBackedUp > 0 || SystemBackupRan || SnapshotsVerified > 0;
    public bool HasFailures => DirectoriesFailed > 0 || SystemBackupFailed || VerificationsFailed > 0;
}

public class BackupScheduler : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    // A sub-minute sweep of every watched directory would keep the disk permanently busy.
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly IConfigManager _configManager;
    private readonly ILogger _logger;
    private readonly SystemState _systemState;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly IPasswordProvider? _passwordProvider;
    private readonly Func<DateTime> _utcNow;

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    // Backup records no history when nothing changed, which would leave such a directory
    // permanently "due" and rescanned every tick. History still drives decisions across
    // restarts; this only suppresses repeat work within one run.
    private readonly Dictionary<string, DateTime> _lastAttemptUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastVerifyAttemptUtc = new(StringComparer.OrdinalIgnoreCase);

    private Task? _loop;
    private bool _isDisposed;

    private DateTime _lastSystemBackupUtc = DateTime.MinValue;

    // Drives the "first success after a run of failures" notification.
    private bool _lastCycleHadFailures;

    /// <param name="utcNow">
    /// Test clock override. SystemState stamps history with the real UTC clock, so a
    /// substitute must stay anchored near real time for due-time comparisons to hold.
    /// </param>
    public BackupScheduler(
        IConfigManager configManager,
        ILogger logger,
        SystemState systemState,
        SizeAnalyzer sizeAnalyzer,
        IPasswordProvider? passwordProvider = null,
        Func<DateTime>? utcNow = null)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _systemState = systemState ?? throw new ArgumentNullException(nameof(systemState));
        _sizeAnalyzer = sizeAnalyzer ?? throw new ArgumentNullException(nameof(sizeAnalyzer));
        _passwordProvider = passwordProvider;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Raised after a sweep that did work, so a host can refresh its UI.</summary>
    public event Action? BackupCycleCompleted;

    /// <summary>
    /// Raised after every sweep that did work, carrying what succeeded and what failed.
    /// Fires alongside <see cref="BackupCycleCompleted"/>.
    /// </summary>
    public event Action<BackupCycleResult>? BackupCycleFinished;

    /// <summary>Raised on the first successful sweep after one or more failing sweeps.</summary>
    public event Action<BackupCycleResult>? BackupRecovered;

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (IsRunning)
        {
            _logger.Log("Backup scheduler is already running.", LogLevel.Warning);
            return Task.CompletedTask;
        }

        var interval = ResolveUserBackupInterval();
        if (interval == null)
        {
            _logger.Log(
                "Backup scheduler not started: backupInterval is not set to a usable value.",
                LogLevel.Warning);
            return Task.CompletedTask;
        }

        _logger.Log(
            $"Backup scheduler started. User-file interval: {interval}. " +
            $"System backup interval: {DescribeSystemBackupInterval()}. " +
            $"Verification interval: {DescribeVerificationInterval()}.",
            LogLevel.Info);

        _loop = RunLoopAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_loop == null)
        {
            return;
        }

        if (!_shutdown.IsCancellationRequested)
        {
            await _shutdown.CancelAsync();
        }

        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            _loop = null;
            _logger.Log("Backup scheduler stopped.", LogLevel.Info);
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        // So StartAsync returns without sweeping inline.
        await Task.Yield();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunDueBackupsAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log and retry next tick rather than killing the scheduler.
                    _logger.Log($"Scheduled backup cycle failed: {ex.Message}", LogLevel.Error);
                }

                await Task.Delay(TickInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    internal async Task<BackupCycleResult> RunDueBackupsAsync(CancellationToken cancellationToken = default)
    {
        var result = new BackupCycleResult();

        // A long backup must not be re-entered by the next tick.
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            _logger.Log("Scheduled backup skipped: a previous cycle is still running.", LogLevel.Debug);
            return result;
        }

        try
        {
            await RunDueUserBackupsAsync(result, cancellationToken);
            await RunDueSystemBackupAsync(result, cancellationToken);
            await RunDueVerificationAsync(result, cancellationToken);

            if (result.DidWork || result.HasFailures)
            {
                if (result.DidWork)
                {
                    BackupCycleCompleted?.Invoke();
                }

                BackupCycleFinished?.Invoke(result);

                if (_lastCycleHadFailures && !result.HasFailures)
                {
                    BackupRecovered?.Invoke(result);
                }

                _lastCycleHadFailures = result.HasFailures;
            }

            return result;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task RunDueUserBackupsAsync(BackupCycleResult result, CancellationToken cancellationToken)
    {
        var interval = ResolveUserBackupInterval();
        if (interval == null)
        {
            return;
        }

        var now = _utcNow();

        foreach (var watchDirectory in _configManager.WatchDirectories.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(watchDirectory.Path) || !Directory.Exists(watchDirectory.Path))
            {
                continue;
            }

            var group = NormalizeGroup(watchDirectory.Path);

            // Later of recorded backup and attempted sweep; see _lastAttemptUtc.
            var lastBackup = _systemState.GetLastBackupTimeForGroup(group);
            var lastActivity = _lastAttemptUtc.TryGetValue(group, out var lastAttempt) && lastAttempt > lastBackup
                ? lastAttempt
                : lastBackup;

            if (lastActivity != DateTime.MinValue && now - lastActivity < interval.Value)
            {
                continue;
            }

            var reason = lastActivity == DateTime.MinValue
                ? "never backed up"
                : $"last backup {FormatAge(now - lastActivity)} ago";

            _logger.Log($"Scheduled backup due for '{watchDirectory.Path}' ({reason}).", LogLevel.Info);

            // Before the attempt, so a throw can't cause a tight retry loop next tick.
            _lastAttemptUtc[group] = now;

            try
            {
                var backup = new Backup(_logger, _systemState, _sizeAnalyzer, _configManager, _passwordProvider);
                await backup.BackupDirectoryAsync(watchDirectory.Path, watchDirectory.StorageType, null, cancellationToken);
                result.DirectoriesBackedUp++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreachable provider should not stall the others.
                _logger.Log(
                    $"Scheduled backup failed for '{watchDirectory.Path}': {ex.Message}",
                    LogLevel.Error);

                result.DirectoriesFailed++;
                result.FailureMessages.Add($"{Path.GetFileName(watchDirectory.Path)}: {ex.Message}");
            }
        }
    }

    private async Task RunDueSystemBackupAsync(BackupCycleResult result, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var systemBackup = _configManager.SystemBackup;
        if (systemBackup is not { Enabled: true })
        {
            return;
        }

        if (!systemBackup.IncludePrograms
            && !systemBackup.IncludeEnvironmentVariables
            && !systemBackup.IncludeWindowsSettings)
        {
            return;
        }

        var interval = Normalize(systemBackup.BackupInterval);
        if (interval == null)
        {
            return;
        }

        var now = _utcNow();
        var lastRun = ResolveLastSystemBackupUtc();

        if (lastRun != DateTime.MinValue && now - lastRun < interval.Value)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var reason = lastRun == DateTime.MinValue
            ? "never backed up"
            : $"last backup {FormatAge(now - lastRun)} ago";

        _logger.Log($"Scheduled system backup due ({reason}).", LogLevel.Info);

        try
        {
            var manager = new SystemBackupManager(_logger, _configManager, _systemState, _passwordProvider);
            await manager.BackupSystemAsync();
            _lastSystemBackupUtc = now;
            result.SystemBackupRan = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"Scheduled system backup failed: {ex.Message}", LogLevel.Error);

            // Mark the attempt so a persistent failure doesn't retry every tick.
            _lastSystemBackupUtc = now;
            result.SystemBackupFailed = true;
            result.FailureMessages.Add($"System backup: {ex.Message}");
        }
    }

    /// <summary>
    /// Verifies snapshots per group on the configured interval. Opt-in, and local-only by
    /// default, because each run re-downloads every unique chunk. The newest snapshot is
    /// always checked; any remaining per-run budget rotates through older snapshots so an
    /// older restore point is eventually covered rather than never looked at.
    /// </summary>
    private async Task RunDueVerificationAsync(BackupCycleResult result, CancellationToken cancellationToken)
    {
        var verification = _configManager.Verification;
        if (verification is not { Enabled: true })
        {
            return;
        }

        var interval = Normalize(verification.VerifyInterval);
        if (interval == null)
        {
            return;
        }

        var now = _utcNow();

        foreach (var group in _systemState.GetBackupGroups())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshots = _systemState.GetBackupsForGroup(group)
                .Where(backup => backup.ArtifactType == BackupArtifactType.SnapshotManifest
                    && !string.IsNullOrWhiteSpace(backup.Path))
                .OrderByDescending(backup => backup.Timestamp)
                .ToList();

            if (snapshots.Count == 0)
            {
                continue;
            }

            // Same back-off shape as _lastAttemptUtc: a persistently failing verify must
            // not re-run every tick.
            if (_lastVerifyAttemptUtc.TryGetValue(group, out var lastAttempt)
                && now - lastAttempt < interval.Value)
            {
                continue;
            }

            var selected = SelectSnapshotsToVerify(snapshots, verification.SnapshotsPerRun);
            if (selected.Count == 0)
            {
                continue;
            }

            var attemptedAny = false;

            foreach (var snapshot in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var storageType = string.IsNullOrWhiteSpace(snapshot.StorageType)
                    ? _configManager.GlobalStorageType
                    : snapshot.StorageType;

                if (verification.LocalStorageOnly
                    && !string.Equals(storageType, "local", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                attemptedAny = true;
                _systemState.RecordSnapshotVerified(snapshot.Path, now);

                _logger.Log($"Scheduled verification due for '{group}' (snapshot {snapshot.SnapshotId}).", LogLevel.Info);

                try
                {
                    using var storage = await _configManager.CreateStorageAsync(storageType);
                    var verifier = new SnapshotIntegrityVerifier(_logger, storage, _passwordProvider, _systemState);
                    var verificationResult = await verifier.VerifyAsync(snapshot.Path, null, cancellationToken);

                    if (verificationResult.IsValid)
                    {
                        result.SnapshotsVerified++;
                        _logger.Log(
                            $"Scheduled verification passed for '{group}' (snapshot {verificationResult.SnapshotId}).",
                            LogLevel.Info);
                    }
                    else
                    {
                        result.VerificationsFailed++;
                        var summary = $"{Path.GetFileName(group)}: {verificationResult.Errors.Count} integrity issue(s)";
                        result.VerificationFailureMessages.Add(summary);

                        _logger.Log(
                            $"Scheduled verification FAILED for '{group}' with {verificationResult.Errors.Count} issue(s).",
                            LogLevel.Error);

                        foreach (var error in verificationResult.Errors.Take(10))
                        {
                            _logger.Log($"Verify Error: {error}", LogLevel.Error);
                        }
                    }

                    await PersistVerificationStateAsync();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Log($"Scheduled verification failed for '{group}': {ex.Message}", LogLevel.Error);
                    result.VerificationsFailed++;
                    result.VerificationFailureMessages.Add($"{Path.GetFileName(group)}: {ex.Message}");

                    // Persist on the failure path too: a failed attempt still consumed its
                    // turn, so without this an unreachable provider would pin the rotation to
                    // the same snapshot across restarts and never reach older ones.
                    await PersistVerificationStateAsync();
                }
            }
            // Only start the group's back-off once something was actually attempted, so a
            // group whose snapshots were all skipped (remote, under local-only) stays due.
            if (attemptedAny)
            {
                _lastVerifyAttemptUtc[group] = now;
            }
        }
    }

    /// <summary>
    /// Persists verification bookkeeping (telemetry and the rotation marker). A failure to
    /// write state must not abort the remaining groups in the sweep.
    /// </summary>
    private async Task PersistVerificationStateAsync()
    {
        try
        {
            await _systemState.SaveStateAsync();
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to persist verification state: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// Newest snapshot first, then the least-recently-verified older ones to fill the budget.
    /// </summary>
    private List<BackupInfo> SelectSnapshotsToVerify(List<BackupInfo> newestFirst, int budget)
    {
        var limit = Math.Max(1, budget);
        var selected = new List<BackupInfo> { newestFirst[0] };

        if (limit == 1 || newestFirst.Count == 1)
        {
            return selected;
        }

        // Never-verified snapshots sort first, since GetSnapshotVerifiedUtc returns MinValue.
        var rotation = newestFirst
            .Skip(1)
            .OrderBy(snapshot => _systemState.GetSnapshotVerifiedUtc(snapshot.Path))
            .ThenByDescending(snapshot => snapshot.Timestamp)
            .Take(limit - 1);

        selected.AddRange(rotation);
        return selected;
    }

    // Newest across the three groups, plus the in-memory marker so a failed run counts.
    private DateTime ResolveLastSystemBackupUtc()
    {
        var candidates = new[]
        {
            _systemState.GetLastBackupTimeForGroup("system_programs"),
            _systemState.GetLastBackupTimeForGroup("system_environment"),
            _systemState.GetLastBackupTimeForGroup("system_settings"),
            _lastSystemBackupUtc
        };

        return candidates.Max();
    }

    private TimeSpan? ResolveUserBackupInterval() => Normalize(_configManager.BackupInterval);

    /// <summary>Clamped interval, or null when the value means "do not schedule".</summary>
    private TimeSpan? Normalize(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return null;
        }

        return interval < MinimumInterval ? MinimumInterval : interval;
    }

    private string DescribeSystemBackupInterval()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "not applicable (non-Windows)";
        }

        if (_configManager.SystemBackup is not { Enabled: true })
        {
            return "disabled";
        }

        var interval = Normalize(_configManager.SystemBackup.BackupInterval);
        return interval?.ToString() ?? "disabled";
    }

    private string DescribeVerificationInterval()
    {
        if (_configManager.Verification is not { Enabled: true })
        {
            return "disabled";
        }

        var interval = Normalize(_configManager.Verification.VerifyInterval);
        if (interval == null)
        {
            return "disabled";
        }

        return _configManager.Verification.LocalStorageOnly
            ? $"{interval} (local storage only)"
            : interval.ToString()!;
    }

    private static string NormalizeGroup(string path)
    {
        // Must match the key Backup records history under, or the scheduler never sees
        // its own backups and re-runs every tick.
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return path;
        }
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1)
        {
            return "less than a minute";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m";
        }

        return age.TotalDays < 1
            ? $"{(int)age.TotalHours}h"
            : $"{(int)age.TotalDays}d";
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        // Don't block Dispose on an in-flight backup; cancellation stops the loop.
        _shutdown.Dispose();
        _runLock.Dispose();
        _loop = null;
    }
}
