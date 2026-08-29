using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReStore.Core.src.core;

public class SnapshotVerificationResult
{
    public string RequestedBackupPath { get; set; } = string.Empty;
    public string ResolvedManifestPath { get; set; } = string.Empty;
    public string SnapshotId { get; set; } = string.Empty;
    public bool EncryptionEnabled { get; set; }
    public bool ManifestHashValid { get; set; }
    public int FileCount { get; set; }
    public int ChunkReferences { get; set; }
    public int UniqueChunks { get; set; }
    public int ChunksDownloaded { get; set; }
    public int MissingChunks { get; set; }
    public int InvalidChunks { get; set; }
    public int InvalidFiles { get; set; }
    public List<string> Errors { get; } = [];

    public bool IsValid => Errors.Count == 0;
}

public class SnapshotIntegrityVerifier(ILogger logger, IStorage storage, IPasswordProvider? passwordProvider = null, SystemState? systemState = null)
{
    // Matches the bound Restore uses, so both paths have the same footprint.
    private const long MaxChunkCacheBytes = 64L * 1024 * 1024;

    private readonly ILogger _logger = logger;
    private readonly IStorage _storage = storage;
    private readonly IPasswordProvider? _passwordProvider = passwordProvider;
    private readonly SystemState? _systemState = systemState;
    private readonly EncryptionService _encryptionService = new(logger);

    public async Task<SnapshotVerificationResult> VerifyAsync(
        string backupPath,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be null or empty", nameof(backupPath));
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"restore_verify_{Guid.NewGuid():N}");
        var result = new SnapshotVerificationResult
        {
            RequestedBackupPath = backupPath
        };

        try
        {
            Directory.CreateDirectory(tempDirectory);

            var manifestPath = backupPath;
            string? expectedRootHashFromHead = null;
            if (backupPath.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
            {
                var headReference = await ResolveManifestPathFromHeadAsync(backupPath, tempDirectory, cancellationToken);
                manifestPath = headReference.ManifestPath;
                expectedRootHashFromHead = headReference.RootHash;
            }

            if (!manifestPath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Snapshot verification requires a manifest path or HEAD reference. Received unsupported artifact: {manifestPath}");
            }

            result.ResolvedManifestPath = manifestPath;

            var manifest = await DownloadManifestAsync(manifestPath, tempDirectory, cancellationToken);
            result.SnapshotId = manifest.SnapshotId;
            result.EncryptionEnabled = manifest.EncryptionEnabled;
            result.FileCount = manifest.Files.Count;
            result.ChunkReferences = manifest.Files.Sum(file => file.Chunks.Count);

            result.ManifestHashValid = SnapshotManifestHasher.IsValid(manifest);
            if (!result.ManifestHashValid)
            {
                result.Errors.Add($"Manifest integrity check failed for snapshot: {manifestPath}");
            }

            if (!string.IsNullOrWhiteSpace(expectedRootHashFromHead)
                && !expectedRootHashFromHead.Equals(manifest.RootHash, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add(
                    $"Snapshot HEAD root hash mismatch for '{backupPath}'. Expected '{expectedRootHashFromHead}', actual '{manifest.RootHash}'.");
            }

            string? chunkStorageNamespace;
            try
            {
                chunkStorageNamespace = SnapshotStoragePaths.NormalizeChunkStorageNamespace(manifest.ChunkStorageNamespace);
            }
            catch (ArgumentException ex)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Invalid chunk storage namespace '{manifest.ChunkStorageNamespace}': {ex.Message}");
                LogVerificationTelemetry(result);
                return result;
            }

            var encryptionMasterKey = await TryResolveEncryptionMasterKeyAsync(manifest);
            var uniqueChunkDescriptors = BuildUniqueChunkDescriptors(manifest, result);
            result.UniqueChunks = uniqueChunkDescriptors.Count;

            var verifiedChunkIds = await VerifyChunksAsync(
                uniqueChunkDescriptors,
                chunkStorageNamespace,
                manifest.EncryptionEnabled,
                encryptionMasterKey,
                tempDirectory,
                result,
                progress,
                cancellationToken);

            await VerifyFilesAsync(
                manifest,
                verifiedChunkIds,
                chunkStorageNamespace,
                encryptionMasterKey,
                tempDirectory,
                result,
                progress,
                cancellationToken);

            LogVerificationTelemetry(result);

            return result;
        }
        finally
        {
            TryDeleteTemporaryDirectory(tempDirectory);
        }
    }

