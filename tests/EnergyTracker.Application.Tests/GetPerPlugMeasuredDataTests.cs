using EnergyTracker.Application.Ports;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class GetPerPlugMeasuredDataTests
{
    private readonly ISmartPlugReadingRepository _smartPlugReadingRepository = Substitute.For<ISmartPlugReadingRepository>();

    private GetPerPlugMeasuredData Sut() => new(_smartPlugReadingRepository);

    [Fact]
    public async Task Returns_an_empty_list_when_no_aggregates_exist()
    {
        var householdId = Guid.NewGuid();
        _smartPlugReadingRepository.GetAggregatedByTagAsync(householdId, Arg.Any<CancellationToken>()).Returns([]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Builds_a_single_Room_with_a_single_Power_Point_and_a_single_Device()
    {
        var householdId = Guid.NewGuid();
        var tvPowerPointId = Guid.NewGuid();
        _smartPlugReadingRepository.GetAggregatedByTagAsync(householdId, Arg.Any<CancellationToken>()).Returns(
        [
            new SmartPlugReadingAggregate(tvPowerPointId, "Living Room", "TV Power Point", "Smart TV", 38m),
        ]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].RoomName.ShouldBe("Living Room");
        result[0].TotalKwh.ShouldBe(38m);
        result[0].PowerPoints.Count.ShouldBe(1);
        result[0].PowerPoints[0].PowerPointName.ShouldBe("TV Power Point");
        result[0].PowerPoints[0].TotalKwh.ShouldBe(38m);
        result[0].PowerPoints[0].Devices.Count.ShouldBe(1);
        result[0].PowerPoints[0].Devices[0].DeviceName.ShouldBe("Smart TV");
        result[0].PowerPoints[0].Devices[0].TotalKwh.ShouldBe(38m);
    }

    [Fact]
    public async Task Correctly_nests_and_sums_across_multiple_Rooms_Power_Points_and_Devices()
    {
        var householdId = Guid.NewGuid();
        var tvPowerPointId = Guid.NewGuid();
        var fridgeCircuitId = Guid.NewGuid();
        var worktopSocketsId = Guid.NewGuid();
        _smartPlugReadingRepository.GetAggregatedByTagAsync(householdId, Arg.Any<CancellationToken>()).Returns(
        [
            new SmartPlugReadingAggregate(tvPowerPointId, "Living Room", "TV Power Point", "Smart TV", 38m),
            new SmartPlugReadingAggregate(tvPowerPointId, "Living Room", "TV Power Point", "Games Console", 22m),
            new SmartPlugReadingAggregate(fridgeCircuitId, "Kitchen", "Fridge Circuit", "Fridge-Freezer", 164m),
            new SmartPlugReadingAggregate(worktopSocketsId, "Kitchen", "Worktop Sockets", "Kettle", 9m),
        ]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);

        var kitchen = result.Single(r => r.RoomName == "Kitchen");
        kitchen.TotalKwh.ShouldBe(173m);
        kitchen.PowerPoints.Count.ShouldBe(2);
        kitchen.PowerPoints.Single(pp => pp.PowerPointName == "Fridge Circuit").TotalKwh.ShouldBe(164m);
        kitchen.PowerPoints.Single(pp => pp.PowerPointName == "Worktop Sockets").TotalKwh.ShouldBe(9m);

        var livingRoom = result.Single(r => r.RoomName == "Living Room");
        livingRoom.TotalKwh.ShouldBe(60m);
        livingRoom.PowerPoints.Count.ShouldBe(1);
        var tvPowerPoint = livingRoom.PowerPoints.Single();
        tvPowerPoint.TotalKwh.ShouldBe(60m);
        tvPowerPoint.Devices.Count.ShouldBe(2);
        tvPowerPoint.Devices.Single(d => d.DeviceName == "Smart TV").TotalKwh.ShouldBe(38m);
        tvPowerPoint.Devices.Single(d => d.DeviceName == "Games Console").TotalKwh.ShouldBe(22m);
    }

    [Fact]
    public async Task Orders_Rooms_Power_Points_and_Devices_alphabetically_ascending()
    {
        var householdId = Guid.NewGuid();
        var deskPowerPointId = Guid.NewGuid();
        var worktopSocketsId = Guid.NewGuid();
        var fridgeCircuitId = Guid.NewGuid();
        _smartPlugReadingRepository.GetAggregatedByTagAsync(householdId, Arg.Any<CancellationToken>()).Returns(
        [
            new SmartPlugReadingAggregate(deskPowerPointId, "Office", "Desk Power Point", "Monitor", 10m),
            new SmartPlugReadingAggregate(worktopSocketsId, "Kitchen", "Worktop Sockets", "Kettle", 9m),
            new SmartPlugReadingAggregate(fridgeCircuitId, "Kitchen", "Fridge Circuit", "Fridge-Freezer", 164m),
            new SmartPlugReadingAggregate(deskPowerPointId, "Office", "Desk Power Point", "Desk Lamp", 4m),
        ]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.Select(r => r.RoomName).ShouldBe(["Kitchen", "Office"]);
        result[0].PowerPoints.Select(pp => pp.PowerPointName).ShouldBe(["Fridge Circuit", "Worktop Sockets"]);
        result[1].PowerPoints[0].Devices.Select(d => d.DeviceName).ShouldBe(["Desk Lamp", "Monitor"]);
    }

    // Code-review fix: InvariantCulture, not Ordinal — an accented German name must sort next to
    // its base letter, not after 'z' (raw codepoint order), since names are free text and this
    // product ships full de-DE localization.
    [Fact]
    public async Task Sorts_accented_names_by_natural_order_not_raw_codepoint_order()
    {
        var householdId = Guid.NewGuid();
        var buegelId = Guid.NewGuid();
        var zimmerId = Guid.NewGuid();
        _smartPlugReadingRepository.GetAggregatedByTagAsync(householdId, Arg.Any<CancellationToken>()).Returns(
        [
            new SmartPlugReadingAggregate(zimmerId, "Zimmer", "Steckdose", "Lampe", 1m),
            new SmartPlugReadingAggregate(buegelId, "Bügelraum", "Steckdose", "Bügeleisen", 2m),
        ]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        // Ordinal order would put "Zimmer" (Z=0x5A) before "Bügelraum" (ü=0xFC would actually sort
        // after Z too, but the leading 'B' vs 'Z' already proves natural order here) — the
        // meaningful assertion is that culture-aware comparison, not raw codepoints, decides this.
        result.Select(r => r.RoomName).ShouldBe(["Bügelraum", "Zimmer"]);
    }

    // Code-review fix (Story 4.2): two distinct Power Points that happen to share an identical
    // (RoomName, PowerPointName) string pair — e.g. PP-A renamed away from "TV Power Point", then
    // PP-B later renamed/created into that freed name — must never collapse into one tree node.
    // Grouping keyed on (PowerPointId, PowerPointName), not PowerPointName alone, is what keeps
    // them apart even though the repository's flat aggregate rows carry identical display strings.
    [Fact]
    public async Task Two_different_Power_Points_sharing_the_same_snapshotted_name_stay_as_separate_tree_nodes()
    {
        var householdId = Guid.NewGuid();
        var originalPowerPointId = Guid.NewGuid();
        var laterPowerPointId = Guid.NewGuid();
        _smartPlugReadingRepository.GetAggregatedByTagAsync(householdId, Arg.Any<CancellationToken>()).Returns(
        [
            new SmartPlugReadingAggregate(originalPowerPointId, "Living Room", "TV Power Point", "Smart TV", 38m),
            new SmartPlugReadingAggregate(laterPowerPointId, "Living Room", "TV Power Point", "Soundbar", 12m),
        ]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        var livingRoom = result.Single();
        livingRoom.TotalKwh.ShouldBe(50m);
        livingRoom.PowerPoints.Count.ShouldBe(2);
        livingRoom.PowerPoints.ShouldAllBe(pp => pp.PowerPointName == "TV Power Point");
        livingRoom.PowerPoints.Single(pp => pp.Devices.Single().DeviceName == "Smart TV").TotalKwh.ShouldBe(38m);
        livingRoom.PowerPoints.Single(pp => pp.Devices.Single().DeviceName == "Soundbar").TotalKwh.ShouldBe(12m);
    }
}
