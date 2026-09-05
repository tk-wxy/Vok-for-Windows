using System.IO;
using Vok.Core.Stage;
using Vok.Features.Stage;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class StageViewModelTests
{
    [Fact]
    public async Task NavigationKeepsOnlyOneFixedSizeKeysetPageResident()
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

        var visited = new List<Guid>();
        while (true)
        {
            Assert.InRange(viewModel.Items.Count, 1, StageViewModel.PageSize);
            visited.AddRange(viewModel.Items.Select(static item => item.Id));
            if (!viewModel.CanLoadOlder)
            {
                break;
            }

            await viewModel.LoadOlderPageAsync(cancellationToken);
        }

        Assert.Equal(10_000, visited.Count);
        Assert.Equal(10_000, visited.Distinct().Count());
        Assert.Equal(16, viewModel.Items.Count);
        Assert.True(viewModel.CanLoadNewer);

        while (viewModel.CanLoadNewer)
        {
            await viewModel.LoadNewerPageAsync(cancellationToken);
            Assert.InRange(viewModel.Items.Count, 1, StageViewModel.PageSize);
        }

        Assert.Equal(repository.Items[0].Id, viewModel.Items[0].Id);
        Assert.Equal("1–64 / 10000", viewModel.PageSummaryText);
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
            addResult: new StageAddOrMoveResult(
                StageAddOrMoveStatus.Applied,
                CountBefore: 2,
                CountAfter: 3,
                AddedCount: 1,
                MovedCount: 1,
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
        Assert.Equal("新增 1 项，回顶 1 项，1 项无效。", viewModel.StatusText);
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
            addResult: new StageAddOrMoveResult(
                StageAddOrMoveStatus.CapacityExceeded,
                1,
                1,
                0,
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
            addResult: new StageAddOrMoveResult(
                StageAddOrMoveStatus.Applied,
                1,
                2,
                1,
                0,
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
        var olderPage = CreatePage(CreateItems(10, namePrefix: "较早"));
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

            var item = Assert.Single(viewModel.Items);
            Assert.Equal(descriptor.TargetValue, item.TargetValue);
            Assert.Equal(1, viewModel.TotalCount);
            Assert.False(viewModel.HasError);

            var page = await repository.LoadPageAsync(
                null,
                StageViewModel.PageSize,
                TestContext.Current.CancellationToken);
            Assert.Equal(50, Assert.Single(page.Items).Revision);
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
        Assert.DoesNotContain(viewModel.Items, item => item.Id == removed.Id);
        Assert.Equal(1, viewModel.TotalCount);
        Assert.True(viewModel.HasError);
        Assert.Contains("已移除", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("列表刷新失败", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task RemovingTheLastItemOnTheLastPageReturnsToThePreviousPage()
    {
        var firstItems = CreateItems(StageViewModel.PageSize, "较新");
        var lastItem = Assert.Single(CreateItems(1, "最旧"));
        var firstPage = CreatePage(firstItems) with
        {
            NextCursor = new StagePageCursor(
                firstItems[^1].SortOrder,
                firstItems[^1].Id),
            TotalCount = StageViewModel.PageSize + 1,
        };
        var repository = new ScriptedStageRepository(
            pages:
            [
                firstPage,
                new StagePage([lastItem], null, StageViewModel.PageSize + 1),
                CreatePage(firstItems),
            ],
            removeResult: new StageRemoveResult(StageRemoveStatus.Removed));
        using var viewModel = new StageViewModel(
            repository,
            new StaticStageFileService([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        await viewModel.LoadOlderPageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(lastItem.Id, Assert.Single(viewModel.Items).Id);

        await viewModel.RemoveCommand.ExecuteAsync(viewModel.Items[0]);

        Assert.Equal(StageViewModel.PageSize, viewModel.Items.Count);
        Assert.Equal(firstItems[0].Id, viewModel.Items[0].Id);
        Assert.Equal(StageViewModel.PageSize, viewModel.TotalCount);
        Assert.Equal("1–64 / 64", viewModel.PageSummaryText);
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
    public async Task RelinkConflictIsExplicitAndConfirmedWriteIsNotReplayed()
    {
        var original = Assert.Single(CreateItems(1));
        var target = new StageTargetDescriptor(
            StageItemKind.File,
            @"C:\Stage\Existing.txt",
            @"C:\STAGE\EXISTING.TXT",
            "Existing.txt");
        var conflictRepository = new ScriptedStageRepository(
            pages: [CreatePage([original])],
            relinkResult: new StageRelinkResult(
                StageRelinkStatus.CanonicalConflict,
                null));
        var files = new StaticStageFileService(
            [new StageTargetResolution(target.TargetValue, target, null, null)]);
        using (var conflictViewModel = new StageViewModel(conflictRepository, files)
        {
            RelinkTargetPickerAsync = (_, _) => Task.FromResult(
                StageRelinkSelection.Selected(target.TargetValue)),
        })
        {
            await conflictViewModel.InitializeAsync(TestContext.Current.CancellationToken);
            await conflictViewModel.RelinkCommand.ExecuteAsync(conflictViewModel.Items[0]);
            Assert.Contains("已在中转站", conflictViewModel.StatusText, StringComparison.Ordinal);
            Assert.Equal(original, Assert.Single(conflictViewModel.Items).Item);
        }

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
        Assert.Contains("源文件已失效", item.AutomationName, StringComparison.Ordinal);

        item.ResetAvailability(item.Revision + 1);
        Assert.True(item.HasAvailabilityStatus);
        item.ResetAvailability(item.Revision);
        Assert.False(item.HasAvailabilityStatus);
        Assert.False(item.IsMissing);
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
            NextCursor: null,
            TotalCount: items.Count);

    private sealed class InMemoryStageRepository(IReadOnlyList<StageItem> items)
        : IStageRepository
    {
        public IReadOnlyList<StageItem> Items { get; } = items;

        public int FirstPageLoadCount { get; private set; }

        public Task<StagePage> LoadPageAsync(
            StagePageCursor? after,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (after is null)
            {
                FirstPageLoadCount++;
            }

            var start = 0;
            if (after is { } cursor)
            {
                start = Items
                    .TakeWhile(item => !(item.SortOrder < cursor.SortOrder ||
                        (item.SortOrder == cursor.SortOrder && item.Id.CompareTo(cursor.Id) < 0)))
                    .Count();
            }

            var page = Items.Skip(start).Take(pageSize).ToArray();
            var hasMore = start + page.Length < Items.Count;
            StagePageCursor? next = hasMore
                ? new StagePageCursor(page[^1].SortOrder, page[^1].Id)
                : null;
            return Task.FromResult(new StagePage(page, next, Items.Count));
        }

        public Task<StageAddOrMoveResult> AddOrMoveAsync(
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
        private readonly StageAddOrMoveResult? _addResult;
        private readonly Exception? _addException;
        private readonly StageRemoveResult? _removeResult;
        private readonly StageItemMutationResult? _setPinnedResult;
        private readonly StageRelinkResult? _relinkResult;
        private readonly int? _failPageLoadAfter;
        private int _pageLoadCount;

        public ScriptedStageRepository(
            IEnumerable<StagePage> pages,
            StageAddOrMoveResult? addResult = null,
            Exception? addException = null,
            StageRemoveResult? removeResult = null,
            StageItemMutationResult? setPinnedResult = null,
            StageRelinkResult? relinkResult = null,
            int? failPageLoadAfter = null)
        {
            _pages = new Queue<StagePage>(pages);
            _addResult = addResult;
            _addException = addException;
            _removeResult = removeResult;
            _setPinnedResult = setPinnedResult;
            _relinkResult = relinkResult;
            _failPageLoadAfter = failPageLoadAfter;
        }

        public List<IReadOnlyList<StageTargetDescriptor>> AddCalls { get; } = [];

        public List<(Guid ItemId, long ExpectedRevision)> RemoveCalls { get; } = [];

        public List<(Guid ItemId, long ExpectedRevision, bool IsPinned)> SetPinnedCalls { get; } = [];

        public List<(Guid ItemId, long ExpectedRevision, StageTargetDescriptor Target)> RelinkCalls { get; } = [];

        public Task<StagePage> LoadPageAsync(
            StagePageCursor? after,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_failPageLoadAfter is { } threshold && _pageLoadCount >= threshold)
            {
                throw new IOException("refresh unavailable");
            }

            _pageLoadCount++;
            return Task.FromResult(_pages.Dequeue());
        }

        public Task<StageAddOrMoveResult> AddOrMoveAsync(
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
            StagePageCursor? after,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _loadCount);
            if (call == 1)
            {
                return first with
                {
                    NextCursor = new StagePageCursor(
                        first.Items[^1].SortOrder,
                        first.Items[^1].Id),
                };
            }

            if (call == 2)
            {
                NavigationStarted.SetResult();
                await ReleaseNavigation.Task.WaitAsync(cancellationToken);
                return older;
            }

            return committed;
        }

        public Task<StageAddOrMoveResult> AddOrMoveAsync(
            IReadOnlyList<StageTargetDescriptor> targets,
            CancellationToken cancellationToken = default)
        {
            AddCallCount++;
            return Task.FromResult(new StageAddOrMoveResult(
                StageAddOrMoveStatus.Applied,
                first.TotalCount,
                committed.TotalCount,
                1,
                0,
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
    }
}
