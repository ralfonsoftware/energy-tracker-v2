using EnergyTracker.Api.Endpoints;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Infrastructure.Adapters;

namespace EnergyTracker.Api.Composition;

internal static class ApplicationServicesRegistration
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IHouseholdRepository, HouseholdRepository>();
        services.AddScoped<ICurrentHouseholdAccessor, CurrentHouseholdAccessor>();
        // Singleton like the factory it depends on; the membership middleware calls it once per /api request.
        services.AddSingleton<HouseholdMembershipResolver>();
        services.AddScoped<CreateHousehold>();
        services.AddScoped<CreateHouseholdInvite>();
        services.AddScoped<AcceptHouseholdInvite>();
        services.AddScoped<RemoveHouseholdMember>();
        services.AddScoped<ListHouseholdMembers>();
        services.AddScoped<ListOpenHouseholdInvites>();
        services.AddScoped<RevokeHouseholdInvite>();
        services.AddScoped<SetYearlyBaseline>();
        services.AddScoped<SetHouseholdLocale>();

        services.AddScoped<ITaggingScaffoldRepository, TaggingScaffoldRepository>();
        services.AddScoped<CreateRoom>();
        services.AddScoped<RenameRoom>();
        services.AddScoped<ArchiveRoom>();
        services.AddScoped<CreatePowerPoint>();
        services.AddScoped<RenamePowerPoint>();
        services.AddScoped<ArchivePowerPoint>();
        services.AddScoped<MovePowerPoint>();
        services.AddScoped<CreateDevice>();
        services.AddScoped<RenameDevice>();
        services.AddScoped<ArchiveDevice>();
        services.AddScoped<MoveDevice>();

        services.AddScoped<IMeterReadingRepository, MeterReadingRepository>();
        services.AddScoped<IMeterRegressionPromptRepository, MeterRegressionPromptRepository>();
        // Singleton: the per-Household lock dictionary must survive across scoped requests to actually
        // serialize concurrent recomputes for the same Household (AD-7).
        services.AddSingleton<IHouseholdRecomputeLock, HouseholdRecomputeLock>();
        services.AddScoped<IStatusRecomputeService, StatusRecomputeService>();
        services.AddScoped<ISmartPlugCoverageSignal, SmartPlugCoverageSignal>();
        services.AddScoped<IAuditCorrectionRecorder, AuditCorrectionRecorder>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<CreateMeterReading>();
        services.AddScoped<ResolveMeterRegressionPrompt>();
        services.AddScoped<GetOpenMeterRegressionPrompt>();
        services.AddScoped<GetCurrentStatus>();
        services.AddScoped<GetMeterReadingHistory>();
        services.AddScoped<EditMeterReading>();
        services.AddScoped<IStatusSnapshotRepository, StatusSnapshotRepository>();
        services.AddScoped<GetStatusHistory>();
        services.AddScoped<ISmartPlugReadingRepository, SmartPlugReadingRepository>();
        services.AddScoped<GetPerPlugMeasuredData>();

        services.AddScoped<ITariffRepository, TariffRepository>();
        services.AddScoped<CreateTariff>();
        services.AddScoped<GetTariffHistory>();
        services.AddScoped<EditTariff>();
        services.AddScoped<CompareTariff>();
        services.AddScoped<GetTariffCheckReminder>();

        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<CreateEvent>();
        services.AddScoped<GetEventHistory>();
        services.AddScoped<SetAiPlausibilityEnabled>();
        services.AddScoped<CorrelateEvent>();
        services.AddScoped<RequeueEventCorrelations>();

        services.AddScoped<IHouseholdExportReader, HouseholdExportReader>();
        services.AddScoped<ExportHouseholdData>();

        // Restore/migration import. Registry is a singleton (in-memory token→temp-file
        // mapping, MemoryHouseholdImportUploadRegistry's own doc comment); the writer/use case are scoped
        // like every other DbContext-backed adapter/use case in this file.
        services.AddSingleton<IHouseholdImportUploadRegistry, MemoryHouseholdImportUploadRegistry>();
        // Uploads are staged in this process's temp directory: AD-24 (single instance).
        services.AddSingleton(new HouseholdImportUploadSettings(Path.GetTempPath(), HouseholdImportEndpoints.MaxFileSizeBytes));
        services.AddScoped<ValidateHouseholdImport>();
        services.AddScoped<IHouseholdRestoreWriter, HouseholdRestoreWriter>();
        services.AddScoped<RestoreHouseholdData>();

        return services;
    }
}
