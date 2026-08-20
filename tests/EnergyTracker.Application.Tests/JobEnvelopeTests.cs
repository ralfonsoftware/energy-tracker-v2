using System.Text.Json;
using EnergyTracker.Application.Ports;
using Shouldly;

namespace EnergyTracker.Application.Tests;

// Regression guard for the deferred-work.md edge case (project-context.md): "Job envelopes must
// be plain JSON-serializable records — a delegate/closure payload works against
// InProcessChannelJobQueue but silently fails to serialize on AzureStorageQueueJobQueue."
public class JobEnvelopeTests
{
    private record SamplePayload(Guid ReferenceId, string Note);

    [Fact]
    public void JobEnvelope_with_a_plain_record_payload_round_trips_through_System_Text_Json()
    {
        var envelope = new JobEnvelope<SamplePayload>(
            Guid.NewGuid(), Guid.NewGuid(), "SampleJob", new SamplePayload(Guid.NewGuid(), "hello"));

        var json = JsonSerializer.Serialize(envelope);
        var roundTripped = JsonSerializer.Deserialize<JobEnvelope<SamplePayload>>(json);

        roundTripped.ShouldBe(envelope);
    }

    [Fact]
    public void ProcessSmartPlugImportPayload_round_trips_through_System_Text_Json()
    {
        var payload = new ProcessSmartPlugImportPayload(Guid.NewGuid(), "/tmp/abc.xlsx", "abc.xlsx");

        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<ProcessSmartPlugImportPayload>(json);

        roundTripped.ShouldBe(payload);
    }
}
