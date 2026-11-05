namespace EnergyTracker.Application;

// Story 11.25, AD-6: a restore file would not fit the process's memory. Thrown before the writer's delete
// phase; BackgroundJobProcessor maps this type to JobFailureCodes.RestoreTooLarge, so the message below is
// logged and never reaches the client.
public class RestoreTooLargeException(string message) : Exception(message);
