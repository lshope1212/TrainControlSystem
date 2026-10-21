using CTC.Core.Dispatching;
using CTC.Core.Exceptions;
using CTC.Core.Models;
using CTC.Core.Scheduling;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Dwell times at schedule stops: the 45 s station / 0 s default (<see cref="DwellTimePolicy"/>),
/// Schedule Builder editing (<see cref="ScheduleTemplateRow"/>), CSV import, schedule
/// validation, the Active Movement Plan (<see cref="CTCService.UpdateMovementPlanStopAsync"/>),
/// and the runtime <c>Arrived → Dwelling → EnRoute</c> cycle.
/// </summary>
/// <remarks>
/// Uses the Blue Line (50 m blocks, 50 km/h) with a station at A3; no other block is a station.
/// </remarks>
[TestClass]
public class CtcDwellTimeTests
{
    private const string StationBlockId = "A3";

    private static readonly TimeSpan StationDwell = TimeSpan.FromSeconds(45);

    private static TimeSpan At(int hours, int minutes, int seconds) => new TimeSpan(hours, minutes, seconds);

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private static TrackLayoutMessage LayoutWithStation()
    {
        var layout = BlueLine.CreateLayout();
        layout.Lines[0].Blocks.Single(block => block.BlockId == StationBlockId).StationName = "Station A3";
        return layout;
    }

    private static CTCService CreateService(FakeMessageSender? sender = null)
    {
        var service = new CTCService(sender ?? new FakeMessageSender());
        service.ApplyTrackLayout(LayoutWithStation());
        return service;
    }

    private static CtcLineState Line(CTCService service) => service.State.FindLine(BlueLine.LineId)!;

    private static ScheduleTemplate Template(CTCService service, int trainCount = 1) =>
        ScheduleTemplateFactory.Create(Line(service), trainCount);

    private static ScheduleTemplateRow Row(ScheduleTemplate template, string blockId) =>
        template.Rows.Single(row => row.BlockId == blockId);

    /// <summary>
    /// Train 000: A1 at 12:00:00, A3 (station) at 12:01:00 with <paramref name="a3Dwell"/>, A5 at
    /// <paramref name="a5Arrival"/>, routed along branch B to B10 at 12:10:00.
    /// </summary>
    private static ScheduledTrain Schedule(TimeSpan a3Dwell, TimeSpan a5Arrival)
    {
        var train = new ScheduledTrain { TrainId = "000", LineId = BlueLine.LineId };
        foreach (var blockId in BlueLine.BranchB)
        {
            train.Route.Add(new ScheduledRouteBlock
            {
                BlockId = blockId,
                ArrivalTime = blockId switch
                {
                    "A1" => At(12, 0, 0),
                    "A3" => At(12, 1, 0),
                    "A5" => a5Arrival,
                    "B10" => At(12, 10, 0),
                    _ => null,
                },
                DwellTime = blockId == "A3" ? a3Dwell : TimeSpan.Zero,
            });
        }

        return train;
    }

    private static async Task<(CTCService Service, FakeMessageSender Sender)> DispatchAsync(TimeSpan? a3Dwell = null, TimeSpan? a5Arrival = null)
    {
        var sender = new FakeMessageSender();
        var service = CreateService(sender);
        service.QueueSchedule([Schedule(a3Dwell ?? StationDwell, a5Arrival ?? At(12, 5, 0))]);
        await service.SetSystemTimeAsync(At(12, 0, 0));
        Assert.HasCount(1, service.State.DispatchedTrains, "Setup: the train should have been dispatched.");
        return (service, sender);
    }

    private static DispatchedTrainState Train(CTCService service) => service.State.DispatchedTrains.Single();

    private static List<MovementSuggestionMessage> Suggestions(FakeMessageSender sender) =>
        sender.SentMessages.OfType<MovementSuggestionMessage>().ToList();

