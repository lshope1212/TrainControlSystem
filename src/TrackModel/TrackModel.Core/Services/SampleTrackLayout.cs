using TrackModel.Core.Models;
using TrainControl.Contracts.Messages;
namespace TrackModel.Core.Services;

/// <summary>Demonstration data based on the supplied dashboard; not a surveyed track.</summary>
public static class SampleTrackLayout
{
    public static TrackLayout Create()
    {
        var layout = new TrackLayout { Name = "Track Model demonstration" };
        for (var n = 101; n <= 128; n++)
            layout.Blocks.Add(new TrackBlock { Id = n.ToString(), Number = n, LineId = "Blue",
                Section = n <= 110 ? "Main" : n <= 120 ? "Return" : n <= 125 ? "Bypass" : "Yard",
                LengthMeters = 121.92, ElevationMeters = 219.456, GradePercent = 1.5,
                HasSignal = true, HasSwitch = n is 103 or 110 or 126, HasCrossing = n == 109,
                StationName = n == 104 ? "Station A" : n == 108 ? "Station B" : n == 115 ? "Station C" : "",
                InitialWaitingPassengers = n == 104 ? 24 : n is 108 or 115 ? 12 : 0 });
        var main = Enumerable.Range(101, 20).Select(n => n.ToString()).ToArray();
        for (var i = 0; i < main.Length; i++) Connect(layout, main[i], main[(i + 1) % main.Length]);
        Connect(layout, "103", "121");
        for (var n = 121; n < 125; n++) Connect(layout, n.ToString(), (n + 1).ToString());
        Connect(layout, "125", "110");
        Connect(layout, "101", "126"); Connect(layout, "126", "127"); Connect(layout, "126", "128");
        ConfigureSwitch(layout, "103", "104", "121");
        ConfigureSwitch(layout, "110", "111", "125");
        ConfigureSwitch(layout, "126", "127", "128");
        for (var n = 1; n <= 8; n++)
            layout.Blocks.Add(new TrackBlock { Id = "G" + n, Number = n, LineId = "Green", LengthMeters = 100,
                HasSignal = true, StationName = n == 4 ? "Park Station" : "", InitialWaitingPassengers = n == 4 ? 30 : 0 });
        for (var n = 1; n <= 8; n++) Connect(layout, "G" + n, "G" + (n % 8 + 1));
        return layout;
    }

    public static void LoadDemo(TrackService service)
    {
        service.SetSystemTime(new TimeSpan(9, 42, 18));
        service.LoadLayout(Create());
        foreach (var b in service.Layout.Blocks)
            service.ApplyCommand(new TrackModelCommandMessage { BlockId = b.Id, CommandedSpeedMetersPerSecond = 11.176, AuthorityMeters = 365.76 });
        service.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = "01", CurrentBlockId = "104",
            ActualSpeedMetersPerSecond = 9.83488, BoardingPassengers = 12, DisembarkingPassengers = 8, ExchangeId = "demo" });
        service.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = "02", CurrentBlockId = "116", ActualSpeedMetersPerSecond = 9.83488 });
        service.ApplyFailures(new TrackModelFailureCommandMessage { BlockId = "119", TrackCircuitFailure = true });
    }

    private static void Connect(TrackLayout layout, string a, string b)
    {
        layout.Blocks.First(x => x.Id == a).ConnectedBlockIds.Add(b);
        layout.Blocks.First(x => x.Id == b).ConnectedBlockIds.Add(a);
    }
    private static void ConfigureSwitch(TrackLayout layout, string id, string normal, string reverse)
    {
        var b = layout.Blocks.First(x => x.Id == id);
        b.NormalNextBlockId = normal; b.ReverseNextBlockId = reverse;
    }
}
