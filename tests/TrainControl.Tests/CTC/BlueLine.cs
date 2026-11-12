using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// The 15-block Blue Line (same topology as the TestUI sample layout): A1-A5, then a switch
/// at A5 to either B6-B10 or C11-C15. Every block is 50 m long with a 50 km/h limit.
/// </summary>
internal static class BlueLine
{
    public const string LineId = "BLUE";

    public const double BlockLengthMeters = 50.0;

    public const double SpeedLimitKilometersPerHour = 50.0;

    /// <summary>A1 → A5 → B6 → B10.</summary>
    public static readonly string[] BranchB = ["A1", "A2", "A3", "A4", "A5", "B6", "B7", "B8", "B9", "B10"];

    /// <summary>A1 → A5 → C11 → C15.</summary>
    public static readonly string[] BranchC = ["A1", "A2", "A3", "A4", "A5", "C11", "C12", "C13", "C14", "C15"];

    /// <summary>
    /// The fixture's yard entry: A1, where the real Blue Line's yard joins. Tests that need a train
    /// to enter service elsewhere state their own fixture yard entries (<see cref="CreateLayout"/>);
    /// CTC itself never accepts a start the layout does not mark.
    /// </summary>
    public static readonly string[] YardEntries = ["A1"];

    /// <param name="speedLimitKph">Overrides the speed limit of every block (e.g. to exceed the vehicle maximum).</param>
    /// <param name="yardEntries">The fixture layout's yard-entry blocks; <see cref="YardEntries"/> (A1) by default.</param>
    public static TrackLayoutMessage CreateLayout(double speedLimitKph = SpeedLimitKilometersPerHour, string[]? yardEntries = null)
    {
        var layout = CreateTopology(speedLimitKph);
        foreach (var block in layout.Lines[0].Blocks)
        {
            block.IsYardEntry = (yardEntries ?? YardEntries).Contains(block.BlockId);
        }

        return layout;
    }

    private static TrackLayoutMessage CreateTopology(double speedLimitKph) => new TrackLayoutMessage
    {
        Lines =
        {
            new TrackLineDefinition
            {
                LineId = LineId,
                Name = "Blue Line",
                Blocks =
                {
                    Block("A1", 1, "A", speedLimitKph, "A2"),
                    Block("A2", 2, "A", speedLimitKph, "A1", "A3"),
                    Block("A3", 3, "A", speedLimitKph, "A2", "A4"),
                    Block("A4", 4, "A", speedLimitKph, "A3", "A5"),
                    Block("A5", 5, "A", speedLimitKph, "A4", "B6", "C11"),
                    Block("B6", 6, "B", speedLimitKph, "A5", "B7"),
                    Block("B7", 7, "B", speedLimitKph, "B6", "B8"),
                    Block("B8", 8, "B", speedLimitKph, "B7", "B9"),
                    Block("B9", 9, "B", speedLimitKph, "B8", "B10"),
                    Block("B10", 10, "B", speedLimitKph, "B9"),
                    Block("C11", 11, "C", speedLimitKph, "A5", "C12"),
                    Block("C12", 12, "C", speedLimitKph, "C11", "C13"),
                    Block("C13", 13, "C", speedLimitKph, "C12", "C14"),
                    Block("C14", 14, "C", speedLimitKph, "C13", "C15"),
                    Block("C15", 15, "C", speedLimitKph, "C14"),
                },
            },
        },
    };

    public static CTCService CreateService(IMessageSender? sender = null, double speedLimitKph = SpeedLimitKilometersPerHour, string[]? yardEntries = null)
    {
        var service = new CTCService(sender ?? new FakeMessageSender());
        service.ApplyTrackLayout(CreateLayout(speedLimitKph, yardEntries));
        ReportAllClear(service);
        return service;
    }