    private static void Occupy(CTCService service, params string[] blockIds)
    {
        foreach (var blockId in blockIds)
        {
            service.ApplyBlockStatus(new BlockStatusMessage { BlockId = blockId, Occupancy = OccupancyState.Occupied });
        }
    }

    // ---- 1, 2: defaults ----

    [TestMethod]
    public void DefaultDwell_StationBlock_Is45Seconds()
    {
        var service = CreateService();

        Assert.AreEqual(StationDwell, DwellTimePolicy.GetDefaultDwellTime(service.State.FindBlock(StationBlockId)!));
        Assert.AreEqual(StationDwell, Row(Template(service), StationBlockId).DefaultDwellTime);
    }

    [TestMethod]
    public void DefaultDwell_RegularBlock_IsZero()
    {
        var service = CreateService();

        Assert.AreEqual(TimeSpan.Zero, DwellTimePolicy.GetDefaultDwellTime(service.State.FindBlock("A4")!));
        Assert.AreEqual(TimeSpan.Zero, Row(Template(service), "A4").DefaultDwellTime);
    }

    [TestMethod]
    public void DefaultDwell_ComesFromStationName_NotBlockNumber()
    {
        Assert.AreEqual(StationDwell, DwellTimePolicy.GetDefaultDwellTime(new CtcBlockState { BlockId = "Z99", StationName = "Anywhere" }));
        Assert.AreEqual(TimeSpan.Zero, DwellTimePolicy.GetDefaultDwellTime(new CtcBlockState { BlockId = "A3", StationName = " " }));
    }

    [TestMethod]
    public void ScheduleBuilder_SelectingStationDestination_FillsDefaultDwell()
    {
        var template = Template(CreateService());

        Row(template, StationBlockId).SetTrainTime(0, "12:01:00");
        Row(template, "A4").SetTrainTime(0, "12:02:00");

        Assert.AreEqual("45", Row(template, StationBlockId).TrainDwellTimes[0]);
        Assert.AreEqual("0", Row(template, "A4").TrainDwellTimes[0]);
    }

    [TestMethod]
    public void Convert_BlankDwell_UsesBlockDefault()
    {
        var template = Template(CreateService());
        Row(template, "A1").TrainTimes[0] = "12:00:00";
        Row(template, StationBlockId).TrainTimes[0] = "12:01:00";
        Row(template, "A5").TrainTimes[0] = "12:05:00";

        var train = ScheduleTemplateConverter.Convert(template).Trains.Single();

        Assert.AreEqual(StationDwell, train.Route.Single(block => block.BlockId == StationBlockId).DwellTime);
        Assert.AreEqual(TimeSpan.Zero, train.Route.Single(block => block.BlockId == "A5").DwellTime);
        Assert.AreEqual(TimeSpan.Zero, train.Route[0].DwellTime);
    }

    // ---- 3, 4: overrides ----

    [TestMethod]
    public void ScheduleBuilder_OverrideStationDwell_IsKeptAndConverted()
    {
        var template = Template(CreateService());
        var station = Row(template, StationBlockId);
        Row(template, "A1").SetTrainTime(0, "12:00:00");
        station.SetTrainTime(0, "12:01:00");
        Row(template, "A5").SetTrainTime(0, "12:05:00");

        Assert.IsNull(station.TrySetTrainDwellTime(0, "60"));

        // Editing the time again must not put the station default back.
        station.SetTrainTime(0, "12:01:30");
        Assert.AreEqual("60", station.TrainDwellTimes[0]);

        var result = ScheduleTemplateConverter.Convert(template);
        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual(Seconds(60), result.Trains.Single().Route.Single(block => block.BlockId == StationBlockId).DwellTime);

        Assert.IsNull(station.TrySetTrainDwellTime(0, "30"));
        Assert.AreEqual(Seconds(30), ScheduleTemplateConverter.Convert(template).Trains.Single().Route.Single(block => block.BlockId == StationBlockId).DwellTime);
    }

