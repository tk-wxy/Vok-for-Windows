using System.Buffers.Binary;
using System.Diagnostics;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Vok.Infrastructure.Windows.Persistence;

namespace Vok.ClipboardProbe;

internal static class ExtendedImageMatrixProbe
{
    private const uint DibV5 = 17;

    public static async Task<ExtendedImageMatrixReport> RunAsync(
        ControlledClipboardProducer producer,
        uint pngFormat,
        IAsyncEnumerator<ClipboardCaptureResult> snapshots,
        int rounds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rounds, 50);
        var probeRoot = Path.Combine(
            Path.GetTempPath(),
            "Vok.ClipboardProbe",
            $"extended-{Guid.NewGuid():N}");
        var normalizationStagingRoot = Path.Combine(probeRoot, "normalization-staging");
        var assetRoot = Path.Combine(probeRoot, "assets");
        var databasePath = Path.Combine(probeRoot, "vok.db");
        Directory.CreateDirectory(normalizationStagingRoot);

        ClipboardImageTemporaryStore? ownedTemporaryStore = null;
        WindowsClipboardImageExecutor? ownedExecutor = null;
        SqliteClipboardHistoryRepository? ownedRepository = null;
        ContentAddressedClipboardAssetStore? ownedAssetStore = null;
        ClipboardImageAssetCoordinator? ownedCoordinator = null;
        try
        {
            var options = new ClipboardImageCaptureOptions();
            var temporaryStore = ownedTemporaryStore =
                new ClipboardImageTemporaryStore(normalizationStagingRoot);
            var executor = ownedExecutor = new WindowsClipboardImageExecutor(
                options,
                new ClipboardImageNormalizer(options),
                temporaryStore,
                TimeSpan.FromSeconds(10));
            var repository = ownedRepository =
                new SqliteClipboardHistoryRepository(databasePath);
            var assetStore = ownedAssetStore =
                new ContentAddressedClipboardAssetStore(assetRoot, options);
            var coordinator = ownedCoordinator = new ClipboardImageAssetCoordinator(
                repository,
                assetStore,
                options,
                TimeSpan.FromSeconds(10));
            var profilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "spool",
                "drivers",
                "color",
                "sRGB Color Space Profile.icm");
            if (!File.Exists(profilePath))
            {
                throw new FileNotFoundException(
                    "The Windows sRGB profile required by the embedded ICC gate is unavailable.",
                    profilePath);
            }

            var embeddedProfile = File.ReadAllBytes(profilePath);
            var baseline = CaptureProcessMemory();
            var samples = new List<ExtendedImageSampleReport>();
            for (var round = 1; round <= rounds; round++)
            {
                samples.Add(await NormalizeAsync(
                    producer,
                    snapshots,
                    executor,
                    coordinator,
                    temporaryStore,
                    normalizationStagingRoot,
                    round,
                    "4k-png-alpha",
                    pngFormat,
                    ClipboardRepresentationKind.Png,
                    expectedWidth: 3840,
                    expectedHeight: 2160,
                    expectLeasedInput: false,
                    expectSourceIcc: false,
                    () => ControlledImagePayloads.CreatePatternPng(
                        3840,
                        2160,
                        cellSize: 64),
                    cancellationToken));
                samples.Add(await NormalizeAsync(
                    producer,
                    snapshots,
                    executor,
                    coordinator,
                    temporaryStore,
                    normalizationStagingRoot,
                    round,
                    "4k-dibv5-alpha",
                    DibV5,
                    ClipboardRepresentationKind.DibV5,
                    expectedWidth: 3840,
                    expectedHeight: 2160,
                    expectLeasedInput: true,
                    expectSourceIcc: false,
                    () => ControlledImagePayloads.CreatePatternDibV5(
                        3840,
                        2160,
                        cellSize: 64),
                    cancellationToken));
                samples.Add(await NormalizeAsync(
                    producer,
                    snapshots,
                    executor,
                    coordinator,
                    temporaryStore,
                    normalizationStagingRoot,
                    round,
                    "8k-png-alpha-icc",
                    pngFormat,
                    ClipboardRepresentationKind.Png,
                    expectedWidth: 7680,
                    expectedHeight: 4320,
                    expectLeasedInput: false,
                    expectSourceIcc: true,
                    () => ControlledImagePayloads.CreatePatternPng(
                        7680,
                        4320,
                        cellSize: 128,
                        embeddedProfile),
                    cancellationToken));
            }

