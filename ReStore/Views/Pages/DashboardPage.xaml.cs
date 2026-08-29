using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ReStore.Core.src.utils;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.backup;
using ReStore.Services;
using ReStore.Views.Windows;

namespace ReStore.Views.Pages
{
    public class BackupHistoryItem
    {
        public string Directory { get; set; } = "";
        public string Path { get; set; } = "";
        public string StorageType { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool IsDiff { get; set; }
        public bool CanRestore => IsSnapshotArtifactPath(Path);
        public string TypeLabel => CanRestore ? "Snapshot" : IsDiff ? "Differential" : "Full";

        private static bool IsSnapshotArtifactPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("\\HEAD", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>One watched directory's backup freshness, for the health panel.</summary>
    public class DirectoryHealthItem
    {
        private static readonly Brush FreshBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        private static readonly Brush AgeingBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        private static readonly Brush StaleBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));

        public string DirectoryName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string? RestorePath { get; set; }
        public string StorageType { get; set; } = "";
        public DateTime? LastBackupUtc { get; set; }

        public bool CanRestore => !string.IsNullOrWhiteSpace(RestorePath);

        public string AgeLabel
        {
            get
            {
                if (LastBackupUtc == null)
                {
                    return "Never backed up";
                }

                var age = DateTime.UtcNow - LastBackupUtc.Value;
                if (age < TimeSpan.FromMinutes(1)) return "Just now";
                if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
                if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
                return $"{(int)age.TotalDays}d ago";
            }
        }

        /// <summary>Green under a day, amber under a week, red beyond — or never backed up.</summary>
        public Brush StalenessColor
        {
            get
            {
                if (LastBackupUtc == null)
                {
                    return StaleBrush;
                }

                var age = DateTime.UtcNow - LastBackupUtc.Value;
                if (age < TimeSpan.FromDays(1)) return FreshBrush;
                return age < TimeSpan.FromDays(7) ? AgeingBrush : StaleBrush;
            }
        }
    }

    public partial class DashboardPage : Page, ILogger
    {
        // The activity pane is a tail view, not an archive; Logger keeps the full log on disk.
        private const int MaxLogBufferChars = 256 * 1024;
        private const int KeepLogBufferChars = 128 * 1024;

        private readonly Logger _fileLogger = AppServices.Logger;
        private ConfigManager _configManager = null!;
        private SystemState? _state;
        private FileWatcher? _watcher;
        private BackupScheduler? _scheduler;
        private readonly StringBuilder _logBuffer = new();
        private readonly ObservableCollection<BackupHistoryItem> _backupHistory = new();
        private readonly ObservableCollection<DirectoryHealthItem> _directoryHealth = new();
        private readonly ObservableCollection<string> _needsAttention = new();
        private readonly BackupNotificationService _notifications = new(AppServices.Logger);
        private System.Windows.Threading.DispatcherTimer? _statsTimer;
        private bool _initialized;

