using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardImageAssetCoordinatorTests
{
    [Fact]
    public async Task CommitPublishesTwoReadyAssetsAndDuplicatePreservesPin()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "vok.db");
        var assetRoot = Path.Combine(directory, "assets");
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(databasePath);
            using var coordinator = new ClipboardImageAssetCoordinator(
                repository,
                new ContentAddressedClipboardAssetStore(assetRoot));
            var primary = EncodePng(2, 2, [
                0, 0, 255, 255,
                0, 255, 0, 255,
                255, 0, 0, 255,
                255, 255, 255, 128,
            ]);
            var thumbnail = EncodePng(1, 1, [0, 0, 255, 255]);
            var firstResult = CreateResult(
                Hash(1),
                primary,
                2,
                2,
                thumbnail,
                1,
                1,
                dpiXMilli: 96000,
                dpiYMilli: 96000);

            var first = await coordinator.CommitAsync(
                firstResult,
                DateTimeOffset.Parse("2026-08-30T00:00:00Z", CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);

            Assert.True(firstResult.IsDisposed);
            Assert.Equal(ClipboardContentKind.Image, first.Kind);
            Assert.NotNull(first.Image);
            Assert.Equal(ClipboardAssetState.Ready, first.Image.PrimaryPng?.State);
            Assert.Equal(ClipboardAssetState.Ready, first.Image.ThumbnailPng?.State);
            Assert.NotEqual(first.SemanticHash, first.Image.PrimaryPng?.Hash);
            Assert.True((await repository.SetPinnedAsync(
                first.Id,
                true,
                TestContext.Current.CancellationToken))?.IsPinned);

            var duplicateResult = CreateResult(
                Hash(1),
                primary,
                2,
                2,
                thumbnail,
                1,
                1,
                dpiXMilli: 144000,
                dpiYMilli: 144000);
            var duplicate = await coordinator.CommitAsync(
                duplicateResult,
                DateTimeOffset.Parse("2026-08-30T00:01:00Z", CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);

            Assert.Equal(first.Id, duplicate.Id);
            Assert.Equal(2, duplicate.CaptureCount);
            Assert.True(duplicate.IsPinned);
            Assert.Equal(144000, duplicate.Image?.DpiXMilli);
            Assert.True(duplicateResult.IsDisposed);
            var page = await repository.LoadPageAsync(
                null,
                32,
                TestContext.Current.CancellationToken);
            var loaded = Assert.Single(page.Items);
            Assert.Equal(duplicate, loaded);
            var replay = await repository.GetReplayPayloadAsync(
                duplicate.Id,
                TestContext.Current.CancellationToken);
            Assert.NotNull(replay);
            Assert.Equal(ClipboardContentKind.Image, replay.Kind);
            Assert.Empty(replay.Representations);
            Assert.Equal(duplicate.Image, replay.Image);

            using var connection = OpenDatabase(databasePath);
            Assert.Equal(7L, ExecuteInt64(connection, "PRAGMA user_version;"));
            Assert.Equal(2L, ExecuteInt64(connection, "SELECT COUNT(*) FROM content_assets;"));
            Assert.Equal(2L, ExecuteInt64(connection, "SELECT COUNT(*) FROM clipboard_asset_refs;"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    connection,
                    "SELECT COUNT(*) FROM clipboard_representations WHERE entry_id IN (SELECT id FROM clipboard_entries WHERE kind = 1);"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    connection,
                    "SELECT COUNT(*) FROM clipboard_files WHERE entry_id IN (SELECT id FROM clipboard_entries WHERE kind = 1);"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SharedAssetIsDeletedOnlyAfterItsLastReference()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "shared.db");
        var assetRoot = Path.Combine(directory, "assets");
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(databasePath);
            using var coordinator = new ClipboardImageAssetCoordinator(
                repository,
                new ContentAddressedClipboardAssetStore(assetRoot));
            var sharedThumbnail = EncodePng(1, 1, [1, 2, 3, 255]);
            var first = await coordinator.CommitAsync(
                CreateResult(
                    Hash(10),
                    EncodePng(2, 1, [1, 2, 3, 255, 4, 5, 6, 255]),
                    2,
                    1,
                    sharedThumbnail,
                    1,
                    1),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            var second = await coordinator.CommitAsync(
                CreateResult(
                    Hash(11),
                    EncodePng(2, 1, [7, 8, 9, 255, 10, 11, 12, 255]),
                    2,
                    1,
                    sharedThumbnail,
                    1,
                    1),
                DateTimeOffset.UtcNow.AddSeconds(1),
                TestContext.Current.CancellationToken);
            var sharedHash = first.Image!.ThumbnailPng!.Hash;
            var firstPrimaryHash = first.Image.PrimaryPng!.Hash;
            var secondPrimaryHash = second.Image!.PrimaryPng!.Hash;

            Assert.True(await coordinator.RemoveAsync(
                first.Id,
                TestContext.Current.CancellationToken));

            Assert.False(File.Exists(GetAssetPath(assetRoot, firstPrimaryHash)));
            Assert.True(File.Exists(GetAssetPath(assetRoot, sharedHash)));
            Assert.True(File.Exists(GetAssetPath(assetRoot, secondPrimaryHash)));
            Assert.True(await coordinator.RemoveAsync(
                second.Id,
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(GetAssetPath(assetRoot, sharedHash)));
            Assert.False(File.Exists(GetAssetPath(assetRoot, secondPrimaryHash)));
            using var connection = OpenDatabase(databasePath);
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM content_assets;"));
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM clipboard_asset_refs;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ClearHistoryRemovesAllKindsAndCompletesAssetTombstones()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "clear.db");
        var assetRoot = Path.Combine(directory, "assets");
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(databasePath);
            using var coordinator = new ClipboardImageAssetCoordinator(
                repository,
                new ContentAddressedClipboardAssetStore(assetRoot));
            var text = await repository.UpsertTextAsync(
                new ClipboardTextContent(
                    Hash(80),
                    "clear-text",
                    "clear-text",
                    [new ClipboardStoredRepresentation(
                        ClipboardRepresentationKind.UnicodeText,
                        100,
                        "clear-text",
                        22)]),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            var png = EncodePng(1, 1, [3, 2, 1, 255]);
            var image = await coordinator.CommitAsync(
                CreateResult(Hash(81), png, 1, 1, png, 1, 1),
                DateTimeOffset.UtcNow.AddSeconds(1),
                TestContext.Current.CancellationToken);
            var imagePath = GetAssetPath(assetRoot, image.Image!.PrimaryPng!.Hash);
            Assert.True(File.Exists(imagePath));

            var result = await coordinator.ClearHistoryAsync(
                TestContext.Current.CancellationToken);

            Assert.Equal([text.Id, image.Id], result.RemovedEntryIds);
            Assert.Equal(0, result.RemainingEntryCount);
            Assert.True(result.ManagedBytesBefore > 0);
            Assert.Equal(0, result.ManagedBytesAfter);
            Assert.Equal(0, result.PendingAssetDeleteCount);
            Assert.False(result.AssetCleanupDeferred);
            Assert.False(File.Exists(imagePath));
            using var connection = OpenDatabase(databasePath);
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM clipboard_entries;"));
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM clipboard_asset_refs;"));
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM content_assets;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task StartupDeletesHashVerifiedOrphansButRetainsUnknownContent()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "orphans.db");
        var assetRoot = Path.Combine(directory, "assets");
        try
        {
            var png = EncodePng(1, 1, [7, 6, 5, 255]);
            ClipboardPreparedAsset orphan;
            using (var store = new ContentAddressedClipboardAssetStore(assetRoot))
            using (var stream = new MemoryStream(png, writable: false))
            {
                orphan = store.CommitPng(
                    stream,
                    png.Length,
                    1,
                    1,
                    TestContext.Current.CancellationToken);
            }

            var unknownHash = Hash(999);
            var unknownPath = GetAssetPath(assetRoot, unknownHash);
            Directory.CreateDirectory(Path.GetDirectoryName(unknownPath)!);
            File.WriteAllBytes(unknownPath, [1, 2, 3, 4]);
            var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            using var repository = new SqliteClipboardHistoryRepository(databasePath);
            using var coordinator = new ClipboardImageAssetCoordinator(
                repository,
                new ContentAddressedClipboardAssetStore(assetRoot),
                diagnosticFault: faults.Enqueue);

            _ = await coordinator.ApplyRetentionAsync(
                new ClipboardRetentionOptions(),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);

            Assert.False(File.Exists(Path.Combine(
                assetRoot,
                orphan.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(File.Exists(unknownPath));
            Assert.Contains(
                faults,
                static fault => fault is InvalidDataException &&
                    fault.Message.Contains("content-addressed", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ThumbnailLoadReturnsVerifiedOwnedBytesAndRejectsStaleRevision()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "thumbnail.db");
        var assetRoot = Path.Combine(directory, "assets");
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(databasePath);
            using var coordinator = new ClipboardImageAssetCoordinator(
                repository,
                new ContentAddressedClipboardAssetStore(assetRoot));
            var thumbnailBytes = EncodePng(1, 1, [9, 8, 7, 128]);
            var entry = await coordinator.CommitAsync(
                CreateResult(
                    Hash(12),
                    EncodePng(2, 1, [1, 2, 3, 255, 4, 5, 6, 255]),
                    2,
                    1,
                    thumbnailBytes,
                    1,
                    1),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            var thumbnail = entry.Image!.ThumbnailPng!;

            var loaded = await coordinator.LoadThumbnailAsync(
                thumbnail.Hash,
                thumbnail.Revision,
                TestContext.Current.CancellationToken);

            Assert.NotNull(loaded);
            Assert.True(loaded.IsReady);
            Assert.Equal(4, loaded.DecodedByteLength);
            using (var stream = loaded.OpenPngReadStream())
            using (var copy = new MemoryStream())
            {
                await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
                Assert.Equal(thumbnailBytes, copy.ToArray());
            }

            var stale = await coordinator.LoadThumbnailAsync(
                thumbnail.Hash,
                thumbnail.Revision + 1,
                TestContext.Current.CancellationToken);
            Assert.NotNull(stale);
            Assert.Equal(ClipboardAssetState.Ready, stale.State);
            Assert.Equal(thumbnail.Revision, stale.Revision);
            Assert.False(stale.IsReady);
            Assert.Throws<InvalidOperationException>(stale.OpenPngReadStream);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task DeleteFailureKeepsTombstoneAndStartupRecoveryRetries()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "recovery.db");
        var assetRoot = Path.Combine(directory, "assets");
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(databasePath);
            string hash;
            long readyRevision;
            using (var coordinator = new ClipboardImageAssetCoordinator(
                       repository,
                       new ContentAddressedClipboardAssetStore(assetRoot)))
            {
                var png = EncodePng(1, 1, [1, 2, 3, 255]);
                var entry = await coordinator.CommitAsync(
                    CreateResult(Hash(20), png, 1, 1, png, 1, 1),
                    DateTimeOffset.UtcNow,
                    TestContext.Current.CancellationToken);
                hash = entry.Image!.PrimaryPng!.Hash;
                readyRevision = entry.Image.PrimaryPng.Revision;
                var path = GetAssetPath(assetRoot, hash);
                using (new FileStream(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                {
                    Assert.True(await coordinator.RemoveAsync(
                        entry.Id,
                        TestContext.Current.CancellationToken));
                    var pending = await repository.LoadAssetAsync(
                        hash,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(pending);
                    Assert.Equal(ClipboardAssetState.PendingDelete, pending.State);
                    Assert.Equal(readyRevision + 1, pending.Revision);
                    Assert.True(File.Exists(path));
                }
            }

            var pendingBeforeRestart = await repository.LoadAssetAsync(
                hash,
                TestContext.Current.CancellationToken);
            Assert.NotNull(pendingBeforeRestart);
            using (var restarted = new ClipboardImageAssetCoordinator(
                       repository,
                       new ContentAddressedClipboardAssetStore(assetRoot)))
            {
                Assert.Null(await restarted.VerifyAssetAsync(
                    hash,
                    pendingBeforeRestart.Revision,
                    TestContext.Current.CancellationToken));
            }

            Assert.False(File.Exists(GetAssetPath(assetRoot, hash)));
            Assert.Null(await repository.LoadAssetAsync(
                hash,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task CorruptThumbnailIsQuarantinedWithoutDisablingPrimary()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "quarantine.db");
        var assetRoot = Path.Combine(directory, "assets");
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(databasePath);
            ClipboardEntry entry;
            using (var coordinator = new ClipboardImageAssetCoordinator(
                       repository,
                       new ContentAddressedClipboardAssetStore(assetRoot)))
            {
                entry = await coordinator.CommitAsync(
                    CreateResult(
                        Hash(30),
                        EncodePng(2, 1, [1, 2, 3, 255, 4, 5, 6, 255]),
                        2,
                        1,
                        EncodePng(1, 1, [1, 2, 3, 255]),
                        1,
                        1),
                    DateTimeOffset.UtcNow,
                    TestContext.Current.CancellationToken);
            }

            var thumbnail = entry.Image!.ThumbnailPng!;
            File.WriteAllBytes(GetAssetPath(assetRoot, thumbnail.Hash), [0x00, 0x01, 0x02]);
            using var restarted = new ClipboardImageAssetCoordinator(
                repository,
                new ContentAddressedClipboardAssetStore(assetRoot));
            var verified = await restarted.LoadThumbnailAsync(
                thumbnail.Hash,
                thumbnail.Revision,
                TestContext.Current.CancellationToken);

            Assert.NotNull(verified);
            Assert.Equal(ClipboardAssetState.Quarantined, verified.State);
            Assert.False(verified.IsReady);
            var primary = await repository.LoadAssetAsync(
                entry.Image.PrimaryPng!.Hash,
                TestContext.Current.CancellationToken);
            Assert.NotNull(primary);
            Assert.Equal(ClipboardAssetState.Ready, primary.State);
            Assert.True(File.Exists(GetAssetPath(assetRoot, thumbnail.Hash)));
            var page = await repository.LoadPageAsync(
                null,
                32,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                ClipboardAssetState.Quarantined,
                Assert.Single(page.Items).Image?.ThumbnailPng?.State);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RecaptureInvalidatesAStalePendingDeleteClaim()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "recapture.db");
        var assetRoot = Path.Combine(directory, "assets");
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(databasePath);
            using var coordinator = new ClipboardImageAssetCoordinator(
                repository,
                new ContentAddressedClipboardAssetStore(assetRoot));
            var png = EncodePng(1, 1, [9, 8, 7, 255]);
            var first = await coordinator.CommitAsync(
                CreateResult(Hash(35), png, 1, 1, png, 1, 1),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            var hash = first.Image!.PrimaryPng!.Hash;
            var path = GetAssetPath(assetRoot, hash);
            ClipboardAssetDeleteClaim staleClaim;
            using (new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                Assert.True(await coordinator.RemoveAsync(
                    first.Id,
                    TestContext.Current.CancellationToken));
                staleClaim = Assert.Single(await repository.LoadPendingAssetDeletesAsync(
                    TestContext.Current.CancellationToken));
            }

            var recaptured = await coordinator.CommitAsync(
                CreateResult(Hash(35), png, 1, 1, png, 1, 1),
                DateTimeOffset.UtcNow.AddSeconds(1),
                TestContext.Current.CancellationToken);

            Assert.Equal(hash, recaptured.Image?.PrimaryPng?.Hash);
            Assert.Equal(ClipboardAssetState.Ready, recaptured.Image?.PrimaryPng?.State);
            Assert.True(recaptured.Image?.PrimaryPng?.Revision > staleClaim.Revision);
            Assert.False(await repository.IsAssetDeleteClaimCurrentAsync(
                staleClaim,
                TestContext.Current.CancellationToken));
            Assert.False(await repository.CompleteAssetDeleteAsync(
                staleClaim,
                TestContext.Current.CancellationToken));
            Assert.True(File.Exists(path));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task UnavailableAssetRootDoesNotDisableTextRepository()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "degraded.db");
        var invalidRoot = Path.Combine(directory, "asset-root-is-a-file");
        try
        {
            File.WriteAllText(invalidRoot, "keep");
            Assert.ThrowsAny<IOException>(() =>
                new ContentAddressedClipboardAssetStore(invalidRoot));
            using var repository = new SqliteClipboardHistoryRepository(databasePath);
            var text = await repository.UpsertTextAsync(
                new ClipboardTextContent(
                    Hash(36),
                    "text-still-works",
                    "text-still-works",
                    [new ClipboardStoredRepresentation(
                        ClipboardRepresentationKind.UnicodeText,
                        100,
                        "text-still-works",
                        34)]),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                "text-still-works",
                (await repository.GetReplayPayloadAsync(
                    text.Id,
                    TestContext.Current.CancellationToken))?.PlainText);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task DatabaseFailureLeavesOnlyReusableUnreferencedFiles()
    {
        var directory = CreateTestDirectory();
        try
        {
            var result = CreateResult(
                Hash(40),
                EncodePng(1, 1, [1, 2, 3, 255]),
                1,
                1,
                EncodePng(1, 1, [4, 5, 6, 255]),
                1,
                1);
            using var coordinator = new ClipboardImageAssetCoordinator(
                new ThrowingMetadataRepository(),
                new ContentAddressedClipboardAssetStore(directory),
                new ClipboardImageCaptureOptions(),
                TimeSpan.FromSeconds(2));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.CommitAsync(
                    result,
                    DateTimeOffset.UtcNow,
                    TestContext.Current.CancellationToken));

            Assert.True(result.IsDisposed);
            Assert.Equal(
                2,
                Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories).Count());
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(directory, ".staging"),
                "asset-*.tmp",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task DiskFullDuringThumbnailCommitPublishesNoMetadataAndReleasesOwnership()
    {
        const int diskFullHResult = unchecked((int)0x80070070);
        var repository = new CountingMetadataRepository();
        var store = new DiskFullOnSecondCommitAssetStore(diskFullHResult);
        using var coordinator = new ClipboardImageAssetCoordinator(
            repository,
            store,
            new ClipboardImageCaptureOptions(),
            TimeSpan.FromSeconds(2));
        var result = CreateResult(
            Hash(45),
            EncodePng(2, 2, [
                1, 2, 3, 255,
                4, 5, 6, 128,
                7, 8, 9, 64,
                10, 11, 12, 255,
            ]),
            2,
            2,
            EncodePng(1, 1, [1, 2, 3, 255]),
            1,
            1);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            coordinator.CommitAsync(
                result,
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));

        Assert.Equal(diskFullHResult, exception.HResult);
        Assert.True(result.IsDisposed);
        Assert.Equal(2, store.CommitCount);
        Assert.Equal(0, repository.UpsertCount);
        Assert.Equal(0, coordinator.OutstandingJobCount);
    }

    [Fact]
    public async Task CapacityAndQueuedCancellationReleaseEveryOwnedResultOnce()
    {
        var store = new BlockingAssetStore();
        using var coordinator = new ClipboardImageAssetCoordinator(
            new InMemoryMetadataRepository(),
            store,
            new ClipboardImageCaptureOptions(imageWorkerCapacity: 2),
            TimeSpan.FromSeconds(2));
        var preCancelled = CreateResult(
            Hash(49),
            EncodePng(1, 1, [0, 0, 0, 255]),
            1,
            1,
            EncodePng(1, 1, [0, 0, 0, 255]),
            1,
            1);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                coordinator.CommitAsync(
                    preCancelled,
                    DateTimeOffset.UtcNow,
                    cancellation.Token));
        }

        Assert.True(preCancelled.IsDisposed);
        Assert.Equal(0, coordinator.OutstandingJobCount);
        Assert.False(store.Entered.IsSet);
        var first = CreateResult(
            Hash(50),
            EncodePng(1, 1, [1, 2, 3, 255]),
            1,
            1,
            EncodePng(1, 1, [1, 2, 3, 255]),
            1,
            1);
        var second = CreateResult(
            Hash(51),
            EncodePng(1, 1, [4, 5, 6, 255]),
            1,
            1,
            EncodePng(1, 1, [4, 5, 6, 255]),
            1,
            1);
        var rejected = CreateResult(
            Hash(52),
            EncodePng(1, 1, [7, 8, 9, 255]),
            1,
            1,
            EncodePng(1, 1, [7, 8, 9, 255]),
            1,
            1);
        var firstTask = coordinator.CommitAsync(
            first,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        Assert.True(store.Entered.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        using var queuedCancellation = new CancellationTokenSource();
        var secondTask = coordinator.CommitAsync(
            second,
            DateTimeOffset.UtcNow,
            queuedCancellation.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(
                rejected,
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));
        Assert.True(rejected.IsDisposed);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);
        Assert.True(second.IsDisposed);
        store.Release.Set();
        _ = await firstTask;
        Assert.True(first.IsDisposed);
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.OutstandingJobCount == 0,
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DisposeCancelsActiveAndQueuedJobsIdempotently()
    {
        var store = new BlockingAssetStore();
        var coordinator = new ClipboardImageAssetCoordinator(
            new InMemoryMetadataRepository(),
            store,
            new ClipboardImageCaptureOptions(imageWorkerCapacity: 2),
            TimeSpan.FromSeconds(2));
        var active = CreateResult(
            Hash(60),
            EncodePng(1, 1, [1, 1, 1, 255]),
            1,
            1,
            EncodePng(1, 1, [1, 1, 1, 255]),
            1,
            1);
        var queued = CreateResult(
            Hash(61),
            EncodePng(1, 1, [2, 2, 2, 255]),
            1,
            1,
            EncodePng(1, 1, [2, 2, 2, 255]),
            1,
            1);
        var activeTask = coordinator.CommitAsync(
            active,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        Assert.True(store.Entered.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        var queuedTask = coordinator.CommitAsync(
            queued,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        coordinator.Dispose();
        coordinator.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedTask);
        Assert.True(active.IsDisposed);
        Assert.True(queued.IsDisposed);
        Assert.Equal(0, coordinator.OutstandingJobCount);
        Assert.False(coordinator.IsWorkerThreadAlive);
        Assert.True(store.IsDisposed);
    }

    [Fact]
    public async Task ShutdownWatchdogReturnsAndWorkerReleasesAfterUninterruptibleIoEnds()
    {
        var store = new UninterruptibleAssetStore();
        var coordinator = new ClipboardImageAssetCoordinator(
            new InMemoryMetadataRepository(),
            store,
            new ClipboardImageCaptureOptions(imageWorkerCapacity: 1),
            TimeSpan.FromMilliseconds(100));
        var result = CreateResult(
            Hash(62),
            EncodePng(1, 1, [3, 3, 3, 255]),
            1,
            1,
            EncodePng(1, 1, [3, 3, 3, 255]),
            1,
            1);
        var task = coordinator.CommitAsync(
            result,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        Assert.True(store.Entered.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        var stopwatch = Stopwatch.StartNew();

        coordinator.Dispose();

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(coordinator.IsWorkerThreadAlive);
        Assert.False(result.IsDisposed);
        store.Release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(SpinWait.SpinUntil(
            () => !coordinator.IsWorkerThreadAlive &&
                  coordinator.OutstandingJobCount == 0 &&
                  store.IsDisposed,
            TimeSpan.FromSeconds(2)));
        Assert.True(result.IsDisposed);
    }

    private static ClipboardImageNormalizationResult CreateResult(
        string semanticHash,
        byte[] canonical,
        int width,
        int height,
        byte[] thumbnail,
        int thumbnailWidth,
        int thumbnailHeight,
        int? dpiXMilli = null,
        int? dpiYMilli = null) =>
        new(
            ClipboardRepresentationKind.Png,
            semanticHash,
            width,
            height,
            hasAlpha: true,
            dpiXMilli,
            dpiYMilli,
            thumbnailWidth,
            thumbnailHeight,
            new MemoryPayloadLease(canonical),
            new MemoryPayloadLease(thumbnail));

    private static byte[] EncodePng(int width, int height, byte[] bgraPixels)
    {
        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            bgraPixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static string Hash(int value) =>
        value.ToString("x64", CultureInfo.InvariantCulture);

    private static string GetAssetPath(string root, string hash) =>
        Path.Combine(root, hash[..2], $"{hash}.png");

    private static SqliteConnection OpenDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long ExecuteInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Vok.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class MemoryPayloadLease(byte[] bytes) : IClipboardPayloadLease
    {
        private int _disposed;

        public long Length => bytes.Length;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new MemoryStream(bytes, writable: false);
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class ThrowingMetadataRepository : InMemoryMetadataRepository
    {
        public override Task<ClipboardImageUpsertResult> UpsertImageAsync(
            ClipboardImageContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ClipboardImageUpsertResult>(
                new InvalidOperationException("Injected database failure."));
    }

    private sealed class CountingMetadataRepository : InMemoryMetadataRepository
    {
        private int _upsertCount;

        public int UpsertCount => Volatile.Read(ref _upsertCount);

        public override Task<ClipboardImageUpsertResult> UpsertImageAsync(
            ClipboardImageContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _upsertCount);
            return base.UpsertImageAsync(content, capturedUtc, cancellationToken);
        }
    }

    private class InMemoryMetadataRepository : IClipboardImageMetadataRepository
    {
        public virtual Task<ClipboardImageUpsertResult> UpsertImageAsync(
            ClipboardImageContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default)
        {
            var primary = new ClipboardAssetReference(
                content.PrimaryPng.Hash,
                ClipboardAssetState.Ready,
                1);
            var thumbnail = new ClipboardAssetReference(
                content.ThumbnailPng.Hash,
                ClipboardAssetState.Ready,
                1);
            var image = new ClipboardImageMetadata(
                content.PixelWidth,
                content.PixelHeight,
                content.HasAlpha,
                content.DpiXMilli,
                content.DpiYMilli,
                primary,
                thumbnail);
            var entry = new ClipboardEntry(
                Guid.NewGuid(),
                ClipboardContentKind.Image,
                content.SemanticHash,
                $"{content.PixelWidth}×{content.PixelHeight}",
                capturedUtc,
                capturedUtc,
                1,
                1,
                false,
                1,
                Image: image);
            return Task.FromResult(new ClipboardImageUpsertResult(entry, []));
        }

        public Task<ClipboardRemovalResult> RemoveWithAssetClaimsAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardRemovalResult(false, []));

        public Task<IReadOnlyList<ClipboardAssetDeleteClaim>> LoadPendingAssetDeletesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClipboardAssetDeleteClaim>>([]);

        public Task<ClipboardStoredAsset?> LoadAssetAsync(
            string assetHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ClipboardStoredAsset?>(null);

        public Task<bool> IsAssetDeleteClaimCurrentAsync(
            ClipboardAssetDeleteClaim claim,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> CompleteAssetDeleteAsync(
            ClipboardAssetDeleteClaim claim,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<ClipboardAssetReference?> MarkAssetQuarantinedAsync(
            string assetHash,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ClipboardAssetReference?>(null);
    }

    private sealed class BlockingAssetStore : IClipboardContentAssetStore
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public bool IsDisposed { get; private set; }

        public ClipboardPreparedAsset CommitPng(
            Stream source,
            long expectedLength,
            int expectedPixelWidth,
            int expectedPixelHeight,
            CancellationToken cancellationToken = default)
        {
            Entered.Set();
            Release.Wait(cancellationToken);
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var hash = Convert.ToHexString(SHA256.HashData(memory.ToArray()))
                .ToLowerInvariant();
            return new ClipboardPreparedAsset(
                hash,
                $"{hash[..2]}/{hash}.png",
                "image/png",
                expectedLength,
                expectedPixelWidth,
                expectedPixelHeight);
        }

        public void ValidatePng(
            ClipboardPreparedAsset asset,
            CancellationToken cancellationToken = default)
        {
        }

        public byte[] ReadPng(
            ClipboardPreparedAsset asset,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IClipboardPayloadLease OpenVerifiedPng(
            ClipboardPreparedAsset asset,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Delete(ClipboardAssetDeleteClaim claim)
        {
        }

        public void Dispose()
        {
            IsDisposed = true;
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class DiskFullOnSecondCommitAssetStore(int diskFullHResult)
        : IClipboardContentAssetStore
    {
        private int _commitCount;

        public int CommitCount => Volatile.Read(ref _commitCount);

        public ClipboardPreparedAsset CommitPng(
            Stream source,
            long expectedLength,
            int expectedPixelWidth,
            int expectedPixelHeight,
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _commitCount);
            if (attempt == 2)
            {
                _ = source.ReadByte();
                throw new IOException(
                    "Injected disk-full failure while committing the thumbnail.",
                    diskFullHResult);
            }

            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var hash = Convert.ToHexString(SHA256.HashData(memory.ToArray()))
                .ToLowerInvariant();
            return new ClipboardPreparedAsset(
                hash,
                $"{hash[..2]}/{hash}.png",
                "image/png",
                expectedLength,
                expectedPixelWidth,
                expectedPixelHeight);
        }

        public void ValidatePng(
            ClipboardPreparedAsset asset,
            CancellationToken cancellationToken = default)
        {
        }

        public byte[] ReadPng(
            ClipboardPreparedAsset asset,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IClipboardPayloadLease OpenVerifiedPng(
            ClipboardPreparedAsset asset,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Delete(ClipboardAssetDeleteClaim claim)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class UninterruptibleAssetStore : IClipboardContentAssetStore
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public bool IsDisposed { get; private set; }

        public ClipboardPreparedAsset CommitPng(
            Stream source,
            long expectedLength,
            int expectedPixelWidth,
            int expectedPixelHeight,
            CancellationToken cancellationToken = default)
        {
            Entered.Set();
            Release.Wait();
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var hash = Convert.ToHexString(SHA256.HashData(memory.ToArray()))
                .ToLowerInvariant();
            return new ClipboardPreparedAsset(
                hash,
                $"{hash[..2]}/{hash}.png",
                "image/png",
                expectedLength,
                expectedPixelWidth,
                expectedPixelHeight);
        }

        public void ValidatePng(
            ClipboardPreparedAsset asset,
            CancellationToken cancellationToken = default)
        {
        }

        public byte[] ReadPng(
            ClipboardPreparedAsset asset,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IClipboardPayloadLease OpenVerifiedPng(
            ClipboardPreparedAsset asset,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Delete(ClipboardAssetDeleteClaim claim)
        {
        }

        public void Dispose()
        {
            IsDisposed = true;
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
