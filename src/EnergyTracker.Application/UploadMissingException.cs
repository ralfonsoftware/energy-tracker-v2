namespace EnergyTracker.Application;

// AD-6/AD-24: a job's temp payload file is gone by the time the job runs (the replica or its disk was
// replaced between enqueue and dequeue). Terminal and never retried; BackgroundJobProcessor maps this
// type to JobFailureCodes.UploadMissing, so no English text ever reaches the client.
public class UploadMissingException(string message) : Exception(message);
