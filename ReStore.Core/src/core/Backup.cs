using ReStore.Core.src.utils;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.backup;
using System.Text.Json;

namespace ReStore.Core.src.core;

public class Backup
{
    private readonly ILogger _logger;
    private readonly SystemState _state;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly IConfigManager _config;
    private readonly FileSelectionService _fileSelectionService;
    private readonly FileDiffSyncManager? _diffSyncManager;
    private readonly IPasswordProvider? _passwordProvider;
    private readonly RetentionManager _retentionManager;
    private readonly EncryptionService _encryptionService;

    public Backup(ILogger logger, SystemState state, SizeAnalyzer sizeAnalyzer, IConfigManager config, IPasswordProvider? passwordProvider = null)
    {
        _logger = logger;
        _state = state;
        _sizeAnalyzer = sizeAnalyzer;
        _config = config ?? throw new ArgumentNullException(nameof(config), "Config cannot be null");
        _fileSelectionService = new FileSelectionService(logger, _config);
        _passwordProvider = passwordProvider;
        _encryptionService = new EncryptionService(_logger);

        var backupConfig = new BackupConfigurationManager(logger, _config);
        _diffSyncManager = new FileDiffSyncManager(logger, state, backupConfig);

        _retentionManager = new RetentionManager(_logger, _config, _state);
    }

    public async Task BackupDirectoryAsync(
        string sourceDirectory,
        string? storageTypeOverride = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("Source directory cannot be null or empty", nameof(sourceDirectory));
        }