    [TestMethod]
    public void ScheduleBuilder_RegularBlockDwell_CanBeAssigned()
    {
        var template = Template(CreateService());
        Row(template, "A1").SetTrainTime(0, "12:00:00");
        Row(template, "A4").SetTrainTime(0, "12:02:00");
        Row(template, "A5").SetTrainTime(0, "12:05:00");

        Assert.IsNull(Row(template, "A4").TrySetTrainDwellTime(0, "20"));

        var train = ScheduleTemplateConverter.Convert(template).Trains.Single();
        var a4 = train.Route.Single(block => block.BlockId == "A4");
        Assert.AreEqual(Seconds(20), a4.DwellTime);
        Assert.AreEqual(At(12, 2, 20), a4.DepartureTime);
        Assert.AreEqual(At(12, 2, 0), a4.ArrivalTime, "Dwell must not change the arrival at the same stop.");
    }

    [TestMethod]
    public async Task ActivePlan_OverrideStationDwell_IsStored()
    {
        var (service, _) = await DispatchAsync();

        await service.UpdateMovementPlanStopAsync("000", 0, StationBlockId, At(12, 1, 0), Seconds(60));

        Assert.AreEqual(Seconds(60), Train(service).Stops[0].DwellTime);
        Assert.AreEqual(At(12, 2, 0), Train(service).Stops[0].DepartureTime);
    }

    // ---- 5: changing destination resets dwell ----

    [TestMethod]
    public void ScheduleBuilder_ChangingDestination_ResetsDwellToNewBlockDefault()
    {
        var template = Template(CreateService());
        var regular = Row(template, "A4");
        var station = Row(template, StationBlockId);

        regular.SetTrainTime(0, "12:02:00");
        Assert.IsNull(regular.TrySetTrainDwellTime(0, "20"));

        // The dispatcher moves the destination from A4 to the station.
        regular.SetTrainTime(0, string.Empty);
        station.SetTrainTime(0, "12:02:00");

        Assert.AreEqual(string.Empty, regular.TrainDwellTimes[0]);
        Assert.AreEqual("45", station.TrainDwellTimes[0]);

        // ...and back to a regular block.
        station.SetTrainTime(0, string.Empty);
        regular.SetTrainTime(0, "12:02:00");
        Assert.AreEqual("0", regular.TrainDwellTimes[0]);
    }

    [TestMethod]
    public async Task ActivePlan_ChangingDestination_UsesNewBlockDefaultDwell()
    {
        var (service, _) = await DispatchAsync(a3Dwell: Seconds(60));

        await service.UpdateDispatchedTrainPlanAsync("000", "A4", At(12, 1, 30));
        Assert.AreEqual(TimeSpan.Zero, Train(service).Stops[0].DwellTime);

        await service.UpdateDispatchedTrainPlanAsync("000", StationBlockId, At(12, 1, 30));
        Assert.AreEqual(StationDwell, Train(service).Stops[0].DwellTime);
    }

    [TestMethod]
    public async Task ActivePlan_SameDestination_KeepsManualDwell()
    {
        var (service, _) = await DispatchAsync(a3Dwell: Seconds(60));

        await service.UpdateDispatchedTrainPlanAsync("000", StationBlockId, At(12, 1, 30));

        Assert.AreEqual(Seconds(60), Train(service).Stops[0].DwellTime);
    }

    // ---- 6: negative dwell ----

    [TestMethod]
    public void ScheduleBuilder_NegativeDwell_IsRejected()
    {
        var template = Template(CreateService());
        var station = Row(template, StationBlockId);
        station.SetTrainTime(0, "12:01:00");

        string? error = station.TrySetTrainDwellTime(0, "-5");

        Assert.AreEqual("Dwell time cannot be negative.", error);
        Assert.AreEqual("45", station.TrainDwellTimes[0]);
        Assert.IsNotNull(station.TrySetTrainDwellTime(0, "abc"));
        Assert.IsNull(station.TrySetTrainDwellTime(0, "0"), "A dwell of 0 is valid.");
        Assert.AreEqual("0", station.TrainDwellTimes[0]);
    }

