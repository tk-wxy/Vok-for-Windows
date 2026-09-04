using System.IO;
using Vok.Core.Stage;
using Vok.Features.Stage;
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
    }

    private sealed class ScriptedStageRepository : IStageRepository
    {
        private readonly Queue<StagePage> _pages;
        private readonly StageAddOrMoveResult? _addResult;
        private readonly Exception? _addException;
        private readonly int? _failPageLoadAfter;
        private int _pageLoadCount;

        public ScriptedStageRepository(
            IEnumerable<StagePage> pages,
            StageAddOrMoveResult? addResult = null,
            Exception? addException = null,
            int? failPageLoadAfter = null)
        {
            _pages = new Queue<StagePage>(pages);
            _addResult = addResult;
            _addException = addException;
            _failPageLoadAfter = failPageLoadAfter;
        }

        public List<IReadOnlyList<StageTargetDescriptor>> AddCalls { get; } = [];

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
    }
}