        IStorage? storage = null;
        try
        {
            sourceDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourceDirectory));

            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var storageType = storageTypeOverride ?? GetStorageTypeForDirectory(sourceDirectory);
            storage = await _config.CreateStorageAsync(storageType);

            _logger.Log($"Starting backup of {sourceDirectory} using {storageType} storage");

            progress?.Report(new BackupProgress(sourceDirectory, 0, 0, 0, 0, BackupPhase.Enumerating));

            _sizeAnalyzer.SizeThreshold = _config.SizeThresholdMB * 1024 * 1024;
            var (size, exceedsThreshold) = await _sizeAnalyzer.AnalyzeDirectoryAsync(sourceDirectory);

            if (exceedsThreshold)
            {
                _logger.Log($"Warning: Directory size ({size} bytes) exceeds threshold");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var allFiles = GetFilesInDirectory(sourceDirectory);

            var filesToBackup = _diffSyncManager != null
                ? _diffSyncManager.GetFilesToBackup(allFiles, sourceDirectory)
                : allFiles;

            // Clean up metadata for files that no longer exist in this directory
            var trackedFiles = _state.GetTrackedFilesInDirectory(sourceDirectory) ?? [];
            var filesToRemove = trackedFiles.Except(allFiles, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var file in filesToRemove)
            {
                await _state.AddOrUpdateFileMetadataAsync(file);
            }

            if (filesToBackup.Count == 0 && filesToRemove.Count == 0)
            {
                _logger.Log("No files need to be backed up based on the current state and backup type.", LogLevel.Info);
                await _state.SaveStateAsync();
                return;
            }

            _logger.Log($"Preparing to build snapshot manifest for {sourceDirectory}. Changed files: {filesToBackup.Count}, removed files: {filesToRemove.Count}", LogLevel.Info);
            await CreateSnapshotBackupAsync(sourceDirectory, allFiles, filesToBackup, storage, storageType, progress, cancellationToken);

            if (_diffSyncManager != null)
            {
                await _diffSyncManager.UpdateFileMetadataAsync(filesToBackup);
            }
            else
            {
                foreach (var file in filesToBackup)
                {
                    await _state.AddOrUpdateFileMetadataAsync(file);
                }
            }

            _state.LastBackupTime = DateTime.UtcNow;

            await _state.SaveStateAsync();
        }
        catch (OperationCanceledException)
        {
            // Not a backup failure: the three-phase commit means HEAD has not advanced, so
            // the snapshot simply did not happen. Must not be logged as an error or swallowed.
            _logger.Log($"Backup of {sourceDirectory} cancelled by request.", LogLevel.Warning);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup directory: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            storage?.Dispose();
        }
    }

    public async Task BackupFilesAsync(
        IEnumerable<string> filesToBackup,
        string baseDirectory,
        string? storageTypeOverride = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (filesToBackup == null)
        {
            throw new ArgumentNullException(nameof(filesToBackup));
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory cannot be null or empty", nameof(baseDirectory));
        }

        var fileList = filesToBackup.ToList();
        if (fileList.Count == 0)
        {
            _logger.Log("No files provided for backup.", LogLevel.Info);
            return;
        }

        IStorage? storage = null;
        try
        {
            var storageType = storageTypeOverride ?? GetStorageTypeForDirectory(baseDirectory);
            storage = await _config.CreateStorageAsync(storageType);

            _logger.Log($"Starting backup of {fileList.Count} specific files from base directory {baseDirectory} using {storageType} storage", LogLevel.Info);

            var existingFilesToBackup = fileList.Where(File.Exists).ToList();
            var deletedFiles = fileList.Except(existingFilesToBackup, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var file in deletedFiles)
            {
                await _state.AddOrUpdateFileMetadataAsync(file);
            }

            if (existingFilesToBackup.Count == 0 && deletedFiles.Count == 0)
            {
                _logger.Log("No valid file changes detected for snapshot creation.", LogLevel.Info);
                await _state.SaveStateAsync();
                return;
            }

            var allFiles = GetFilesInDirectory(baseDirectory);
            await CreateSnapshotBackupAsync(baseDirectory, allFiles, existingFilesToBackup, storage, storageType, progress, cancellationToken);

            foreach (var file in existingFilesToBackup)
            {
                await _state.AddOrUpdateFileMetadataAsync(file);
            }

            _logger.Log("Specific file snapshot backup completed.", LogLevel.Info);

            await _state.SaveStateAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.Log($"Backup of specific files from {baseDirectory} cancelled by request.", LogLevel.Warning);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup specific files from {baseDirectory}: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            storage?.Dispose();
        }
    }

    private List<string> GetFilesInDirectory(string directory)
    {
        try
        {
            var directoryList = new List<string> { directory };
            var files = _fileSelectionService.GetFilesToBackup(directoryList);

            _logger.Log($"Found {files.Count} files to backup in {directory}", LogLevel.Info);
            return files;
        }
        catch (Exception ex)
        {
            _logger.Log($"Error collecting files: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException(
                $"Failed to enumerate files for backup in '{directory}'.",
                ex);
        }
    }

    private string GetStorageTypeForDirectory(string directory)
    {
        var normalizedDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));

        var watchConfig = _config.WatchDirectories.FirstOrDefault(w =>
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(w.Path))
                .Equals(normalizedDir, StringComparison.OrdinalIgnoreCase));

        return watchConfig?.StorageType ?? _config.GlobalStorageType;
    }

    private async Task CreateSnapshotBackupAsync(
        string sourceDirectory,
        List<string> allFiles,
        List<string> changedFiles,
        IStorage storage,
        string storageType,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var chunkConfig = _config.ChunkDiffing ?? new ChunkDiffingConfig();

        if (allFiles.Count > chunkConfig.MaxFilesPerSnapshot)
        {
            throw new InvalidOperationException(
                $"Snapshot exceeds maxFilesPerSnapshot safety limit ({chunkConfig.MaxFilesPerSnapshot}) for directory '{sourceDirectory}'.");
        }

        try
        {
            var normalizedSourceDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourceDirectory));
            var normalizedChangedFiles = changedFiles
                .Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            byte[]? encryptionMasterKey = null;
            string? encryptionSalt = null;
            string? chunkStorageNamespace = null;

            if (_config.Encryption.Enabled)
            {
                EnsureEncryptionProviderAvailable();

                var password = await _passwordProvider!.GetPasswordAsync();
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("Encryption is enabled but no password was provided");
                }

                if (string.IsNullOrWhiteSpace(_config.Encryption.Salt))
                {
                    throw new InvalidOperationException("Encryption is enabled but encryption salt is missing from configuration");
                }

                var saltBytes = Convert.FromBase64String(_config.Encryption.Salt);
                encryptionMasterKey = _encryptionService.DeriveKeyFromPassword(password, saltBytes, _config.Encryption.KeyDerivationIterations);
                encryptionSalt = _config.Encryption.Salt;
                chunkStorageNamespace = SnapshotStoragePaths.BuildEncryptedChunkNamespace(encryptionMasterKey);
            }

            var chunkingService = new ChunkingService(
                _logger,
                chunkConfig,
                _encryptionService,
                _config.Encryption.Enabled,
                encryptionMasterKey);

            var previousManifest = await TryLoadLatestManifestAsync(normalizedSourceDirectory, storage);
            var previousFilesByPath = previousManifest?.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, SnapshotFileManifestEntry>(StringComparer.OrdinalIgnoreCase);

            string? previousChunkStorageNamespace;
            try
            {
                previousChunkStorageNamespace = SnapshotStoragePaths.NormalizeChunkStorageNamespace(previousManifest?.ChunkStorageNamespace);
            }
            catch (ArgumentException ex)
            {
                _logger.Log(
                    $"Previous snapshot namespace is invalid and will not be reused: {ex.Message}",
                    LogLevel.Warning);
                previousChunkStorageNamespace = null;
            }

            var canReusePreviousManifestEntries = string.Equals(
                previousChunkStorageNamespace,
                chunkStorageNamespace,
                StringComparison.OrdinalIgnoreCase);

            if (previousManifest != null && !canReusePreviousManifestEntries)
            {
                _logger.Log(
                    "Previous snapshot chunk namespace differs from current backup context; rebuilding all file manifests for this snapshot.",
                    LogLevel.Info);
            }

            var manifestFiles = new List<SnapshotFileManifestEntry>();

            // Payloads upload as they are produced and are not retained, so peak memory does
            // not scale with the size of the change set. Only the handled ids are kept, to
            // skip chunks this snapshot already stored.
            var uploadedChunkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uploadTelemetry = new ChunkUploadTelemetry();

            var orderedFiles = allFiles
                .Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)))
                .Where(File.Exists)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var chunkingBytesTotal = orderedFiles.Sum(path =>
            {
                try
                {
                    return new FileInfo(path).Length;
                }
                catch
                {
                    return 0L;
                }
            });

            var filesProcessed = 0;
            var bytesProcessed = 0L;

            async Task UploadChunkAsync(ChunkBuildPayload chunkPayload, CancellationToken uploadToken)
            {
                if (!uploadedChunkIds.Add(chunkPayload.ChunkId))
                {
                    return;
                }

                await UploadChunkIfMissingAsync(
                    storage,
                    chunkPayload,
                    chunkStorageNamespace,
                    uploadTelemetry,
                    uploadToken);

                // Uploads interleave with chunking, so the byte totals are the running
                // chunking totals, not a separate upload-only denominator.
                progress?.Report(new BackupProgress(
                    $"chunk {uploadedChunkIds.Count}",
                    filesProcessed,
                    orderedFiles.Count,
                    bytesProcessed,
                    chunkingBytesTotal,
                    BackupPhase.Uploading));
            }

            foreach (var absolutePath in orderedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(normalizedSourceDirectory, absolutePath)
                    .Replace(Path.DirectorySeparatorChar, '/');

                progress?.Report(new BackupProgress(
                    relativePath,
                    filesProcessed,
                    orderedFiles.Count,
                    bytesProcessed,
                    chunkingBytesTotal,
                    BackupPhase.Chunking));

                if (canReusePreviousManifestEntries
                    && !normalizedChangedFiles.Contains(absolutePath)
                    && previousFilesByPath.TryGetValue(relativePath, out var existingEntry))
                {
                    manifestFiles.Add(existingEntry);
                    filesProcessed++;
                    bytesProcessed += existingEntry.SizeBytes;
                    continue;
                }

                var chunkBuild = await chunkingService.BuildFileManifestEntryAsync(
                    absolutePath,
                    normalizedSourceDirectory,
                    UploadChunkAsync,
                    cancellationToken);

                manifestFiles.Add(chunkBuild.FileEntry);
                filesProcessed++;
                bytesProcessed += chunkBuild.FileEntry.SizeBytes;
            }

            progress?.Report(new BackupProgress(
                string.Empty,
                filesProcessed,
                orderedFiles.Count,
                bytesProcessed,
                chunkingBytesTotal,
                BackupPhase.Chunking));

            var snapshotId = SnapshotStoragePaths.BuildSnapshotId();
            var manifest = new SnapshotManifest
            {
                Version = chunkConfig.ManifestVersion,
                SnapshotId = snapshotId,
                Group = normalizedSourceDirectory,
                CreatedUtc = DateTime.UtcNow,
                BackupMode = _config.BackupType.ToString(),
                EncryptionEnabled = _config.Encryption.Enabled,
                EncryptionSalt = encryptionSalt,
                KeyDerivationIterations = _config.Encryption.KeyDerivationIterations,
                ChunkStorageNamespace = chunkStorageNamespace,
                Profile = ChunkingProfile.FromConfig(chunkConfig),
                Files = manifestFiles
            };

            manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);

            uploadTelemetry.CandidateChunks = uploadedChunkIds.Count;

            // Every chunk is stored by this point. The snapshot commits in three phases —
            // chunks, then the manifest, then HEAD — so cancelling before HEAD leaves HEAD on
            // the previous snapshot and an interrupted backup is inert.
            progress?.Report(new BackupProgress(
                "manifest",
                orderedFiles.Count,
                orderedFiles.Count,
                chunkingBytesTotal,
                chunkingBytesTotal,
                BackupPhase.Finalising));

            var manifestPath = SnapshotStoragePaths.GetManifestPath(normalizedSourceDirectory, snapshotId);
            await UploadManifestAsync(storage, manifestPath, manifest);

            var headPath = SnapshotStoragePaths.GetHeadPath(normalizedSourceDirectory);
            await UploadSnapshotHeadAsync(storage, headPath, manifestPath, manifest.RootHash);

            var referencedChunkIds = manifest.Files
                .SelectMany(file => file.Chunks)
                .Select(chunk => chunk.ChunkId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            LogChunkReuseTelemetry(normalizedSourceDirectory, manifest, referencedChunkIds, uploadTelemetry);

            var logicalSize = manifest.Files.Sum(file => file.SizeBytes);
            _state.AddSnapshotBackup(
                normalizedSourceDirectory,
                snapshotId,
                manifestPath,
                storageType,
                referencedChunkIds,
                logicalSize,
                manifest.RootHash,
                _config.Encryption.Enabled,
                chunkStorageNamespace);

            await _retentionManager.ApplyGroupAsync(normalizedSourceDirectory);
            _logger.Log($"Snapshot backup completed: {manifestPath}", LogLevel.Info);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to create snapshot backup: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    /// <summary>
    /// Uploads one chunk unless the provider already has it. Called as each chunk is produced,
    /// so the payload can be released immediately afterwards.
    /// </summary>
    private async Task UploadChunkIfMissingAsync(
        IStorage storage,
        ChunkBuildPayload chunk,
        string? chunkStorageNamespace,
        ChunkUploadTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var chunkPath = SnapshotStoragePaths.GetChunkPath(chunk.ChunkId, chunkStorageNamespace);
        if (await storage.ExistsAsync(chunkPath))
        {
            telemetry.ReusedChunks++;
            return;
        }

        var tempChunkPath = Path.Combine(Path.GetTempPath(), $"restore_chunk_{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(tempChunkPath, chunk.StoredPayload, cancellationToken);

        try
        {
            await storage.UploadAsync(tempChunkPath, chunkPath);
            telemetry.UploadedChunks++;
            telemetry.UploadedStoredBytes += chunk.StoredPayload.Length;
            _logger.Log($"Uploaded chunk {chunk.ChunkId} to {chunkPath}", LogLevel.Debug);
        }
        finally
        {
            TryDeleteTemporaryFile(tempChunkPath);
        }
    }

    private void LogChunkReuseTelemetry(
        string sourceDirectory,
        SnapshotManifest manifest,
        IReadOnlyCollection<string> referencedChunkIds,
        ChunkUploadTelemetry uploadTelemetry)
    {
        var totalChunkReferences = manifest.Files.Sum(file => file.Chunks.Count);
        var totalUniqueChunks = referencedChunkIds.Count;
        var uniqueReusedChunks = Math.Max(0, totalUniqueChunks - uploadTelemetry.UploadedChunks);
        var manifestReuseRatio = totalUniqueChunks == 0 ? 0 : (double)uniqueReusedChunks / totalUniqueChunks;
        var uploadBypassRatio = uploadTelemetry.CandidateChunks == 0
            ? 0
            : (double)uploadTelemetry.ReusedChunks / uploadTelemetry.CandidateChunks;

        // Stored size of each unique chunk this snapshot references, taken from the manifest
        // so the figure is exact rather than an average-size estimate.
        var referencedStoredBytes = manifest.Files
            .SelectMany(file => file.Chunks)
            .GroupBy(chunk => chunk.ChunkId, StringComparer.OrdinalIgnoreCase)
            .Sum(group => (long)group.First().StoredSizeBytes);

        var dedupSavedBytes = Math.Max(0, referencedStoredBytes - uploadTelemetry.UploadedStoredBytes);

        _logger.Log(
            $"Chunk telemetry: group='{sourceDirectory}', snapshot='{manifest.SnapshotId}', fileCount={manifest.Files.Count}, chunkRefs={totalChunkReferences}, uniqueChunks={totalUniqueChunks}, uploadedChunks={uploadTelemetry.UploadedChunks}, reusedChunks={uniqueReusedChunks}, manifestReuseRatio={manifestReuseRatio:P2}, candidateChunks={uploadTelemetry.CandidateChunks}, storageHitChunks={uploadTelemetry.ReusedChunks}, uploadBypassRatio={uploadBypassRatio:P2}, referencedStoredBytes={referencedStoredBytes}, uploadedStoredBytes={uploadTelemetry.UploadedStoredBytes}, dedupSavedBytes={dedupSavedBytes}",
            LogLevel.Info);

        _state.RecordSnapshotBackupTelemetry(
            fileCount: manifest.Files.Count,
            chunkReferences: totalChunkReferences,
            uniqueChunks: totalUniqueChunks,
            uploadedChunks: uploadTelemetry.UploadedChunks,
            uniqueReusedChunks: uniqueReusedChunks,
            storageHitChunks: uploadTelemetry.ReusedChunks,
            candidateChunks: uploadTelemetry.CandidateChunks,
            referencedStoredBytes: referencedStoredBytes,
            uploadedStoredBytes: uploadTelemetry.UploadedStoredBytes);
    }

    private async Task UploadManifestAsync(IStorage storage, string manifestPath, SnapshotManifest manifest)
    {
        var tempManifestPath = Path.Combine(Path.GetTempPath(), $"restore_manifest_{Guid.NewGuid():N}.json");

        try
        {
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(tempManifestPath, manifestJson);
            await storage.UploadAsync(tempManifestPath, manifestPath);
            _logger.Log($"Uploaded snapshot manifest: {manifestPath}", LogLevel.Debug);
        }
        finally
        {
            TryDeleteTemporaryFile(tempManifestPath);
        }
    }

    private async Task UploadSnapshotHeadAsync(IStorage storage, string headPath, string manifestPath, string rootHash)
    {
        var tempHeadPath = Path.Combine(Path.GetTempPath(), $"restore_head_{Guid.NewGuid():N}.txt");

        try
        {
            var content = $"{manifestPath}\n{rootHash}\n";
            await File.WriteAllTextAsync(tempHeadPath, content);
            await storage.UploadAsync(tempHeadPath, headPath);
            _logger.Log($"Updated snapshot head: {headPath}", LogLevel.Debug);
        }
        finally
        {
            TryDeleteTemporaryFile(tempHeadPath);
        }
    }

    private async Task<SnapshotManifest?> TryLoadLatestManifestAsync(string sourceDirectory, IStorage storage)
    {
        var previousBackupPath = _state.GetPreviousBackupPath(sourceDirectory);
        if (string.IsNullOrWhiteSpace(previousBackupPath))
        {
            return null;
        }

        var previousPath = previousBackupPath;
        string? expectedRootHashFromHead = null;
        if (previousPath.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
        {
            var headReference = await ResolveManifestPathFromHeadAsync(storage, previousPath);
            previousPath = headReference.ManifestPath;
            expectedRootHashFromHead = headReference.RootHash;
        }

        if (!previousPath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Log($"Ignoring non-manifest previous backup in snapshot flow: {previousPath}", LogLevel.Warning);
            return null;
        }

        var tempManifestPath = Path.Combine(Path.GetTempPath(), $"restore_prev_manifest_{Guid.NewGuid():N}.json");

        try
        {
            await storage.DownloadAsync(previousPath, tempManifestPath);
            var json = await File.ReadAllTextAsync(tempManifestPath);
            var previousManifest = JsonSerializer.Deserialize<SnapshotManifest>(json);

            if (previousManifest == null)
            {
                throw new InvalidOperationException($"Failed to deserialize previous snapshot manifest: {previousPath}");
            }

            if (!SnapshotManifestHasher.IsValid(previousManifest))
            {
                throw new InvalidOperationException($"Previous snapshot manifest hash validation failed: {previousPath}");
            }

            if (!string.IsNullOrWhiteSpace(expectedRootHashFromHead)
                && !expectedRootHashFromHead.Equals(previousManifest.RootHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Previous snapshot HEAD root hash mismatch. Expected '{expectedRootHashFromHead}', actual '{previousManifest.RootHash}'.");
            }

            try
            {
                var normalizedManifestGroup = Path.GetFullPath(Environment.ExpandEnvironmentVariables(previousManifest.Group));
                if (!normalizedManifestGroup.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Log(
                        $"Ignoring previous snapshot manifest from different group. Expected '{sourceDirectory}', found '{normalizedManifestGroup}'.",
                        LogLevel.Warning);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.Log(
                    $"Ignoring previous snapshot manifest with invalid group path '{previousManifest.Group}': {ex.Message}",
                    LogLevel.Warning);
                return null;
            }

            return previousManifest;
        }
        catch (FileNotFoundException)
        {
            _logger.Log($"Previous snapshot manifest not found in storage: {previousPath}", LogLevel.Warning);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"Unable to load previous snapshot manifest '{previousPath}'. Falling back to full snapshot rebuild: {ex.Message}",
                LogLevel.Warning);
            return null;
        }
        finally
        {
            TryDeleteTemporaryFile(tempManifestPath);
        }
    }

    private async Task<SnapshotHeadReference> ResolveManifestPathFromHeadAsync(IStorage storage, string headPath)
    {
        var tempHeadPath = Path.Combine(Path.GetTempPath(), $"restore_head_download_{Guid.NewGuid():N}.txt");
        try
        {
            await storage.DownloadAsync(headPath, tempHeadPath);
            var lines = await File.ReadAllLinesAsync(tempHeadPath);
            var nonEmptyLines = lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();

            var manifestPath = nonEmptyLines.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                throw new InvalidOperationException($"Snapshot head file does not contain a manifest path: {headPath}");
            }

            var rootHash = nonEmptyLines.Count > 1 ? nonEmptyLines[1] : null;
            return new SnapshotHeadReference(manifestPath, string.IsNullOrWhiteSpace(rootHash) ? null : rootHash);
        }
        finally
        {
            TryDeleteTemporaryFile(tempHeadPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        // Runs in finally blocks around chunk, manifest and HEAD uploads: a transient lock
        // must not mask the upload's own outcome, and a leftover temp file is inert.
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ChunkUploadTelemetry
    {
        public int CandidateChunks { get; set; }
        public int UploadedChunks { get; set; }
        public int ReusedChunks { get; set; }

        /// <summary>Stored bytes actually transferred to the provider.</summary>
        public long UploadedStoredBytes { get; set; }
    }

    private sealed class SnapshotHeadReference
    {
        public SnapshotHeadReference(string manifestPath, string? rootHash)
        {
            ManifestPath = manifestPath;
            RootHash = rootHash;
        }

        public string ManifestPath { get; }
        public string? RootHash { get; }
    }

    private void EnsureEncryptionProviderAvailable()
    {
        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encryption is enabled but no password provider is available");
        }
    }
}
