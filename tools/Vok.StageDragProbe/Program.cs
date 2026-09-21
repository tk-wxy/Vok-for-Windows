namespace Vok.StageDragProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        try
        {
            var options = StageDragProbeOptions.Parse(arguments);
            ProbePathPolicy.Validate(options, ProbePathEnvironment.Capture());
            Console.Error.WriteLine(
                $"{options.Verb} execution is not enabled until its N2.1 checkpoint is implemented.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
