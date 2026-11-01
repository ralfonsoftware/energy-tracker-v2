namespace EnergyTracker.Api.Tests;

// Serilog.AspNetCore without preserveStaticLogger writes through the static Log.Logger, which every host in the process
// replaces when it builds. Tests that capture log events must therefore run alone, or another host's logger takes the events.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialLoggingGroup
{
    public const string Name = "Serial logging";
}
