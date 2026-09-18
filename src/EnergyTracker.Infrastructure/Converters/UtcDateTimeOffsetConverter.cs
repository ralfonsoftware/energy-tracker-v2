using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EnergyTracker.Infrastructure.Converters;

// Npgsql refuses to write a DateTimeOffset whose offset is not zero to a "timestamp with time
// zone" column ("only offset 0 (UTC) is supported"), while SQL Server's datetimeoffset accepts
// any offset — an AD-2 dual-provider divergence a client sending a non-zero wire offset (exactly
// what project-context.md mandates) would otherwise hit as an unhandled 500 on Postgres only.
//
// ToUniversalTime() shifts only the offset, never the instant, so no stored value's meaning
// changes; the read side is the identity, so previously-stored non-zero offsets (SQL Server)
// still read back exactly as stored.
//
// AD-9 coupling: EveHomeXlsxParser deliberately constructs its DateTimeOffset with TimeSpan.Zero
// (local wall-clock time, never UTC-converted) specifically so this converter is a no-op for that
// data. If that offset is ever changed to a real local offset, ToUniversalTime() here would shift
// those wall-clock values across midnight boundaries — the exact corruption AD-9 exists to
// prevent. See tests/EnergyTracker.Architecture.Tests for the guard pinning this pairing.
public class UtcDateTimeOffsetConverter()
    : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);