    private async Task<SnapshotManifest> DownloadManifestAsync(
        string manifestPath,
        string tempDirectory,
        CancellationToken cancellationToken = default)
    {
        var tempManifestPath = Path.Combine(tempDirectory, "snapshot.manifest.json");
        await _storage.DownloadAsync(manifestPath, tempManifestPath);

        var manifestJson = await File.ReadAllTextAsync(tempManifestPath, cancellationToken);
        return JsonSerializer.Deserialize<SnapshotManifest>(manifestJson)
            ?? throw new InvalidOperationException($"Failed to deserialize snapshot manifest: {manifestPath}");
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

    private async Task<byte[]?> TryResolveEncryptionMasterKeyAsync(SnapshotManifest manifest)
    {
        if (!manifest.EncryptionEnabled)
        {
            return null;
        }

        if (_passwordProvider == null)
        {
            throw new InvalidOperationException("Encrypted snapshot verification requires a password provider");
        }

        var password = await _passwordProvider.GetPasswordAsync();
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password required to verify encrypted snapshot");
        }

        if (string.IsNullOrWhiteSpace(manifest.EncryptionSalt))
        {
            throw new InvalidOperationException("Snapshot manifest is encrypted but encryption salt is missing");
        }

        var saltBytes = Convert.FromBase64String(manifest.EncryptionSalt);
        return _encryptionService.DeriveKeyFromPassword(password, saltBytes, manifest.KeyDerivationIterations);
    }

    private static Dictionary<string, ChunkDescriptor> BuildUniqueChunkDescriptors(
        SnapshotManifest manifest,
        SnapshotVerificationResult result)
    {
        var descriptors = new Dictionary<string, ChunkDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in manifest.Files.SelectMany(file => file.Chunks))
        {
            string normalizedChunkId;
            try
            {
                normalizedChunkId = SnapshotStoragePaths.NormalizeChunkId(chunk.ChunkId);
            }
            catch (ArgumentException ex)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Invalid chunk id '{chunk.ChunkId}': {ex.Message}");
                continue;
            }

