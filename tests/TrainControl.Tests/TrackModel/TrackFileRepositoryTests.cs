using System.Text.Json;
using TrackModel.Core.Models;
using TrackModel.Core.Persistence;
using TrackModel.Core.Services;

namespace TrainControl.Tests.TrackModel;

[TestClass]
public class TrackFileRepositoryTests
{
    [TestMethod]
    public void Json_RoundTripPreservesTopologyAndExcludesRuntimeState()
    {
        var layout = SampleTrackLayout.Create();
        layout.Blocks[0].IsOccupied = true;
        layout.Blocks[0].BrokenRail = true;
        WithFile(".json", JsonSerializer.Serialize(layout, new JsonSerializerOptions(JsonSerializerDefaults.Web)), path =>
        {
            var result = new TrackFileRepository().Load(path);
            Assert.HasCount(36, result.Blocks);
            var originalSwitch = result.Blocks.Single(b => b.Id == "103");
            Assert.AreEqual("104", originalSwitch.NormalNextBlockId);
            Assert.AreEqual("121", originalSwitch.ReverseNextBlockId);
            Assert.IsFalse(result.Blocks[0].IsOccupied);
            Assert.IsFalse(result.Blocks[0].BrokenRail);
        });
    }

    [TestMethod]
    public void Csv_ImportsQuotedStationNamesAndSemicolonConnections()
    {
        const string csv = "Id,LineId,LengthMeters,StationName,InitialWaitingPassengers,ConnectedBlockIds\n" +
            "B1,Blue,100,\"Station, North\",12,B2\nB2,Blue,120,,0,B1\n";
        WithFile(".csv", csv, path =>
        {
            var result = new TrackFileRepository().Load(path);
            Assert.HasCount(2, result.Blocks);
            Assert.AreEqual("Station, North", result.Blocks[0].StationName);
            Assert.AreEqual("B2", result.Blocks[0].ConnectedBlockIds.Single());
            var service = new TrackService(); service.LoadLayout(result);
            Assert.AreEqual(12, service.CreateTrainEnvironment("B1").WaitingPassengers);
        });
    }

    [TestMethod]
    public void InvalidJson_RejectsMissingBlockConnection()
    {
        const string json = "{\"name\":\"Bad track\",\"blocks\":[{\"id\":\"B1\",\"lineId\":\"Blue\",\"lengthMeters\":100,\"connectedBlockIds\":[\"missing\"]}]}";
        WithFile(".json", json, path => Assert.Throws<ArgumentException>(() => new TrackFileRepository().Load(path)));
    }

    [TestMethod]
    public void InvalidCsv_RejectsDuplicateHeaders()
    {
        WithFile(".csv", "Id,id,LineId,LengthMeters\nB1,B1,Blue,100\n",
            path => Assert.Throws<InvalidDataException>(() => new TrackFileRepository().Load(path)));
    }

    [TestMethod]
    public void NullStationMetadata_RejectsImportBeforeReplacingLiveState()
    {
        const string json = "{\"name\":\"Bad metadata\",\"blocks\":[{\"id\":\"B1\",\"lineId\":\"Blue\",\"lengthMeters\":100,\"stationName\":null}]}";
        WithFile(".json", json, path => Assert.Throws<ArgumentException>(() => new TrackFileRepository().Load(path)));
    }

    private static void WithFile(string extension, string contents, Action<string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "track-model-" + Guid.NewGuid().ToString("N") + extension);
        try { File.WriteAllText(path, contents); check(path); }
        finally { File.Delete(path); }
    }
}
