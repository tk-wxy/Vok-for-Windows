namespace Vok.StageDragProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        try
        {
            var options = StageDragProbeOptions.Parse(arguments);
            var environment = ProbePathEnvironment.Capture();
            ProbePathPolicy.Validate(options, environment);
            var identity = ProbeProcessIdentity.Capture(options);
            var output = ProbeOutputDirectory.Reserve(options, environment);
            return ProbeCommandRunner.Run(options, output, identity);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
