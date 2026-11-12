using System.Collections.Concurrent;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The production Track Model connection publishes every occupancy step of a train to the Track
/// Controller as its own observation, in order, even when one position report crosses several
/// boundaries: the Track Controller (its handoff, and through it CTC's sequential tracking) never misses
/// an intermediate block.
/// </summary>
[TestClass]
public sealed class OccupancyTransitionPublicationTests
{
    [TestMethod]
    public async Task OneCoalescedReport_ReachesTheTrackController_AsEveryIntermediateStepInOrder()
    {
        await using var system = new LiveSystem();
        var received = new ConcurrentQueue<MessageEnvelope>();
        using var stop = new CancellationTokenSource();
        var listener = NamedPipeTransport.ListenAsync(system.TrackControllerPipe, envelope => { received.Enqueue(envelope); return Task.CompletedTask; }, _ => { }, stop.Token);
        try
        {
            await system.StartTrackModelAsync(allLines: true);
            string[] route = PhysicalDispatchIntegrationTests.GreenRoute; // 100, 100, 200, 200, 100, 100 m
            await system.OnTrackModel(track =>
            {
                track.RequestTrainPlacement(new TrainDispatchRequestMessage { RequestId = "d1", TrainId = "001", LineId = "Green", StartBlockId = route[0], RouteBlockIds = [.. route] });
                return track.ApplyTrainPosition(new TrainStateMessage { TrainId = "001", PositionMeters = 0, TrainLengthMeters = 32.2, Sequence = 1, DispatchRequestId = "d1" });
            });
            await Task.Delay(500);
            received.Clear();

            // One report for 25 s of travel (coalesced): front 0 -> 250 m crosses 63/64, the rear leaving 63,
            // 64/65 and the rear leaving 64.
            var result = await system.OnTrackModel(track => track.ApplyTrainPosition(new TrainStateMessage { TrainId = "001", PositionMeters = 250, TrainLengthMeters = 32.2, Sequence = 251, DispatchRequestId = "d1" }));
            Assert.AreEqual(TrackModel.Core.Models.TrainPositionOutcome.Applied, result.Outcome, result.Detail);
            await Task.Delay(1500);

            var greenObservations = received.Where(e => e.MessageType == "TrackModelBlockStates")
                .Select(e => MessageSerializer.ReadValue<List<TrackModelBlockStateMessage>>(e, "blocks"))
                .Where(batch => batch[0].BlockId.StartsWith("GREEN-", StringComparison.Ordinal))
                .Select(batch => (At: batch[0].ObservedAtUtc, Occupied: string.Join("+", batch.Where(state => state.Occupancy == OccupancyState.Occupied).Select(state => state.BlockId))))
                .ToList();
            Console.WriteLine(string.Join(" | ", greenObservations.Select(o => o.Occupied)));

            var distinct = new List<string>();
            foreach (var observation in greenObservations.Where(o => distinct.Count == 0 || distinct[^1] != o.Occupied)) distinct.Add(observation.Occupied);
            CollectionAssert.AreEqual(new[] { "GREEN-63+GREEN-64", "GREEN-64", "GREEN-64+GREEN-65", "GREEN-65" }, distinct.ToArray(),
                "Every intermediate footprint is observed, in the order the train passed through it.");
            for (int i = 1; i < greenObservations.Count; i++)
            {
                Assert.IsGreaterThan(greenObservations[i - 1].At, greenObservations[i].At, "Strictly newer observations: the Track Controller never discards a step as a duplicate.");
            }
        }
        finally
        {
            await stop.CancelAsync();
            await listener;
        }
    }
}
