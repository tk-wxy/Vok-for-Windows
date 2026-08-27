namespace Vok.Hosting;

public sealed record WorkspaceHostOptions(
    bool PrecreateWindowHandle,
    TimeSpan RevealWatchdogTimeout)
{
    private const string PrecreateEnvironmentVariable = "VOK_M0_PRECREATE_HWND";
    public static readonly TimeSpan DefaultRevealWatchdogTimeout = TimeSpan.FromSeconds(2);

    public WorkspaceHostOptions(bool precreateWindowHandle)
        : this(precreateWindowHandle, DefaultRevealWatchdogTimeout)
    {
    }

    public static WorkspaceHostOptions FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(PrecreateEnvironmentVariable);
        return new WorkspaceHostOptions(
            !string.Equals(value, "0", StringComparison.Ordinal),
            DefaultRevealWatchdogTimeout);
    }
}
