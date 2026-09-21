using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-8: the one real IAiPlausibilityClient adapter — a plain HttpClient call to the OpenAI-
// compatible chat/completions shape LMStudio's local server and effectively every cloud LLM
// provider expose identically. "Local vs cloud" is purely which BaseUrl/ApiKey this typed client
// was configured with at the composition root (Program.cs); this class itself has no branch on
// deployment target. No SDK dependency — Directory.Packages.props has zero AI/LLM package
// references by design (Dev Notes).
public class OpenAiCompatibleClient(HttpClient httpClient, ILogger<OpenAiCompatibleClient> logger, TimeSpan? requestTimeout = null)
    : IAiPlausibilityClient
{
    // A classification call is small and latency-sensitive to the job-processing loop (AD-6 has
    // exactly one worker) — this bounds a slow/hung local model or cloud endpoint so one flaky
    // call can't stall every other queued job behind it. Overridable (constructor param, not a
    // hardcoded constant) purely so OpenAiCompatibleClientTests's timeout scenario doesn't need to
    // block for the real production duration.
    private readonly TimeSpan requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);

    private const string SystemPrompt =
        "You classify whether a short household event description plausibly explains an observed " +
        "energy consumption deviation. Respond with exactly one word and nothing else: \"Bump\", " +
        "\"Dip\", or \"None\".";

    public async Task<AiPlausibilityDirection?> ClassifyAsync(
        string eventDescription,
        IReadOnlyCollection<AiPlausibilityDirection> observedDirections,
        CancellationToken cancellationToken)
    {
        if (observedDirections.Count == 0)
        {
            return null;
        }

        var observedLabels = string.Join(" or ", observedDirections.Select(d => d.ToString()));
        var userPrompt =
            $"Observed consumption deviation(s) in the time window around this event: {observedLabels}. " +
            $"Household event: \"{eventDescription}\". " +
            "Which single observed deviation, if any, does this event plausibly explain?";

        var request = new ChatCompletionRequest(
            Model: "default",
            Messages: [new ChatMessage("system", SystemPrompt), new ChatMessage("user", userPrompt)],
            Temperature: 0);

        // Linked so the caller's own cancellationToken (e.g. job-processing shutdown) still wins —
        // only OUR timeout firing is swallowed below; a genuine caller-driven cancellation must
        // still propagate as OperationCanceledException so the job-processing loop treats it as
        // retryable-on-shutdown (BackgroundJobProcessor.ProcessAsync), not "completed, no match".
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(requestTimeout);

        try
        {
            using var response = await httpClient.PostAsJsonAsync("v1/chat/completions", request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OpenAiCompatibleClient: classification call returned non-success status {StatusCode}", response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(timeoutCts.Token);
            var content = body?.Choices?.Count > 0 ? body.Choices[0].Message?.Content : null;
            return ParseDirection(content, observedDirections);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAiCompatibleClient: classification call timed out after {Timeout}", requestTimeout);
            return null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "OpenAiCompatibleClient: classification call failed");
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "OpenAiCompatibleClient: malformed classification response");
            return null;
        }
    }

    // Defensive, case-insensitive match — anything else (extra prose, an unrecognized word, an
    // empty body) is "no plausible match", never a thrown exception. Also refuses to return a
    // direction the caller never listed as observed, in case the model hallucinates outside the
    // options it was given.
    private static AiPlausibilityDirection? ParseDirection(
        string? content, IReadOnlyCollection<AiPlausibilityDirection> observedDirections)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();
        foreach (var direction in observedDirections)
        {
            if (string.Equals(trimmed, direction.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return direction;
            }
        }

        return null;
    }

    private record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature);

    private record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatCompletionChoice>? Choices);

    private record ChatCompletionChoice(
        [property: JsonPropertyName("message")] ChatMessage? Message);
}
