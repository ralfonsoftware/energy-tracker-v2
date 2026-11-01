using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.22 (AC #3, #4; AD-26, ADR 0008): the Azure Monitor branch cannot run an exporter in tests, so this source scan is
// its automated proof that span redaction is wired on both exporter branches of ObservabilityRegistration.
public class InviteTokenRedactionCoversBothExportersTests
{
    private const string File_ = "src/EnergyTracker.Api/Composition/ObservabilityRegistration.cs";
    private const string Call = "AddInviteTokenRedaction(";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EnergyTracker.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("EnergyTracker.sln not found above the test binaries.");
    }

    private static string Code() => CSharpSource.StripComments(System.IO.File.ReadAllText(Path.Combine(FindRepoRoot(), File_)));

    private static string Branch(string code, string label, string next)
    {
        var start = code.IndexOf(label, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{File_} no longer has {label}: update this guard with it.");
        var end = code.IndexOf(next, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return code[start..end];
    }

    // The call must sit on the tracer builder: text between .WithTracing( and .WithMetrics( of the same branch.
    private static string Tracing(string branch) => Branch(branch, ".WithTracing(", ".WithMetrics(");

    [Fact]
    public void The_azuremonitor_and_otlp_branches_both_call_the_span_redaction()
    {
        var code = Code();

        Tracing(Branch(code, "case \"azuremonitor\":", "case \"otlp\":")).ShouldContain(Call);
        Tracing(Branch(code, "case \"otlp\":", "default:")).ShouldContain(Call);
    }

    [Fact]
    public void Known_bad_sample_a_branch_without_the_call_is_detected()
    {
        const string bad = "case \"azuremonitor\": builder.Services.AddOpenTelemetry().UseAzureMonitor(); break; case \"otlp\":";

        Branch(bad, "case \"azuremonitor\":", "case \"otlp\":").Contains(Call, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void Known_bad_sample_a_call_on_the_metrics_builder_is_detected()
    {
        const string bad = "case \"otlp\": x.WithTracing(t => t.AddOtlpExporter()).WithMetrics(m => m.AddInviteTokenRedaction()); default:";

        Tracing(Branch(bad, "case \"otlp\":", "default:")).Contains(Call, StringComparison.Ordinal).ShouldBeFalse();
    }
}