    [TestMethod]
    public void Convert_NegativeDwell_IsRejected()
    {
        var template = Template(CreateService());
        Row(template, "A1").TrainTimes[0] = "12:00:00";
        Row(template, StationBlockId).TrainTimes[0] = "12:01:00";
        Row(template, StationBlockId).TrainDwellTimes[0] = "-1";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.Contains("cannot be negative", result.ErrorMessage!);
    }

    [TestMethod]
    public async Task ActivePlan_NegativeDwell_IsRejectedAndNothingChanges()
    {
        var (service, sender) = await DispatchAsync();

        var ex = await Assert.ThrowsExactlyAsync<DispatchException>(
            () => service.UpdateMovementPlanStopAsync("000", 0, StationBlockId, At(12, 1, 0), Seconds(-1)));

        Assert.Contains("cannot be negative", ex.Message);
        Assert.AreEqual(StationDwell, Train(service).Stops[0].DwellTime);
        Assert.HasCount(2, sender.SentMessages);
    }

    // ---- 7: dwell shortens the next movement ----

    [TestMethod]
    public void AvailableTravelTime_IsNextArrivalMinusPreviousDeparture()
    {
        // A at 10:00:00 dwelling 45 s, B at 10:05:00: only 4 min 15 s to travel.
        var a = new MovementPlanStop { BlockId = "A", ArrivalTime = At(10, 0, 0), DwellTime = StationDwell };

        Assert.AreEqual(At(10, 0, 45), a.DepartureTime);
        Assert.AreEqual(new TimeSpan(0, 4, 15), SpeedPlanner.AvailableTravelTime(a.DepartureTime, At(10, 5, 0)));
    }

    [TestMethod]
    public async Task PlannedSpeed_NextMovement_UsesDepartureNotArrival()
    {
        // A3 (12:01:00) + 45 s dwell -> A5 at 12:05:00: 100 m (A3, A4) in 195 s, not 240 s.
        var (service, _) = await DispatchAsync();

        var stops = Train(service).Stops;
        Assert.AreEqual("A5", stops[1].BlockId);
        Assert.AreEqual(100.0 / 195.0, stops[1].PlannedSpeedMetersPerSecond, 1e-9);
    }

    [TestMethod]
    public void Feasibility_DwellLeavesTooLittleTime_IsRejected()
    {
        // 100 m in 240 s is fine without dwell; a 235 s dwell leaves 5 s, needing 72 km/h on 50 km/h track.
        var service = CreateService();
        service.QueueSchedule([Schedule(TimeSpan.Zero, At(12, 5, 0))]);

        var ex = Assert.ThrowsExactly<ArgumentException>(() => service.QueueSchedule([Schedule(Seconds(235), At(12, 5, 0))]));

        Assert.Contains("cannot travel from A3 to A5", ex.Message);
    }

    // ---- 8, 9: changing dwell after dispatch ----

    [TestMethod]
    public async Task DwellChange_RecalculatesNextMovementSpeed()
    {
        var (service, _) = await DispatchAsync();

        await service.UpdateMovementPlanStopAsync("000", 0, StationBlockId, At(12, 1, 0), Seconds(120));

        // 100 m in 240 - 120 = 120 s.
        Assert.AreEqual(100.0 / 120.0, Train(service).Stops[1].PlannedSpeedMetersPerSecond, 1e-9);

        await service.UpdateMovementPlanStopAsync("000", 0, StationBlockId, At(12, 1, 0), TimeSpan.Zero);

        Assert.AreEqual(100.0 / 240.0, Train(service).Stops[1].PlannedSpeedMetersPerSecond, 1e-9);
    }

