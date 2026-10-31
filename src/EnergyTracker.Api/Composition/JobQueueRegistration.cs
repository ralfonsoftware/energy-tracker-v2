using Azure.Storage.Queues;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Infrastructure.Adapters;

namespace EnergyTracker.Api.Composition;

internal static class JobQueueRegistration
{
    public static IServiceCollection AddJobQueue(this IServiceCollection services, IConfiguration configuration)
    {
        // AD-6: JobQueue:Provider is read exactly once, here at the composition root — same
        // switch-on-lowercased-config-value shape as Database:Provider (Program.cs) and Otel:Exporter (ObservabilityRegistration).
        services.AddScoped<JobHouseholdContext>();
        services.AddSingleton<BackgroundJobProcessor>();
        // Singleton — both queue adapters below (InProcessChannelJobQueue/AzureStorageQueueJobQueue) are
        // themselves singletons and inject this directly (AD-6).
        services.AddSingleton<BackgroundJobEnqueueRecorder>();
        services.AddScoped<IBackgroundJobRepository, BackgroundJobRepository>();
        // AD-6 (amended 2026-10-10): the one writer of BackgroundJob.Status. Timings are operational
        // constants (1 min heartbeat, 5 min stale), not Household configuration.
        services.AddSingleton(JobLifecycleTimings.Default);
        services.AddScoped<IBackgroundJobLifecycle, BackgroundJobLifecycle>();
        services.AddScoped<GetBackgroundJobStatus>();
        services.AddScoped<ListSmartPlugImportJobs>();
        services.AddScoped<CleanUpSmartPlugImportJobs>();
        services.AddScoped<ISmartPlugParser, EveHomeXlsxParser>();
        services.AddScoped<ISmartPlugParser, MerossCsvParser>();
        services.AddScoped<ISmartPlugImportRepository, SmartPlugImportRepository>();
        services.AddScoped<CompleteSmartPlugImportProcessing>();
        services.AddScoped<ProcessSmartPlugImport>();
        services.AddScoped<MapSmartPlugImportToPowerPoint>();

        var jobQueueProvider = (configuration["JobQueue:Provider"] ?? string.Empty).Trim().ToLowerInvariant();
        switch (jobQueueProvider)
        {
            case "azurestoragequeue":
                var jobQueueConnectionString = configuration["JobQueue:ConnectionString"] ?? string.Empty;
                // Base64 message encoding so a JSON payload survives the queue message's XML envelope
                // untouched (the SDK's raw/"None" default is not XML-safe for arbitrary JSON text).
                services.AddSingleton(sp =>
                {
                    var queueOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
                    // A body that fails Base64 decoding would otherwise make ReceiveMessagesAsync throw forever.
                    var decodingLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<AzureStorageQueueJobProcessingService>();
                    queueOptions.MessageDecodingFailed += args => QueueMessageDecodingFailureHandler.HandleAsync(args, decodingLogger);
                    return new QueueClient(jobQueueConnectionString, "jobs", queueOptions);
                });
                services.AddSingleton(QueueConsumerTimings.Default);
                services.AddSingleton<IBackgroundJobQueue, AzureStorageQueueJobQueue>();
                services.AddHostedService<AzureStorageQueueJobProcessingService>();
                break;
            default:
                // Unset/unrecognized: same "unset stays off"-shaped default as Otel:Exporter, not
                // Database:Provider's hard-required default — the in-process adapter needs no external
                // config to function.
                services.AddSingleton<InProcessChannelJobQueue>();
                // AD-24: process-local startup recovery; the Azure adapter never runs it.
                services.AddSingleton<InProcessJobStartupSweep>();
                services.AddSingleton<IBackgroundJobQueue>(sp => sp.GetRequiredService<InProcessChannelJobQueue>());
                services.AddHostedService<InProcessChannelJobProcessingService>();
                break;
        }

        // AD-24: the instance heartbeat observes the single-instance invariant (a warning in the log, never a
        // startup block). Outside the queue-provider switch on purpose: both providers need it.
        services.AddSingleton(sp => new AppInstanceIdentity(Guid.NewGuid(), sp.GetRequiredService<TimeProvider>().GetUtcNow()));
        services.AddSingleton(InstanceHeartbeatTimings.Default);
        services.AddScoped<IAppInstanceRepository, AppInstanceRepository>();
        services.AddScoped<RecordInstanceHeartbeat>();
        services.AddHostedService<InstanceHeartbeatService>();

        return services;
    }
}
