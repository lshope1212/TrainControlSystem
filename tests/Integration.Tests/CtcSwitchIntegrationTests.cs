using CTC.Core.Models;
using CTC.Core.Scheduling;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

[TestClass]
public sealed class CtcSwitchIntegrationTests
{
    [TestMethod]
    public async Task CtcReverseSwitch_UsesVitalInterlockingWithoutExternalReservations()
    {
        await using var system = new LiveSystem();
        await system.StartTrackControllerAsync(LiveSystem.CourseDeployment);
        await system.StartCtcAsync();
        await system.StartTrackModelAsync(allLines: true);
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindLine("Green") is { } line && line.Blocks.All(block => block.Occupancy == OccupancyState.Clear)), "Green reports clear.");
        await system.ResumeTrackControllerAsync();
        foreach (string id in new[] { "GREEN-13", "GREEN-77", "GREEN-85" })
        {
            await system.OnCtcAsync(ctc => ctc.SetSwitchPositionAsync(id, SwitchPosition.Reverse));
            await LiveSystem.UntilAsync(async () => await system.OnTrackModel(track => track.FindBlock(id)!.Switch) == SwitchPosition.Reverse
                && await system.OnCtc(ctc => ctc.State.FindBlock(id)!.Switch) == SwitchPosition.Reverse, $"{id} physically reverses and CTC receives proof.");
        }
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "blocker", CurrentBlockId = "GREEN-100" });
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock("GREEN-100").Occupancy == OccupancyState.Occupied), "The protection zone is occupied.");
        await system.OnCtcAsync(ctc => ctc.SetSwitchPositionAsync("GREEN-85", SwitchPosition.Normal));
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideReceipts.Any(receipt => !receipt.IsAccepted && receipt.Detail.Contains("GREEN-100 is occupied"))), "The independent interlock refuses the unsafe CTC command.");
        Assert.AreEqual(SwitchPosition.Reverse, await system.OnTrackModel(track => track.FindBlock("GREEN-85")!.Switch));
    }

}