    [TestMethod]
    public async Task DwellChange_UpdatesPlanWithoutRouteAuthorityOrResend()
    {
        var (service, sender) = await DispatchAsync();
        var train = Train(service);
        var routeBefore = train.ActiveRouteBlockIds.ToArray();
        double authorityBefore = train.SuggestedAuthorityMeters;
        double speedBefore = train.SuggestedSpeedMetersPerSecond;
        var events = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => events.Add(e);

        string? warning = await service.UpdateMovementPlanStopAsync("000", 0, StationBlockId, At(12, 1, 0), Seconds(60));

        Assert.IsNull(warning);
        Assert.AreEqual(Seconds(60), train.Stops[0].DwellTime);
        Assert.AreEqual(At(12, 1, 0), train.Stops[0].ArrivalTime, "Dwell must not move the arrival.");
        Assert.AreEqual(At(12, 2, 0), train.Stops[0].DepartureTime);
        CollectionAssert.AreEqual(routeBefore, train.ActiveRouteBlockIds.ToArray());
        Assert.AreEqual(authorityBefore, train.SuggestedAuthorityMeters);
        Assert.AreEqual(speedBefore, train.SuggestedSpeedMetersPerSecond);
        Assert.HasCount(2, sender.SentMessages, "A dwell change alone does not change the movement under way.");
        Assert.AreEqual(CtcStateChangeKind.TrainMovementPlanUpdated, events.Single().Kind);
    }

