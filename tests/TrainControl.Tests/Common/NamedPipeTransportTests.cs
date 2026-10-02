using System.IO.Pipes;
using System.Text;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.Common;

[TestClass]
public class NamedPipeTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    // A unique pipe per test so tests never collide with each other or running apps.
    private static string UniquePipeName() => $"TrainControl.Tests.{Guid.NewGuid():N}";

    [TestMethod]
    public async Task SendAsync_DeliversEnvelopeToListener()
    {
        var pipeName = UniquePipeName();
        using var cts = new CancellationTokenSource(TestTimeout);
        var received = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        var listener = NamedPipeTransport.ListenAsync(
            pipeName,
            envelope => { received.TrySetResult(envelope); return Task.CompletedTask; },
            ex => received.TrySetException(ex),
            cts.Token);

        await NamedPipeTransport.SendAsync(pipeName, new MaintenanceRequestMessage { BlockId = "G12", RequestedState = MaintenanceState.Closed }, cts.Token);

        var envelope = await received.Task.WaitAsync(cts.Token);
        var message = MessageSerializer.DeserializePayload<MaintenanceRequestMessage>(envelope);
        Assert.AreEqual("MaintenanceRequestMessage", envelope.MessageType);
        Assert.AreEqual("G12", message.BlockId);
        Assert.AreEqual(MaintenanceState.Closed, message.RequestedState);

        cts.Cancel();
        await listener;
    }

    [TestMethod]
    public async Task ListenAsync_MalformedMessage_ReportsErrorAndKeepsListening()
    {
        var pipeName = UniquePipeName();
        using var cts = new CancellationTokenSource(TestTimeout);
        var errorReported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        var listener = NamedPipeTransport.ListenAsync(
            pipeName,
            envelope => { received.TrySetResult(envelope); return Task.CompletedTask; },
            ex => errorReported.TrySetResult(ex),
            cts.Token);

        await using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(cts.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            await writer.WriteLineAsync("this is not json");
        }

        await errorReported.Task.WaitAsync(cts.Token);

        await NamedPipeTransport.SendAsync(pipeName, new BlockStatusMessage { BlockId = "G12" }, cts.Token);

        var envelope = await received.Task.WaitAsync(cts.Token);
        Assert.AreEqual("BlockStatusMessage", envelope.MessageType);

        cts.Cancel();
        await listener;
    }

    [TestMethod]
    public async Task SendAsync_NoListener_ThrowsTimeoutException()
    {
        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            NamedPipeTransport.SendAsync(UniquePipeName(), new BlockStatusMessage(), connectTimeoutMilliseconds: 100));
    }
}
