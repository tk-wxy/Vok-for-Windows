namespace Vok.Hosting;

public sealed record WorkspaceHostOptions(bool PrecreateWindowHandle)
{
    private const string PrecreateEnvironmentVariable = "VOK_M0_PRECREATE_HWND";

    public static WorkspaceHostOptions FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(PrecreateEnvironmentVariable);
        return new WorkspaceHostOptions(!string.Equals(value, "0", StringComparison.Ordinal));
    }
}
