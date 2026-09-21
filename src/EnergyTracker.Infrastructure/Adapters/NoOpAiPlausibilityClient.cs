using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-8: registered when AiPlausibility:BaseUrl is unset/blank at the composition root. Always
// returns null — CorrelateEvent is the one place allowed to check "is AI configured/enabled"; every
// other code path just sees an absent correlation, exactly as if a real client had found no match.
public class NoOpAiPlausibilityClient : IAiPlausibilityClient
{
    public Task<AiPlausibilityDirection?> ClassifyAsync(
        string eventDescription,
        IReadOnlyCollection<AiPlausibilityDirection> observedDirections,
        CancellationToken cancellationToken) =>
        Task.FromResult<AiPlausibilityDirection?>(null);
}