            var descriptor = new ChunkDescriptor(normalizedChunkId, chunk.ContentHash, chunk.PlainSizeBytes);
            if (!descriptors.TryAdd(descriptor.ChunkId, descriptor))
            {
                var existing = descriptors[descriptor.ChunkId];
                if (!existing.ContentHash.Equals(descriptor.ContentHash, StringComparison.OrdinalIgnoreCase)
                    || existing.PlainSizeBytes != descriptor.PlainSizeBytes)
                {
                    result.InvalidChunks++;
                    result.Errors.Add($"Inconsistent manifest metadata for chunk '{descriptor.ChunkId}'.");
                }
            }
        }

        return descriptors;
    }

    private async Task<HashSet<string>> VerifyChunksAsync(
        IReadOnlyDictionary<string, ChunkDescriptor> chunkDescriptors,
        string? chunkStorageNamespace,
        bool encrypted,
        byte[]? encryptionMasterKey,
        string tempDirectory,
        SnapshotVerificationResult result,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var verifiedChunkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chunksHandled = 0;

        foreach (var descriptor in chunkDescriptors.Values.OrderBy(value => value.ChunkId, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new VerificationProgress(
                descriptor.ChunkId,
                chunksHandled,
                chunkDescriptors.Count,
                "chunks"));

            var remoteChunkPath = SnapshotStoragePaths.GetChunkPath(descriptor.ChunkId, chunkStorageNamespace);
            var tempChunkPath = Path.Combine(tempDirectory, $"{descriptor.ChunkId}.chunk");

            try
            {
                await _storage.DownloadAsync(remoteChunkPath, tempChunkPath);
                result.ChunksDownloaded++;
            }
            catch (FileNotFoundException)
            {
                result.MissingChunks++;
                result.Errors.Add($"Missing chunk object: {remoteChunkPath}");
                chunksHandled++;
                continue;
            }
            catch (Exception ex)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Failed to download chunk '{descriptor.ChunkId}': {ex.Message}");
                chunksHandled++;
                continue;
            }

            try
            {
                var storedBytes = await File.ReadAllBytesAsync(tempChunkPath, cancellationToken);
                var plainBytes = encrypted
                    ? EncryptionService.DecryptChunkDeterministic(storedBytes, encryptionMasterKey!, descriptor.ChunkId)
                    : storedBytes;

                if (plainBytes.Length != descriptor.PlainSizeBytes)
                {
                    result.InvalidChunks++;
                    result.Errors.Add(
                        $"Chunk size mismatch for '{descriptor.ChunkId}'. Expected {descriptor.PlainSizeBytes}, actual {plainBytes.Length}.");
                    continue;
                }

                var chunkHash = Convert.ToHexStringLower(SHA256.HashData(plainBytes));
                if (!chunkHash.Equals(descriptor.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    result.InvalidChunks++;
                    result.Errors.Add(
                        $"Chunk hash mismatch for '{descriptor.ChunkId}'. Expected '{descriptor.ContentHash}', actual '{chunkHash}'.");
                    continue;
                }

                // Not retained: the per-file pass re-fetches what it needs, keeping peak
                // temp usage at one chunk instead of the whole unique-chunk set.
                verifiedChunkIds.Add(descriptor.ChunkId);
            }
            catch (CryptographicException ex)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Failed to decrypt chunk '{descriptor.ChunkId}': {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.InvalidChunks++;
                result.Errors.Add($"Failed to validate chunk '{descriptor.ChunkId}': {ex.Message}");
            }
            finally
            {
                TryDeleteTemporaryFile(tempChunkPath);
                chunksHandled++;
            }
        }

        return verifiedChunkIds;
    }

    /// <summary>
    /// Re-hashes each file from its chunks, fetched on demand through a size-bounded LRU.
    /// </summary>
    private async Task VerifyFilesAsync(
        SnapshotManifest manifest,
        IReadOnlySet<string> verifiedChunkIds,
        string? chunkStorageNamespace,
        byte[]? encryptionMasterKey,
        string tempDirectory,
        SnapshotVerificationResult result,
        IProgress<VerificationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var chunkCache = new ChunkByteCache(MaxChunkCacheBytes);
        var filesHandled = 0;

        foreach (var file in manifest.Files.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new VerificationProgress(
                file.RelativePath,
                filesHandled,
                manifest.Files.Count,
                "files"));

            using var fileHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long reconstructedSize = 0;
            bool hasMissingChunk = false;

            foreach (var chunk in file.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string normalizedChunkId;
                try
                {
                    normalizedChunkId = SnapshotStoragePaths.NormalizeChunkId(chunk.ChunkId);
                }
                catch (ArgumentException ex)
                {
                    hasMissingChunk = true;
                    result.Errors.Add($"File '{file.RelativePath}' references invalid chunk id '{chunk.ChunkId}': {ex.Message}");
                    continue;
                }

                // Already reported by the chunk pass; don't re-download a known-bad object.
                if (!verifiedChunkIds.Contains(normalizedChunkId))
                {
                    hasMissingChunk = true;
                    continue;
                }

                byte[] chunkBytes;
                try
                {
                    chunkBytes = await LoadPlaintextChunkAsync(
                        normalizedChunkId,
                        chunkStorageNamespace,
                        manifest.EncryptionEnabled,
                        encryptionMasterKey,
                        tempDirectory,
                        chunkCache,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    hasMissingChunk = true;
                    result.Errors.Add(
                        $"File '{file.RelativePath}' could not read chunk '{normalizedChunkId}': {ex.Message}");
                    continue;
                }

                fileHasher.AppendData(chunkBytes);
                reconstructedSize += chunkBytes.Length;
            }

            filesHandled++;

            if (hasMissingChunk)
            {
                result.InvalidFiles++;
                result.Errors.Add(
                    $"File '{file.RelativePath}' could not be reconstructed because one or more chunks are missing or invalid.");
                continue;
            }

            var fileHash = Convert.ToHexStringLower(fileHasher.GetHashAndReset());
            if (!fileHash.Equals(file.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                result.InvalidFiles++;
                result.Errors.Add(
                    $"File hash mismatch for '{file.RelativePath}'. Expected '{file.ContentHash}', actual '{fileHash}'.");
            }

            if (reconstructedSize != file.SizeBytes)
            {
                result.InvalidFiles++;
                result.Errors.Add(
                    $"File size mismatch for '{file.RelativePath}'. Expected {file.SizeBytes}, actual {reconstructedSize}.");
            }
        }
    }

    private async Task<byte[]> LoadPlaintextChunkAsync(
        string normalizedChunkId,
        string? chunkStorageNamespace,
        bool encrypted,
        byte[]? encryptionMasterKey,
        string tempDirectory,
        ChunkByteCache chunkCache,
        CancellationToken cancellationToken = default)
    {
        if (chunkCache.TryGetValue(normalizedChunkId, out var cached))
        {
            return cached;
        }

        var remoteChunkPath = SnapshotStoragePaths.GetChunkPath(normalizedChunkId, chunkStorageNamespace);
        var tempChunkPath = Path.Combine(tempDirectory, $"{normalizedChunkId}.verify.chunk");

        try
        {
            await _storage.DownloadAsync(remoteChunkPath, tempChunkPath);

            var storedBytes = await File.ReadAllBytesAsync(tempChunkPath, cancellationToken);
            var plainBytes = encrypted
                ? EncryptionService.DecryptChunkDeterministic(storedBytes, encryptionMasterKey!, normalizedChunkId)
                : storedBytes;

            chunkCache.Set(normalizedChunkId, plainBytes);
            return plainBytes;
        }
        finally
        {
            TryDeleteTemporaryFile(tempChunkPath);
        }
    }

    /// <summary>Size-bounded LRU over plaintext chunk bytes.</summary>
    private sealed class ChunkByteCache(long maxBytes)
    {
        private readonly long _maxBytes = Math.Max(0, maxBytes);
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CacheEntry> _lru = [];
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

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(chunkId, bytes));
            _lru.AddFirst(node);
            _entries[chunkId] = node;
            _currentBytes += node.Value.SizeBytes;

            while (_currentBytes > _maxBytes && _lru.Last != null)
            {
                var evicted = _lru.Last;
                _lru.RemoveLast();
                _entries.Remove(evicted.Value.ChunkId);
                _currentBytes -= evicted.Value.SizeBytes;
            }
        }

        private sealed class CacheEntry(string chunkId, byte[] bytes)
        {
            public string ChunkId { get; } = chunkId;
            public byte[] Bytes { get; } = bytes;
            public long SizeBytes { get; } = bytes.LongLength;
        }
    }

    private void LogVerificationTelemetry(SnapshotVerificationResult result)
    {
        var verifiedChunks = Math.Max(0, result.UniqueChunks - result.MissingChunks - result.InvalidChunks);
        var chunkVerificationRatio = result.UniqueChunks == 0 ? 0 : (double)verifiedChunks / result.UniqueChunks;
        var failureCount = result.Errors.Count;

        _logger.Log(
            $"Verification telemetry: path='{result.ResolvedManifestPath}', snapshot='{result.SnapshotId}', success={result.IsValid}, files={result.FileCount}, chunkRefs={result.ChunkReferences}, uniqueChunks={result.UniqueChunks}, downloadedChunks={result.ChunksDownloaded}, missingChunks={result.MissingChunks}, invalidChunks={result.InvalidChunks}, invalidFiles={result.InvalidFiles}, verifiedChunkRatio={chunkVerificationRatio:P2}, validationFailures={failureCount}",
            result.IsValid ? LogLevel.Info : LogLevel.Warning);

        _systemState?.RecordVerificationTelemetry(
            success: result.IsValid,
            fileCount: result.FileCount,
            chunkReferences: result.ChunkReferences,
            uniqueChunks: result.UniqueChunks,
            downloadedChunks: result.ChunksDownloaded,
            missingChunks: result.MissingChunks,
            invalidChunks: result.InvalidChunks,
            invalidFiles: result.InvalidFiles,
            validationFailures: failureCount);
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        // Runs in a finally on the per-chunk path. A transient lock (indexer, AV scanner) must
        // not turn a passing verification into a thrown exception.
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

    private static void TryDeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record ChunkDescriptor(string ChunkId, string ContentHash, int PlainSizeBytes);

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
