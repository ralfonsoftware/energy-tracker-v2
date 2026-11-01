using System.Diagnostics;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.23 (AD-19, ADR 0009): a host that throws before app.Run() must end its process with exit code 1 and
// leave the message on stderr. Without that, a dotnet process that is PID 1 in a container keeps spinning
// after the runtime's abort and restart policies never see the failure. The tests start the built API as a
// real child process, because only a process boundary shows what the operator's platform sees. No database is
// needed: every failing path throws before a connection opens, and the passing one only listens.
public class FailedStartupExitTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1";
    private static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("missing-connection-string", "ConnectionStrings__Default")]
    [InlineData("data-protection-fail-closed", "DataProtection:AllowUnprotectedKeys")]
    [InlineData("unusable-ai-endpoint", "Ai__Endpoint")]
    [InlineData("unsupported-database-provider", "Unsupported Database:Provider 'bogus'")]
    public async Task A_host_that_throws_during_startup_exits_with_code_1_and_prints_the_message(string scenario, string expectedInMessage)
    {
        var environment = ScenarioEnvironment(scenario);

        var result = await RunApiAsync(environment, ExitDeadline, TestContext.Current.CancellationToken);

        result.TimedOut.ShouldBeFalse($"The process did not exit within {ExitDeadline}. Stderr: {result.StandardError}");
        result.ExitCode.ShouldBe(1, "A runtime abort (134) or a hang is what the container platform must never see.");
        result.StandardError.ShouldContain(expectedInMessage);
    }

    [Fact]
    public async Task The_probe_reports_a_child_that_keeps_running_as_timed_out()
    {
        // Known-bad sample: a valid configuration starts and listens, so it never exits. The helper must say so,
        // otherwise the exit-code assertions above could pass for a process that merely was not waited for.
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["ConnectionStrings__Default"] = UnreachableDatabase,
            ["DataProtection__AllowUnprotectedKeys"] = "true",
        };

        var result = await RunApiAsync(environment, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        result.TimedOut.ShouldBeTrue();
        result.ExitCode.ShouldBeNull();
        result.StandardOutput.ShouldContain("Now listening", customMessage: "The child must have started, not merely been slow to fail.");
    }

    private static Dictionary<string, string> ScenarioEnvironment(string scenario)
    {
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["ConnectionStrings__Default"] = UnreachableDatabase,
            ["DataProtection__AllowUnprotectedKeys"] = "true",
        };

        switch (scenario)
        {
            case "missing-connection-string":
                environment["ConnectionStrings__Default"] = string.Empty;
                break;
            case "data-protection-fail-closed":
                environment["DataProtection__AllowUnprotectedKeys"] = string.Empty;
                break;
            case "unusable-ai-endpoint":
                environment["Ai__Endpoint"] = "localhost:1234";
                break;
            case "unsupported-database-provider":
                environment["Database__Provider"] = "bogus";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario.");
        }

        return environment;
    }

    private static async Task<ChildResult> RunApiAsync(
        IReadOnlyDictionary<string, string> environment,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var apiDll = Path.Combine(AppContext.BaseDirectory, "EnergyTracker.Api.dll");
        File.Exists(apiDll).ShouldBeTrue($"{apiDll} must be in the test output; the Api project is referenced.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add(apiDll);
        // The child starts from a copy of this process's environment. A developer's or CI's own settings for the
        // keys under test would change which throw fires, so only the scenario decides them.
        foreach (var key in startInfo.Environment.Keys.Where(IsKeyUnderTest).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The dotnet child process did not start.");
        try
        {
            var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);

            using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadlineSource.CancelAfter(deadline);
            try
            {
                await process.WaitForExitAsync(deadlineSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillIfRunning(process);
                await process.WaitForExitAsync(CancellationToken.None);
                return new ChildResult(null, true, await standardError, await standardOutput);
            }

            return new ChildResult(process.ExitCode, false, await standardError, await standardOutput);
        }
        finally
        {
            // Also covers a cancelled test run: a listening child must not outlive it.
            KillIfRunning(process);
        }
    }

    private static bool IsKeyUnderTest(string key) =>
        key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("DataProtection__", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Database__", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Ai__", StringComparison.OrdinalIgnoreCase);

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The child exited between the check and the kill.
        }
    }

    private sealed record ChildResult(int? ExitCode, bool TimedOut, string StandardError, string StandardOutput);
}
