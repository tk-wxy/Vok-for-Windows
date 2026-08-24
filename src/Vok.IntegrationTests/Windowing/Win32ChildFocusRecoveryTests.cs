using Vok.Core.Workspace;
using Vok.Infrastructure.Windows.Windowing;
using Xunit;

namespace Vok.IntegrationTests.Windowing;

public sealed class Win32ChildFocusRecoveryTests
{
    [Fact]
    public void CrossThreadRecoveryAttachesSetsFocusAndDetachesInOrder()
    {
        var native = new FakeNativeApi();
        var recovery = new Win32ChildFocusRecovery(native);

        var result = recovery.TryRestore(Target);

        Assert.True(result.Attempted);
        Assert.True(result.ValidationSucceeded);
        Assert.True(result.AttachSucceeded);
        Assert.True(result.SetFocusAttempted);
        Assert.True(result.DetachSucceeded);
        Assert.Equal(["attach:true", "set-focus", "attach:false"], native.Log);
    }

    [Fact]
    public void SetFocusExceptionStillDetachesInputQueues()
    {
        var native = new FakeNativeApi { ThrowOnSetFocus = true };
        var recovery = new Win32ChildFocusRecovery(native);

        _ = Assert.Throws<InvalidOperationException>(() => recovery.TryRestore(Target));

        Assert.Equal(["attach:true", "set-focus", "attach:false"], native.Log);
    }

    [Fact]
    public void InvalidOrReusedChildIsRejectedBeforeInputQueuesAreAttached()
    {
        var native = new FakeNativeApi { FocusProcessId = 999 };
        var recovery = new Win32ChildFocusRecovery(native);

        var result = recovery.TryRestore(Target);

        Assert.True(result.Attempted);
        Assert.False(result.ValidationSucceeded);
        Assert.Empty(native.Log);
    }

    [Fact]
    public void CapturedUnrelatedWindowIsRejectedEvenIfItLaterLooksLikeAChild()
    {
        var native = new FakeNativeApi();
        var recovery = new Win32ChildFocusRecovery(native);
        var unrelatedCapture = Target with
        {
            FocusedWindowRelationship = ForegroundWindowRelationship.Unrelated,
        };

        var result = recovery.TryRestore(unrelatedCapture);

        Assert.False(result.ValidationSucceeded);
        Assert.Empty(native.Log);
    }

    [Fact]
    public void AttachFailureDoesNotCallSetFocusOrDetachAnUnattachedQueue()
    {
        var native = new FakeNativeApi { AttachSucceeds = false };
        var recovery = new Win32ChildFocusRecovery(native);

        var result = recovery.TryRestore(Target);

        Assert.True(result.AttachAttempted);
        Assert.False(result.AttachSucceeded);
        Assert.False(result.SetFocusAttempted);
        Assert.False(result.DetachAttempted);
        Assert.Equal(["attach:true"], native.Log);
    }

    [Fact]
    public void SameThreadRecoveryDoesNotAttachInputQueues()
    {
        var native = new FakeNativeApi { CurrentThreadId = Target.ThreadId };
        var recovery = new Win32ChildFocusRecovery(native);

        var result = recovery.TryRestore(Target);

        Assert.False(result.AttachAttempted);
        Assert.True(result.SetFocusAttempted);
        Assert.False(result.DetachAttempted);
        Assert.Equal(["set-focus"], native.Log);
    }

    [Fact]
    public void DetachFailureIsReportedWithoutASecondDetachAttempt()
    {
        var native = new FakeNativeApi { DetachSucceeds = false };
        var recovery = new Win32ChildFocusRecovery(native);

        var result = recovery.TryRestore(Target);

        Assert.True(result.DetachAttempted);
        Assert.False(result.DetachSucceeded);
        Assert.Equal(1, native.Log.Count(entry => entry == "attach:false"));
    }

    private static readonly ForegroundTarget Target = new(
        WindowHandle: 0x100,
        ProcessId: 50,
        ThreadId: 60,
        FocusedWindowHandle: 0x101,
        FocusedProcessId: 50,
        FocusedThreadId: 60,
        FocusedWindowRelationship: ForegroundWindowRelationship.Descendant);

    private sealed class FakeNativeApi : IChildFocusRecoveryNativeApi
    {
        public List<string> Log { get; } = [];
        public uint CurrentThreadId { get; set; } = 70;
        public uint FocusProcessId { get; set; } = Target.FocusedProcessId;
        public bool AttachSucceeds { get; set; } = true;
        public bool DetachSucceeds { get; set; } = true;
        public bool ThrowOnSetFocus { get; set; }

        public uint GetCurrentThreadId() => CurrentThreadId;

        public bool IsWindow(nint window) =>
            window == Target.WindowHandle || window == Target.FocusedWindowHandle;

        public uint GetWindowThreadProcessId(nint window, out uint processId)
        {
            if (window == Target.WindowHandle)
            {
                processId = Target.ProcessId;
                return Target.ThreadId;
            }

            processId = FocusProcessId;
            return Target.FocusedThreadId;
        }

        public bool IsChild(nint parent, nint child) =>
            parent == Target.WindowHandle && child == Target.FocusedWindowHandle;

        public nint GetForegroundWindow() => Target.WindowHandle;

        public bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach)
        {
            Log.Add($"attach:{attach.ToString().ToLowerInvariant()}");
            return attach ? AttachSucceeds : DetachSucceeds;
        }

        public nint SetFocus(nint window)
        {
            Log.Add("set-focus");
            if (ThrowOnSetFocus)
            {
                throw new InvalidOperationException("set focus failure");
            }

            return 0;
        }
    }
}
