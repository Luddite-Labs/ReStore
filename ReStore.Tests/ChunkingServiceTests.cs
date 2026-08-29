using FluentAssertions;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Pins chunk boundaries. Chunk ids are content hashes used for dedup, so moving
/// boundaries means nothing in a user's remote storage can be reused. A failure here is
/// the intended signal; re-baseline only if you accept that re-upload cost.
/// </summary>
public class ChunkingServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger = new();

    public ChunkingServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreChunkingTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    private static ChunkDiffingConfig SmallChunkConfig() => new()
    {
        // Sub-KB sizes keep the fixtures small while still exercising several cuts.
        MinChunkSizeKB = 1,
        TargetChunkSizeKB = 2,
        MaxChunkSizeKB = 8,
        RollingHashWindowSize = 32
    };

    private ChunkingService CreateService(ChunkDiffingConfig? config = null)
    {
        return new ChunkingService(
            _logger,
            config ?? SmallChunkConfig(),
            new EncryptionService(_logger),
            encryptionEnabled: false,
            encryptionMasterKey: null);
    }

    /// <summary>Avoids depending on Random's internals across runtimes.</summary>
    private static byte[] DeterministicBytes(int length, uint seed)
    {
        var buffer = new byte[length];
        var state = seed;
        for (var index = 0; index < length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            buffer[index] = (byte)(state & 0xFF);
        }

        return buffer;
    }

    private async Task<string> WriteFixtureAsync(string name, byte[] contents)
    {
        var path = Path.Combine(_testRoot, name);
        await File.WriteAllBytesAsync(path, contents);
        return path;
    }

    [Fact]
    public async Task BuildFileManifestEntryAsync_ShouldProduceStableChunkBoundaries_ForKnownInput()
    {
        var filePath = await WriteFixtureAsync("golden.bin", DeterministicBytes(64 * 1024, seed: 0x5EED_1234));

        var result = await CreateService().BuildFileManifestEntryAsync(filePath, _testRoot);

        // Golden vector: chunk sizes for this exact input under algorithm v2.
        var chunkSizes = result.FileEntry.Chunks.Select(chunk => chunk.PlainSizeBytes).ToList();
        chunkSizes.Should().Equal(GoldenChunkSizes, GoldenMismatchExplanation);

        // Chunk ids are the dedup keys, so pin the first one exactly.
        result.FileEntry.Chunks[0].ChunkId.Should().Be(GoldenFirstChunkId, GoldenMismatchExplanation);
    }

    private const string GoldenMismatchExplanation =
        "chunk boundaries changed, which means no chunk already stored remotely can be reused; " +
        "re-baseline only if that re-upload cost is intended";

    // For DeterministicBytes(64 KiB, 0x5EED1234) under SmallChunkConfig. The 8192 entry
    // was cut by the max-size ceiling rather than a hash match.
    private static readonly int[] GoldenChunkSizes =
    [
        3084, 3987, 3041, 7147, 6486, 2923, 1563, 1583, 3505, 2363,
        3067, 1210, 8192, 2067, 5130, 1082, 1508, 4039, 3559
    ];

    private const string GoldenFirstChunkId = "b1e7db7c47a471ecce3438a1070ef9e6ff4caa401f5e4f3a6d131fc509414139";

    [Fact]
    public async Task BuildFileManifestEntryAsync_ShouldBeDeterministic_AcrossRuns()
    {
        var contents = DeterministicBytes(48 * 1024, seed: 0xABCD_0001);
        var first = await WriteFixtureAsync("determinism-a.bin", contents);
        var second = await WriteFixtureAsync("determinism-b.bin", contents);

        var service = CreateService();
        var firstResult = await service.BuildFileManifestEntryAsync(first, _testRoot);
        var secondResult = await service.BuildFileManifestEntryAsync(second, _testRoot);

        secondResult.FileEntry.Chunks.Select(c => c.ChunkId)
            .Should().Equal(firstResult.FileEntry.Chunks.Select(c => c.ChunkId));
    }

    [Fact]
    public async Task BuildFileManifestEntryAsync_ShouldReuseChunkIds_WhenOnlyTailDiffers()
    {
        // An edit must not shift every later boundary the way fixed-size blocking would.
        var prefix = DeterministicBytes(32 * 1024, seed: 0x1111_2222);

        var original = prefix.Concat(DeterministicBytes(4 * 1024, seed: 0x3333)).ToArray();
        var edited = prefix.Concat(DeterministicBytes(4 * 1024, seed: 0x4444)).ToArray();

        var service = CreateService();
        var originalResult = await service.BuildFileManifestEntryAsync(
            await WriteFixtureAsync("tail-original.bin", original), _testRoot);
        var editedResult = await service.BuildFileManifestEntryAsync(
            await WriteFixtureAsync("tail-edited.bin", edited), _testRoot);

        var shared = originalResult.FileEntry.Chunks.Select(c => c.ChunkId)
            .Intersect(editedResult.FileEntry.Chunks.Select(c => c.ChunkId))
            .ToList();

        shared.Should().NotBeEmpty("chunks covering the untouched prefix should be reusable");
    }

    [Fact]
    public async Task BuildFileManifestEntryAsync_ShouldRespectConfiguredChunkSizeBounds()
    {
        var config = SmallChunkConfig();
        var filePath = await WriteFixtureAsync("bounds.bin", DeterministicBytes(128 * 1024, seed: 0x9999_7777));

        var result = await CreateService(config).BuildFileManifestEntryAsync(filePath, _testRoot);
        var chunks = result.FileEntry.Chunks;

        chunks.Should().HaveCountGreaterThan(1);

        // The final chunk is whatever remains, so it may be under the minimum.
        foreach (var chunk in chunks.Take(chunks.Count - 1))
        {
            chunk.PlainSizeBytes.Should().BeGreaterThanOrEqualTo(config.MinChunkSizeKB * 1024);
            chunk.PlainSizeBytes.Should().BeLessThanOrEqualTo(config.MaxChunkSizeKB * 1024);
        }

        chunks[^1].PlainSizeBytes.Should().BeLessThanOrEqualTo(config.MaxChunkSizeKB * 1024);
    }

    [Fact]
    public async Task BuildFileManifestEntryAsync_ShouldReconstructOriginalContent()
    {
        var contents = DeterministicBytes(96 * 1024, seed: 0xDEAD_BEEF);
        var filePath = await WriteFixtureAsync("reassemble.bin", contents);

        var result = await CreateService().BuildFileManifestEntryAsync(filePath, _testRoot);

        result.FileEntry.Chunks.Sum(chunk => (long)chunk.PlainSizeBytes)
            .Should().Be(contents.Length, "chunking must be lossless");

        var reassembled = result.ChunkPayloads.SelectMany(payload => payload.StoredPayload).ToArray();
        reassembled.Should().Equal(contents);
    }

    [Fact]
    public async Task BuildFileManifestEntryAsync_ShouldHandleEmptyFile()
    {
        var filePath = await WriteFixtureAsync("empty.bin", []);

        var result = await CreateService().BuildFileManifestEntryAsync(filePath, _testRoot);

        result.FileEntry.Chunks.Should().BeEmpty();
        result.FileEntry.SizeBytes.Should().Be(0);
    }

    [Fact]
    public async Task BuildFileManifestEntryAsync_WithSink_ShouldProduceIdenticalChunksAndNotRetainPayloads()
    {
        var contents = DeterministicBytes(96 * 1024, seed: 0x5EED_1234);
        var buffered = await WriteFixtureAsync("sink-buffered.bin", contents);
        var streamed = await WriteFixtureAsync("sink-streamed.bin", contents);

        var service = CreateService();
        var bufferedResult = await service.BuildFileManifestEntryAsync(buffered, _testRoot);

        var sunkPayloads = new List<ChunkBuildPayload>();
        var streamedResult = await service.BuildFileManifestEntryAsync(
            streamed,
            _testRoot,
            (payload, _) =>
            {
                sunkPayloads.Add(payload);
                return Task.CompletedTask;
            });

        streamedResult.FileEntry.Chunks.Select(chunk => chunk.ChunkId)
            .Should().Equal(bufferedResult.FileEntry.Chunks.Select(chunk => chunk.ChunkId),
                "the sink must not change chunk boundaries or dedup keys");

        streamedResult.ChunkPayloads.Should().BeEmpty(
            "payloads handed to the sink are released rather than accumulated, which is the point of the sink");

        sunkPayloads.Select(payload => payload.ChunkId)
            .Should().Equal(bufferedResult.ChunkPayloads.Select(payload => payload.ChunkId));

        sunkPayloads.SelectMany(payload => payload.StoredPayload).Should().Equal(contents);
    }

    [Fact]
    public async Task BuildFileManifestEntryAsync_WithSink_ShouldSurfaceSinkFailure()
    {
        var filePath = await WriteFixtureAsync("sink-throws.bin", DeterministicBytes(32 * 1024, seed: 0x2222));

        var act = async () => await CreateService().BuildFileManifestEntryAsync(
            filePath,
            _testRoot,
            (_, _) => throw new InvalidOperationException("upload failed"));

        // A failed chunk upload must abort the snapshot rather than yield a manifest
        // referencing an object that was never stored.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("upload failed");
    }
}
