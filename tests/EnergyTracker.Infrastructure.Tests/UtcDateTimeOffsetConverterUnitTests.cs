using EnergyTracker.Infrastructure.Converters;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// A fast, dependency-free complement to UtcDateTimeOffsetConverterTests (which require Docker):
// exercises UtcDateTimeOffsetConverter's own conversion delegates directly, with no DbContext and
// no Testcontainer, so the converter's core "shifts only the offset, never the instant" property
// is checkable in any environment.
public class UtcDateTimeOffsetConverterUnitTests
{
    // ConvertToProviderExpression/ConvertFromProviderExpression are typed Expression<Func<DateTimeOffset,
    // DateTimeOffset>> on this ValueConverter<DateTimeOffset, DateTimeOffset> subclass — Compile() hands
    // back a directly-callable, properly-typed delegate, no DynamicInvoke/null-forgiving cast needed.
    private static readonly Func<DateTimeOffset, DateTimeOffset> WriteSide = new UtcDateTimeOffsetConverter().ConvertToProviderExpression.Compile();
    private static readonly Func<DateTimeOffset, DateTimeOffset> ReadSide = new UtcDateTimeOffsetConverter().ConvertFromProviderExpression.Compile();

    [Fact]
    public void The_write_side_shifts_a_non_zero_offset_to_UTC_without_changing_the_instant()
    {
        var submitted = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2));

        var converted = WriteSide(submitted);

        converted.Offset.ShouldBe(TimeSpan.Zero);
        converted.ShouldBe(submitted.ToUniversalTime());
        converted.UtcDateTime.ShouldBe(submitted.UtcDateTime);
    }

    [Fact]
    public void The_write_side_shifts_a_negative_offset_to_UTC_without_changing_the_instant()
    {
        var submitted = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(-5));

        var converted = WriteSide(submitted);

        converted.Offset.ShouldBe(TimeSpan.Zero);
        converted.ShouldBe(submitted.ToUniversalTime());
    }

    [Fact]
    public void The_write_side_is_a_no_op_for_an_already_zero_offset_value()
    {
        var submitted = DateTimeOffset.UtcNow;

        var converted = WriteSide(submitted);

        converted.ShouldBe(submitted);
    }

    [Fact]
    public void The_read_side_is_the_identity_so_a_previously_stored_non_zero_offset_reads_back_unchanged()
    {
        var storedNonZeroOffset = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2));

        var read = ReadSide(storedNonZeroOffset);

        read.ShouldBe(storedNonZeroOffset);
        read.Offset.ShouldBe(storedNonZeroOffset.Offset);
    }
}
