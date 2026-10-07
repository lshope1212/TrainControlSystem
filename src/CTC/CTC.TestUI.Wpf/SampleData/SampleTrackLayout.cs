using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.SampleData;

/// <summary>
/// The Blue Line layout the Test UI sends to the running CTC, expressed with the real
/// shared contracts: 15 blocks, each 50 m long with a 50 km/h speed limit, branching at
/// A5 to section B or section C. It is NOT the Green/Red line data that the Track Model
/// will eventually provide.
/// </summary>
public static class SampleTrackLayout
{
    /// <summary>Every Blue Line block has the same civil speed limit.</summary>
    private const double BlueLineSpeedLimitKilometersPerHour = 50.0;

    public static TrackLayoutMessage Create() => new TrackLayoutMessage
    {
        Lines =
        {
            new TrackLineDefinition
            {
                LineId = "BLUE",
                Name = "Blue Line",
                Blocks =
                {
                    Block("A1",  1, "A", 50, connectedTo: ["A2"]),
                    Block("A2",  2, "A", 50, connectedTo: ["A1", "A3"]),
                    Block("A3",  3, "A", 50, connectedTo: ["A2", "A4"],
                        hasCrossing: true),

                    Block("A4",  4, "A", 50, connectedTo: ["A3", "A5"]),

                    Block("A5",  5, "A", 50,
                        connectedTo: ["A4", "B6", "C11"],
                        hasSwitch: true),

                    Block("B6",  6, "B", 50,
                        connectedTo: ["A5", "B7"],
                        hasSignal: true),

                    Block("B7",  7, "B", 50, connectedTo: ["B6", "B8"]),
                    Block("B8",  8, "B", 50, connectedTo: ["B7", "B9"]),
                    Block("B9",  9, "B", 50, connectedTo: ["B8", "B10"]),

                    Block("B10", 10, "B", 50,
                        connectedTo: ["B9"],
                        station: "Station B"),

                    Block("C11", 11, "C", 50,
                        connectedTo: ["A5", "C12"],
                        hasSignal: true),

                    Block("C12", 12, "C", 50, connectedTo: ["C11", "C13"]),
                    Block("C13", 13, "C", 50, connectedTo: ["C12", "C14"]),
                    Block("C14", 14, "C", 50, connectedTo: ["C13", "C15"]),

                    Block("C15", 15, "C", 50,
                        connectedTo: ["C14"],
                        station: "Station C"),
                }
            }
        }
    };

    private static TrackBlockDefinition Block(
        string blockId,
        int number,
        string section,
        double lengthMeters,
        string[] connectedTo,
        string station = "",
        bool hasSwitch = false,
        bool hasSignal = false,
        bool hasCrossing = false,
        double speedLimitKilometersPerHour = BlueLineSpeedLimitKilometersPerHour)
    {
        var block = new TrackBlockDefinition
        {
            BlockId = blockId,
            BlockNumber = number,
            Section = section,
            LengthMeters = lengthMeters,
            SpeedLimitKilometersPerHour = speedLimitKilometersPerHour,
            StationName = station,
            HasSwitch = hasSwitch,
            HasSignal = hasSignal,
            HasCrossing = hasCrossing,
        };
        block.ConnectedBlockIds.AddRange(connectedTo);
        return block;
    }
}
