using System.Security.Cryptography;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.backup;

public class ChunkBuildPayload
{
    public string ChunkId { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int PlainSizeBytes { get; set; }
    public byte[] StoredPayload { get; set; } = [];
}

public class ChunkedFileBuildResult
{
    public SnapshotFileManifestEntry FileEntry { get; set; } = new();
    public List<ChunkBuildPayload> ChunkPayloads { get; set; } = [];
}

public class ChunkingService
{
    private static readonly ulong[] GEAR_TABLE = BuildGearTable();

    private readonly ILogger _logger;
    private readonly ChunkDiffingConfig _chunkConfig;
    private readonly ChunkingProfile _profile;
    private readonly EncryptionService _encryptionService;
    private readonly bool _encryptionEnabled;
    private readonly byte[]? _encryptionMasterKey;

    public ChunkingService(
        ILogger logger,
        ChunkDiffingConfig chunkConfig,
        EncryptionService encryptionService,
        bool encryptionEnabled,
        byte[]? encryptionMasterKey)
    {
        _logger = logger;
        _chunkConfig = chunkConfig;
        _profile = ChunkingProfile.FromConfig(chunkConfig);
        _encryptionService = encryptionService;
        _encryptionEnabled = encryptionEnabled;
        _encryptionMasterKey = encryptionMasterKey;

        if (_encryptionEnabled && (_encryptionMasterKey == null || _encryptionMasterKey.Length == 0))
        {
            throw new ArgumentException("Encryption master key is required when chunk encryption is enabled", nameof(encryptionMasterKey));
        }
    }

    public Task<ChunkedFileBuildResult> BuildFileManifestEntryAsync(string filePath, string baseDirectory, CancellationToken cancellationToken = default)
    {
        return BuildFileManifestEntryAsync(filePath, baseDirectory, null, cancellationToken);
    }

    /// <summary>
    /// Chunks a file and builds its manifest entry.
    /// </summary>
    /// <param name="chunkSink">
    /// Invoked once per chunk as it is produced. When supplied, payload bytes are handed to the
    /// sink and not retained, so peak memory stays at roughly one chunk rather than the whole
    /// file. When null the payloads are collected into
    /// <see cref="ChunkedFileBuildResult.ChunkPayloads"/> instead.
    /// </param>
    public async Task<ChunkedFileBuildResult> BuildFileManifestEntryAsync(
        string filePath,
        string baseDirectory,
        Func<ChunkBuildPayload, CancellationToken, Task>? chunkSink,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Cannot chunk missing file: {filePath}", filePath);
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory cannot be null or empty", nameof(baseDirectory));
        }

        var fileInfo = new FileInfo(filePath);
        var fileHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunkPayloads = new List<ChunkBuildPayload>();
        var chunkEntries = new List<SnapshotChunkManifestEntry>();

        using var chunkStream = new MemoryStream(_profile.MaxChunkSizeBytes);
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 128 * 1024, useAsync: true);

        var readBuffer = new byte[128 * 1024];
        var currentChunkSize = 0;
        var rollingHash = new RollingWindowHash(_profile.RollingHashWindowSize, GEAR_TABLE);

        while (true)
        {
            var bytesRead = await fileStream.ReadAsync(readBuffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            // Once per read buffer, not per byte: a per-byte check dominates this loop without
            // making cancellation meaningfully more responsive.
            cancellationToken.ThrowIfCancellationRequested();

            fileHasher.AppendData(readBuffer.AsSpan(0, bytesRead));

            for (var index = 0; index < bytesRead; index++)
            {
                var currentByte = readBuffer[index];
                chunkStream.WriteByte(currentByte);
                currentChunkSize++;

                rollingHash.Add(currentByte);

                if (!ShouldCutChunk(rollingHash.Value, currentChunkSize))
                {
                    continue;
                }

                await AppendChunkPayloadAsync(chunkStream, chunkEntries, chunkPayloads, chunkSink, cancellationToken);
                if (chunkEntries.Count > _chunkConfig.MaxChunksPerFile)
                {
                    throw new InvalidOperationException($"File exceeds maxChunksPerFile safety limit ({_chunkConfig.MaxChunksPerFile}): {filePath}");
                }

                currentChunkSize = 0;
                rollingHash.Reset();
            }
        }

        if (chunkStream.Length > 0)
        {
            await AppendChunkPayloadAsync(chunkStream, chunkEntries, chunkPayloads, chunkSink, cancellationToken);
        }

        if (chunkEntries.Count > _chunkConfig.MaxChunksPerFile)
        {
            throw new InvalidOperationException($"File exceeds maxChunksPerFile safety limit ({_chunkConfig.MaxChunksPerFile}): {filePath}");
        }

        var relativePath = Path.GetRelativePath(baseDirectory, filePath)
            .Replace(Path.DirectorySeparatorChar, '/');

        var fileHash = Convert.ToHexStringLower(fileHasher.GetHashAndReset());
        var fileEntry = new SnapshotFileManifestEntry
        {
            RelativePath = relativePath,
            SizeBytes = fileInfo.Length,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
            ContentHash = fileHash,
            Chunks = chunkEntries
        };

        _logger.Log($"Chunked file '{relativePath}' into {chunkEntries.Count} chunk(s)", LogLevel.Debug);

        return new ChunkedFileBuildResult
        {
            FileEntry = fileEntry,
            ChunkPayloads = chunkPayloads
        };
    }