    [TestMethod]
    public async Task DwellChange_AtLaterStop_AffectsOnlyTheFollowingMovement()
    {
        // Stops: A3, A5, B10. A 60 s dwell at A5 leaves the movement to B10 250 m (A5..B9) in 12:10 - 12:06 = 240 s.
        var (service, _) = await DispatchAsync();
        double a5Speed = Train(service).Stops[1].PlannedSpeedMetersPerSecond;

        await service.UpdateMovementPlanStopAsync("000", 1, "A5", At(12, 5, 0), Seconds(60));

        var stops = Train(service).Stops;
        Assert.AreEqual(a5Speed, stops[1].PlannedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(250.0 / 240.0, stops[2].PlannedSpeedMetersPerSecond, 1e-9);
    }

    // ---- 10: impossible dwell ----

    [TestMethod]
    public async Task DwellPastNextArrival_IsRejectedAndPlanUnchanged()
    {
        // A3 at 12:01:00 + 5 min dwell = 12:06:00, but A5 is at 12:05:00.
        var (service, sender) = await DispatchAsync();

        var ex = await Assert.ThrowsExactlyAsync<DispatchException>(
            () => service.UpdateMovementPlanStopAsync("000", 0, StationBlockId, At(12, 1, 0), TimeSpan.FromMinutes(5)));

        Assert.AreEqual(
            "Train 000 dwells 300 s at A3 and does not depart until 12:06:00, so it cannot enter A5 at 12:05:00. Shorten the dwell or make the arrival later.",
            ex.Message);
        Assert.AreEqual(StationDwell, Train(service).Stops[0].DwellTime);
        Assert.HasCount(2, sender.SentMessages);
    }

    [TestMethod]
    public void Schedule_DwellPastNextArrival_IsRejected()
    {
        var service = CreateService();

        var ex = Assert.ThrowsExactly<ArgumentException>(() => service.QueueSchedule([Schedule(TimeSpan.FromMinutes(5), At(12, 5, 0))]));

        Assert.Contains("does not depart until 12:06:00", ex.Message);
        Assert.IsEmpty(service.State.DispatchQueue);
    }

    [TestMethod]
    public void Schedule_DwellEndingExactlyAtNextArrival_IsRejected()
    {
        var service = CreateService();

        Assert.ThrowsExactly<ArgumentException>(() => service.QueueSchedule([Schedule(TimeSpan.FromMinutes(4), At(12, 5, 0))]));
    }

    // ---- 11: CSV ----

    private const string CsvWithoutDwell =
        "Line,Section,Block Number,Train 1\n"
        + "Blue,A,1,12:00:00\n"
        + "Blue,A,3,12:01:00\n"
        + "Blue,A,5,12:05:00\n";

    [TestMethod]
    public void CsvImport_WithoutDwellColumn_StillWorksWithDefaults()
    {
        var service = CreateService();

        var import = ScheduleCsvImporter.Import(Line(service), CsvWithoutDwell);
        Assert.IsTrue(import.IsSuccess, import.ErrorMessage);
        var convert = ScheduleTemplateConverter.Convert(import.Template!);
        Assert.IsTrue(convert.IsSuccess, convert.ErrorMessage);

        var route = convert.Trains.Single().Route;
        Assert.AreEqual(StationDwell, route.Single(block => block.BlockId == StationBlockId).DwellTime);
        Assert.AreEqual(TimeSpan.Zero, route.Single(block => block.BlockId == "A5").DwellTime);
        service.QueueSchedule(convert.Trains);
    }

    [TestMethod]
    public void CsvImport_DwellColumn_AppliesToTimedTrainsAndBlankKeepsDefault()
    {
        const string csv =
            "Line,Section,Block Number,DwellTimeSeconds,Train 1,Train 2\n"
            + "Blue,A,1,,12:00:00,12:10:00\n"
            + "Blue,A,3,,12:01:00,12:11:00\n"
            + "Blue,A,4,20,12:02:00,\n"
            + "Blue,A,5,,12:05:00,12:15:00\n";
        var service = CreateService();

        var import = ScheduleCsvImporter.Import(Line(service), csv);
        Assert.IsTrue(import.IsSuccess, import.ErrorMessage);
        Assert.HasCount(2, import.Template!.TrainIds, "The dwell column is not a train.");
        var trains = ScheduleTemplateConverter.Convert(import.Template!).Trains;

        Assert.AreEqual(Seconds(20), trains[0].Route.Single(block => block.BlockId == "A4").DwellTime);
        Assert.AreEqual(StationDwell, trains[0].Route.Single(block => block.BlockId == StationBlockId).DwellTime);
        Assert.AreEqual(StationDwell, trains[1].Route.Single(block => block.BlockId == StationBlockId).DwellTime);
    }

    [TestMethod]
    public void CsvImport_NegativeDwell_IsRejected()
    {
        const string csv =
            "Line,Section,Block Number,Train 1,DwellTimeSeconds\n"
            + "Blue,A,1,12:00:00,\n"
            + "Blue,A,3,12:01:00,-10\n";

        var import = ScheduleCsvImporter.Import(Line(CreateService()), csv);

        Assert.IsFalse(import.IsSuccess);
        Assert.AreEqual("Row 3: Dwell time cannot be negative.", import.ErrorMessage);
    }

    // ---- Runtime: Arrived -> Dwelling -> EnRoute ----

    [TestMethod]
    public async Task ArrivingAtStationStop_DwellsThenDepartsWithSpeedFromDeparture()
    {
        var (service, sender) = await DispatchAsync();
        var train = Train(service);
        Occupy(service, "A1", "A2");
        await service.SetSystemTimeAsync(At(12, 1, 0));
        Occupy(service, "A3");

        Assert.AreEqual(TrainMovementPhase.Dwelling, train.Phase);
        Assert.AreEqual(At(12, 1, 45), train.ReadyToDepartTime);
        Assert.AreEqual(StationBlockId, train.TargetBlockId);

        // Still dwelling: nothing is sent.
        await service.SetSystemTimeAsync(At(12, 1, 44));
        Assert.AreEqual(TrainMovementPhase.Dwelling, train.Phase);
        Assert.HasCount(1, Suggestions(sender));

        // Dwell over: departs toward A5 with 100 m in 12:05:00 - 12:01:45 = 195 s.
        await service.SetSystemTimeAsync(At(12, 1, 45));
        Assert.AreEqual(TrainMovementPhase.EnRoute, train.Phase);
        Assert.AreEqual("A5", train.TargetBlockId);
        Assert.HasCount(2, Suggestions(sender));
        Assert.AreEqual(100.0 / 195.0, Suggestions(sender)[^1].SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(Suggestions(sender)[^1].SuggestedSpeedMetersPerSecond, train.SuggestedSpeedMetersPerSecond);
        Assert.HasCount(1, sender.SentMessages.OfType<MovementRequestMessage>());
    }

    [TestMethod]
    public async Task DwellExtendedWhileDwelling_DelaysDeparture()
    {
        var (service, sender) = await DispatchAsync();
        Occupy(service, "A1", "A2");
        await service.SetSystemTimeAsync(At(12, 1, 0));
        Occupy(service, "A3");

        await service.UpdateMovementPlanStopAsync("000", 0, StationBlockId, At(12, 1, 0), Seconds(90));
        await service.SetSystemTimeAsync(At(12, 1, 45));

        Assert.AreEqual(TrainMovementPhase.Dwelling, Train(service).Phase);
        Assert.AreEqual(At(12, 2, 30), Train(service).ReadyToDepartTime);
        Assert.HasCount(1, Suggestions(sender));

        await service.SetSystemTimeAsync(At(12, 2, 30));
        Assert.AreEqual(TrainMovementPhase.EnRoute, Train(service).Phase);
        Assert.AreEqual(100.0 / 150.0, Suggestions(sender)[^1].SuggestedSpeedMetersPerSecond, 1e-9);
    }

    [TestMethod]
    public async Task ZeroDwellStop_IsArrivedAndProceedsOnNextTimeUpdate()
    {
        var (service, sender) = await DispatchAsync(a3Dwell: TimeSpan.Zero);
        Occupy(service, "A1", "A2", "A3");

        Assert.AreEqual(TrainMovementPhase.Arrived, Train(service).Phase);

        await service.SetSystemTimeAsync(At(12, 1, 0));

        Assert.AreEqual(TrainMovementPhase.EnRoute, Train(service).Phase);
        Assert.AreEqual("A5", Train(service).TargetBlockId);
        Assert.HasCount(2, Suggestions(sender));
    }

    [TestMethod]
    public async Task FinalStop_DwellsThenCompletes()
    {
        var (service, _) = await DispatchAsync();
        var train = Train(service);
        await service.UpdateMovementPlanStopAsync("000", 2, "B10", At(12, 10, 0), Seconds(30));
        Occupy(service, "A1", "A2", "A3");
        await service.SetSystemTimeAsync(At(12, 2, 0));
        Occupy(service, "A4", "A5");
        await service.SetSystemTimeAsync(At(12, 5, 0));
        await service.SetSystemTimeAsync(At(12, 9, 0));
        Occupy(service, "B6", "B7", "B8", "B9", "B10");

        Assert.AreEqual(TrainMovementPhase.Dwelling, train.Phase);

        await service.SetSystemTimeAsync(At(12, 9, 30));

        Assert.AreEqual(TrainMovementPhase.Completed, train.Phase);
        Assert.AreEqual("B10", train.TargetBlockId);
    }

    [TestMethod]
    public async Task DepartureSendFailure_StaysReadyAndRetries()
    {
        var (service, sender) = await DispatchAsync(a3Dwell: TimeSpan.Zero);
        var events = new List<CtcStateChangeKind>();
        service.StateChanged += (_, e) => events.Add(e.Kind);
        Occupy(service, "A1", "A2", "A3");
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");

        await service.SetSystemTimeAsync(At(12, 1, 0));

        Assert.AreEqual(TrainMovementPhase.Arrived, Train(service).Phase);
        Assert.AreEqual(StationBlockId, Train(service).TargetBlockId);
        Assert.Contains(CtcStateChangeKind.TrainMovementSuggestionFailed, events);

        sender.ExceptionToThrow = null;
        await service.SetSystemTimeAsync(At(12, 1, 1));

        Assert.AreEqual(TrainMovementPhase.EnRoute, Train(service).Phase);
        Assert.AreEqual("A5", Train(service).TargetBlockId);
    }
}
