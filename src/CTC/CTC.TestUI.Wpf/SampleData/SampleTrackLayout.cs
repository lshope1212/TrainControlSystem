using System.Text.Json;
using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.SampleData;

/// <summary>
/// The complete course railway the Test UI sends to the running CTC, expressed with the real
/// shared contracts: the Blue (15 blocks), Red (76) and Green (150) lines of the course
/// workbook "Track Layout &amp; Vehicle Data vF5".
/// </summary>
/// <remarks>
/// The data comes from course-vf5-layout.json, a fixture generated from the workbook during
/// development and embedded in this assembly, so nothing reads Excel at runtime. Blue keeps
/// its original IDs (A1..C15) that existing schedules use; Red and Green IDs are
/// line-qualified (e.g. RED-C7, GREEN-W132) so block IDs stay unique across the message.
/// </remarks>
public static class SampleTrackLayout
{
    private const string FixtureResourceName = "CTC.TestUI.Wpf.SampleData.course-vf5-layout.json";

    public static TrackLayoutMessage Create()
    {
        using var stream = typeof(SampleTrackLayout).Assembly.GetManifestResourceStream(FixtureResourceName)
            ?? throw new InvalidOperationException($"Embedded layout fixture '{FixtureResourceName}' is missing.");

        return JsonSerializer.Deserialize<TrackLayoutMessage>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The embedded layout fixture is empty.");
    }
}
