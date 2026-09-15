using System.IO;
using Vok.Core.Stage;
using Vok.Features.Stage;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class StageViewModelTests
{
    [Fact]
    public async Task NavigationTraversesTenThousandItemsWithOneBoundedRollingWindow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryStageRepository(CreateItems(10_000));
        using var viewModel = new StageViewModel(repository, new StaticStageFileService([]));

        await viewModel.InitializeAsync(cancellationToken);
        await viewModel.InitializeAsync(cancellationToken);

        Assert.Equal(10_000, viewModel.TotalCount);
        Assert.Equal(StageViewModel.PageSize, viewModel.Items.Count);
        Assert.False(viewModel.CanLoadNewer);
        Assert.True(viewModel.CanLoadOlder);
        Assert.Equal(1, repository.FirstPageLoadCount);

        var visited = new HashSet<Guid>(viewModel.Items.Select(static item => item.Id));
        while (true)
        {
            Assert.InRange(
                viewModel.Items.Count,
                1,
                StageRollingWindow.DefaultSegmentSize *
                    StageRollingWindow.DefaultMaximumSegmentCount);
            visited.UnionWith(viewModel.Items.Select(static item => item.Id));
            if (!viewModel.CanLoadOlder)
            {
                break;
            }

            await viewModel.LoadOlderPageAsync(cancellationToken);
        }

        Assert.Equal(10_000, visited.Count);
        Assert.InRange(viewModel.Items.Count, 1, 192);
        Assert.True(viewModel.CanLoadNewer);

        while (viewModel.CanLoadNewer)
        {
            await viewModel.LoadNewerPageAsync(cancellationToken);
            Assert.InRange(viewModel.Items.Count, 1, 192);
        }

        Assert.Equal(repository.Items[0].Id, viewModel.Items[0].Id);
        Assert.Equal("已加载 192 / 10000", viewModel.PageSummaryText);
    }

    [Fact]
    public async Task EdgeLoadsPreserveRetainedProjectionIdentityAndEvictOnlyOppositeSegment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryStageRepository(CreateItems(256));
        using var viewModel = new StageViewModel(repository, new StaticStageFileService([]));
        await viewModel.InitializeAsync(cancellationToken);
        var firstSegment = viewModel.Items.ToArray();
        Assert.True(firstSegment[0].ApplyAvailability(
            firstSegment[0].Revision,
            StageFileAvailability.Missing));

        await viewModel.LoadOlderPageAsync(cancellationToken);
        Assert.Equal(128, viewModel.Items.Count);
        Assert.All(firstSegment, item => Assert.Contains(item, viewModel.Items));
        Assert.True(firstSegment[0].IsMissing);
        var secondSegment = viewModel.Items.Skip(64).Take(64).ToArray();

        await viewModel.LoadOlderPageAsync(cancellationToken);
        await viewModel.LoadOlderPageAsync(cancellationToken);

        Assert.Equal(192, viewModel.Items.Count);
        Assert.DoesNotContain(firstSegment[0], viewModel.Items);
        Assert.All(secondSegment, item => Assert.Contains(item, viewModel.Items));
        var retainedMiddle = viewModel.Items.Skip(64).Take(64).ToArray();

        await viewModel.LoadNewerPageAsync(cancellationToken);

        Assert.Equal(192, viewModel.Items.Count);
        Assert.NotSame(firstSegment[0], viewModel.Items[0]);
        Assert.False(viewModel.Items[0].HasAvailabilityResult);
        Assert.All(secondSegment, item => Assert.Contains(item, viewModel.Items));
        Assert.All(retainedMiddle, item => Assert.Contains(item, viewModel.Items));
        Assert.Equal(
            repository.Items.Take(192).Select(static item => item.Id),
            viewModel.Items.Select(static item => item.Id));
    }

    [Fact]
    public async Task EdgeTotalDriftPerformsOneNewestResetWithoutApplyingTheDriftPage()
    {
        var newest = CreateItems(StageViewModel.PageSize);
        var outside = Assert.Single(CreateItems(1, "边界")) with
        {
            Id = CreateDeterministicGuid(3_000),
            SortOrder = 0,
        };
        var firstPage = CreatePage(newest) with
        {
            OlderCursor = new StagePageCursor(newest[^1].SortOrder, newest[^1].Id),
            OlderBoundary = ToOrderAnchor(outside),
            TotalCount = newest.Count + 1,
        };
        var driftPage = new StagePage(
            [outside],
            NewerCursor: new StagePageCursor(outside.SortOrder, outside.Id),
            OlderCursor: null,
            TotalCount: newest.Count + 2,
            NewerBoundary: ToOrderAnchor(newest[^1]));
        var resetItems = CreateItems(2, "重置");
        var repository = new ScriptedStageRepository(
            pages: [firstPage, driftPage, CreatePage(resetItems)]);
        using var viewModel = new StageViewModel(repository, new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.LoadOlderPageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, repository.LoadCalls.Count);
        Assert.Equal(
            [
                (StagePageCursor?)null,
                new StagePageCursor(newest[^1].SortOrder, newest[^1].Id),
                null,
            ],
            repository.LoadCalls.Select(static call => call.Anchor));
        Assert.Equal(
            resetItems.Select(static item => item.Id),
            viewModel.Items.Select(static item => item.Id));
        Assert.DoesNotContain(viewModel.Items, item => item.Id == outside.Id);
        Assert.Equal(resetItems.Count, viewModel.TotalCount);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task AppliedDropWritesOnlyValidTargetsAndReloadsAuthoritativeFirstPage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var original = CreateItems(2);
        var committed = CreateItems(3, namePrefix: "已提交");
        var repository = new ScriptedStageRepository(
            pages:
            [
                CreatePage(original),
                CreatePage(committed),
            ],
            addResult: new StageAddBatchResult(
                StageAddBatchStatus.Applied,
                CountBefore: 2,
                CountAfter: 3,
                AddedCount: 1,
                Capacity: 200));
        var valid = new StageTargetDescriptor(
            StageItemKind.File,
            @"C:\Temp\valid.txt",
            @"C:\TEMP\VALID.TXT",
            "valid.txt");
        var fileService = new StaticStageFileService(
        [
            new StageTargetResolution(@"C:\Temp\valid.txt", valid, null, null),
            new StageTargetResolution(
                @"C:\Temp\missing.txt",
                null,
                StageTargetFailureKind.Missing,
                "missing"),
        ]);
        using var viewModel = new StageViewModel(repository, fileService);
        await viewModel.InitializeAsync(cancellationToken);

        await viewModel.AddDroppedPathsAsync(
            [@"C:\Temp\valid.txt", @"C:\Temp\missing.txt"],
            cancellationToken);

        Assert.Equal(
            [@"C:\Temp\valid.txt", @"C:\Temp\missing.txt"],
            Assert.Single(fileService.Inputs));
        Assert.Equal(valid, Assert.Single(Assert.Single(repository.AddCalls)));
        Assert.Equal(committed.Select(static item => item.Id), viewModel.Items.Select(static item => item.Id));
        Assert.Equal(3, viewModel.TotalCount);
        Assert.Equal("新增 1 项，1 项无效。", viewModel.StatusText);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task AppliedDropPreservesSameBatchDuplicateDescriptorsInSourceOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var descriptors = new[]
        {
            new StageTargetDescriptor(
                StageItemKind.File,
                @"C:\Stage\repeat.txt",
                @"C:\STAGE\REPEAT.TXT",
                "first"),
            new StageTargetDescriptor(
                StageItemKind.File,
                @"c:\stage\REPEAT.TXT",
                @"c:\stage\repeat.txt",
                "second"),
            new StageTargetDescriptor(
                StageItemKind.File,
                @"C:\Stage\repeat.txt",
                @"C:\STAGE\REPEAT.TXT",
                "third"),
        };
        var committed = CreateItems(3, namePrefix: "已提交");
        var repository = new ScriptedStageRepository(
            pages:
            [
                CreatePage([]),
                CreatePage(committed),
            ],
            addResult: new StageAddBatchResult(
                StageAddBatchStatus.Applied,
                CountBefore: 0,
                CountAfter: 3,
                AddedCount: 3,
                Capacity: 50));
        var resolutions = descriptors
            .Select(descriptor => new StageTargetResolution(
                descriptor.TargetValue,
                descriptor,
                null,
                null))
            .ToArray();
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService(resolutions));
        await viewModel.InitializeAsync(cancellationToken);

        await viewModel.AddDroppedPathsAsync(
            descriptors.Select(static descriptor => descriptor.TargetValue).ToArray(),
            cancellationToken);

        Assert.Equal(descriptors, Assert.Single(repository.AddCalls));
        Assert.Equal(3, viewModel.TotalCount);
        Assert.Equal("新增 3 项。", viewModel.StatusText);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task DropSeparatesRejectedUnknownAndCommittedButRefreshFailedResults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var valid = new StageTargetDescriptor(
            StageItemKind.Directory,
            @"C:\Temp",
            @"C:\TEMP",
            "Temp");
        var resolution = new StageTargetResolution(@"C:\Temp", valid, null, null);

        var capacityRepository = new ScriptedStageRepository(
            pages: [CreatePage(CreateItems(1))],
            addResult: new StageAddBatchResult(
                StageAddBatchStatus.CapacityExceeded,
                1,
                1,
                0,
                1));
        using (var capacityViewModel = new StageViewModel(
                   capacityRepository,
                   new StaticStageFileService([resolution])))
        {
            await capacityViewModel.InitializeAsync(cancellationToken);
            var before = capacityViewModel.Items.Select(static item => item.Id).ToArray();

            await capacityViewModel.AddDroppedPathsAsync([@"C:\Temp"], cancellationToken);

            Assert.Equal(before, capacityViewModel.Items.Select(static item => item.Id));
            Assert.Contains("整批未放入", capacityViewModel.StatusText, StringComparison.Ordinal);
            Assert.False(capacityViewModel.HasError);
        }

        var refreshFailureRepository = new ScriptedStageRepository(
            pages: [CreatePage(CreateItems(1))],
            addResult: new StageAddBatchResult(
                StageAddBatchStatus.Applied,
                1,
                2,
                1,
                200),
            failPageLoadAfter: 1);
        using (var refreshFailureViewModel = new StageViewModel(
                   refreshFailureRepository,
                   new StaticStageFileService([resolution])))
        {
            await refreshFailureViewModel.InitializeAsync(cancellationToken);

            await refreshFailureViewModel.AddDroppedPathsAsync(
                [@"C:\Temp"],
                cancellationToken);

            Assert.StartsWith("项目已保存，但列表刷新失败", refreshFailureViewModel.StatusText);
            Assert.True(refreshFailureViewModel.HasError);
        }

        var unknownRepository = new ScriptedStageRepository(
            pages: [CreatePage(CreateItems(1))],
            addException: new IOException("database result unavailable"));
        using var unknownViewModel = new StageViewModel(
            unknownRepository,
            new StaticStageFileService([resolution]));
        await unknownViewModel.InitializeAsync(cancellationToken);

        await unknownViewModel.AddDroppedPathsAsync([@"C:\Temp"], cancellationToken);

        Assert.Contains("结果待核对", unknownViewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("已保存", unknownViewModel.StatusText, StringComparison.Ordinal);
        Assert.True(unknownViewModel.HasError);
    }

    [Fact]
    public async Task QueuedDropCannotBeOverwrittenByAnOlderPageQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstPage = CreatePage(CreateItems(64));
        var olderPage = CreatePage(CreateItems(10, namePrefix: "较早")
            .Select((item, index) => item with
            {
                Id = CreateDeterministicGuid(1_000 + index),
                SortOrder = -index - 1,
            })
            .ToArray());
        var committedPage = CreatePage(CreateItems(3, namePrefix: "提交后"));
        var repository = new DelayedNavigationStageRepository(
            firstPage,
            olderPage,
            committedPage);
        var descriptor = new StageTargetDescriptor(
            StageItemKind.File,
            @"C:\Temp\new.txt",
            @"C:\TEMP\NEW.TXT",
            "new.txt");
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService(
                [new StageTargetResolution(descriptor.TargetValue, descriptor, null, null)]));
        await viewModel.InitializeAsync(cancellationToken);

        var navigation = viewModel.LoadOlderPageAsync(cancellationToken);
        await repository.NavigationStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            cancellationToken);
        var drop = viewModel.AddDroppedPathsAsync([descriptor.TargetValue], cancellationToken);
        Assert.False(drop.IsCompleted);

        repository.ReleaseNavigation.SetResult();
        await Task.WhenAll(navigation, drop);

        Assert.Equal(
            committedPage.Items.Select(static item => item.Id),
            viewModel.Items.Select(static item => item.Id));
        Assert.False(viewModel.CanLoadNewer);
        Assert.Equal(1, repository.AddCallCount);
    }

    [Fact]
    public async Task HundredAlternatingDuplicateDropsAndPrecancellationsStayBounded()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Vok.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "stage-vm-cycles.db");
            using var repository = new SqliteStageRepository(
                databasePath,
                new StageOptions(capacity: 50));
            var descriptor = new StageTargetDescriptor(
                StageItemKind.File,
                @"C:\Stage\repeat.txt",
                @"C:\STAGE\REPEAT.TXT",
                "repeat.txt");
            using var viewModel = new StageViewModel(
                repository,
                new StaticStageFileService(
                    [new StageTargetResolution(
                        descriptor.TargetValue,
                        descriptor,
                        null,
                        null)]));
            await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

            for (var index = 0; index < 100; index++)
            {
                if (index % 2 == 0)
                {
                    await viewModel.AddDroppedPathsAsync(
                        [descriptor.TargetValue],
                        TestContext.Current.CancellationToken);
                }
                else
                {
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        viewModel.AddDroppedPathsAsync(
                            [descriptor.TargetValue],
                            cancellation.Token));
                }

                Assert.InRange(viewModel.Items.Count, 0, StageViewModel.PageSize);
                Assert.False(viewModel.IsLoading);
            }

            Assert.Equal(50, viewModel.Items.Count);
            Assert.All(
                viewModel.Items,
                item => Assert.Equal(descriptor.TargetValue, item.TargetValue));
            Assert.Equal(
                50,
                viewModel.Items.Select(static item => item.Id).Distinct().Count());
            Assert.Equal(50, viewModel.TotalCount);
            Assert.False(viewModel.HasError);

            var page = await repository.LoadPageAsync(
                null,
                StageViewModel.PageSize,
                TestContext.Current.CancellationToken);
            Assert.Equal(50, page.Items.Count);
            Assert.All(page.Items, item => Assert.Equal(1, item.Revision));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConfirmedRemovalReloadsTheAuthoritativePageAndExplainsFileSafety()
    {
        var original = CreateItems(2);
        var repository = new ScriptedStageRepository(
            pages:
            [
                CreatePage(original),
                CreatePage([original[1]]),
            ],
            removeResult: new StageRemoveResult(StageRemoveStatus.Removed));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var removed = viewModel.Items[0];

        await viewModel.RemoveCommand.ExecuteAsync(removed);

        Assert.Equal((removed.Id, removed.Revision), Assert.Single(repository.RemoveCalls));
        Assert.Equal(original[1].Id, Assert.Single(viewModel.Items).Id);
        Assert.Equal(1, viewModel.TotalCount);
        Assert.Contains("原文件未受影响", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task RevisionConflictDoesNotRemoveTheStaleProjectionAndReloadsAuthority()
    {
        var original = CreateItems(2);
        var refreshed = original[0] with
        {
            ResolvedName = "项目已回顶",
            Revision = original[0].Revision + 1,
            UpdatedUtc = original[0].UpdatedUtc.AddSeconds(1),
        };
        var repository = new ScriptedStageRepository(
            pages:
            [
                CreatePage(original),
                CreatePage([refreshed, original[1]]),
            ],
            removeResult: new StageRemoveResult(StageRemoveStatus.RevisionConflict));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.RemoveCommand.ExecuteAsync(viewModel.Items[0]);

        Assert.Equal(2, viewModel.Items.Count);
        Assert.Equal(refreshed.Revision, viewModel.Items[0].Revision);
        Assert.Contains("未执行移除", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task ConfirmedRemovalIsNotReplayedWhenPageRefreshFails()
    {
        var original = CreateItems(2);
        var repository = new ScriptedStageRepository(
            pages: [CreatePage(original)],
            removeResult: new StageRemoveResult(StageRemoveStatus.Removed),
            failPageLoadAfter: 1);
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var removed = viewModel.Items[0];

        await viewModel.RemoveCommand.ExecuteAsync(removed);

        Assert.Single(repository.RemoveCalls);
        Assert.Contains(viewModel.Items, item => item.Id == removed.Id);
        Assert.Equal(2, viewModel.TotalCount);
        Assert.False(viewModel.CanLoadNewer);
        Assert.False(viewModel.CanLoadOlder);
        Assert.True(viewModel.HasError);
        Assert.Contains("已移除", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("列表刷新失败", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task RemovingTheLastItemOnTheLastPageReturnsToThePreviousPage()
    {
        var firstItems = CreateItems(StageViewModel.PageSize, "较新");
        var lastItem = Assert.Single(CreateItems(1, "最旧")) with
        {
            Id = CreateDeterministicGuid(2_000),
            SortOrder = 0,
        };
        var firstPage = CreatePage(firstItems) with
        {
            OlderCursor = new StagePageCursor(
                firstItems[^1].SortOrder,
                firstItems[^1].Id),
            OlderBoundary = ToOrderAnchor(lastItem),
            TotalCount = StageViewModel.PageSize + 1,
        };
        var lastPage = new StagePage(
            [lastItem],
            NewerCursor: new StagePageCursor(lastItem.SortOrder, lastItem.Id),
            OlderCursor: null,
            TotalCount: StageViewModel.PageSize + 1,
            NewerBoundary: ToOrderAnchor(firstItems[^1]));
        var repository = new ScriptedStageRepository(
            pages:
            [
                firstPage,
                lastPage,
                CreatePage(firstItems),
            ],
            removeResult: new StageRemoveResult(StageRemoveStatus.Removed));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        await viewModel.LoadOlderPageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            StageViewModel.PageSize + 1,
            viewModel.Items.Count);
        Assert.Equal(lastItem.Id, viewModel.Items[^1].Id);

        await viewModel.RemoveCommand.ExecuteAsync(viewModel.Items[^1]);

        Assert.Equal(StageViewModel.PageSize, viewModel.Items.Count);
        Assert.Equal(firstItems[0].Id, viewModel.Items[0].Id);
        Assert.Equal(StageViewModel.PageSize, viewModel.TotalCount);
        Assert.Equal("已加载 64 / 64", viewModel.PageSummaryText);
        Assert.False(viewModel.CanLoadNewer);
        Assert.False(viewModel.CanLoadOlder);
    }

    [Fact]
    public async Task AppliedPinUsesTheAuthoritativeItemAndInvalidatesOldAvailability()
    {
        var original = Assert.Single(CreateItems(1));
        var authoritative = original with
        {
            IsPinned = true,
            Revision = original.Revision + 1,
            UpdatedUtc = original.UpdatedUtc.AddSeconds(1),
        };
        var repository = new ScriptedStageRepository(
            pages: [CreatePage([original])],
            setPinnedResult: new StageItemMutationResult(
                StageItemMutationStatus.Applied,
                authoritative));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var projection = Assert.Single(viewModel.Items);
        Assert.True(projection.ApplyAvailability(
            projection.Revision,
            StageFileAvailability.Missing));
        StageItemViewModel? authorityChanged = null;
        viewModel.ItemAuthorityChanged += item => authorityChanged = item;

        await viewModel.TogglePinnedCommand.ExecuteAsync(projection);

        Assert.Equal(
            (original.Id, original.Revision, true),
            Assert.Single(repository.SetPinnedCalls));
        Assert.Same(projection, Assert.Single(viewModel.Items));
        Assert.Same(projection, authorityChanged);
        Assert.Equal(authoritative, projection.Item);
        Assert.True(projection.IsPinned);
        Assert.Equal(original.Revision + 1, projection.Revision);
        Assert.False(projection.HasAvailabilityStatus);
        Assert.False(projection.IsMissing);
        Assert.Contains("已固定", projection.AutomationName, StringComparison.Ordinal);
        Assert.Contains("已固定", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task PinRevisionConflictReloadsAuthorityInsteadOfOverwritingIt()
    {
        var original = Assert.Single(CreateItems(1));
        var refreshed = original with
        {
            ResolvedName = "外部已更新",
            Revision = original.Revision + 1,
            UpdatedUtc = original.UpdatedUtc.AddSeconds(1),
        };
        var repository = new ScriptedStageRepository(
            pages:
            [
                CreatePage([original]),
                CreatePage([refreshed]),
            ],
            setPinnedResult: new StageItemMutationResult(
                StageItemMutationStatus.RevisionConflict,
                null));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.TogglePinnedCommand.ExecuteAsync(viewModel.Items[0]);

        var current = Assert.Single(viewModel.Items);
        Assert.Equal(refreshed, current.Item);
        Assert.False(current.IsPinned);
        Assert.Contains("未更改固定状态", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task ConfirmedPinIsNotReplayedWhenProjectionApplyNeedsAuthorityReload()
    {
        var original = Assert.Single(CreateItems(1));
        var authoritative = original with
        {
            IsPinned = true,
            Revision = original.Revision + 1,
            UpdatedUtc = original.UpdatedUtc.AddSeconds(1),
        };
        var invalidProjectionResult = authoritative with { Id = Guid.NewGuid() };
        var repository = new ScriptedStageRepository(
            pages:
            [
                CreatePage([original]),
                CreatePage([authoritative]),
            ],
            setPinnedResult: new StageItemMutationResult(
                StageItemMutationStatus.Applied,
                invalidProjectionResult));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.TogglePinnedCommand.ExecuteAsync(viewModel.Items[0]);

        Assert.Single(repository.SetPinnedCalls);
        Assert.Equal(authoritative, Assert.Single(viewModel.Items).Item);
        Assert.Contains("列表已重新加载", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task CancelledRelinkDoesNotResolveOrCallTheRepository()
    {
        var original = Assert.Single(CreateItems(1));
        var repository = new ScriptedStageRepository(
            pages: [CreatePage([original])]);
        var files = new StaticStageFileService([]);
        using var viewModel = new StageViewModel(repository, files)
        {
            RelinkTargetPickerAsync = static (_, _) =>
                Task.FromResult(StageRelinkSelection.Cancelled),
        };
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.RelinkCommand.ExecuteAsync(viewModel.Items[0]);

        Assert.Empty(files.Inputs);
        Assert.Empty(repository.RelinkCalls);
        Assert.Contains("已取消重新关联", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task AppliedRelinkUsesAuthorityAndInvalidatesOldAvailability()
    {
        var original = Assert.Single(CreateItems(1));
        var target = new StageTargetDescriptor(
            StageItemKind.File,
            @"C:\Stage\Replacement.txt",
            @"C:\STAGE\REPLACEMENT.TXT",
            "Replacement.txt");
        var authoritative = original with
        {
            TargetValue = target.TargetValue,
            CanonicalKey = target.CanonicalKey,
            ResolvedName = target.ResolvedName,
            Revision = original.Revision + 1,
            UpdatedUtc = original.UpdatedUtc.AddSeconds(1),
        };
        var repository = new ScriptedStageRepository(
            pages: [CreatePage([original])],
            relinkResult: new StageRelinkResult(
                StageRelinkStatus.Applied,
                authoritative));
        var files = new StaticStageFileService(
            [new StageTargetResolution(target.TargetValue, target, null, null)]);
        using var viewModel = new StageViewModel(repository, files)
        {
            RelinkTargetPickerAsync = (_, _) => Task.FromResult(
                StageRelinkSelection.Selected(target.TargetValue)),
        };
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var projection = Assert.Single(viewModel.Items);
        Assert.True(projection.ApplyAvailability(
            projection.Revision,
            StageFileAvailability.Missing));
        StageItemViewModel? authorityChanged = null;
        viewModel.ItemAuthorityChanged += item => authorityChanged = item;

        await viewModel.RelinkCommand.ExecuteAsync(projection);

        Assert.Equal([target.TargetValue], Assert.Single(files.Inputs));
        var call = Assert.Single(repository.RelinkCalls);
        Assert.Equal(original.Id, call.ItemId);
        Assert.Equal(original.Revision, call.ExpectedRevision);
        Assert.Equal(target, call.Target);
        Assert.Same(projection, Assert.Single(viewModel.Items));
        Assert.Same(projection, authorityChanged);
        Assert.Equal(authoritative, projection.Item);
        Assert.False(projection.HasAvailabilityStatus);
        Assert.Contains("重新关联到", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task RelinkResolutionAndKindFailuresNeverReachTheRepository()
    {
        var original = Assert.Single(CreateItems(1));
        var repository = new ScriptedStageRepository(
            pages:
            [
                CreatePage([original]),
                CreatePage([original]),
            ]);
        var missingFiles = new StaticStageFileService(
            [new StageTargetResolution(
                @"C:\Missing.txt",
                null,
                StageTargetFailureKind.Missing,
                "目标不存在。")]);
        using (var missingViewModel = new StageViewModel(repository, missingFiles)
        {
            RelinkTargetPickerAsync = static (_, _) => Task.FromResult(
                StageRelinkSelection.Selected(@"C:\Missing.txt")),
        })
        {
            await missingViewModel.InitializeAsync(TestContext.Current.CancellationToken);
            await missingViewModel.RelinkCommand.ExecuteAsync(missingViewModel.Items[0]);
            Assert.Contains("目标不存在", missingViewModel.StatusText, StringComparison.Ordinal);
        }

        var directoryTarget = new StageTargetDescriptor(
            StageItemKind.Directory,
            @"C:\Folder",
            @"C:\FOLDER",
            "Folder");
        var wrongKindFiles = new StaticStageFileService(
            [new StageTargetResolution(
                directoryTarget.TargetValue,
                directoryTarget,
                null,
                null)]);
        using (var wrongKindViewModel = new StageViewModel(repository, wrongKindFiles)
        {
            RelinkTargetPickerAsync = (_, _) => Task.FromResult(
                StageRelinkSelection.Selected(directoryTarget.TargetValue)),
        })
        {
            await wrongKindViewModel.InitializeAsync(TestContext.Current.CancellationToken);
            await wrongKindViewModel.RelinkCommand.ExecuteAsync(wrongKindViewModel.Items[0]);
            Assert.Contains("不是文件", wrongKindViewModel.StatusText, StringComparison.Ordinal);
        }

        Assert.Empty(repository.RelinkCalls);
    }

    [Fact]
    public async Task RelinkConfirmedWriteWithInvalidAuthorityReloadsWithoutReplay()
    {
        var original = Assert.Single(CreateItems(1));
        var target = new StageTargetDescriptor(
            StageItemKind.File,
            @"C:\Stage\Existing.txt",
            @"C:\STAGE\EXISTING.TXT",
            "Existing.txt");
        var files = new StaticStageFileService(
            [new StageTargetResolution(target.TargetValue, target, null, null)]);
        var authoritative = original with
        {
            TargetValue = target.TargetValue,
            CanonicalKey = target.CanonicalKey,
            ResolvedName = target.ResolvedName,
            Revision = original.Revision + 1,
        };
        var invalidAuthority = authoritative with { Id = Guid.NewGuid() };
        var committedRepository = new ScriptedStageRepository(
            pages:
            [
                CreatePage([original]),
                CreatePage([authoritative]),
            ],
            relinkResult: new StageRelinkResult(
                StageRelinkStatus.Applied,
                invalidAuthority));
        using var committedViewModel = new StageViewModel(committedRepository, files)
        {
            RelinkTargetPickerAsync = (_, _) => Task.FromResult(
                StageRelinkSelection.Selected(target.TargetValue)),
        };
        await committedViewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await committedViewModel.RelinkCommand.ExecuteAsync(committedViewModel.Items[0]);

        Assert.Single(committedRepository.RelinkCalls);
        Assert.Equal(authoritative, Assert.Single(committedViewModel.Items).Item);
        Assert.Contains("列表已重新加载", committedViewModel.StatusText, StringComparison.Ordinal);
        Assert.False(committedViewModel.HasError);
    }

    [Fact]
    public async Task DisposedRelinkCannotResolveOrCommitALatePickerResult()
    {
        var original = Assert.Single(CreateItems(1));
        var repository = new ScriptedStageRepository(
            pages: [CreatePage([original])]);
        var files = new StaticStageFileService([]);
        var pickerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pickerResult = new TaskCompletionSource<StageRelinkSelection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new StageViewModel(repository, files)
        {
            RelinkTargetPickerAsync = (_, _) =>
            {
                pickerStarted.TrySetResult();
                return pickerResult.Task;
            },
        };
        try
        {
            await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
            var operation = viewModel.RelinkCommand.ExecuteAsync(viewModel.Items[0]);
            await pickerStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

            viewModel.Dispose();
            pickerResult.SetResult(
                StageRelinkSelection.Selected(@"C:\Stage\Late.txt"));
            await operation;

            Assert.Empty(files.Inputs);
            Assert.Empty(repository.RelinkCalls);
            Assert.False(viewModel.IsLoading);
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    [Fact]
    public void AvailabilityOnlyAppliesToTheExpectedItemRevision()
    {
        var item = new StageItemViewModel(Assert.Single(CreateItems(1)));

        Assert.False(item.HasAvailabilityStatus);
        Assert.False(item.ApplyAvailability(
            item.Revision + 1,
            StageFileAvailability.Missing));
        Assert.False(item.HasAvailabilityStatus);

        Assert.True(item.ApplyAvailability(
            item.Revision,
            StageFileAvailability.Missing));
        Assert.True(item.IsMissing);
        Assert.True(item.HasAvailabilityStatus);
        Assert.Equal("源文件已失效", item.AvailabilityText);
        Assert.Equal("已失效", item.CompactAvailabilityText);
        Assert.Equal("已失效", item.CompactMetaText);
        Assert.True(item.HasCompactStatus);
        Assert.Equal("已失效", item.CompactStatusText);
        Assert.Contains("源文件已失效", item.AutomationName, StringComparison.Ordinal);

        item.ResetAvailability(item.Revision + 1);
        Assert.True(item.HasAvailabilityStatus);
        item.ResetAvailability(item.Revision);
        Assert.False(item.HasAvailabilityStatus);
        Assert.False(item.IsMissing);
        Assert.False(item.HasCompactStatus);
        Assert.Equal(string.Empty, item.CompactStatusText);
        Assert.Equal("文件", item.CompactMetaText);
    }

    [Theory]
    [InlineData(StageItemKind.File, @"C:\Stage\report.docx", ".docx")]
    [InlineData(StageItemKind.File, @"C:\Stage\README", "文件")]
    [InlineData(StageItemKind.Directory, @"C:\Stage\Design", "文件夹")]
    [InlineData(StageItemKind.ShellLink, @"C:\Stage\Workbench.lnk", ".lnk")]
    public void CompactMetaUsesTypeOrExtensionUntilAvailabilityFails(
        StageItemKind kind,
        string target,
        string expectedMeta)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new StageItemViewModel(new StageItem(
            Guid.NewGuid(),
            kind,
            target,
            target.ToUpperInvariant(),
            Path.GetFileName(target),
            1,
            false,
            1,
            now,
            now));

        Assert.Equal(expectedMeta, item.CompactMetaText);

        Assert.True(item.ApplyAvailability(
            item.Revision,
            StageFileAvailability.Unavailable));
        Assert.Equal("暂不可用", item.CompactMetaText);

        item.ResetAvailability(item.Revision);
        Assert.Equal(expectedMeta, item.CompactMetaText);
    }

    [Theory]
    [InlineData(StageFileAvailability.Available, "", false)]
    [InlineData(StageFileAvailability.Missing, "已失效", true)]
    [InlineData(StageFileAvailability.Unavailable, "暂不可用", true)]
    [InlineData(StageFileAvailability.Unknown, "状态未知", true)]
    public void CompactStatusUsesPresentationOnlyText(
        StageFileAvailability availability,
        string expectedText,
        bool expectedVisible)
    {
        var source = Assert.Single(CreateItems(1));
        var item = new StageItemViewModel(source);

        Assert.True(item.ApplyAvailability(item.Revision, availability));

        Assert.Equal(expectedText, item.CompactAvailabilityText);
        Assert.Equal(expectedText, item.CompactStatusText);
        Assert.Equal(expectedVisible, item.HasCompactStatus);
        Assert.Equal(
            availability is StageFileAvailability.Missing
                ? "源文件已失效"
                : availability is StageFileAvailability.Unavailable
                    ? "暂不可访问"
                    : availability is StageFileAvailability.Unknown
                        ? "位置状态未知"
                        : string.Empty,
            item.AvailabilityText);

        item.Update(source with
        {
            IsPinned = true,
            Revision = source.Revision + 1,
        });

        Assert.True(item.HasCompactStatus);
        Assert.Equal("固定", item.CompactStatusText);
        Assert.DoesNotContain("固定 ·", item.AutomationName, StringComparison.Ordinal);
        Assert.Contains("已固定", item.AutomationName, StringComparison.Ordinal);

        Assert.True(item.ApplyAvailability(item.Revision, availability));
        Assert.Equal(
            expectedText.Length == 0 ? "固定" : $"固定 · {expectedText}",
            item.CompactStatusText);
    }

    [Fact]
    public async Task SamePageReorderPreviewDoesNotMutateProjectionAndCancelsWithoutWrite()
    {
        var original = CreateItems(4);
        var repository = new ScriptedStageRepository([CreatePage(original)]);
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var source = viewModel.Items[2];
        var originalIds = viewModel.Items.Select(static item => item.Id).ToArray();
        var projectionChanges = 0;
        viewModel.Items.CollectionChanged += (_, _) => projectionChanges++;

        var generation = viewModel.BeginReorder(source);
        Assert.True(generation > 0);
        for (var pointerUpdate = 0; pointerUpdate < 1_000; pointerUpdate++)
        {
            Assert.True(viewModel.PreviewReorder(
                generation,
                pointerUpdate % viewModel.Items.Count));
        }

        Assert.Equal(originalIds, viewModel.Items.Select(static item => item.Id));
        Assert.Equal(0, projectionChanges);
        Assert.True(source.IsDragging);

        Assert.True(viewModel.TryCancelReorderPreview());

        Assert.Equal(originalIds, viewModel.Items.Select(static item => item.Id));
        Assert.Equal(0, projectionChanges);
        Assert.Empty(repository.ReorderCalls);
        Assert.False(viewModel.IsReordering);
        Assert.False(source.IsDragging);

        var nextGeneration = viewModel.BeginReorder(viewModel.Items[1]);
        Assert.False(viewModel.TryCancelReorderPreview(generation));
        Assert.True(viewModel.IsReordering);
        Assert.True(viewModel.TryCancelReorderPreview(nextGeneration));
    }

    [Fact]
    public async Task ReorderReleasedAtItsOriginalSlotDoesNotCallTheRepository()
    {
        var repository = new ScriptedStageRepository([CreatePage(CreateItems(3))]);
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var generation = viewModel.BeginReorder(viewModel.Items[1]);

        Assert.True(await viewModel.CommitReorderAsync(generation));

        Assert.Empty(repository.ReorderCalls);
        Assert.False(viewModel.IsReordering);
        Assert.Equal("项目顺序没有变化。", viewModel.StatusText);
    }

    [Fact]
    public async Task HundredPreviewCancelCyclesKeepOnePageAndNoActiveSession()
    {
        var repository = new ScriptedStageRepository([CreatePage(CreateItems(32))]);
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var originalProjections = viewModel.Items.ToArray();

        for (var cycle = 0; cycle < 100; cycle++)
        {
            var sourceIndex = cycle % originalProjections.Length;
            var targetIndex = (cycle * 7) % originalProjections.Length;
            var generation = viewModel.BeginReorder(viewModel.Items[sourceIndex]);
            Assert.True(generation > 0);
            Assert.True(viewModel.PreviewReorder(generation, targetIndex));
            Assert.True(viewModel.TryCancelReorderPreview(generation));
            Assert.Equal(originalProjections, viewModel.Items);
            Assert.False(viewModel.IsReordering);
            Assert.False(viewModel.IsCommitting);
        }

        Assert.Empty(repository.ReorderCalls);
    }

    [Fact]
    public async Task AppliedSameWindowReorderUsesBoundaryAndPreservesAvailability()
    {
        var original = CreateItems(StageViewModel.PageSize);
        var olderBoundary = new StageOrderAnchor(
            CreateDeterministicGuid(100),
            revision: 7,
            sortOrder: -10);
        var authoritativeSource = original[^2] with
        {
            SortOrder = 0,
            Revision = original[^2].Revision + 1,
            UpdatedUtc = original[^2].UpdatedUtc.AddSeconds(1),
        };
        var repository = new ScriptedStageRepository(
            pages:
            [
                CreatePage(original) with
                {
                    OlderCursor = new StagePageCursor(
                        original[^1].SortOrder,
                        original[^1].Id),
                    OlderBoundary = olderBoundary,
                    TotalCount = original.Count + 1,
                },
            ],
            reorderResult: new StageReorderResult(
                StageReorderStatus.Applied,
                [authoritativeSource]));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var source = viewModel.Items[^2];
        var projectionChanges = 0;
        viewModel.Items.CollectionChanged += (_, _) => projectionChanges++;
        Assert.True(source.ApplyAvailability(
            source.Revision,
            StageFileAvailability.Missing));

        var generation = viewModel.BeginReorder(source);
        Assert.True(viewModel.PreviewReorder(generation, viewModel.Items.Count - 1));
        Assert.Equal(original.Select(static item => item.Id), viewModel.Items.Select(static item => item.Id));
        Assert.Equal(0, projectionChanges);
        Assert.True(await viewModel.CommitReorderAsync(generation));

        var request = Assert.Single(repository.ReorderCalls);
        Assert.Equal(
            new StageItemVersion(original[^2].Id, original[^2].Revision),
            request.Source);
        Assert.Equal(original[^1].Id, request.NewerNeighbor?.Id);
        Assert.Equal(olderBoundary, request.OlderNeighbor);
        Assert.Equal(authoritativeSource, source.Item);
        Assert.True(source.IsMissing);
        var expectedOrder = original
            .Where(item => item.Id != source.Id)
            .Select(static item => item.Id)
            .Append(source.Id);
        Assert.Equal(expectedOrder, viewModel.Items.Select(static item => item.Id));
        Assert.Equal(1, projectionChanges);
        Assert.False(viewModel.IsReordering);
        Assert.False(viewModel.IsCommitting);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task ReorderClaimsCommitOnceAndVisualExitDoesNotRollbackIt()
    {
        var original = CreateItems(3);
        var authoritativeSource = original[2] with
        {
            SortOrder = original[0].SortOrder + 1,
            Revision = original[2].Revision + 1,
            UpdatedUtc = original[2].UpdatedUtc.AddSeconds(1),
        };
        var commitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource<StageReorderResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new ScriptedStageRepository(
            pages: [CreatePage(original)],
            reorderHandler: (_, _) =>
            {
                commitStarted.TrySetResult();
                return releaseCommit.Task;
            });
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var source = viewModel.Items[2];
        var generation = viewModel.BeginReorder(source);
        Assert.True(viewModel.PreviewReorder(generation, 0));

        var firstCommit = viewModel.CommitReorderAsync(generation);
        await commitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.IsCommitting);
        Assert.False(await viewModel.CommitReorderAsync(generation));
        Assert.Equal(source.Id, viewModel.Items[0].Id);

        releaseCommit.SetResult(new StageReorderResult(
            StageReorderStatus.Applied,
            [authoritativeSource]));
        Assert.True(await firstCommit);

        Assert.Single(repository.ReorderCalls);
        Assert.Equal(source.Id, viewModel.Items[0].Id);
        Assert.False(viewModel.IsCommitting);
    }

    [Fact]
    public async Task ReorderConflictRestoresPreviewAndReloadsFirstPageWithoutReplay()
    {
        var original = CreateItems(3);
        var refreshed = original.Select(item => item with
        {
            Revision = item.Revision + 1,
            UpdatedUtc = item.UpdatedUtc.AddSeconds(1),
        }).ToArray();
        var repository = new ScriptedStageRepository(
            pages: [CreatePage(original), CreatePage(refreshed)],
            reorderResult: new StageReorderResult(
                StageReorderStatus.NeighborConflict,
                []));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var generation = viewModel.BeginReorder(viewModel.Items[2]);
        Assert.True(viewModel.PreviewReorder(generation, 0));

        Assert.True(await viewModel.CommitReorderAsync(generation));

        Assert.Single(repository.ReorderCalls);
        Assert.Equal(
            refreshed.Select(static item => item.Id),
            viewModel.Items.Select(static item => item.Id));
        Assert.All(viewModel.Items, item => Assert.Equal(2, item.Revision));
        Assert.Contains("未保存", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SamePageReorderRepairsTheNextPageCursorToTheNewPageTail()
    {
        var original = CreateItems(StageViewModel.PageSize);
        var olderBoundary = new StageOrderAnchor(
            CreateDeterministicGuid(100),
            revision: 1,
            sortOrder: -10);
        var firstPage = new StagePage(
            original,
            NewerCursor: null,
            OlderCursor: new StagePageCursor(original[^1].SortOrder, original[^1].Id),
            TotalCount: StageViewModel.PageSize + 1,
            OlderBoundary: olderBoundary);
        var authoritativeSource = original[0] with
        {
            SortOrder = 0,
            Revision = original[0].Revision + 1,
            UpdatedUtc = original[0].UpdatedUtc.AddSeconds(1),
        };
        var olderItem = Assert.Single(CreateItems(1, "较旧")) with
        {
            Id = olderBoundary.Id,
            SortOrder = -10,
            Revision = olderBoundary.Revision,
        };
        var repository = new ScriptedStageRepository(
            pages:
            [
                firstPage,
                new StagePage(
                    [olderItem],
                    NewerCursor: new StagePageCursor(
                        olderItem.SortOrder,
                        olderItem.Id),
                    OlderCursor: null,
                    TotalCount: StageViewModel.PageSize + 1,
                    NewerBoundary: ToOrderAnchor(authoritativeSource)),
            ],
            reorderResult: new StageReorderResult(
                StageReorderStatus.Applied,
                [authoritativeSource]));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var generation = viewModel.BeginReorder(viewModel.Items[0]);
        Assert.True(viewModel.PreviewReorder(generation, viewModel.Items.Count - 1));
        Assert.True(await viewModel.CommitReorderAsync(generation));
        await viewModel.LoadOlderPageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, repository.LoadCalls.Count);
        Assert.Equal(
            new StagePageCursor(authoritativeSource.SortOrder, authoritativeSource.Id),
            repository.LoadCalls[1].Anchor);
        Assert.All(
            repository.LoadCalls,
            call => Assert.Equal(StagePageDirection.TowardOlder, call.Direction));
        Assert.Equal(StageViewModel.PageSize + 1, viewModel.Items.Count);
        Assert.Equal(olderItem.Id, viewModel.Items[^1].Id);
    }

    [Fact]
    public async Task SortOrderExhaustionRestoresTheExactProjection()
    {
        var original = CreateItems(3);
        var repository = new ScriptedStageRepository(
            pages: [CreatePage(original)],
            reorderResult: new StageReorderResult(
                StageReorderStatus.SortOrderExhausted,
                []));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var originalProjections = viewModel.Items.ToArray();
        var generation = viewModel.BeginReorder(viewModel.Items[2]);
        Assert.True(viewModel.PreviewReorder(generation, 0));

        Assert.True(await viewModel.CommitReorderAsync(generation));

        Assert.Equal(originalProjections, viewModel.Items);
        Assert.Single(repository.ReorderCalls);
        Assert.False(viewModel.HasError);
        Assert.Contains("未保存", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppliedReorderProjectionFailureReloadsAuthorityWithoutReplayingWrite()
    {
        var original = CreateItems(3);
        var authoritativeSource = original[2] with
        {
            SortOrder = original[0].SortOrder + 1,
            Revision = original[2].Revision + 1,
            UpdatedUtc = original[2].UpdatedUtc.AddSeconds(1),
        };
        var invalidAuthority = authoritativeSource with { IsPinned = true };
        var refreshed =
            new[] { authoritativeSource, original[0], original[1] };
        var repository = new ScriptedStageRepository(
            pages: [CreatePage(original), CreatePage(refreshed)],
            reorderResult: new StageReorderResult(
                StageReorderStatus.Applied,
                [invalidAuthority]));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var generation = viewModel.BeginReorder(viewModel.Items[2]);
        Assert.True(viewModel.PreviewReorder(generation, 0));

        Assert.True(await viewModel.CommitReorderAsync(generation));

        Assert.Single(repository.ReorderCalls);
        Assert.Equal(refreshed, viewModel.Items.Select(static item => item.Item));
        Assert.Contains("已重新加载", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasError);
    }

    private static IReadOnlyList<StageItem> CreateItems(
        int count,
        string namePrefix = "项目")
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, count)
            .Select(index => new StageItem(
                CreateDeterministicGuid(index + 1),
                (index % 3) switch
                {
                    0 => StageItemKind.File,
                    1 => StageItemKind.Directory,
                    _ => StageItemKind.ShellLink,
                },
                $@"C:\Stage\{namePrefix}-{index + 1}",
                $@"C:\STAGE\{namePrefix.ToUpperInvariant()}-{index + 1}",
                $"{namePrefix} {index + 1}",
                count - index,
                false,
                1,
                now,
                now))
            .ToArray();
    }

    private static Guid CreateDeterministicGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        return new Guid(bytes);
    }

    private static StagePage CreatePage(IReadOnlyList<StageItem> items) =>
        new(
            items,
            NewerCursor: null,
            OlderCursor: null,
            TotalCount: items.Count);

    private static StageOrderAnchor ToOrderAnchor(StageItem item) =>
        new(item.Id, item.Revision, item.SortOrder);

    private static int Compare(StageItem item, StagePageCursor cursor)
    {
        var orderComparison = item.SortOrder.CompareTo(cursor.SortOrder);
        return orderComparison != 0
            ? orderComparison
            : StringComparer.Ordinal.Compare(
                item.Id.ToString("D"),
                cursor.Id.ToString("D"));
    }

    private sealed class InMemoryStageRepository(IReadOnlyList<StageItem> items)
        : IStageRepository
    {
        public IReadOnlyList<StageItem> Items { get; } = items;

        public int FirstPageLoadCount { get; private set; }

        public Task<StagePage> LoadPageAsync(
            StagePageCursor? anchor,
            StagePageDirection direction,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (anchor is null)
            {
                Assert.Equal(StagePageDirection.TowardOlder, direction);
                FirstPageLoadCount++;
            }

            IEnumerable<StageItem> candidates = direction switch
            {
                StagePageDirection.TowardOlder => Items.Where(item =>
                    anchor is null || Compare(item, anchor.Value) < 0),
                StagePageDirection.TowardNewer => Items
                    .Where(item => anchor is not null && Compare(item, anchor.Value) > 0)
                    .Reverse(),
                _ => throw new ArgumentOutOfRangeException(nameof(direction)),
            };
            var page = candidates.Take(pageSize).ToArray();
            if (direction is StagePageDirection.TowardNewer)
            {
                Array.Reverse(page);
            }

            if (page.Length == 0)
            {
                return Task.FromResult(new StagePage([], null, null, Items.Count));
            }

            var firstIndex = Items.ToList().FindIndex(item => item.Id == page[0].Id);
            var lastIndex = Items.ToList().FindIndex(item => item.Id == page[^1].Id);
            var newerBoundary = firstIndex > 0
                ? ToOrderAnchor(Items[firstIndex - 1])
                : (StageOrderAnchor?)null;
            var olderBoundary = lastIndex < Items.Count - 1
                ? ToOrderAnchor(Items[lastIndex + 1])
                : (StageOrderAnchor?)null;
            return Task.FromResult(new StagePage(
                page,
                NewerCursor: newerBoundary is null
                    ? null
                    : new StagePageCursor(page[0].SortOrder, page[0].Id),
                OlderCursor: olderBoundary is null
                    ? null
                    : new StagePageCursor(page[^1].SortOrder, page[^1].Id),
                TotalCount: Items.Count,
                newerBoundary,
                olderBoundary));
        }

        public Task<StageAddBatchResult> AddBatchAsync(
            IReadOnlyList<StageTargetDescriptor> targets,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageRemoveResult> RemoveAsync(
            Guid itemId,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageItemMutationResult> SetPinnedAsync(
            Guid itemId,
            long expectedRevision,
            bool isPinned,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageRelinkResult> RelinkAsync(
            Guid itemId,
            long expectedRevision,
            StageTargetDescriptor target,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageReorderResult> ReorderAsync(
            StageReorderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaticStageFileService(
        IReadOnlyList<StageTargetResolution> resolutions) : IStageFileService
    {
        public List<IReadOnlyList<string>> Inputs { get; } = [];

        public Task<IReadOnlyList<StageTargetResolution>> ResolveAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            Inputs.Add(inputs.ToArray());
            return Task.FromResult(resolutions);
        }

        public Task<StageFileAvailability> InspectAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(StageFileAvailability.Available);

        public Task OpenAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevealInExplorerAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedStageRepository : IStageRepository
    {
        private readonly Queue<StagePage> _pages;
        private readonly StageAddBatchResult? _addResult;
        private readonly Exception? _addException;
        private readonly StageRemoveResult? _removeResult;
        private readonly StageItemMutationResult? _setPinnedResult;
        private readonly StageRelinkResult? _relinkResult;
        private readonly StageReorderResult? _reorderResult;
        private readonly Func<StageReorderRequest, CancellationToken, Task<StageReorderResult>>?
            _reorderHandler;
        private readonly int? _failPageLoadAfter;
        private int _pageLoadCount;

        public ScriptedStageRepository(
            IEnumerable<StagePage> pages,
            StageAddBatchResult? addResult = null,
            Exception? addException = null,
            StageRemoveResult? removeResult = null,
            StageItemMutationResult? setPinnedResult = null,
            StageRelinkResult? relinkResult = null,
            int? failPageLoadAfter = null,
            StageReorderResult? reorderResult = null,
            Func<StageReorderRequest, CancellationToken, Task<StageReorderResult>>?
                reorderHandler = null)
        {
            _pages = new Queue<StagePage>(pages);
            _addResult = addResult;
            _addException = addException;
            _removeResult = removeResult;
            _setPinnedResult = setPinnedResult;
            _relinkResult = relinkResult;
            _failPageLoadAfter = failPageLoadAfter;
            _reorderResult = reorderResult;
            _reorderHandler = reorderHandler;
        }

        public List<IReadOnlyList<StageTargetDescriptor>> AddCalls { get; } = [];

        public List<(Guid ItemId, long ExpectedRevision)> RemoveCalls { get; } = [];

        public List<(Guid ItemId, long ExpectedRevision, bool IsPinned)> SetPinnedCalls { get; } = [];

        public List<(Guid ItemId, long ExpectedRevision, StageTargetDescriptor Target)> RelinkCalls { get; } = [];

        public List<StageReorderRequest> ReorderCalls { get; } = [];

        public List<(StagePageCursor? Anchor, StagePageDirection Direction, int PageSize)>
            LoadCalls { get; } = [];

        public Task<StagePage> LoadPageAsync(
            StagePageCursor? anchor,
            StagePageDirection direction,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCalls.Add((anchor, direction, pageSize));
            if (_failPageLoadAfter is { } threshold && _pageLoadCount >= threshold)
            {
                throw new IOException("refresh unavailable");
            }

            _pageLoadCount++;
            return Task.FromResult(_pages.Dequeue());
        }

        public Task<StageAddBatchResult> AddBatchAsync(
            IReadOnlyList<StageTargetDescriptor> targets,
            CancellationToken cancellationToken = default)
        {
            AddCalls.Add(targets.ToArray());
            if (_addException is not null)
            {
                throw _addException;
            }

            return Task.FromResult(_addResult ?? throw new InvalidOperationException());
        }

        public Task<StageRemoveResult> RemoveAsync(
            Guid itemId,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveCalls.Add((itemId, expectedRevision));
            return Task.FromResult(
                _removeResult ?? throw new InvalidOperationException());
        }

        public Task<StageItemMutationResult> SetPinnedAsync(
            Guid itemId,
            long expectedRevision,
            bool isPinned,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetPinnedCalls.Add((itemId, expectedRevision, isPinned));
            return Task.FromResult(
                _setPinnedResult ?? throw new InvalidOperationException());
        }

        public Task<StageRelinkResult> RelinkAsync(
            Guid itemId,
            long expectedRevision,
            StageTargetDescriptor target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RelinkCalls.Add((itemId, expectedRevision, target));
            return Task.FromResult(
                _relinkResult ?? throw new InvalidOperationException());
        }

        public Task<StageReorderResult> ReorderAsync(
            StageReorderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReorderCalls.Add(request);
            return _reorderHandler?.Invoke(request, cancellationToken) ??
                Task.FromResult(
                    _reorderResult ?? throw new InvalidOperationException());
        }
    }

    private sealed class DelayedNavigationStageRepository(
        StagePage first,
        StagePage older,
        StagePage committed) : IStageRepository
    {
        private int _loadCount;

        public TaskCompletionSource NavigationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseNavigation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AddCallCount { get; private set; }

        public async Task<StagePage> LoadPageAsync(
            StagePageCursor? anchor,
            StagePageDirection direction,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _loadCount);
            if (call == 1)
            {
                return first with
                {
                    OlderCursor = new StagePageCursor(
                        first.Items[^1].SortOrder,
                        first.Items[^1].Id),
                    OlderBoundary = ToOrderAnchor(older.Items[0]),
                    TotalCount = first.Items.Count + older.Items.Count,
                };
            }

            if (call == 2)
            {
                NavigationStarted.SetResult();
                await ReleaseNavigation.Task.WaitAsync(cancellationToken);
                return older with
                {
                    NewerCursor = new StagePageCursor(
                        older.Items[0].SortOrder,
                        older.Items[0].Id),
                    NewerBoundary = ToOrderAnchor(first.Items[^1]),
                    TotalCount = first.Items.Count + older.Items.Count,
                };
            }

            return committed;
        }

        public Task<StageAddBatchResult> AddBatchAsync(
            IReadOnlyList<StageTargetDescriptor> targets,
            CancellationToken cancellationToken = default)
        {
            AddCallCount++;
            return Task.FromResult(new StageAddBatchResult(
                StageAddBatchStatus.Applied,
                first.TotalCount,
                committed.TotalCount,
                1,
                200));
        }

        public Task<StageRemoveResult> RemoveAsync(
            Guid itemId,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageItemMutationResult> SetPinnedAsync(
            Guid itemId,
            long expectedRevision,
            bool isPinned,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageRelinkResult> RelinkAsync(
            Guid itemId,
            long expectedRevision,
            StageTargetDescriptor target,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageReorderResult> ReorderAsync(
            StageReorderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