        public DashboardPage()
        {
            InitializeComponent();

            ValidateBtn.Click += (_, __) => ValidateConfig();
            StartWatcherBtn.Click += async (_, __) => await StartWatcherAsync();
            StopWatcherBtn.Click += (_, __) => StopWatcher();
            ManualBackupBtn.Click += async (_, __) => await ManualBackupAsync();
            RestoreBtn.Click += async (_, __) => await RestoreBackupAsync();
            SystemBackupBtn.Click += async (_, __) => await SystemBackupAsync();
            RefreshHistoryBtn.Click += async (_, __) => await RefreshBackupHistoryAsync();
            ClearLogsBtn.Click += (_, __) => ClearLogs();

            BackupHistoryList.ItemsSource = _backupHistory;
            DirectoryHealthList.ItemsSource = _directoryHealth;
            NeedsAttentionList.ItemsSource = _needsAttention;

            if (OperatingSystem.IsWindows())
            {
                SystemBackupBtn.Visibility = Visibility.Visible;
            }
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            var storyboard = new System.Windows.Media.Animation.Storyboard();

            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Children.Add(fadeIn);
            storyboard.Begin(this);

            if (!_initialized)
            {
                _initialized = true;
                await InitializeAsync();
            }

            StartStatsTimer();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _statsTimer?.Stop();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _configManager = await AppServices.GetConfigManagerAsync();
                _state = await AppServices.GetSystemStateAsync();

                var configPath = _configManager.GetConfigFilePath();
                StatusText.Text = $"Config loaded: {configPath}";
                UpdateStatistics();
                await RefreshBackupHistoryAsync();

                await OfferFirstRunSetupIfNeededAsync();
            }
            catch (Exception ex)
            {
                Log($"Failed to load config: {ex.Message}", LogLevel.Error);
                StatusText.Text = "Configuration error - see logs";

                MessageBox.Show(
                    $"ReStore could not load its configuration:\n\n{ex.Message}\n\nUse the Settings page to correct it.",
                    "Configuration Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Runs the setup wizard when the config still looks untouched. Config auto-creation
        /// means a file always exists, so "first run" is inferred from it having no usable
        /// watch directories rather than from the file being absent.
        /// </summary>
        private async Task OfferFirstRunSetupIfNeededAsync()
        {
            if (!NeedsFirstRunSetup())
            {
                return;
            }

            var wizard = new FirstRunWizardWindow(_configManager)
            {
                Owner = Window.GetWindow(this)
            };

            wizard.ShowDialog();

            if (!wizard.SetupCompleted)
            {
                Log("First-run setup skipped. Configure ReStore on the Settings page.", LogLevel.Warning);
                StatusText.Text = "Setup skipped - configure on the Settings page";
                return;
            }

            Log("First-run setup completed.", LogLevel.Info);
            StatusText.Text = "Setup complete";
            UpdateStatistics();

            foreach (var folder in wizard.FoldersToBackUpNow)
            {
                await RunFirstBackupAsync(folder);
            }

            await RefreshBackupHistoryAsync();
            UpdateStatistics();
        }

        private bool NeedsFirstRunSetup()
        {
            if (_configManager == null || _state == null)
            {
                return false;
            }

            return FirstRunDetector.NeedsSetup(
                _configManager.StorageSources,
                LocalBackupDirectoryExists(),
                _state.GetTotalBackupCount());
        }

        /// <summary>
        /// The local provider needs no credentials, so an existing backup directory is the only
        /// evidence that it was ever actually used.
        /// </summary>
        private bool LocalBackupDirectoryExists()
        {
            if (_configManager == null
                || !_configManager.StorageSources.TryGetValue("local", out var localConfig)
                || string.IsNullOrWhiteSpace(localConfig.Path))
            {
                return false;
            }

            try
            {
                return System.IO.Directory.Exists(Environment.ExpandEnvironmentVariables(localConfig.Path));
            }
            catch (Exception ex)
            {
                Log($"Could not check local backup directory: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }

        private async Task RunFirstBackupAsync(string folder)
        {
            var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();
            passwordProvider.SetEncryptionMode(true);

            var progressWindow = new OperationProgressWindow(
                $"Backing up {System.IO.Path.GetFileName(folder)}",
                _fileLogger);

            progressWindow.RunModal(Window.GetWindow(this), async token =>
            {
                var backup = new Backup(progressWindow, _state!, new SizeAnalyzer(), _configManager, passwordProvider);
                await backup.BackupDirectoryAsync(folder, null, progressWindow.BackupProgress, token);
            });

            if (progressWindow.Succeeded)
            {
                Log($"First backup completed: {folder}", LogLevel.Info);
            }
            else if (progressWindow.Failure != null)
            {
                Log($"First backup failed for {folder}: {progressWindow.Failure.Message}", LogLevel.Error);
            }
        }

        private void StartStatsTimer()
        {
            if (_statsTimer != null)
            {
                _statsTimer.Start();
                return;
            }

            _statsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _statsTimer.Tick += (_, __) => UpdateStatistics();
            _statsTimer.Start();
        }

        private void UpdateStatistics()
        {
            if (_state != null)
            {
                // Locked accessor: the scheduler and watcher append to BackupHistory on their
                // own threads, so enumerating it directly is not safe.
                TotalBackupsText.Text = _state.GetTotalBackupCount().ToString();

                if (_state.LastBackupTime != DateTime.MinValue)
                {
                    var timeSince = DateTime.UtcNow - _state.LastBackupTime;
                    if (timeSince.TotalMinutes < 1)
                        LastBackupText.Text = "Just now";
                    else if (timeSince.TotalHours < 1)
                        LastBackupText.Text = $"{(int)timeSince.TotalMinutes}m ago";
                    else if (timeSince.TotalDays < 1)
                        LastBackupText.Text = $"{(int)timeSince.TotalHours}h ago";
                    else
                        LastBackupText.Text = _state.LastBackupTime.ToLocalTime().ToString("MMM dd, HH:mm");
                }
                else
                {
                    LastBackupText.Text = "Never";
                }
            }

            WatchedDirsText.Text = _configManager.WatchDirectories.Count.ToString();

            // Read from the already-loaded config rather than re-reading AppSettings from
            // disk; this runs on a 5-second timer.
            var defaultStorage = _configManager.GlobalStorageType;
            if (!string.IsNullOrEmpty(defaultStorage))
            {
                StorageInfoText.Text = $"Storage: {defaultStorage}";
            }

            UpdateHealthPanel();
        }

        /// <summary>
        /// Surfaces <see cref="SystemState.Telemetry"/>, which is persisted on every operation
        /// but was otherwise never shown anywhere in the app.
        /// </summary>
        private void UpdateHealthPanel()
        {
            if (_state == null || _configManager == null)
            {
                return;
            }

            var telemetry = _state.Telemetry;

            var backup = telemetry.Backup;
            if (backup.SnapshotCount > 0)
            {
                // Exact, not estimated: the difference between the stored bytes every
                // snapshot referenced and the bytes actually transferred.
                var savedBytes = backup.DedupSavedBytes;

                DedupSavingsText.Text = savedBytes > 0 ? ByteFormatter.Format(savedBytes) : "—";
                DedupDetailText.Text =
                    $"{backup.UniqueReusedChunks:N0} of {backup.UniqueChunks:N0} chunks reused across {backup.SnapshotCount:N0} snapshot(s)";
            }
            else
            {
                DedupSavingsText.Text = "—";
                DedupDetailText.Text = "No snapshots recorded yet";
            }

            var verification = telemetry.Verification;
            if (verification.RunCount > 0)
            {
                var passRate = (double)verification.SuccessCount / verification.RunCount;
                VerificationText.Text = $"{passRate:P0} passing";
                VerificationDetailText.Text =
                    $"{verification.SuccessCount:N0}/{verification.RunCount:N0} runs, last {FormatAge(verification.LastUpdatedUtc)}";
                VerificationText.Foreground = verification.SuccessCount == verification.RunCount
                    ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                    : new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
            else
            {
                VerificationText.Text = "Never run";
                VerificationDetailText.Text = _configManager.Verification.Enabled
                    ? "Scheduled verification is on; awaiting first run"
                    : "Scheduled verification is off";
            }

            var restore = telemetry.Restore;
            if (restore.AttemptCount > 0)
            {
                var successRate = (double)restore.SuccessCount / restore.AttemptCount;
                RestoreReliabilityText.Text = $"{successRate:P0} succeeded";
                RestoreReliabilityDetailText.Text =
                    $"{restore.SuccessCount:N0}/{restore.AttemptCount:N0} attempts, last {FormatAge(restore.LastUpdatedUtc)}";
            }
            else
            {
                RestoreReliabilityText.Text = "No attempts";
                RestoreReliabilityDetailText.Text = "Restores have not been exercised";
            }

            UpdateDirectoryHealth();
            UpdateNeedsAttention();
        }

        private void UpdateDirectoryHealth()
        {
            var items = new List<DirectoryHealthItem>();

            foreach (var watchDirectory in _configManager.WatchDirectories)
            {
                if (string.IsNullOrWhiteSpace(watchDirectory.Path))
                {
                    continue;
                }

                string group;
                try
                {
                    group = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(watchDirectory.Path));
                }
                catch
                {
                    group = watchDirectory.Path;
                }

                var lastBackup = _state!.GetLastBackupTimeForGroup(group);
                var newestSnapshot = _state.GetLatestSnapshotForGroup(group);

                items.Add(new DirectoryHealthItem
                {
                    DirectoryName = System.IO.Path.GetFileName(group.TrimEnd(
                        System.IO.Path.DirectorySeparatorChar,
                        System.IO.Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : group,
                    FullPath = group,
                    RestorePath = newestSnapshot?.Path,
                    StorageType = newestSnapshot?.StorageType ?? watchDirectory.StorageType ?? _configManager.GlobalStorageType,
                    LastBackupUtc = lastBackup == DateTime.MinValue ? null : lastBackup
                });
            }

            _directoryHealth.Clear();
            foreach (var item in items.OrderBy(item => item.LastBackupUtc ?? DateTime.MinValue))
            {
                _directoryHealth.Add(item);
            }

            var staleCount = items.Count(item =>
                item.LastBackupUtc == null || DateTime.UtcNow - item.LastBackupUtc.Value > TimeSpan.FromDays(7));

            HealthSummaryText.Text = staleCount == 0
                ? $"{items.Count} watched folder(s), all current"
                : $"{staleCount} of {items.Count} watched folder(s) need attention";
        }

        private void UpdateNeedsAttention()
        {
            _needsAttention.Clear();

            var telemetry = _state!.Telemetry;

            foreach (var (category, count) in telemetry.Restore.FailureCategoryCounts.OrderByDescending(entry => entry.Value))
            {
                _needsAttention.Add($"Restore failures — {DescribeFailureCategory(category)}: {count:N0}");
            }

            var verification = telemetry.Verification;
            if (verification.MissingChunks > 0)
            {
                _needsAttention.Add($"Verification found {verification.MissingChunks:N0} missing chunk object(s) — affected snapshots may not restore.");
            }

            if (verification.InvalidChunks > 0)
            {
                _needsAttention.Add($"Verification found {verification.InvalidChunks:N0} corrupted chunk(s).");
            }

            if (verification.InvalidFiles > 0)
            {
                _needsAttention.Add($"Verification could not reconstruct {verification.InvalidFiles:N0} file(s).");
            }

            if (!_configManager.Retention.Enabled)
            {
                _needsAttention.Add("Retention is off — old snapshots are kept forever and storage will grow without bound.");
            }

            foreach (var stale in _directoryHealth.Where(item => item.LastBackupUtc == null))
            {
                _needsAttention.Add($"'{stale.DirectoryName}' has never been backed up.");
            }

            NeedsAttentionPanel.Visibility = _needsAttention.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string DescribeFailureCategory(string category) => category switch
        {
            "missing-artifact" => "missing snapshot artifact",
            "decryption-integrity-failure" => "wrong password or tampered data",
            "manifest-integrity-failure" => "manifest integrity",
            "chunk-validation-failure" => "corrupted chunk",
            "file-validation-failure" => "restored file mismatch",
            "restore-conflict" => "destination conflict",
            _ => category
        };

        private static string FormatAge(DateTime utcTimestamp)
        {
            if (utcTimestamp == DateTime.MinValue)
            {
                return "never";
            }

            var age = DateTime.UtcNow - utcTimestamp;
            if (age < TimeSpan.FromMinutes(1)) return "just now";
            if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
            if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
            return $"{(int)age.TotalDays}d ago";
        }

        private async void HealthRestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not DirectoryHealthItem health)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(health.RestorePath))
            {
                MessageBox.Show("This folder has no snapshot to restore yet.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await RestoreWithPreviewAsync(new BackupHistoryItem
            {
                Directory = health.DirectoryName,
                Path = health.RestorePath,
                StorageType = health.StorageType,
                Timestamp = health.LastBackupUtc ?? DateTime.UtcNow
            });
        }

        private async Task RefreshBackupHistoryAsync()
        {
            if (_state == null) return;

            await Task.Run(() =>
            {
                var items = new List<BackupHistoryItem>();
                foreach (var kvp in _state.GetBackupHistorySnapshot())
                {
                    if (IsSystemBackupGroup(kvp.Key))
                    {
                        continue;
                    }

                    var directory = kvp.Key;
                    foreach (var backup in kvp.Value.OrderByDescending(b => b.Timestamp).Take(10))
                    {
                        if (backup.ArtifactType != BackupArtifactType.SnapshotManifest
                            && !backup.Path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)
                            && !backup.Path.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)
                            && !backup.Path.EndsWith("\\HEAD", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var storageType = string.IsNullOrWhiteSpace(backup.StorageType)
                            ? _configManager.GlobalStorageType
                            : backup.StorageType;

                        items.Add(new BackupHistoryItem
                        {
                            Directory = System.IO.Path.GetFileName(directory),
                            Path = backup.Path,
                            StorageType = storageType ?? _configManager.GlobalStorageType,
                            Timestamp = backup.Timestamp,
                            IsDiff = backup.IsDiff
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    _backupHistory.Clear();
                    foreach (var item in items.OrderByDescending(i => i.Timestamp).Take(20))
                    {
                        _backupHistory.Add(item);
                    }
                });
            });
        }

        private void ClearLogs()
        {
            _logBuffer.Clear();
            LogBox.Text = string.Empty;
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            _fileLogger.Log(message, level);
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            Dispatcher.Invoke(() =>
            {
                // Append rather than reassigning Text, which would rebuild the whole string
                // per message, and keep only a bounded tail so a long run cannot grow without
                // limit.
                _logBuffer.AppendLine(line);
                TrimLogBufferIfNeeded();
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
            });
        }

        /// <summary>Keeps the visible log to a bounded tail; the full log is on disk.</summary>
        private void TrimLogBufferIfNeeded()
        {
            if (_logBuffer.Length <= MaxLogBufferChars)
            {
                return;
            }

            var retained = _logBuffer.ToString(_logBuffer.Length - KeepLogBufferChars, KeepLogBufferChars);
            var firstLineBreak = retained.IndexOf('\n');
            if (firstLineBreak >= 0 && firstLineBreak + 1 < retained.Length)
            {
                retained = retained[(firstLineBreak + 1)..];
            }

            _logBuffer.Clear();
            _logBuffer.Append(retained);
            LogBox.Text = retained;
        }

        private void ValidateConfig()
        {
            try
            {
                var result = _configManager.ValidateConfiguration();
                if (!result.IsValid)
                {
                    foreach (var e in result.Errors) Log($"Config Error: {e}", LogLevel.Error);
                }
                foreach (var w in result.Warnings) Log($"Config Warning: {w}", LogLevel.Warning);
                foreach (var i in result.Info) Log($"Config Info: {i}", LogLevel.Info);
                StatusText.Text = result.IsValid ? "Configuration is valid" : "Configuration has errors";
            }
            catch (Exception ex)
            {
                Log($"Validation error: {ex.Message}", LogLevel.Error);
            }
        }

        public async Task StartWatcherAsync()
        {
            try
            {
                if (_watcher != null)
                {
                    Log("Watcher already running", LogLevel.Warning);
                    return;
                }

                var result = _configManager.ValidateConfiguration();
                if (!result.IsValid)
                {
                    Log("Cannot start watcher due to config errors", LogLevel.Error);
                    return;
                }

                _state ??= await AppServices.GetSystemStateAsync();

                var sizeAnalyzer = new SizeAnalyzer();
                var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();
                passwordProvider.SetEncryptionMode(true);
                _watcher = new FileWatcher(_configManager, this, _state, sizeAnalyzer, passwordProvider);
                await _watcher.StartAsync();

                WatcherService.Instance.SetWatcher(_watcher);

                // Runs alongside the watcher so the configured backupInterval is honoured
                // for changes that happened while the app was closed.
                _scheduler = new BackupScheduler(_configManager, this, _state, sizeAnalyzer, passwordProvider);
                _scheduler.BackupCycleCompleted += OnScheduledBackupCompleted;

                _notifications.Configure(
                    Application.Current.MainWindow is Views.MainWindow mainWindow ? mainWindow.TrayManager : null,
                    _configManager.Notifications);

                _scheduler.BackupCycleFinished += OnScheduledCycleFinished;
                _scheduler.BackupRecovered += OnScheduledBackupRecovered;

                await _scheduler.StartAsync();

                StatusText.Text = "Watcher running";
                WatcherStatusText.Text = "Running";
                UpdateStatusIndicator(true);
                Log("File watcher started", LogLevel.Info);
                UpdateStatistics();
            }
            catch (Exception ex)
            {
                Log($"Failed to start watcher: {ex.Message}", LogLevel.Error);
                StopWatcher();
            }
        }

        private void OnScheduledBackupCompleted()
        {
            // Raised from the scheduler's loop thread; marshal to the UI thread.
            _ = Dispatcher.InvokeAsync(async () =>
            {
                UpdateStatistics();
                await RefreshBackupHistoryAsync();
            });
        }

        private void OnScheduledCycleFinished(BackupCycleResult result)
        {
            _ = Dispatcher.InvokeAsync(() => _notifications.NotifyCycleFinished(result));
        }

        private void OnScheduledBackupRecovered(BackupCycleResult result)
        {
            _ = Dispatcher.InvokeAsync(() => _notifications.NotifyRecovered(result));
        }

        public void StopWatcher()
        {
            try
            {
                if (_scheduler != null)
                {
                    _scheduler.BackupCycleCompleted -= OnScheduledBackupCompleted;
                    _scheduler.BackupCycleFinished -= OnScheduledCycleFinished;
                    _scheduler.BackupRecovered -= OnScheduledBackupRecovered;
                    _scheduler.Dispose();
                    _scheduler = null;
                }

                _watcher?.Dispose();
                _watcher = null;

                WatcherService.Instance.SetWatcher(null);

                StatusText.Text = "Watcher stopped";
                WatcherStatusText.Text = "Stopped";
                UpdateStatusIndicator(false);
                Log("File watcher stopped", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log($"Error stopping watcher: {ex.Message}", LogLevel.Warning);
            }
        }

        private async Task ManualBackupAsync()
        {
            try
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "Select folder to backup",
                    Multiselect = false
                };

                if (dialog.ShowDialog() == true)
                {
                    var folderPath = dialog.FolderName;
                    if (string.IsNullOrEmpty(folderPath))
                    {
                        Log("Invalid folder selected", LogLevel.Error);
                        return;
                    }

                    Log($"Starting manual backup of: {folderPath}", LogLevel.Info);

                    _state ??= await AppServices.GetSystemStateAsync();

                    var sizeAnalyzer = new SizeAnalyzer();
                    var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();
                    passwordProvider.SetEncryptionMode(true);

                    var progressWindow = new OperationProgressWindow(
                        $"Backing up {System.IO.Path.GetFileName(folderPath)}",
                        _fileLogger);

                    progressWindow.RunModal(Window.GetWindow(this), async token =>
                    {
                        var backup = new Backup(progressWindow, _state, sizeAnalyzer, _configManager, passwordProvider);
                        await backup.BackupDirectoryAsync(folderPath, null, progressWindow.BackupProgress, token);
                    });

                    if (progressWindow.Succeeded)
                    {
                        Log($"Manual backup completed: {folderPath}", LogLevel.Info);
                    }
                    else if (progressWindow.WasCancelled)
                    {
                        Log($"Manual backup cancelled: {folderPath}", LogLevel.Warning);
                    }
                    else if (progressWindow.Failure != null)
                    {
                        Log($"Manual backup failed: {progressWindow.Failure.Message}", LogLevel.Error);
                        MessageBox.Show($"Backup failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    await RefreshBackupHistoryAsync();
                    UpdateStatistics();
                }
            }
            catch (Exception ex)
            {
                Log($"Manual backup failed: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task RestoreBackupAsync()
        {
            if (_backupHistory.Count == 0)
            {
                MessageBox.Show("No backups available to restore.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var latestBackup = _backupHistory.FirstOrDefault(item => item.CanRestore);
            if (latestBackup == null)
            {
                Log("No restorable snapshot found in history", LogLevel.Warning);
                return;
            }

            await RestoreWithPreviewAsync(latestBackup);
        }

        private async void RestoreBackupBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupHistoryItem backup)
            {
                await RestoreWithPreviewAsync(backup);
            }
        }

        private async Task RestoreWithPreviewAsync(BackupHistoryItem backup)
        {
            try
            {
                if (!backup.CanRestore)
                {
                    MessageBox.Show("This history item is not a snapshot artifact and cannot be restored here.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var dialog = new OpenFolderDialog
                {
                    Title = "Select restore destination folder",
                    Multiselect = false
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var targetPath = dialog.FolderName;
                if (string.IsNullOrEmpty(targetPath))
                {
                    Log("Invalid restore destination", LogLevel.Error);
                    return;
                }

                _state ??= await AppServices.GetSystemStateAsync();

                var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();
                passwordProvider.SetEncryptionMode(false);

                RestorePreview preview;
                using (var previewStorage = await CreateStorageForHistoryItemAsync(backup))
                {
                    var previewRestore = new Restore(this, previewStorage, passwordProvider, _state);
                    preview = await previewRestore.PreviewRestoreAsync(backup.Path, targetPath);
                }

                var confirm = new OperationConfirm(preview, Window.GetWindow(this));
                if (!confirm.Confirmed)
                {
                    return;
                }

                var progressWindow = new OperationProgressWindow($"Restoring {backup.Directory}", _fileLogger);
                progressWindow.RunModal(Window.GetWindow(this), async token =>
                {
                    using var storage = await CreateStorageForHistoryItemAsync(backup);
                    var restore = new Restore(progressWindow, storage, passwordProvider, _state);

                    var outcome = await restore.RestoreFromBackupAsync(
                        backup.Path,
                        targetPath,
                        new RestoreOptions
                        {
                            ConflictPolicy = confirm.ConflictPolicy,
                            RelativePaths = confirm.SelectedRelativePaths
                        },
                        progressWindow.RestoreProgress,
                        token);

                    progressWindow.Log(
                        $"Restored {outcome.FilesRestored} file(s); skipped {outcome.FilesSkipped}, kept both {outcome.FilesKeptBoth}, overwrote {outcome.FilesOverwritten}.",
                        LogLevel.Info);
                });

                await SaveStateSafelyAsync();

                if (progressWindow.Succeeded)
                {
                    Log($"Restore completed: {targetPath}", LogLevel.Info);
                    MessageBox.Show("Restore completed successfully!", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (progressWindow.WasCancelled)
                {
                    Log("Restore cancelled.", LogLevel.Warning);
                }
                else if (progressWindow.Failure != null)
                {
                    Log($"Restore failed: {progressWindow.Failure.Message}", LogLevel.Error);
                    MessageBox.Show($"Restore failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                await SaveStateSafelyAsync();

                Log($"Restore failed: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task SaveStateSafelyAsync()
        {
            if (_state == null)
            {
                return;
            }

            try
            {
                await _state.SaveStateAsync();
            }
            catch (Exception saveEx)
            {
                Log($"Failed to persist restore telemetry: {saveEx.Message}", LogLevel.Warning);
            }
        }

        /// <summary>Wraps the confirm dialog so both entry points read the same way.</summary>
        private sealed class OperationConfirm
        {
            public OperationConfirm(RestorePreview preview, Window? owner)
            {
                var window = new RestoreConfirmWindow(preview);
                if (owner != null)
                {
                    window.Owner = owner;
                }

                Confirmed = window.ShowDialog() == true && window.Confirmed;
                ConflictPolicy = window.ConflictPolicy;
                SelectedRelativePaths = window.SelectedRelativePaths;
            }

            public bool Confirmed { get; }
            public RestoreConflictPolicy ConflictPolicy { get; }
            public IReadOnlySet<string>? SelectedRelativePaths { get; }
        }

        private static bool IsSystemBackupGroup(string group)
        {
            return group.Equals("system_programs", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_environment", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_settings", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<IStorage> CreateStorageForHistoryItemAsync(BackupHistoryItem backup)
        {
            var storageType = string.IsNullOrWhiteSpace(backup.StorageType)
                ? _configManager.GlobalStorageType
                : backup.StorageType;

            return await _configManager.CreateStorageAsync(storageType);
        }

        private async Task SystemBackupAsync()
        {
            if (!OperatingSystem.IsWindows())
            {
                MessageBox.Show("System backup is only available on Windows.", "System Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Mirrors what BackupSystemAsync actually runs, which is driven by the
                // systemBackup.include* switches rather than a fixed list.
                var components = new List<string>();
                if (_configManager.SystemBackup.IncludePrograms)
                {
                    components.Add("• Installed programs list");
                }

                if (_configManager.SystemBackup.IncludeEnvironmentVariables)
                {
                    components.Add("• Environment variables");
                }

                if (_configManager.SystemBackup.IncludeWindowsSettings)
                {
                    components.Add("• Windows settings");
                }

                if (components.Count == 0)
                {
                    MessageBox.Show(
                        "No system backup components are enabled. Enable at least one on the Settings page.",
                        "System Backup",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var result = MessageBox.Show(
                    $"This will backup:\n{string.Join("\n", components)}\n\nContinue?",
                    "System Backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    Log("Starting system backup...", LogLevel.Info);

                    _state ??= await AppServices.GetSystemStateAsync();

                    var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();
                    passwordProvider.SetEncryptionMode(true);
                    var systemBackup = new SystemBackupManager(this, _configManager, _state, passwordProvider);
                    await systemBackup.BackupSystemAsync();

                    Log("System backup completed", LogLevel.Info);
                    await RefreshBackupHistoryAsync();
                    UpdateStatistics();
                    MessageBox.Show("System backup completed successfully!", "System Backup", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log($"System backup failed: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"System backup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateStatusIndicator(bool isRunning)
        {
            Dispatcher.Invoke(() =>
            {
                var color = isRunning ? System.Windows.Media.Color.FromRgb(16, 185, 129) : System.Windows.Media.Color.FromRgb(239, 68, 68);
                var colorAnimation = new System.Windows.Media.Animation.ColorAnimation
                {
                    To = color,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
                };

                var brush = StatusIndicator.Fill as System.Windows.Media.SolidColorBrush;
                brush?.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, colorAnimation);

                if (StatusIndicator.Effect is System.Windows.Media.Effects.DropShadowEffect effect)
                {
                    effect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, colorAnimation);
                }
            });
        }
    }
}
