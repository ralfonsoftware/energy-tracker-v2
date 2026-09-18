namespace EnergyTracker.Application;

/// <summary>Thrown when Event creation input (Description or OccurredAt) fails validation.</summary>
public class EventValidationException(string message, string errorCode) : Exception(message)
{
    /// <summary>
    /// Stable machine-readable code surfaced in the ProblemDetails payload so the client can render
    /// a localized message (AD-18) instead of echoing this exception's English text.
    /// </summary>
    public string ErrorCode { get; } = errorCode;
}