            var end = CaptureProcessMemory();
            var png4KHashes = samples
                .Where(static sample => sample.Name == "4k-png-alpha")
                .Select(static sample => sample.SemanticHash)
                .ToArray();
            var dib4KHashes = samples
                .Where(static sample => sample.Name == "4k-dibv5-alpha")
                .Select(static sample => sample.SemanticHash)
                .ToArray();
            var png8KHashes = samples
                .Where(static sample => sample.Name == "8k-png-alpha-icc")
                .Select(static sample => sample.SemanticHash)
                .ToArray();
            var stableIdentities =
                png4KHashes.Length == rounds &&
                dib4KHashes.Length == rounds &&
                png8KHashes.Length == rounds &&
                png4KHashes.Distinct(StringComparer.Ordinal).Count() == 1 &&
                dib4KHashes.Distinct(StringComparer.Ordinal).Count() == 1 &&
                png8KHashes.Distinct(StringComparer.Ordinal).Count() == 1 &&
                string.Equals(png4KHashes[0], dib4KHashes[0], StringComparison.Ordinal);
            var ownershipReturnedToZero = samples.All(static sample =>
                sample.OutstandingJobsAfterDispose == 0 &&
                sample.AssetCoordinatorJobsAfterCommit == 0 &&
                sample.OutstandingTemporaryLeasesAfterDispose == 0 &&
                sample.StagingFilesAfterDispose == 0);
            var page = await repository.LoadPageAsync(
                after: null,
                pageSize: 16,
                cancellationToken);
            var imageEntries = page.Items
                .Where(static entry => entry.Kind is ClipboardContentKind.Image)
                .ToArray();
            var fourKEntry = imageEntries.SingleOrDefault(entry =>
                string.Equals(entry.SemanticHash, png4KHashes[0], StringComparison.Ordinal));
            var eightKEntry = imageEntries.SingleOrDefault(entry =>
                string.Equals(entry.SemanticHash, png8KHashes[0], StringComparison.Ordinal));
            var assetPngFileCount = Directory.EnumerateFiles(
                assetRoot,
                "*.png",
                SearchOption.AllDirectories).Count();
            var assetStagingFileCount = Directory.EnumerateFiles(
                Path.Combine(assetRoot, ".staging"),
                "asset-*.tmp",
                SearchOption.TopDirectoryOnly).Count();
            var persistenceStable =
                imageEntries.Length == 2 &&
                fourKEntry?.CaptureCount == checked(rounds * 2L) &&
                eightKEntry?.CaptureCount == rounds &&
                assetPngFileCount == 4 &&
                assetStagingFileCount == 0 &&
                coordinator.OutstandingJobCount == 0;
            var success =
                samples.All(static sample => sample.Success) &&
                stableIdentities &&
                ownershipReturnedToZero &&
                persistenceStable;

