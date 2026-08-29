using ReStore.Core.src.utils;
using ReStore.Core.src.storage;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReStore.Core.src.core;

/// <summary>Options controlling which files a restore writes and how conflicts resolve.</summary>
public sealed class RestoreOptions
{
    public RestoreConflictPolicy ConflictPolicy { get; init; } = RestoreConflictPolicy.Skip;

    /// <summary>
    /// Relative paths (manifest form, '/'-separated) to restore. Null restores everything.
    /// Compared case-insensitively.
    /// </summary>
    public IReadOnlySet<string>? RelativePaths { get; init; }

    /// <summary>Produce the preview and return without writing any file.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Safe default: existing files are left alone. Callers that mean to replace user data
    /// must ask for it explicitly, which matches the CLI's documented <c>--conflict skip</c>
    /// default and <see cref="RestoreConflictPolicy"/>'s stated intent.
    /// </summary>
    public static RestoreOptions Default { get; } = new();

    /// <summary>Replaces differing files, as the pre-conflict-policy behaviour did.</summary>
    public static RestoreOptions Overwrite { get; } = new() { ConflictPolicy = RestoreConflictPolicy.Overwrite };
}

public class Restore(ILogger logger, IStorage storage, IPasswordProvider? passwordProvider = null, SystemState? systemState = null)
{
    private const long MaxChunkCacheBytes = 64L * 1024 * 1024;

    private readonly ILogger _logger = logger;
    private readonly IStorage _storage = storage;
    private readonly IPasswordProvider? _passwordProvider = passwordProvider;
    private readonly SystemState? _systemState = systemState;
    private readonly EncryptionService _encryptionService = new(logger);

    /// <summary>
    /// Restores every file, replacing any that already exist. Kept explicit rather than
    /// deferring to <see cref="RestoreOptions.Default"/>, whose safe default is now Skip.
    /// </summary>
    public Task RestoreFromBackupAsync(
        string backupPath,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        return RestoreFromBackupAsync(backupPath, targetDirectory, RestoreOptions.Overwrite, null, cancellationToken);
    }

    public async Task<RestoreOutcome> RestoreFromBackupAsync(
        string backupPath,
        string targetDirectory,
        RestoreOptions options,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be null or empty", nameof(backupPath));
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Target directory cannot be null or empty", nameof(targetDirectory));
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"restore_snapshot_{Guid.NewGuid():N}");
        SnapshotManifest? loadedManifest = null;
        string resolvedManifestPath = backupPath;
        var restoreTelemetry = new RestoreTelemetry();
        var outcome = new RestoreOutcome();

        try
        {
            _logger.Log($"Starting restore from {backupPath} to {targetDirectory}", LogLevel.Info);

            Directory.CreateDirectory(tempDirectory);

            var loaded = await LoadManifestAsync(backupPath, tempDirectory, cancellationToken);
            var manifest = loaded.Manifest;
            loadedManifest = manifest;
            resolvedManifestPath = loaded.ManifestPath;

            var selectedFiles = SelectFiles(manifest, options.RelativePaths);

            // Telemetry must reflect the selected subset, or a partial restore reports a
            // success ratio against files it was never asked to write.
            restoreTelemetry.FileCountExpected = selectedFiles.Count;
            restoreTelemetry.ChunkReferencesExpected = selectedFiles.Sum(file => file.Chunks.Count);

            var totalBytes = selectedFiles.Sum(file => file.SizeBytes);

            progress?.Report(new RestoreProgress(
                string.Empty, 0, selectedFiles.Count, 0, totalBytes, RestorePhase.Previewing));

            var preview = BuildPreview(
                backupPath,
                resolvedManifestPath,
                manifest,
                targetDirectory,
                selectedFiles,
                manifest.Files.Count - selectedFiles.Count);

            if (options.ConflictPolicy == RestoreConflictPolicy.Fail && preview.HasConflicts)
            {
                throw new InvalidOperationException(
                    $"Restore would replace {preview.DifferingFileCount} existing file(s) at '{targetDirectory}'. " +
                    "Re-run with a conflict policy of Skip, Overwrite or KeepBoth.");
            }

            if (options.DryRun)
            {
                _logger.Log(
                    $"Dry-run restore preview: files={preview.FileCount}, bytes={preview.TotalBytes}, existing={preview.ExistingFileCount}, identical={preview.IdenticalFileCount}, differing={preview.DifferingFileCount}",
                    LogLevel.Info);

                LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: true);
                return outcome;
            }

