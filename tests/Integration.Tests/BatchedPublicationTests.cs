using System.Collections.Concurrent;
using System.Diagnostics;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// How the full network travels: the Track Model's feedback as one Track Controller batch per line,
/// inputs that never wait on the optional TestUI, and compatibility with a controller that owns
/// only some of the lines.
/// </summary>
[TestClass]
public sealed class BatchedPublicationTests
{
    [TestMethod]
    public async Task TrackModelFeedback_TravelsAsOneBatchPerLine()
    {
        await using var system = new LiveSystem();
        var received = new ConcurrentQueue<(DateTime At, MessageEnvelope Envelope)>();
        using var stop = new CancellationTokenSource();
        var listener = NamedPipeTransport.ListenAsync(system.TrackControllerPipe, envelope => { received.Enqueue((DateTime.UtcNow, envelope)); return Task.CompletedTask; }, _ => { }, stop.Token);
        try
        {
            await system.StartTrackModelAsync(allLines: true);
            await Task.Delay(TimeSpan.FromSeconds(3.5));

            var envelopes = received.Select(item => item.Envelope).ToList();
            Assert.IsTrue(envelopes.All(envelope => envelope.MessageType == "TrackModelBlockStates"), string.Join(", ", envelopes.Select(envelope => envelope.MessageType).Distinct()));
            var batches = envelopes.Select(envelope => MessageSerializer.ReadValue<List<TrackModelBlockStateMessage>>(envelope, "blocks")).ToList();
            foreach (var batch in batches)
            {
                string prefix = batch[0].BlockId.StartsWith("RED-", StringComparison.Ordinal) ? "RED-" : batch[0].BlockId.StartsWith("GREEN-", StringComparison.Ordinal) ? "GREEN-" : "";
                Assert.IsTrue(batch.All(state => prefix.Length == 0 ? !state.BlockId.Contains('-') : state.BlockId.StartsWith(prefix, StringComparison.Ordinal)), "A batch holds one line.");
                Assert.HasCount(prefix switch { "RED-" => 76, "GREEN-" => 150, _ => 15 }, batch, "A batch holds the whole line.");
            }

            // Initial snapshot plus a 1 s heartbeat: a handful of connections, not 241 per publication.
            Assert.IsGreaterThanOrEqualTo(9, batches.Count);
            Assert.IsLessThanOrEqualTo(18, batches.Count);
            Assert.AreEqual(OccupancyState.Occupied, batches.Last(batch => batch.Count == 15).Single(state => state.BlockId == "1").Occupancy);
        }
        finally
        {
            await stop.CancelAsync();
            await listener;
        }
    }

    /// <summary>
    /// Inputs to the Track Model are applied back to back. Its answer to the optional standalone
    /// TestUI is sent separately; previously each input waited for that absent pipe's 100 ms connect timeout.
    /// </summary>
    [TestMethod]
    public async Task TrackModelInputs_DoNotWaitForTheAbsentTestUi()
    {
        await using var system = new LiveSystem();
        await system.StartTrackModelAsync(allLines: true);
        await system.SendToTrackModelAsync(new TrackModelSnapshotRequestMessage());

        var clock = Stopwatch.StartNew();
        for (int i = 1; i <= 20; i++)
        {
            await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "07", CurrentBlockId = "GREEN-" + (62 + i) });
        }

        await LiveSystem.UntilAsync(async () => await system.OnTrackModel(track => track.FindBlock("GREEN-82")!.TrainId) == "07", "the last move is applied.");
        Assert.IsLessThan(1.5, clock.Elapsed.TotalSeconds, $"20 inputs took {clock.Elapsed.TotalSeconds:0.00} s.");
    }

    /// <summary>
    /// A controller loaded with only the Blue Line still serves Blue while the Track Model runs all
    /// three lines: it refuses the Red and Green batches, which CTC then shows as Unknown, never as clear.
    /// </summary>
    [TestMethod]
    public async Task BlueOnlyTrackController_KeepsServingBlue_AndUnownedLinesStayUnknown()
    {
        await using var system = new LiveSystem();
        await system.StartTrackControllerAsync();
        await system.StartCtcAsync();
        await system.StartTrackModelAsync(allLines: true);

        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.Lines.Count == 3), "CTC loads all three lines.");
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindLine("Blue")!.Blocks.All(block => block.Occupancy != OccupancyState.Unknown && !block.IsStatusStale)),
            "the Blue Line is live.");
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideReceipts.Any(receipt => !receipt.IsAccepted && receipt.Detail.Contains("RED-1"))), "the controller refuses Red feedback it does not own.");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.IsTrue(system.Wayside!.LatestSnapshot.Blocks.All(block => block.IsFresh));
        Assert.IsTrue(await system.OnCtc(ctc => ctc.State.Lines.Where(line => line.LineId != "Blue").SelectMany(line => line.Blocks).All(block => block.Occupancy == OccupancyState.Unknown)));
    }
}
