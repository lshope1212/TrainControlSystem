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

    /// <summary>
    /// Canonical ID of the sample train the standalone demonstration places on Blue block 1.
    /// It is a real fleet ID, so a demonstration seed must never be loaded in the integrated
    /// system, where CTC allocates that same train.
    /// </summary>
    public const string DemonstrationTrainId = "001";

    /// <summary>Blue block where <see cref="LoadDemonstration"/> places <see cref="DemonstrationTrainId"/>.</summary>
    public const string DemonstrationTrainBlockId = "1";

    /// <summary>
    /// The Blue Line on its own at 09:00 with no trains: trains appear only when the Train Model
    /// reports them, so no fleet ID is occupied before CTC schedules and dispatches it.
    /// </summary>
    public static void Load(TrackService service)
    {
        service.SetSystemTime(new TimeSpan(9, 0, 0));
        service.LoadLayout(Create());
        // Station demand is seeded in the JSON. No trains, tickets, faults, or movement are pre-applied.
    }

    /// <summary>
    /// Standalone demonstration/test start-up only: <see cref="Load"/>, then the stopped sample
    /// train <see cref="DemonstrationTrainId"/> in block <see cref="DemonstrationTrainBlockId"/>.
    /// Not used by the production Track Model.
    /// </summary>
    public static void LoadDemonstration(TrackService service)
    {
        Load(service);
        SeedDemonstrationTrain(service);
    }

    /// <summary>Places the stopped sample train <see cref="DemonstrationTrainId"/> in Blue block <see cref="DemonstrationTrainBlockId"/>.</summary>
    internal static void SeedDemonstrationTrain(TrackService service) =>
        service.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = DemonstrationTrainId, CurrentBlockId = DemonstrationTrainBlockId });
}