            byte[]? encryptionMasterKey = await ResolveEncryptionMasterKeyAsync(manifest);

            // Only now that the restore will actually write: a dry run must leave the
            // filesystem exactly as it found it.
            Directory.CreateDirectory(targetDirectory);

            var chunkCache = new ChunkByteCache(MaxChunkCacheBytes);
            var chunkStorageNamespace = SnapshotStoragePaths.NormalizeChunkStorageNamespace(manifest.ChunkStorageNamespace);
            var conflictsByRelativePath = preview.Entries.ToDictionary(
                entry => entry.RelativePath,
                entry => entry.Conflict,
                StringComparer.OrdinalIgnoreCase);

            foreach (var file in selectedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outputPath = ResolveSafeOutputPath(targetDirectory, file.RelativePath);
                var conflict = conflictsByRelativePath.GetValueOrDefault(file.RelativePath, RestoreConflictKind.None);

                if (conflict != RestoreConflictKind.None && options.ConflictPolicy == RestoreConflictPolicy.Skip)
                {
                    outcome.FilesSkipped++;
                    outcome.SkippedRelativePaths.Add(file.RelativePath);
                    restoreTelemetry.ChunkReferencesProcessed += file.Chunks.Count;
                    restoreTelemetry.FileCountCompleted++;

                    progress?.Report(new RestoreProgress(
                        file.RelativePath,
                        restoreTelemetry.FileCountCompleted,
                        selectedFiles.Count,
                        outcome.BytesRestored,
                        totalBytes,
                        RestorePhase.Restoring));
                    continue;
                }

                if (conflict != RestoreConflictKind.None && options.ConflictPolicy == RestoreConflictPolicy.KeepBoth)
                {
                    outputPath = BuildKeepBothPath(outputPath);
                    outcome.FilesKeptBoth++;
                }
                else if (conflict != RestoreConflictKind.None)
                {
                    outcome.FilesOverwritten++;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                progress?.Report(new RestoreProgress(
                    file.RelativePath,
                    restoreTelemetry.FileCountCompleted,
                    selectedFiles.Count,
                    outcome.BytesRestored,
                    totalBytes,
                    RestorePhase.Restoring));

                // Written to a sibling temp file and moved into place on success, so a
                // cancellation or failure mid-file cannot leave a truncated file where the
                // user's real one was.
                var partialPath = outputPath + ".restorepartial";

                try
                {
                    await using (var outputStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                    {
                        foreach (var chunk in file.Chunks)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var chunkBytes = await LoadChunkBytesAsync(
                                chunk,
                                chunkStorageNamespace,
                                manifest.EncryptionEnabled,
                                encryptionMasterKey,
                                tempDirectory,
                                chunkCache,
                                restoreTelemetry,
                                cancellationToken);

                            restoreTelemetry.ChunkReferencesProcessed++;

                            await outputStream.WriteAsync(chunkBytes, cancellationToken);
                        }

                        await outputStream.FlushAsync(cancellationToken);
                    }

                    var fileHash = FileHasher.ComputeHash(partialPath);
                    if (!fileHash.Equals(file.ContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Restored file hash mismatch for '{file.RelativePath}'. Expected '{file.ContentHash}', actual '{fileHash}'.");
                    }

                    File.Move(partialPath, outputPath, overwrite: true);
                }
                catch
                {
                    TryDeleteTemporaryFile(partialPath);
                    throw;
                }

                outcome.FilesRestored++;
                outcome.BytesRestored += file.SizeBytes;
                restoreTelemetry.FileCountCompleted++;

                progress?.Report(new RestoreProgress(
                    file.RelativePath,
                    restoreTelemetry.FileCountCompleted,
                    selectedFiles.Count,
                    outcome.BytesRestored,
                    totalBytes,
                    RestorePhase.Restoring));
            }

            progress?.Report(new RestoreProgress(
                string.Empty,
                restoreTelemetry.FileCountCompleted,
                selectedFiles.Count,
                outcome.BytesRestored,
                totalBytes,
                RestorePhase.Finalising));

            _logger.Log("Restore completed successfully.", LogLevel.Info);
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: true);

            return outcome;
        }
        catch (OperationCanceledException)
        {
            // A cancellation is not a restore failure; recording it as one would poison the
            // failure-category telemetry the health panel reports on.
            _logger.Log("Restore cancelled by request.", LogLevel.Warning);
            throw;
        }
        catch (FileNotFoundException ex)
        {
            restoreTelemetry.FailureCategory = ClassifyRestoreFailure(ex);
            restoreTelemetry.FailureMessage = ex.Message;
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: false);

