using System.IO;
using System.Windows;
using Vok.Hosting;
using Xunit;

namespace Vok.IntegrationTests.Hosting;

public sealed class WorkspaceFileDropRouterTests
{
    [Fact]
    public async Task RoutesOneSnapshotToTheFrozenTargetAndNeverHighlightsTwoTargets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var visualState = new Dictionary<WorkspaceFileDropTargetKind, bool>();
        var launcher = new FakeTarget(WorkspaceFileDropTargetKind.Launcher, visualState);
        var stage = new FakeTarget(WorkspaceFileDropTargetKind.Stage, visualState);
        var started = new List<long>();
        var ended = new List<(long Id, bool Accepted)>();
        using var router = new WorkspaceFileDropRouter(
            [launcher, stage],
            started.Add,
            (id, accepted) => ended.Add((id, accepted)),
            () => true);
        var sourcePaths = new[] { @"C:\Stage\alpha.txt" };
        var data = new CountingFileDropDataObject(sourcePaths);

        Assert.True(router.Begin(data, WorkspaceFileDropTargetKind.Launcher));
        Assert.Equal(WorkspaceFileDropTargetKind.Launcher, router.CurrentTarget);
        for (var index = 0; index < 100; index++)
        {
            var target = (index % 3) switch
            {
                0 => WorkspaceFileDropTargetKind.Stage,
                1 => WorkspaceFileDropTargetKind.None,
                _ => WorkspaceFileDropTargetKind.Launcher,
            };
            _ = router.Update(target);
        }