            return new ExtendedImageMatrixReport(
                Rounds: rounds,
                ProfilePath: profilePath,
                BaselineMemory: baseline,
                EndMemory: end,
                PeakWorkingSetBytes: samples.Max(static sample => sample.Memory.WorkingSetBytes),
                PeakPrivateBytes: samples.Max(static sample => sample.Memory.PrivateBytes),
                StableIdentities: stableIdentities,
                OwnershipReturnedToZero: ownershipReturnedToZero,
                PersistenceStable: persistenceStable,
                HistoryEntryCount: imageEntries.Length,
                FourKCaptureCount: fourKEntry?.CaptureCount,
                EightKCaptureCount: eightKEntry?.CaptureCount,
                AssetPngFileCount: assetPngFileCount,
                AssetStagingFileCount: assetStagingFileCount,
                Samples: samples,
                Success: success);
        }
        finally
        {
            ownedCoordinator?.Dispose();
            ownedAssetStore?.Dispose();
            ownedRepository?.Dispose();
            ownedExecutor?.Dispose();
            ownedTemporaryStore?.Dispose();
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, recursive: true);
            }
        }
    }

    private static async Task<ExtendedImageSampleReport> NormalizeAsync(
        ControlledClipboardProducer producer,
        IAsyncEnumerator<ClipboardCaptureResult> snapshots,
        WindowsClipboardImageExecutor executor,
        ClipboardImageAssetCoordinator coordinator,
        ClipboardImageTemporaryStore temporaryStore,
        string stagingRoot,
        int round,
        string name,
        uint nativeFormat,
        ClipboardRepresentationKind expectedKind,
        int expectedWidth,
        int expectedHeight,
        bool expectLeasedInput,
        bool expectSourceIcc,
        Func<byte[]> payloadFactory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = payloadFactory();
        var inputBytes = payload.LongLength;
        await producer.WriteImmediateAsync(
            [new ProducerFormat(nativeFormat, payload)],
            cancellationToken);
        payload = [];

        ClipboardCaptureResult? capture = await ReadNextAsync(
            snapshots,
            cancellationToken);
        var capturedFormat = capture.Formats.Single(format =>
            format.RepresentationKind == expectedKind &&
            format.CaptureState is ClipboardFormatCaptureState.Captured);
        var inputStorage = capturedFormat.PayloadStorageKind;
        var stopwatch = Stopwatch.StartNew();
        ClipboardImageNormalizationResult? result = null;
        try
        {
            var normalizeTask = executor.NormalizeAsync(capture, cancellationToken);
            capture = null;
            result = await normalizeTask;
            stopwatch.Stop();

            bool canonicalContainsIcc;
            using (var canonical = result.OpenCanonicalPngReadStream())
            {
                canonicalContainsIcc = ContainsPngChunk(canonical, "iCCP"u8);
            }

            var dimensionsMatch =
                result.PixelWidth == expectedWidth &&
                result.PixelHeight == expectedHeight;
            var hasExpectedStorage =
                !expectLeasedInput ||
                inputStorage is ClipboardPayloadStorageKind.LeasedBlob;
            var success =
                dimensionsMatch &&
                result.HasAlpha &&
                hasExpectedStorage &&
                (!expectSourceIcc || !canonicalContainsIcc);
            var semanticHash = result.SemanticHash;
            var pixelWidth = result.PixelWidth;
            var pixelHeight = result.PixelHeight;
            var hasAlpha = result.HasAlpha;
            var dpiXMilli = result.DpiXMilli;
            var dpiYMilli = result.DpiYMilli;
            var canonicalBytes = result.CanonicalPngByteLength;
            var thumbnailBytes = result.ThumbnailPngByteLength;
            var commitTask = coordinator.CommitAsync(
                result,
                DateTimeOffset.UtcNow,
                cancellationToken);
            result = null;
            var entry = await commitTask;
            var report = new ExtendedImageSampleReport(
                Round: round,
                Name: name,
                InputBytes: inputBytes,
                InputStorage: inputStorage.ToString(),
                PixelWidth: pixelWidth,
                PixelHeight: pixelHeight,
                HasAlpha: hasAlpha,
                DpiXMilli: dpiXMilli,
                DpiYMilli: dpiYMilli,
                SemanticHash: semanticHash,
                CanonicalBytes: canonicalBytes,
                ThumbnailBytes: thumbnailBytes,
                SourceHadEmbeddedIcc: expectSourceIcc,
                CanonicalContainsIcc: canonicalContainsIcc,
                ElapsedMilliseconds: stopwatch.ElapsedMilliseconds,
                StoredEntryId: entry.Id,
                StoredCaptureCount: entry.CaptureCount,
                Memory: default,
                OutstandingJobsAfterDispose: -1,
                AssetCoordinatorJobsAfterCommit: -1,
                OutstandingTemporaryLeasesAfterDispose: -1,
                StagingFilesAfterDispose: -1,
                Success: success &&
                    entry.Kind is ClipboardContentKind.Image &&
                    string.Equals(entry.SemanticHash, semanticHash, StringComparison.Ordinal));

            var memory = CaptureProcessMemory();
            return report with
            {
                Memory = memory,
                OutstandingJobsAfterDispose = executor.OutstandingJobCount,
                AssetCoordinatorJobsAfterCommit = coordinator.OutstandingJobCount,
                OutstandingTemporaryLeasesAfterDispose =
                    temporaryStore.OutstandingLeaseCount,
                StagingFilesAfterDispose = Directory.EnumerateFiles(
                    stagingRoot,
                    "*",
                    SearchOption.AllDirectories).Count(),
            };
        }
        finally
        {
            result?.Dispose();
            capture?.Dispose();
        }
    }

    private static async Task<ClipboardCaptureResult> ReadNextAsync(
        IAsyncEnumerator<ClipboardCaptureResult> snapshots,
        CancellationToken cancellationToken)
    {
        if (!await snapshots.MoveNextAsync().AsTask().WaitAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Clipboard listener ended before the extended image sample arrived.");
        }

        return snapshots.Current;
    }

    private static bool ContainsPngChunk(Stream stream, ReadOnlySpan<byte> type)
    {
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        ReadOnlySpan<byte> expectedSignature =
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (!signature.SequenceEqual(expectedSignature))
        {
            throw new InvalidDataException("The normalized image is not a PNG stream.");
        }

        Span<byte> chunkHeader = stackalloc byte[8];
        while (true)
        {
            stream.ReadExactly(chunkHeader);
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunkHeader));
            if (chunkHeader[4..].SequenceEqual(type))
            {
                return true;
            }

            stream.Seek(checked(length + 4L), SeekOrigin.Current);
            if (chunkHeader[4..].SequenceEqual("IEND"u8))
            {
                return false;
            }
        }
    }

    private static ProcessMemoryReport CaptureProcessMemory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessMemoryReport(
            process.WorkingSet64,
            process.PrivateMemorySize64);
    }
}

