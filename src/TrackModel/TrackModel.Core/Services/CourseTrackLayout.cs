using System.Text.Json;
using TrackModel.Core.Models;
using TrackModel.Core.Persistence;
using TrainControl.Contracts.Messages;

namespace TrackModel.Core.Services;

/// <summary>
/// The complete course network the production Track Model runs: the Blue (15 blocks), Red (76)
/// and Green (150) lines of the course workbook "Track Layout &amp; Vehicle Data vF5", loaded
/// together. Static data is bundled; test populations are explicit simulation defaults.
/// </summary>
/// <remarks>
/// <para>
/// <c>SampleLayouts/course-lines.json</c> is the workbook imported by this Track Model's own XLSX
/// importer (<see cref="TrackFileRepository"/>) and exported as JSON, so nothing reads Excel at
/// run time. Regenerate it the same way: Track Model, Import the vF5 workbook, Export. The Blue
/// Line inside it is identical to <see cref="BlueLineTrackLayout"/> (IDs 1–15); Red and Green IDs
/// are <c>RED-n</c> and <c>GREEN-n</c>. See the importer for the conversion rules and the data the
/// workbook does not contain.
/// </para>
/// </remarks>
public static class CourseTrackLayout
{
    public static IReadOnlyList<string> LineIds { get; } = ["Blue", "Red", "Green"];

    public static TrackLayout Create()
    {
        using var stream = typeof(CourseTrackLayout).Assembly.GetManifestResourceStream("TrackModel.CourseLines.json")
            ?? throw new InvalidOperationException("Bundled course layout is missing.");
        var layout = JsonSerializer.Deserialize<TrackLayout>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        TrackLayoutValidator.Validate(layout);
        return layout;
    }

    /// <summary>
    /// The production start-up state: all three lines, simulation time 09:00 and no trains.
    /// Every line starts clear; a train appears only when the Train Model reports it, so no fleet
    /// ID (001-010) is occupied before CTC has scheduled and dispatched that train.
    /// </summary>
    public static void Load(TrackService service)
    {
        service.SetSystemTime(new TimeSpan(9, 0, 0));
        service.LoadLayout(Create());
    }

    /// <summary>
    /// Standalone demonstration/test start-up only: <see cref="Load"/>, then the stopped sample
    /// train <see cref="BlueLineTrackLayout.DemonstrationTrainId"/> ("001") in Blue block 1,
    /// exactly as <see cref="BlueLineTrackLayout.LoadDemonstration"/> places it. Red and Green
    /// start clear. Not used by the production Track Model: in the integrated system CTC
    /// allocates train 001 itself.
    /// </summary>
    public static void LoadDemonstration(TrackService service)
    {
        Load(service);
        BlueLineTrackLayout.SeedDemonstrationTrain(service);
    }
}