        Assert.Equal(1, data.GetDataPresentCallCount);
        Assert.Equal(0, data.GetDataCallCount);
        var completion = router.Complete(data, WorkspaceFileDropTargetKind.Stage);
        sourcePaths[0] = @"C:\Stage\mutated.txt";
        await stage.Received.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.True(completion.Accepted);
        Assert.Equal(@"C:\Stage\alpha.txt", Assert.Single(completion.Paths));
        Assert.Equal(@"C:\Stage\alpha.txt", Assert.Single(stage.ReceivedPaths!));
        Assert.Null(launcher.ReceivedPaths);
        Assert.Equal(1, data.GetDataCallCount);
        Assert.Single(started);
        Assert.Equal((started[0], true), Assert.Single(ended));
        Assert.All(visualState.Values, Assert.False);
        Assert.True(launcher.MaximumSimultaneouslyTargeted <= 1);
        Assert.True(stage.MaximumSimultaneouslyTargeted <= 1);
    }

    [Fact]
    public void ClaimsAndClearsTheSessionBeforeReadingForeignDropData()
    {
        var visualState = new Dictionary<WorkspaceFileDropTargetKind, bool>();
        var launcher = new FakeTarget(WorkspaceFileDropTargetKind.Launcher, visualState);
        var stage = new FakeTarget(WorkspaceFileDropTargetKind.Stage, visualState);
        var ended = new List<bool>();
        using var router = new WorkspaceFileDropRouter(
            [launcher, stage],
            _ => { },
            (_, accepted) => ended.Add(accepted),
            () => true);
        var data = new CountingFileDropDataObject(
            [@"C:\Stage\alpha.txt"],
            beforeGetData: () =>
            {
                Assert.False(router.IsTracking);
                Assert.All(visualState.Values, Assert.False);
            });

        Assert.True(router.Begin(data, WorkspaceFileDropTargetKind.Stage));
        var completion = router.Complete(data, WorkspaceFileDropTargetKind.Stage);

        Assert.True(completion.Accepted);
        Assert.Equal([true], ended);
    }

    [Fact]
    public void NoneOrWithdrawnDataRejectsWithoutLeakingATerminal()
    {
        var visualState = new Dictionary<WorkspaceFileDropTargetKind, bool>();
        var target = new FakeTarget(WorkspaceFileDropTargetKind.Launcher, visualState);
        var ended = new List<bool>();
        using var router = new WorkspaceFileDropRouter(
            [target],
            _ => { },
            (_, accepted) => ended.Add(accepted),
            () => true);

        var noTargetData = new CountingFileDropDataObject([@"C:\Stage\one.txt"]);
        Assert.True(router.Begin(noTargetData, WorkspaceFileDropTargetKind.None));
        Assert.False(router.Complete(
            noTargetData,
            WorkspaceFileDropTargetKind.None).Accepted);
        Assert.Equal(0, noTargetData.GetDataCallCount);

        var withdrawn = new CountingFileDropDataObject(
            [@"C:\Stage\two.txt"],
            throwWhenReading: true);
        Assert.True(router.Begin(withdrawn, WorkspaceFileDropTargetKind.Launcher));
        Assert.False(router.Complete(
            withdrawn,
            WorkspaceFileDropTargetKind.Launcher).Accepted);
        router.Cancel();

        Assert.Equal(1, withdrawn.GetDataCallCount);
        Assert.Equal([false, false], ended);
        Assert.False(router.IsTracking);
        Assert.All(visualState.Values, Assert.False);
    }

    [Fact]
    public void ReplacementEndsOnceAndRefusesOrphanWhenEndCallbackHidesOwner()
    {
        var visualState = new Dictionary<WorkspaceFileDropTargetKind, bool>();
        var target = new FakeTarget(WorkspaceFileDropTargetKind.Launcher, visualState);
        var ownerVisible = true;
        var ended = 0;
        using var router = new WorkspaceFileDropRouter(
            [target],
            _ => { },
            (_, _) =>
            {
                ended++;
                ownerVisible = false;
            },
            () => ownerVisible);
        var first = new CountingFileDropDataObject([@"C:\Stage\one.txt"]);
        var replacement = new CountingFileDropDataObject([@"C:\Stage\two.txt"]);

        Assert.True(router.Begin(first, WorkspaceFileDropTargetKind.Launcher));
        Assert.False(router.Begin(replacement, WorkspaceFileDropTargetKind.Launcher));
        router.Cancel();

        Assert.Equal(1, ended);
        Assert.Equal(0, replacement.GetDataPresentCallCount);
        Assert.False(router.IsTracking);
    }

    [Fact]
    public async Task AcceptedOleResultIsNotRewrittenWhenAsyncDispatchFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var visualState = new Dictionary<WorkspaceFileDropTargetKind, bool>();
        var target = new FakeTarget(
            WorkspaceFileDropTargetKind.Stage,
            visualState,
            receiveException: new IOException("stage unavailable"));
        using var router = new WorkspaceFileDropRouter(
            [target],
            _ => { },
            (_, _) => { },
            () => true);
        var data = new CountingFileDropDataObject([@"C:\Stage\one.txt"]);

        Assert.True(router.Begin(data, WorkspaceFileDropTargetKind.Stage));
        var completion = router.Complete(data, WorkspaceFileDropTargetKind.Stage);
        await target.FailureReported.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            cancellationToken);

        Assert.True(completion.Accepted);
        Assert.Equal("stage unavailable", target.ReportedException?.Message);
        Assert.False(router.IsTracking);
    }

    [Fact]
    public void HundredAlternatingDropsAndCancellationsLeaveNoSessionOrVisualState()
    {
        var visualState = new Dictionary<WorkspaceFileDropTargetKind, bool>();
        var launcher = new FakeTarget(WorkspaceFileDropTargetKind.Launcher, visualState);
        var stage = new FakeTarget(WorkspaceFileDropTargetKind.Stage, visualState);
        var started = new List<long>();
        var ended = new List<(long Id, bool Accepted)>();
        using var router = new WorkspaceFileDropRouter(
            [launcher, stage],
            started.Add,
            (id, accepted) => ended.Add((id, accepted)),
            () => true);
        var dataObjects = new List<CountingFileDropDataObject>();

        for (var index = 0; index < 100; index++)
        {
            var data = new CountingFileDropDataObject([$@"C:\Stage\cycle-{index}.txt"]);
            dataObjects.Add(data);
            Assert.True(router.Begin(data, WorkspaceFileDropTargetKind.Launcher));
            Assert.True(router.Update(WorkspaceFileDropTargetKind.Stage));
            Assert.False(router.Update(WorkspaceFileDropTargetKind.None));
            Assert.True(router.Update(WorkspaceFileDropTargetKind.Stage));

            if (index % 2 == 0)
            {
                var completion = router.Complete(
                    data,
                    WorkspaceFileDropTargetKind.Stage);
                Assert.True(completion.Accepted);
            }
            else
            {
                router.Cancel();
            }

            Assert.False(router.IsTracking);
            Assert.Equal(WorkspaceFileDropTargetKind.None, router.CurrentTarget);
            Assert.All(visualState.Values, Assert.False);
        }

        Assert.Equal(100, started.Count);
        Assert.Equal(100, ended.Count);
        Assert.Equal(50, ended.Count(static terminal => terminal.Accepted));
        Assert.Equal(50, stage.ReceiveCount);
        Assert.Equal(100, dataObjects.Sum(static data => data.GetDataPresentCallCount));
        Assert.Equal(50, dataObjects.Sum(static data => data.GetDataCallCount));
        Assert.True(launcher.MaximumSimultaneouslyTargeted <= 1);
        Assert.True(stage.MaximumSimultaneouslyTargeted <= 1);
    }

    private sealed class FakeTarget : IWorkspaceFileDropTarget
    {
        private readonly IDictionary<WorkspaceFileDropTargetKind, bool> _visualState;
        private readonly Exception? _receiveException;

        public FakeTarget(
            WorkspaceFileDropTargetKind kind,
            IDictionary<WorkspaceFileDropTargetKind, bool> visualState,
            Exception? receiveException = null)
        {
            Kind = kind;
            _visualState = visualState;
            _visualState[kind] = false;
            _receiveException = receiveException;
        }

        public WorkspaceFileDropTargetKind Kind { get; }

        public bool IsAvailable { get; set; } = true;

        public int MaximumSimultaneouslyTargeted { get; private set; }

        public IReadOnlyList<string>? ReceivedPaths { get; private set; }

        public int ReceiveCount { get; private set; }

        public Exception? ReportedException { get; private set; }

        public TaskCompletionSource Received { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FailureReported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CancelInteractions()
        {
        }

        public void SetExternalDragState(bool sessionActive, bool targeted)
        {
            _visualState[Kind] = sessionActive && targeted;
            MaximumSimultaneouslyTargeted = Math.Max(
                MaximumSimultaneouslyTargeted,
                _visualState.Values.Count(static value => value));
            Assert.InRange(_visualState.Values.Count(static value => value), 0, 1);
        }

        public Task ReceiveAsync(
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken)
        {
            ReceiveCount++;
            ReceivedPaths = paths;
            Received.TrySetResult();
            return _receiveException is null
                ? Task.CompletedTask
                : Task.FromException(_receiveException);
        }

        public void ReportDispatchFailure(Exception exception)
        {
            ReportedException = exception;
            FailureReported.TrySetResult();
        }
    }

    private sealed class CountingFileDropDataObject(
        string[] paths,
        bool throwWhenReading = false,
        Action? beforeGetData = null) : IDataObject
    {
        public int GetDataPresentCallCount { get; private set; }

        public int GetDataCallCount { get; private set; }

        public object GetData(string format, bool autoConvert)
        {
            GetDataCallCount++;
            beforeGetData?.Invoke();
            if (throwWhenReading)
            {
                throw new System.Runtime.InteropServices.COMException("source withdrew data");
            }

            return format == DataFormats.FileDrop ? paths : null!;
        }

        public object GetData(string format) => GetData(format, autoConvert: true);

        public object GetData(Type format) => GetData(format.FullName ?? format.Name);

        public bool GetDataPresent(string format, bool autoConvert)
        {
            GetDataPresentCallCount++;
            return format == DataFormats.FileDrop;
        }

        public bool GetDataPresent(string format) =>
            GetDataPresent(format, autoConvert: true);

        public bool GetDataPresent(Type format) =>
            GetDataPresent(format.FullName ?? format.Name);

        public string[] GetFormats(bool autoConvert) => [DataFormats.FileDrop];

        public string[] GetFormats() => GetFormats(autoConvert: true);

        public void SetData(string format, object data, bool autoConvert) =>
            throw new NotSupportedException();

        public void SetData(string format, object data) =>
            throw new NotSupportedException();

        public void SetData(Type format, object data) =>
            throw new NotSupportedException();

        public void SetData(object data) => throw new NotSupportedException();
    }
}
