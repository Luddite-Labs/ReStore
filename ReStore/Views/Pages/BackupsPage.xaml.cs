using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using ReStore.Services;
using ReStore.Views.Windows;

namespace ReStore.Views.Pages
{
    public class BackupItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private static readonly Brush _recentBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        private static readonly Brush _archivedBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));

        public string Directory { get; set; } = "";
        public string Group { get; set; } = "";
        public string Path { get; set; } = "";
        public string StorageType { get; set; } = "";
        public string? StorageBasePath { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsDiff { get; set; }
        public long SizeBytes { get; set; }
        public BackupArtifactType ArtifactType { get; set; } = BackupArtifactType.Archive;
        public List<string> ChunkIds { get; set; } = [];
        public string? ChunkStorageNamespace { get; set; }
        public bool Encrypted { get; set; }
        public string? Label { get; set; }

        public string LabelDisplay => string.IsNullOrWhiteSpace(Label) ? string.Empty : Label;
        public Visibility LabelVisibility => string.IsNullOrWhiteSpace(Label) ? Visibility.Collapsed : Visibility.Visible;

        public string TypeLabel => CanVerify
            ? (Encrypted ? "Snapshot (encrypted)" : "Snapshot")
            : IsDiff ? "Differential" : "Full";
        public string SizeLabel => SizeBytes == 0 ? "Unknown" : ByteFormatter.Format(SizeBytes);
        public string TimestampLabel => $"{Timestamp.ToUniversalTime():MMM dd, yyyy HH:mm:ss} UTC";
        public string StatusText => DateTime.UtcNow - Timestamp.ToUniversalTime() < TimeSpan.FromDays(7) ? "Recent" : "Archived";
        public Brush StatusColor => DateTime.UtcNow - Timestamp.ToUniversalTime() < TimeSpan.FromDays(7) ? _recentBrush : _archivedBrush;
        public bool CanVerify => IsSnapshotArtifactPath(Path);
        public bool CanRestore => IsSnapshotArtifactPath(Path);
        public bool CanOpenLocation => (IsLocalStorageType(StorageType) || System.IO.Path.IsPathRooted(Path))
            && TryResolveStoragePath(Path, StorageBasePath, out var resolvedPath)
            && (File.Exists(resolvedPath) || System.IO.Directory.Exists(resolvedPath));
        public Visibility OpenLocationVisibility => CanOpenLocation ? Visibility.Visible : Visibility.Collapsed;
        public string? ResolvedStoragePath => TryResolveStoragePath(Path, StorageBasePath, out var resolvedPath)
            ? resolvedPath
            : null;

        public string DisplayPath
        {
            get
            {
                if (!IsLocalStorageType(StorageType))
                {
                    return Path;
                }

                if (TryResolveStoragePath(Path, StorageBasePath, out var resolvedPath))
                {
                    return resolvedPath;
                }

                return Path;
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

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

        private static bool IsLocalStorageType(string? storageType)
        {
            return string.Equals(storageType, "local", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveStoragePath(string? artifactPath, string? storageBasePath, out string resolvedPath)
        {
            resolvedPath = string.Empty;

            if (string.IsNullOrWhiteSpace(artifactPath))
            {
                return false;
            }

            if (System.IO.Path.IsPathRooted(artifactPath))
            {
                resolvedPath = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(artifactPath));
                return true;
            }

            if (string.IsNullOrWhiteSpace(storageBasePath))
            {
                return false;
            }

            var relativePath = artifactPath;
            if (relativePath.StartsWith("./", StringComparison.Ordinal) || relativePath.StartsWith(".\\", StringComparison.Ordinal))
            {
                relativePath = relativePath[2..];
            }

            var normalizedRelativePath = relativePath
                .Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar)
                .TrimStart(System.IO.Path.DirectorySeparatorChar);

            resolvedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(storageBasePath, normalizedRelativePath));
            return true;
        }
    }

    public partial class BackupsPage : Page
    {
        private readonly Logger _logger = AppServices.Logger;
        private ConfigManager _configManager = null!;
        private SystemState? _state;
        private readonly ObservableCollection<BackupItem> _backups = [];
        private List<BackupItem> _allBackups = [];
        private bool _initialized;

        public BackupsPage()
        {
            InitializeComponent();

            BackupsList.ItemsSource = _backups;

            SearchBox.TextChanged += (_, __) => ApplyFilters();
            FilterTypeCombo.SelectionChanged += (_, __) => ApplyFilters();
            FilterStorageCombo.SelectionChanged += (_, __) => ApplyFilters();
            SortCombo.SelectionChanged += (_, __) => ApplyFilters();
            RefreshBtn.Click += async (_, __) => await LoadBackupsAsync();

            RestoreSelectedBtn.Click += async (_, __) => await RestoreSelectedAsync();
            DeleteSelectedBtn.Click += async (_, __) => await DeleteSelectedAsync();
            SelectAllBtn.Click += (_, __) => SelectAll();
            DeselectAllBtn.Click += (_, __) => DeselectAll();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _configManager = await AppServices.GetConfigManagerAsync();
                _state = await AppServices.GetSystemStateAsync();

                await LoadBackupsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
        }

        private async Task LoadBackupsAsync()
        {
            if (_state == null) return;
            await Task.Run(() =>
            {
                var items = new List<BackupItem>();

                foreach (var kvp in _state.GetBackupHistorySnapshot())
                {
                    if (IsSystemBackupGroup(kvp.Key))
                    {
                        continue;
                    }

                    var directory = kvp.Key;
                    foreach (var backup in kvp.Value)
                    {
                        var storageType = string.IsNullOrWhiteSpace(backup.StorageType)
                            ? _configManager.GlobalStorageType
                            : backup.StorageType;

                        items.Add(new BackupItem
                        {
                            Directory = Path.GetFileName(directory),
                            Group = directory,
                            Path = backup.Path,
                            StorageType = storageType ?? string.Empty,
                            StorageBasePath = ResolveStorageBasePath(storageType),
                            Timestamp = backup.Timestamp,
                            IsDiff = backup.IsDiff,
                            SizeBytes = backup.SizeBytes,
                            ArtifactType = backup.ArtifactType,
                            ChunkIds = [.. backup.ChunkIds],
                            ChunkStorageNamespace = backup.ChunkStorageNamespace,
                            Encrypted = backup.Encrypted,
                            Label = backup.Label,
                            IsSelected = false
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    foreach (var backup in _allBackups)
                    {
                        backup.PropertyChanged -= BackupItem_PropertyChanged;
                    }

                    foreach (var backup in items)
                    {
                        backup.PropertyChanged += BackupItem_PropertyChanged;
                    }

                    _allBackups = items;
                    RefreshStorageFilterOptions();
                    ApplyFilters();
                });
            });
        }

        private void BackupItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(BackupItem.IsSelected))
            {
                return;
            }

            UpdateStats();
            UpdateSelectionButtons();
        }

        private void ApplyFilters()
        {
            var filtered = _allBackups.AsEnumerable();

            var searchText = SearchBox.Text?.Trim().ToLower();
            if (!string.IsNullOrEmpty(searchText))
            {
                filtered = filtered.Where(b => b.Directory.ToLower().Contains(searchText) ||
                                              b.Path.ToLower().Contains(searchText));
            }

            if (FilterTypeCombo.SelectedItem is ComboBoxItem filterItem)
            {
                var filterTag = filterItem.Tag?.ToString();
                var now = DateTime.UtcNow;

                filtered = filterTag switch
                {
                    "encrypted" => filtered.Where(b => b.Encrypted),
                    "plain" => filtered.Where(b => !b.Encrypted),
                    "recent" => filtered.Where(b => now - b.Timestamp.ToUniversalTime() <= TimeSpan.FromDays(7)),
                    "old" => filtered.Where(b => now - b.Timestamp.ToUniversalTime() > TimeSpan.FromDays(30)),
                    _ => filtered
                };
            }

            if (FilterStorageCombo.SelectedItem is ComboBoxItem storageItem
                && storageItem.Tag?.ToString() is { } storageTag
                && storageTag != "all")
            {
                filtered = filtered.Where(b => string.Equals(b.StorageType, storageTag, StringComparison.OrdinalIgnoreCase));
            }

            if (SortCombo.SelectedItem is ComboBoxItem sortItem)
            {
                var sortTag = sortItem.Tag?.ToString();
                filtered = sortTag switch
                {
                    "oldest" => filtered.OrderBy(b => b.Timestamp),
                    "dir_asc" => filtered.OrderBy(b => b.Directory),
                    "dir_desc" => filtered.OrderByDescending(b => b.Directory),
                    "size_desc" => filtered.OrderByDescending(b => b.SizeBytes),
                    _ => filtered.OrderByDescending(b => b.Timestamp)
                };
            }

            _backups.Clear();
            foreach (var item in filtered)
            {
                _backups.Add(item);
            }

            UpdateStats();
            UpdateSelectionButtons();
        }

        /// <summary>Rebuilds the provider dropdown from what history actually contains.</summary>
        private void RefreshStorageFilterOptions()
        {
            var previousTag = (FilterStorageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";

            FilterStorageCombo.Items.Clear();
            FilterStorageCombo.Items.Add(new ComboBoxItem { Content = "All Providers", Tag = "all" });

            foreach (var storageType in _allBackups
                .Select(backup => backup.StorageType)
                .Where(storageType => !string.IsNullOrWhiteSpace(storageType))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(storageType => storageType, StringComparer.OrdinalIgnoreCase))
            {
                FilterStorageCombo.Items.Add(new ComboBoxItem { Content = storageType, Tag = storageType });
            }

            var restored = FilterStorageCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), previousTag, StringComparison.OrdinalIgnoreCase));

            FilterStorageCombo.SelectedItem = restored ?? FilterStorageCombo.Items[0];
        }

        private void UpdateStats()
        {
            var total = _backups.Count;
            var selected = _backups.Count(b => b.IsSelected);
            var totalSize = _backups.Sum(b => b.SizeBytes);

            if (selected > 0)
            {
                StatsText.Text = $"{selected} of {total} selected • {ByteFormatter.Format(totalSize)} total";
            }
            else
            {
                StatsText.Text = $"{total} backups • {ByteFormatter.Format(totalSize)} total";
            }

            UpdateStorageBreakdown();
        }

        private void UpdateStorageBreakdown()
        {
            if (_backups.Count == 0)
            {
                StorageBreakdownText.Text = string.Empty;
                return;
            }

            var breakdown = _backups
                .GroupBy(backup => string.IsNullOrWhiteSpace(backup.StorageType) ? "unknown" : backup.StorageType,
                    StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Sum(backup => backup.SizeBytes))
                .Select(group => $"{group.Key}: {group.Count()} • {ByteFormatter.Format(group.Sum(backup => backup.SizeBytes))}");

            StorageBreakdownText.Text = "By provider — " + string.Join("   |   ", breakdown);
        }

        private void UpdateSelectionButtons()
        {
            var hasSelection = _backups.Any(b => b.IsSelected);
            RestoreSelectedBtn.IsEnabled = hasSelection;
            DeleteSelectedBtn.IsEnabled = hasSelection;
        }

        private void SelectAll()
        {
            foreach (var backup in _backups)
            {
                backup.IsSelected = true;
            }
            UpdateStats();
            UpdateSelectionButtons();
        }

        private void DeselectAll()
        {
            foreach (var backup in _backups)
            {
                backup.IsSelected = false;
            }
            UpdateStats();
            UpdateSelectionButtons();
        }

        private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                await RestoreSingleBackupAsync(backup);
            }
        }

        private async void VerifyBackup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                await VerifySingleBackupAsync(backup);
            }
        }

        private void OpenBackupLocation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                OpenBackupLocation(backup);
            }
        }

        private async Task RestoreSingleBackupAsync(BackupItem backup)
        {
            try
            {
                if (!backup.CanRestore)
                {
                    MessageBox.Show(
                        "Restore is only available for snapshot manifest or HEAD artifacts on this page.",
                        "Restore Not Available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
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
                if (string.IsNullOrEmpty(targetPath)) return;

                var passwordProvider = App.GlobalPasswordProvider ?? new GuiPasswordProvider();
                passwordProvider.SetEncryptionMode(false);

                // Preview first: the user sees exactly which existing files are at risk
                // before anything is written.
                RestorePreview preview;
                using (var previewStorage = await CreateStorageForBackupAsync(backup))
                {
                    var previewRestore = new Restore(_logger, previewStorage, passwordProvider, _state);
                    preview = await previewRestore.PreviewRestoreAsync(backup.Path, targetPath);
                }

                var confirm = new RestoreConfirmWindow(preview) { Owner = Window.GetWindow(this) };
                if (confirm.ShowDialog() != true || !confirm.Confirmed)
                {
                    return;
                }

                var progressWindow = new OperationProgressWindow($"Restoring {backup.Directory}", _logger);
                progressWindow.RunModal(Window.GetWindow(this), async token =>
                {
                    using var storage = await CreateStorageForBackupAsync(backup);
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

                await SaveStateSafelyAsync("restore telemetry");

                if (progressWindow.Succeeded)
                {
                    MessageBox.Show("Restore completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (progressWindow.Failure != null)
                {
                    MessageBox.Show($"Restore failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                await SaveStateSafelyAsync("restore telemetry");
                MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task VerifySingleBackupAsync(BackupItem backup)
        {
            if (!backup.CanVerify)
            {
                MessageBox.Show(
                    "Verification is only available for snapshot manifests or HEAD references.",
                    "Verification Not Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirmation = MessageBox.Show(
                $"Verify snapshot integrity?\n\n{backup.Directory}\n{backup.TimestampLabel}\n\nPath: {backup.Path}",
                "Confirm Verification",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var passwordProvider = App.GlobalPasswordProvider ?? new GuiPasswordProvider();
                passwordProvider.SetEncryptionMode(false);

                SnapshotVerificationResult? verificationResult = null;

                var progressWindow = new OperationProgressWindow($"Verifying {backup.Directory}", _logger);
                progressWindow.RunModal(Window.GetWindow(this), async token =>
                {
                    using var storage = await CreateStorageForBackupAsync(backup);
                    var verifier = new SnapshotIntegrityVerifier(progressWindow, storage, passwordProvider, _state);
                    verificationResult = await verifier.VerifyAsync(backup.Path, progressWindow.VerificationProgress, token);
                });

                await SaveStateSafelyAsync("verification telemetry");

                if (progressWindow.WasCancelled)
                {
                    return;
                }

                if (progressWindow.Failure != null)
                {
                    MessageBox.Show($"Verification failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (verificationResult == null)
                {
                    return;
                }

                if (verificationResult.IsValid)
                {
                    MessageBox.Show(
                        $"Verification passed.\n\nSnapshot: {verificationResult.SnapshotId}\nFiles: {verificationResult.FileCount}\nUnique chunks: {verificationResult.UniqueChunks}",
                        "Verification Passed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var previewErrors = string.Join(
                    "\n",
                    verificationResult.Errors
                        .Take(5)
                        .Select((error, index) => $"{index + 1}. {error}"));
                var remainingErrors = verificationResult.Errors.Count > 5
                    ? $"\n...and {verificationResult.Errors.Count - 5} more issue(s)."
                    : string.Empty;

                MessageBox.Show(
                    $"Verification failed with {verificationResult.Errors.Count} issue(s).\n\n{previewErrors}{remainingErrors}",
                    "Verification Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                await SaveStateSafelyAsync("verification telemetry");
                MessageBox.Show($"Verification failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LabelBackup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not BackupItem backup)
            {
                return;
            }

            var dialog = new TextInputWindow(
                "Label restore point",
                $"Name this restore point so it is easy to find later.\n\n{backup.Directory} — {backup.TimestampLabel}",
                backup.Label,
                "For example: 'before Windows reinstall'. Leave empty to remove the label.")
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() != true || _state == null)
            {
                return;
            }

            var label = string.IsNullOrWhiteSpace(dialog.Value) ? null : dialog.Value.Trim();

            if (!_state.SetBackupLabel(backup.Group, backup.Path, label))
            {
                MessageBox.Show("Could not find this backup in the history.", "Label", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await SaveStateSafelyAsync("backup label");
            await LoadBackupsAsync();
        }

        private async Task RestoreSelectedAsync()
        {
            var selected = _backups.Where(b => b.IsSelected).ToList();
            if (selected.Count == 0) return;

            var result = MessageBox.Show(
                $"Restore {selected.Count} backup(s)?\n\nYou will be prompted for a destination folder for each backup.",
                "Confirm Restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                foreach (var backup in selected)
                {
                    await RestoreSingleBackupAsync(backup);
                }
            }
        }

        private async void DeleteBackup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                await DeleteSingleBackupAsync(backup);
            }
        }

        private async Task DeleteSingleBackupAsync(BackupItem backup)
        {
            var result = MessageBox.Show(
                $"Delete this backup?\n\n{backup.Directory}\n{backup.TimestampLabel}\n\nThis action cannot be undone.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await DeleteBackupArtifactAsync(backup);
                    await SaveStateSafelyAsync("backup deletion");

                    _allBackups.Remove(backup);
                    _backups.Remove(backup);
                    UpdateStats();

                    MessageBox.Show("Backup deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Delete failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task DeleteSelectedAsync()
        {
            var selected = _backups.Where(b => b.IsSelected).ToList();
            if (selected.Count == 0) return;

            var result = MessageBox.Show(
                $"Delete {selected.Count} backup(s)?\n\nThis action cannot be undone.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var successCount = 0;
                var failCount = 0;

                foreach (var backup in selected)
                {
                    try
                    {
                        await DeleteBackupArtifactAsync(backup);

                        _allBackups.Remove(backup);
                        _backups.Remove(backup);
                        successCount++;
                    }
                    catch
                    {
                        failCount++;
                    }
                }

                await SaveStateSafelyAsync("backup deletion");

                UpdateStats();

                if (failCount > 0)
                {
                    MessageBox.Show($"Deleted {successCount} backup(s). Failed to delete {failCount}.", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show($"Successfully deleted {successCount} backup(s).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void ShowDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BackupItem backup)
            {
                var details = $"Directory: {backup.Directory}\n\n" +
                             (string.IsNullOrWhiteSpace(backup.Label) ? "" : $"Label: {backup.Label}\n\n") +
                             $"Path: {backup.DisplayPath}\n\n" +
                             $"Timestamp: {backup.TimestampLabel}\n\n" +
                             $"Type: {backup.TypeLabel}\n\n" +
                             $"Size: {backup.SizeLabel}\n\n" +
                             $"Status: {backup.StatusText}";

                MessageBox.Show(details, "Backup Details", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async Task SaveStateSafelyAsync(string context)
        {
            if (_state == null)
            {
                return;
            }

            try
            {
                await _state.SaveStateAsync();
            }
            catch (Exception ex)
            {
                _logger.Log($"Failed to persist {context}: {ex.Message}", LogLevel.Warning);
            }
        }

        private static bool IsSystemBackupGroup(string group)
        {
            return group.Equals("system_programs", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_environment", StringComparison.OrdinalIgnoreCase)
                || group.Equals("system_settings", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<IStorage> CreateStorageForBackupAsync(BackupItem backup)
        {
            var storageType = string.IsNullOrWhiteSpace(backup.StorageType)
                ? _configManager.GlobalStorageType
                : backup.StorageType;

            return await _configManager.CreateStorageAsync(storageType);
        }

        private async Task DeleteBackupArtifactAsync(BackupItem backup)
        {
            using var storage = await CreateStorageForBackupAsync(backup);

            if (backup.ArtifactType == BackupArtifactType.SnapshotManifest)
            {
                await DeleteSnapshotManifestBackupAsync(storage, backup);
            }
            else
            {
                await DeleteArchiveBackupAsync(storage, backup.Path);
            }

            _state?.RemoveBackupsFromGroup(backup.Group, [backup.Path]);
        }

        private async Task DeleteSnapshotManifestBackupAsync(IStorage storage, BackupItem backup)
        {
            var manifestExists = await storage.ExistsAsync(backup.Path);
            if (manifestExists)
            {
                await storage.DeleteAsync(backup.Path);
            }

            if (_state == null)
            {
                return;
            }

            var unreferencedChunkIds = _state.UnregisterChunkReferences(
                backup.StorageType,
                backup.ChunkIds,
                backup.ChunkStorageNamespace);

            foreach (var chunkId in unreferencedChunkIds)
            {
                string chunkPath;
                try
                {
                    chunkPath = SnapshotStoragePaths.GetChunkPath(chunkId, backup.ChunkStorageNamespace);
                }
                catch (ArgumentException ex)
                {
                    _logger.Log($"Invalid chunk metadata for '{chunkId}': {ex.Message}", LogLevel.Warning);
                    continue;
                }

                try
                {
                    if (await storage.ExistsAsync(chunkPath))
                    {
                        await storage.DeleteAsync(chunkPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"Failed deleting chunk '{chunkPath}': {ex.Message}", LogLevel.Warning);
                }
            }
        }

        private static async Task DeleteArchiveBackupAsync(IStorage storage, string backupPath)
        {
            if (await storage.ExistsAsync(backupPath))
            {
                await storage.DeleteAsync(backupPath);
            }

            if (!backupPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var metadataPath = backupPath + ".meta";
            if (await storage.ExistsAsync(metadataPath))
            {
                await storage.DeleteAsync(metadataPath);
            }
        }

        private string? ResolveStorageBasePath(string? storageType)
        {
            if (string.IsNullOrWhiteSpace(storageType))
            {
                return null;
            }

            if (!_configManager.StorageSources.TryGetValue(storageType, out var storageConfig)
                || string.IsNullOrWhiteSpace(storageConfig.Path))
            {
                return null;
            }

            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(storageConfig.Path));
        }

        private static void OpenBackupLocation(BackupItem backup)
        {
            var resolvedPath = backup.ResolvedStoragePath;
            if (!backup.CanOpenLocation || string.IsNullOrWhiteSpace(resolvedPath))
            {
                MessageBox.Show(
                    "This action is available only for local backups with existing files.",
                    "Open Location",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                var arguments = Directory.Exists(resolvedPath)
                    ? $"\"{resolvedPath}\""
                    : $"/select,\"{resolvedPath}\"";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = arguments,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open location: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
