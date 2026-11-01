namespace EnergyTracker.Api;

// ADR 0009: a throw before app.Run() must end the process with exit code 1. A dotnet process that is PID 1 in a
// container does not terminate on the runtime's abort and spins instead, so restart policies never see it.
public static class StartupFailureExit
{
    private static int registered;

    public static void Register()
    {
        if (Interlocked.CompareExchange(ref registered, 1, 0) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // The runtime raises this event before it prints its own text, so the handler prints the exception
            // itself. Console.Error, not Serilog: the logger may be half-built.
            Console.Error.WriteLine("Unhandled exception. " + e.ExceptionObject);
            Console.Error.Flush();
            Environment.Exit(1);
        };
    }
}
