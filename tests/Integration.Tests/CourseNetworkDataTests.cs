using System.Text.Json;
using CTC.Core.Services;
using CtcFixture = CTC.TestUI.Wpf.SampleData.SampleTrackLayout;
using TrackController.Contracts.Messages;
using TrackController.Core.Models;
using TrackController.Runtime;
using TrackModel.Core.Models;
using TrackModel.Core.Services;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The three-line course network is one set of data across modules: the Track Model's layout is
/// the authority, the Track Controller's maps and ownership derive from it, and CTC loads it
/// as published. These checks need no running modules.
/// </summary>
[TestClass]
public sealed class CourseNetworkDataTests
{
    private static readonly TrackLayout Course = CourseTrackLayout.Create();

    private static string CourseDeployment => Path.Combine(LiveSystem.SamplesFolder, "course-all-pc.deployment.json");

    [TestMethod]
    public void TrackModelCourseLayout_HasAllThreeLinesWithUniqueIds()
    {
        var counts = Course.Blocks.GroupBy(block => block.LineId).ToDictionary(line => line.Key, line => line.Count());
        CollectionAssert.AreEqual(new[] { "Blue", "Red", "Green" }, counts.Keys.ToArray());
        Assert.AreEqual(15, counts["Blue"]);
        Assert.AreEqual(76, counts["Red"]);
        Assert.AreEqual(150, counts["Green"]);
        Assert.HasCount(241, Course.Blocks.Select(block => block.Id).Distinct(StringComparer.Ordinal).ToArray(), "Block IDs are unique across the whole network.");

        // Blue keeps its production IDs and data; Red and Green are qualified at the source.
        Assert.AreEqual(JsonSerializer.Serialize(BlueLineTrackLayout.Create().Blocks), JsonSerializer.Serialize(Course.Blocks.Where(block => block.LineId == "Blue").ToList()));
        foreach (var block in Course.Blocks.Where(block => block.LineId != "Blue"))
        {
            Assert.AreEqual($"{block.LineId.ToUpperInvariant()}-{block.Number}", block.Id, "Section and number stay separate fields; the ID is line + number.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(block.Section));
        }

        // Switch blocks are where both workbook legs meet, with the first pair Normal.
        var switches = Course.Blocks.Where(block => block.HasSwitch).Select(block => $"{block.Id}:{block.NormalNextBlockId}/{block.ReverseNextBlockId}").ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "5:6/11",
            "RED-16:RED-15/RED-1", "RED-27:RED-28/RED-76", "RED-33:RED-32/RED-72", "RED-38:RED-39/RED-71", "RED-44:RED-43/RED-67", "RED-52:RED-53/RED-66",
            "GREEN-13:GREEN-12/GREEN-1", "GREEN-28:GREEN-29/GREEN-150", "GREEN-77:GREEN-76/GREEN-101", "GREEN-85:GREEN-86/GREEN-100",
        }, switches);
        CollectionAssert.AreEqual(new[] { "3", "RED-11", "RED-47", "GREEN-19", "GREEN-108" }, Course.Blocks.Where(block => block.HasCrossing).Select(block => block.Id).ToArray());
        Assert.HasCount(2 + 8 + 18, Course.Blocks.Where(block => block.StationName.Length > 0).ToArray());
    }

    /// <summary>
    /// The CTC TestUI fixture was generated from the same workbook independently. Every physical
    /// attribute and every connection agree; the only differences are the documented conversion
    /// choices (switch placed on its common block, yard switches not modelled, unnamed station).
    /// </summary>
    [TestMethod]
    public void TrackModelCourseLayout_AgreesWithTheIndependentCtcFixture()
    {
        var fixture = CtcFixture.Create();
        var known = new HashSet<string>
        {
            "Red 9 switch", "Red 15 switch", "Red 16 switch", "Red 32 switch", "Red 33 switch", "Red 43 switch", "Red 44 switch",
            "Green 12 switch", "Green 13 switch", "Green 58 switch", "Green 62 switch", "Green 76 switch", "Green 77 switch", "Green 16 station",
        };
        var differences = new List<string>();
        foreach (var line in fixture.Lines.Where(line => line.LineId != "BLUE"))
        {
            string lineId = line.LineId[0] + line.LineId[1..].ToLowerInvariant();
            var ids = line.Blocks.ToDictionary(block => block.BlockId, block => $"{line.LineId}-{block.BlockNumber}");
            foreach (var expected in line.Blocks)
            {
                var actual = Course.Blocks.Single(block => block.LineId == lineId && block.Number == expected.BlockNumber);
                Assert.AreEqual(expected.Section, actual.Section);
                Assert.AreEqual(expected.LengthMeters, actual.LengthMeters, 1e-9);
                Assert.AreEqual(expected.GradePercent, actual.GradePercent, 1e-9);
                Assert.AreEqual(expected.ElevationMeters, actual.ElevationMeters, 1e-9);
                Assert.AreEqual(expected.SpeedLimitKilometersPerHour, actual.SpeedLimitMetersPerSecond * 3.6, 1e-9);
                Assert.AreEqual(expected.HasCrossing, actual.HasCrossing, actual.Id);
                Assert.AreEqual(expected.HasSignal, actual.HasSignal, actual.Id);
                CollectionAssert.AreEquivalent(expected.ConnectedBlockIds.Select(id => ids[id]).ToArray(), actual.ConnectedBlockIds.ToArray(), actual.Id);
                if (expected.HasSwitch != actual.HasSwitch) differences.Add($"{lineId} {actual.Number} switch");
                if (expected.StationName != actual.StationName) differences.Add($"{lineId} {actual.Number} station");
            }
        }

        CollectionAssert.AreEquivalent(known.ToArray(), differences.ToArray(), string.Join(", ", differences));
    }

    [TestMethod]
    public void TrackControllerRedAndGreenMaps_AreWhatTheTrackModelLayoutDerives()
    {
        bool regenerate = Environment.GetEnvironmentVariable("TRAINCONTROL_REGENERATE_TRACK_MAPS") == "1";
        foreach (string line in new[] { "Red", "Green" })
        {
            string map = JsonSerializer.Serialize(CourseTrackMaps.BuildMap(Course, line), CourseTrackMaps.Json);
            string territories = JsonSerializer.Serialize(CourseTrackMaps.BuildSingleTerritory(Course, line), CourseTrackMaps.Json);
            if (regenerate)
            {
                File.WriteAllText(Path.Combine(CourseTrackMaps.SourceSamplesFolder(), CourseTrackMaps.MapFile(line)), map + Environment.NewLine);
                File.WriteAllText(Path.Combine(CourseTrackMaps.SourceSamplesFolder(), CourseTrackMaps.TerritoriesFile(line)), territories + Environment.NewLine);
                continue;
            }

            Assert.AreEqual(map, CourseTrackMaps.Canonical<WaysideConfigurationMessage>(File.ReadAllText(Path.Combine(LiveSystem.SamplesFolder, CourseTrackMaps.MapFile(line)))),
                $"{CourseTrackMaps.MapFile(line)} no longer matches the Track Model layout; regenerate it (see CourseTrackMaps).");
            Assert.AreEqual(territories, CourseTrackMaps.Canonical<TerritoryDefinitions>(File.ReadAllText(Path.Combine(LiveSystem.SamplesFolder, CourseTrackMaps.TerritoriesFile(line)))));
        }

        if (regenerate)
        {
            Assert.Inconclusive("Track maps regenerated in the source tree; rebuild and run again without TRAINCONTROL_REGENERATE_TRACK_MAPS.");
        }
    }

    /// <summary>The Green map enforces the course's one-way sections; Red runs both ways.</summary>
    [TestMethod]
    public void TrackControllerMaps_FollowTheCourseDirectionsOfTravel()
    {
        var green = CourseTrackMaps.BuildMap(Course, "Green").Blocks.ToDictionary(block => block.BlockId);
        bool Permits(string from, string to) => green[from].NextBlockIds.Contains(to);

        Assert.IsTrue(Permits("GREEN-62", "GREEN-63") && !Permits("GREEN-63", "GREEN-62"), "K-M runs in increasing block order.");
        Assert.IsTrue(Permits("GREEN-13", "GREEN-12") && Permits("GREEN-2", "GREEN-1") && Permits("GREEN-1", "GREEN-13"), "A-C runs toward block 1, then back to D.");
        Assert.IsFalse(Permits("GREEN-12", "GREEN-13") || Permits("GREEN-1", "GREEN-2"));
        Assert.IsTrue(Permits("GREEN-14", "GREEN-15") && Permits("GREEN-15", "GREEN-14"), "D-F is bidirectional.");
        Assert.IsTrue(Permits("GREEN-100", "GREEN-85") && !Permits("GREEN-99", "GREEN-98"), "O-Q loops back to N at 85.");
        Assert.IsTrue(Permits("GREEN-77", "GREEN-101") && Permits("GREEN-150", "GREEN-28") && !Permits("GREEN-101", "GREEN-77"));

        var red = CourseTrackMaps.BuildMap(Course, "Red").Blocks;
        Assert.IsTrue(red.All(block => block.NextBlockIds.Count == Course.Blocks.Single(b => b.Id == block.BlockId).ConnectedBlockIds.Count), "Every Red connection runs both ways.");
    }

    /// <summary>The Blue map predates this conversion and is stricter (1 toward 15 only), but uses exactly the Track Model's blocks.</summary>
    [TestMethod]
    public void TrackControllerBlueMap_UsesTheTrackModelBlocks()
    {
        var map = JsonSerializer.Deserialize<WaysideConfigurationMessage>(File.ReadAllText(Path.Combine(LiveSystem.SamplesFolder, "blue-line-track-map.json")), CourseTrackMaps.Json)!;
        var blue = Course.Blocks.Where(block => block.LineId == "Blue").ToDictionary(block => block.Id);
        CollectionAssert.AreEquivalent(blue.Keys.ToArray(), map.Blocks.Select(block => block.BlockId).ToArray());
        foreach (var block in map.Blocks)
        {
            var track = blue[block.BlockId];
            Assert.AreEqual(track.LengthMeters, block.LengthMeters, 1e-9);
            Assert.AreEqual(track.SpeedLimitMetersPerSecond, block.SpeedLimitMetersPerSecond, 1e-9);
            Assert.IsTrue(block.NextBlockIds.All(track.ConnectedBlockIds.Contains), block.BlockId);
            Assert.AreEqual(track.HasSwitch, block.NormalNextBlockId is not null);
            Assert.AreEqual(track.HasSignal, block.HasSignal);
            Assert.AreEqual(track.HasCrossing, block.HasCrossing);
        }
    }

    [TestMethod]
    public async Task CombinedDeployment_OwnsEveryTrackModelBlockExactlyOnce()
    {
        var plan = WaysideDeployment.Read(CourseDeployment);
        CollectionAssert.AreEqual(new[] { "wayside-blue", "wayside-red", "wayside-green" }, plan.Territories.Select(territory => territory.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "Blue Line", "Red Line", "Green Line" }, plan.Territories.Select(territory => territory.Configuration.LineName).ToArray());
        var owned = plan.Territories.SelectMany(territory => WaysideConfiguration.GetOwnedIds(territory.Configuration)).ToArray();
        Assert.HasCount(241, owned);
        CollectionAssert.AreEquivalent(Course.Blocks.Select(block => block.Id).ToArray(), owned, "Track Model and Track Controller use exactly the same block IDs.");
        foreach (var territory in plan.Territories)
        {
            Assert.IsEmpty(WaysideConfiguration.GetNeighborIds(territory.Configuration), "One territory per line: no boundaries inside a line.");
        }

        await using var router = plan.CreateRuntime();
        var snapshot = await router.PollAsync();
        Assert.HasCount(241, snapshot.Blocks);
        Assert.AreEqual("Blue Line / Red Line / Green Line", snapshot.LineName);
        StringAssert.Contains(router.GetOwnerDescription("GREEN-150"), "Green Line");
        StringAssert.Contains(router.GetOwnerDescription("1"), "Blue Line");
    }

    /// <summary>One layout message carries all three lines, and CTC loads it verbatim; an equivalent repeat changes nothing.</summary>
    [TestMethod]
    public void TrackModelLayoutMessage_LoadsAllThreeLinesIntoCtc()
    {
        var track = new TrackService();
        CourseTrackLayout.Load(track);
        var message = track.CreateLayoutMessage();
        CollectionAssert.AreEqual(new[] { "Blue Line", "Red Line", "Green Line" }, message.Lines.Select(line => line.Name).ToArray());

        var ctc = new CTCService(new NullSender());
        var first = ctc.ApplyTrackLayout(message);
        CollectionAssert.AreEqual(new[] { "Blue", "Red", "Green" }, first.AddedLineIds.ToArray());
        Assert.AreEqual(241, ctc.State.Lines.Sum(line => line.Blocks.Count));
        Assert.AreEqual("Red Line", ctc.State.FindLine("Red")!.Name);
        Assert.AreEqual(55, ctc.State.FindBlock("RED-52")!.SpeedLimitKilometersPerHour, 1e-9);
        Assert.IsTrue(ctc.State.FindBlock("GREEN-77")!.HasSwitch);

        var repeat = ctc.ApplyTrackLayout(track.CreateLayoutMessage());
        Assert.IsFalse(repeat.Changed);
        CollectionAssert.AreEqual(new[] { "Blue", "Red", "Green" }, repeat.UnchangedLineIds.ToArray());
    }

    private sealed class NullSender : CTC.Core.Interfaces.IMessageSender
    {
        public Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : class => Task.CompletedTask;
    }
}
