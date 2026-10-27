using System.Text.Json;
using TrackModel.Core.Models;
using TrackModel.Core.Persistence;
using TrainControl.Contracts.Messages;

namespace TrackModel.Core.Services;

/// <summary>Iteration 2 course Blue Line. Static data is bundled; test populations are explicit simulation defaults.</summary>
public static class BlueLineTrackLayout
{
    public static TrackLayout Create()
    {
        using var stream = typeof(BlueLineTrackLayout).Assembly.GetManifestResourceStream("TrackModel.BlueLine.json")
            ?? throw new InvalidOperationException("Bundled Blue Line layout is missing.");
        var layout = JsonSerializer.Deserialize<TrackLayout>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        TrackLayoutValidator.Validate(layout);
        return layout;
    }

    public static void LoadDemonstration(TrackService service)
    {
        service.SetSystemTime(new TimeSpan(9, 0, 0));
        service.LoadLayout(Create());
        // Station demand is seeded in the JSON. No tickets, faults, or movement are pre-applied.
        service.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = "01", CurrentBlockId = "1" });
    }
}