            _logger.Log($"Restore failed: required snapshot artifact was not found. {ex.Message}", LogLevel.Error);
            throw;
        }
        catch (CryptographicException ex)
        {
            _passwordProvider?.ClearPassword();

            restoreTelemetry.FailureCategory = ClassifyRestoreFailure(ex);
            restoreTelemetry.FailureMessage = ex.Message;
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: false);

            _logger.Log($"Restore failed: snapshot decryption integrity check failed. {ex.Message}", LogLevel.Error);
            throw;
        }
        catch (Exception ex)
        {
            restoreTelemetry.FailureCategory = ClassifyRestoreFailure(ex);
            restoreTelemetry.FailureMessage = ex.Message;
            LogRestoreTelemetry(resolvedManifestPath, loadedManifest, restoreTelemetry, wasSuccessful: false);

            _logger.Log($"Restore failed: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (Exception cleanupEx)
                {
                    _logger.Log($"Failed to clean up restore temporary directory {tempDirectory}: {cleanupEx.Message}", LogLevel.Warning);
                }
            }
        }
    }

    /// <summary>
    /// Reads the manifest and reports what a restore would write, without downloading any
    /// chunk objects or touching the target directory.
    /// </summary>
    public async Task<RestorePreview> PreviewRestoreAsync(
        string backupPath,
        string targetDirectory,
        IReadOnlySet<string>? relativePaths = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be null or empty", nameof(backupPath));
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Target directory cannot be null or empty", nameof(targetDirectory));
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"restore_preview_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDirectory);

            var loaded = await LoadManifestAsync(backupPath, tempDirectory, cancellationToken);
            var selectedFiles = SelectFiles(loaded.Manifest, relativePaths);

            return BuildPreview(
                backupPath,
                loaded.ManifestPath,
                loaded.Manifest,
                targetDirectory,
                selectedFiles,
                loaded.Manifest.Files.Count - selectedFiles.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (Exception cleanupEx)
                {
                    _logger.Log($"Failed to clean up preview temporary directory {tempDirectory}: {cleanupEx.Message}", LogLevel.Warning);
                }
            }
        }
    }

    private async Task<LoadedManifest> LoadManifestAsync(
        string backupPath,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        var tempManifestPath = Path.Combine(tempDirectory, "snapshot.manifest.json");
        string? expectedRootHashFromHead = null;

        var manifestPath = backupPath;
        if (backupPath.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
        {
            var headReference = await ResolveManifestPathFromHeadAsync(backupPath, tempDirectory, cancellationToken);
            manifestPath = headReference.ManifestPath;
            expectedRootHashFromHead = headReference.RootHash;
        }

        if (!manifestPath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"User-file restore requires a snapshot manifest path. Received unsupported artifact: {manifestPath}");
        }

        _logger.Log($"Downloading snapshot manifest: {manifestPath}", LogLevel.Debug);
        await _storage.DownloadAsync(manifestPath, tempManifestPath);

        cancellationToken.ThrowIfCancellationRequested();

        var manifestJson = await File.ReadAllTextAsync(tempManifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(manifestJson)
            ?? throw new InvalidOperationException($"Failed to deserialize snapshot manifest: {manifestPath}");

        if (!SnapshotManifestHasher.IsValid(manifest))
        {
            throw new InvalidOperationException($"Manifest integrity check failed for snapshot: {manifestPath}");
        }

        if (!string.IsNullOrWhiteSpace(expectedRootHashFromHead)
            && !expectedRootHashFromHead.Equals(manifest.RootHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Snapshot HEAD root hash mismatch for '{backupPath}'. Expected '{expectedRootHashFromHead}', actual '{manifest.RootHash}'.");
        }

        return new LoadedManifest(manifest, manifestPath);
    }

    private async Task<byte[]?> ResolveEncryptionMasterKeyAsync(SnapshotManifest manifest)
    {
        if (!manifest.EncryptionEnabled)
        {
            return null;
        }

        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encrypted snapshot detected but no password provider available");
        }

        var password = await _passwordProvider.GetPasswordAsync();
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password required to decrypt snapshot");
        }

        if (string.IsNullOrWhiteSpace(manifest.EncryptionSalt))
        {
            throw new InvalidOperationException("Snapshot manifest is encrypted but encryption salt is missing");
        }

        var saltBytes = Convert.FromBase64String(manifest.EncryptionSalt);
        return _encryptionService.DeriveKeyFromPassword(
            password,
            saltBytes,
            manifest.KeyDerivationIterations);
    }

    private static List<SnapshotFileManifestEntry> SelectFiles(
        SnapshotManifest manifest,
        IReadOnlySet<string>? relativePaths)
    {
        var ordered = manifest.Files.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);

        if (relativePaths == null)
        {
            return [.. ordered];
        }

        var normalizedSelection = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeManifestRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. ordered.Where(entry =>
            normalizedSelection.Contains(NormalizeManifestRelativePath(entry.RelativePath)))];
    }

    private static string NormalizeManifestRelativePath(string relativePath)
    {
        return relativePath
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private RestorePreview BuildPreview(
        string requestedBackupPath,
        string resolvedManifestPath,
        SnapshotManifest manifest,
        string targetDirectory,
        List<SnapshotFileManifestEntry> selectedFiles,
        int filesFilteredOut)
    {
        var entries = new List<RestorePreviewEntry>(selectedFiles.Count);

        foreach (var file in selectedFiles)
        {
            // Rejects traversal before anything is reported, so a hostile manifest cannot
            // have its escaping paths shown as if they were legitimate targets.
            var targetPath = ResolveSafeOutputPath(targetDirectory, file.RelativePath);

            var conflict = RestoreConflictKind.None;
            long? existingSize = null;
            DateTime? existingLastModified = null;

            var existing = new FileInfo(targetPath);
            if (existing.Exists)
            {
                existingSize = existing.Length;
                existingLastModified = existing.LastWriteTimeUtc;

                conflict = IsSameContent(existing, file)
                    ? RestoreConflictKind.Identical
                    : RestoreConflictKind.Differs;
            }

            entries.Add(new RestorePreviewEntry
            {
                RelativePath = file.RelativePath,
                TargetPath = targetPath,
                SizeBytes = file.SizeBytes,
                LastModifiedUtc = file.LastModifiedUtc,
                ContentHash = file.ContentHash,
                Conflict = conflict,
                ExistingSizeBytes = existingSize,
                ExistingLastModifiedUtc = existingLastModified
            });
        }

        return new RestorePreview
        {
            RequestedBackupPath = requestedBackupPath,
            ResolvedManifestPath = resolvedManifestPath,
            SnapshotId = manifest.SnapshotId,
            SnapshotCreatedUtc = manifest.CreatedUtc,
            TargetDirectory = Path.GetFullPath(targetDirectory),
            EncryptionEnabled = manifest.EncryptionEnabled,
            Entries = entries,
            FilesFilteredOut = Math.Max(0, filesFilteredOut)
        };
    }

    private static bool IsSameContent(FileInfo existing, SnapshotFileManifestEntry file)
    {
        if (existing.Length != file.SizeBytes)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(file.ContentHash))
        {
            return false;
        }

        try
        {
            return FileHasher.ComputeHash(existing.FullName)
                .Equals(file.ContentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BuildKeepBothPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);

        var candidate = Path.Combine(directory, $"{fileName} (restored){extension}");
        var suffix = 2;

        // Repeat runs must not collide with the copy an earlier run already wrote.
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{fileName} (restored {suffix}){extension}");
            suffix++;
        }

        return candidate;
    }

    private async Task<SnapshotHeadReference> ResolveManifestPathFromHeadAsync(
        string headPath,
        string tempDirectory,
        CancellationToken cancellationToken = default)
    {
        var tempHeadPath = Path.Combine(tempDirectory, "snapshot.head");
        await _storage.DownloadAsync(headPath, tempHeadPath);

        var lines = await File.ReadAllLinesAsync(tempHeadPath, cancellationToken);
        var nonEmptyLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList();

        var manifestPath = nonEmptyLines.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException($"Snapshot HEAD did not contain a manifest path: {headPath}");
        }

        var rootHash = nonEmptyLines.Count > 1 ? nonEmptyLines[1] : null;
        return new SnapshotHeadReference(manifestPath, string.IsNullOrWhiteSpace(rootHash) ? null : rootHash);
    }

    private async Task<byte[]> LoadChunkBytesAsync(
        SnapshotChunkManifestEntry chunk,
        string? chunkStorageNamespace,
        bool encrypted,
        byte[]? encryptionMasterKey,
        string tempDirectory,
        ChunkByteCache chunkCache,
        RestoreTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        var normalizedChunkId = SnapshotStoragePaths.NormalizeChunkId(chunk.ChunkId);

        if (chunkCache.TryGetValue(normalizedChunkId, out var cachedBytes))
        {
            telemetry.ChunkCacheHits++;
            return cachedBytes;
        }

        var chunkRemotePath = SnapshotStoragePaths.GetChunkPath(normalizedChunkId, chunkStorageNamespace);
        var chunkTempPath = Path.Combine(tempDirectory, $"{normalizedChunkId}.chunk");
        await _storage.DownloadAsync(chunkRemotePath, chunkTempPath);
        telemetry.ChunkDownloads++;

        var storedBytes = await File.ReadAllBytesAsync(chunkTempPath, cancellationToken);
        var plaintextBytes = encrypted
            ? EncryptionService.DecryptChunkDeterministic(storedBytes, encryptionMasterKey!, normalizedChunkId)
            : storedBytes;

        if (plaintextBytes.Length != chunk.PlainSizeBytes)
        {
            throw new InvalidOperationException(
                $"Chunk size mismatch for chunk '{normalizedChunkId}'. Expected {chunk.PlainSizeBytes}, actual {plaintextBytes.Length}.");
        }

        var actualChunkHash = Convert.ToHexStringLower(SHA256.HashData(plaintextBytes));
        if (!actualChunkHash.Equals(chunk.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Chunk hash mismatch for chunk '{normalizedChunkId}'. Expected '{chunk.ContentHash}', actual '{actualChunkHash}'.");
        }

        chunkCache.Set(normalizedChunkId, plaintextBytes);
        return plaintextBytes;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort; a leftover .restorepartial is inert.
        }
    }

    private sealed record LoadedManifest(SnapshotManifest Manifest, string ManifestPath);

    private sealed class ChunkByteCache(long maxBytes)
    {
        private readonly long _maxBytes = Math.Max(0, maxBytes);
        private readonly Dictionary<string, LinkedListNode<ChunkCacheEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<ChunkCacheEntry> _lru = [];
        private long _currentBytes;

        public bool TryGetValue(string chunkId, out byte[] bytes)
        {
            if (_entries.TryGetValue(chunkId, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }

            bytes = [];
            return false;
        }

        public void Set(string chunkId, byte[] bytes)
        {
            if (_maxBytes == 0 || bytes.LongLength > _maxBytes)
            {
                return;
            }

            if (_entries.TryGetValue(chunkId, out var existing))
            {
                _currentBytes -= existing.Value.SizeBytes;
                _lru.Remove(existing);
                _entries.Remove(chunkId);
            }

            var entry = new ChunkCacheEntry(chunkId, bytes);
            var node = new LinkedListNode<ChunkCacheEntry>(entry);
            _lru.AddFirst(node);
            _entries[chunkId] = node;
            _currentBytes += entry.SizeBytes;

            Trim();
        }

        private void Trim()
        {
            while (_currentBytes > _maxBytes && _lru.Last != null)
            {
                var node = _lru.Last;
                _lru.RemoveLast();
                _entries.Remove(node.Value.ChunkId);
                _currentBytes -= node.Value.SizeBytes;
            }
        }
    }

    private sealed class ChunkCacheEntry(string chunkId, byte[] bytes)
    {
        public string ChunkId { get; } = chunkId;
        public byte[] Bytes { get; } = bytes;
        public long SizeBytes { get; } = bytes.LongLength;
    }

    private static string ResolveSafeOutputPath(string targetDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Snapshot manifest contains an empty relative file path.");
        }

        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelativePath))
        {
            throw new InvalidOperationException(
                $"Snapshot manifest path '{relativePath}' must be relative to the restore target directory.");
        }

        var normalizedTargetDirectory = Path.GetFullPath(targetDirectory);
        var combinedPath = Path.GetFullPath(Path.Combine(normalizedTargetDirectory, normalizedRelativePath));
        var normalizedTargetPrefix = normalizedTargetDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedTargetDirectory
            : normalizedTargetDirectory + Path.DirectorySeparatorChar;

        if (!combinedPath.StartsWith(normalizedTargetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Snapshot manifest path '{relativePath}' resolves outside the restore target directory.");
        }

        return combinedPath;
    }

    private static string ClassifyRestoreFailure(Exception exception)
    {
        if (exception is FileNotFoundException)
        {
            return "missing-artifact";
        }

        if (exception is CryptographicException)
        {
            return "decryption-integrity-failure";
        }

        if (exception is InvalidOperationException invalidOperationException)
        {
            if (invalidOperationException.Message.Contains("Manifest integrity check failed", StringComparison.OrdinalIgnoreCase))
            {
                return "manifest-integrity-failure";
            }

            if (invalidOperationException.Message.Contains("HEAD root hash mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return "manifest-integrity-failure";
            }

            if (invalidOperationException.Message.Contains("Chunk hash mismatch", StringComparison.OrdinalIgnoreCase)
                || invalidOperationException.Message.Contains("Chunk size mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return "chunk-validation-failure";
            }

            if (invalidOperationException.Message.Contains("Restored file hash mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return "file-validation-failure";
            }

            if (invalidOperationException.Message.Contains("would replace", StringComparison.OrdinalIgnoreCase))
            {
                return "restore-conflict";
            }
        }

        return "unexpected-error";
    }

    private void LogRestoreTelemetry(
        string manifestPath,
        SnapshotManifest? manifest,
        RestoreTelemetry telemetry,
        bool wasSuccessful)
    {
        var cacheHitRatio = telemetry.ChunkReferencesProcessed == 0
            ? 0
            : (double)telemetry.ChunkCacheHits / telemetry.ChunkReferencesProcessed;

        var validationFailures = wasSuccessful ? 0 : 1;
        var snapshotId = manifest?.SnapshotId ?? "unknown";

        _logger.Log(
            $"Restore telemetry: manifest='{manifestPath}', snapshot='{snapshotId}', success={wasSuccessful}, filesExpected={telemetry.FileCountExpected}, filesRestored={telemetry.FileCountCompleted}, chunkRefsExpected={telemetry.ChunkReferencesExpected}, chunkRefsProcessed={telemetry.ChunkReferencesProcessed}, chunkDownloads={telemetry.ChunkDownloads}, chunkCacheHits={telemetry.ChunkCacheHits}, chunkCacheHitRatio={cacheHitRatio:P2}, validationFailures={validationFailures}, failureCategory='{telemetry.FailureCategory ?? "none"}'",
            wasSuccessful ? LogLevel.Info : LogLevel.Warning);

        _systemState?.RecordRestoreTelemetry(
            success: wasSuccessful,
            filesExpected: telemetry.FileCountExpected,
            filesRestored: telemetry.FileCountCompleted,
            chunkReferencesExpected: telemetry.ChunkReferencesExpected,
            chunkReferencesProcessed: telemetry.ChunkReferencesProcessed,
            chunkDownloads: telemetry.ChunkDownloads,
            chunkCacheHits: telemetry.ChunkCacheHits,
            failureCategory: telemetry.FailureCategory,
            validationFailures: validationFailures);
    }

    private sealed class RestoreTelemetry
    {
        public int FileCountExpected { get; set; }
        public int FileCountCompleted { get; set; }
        public int ChunkReferencesExpected { get; set; }
        public int ChunkReferencesProcessed { get; set; }
        public int ChunkDownloads { get; set; }
        public int ChunkCacheHits { get; set; }
        public string? FailureCategory { get; set; }
        public string? FailureMessage { get; set; }
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
}