    /// <summary>
    /// Reports every block Clear and open, as a live wayside does before any train runs. CTC treats a
    /// block it has no report for as Unknown, which is unsafe for authority, so tests that expect a
    /// train to be released start from this.
    /// </summary>
    public static void ReportAllClear(CTCService service)
    {
        foreach (var block in service.State.Lines.SelectMany(line => line.Blocks).ToList())
        {
            Report(service, block.BlockId, OccupancyState.Clear);
        }
    }

    /// <summary>Reports one block's occupancy (open, no equipment states), as the Track Controller would.</summary>
    public static void Report(CTCService service, string blockId, OccupancyState occupancy) =>
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = blockId, Occupancy = occupancy });

    /// <summary>
    /// The released train appears in its start block (what the Train Model integration will do):
    /// the block reports occupied and CTC issues the train's first complete movement request.
    /// </summary>
    public static async Task PlaceTrainAsync(CTCService service, string blockId)
    {
        Report(service, blockId, OccupancyState.Occupied);
        await service.SynchronizeMovementRequestsAsync();
    }

    /// <summary>
    /// A train entering each block of <paramref name="route"/> <paramref name="secondsPerBlock"/>
    /// seconds after the previous one, starting at <paramref name="departure"/>. Only the
    /// <paramref name="timedBlocks"/> carry a time (default: every block); the others are routed through.
    /// </summary>
    public static ScheduledTrain Train(
        string trainId,
        TimeSpan departure,
        double secondsPerBlock = 4,
        string[]? route = null,
        string[]? timedBlocks = null)
    {
        var train = new ScheduledTrain { TrainId = trainId, LineId = LineId };
        var blocks = route ?? BranchB;
        for (int i = 0; i < blocks.Length; i++)
        {
            bool isTimed = timedBlocks is null || timedBlocks.Contains(blocks[i]);
            train.Route.Add(new ScheduledRouteBlock
            {
                BlockId = blocks[i],
                ArrivalTime = isTimed ? departure + TimeSpan.FromSeconds(secondsPerBlock * i) : null,
            });
        }

        return train;
    }

    /// <summary>Line ID of the second line in <see cref="CreateTwoLineService"/>.</summary>
    public const string GreenLineId = "GREEN";

    /// <summary>
    /// The Blue Line plus an identical "Green Line" whose block IDs carry a "G-" prefix (block IDs
    /// are unique across the whole layout), for tests about IDs shared between lines.
    /// </summary>
    public static CTCService CreateTwoLineService(IMessageSender? sender = null)
    {
        var layout = CreateLayout();
        var green = new TrackLineDefinition { LineId = GreenLineId, Name = "Green Line" };
        foreach (var block in layout.Lines[0].Blocks)
        {
            var copy = Block("G-" + block.BlockId, block.BlockNumber, block.Section, block.SpeedLimitKilometersPerHour,
                block.ConnectedBlockIds.Select(id => "G-" + id).ToArray());
            copy.IsYardEntry = block.IsYardEntry;
            green.Blocks.Add(copy);
        }

        layout.Lines.Add(green);
        var service = new CTCService(sender ?? new FakeMessageSender());
        service.ApplyTrackLayout(layout);
        ReportAllClear(service);
        return service;
    }

    /// <summary>Like <see cref="Train"/>, on the Green Line of <see cref="CreateTwoLineService"/> (branch B).</summary>
    public static ScheduledTrain GreenTrain(string trainId, TimeSpan departure)
    {
        var train = Train(trainId, departure, route: BranchB.Select(id => "G-" + id).ToArray());
        train.LineId = GreenLineId;
        return train;
    }

    private static TrackBlockDefinition Block(string blockId, int number, string section, double speedLimitKph, params string[] connectedTo) => new TrackBlockDefinition
    {
        BlockId = blockId,
        BlockNumber = number,
        Section = section,
        LengthMeters = BlockLengthMeters,
        SpeedLimitKilometersPerHour = speedLimitKph,
        ConnectedBlockIds = connectedTo.ToList(),
    };
}
