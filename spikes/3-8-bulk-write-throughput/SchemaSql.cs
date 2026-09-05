namespace BulkWriteThroughputSpike;

// Raw DDL for the spike-only tables/indexes (AC #1, #2), issued directly against the real Azure
// SQL Basic / Postgres databases this project already uses — never Testcontainers (see Dev
// Notes on this story). Index syntax is copied from the real migrations
// (20260822165109_AddSmartPlugReadingUniqueIndex / 20260822165112_...), renamed onto the
// Spike_SmartPlugReading table.
public static class SchemaSql
{
    public static string[] CreateStatements(SpikeProvider provider) => provider switch
    {
        SpikeProvider.SqlServer =>
        [
            """
            CREATE TABLE [Spike_SmartPlugImport] (
                [Id] uniqueidentifier NOT NULL PRIMARY KEY,
                [HouseholdId] uniqueidentifier NOT NULL,
                [CreatedAtUtc] datetimeoffset NOT NULL
            );
            """,
            $"""
            CREATE TABLE [Spike_SmartPlugReading] (
                [Id] uniqueidentifier NOT NULL PRIMARY KEY,
                [HouseholdId] uniqueidentifier NOT NULL,
                [PowerPointId] uniqueidentifier NULL,
                [IntervalStart] datetimeoffset NOT NULL,
                [IntervalEnd] datetimeoffset NOT NULL,
                [KwhValue] decimal(18,6) NOT NULL,
                [RoomName] nchar({DataGenerator.RoomNameLength}) NOT NULL,
                [PowerPointName] nchar({DataGenerator.PowerPointNameLength}) NOT NULL,
                [DeviceName] nchar({DataGenerator.DeviceNameLength}) NOT NULL,
                [SmartPlugImportId] uniqueidentifier NULL,
                CONSTRAINT [FK_Spike_SmartPlugReading_Spike_SmartPlugImport] FOREIGN KEY ([SmartPlugImportId])
                    REFERENCES [Spike_SmartPlugImport] ([Id]) ON DELETE NO ACTION
            );
            """,
            // Same two AD-23/AD-20 unique indexes as production, verbatim syntax from
            // 20260822165112_AddSmartPlugReadingUniqueIndex.cs.
            """
            CREATE UNIQUE NONCLUSTERED INDEX [IX_Spike_SmartPlugReading_PowerPointId_IntervalStart]
            ON [Spike_SmartPlugReading] ([PowerPointId], [IntervalStart])
            WHERE [PowerPointId] IS NOT NULL;
            """,
            """
            CREATE UNIQUE NONCLUSTERED INDEX [IX_Spike_SmartPlugReading_HouseholdId_IntervalStart_WhenPowerPointIdNull]
            ON [Spike_SmartPlugReading] ([HouseholdId], [IntervalStart])
            WHERE [PowerPointId] IS NULL;
            """,
            "CREATE INDEX [IX_Spike_SmartPlugReading_SmartPlugImportId] ON [Spike_SmartPlugReading] ([SmartPlugImportId]);",
        ],
        SpikeProvider.Postgres =>
        [
            """
            CREATE TABLE "Spike_SmartPlugImport" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "HouseholdId" uuid NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL
            );
            """,
            $"""
            CREATE TABLE "Spike_SmartPlugReading" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "HouseholdId" uuid NOT NULL,
                "PowerPointId" uuid NULL,
                "IntervalStart" timestamptz NOT NULL,
                "IntervalEnd" timestamptz NOT NULL,
                "KwhValue" numeric(18,6) NOT NULL,
                "RoomName" char({DataGenerator.RoomNameLength}) NOT NULL,
                "PowerPointName" char({DataGenerator.PowerPointNameLength}) NOT NULL,
                "DeviceName" char({DataGenerator.DeviceNameLength}) NOT NULL,
                "SmartPlugImportId" uuid NULL,
                CONSTRAINT "FK_Spike_SmartPlugReading_Spike_SmartPlugImport" FOREIGN KEY ("SmartPlugImportId")
                    REFERENCES "Spike_SmartPlugImport" ("Id") ON DELETE NO ACTION
            );
            """,
            // Postgres never treats NULL = NULL as a match even in a plain composite unique
            // index, so (unlike SQL Server) no explicit filter is needed on the first index —
            // verbatim mirror of 20260822165109_AddSmartPlugReadingUniqueIndex.cs's own choice.
            """
            CREATE UNIQUE INDEX "IX_Spike_SmartPlugReading_PowerPointId_IntervalStart"
            ON "Spike_SmartPlugReading" ("PowerPointId", "IntervalStart");
            """,
            """
            CREATE UNIQUE INDEX "IX_Spike_SmartPlugReading_HouseholdId_IntervalStart_WhenPowerPointIdNull"
            ON "Spike_SmartPlugReading" ("HouseholdId", "IntervalStart")
            WHERE "PowerPointId" IS NULL;
            """,
            """CREATE INDEX "IX_Spike_SmartPlugReading_SmartPlugImportId" ON "Spike_SmartPlugReading" ("SmartPlugImportId");""",
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    // Drop order matters: child (reading) before parent (import) — FK dependency.
    public static string[] DropStatements(SpikeProvider provider) => provider switch
    {
        SpikeProvider.SqlServer =>
        [
            "IF OBJECT_ID('Spike_SmartPlugReading', 'U') IS NOT NULL DROP TABLE [Spike_SmartPlugReading];",
            "IF OBJECT_ID('Spike_SmartPlugImport', 'U') IS NOT NULL DROP TABLE [Spike_SmartPlugImport];",
        ],
        SpikeProvider.Postgres =>
        [
            """DROP TABLE IF EXISTS "Spike_SmartPlugReading";""",
            """DROP TABLE IF EXISTS "Spike_SmartPlugImport";""",
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    // AC #10: confirm zero Spike_* objects remain, via schema introspection.
    public static string RemainingSpikeObjectsQuery(SpikeProvider provider) => provider switch
    {
        SpikeProvider.SqlServer =>
            "SELECT name FROM sys.tables WHERE name LIKE 'Spike\\_%' ESCAPE '\\';",
        SpikeProvider.Postgres =>
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name LIKE 'Spike\\_%' ESCAPE '\\';",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static string TruncateReadingsStatement(SpikeProvider provider) => provider switch
    {
        SpikeProvider.SqlServer => "TRUNCATE TABLE [Spike_SmartPlugReading];",
        SpikeProvider.Postgres => """TRUNCATE TABLE "Spike_SmartPlugReading";""",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}
