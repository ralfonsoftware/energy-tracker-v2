using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.19 (AC #7): the composition root is pinned as text. The ordered list of service descriptors and of
// hosted services is compared with approved files, so a registration that moves, changes lifetime or starts in a
// different order is a visible diff. Moving code out of Program.cs must leave these files unedited.
// To approve an intended change, run the tests with ENERGYTRACKER_UPDATE_SNAPSHOTS=1 and review the diff.
public class ServiceRegistrationSnapshotTests
{
    private static readonly string[] OwnedNamespacePrefixes = ["EnergyTracker."];

    private static readonly string[] TelemetryNamespacePrefixes = ["OpenTelemetry.", "Azure.Monitor."];

    private static readonly Type[] ExplicitFrameworkTypes = [typeof(TimeProvider), typeof(IHttpContextAccessor)];

    public static TheoryData<string> Variants =>
        ["default", "azurestoragequeue", "otlp", "azuremonitor"];

    private static string SnapshotDirectory([CallerFilePath] string testFile = "") =>
        Path.Combine(Path.GetDirectoryName(testFile)!, "Snapshots");

    private static List<ServiceDescriptor> CaptureRegistrations(string variant)
    {
        List<ServiceDescriptor>? captured = null;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            switch (variant)
            {
                case "azurestoragequeue":
                    builder.UseSetting("JobQueue:Provider", "azurestoragequeue");
                    builder.UseSetting("JobQueue:ConnectionString", "UseDevelopmentStorage=true");
                    break;
                case "otlp":
                    builder.UseSetting("Otel:Exporter", "otlp");
                    builder.UseSetting("Otel:OtlpEndpoint", "http://localhost:18889");
                    break;
                case "azuremonitor":
                    builder.UseSetting("Otel:Exporter", "azuremonitor");
                    builder.UseSetting("Otel:AzureMonitorConnectionString", "InstrumentationKey=00000000-0000-0000-0000-000000000000");
                    break;
            }

            builder.ConfigureTestServices(services => captured = [.. services]);
        });

        _ = factory.Services;
        return captured ?? throw new InvalidOperationException("The host did not expose its service collection.");
    }

    private static bool IsPinned(ServiceDescriptor descriptor, bool telemetry)
    {
        var names = new[] { descriptor.ServiceType.FullName, ImplementationName(descriptor) };
        return names.Any(name => name is not null && OwnedNamespacePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
               || ExplicitFrameworkTypes.Contains(descriptor.ServiceType)
               || descriptor.ServiceType == typeof(IHostedService)
               || (telemetry && names.Any(name => name is not null && TelemetryNamespacePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))));
    }

    private static string? ImplementationName(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType?.FullName
            : descriptor.ImplementationType?.FullName;

    private static string Describe(ServiceDescriptor descriptor)
    {
        var implementation =
            ImplementationName(descriptor) is { } type ? type
            : (descriptor.IsKeyedService ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance) is { } instance
                ? $"instance {instance.GetType().FullName}"
                : "factory";
        var key = descriptor.IsKeyedService ? $" [key {descriptor.ServiceKey}]" : string.Empty;
        return $"{descriptor.Lifetime} {descriptor.ServiceType.FullName}{key} -> {implementation}";
    }

    internal static string Render(IEnumerable<ServiceDescriptor> registrations, bool telemetry)
    {
        var all = registrations.ToList();
        var text = new StringBuilder();
        text.AppendLine("# service descriptors, in registration order");
        foreach (var descriptor in all.Where(d => IsPinned(d, telemetry)))
        {
            text.AppendLine(Describe(descriptor));
        }

        text.AppendLine();
        text.AppendLine("# hosted services, in start order");
        foreach (var descriptor in all.Where(d => d.ServiceType == typeof(IHostedService)))
        {
            text.AppendLine(ImplementationName(descriptor) ?? "factory");
        }

        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void Registrations_match_the_approved_snapshot(string variant)
    {
        var actual = Render(CaptureRegistrations(variant), telemetry: variant is "otlp" or "azuremonitor");
        var path = Path.Combine(SnapshotDirectory(), $"service-registrations.{variant}.txt");

        if (Environment.GetEnvironmentVariable("ENERGYTRACKER_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(path, actual);
        }

        File.Exists(path).ShouldBeTrue($"Approved snapshot missing: {path}");
        var approved = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        actual.ShouldBe(approved, "The service registrations changed. If intended, re-approve with ENERGYTRACKER_UPDATE_SNAPSHOTS=1.");
    }

    [Fact]
    public void The_snapshot_is_not_vacuous_and_sees_the_known_registrations()
    {
        var text = Render(CaptureRegistrations("default"), telemetry: false);

        text.ShouldContain("Scoped EnergyTracker.Application.Ports.ISmartPlugParser -> EnergyTracker.Infrastructure.Adapters.EveHomeXlsxParser");
        text.ShouldContain("Scoped EnergyTracker.Application.Ports.ISmartPlugParser -> EnergyTracker.Infrastructure.Adapters.MerossCsvParser");
        text.ShouldContain("InstanceHeartbeatService");
        text.Split('\n').Length.ShouldBeGreaterThan(60);
    }

    [Fact]
    public void Render_detects_a_reordered_registration()
    {
        ServiceDescriptor first = ServiceDescriptor.Singleton<IHttpContextAccessor, HttpContextAccessor>();
        ServiceDescriptor second = ServiceDescriptor.Singleton<TimeProvider>(TimeProvider.System);

        Render([first, second], telemetry: false).ShouldNotBe(Render([second, first], telemetry: false));
    }
}