internal sealed record ExtendedImageMatrixReport(
    int Rounds,
    string ProfilePath,
    ProcessMemoryReport BaselineMemory,
    ProcessMemoryReport EndMemory,
    long PeakWorkingSetBytes,
    long PeakPrivateBytes,
    bool StableIdentities,
    bool OwnershipReturnedToZero,
    bool PersistenceStable,
    int HistoryEntryCount,
    long? FourKCaptureCount,
    long? EightKCaptureCount,
    int AssetPngFileCount,
    int AssetStagingFileCount,
    IReadOnlyList<ExtendedImageSampleReport> Samples,
    bool Success);

internal sealed record ExtendedImageSampleReport(
    int Round,
    string Name,
    long InputBytes,
    string InputStorage,
    int PixelWidth,
    int PixelHeight,
    bool HasAlpha,
    int? DpiXMilli,
    int? DpiYMilli,
    string SemanticHash,
    long CanonicalBytes,
    long ThumbnailBytes,
    bool SourceHadEmbeddedIcc,
    bool CanonicalContainsIcc,
    long ElapsedMilliseconds,
    Guid StoredEntryId,
    long StoredCaptureCount,
    ProcessMemoryReport Memory,
    int OutstandingJobsAfterDispose,
    int AssetCoordinatorJobsAfterCommit,
    int OutstandingTemporaryLeasesAfterDispose,
    int StagingFilesAfterDispose,
    bool Success);

internal readonly record struct ProcessMemoryReport(
    long WorkingSetBytes,
    long PrivateBytes);