    private bool ShouldCutChunk(ulong rollingHash, int currentChunkSize)
    {
        if (currentChunkSize < _profile.MinChunkSizeBytes)
        {
            return false;
        }

        if (currentChunkSize >= _profile.MaxChunkSizeBytes)
        {
            return true;
        }

        if (_profile.TargetChunkSizeBytes <= 1)
        {
            return true;
        }

        var targetSize = (ulong)_profile.TargetChunkSizeBytes;
        return rollingHash % targetSize == targetSize - 1;
    }

    private async Task AppendChunkPayloadAsync(
        MemoryStream chunkStream,
        List<SnapshotChunkManifestEntry> chunkEntries,
        List<ChunkBuildPayload> chunkPayloads,
        Func<ChunkBuildPayload, CancellationToken, Task>? chunkSink,
        CancellationToken cancellationToken)
    {
        var plaintext = chunkStream.ToArray();
        if (plaintext.Length == 0)
        {
            chunkStream.SetLength(0);
            chunkStream.Position = 0;
            return;
        }

        var chunkHash = Convert.ToHexStringLower(SHA256.HashData(plaintext));
        var storedPayload = _encryptionEnabled
            ? EncryptionService.EncryptChunkDeterministic(plaintext, _encryptionMasterKey!, chunkHash)
            : plaintext;

        chunkEntries.Add(new SnapshotChunkManifestEntry
        {
            ChunkId = chunkHash,
            ContentHash = chunkHash,
            PlainSizeBytes = plaintext.Length,
            StoredSizeBytes = storedPayload.Length
        });

        var payload = new ChunkBuildPayload
        {
            ChunkId = chunkHash,
            ContentHash = chunkHash,
            PlainSizeBytes = plaintext.Length,
            StoredPayload = storedPayload
        };

        if (chunkSink != null)
        {
            await chunkSink(payload, cancellationToken);
        }
        else
        {
            chunkPayloads.Add(payload);
        }

        chunkStream.SetLength(0);
        chunkStream.Position = 0;
    }

    private static ulong[] BuildGearTable()
    {
        var table = new ulong[256];
        ulong seed = 0x9E3779B185EBCA87UL;
        for (var index = 0; index < table.Length; index++)
        {
            seed ^= seed >> 12;
            seed ^= seed << 25;
            seed ^= seed >> 27;
            table[index] = seed * 0x2545F4914F6CDD1DUL;
        }

        return table;
    }

    private sealed class RollingWindowHash
    {
        private const ulong HashBase = 257;

        private readonly int _windowSize;
        private readonly ulong[] _gearTable;
        private readonly byte[] _window;
        private readonly ulong _oldestByteMultiplier;
        private int _position;
        private int _count;

        public RollingWindowHash(int windowSize, ulong[] gearTable)
        {
            _windowSize = Math.Max(0, windowSize);
            _gearTable = gearTable;
            _window = _windowSize == 0 ? [] : new byte[_windowSize];
            _oldestByteMultiplier = ComputeOldestByteMultiplier(_windowSize);
        }

        public ulong Value { get; private set; }

        public void Add(byte value)
        {
            var contribution = _gearTable[value];

            unchecked
            {
                if (_windowSize == 0)
                {
                    Value = (Value * HashBase) + contribution;
                    return;
                }

                if (_count < _windowSize)
                {
                    _window[_position] = value;
                    _position = (_position + 1) % _windowSize;
                    _count++;
                    Value = (Value * HashBase) + contribution;
                    return;
                }

                var removed = _window[_position];
                _window[_position] = value;
                _position = (_position + 1) % _windowSize;

                Value = ((Value - (_gearTable[removed] * _oldestByteMultiplier)) * HashBase) + contribution;
            }
        }

        public void Reset()
        {
            Value = 0;
            _position = 0;
            _count = 0;
        }

        private static ulong ComputeOldestByteMultiplier(int windowSize)
        {
            if (windowSize <= 1)
            {
                return 1;
            }

            ulong multiplier = 1;
            unchecked
            {
                for (var index = 1; index < windowSize; index++)
                {
                    multiplier *= HashBase;
                }
            }

            return multiplier;
        }
    }
}
