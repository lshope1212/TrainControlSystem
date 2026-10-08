using System.Buffers.Binary;
using System.Text;
using TrainControl.Common.Communication;
using TrainController.Abstractions.Hardware;
using TrainController.Abstractions.Outputs;
using static TrainControl.Tests.TrainController.ControllerTestInputs;

namespace TrainControl.Tests.TrainController.Hardware;

/// <summary>Stream that returns at most one byte per read, like a badly fragmented TCP stream.</summary>
internal sealed class TrickleStream : Stream
{
    private readonly MemoryStream _inner;

    public TrickleStream(byte[] data) => _inner = new MemoryStream(data);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(1, count));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_inner.Read(buffer.Span[..Math.Min(1, buffer.Length)]));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

[TestClass]
public class HardwareProtocolTests
{
    private static async Task<byte[]> FramesAsync(params byte[][] payloads)
    {
        using var buffer = new MemoryStream();
        foreach (var payload in payloads)
        {
            await LengthPrefixedFraming.WriteFrameAsync(buffer, payload, CancellationToken.None);
        }

        return buffer.ToArray();
    }

    [TestMethod]
    public async Task Framing_ReassemblesFragmentedReads_AndKeepsMessageBoundaries()
    {
        var first = Encoding.UTF8.GetBytes("{\"a\":1}");
        var second = Encoding.UTF8.GetBytes(new string('x', 5000));
        var stream = new TrickleStream(await FramesAsync(first, second, Array.Empty<byte>()));

        CollectionAssert.AreEqual(first, await LengthPrefixedFraming.ReadFrameAsync(stream, CancellationToken.None));
        CollectionAssert.AreEqual(second, await LengthPrefixedFraming.ReadFrameAsync(stream, CancellationToken.None));
        CollectionAssert.AreEqual(Array.Empty<byte>(), await LengthPrefixedFraming.ReadFrameAsync(stream, CancellationToken.None));
        Assert.IsNull(await LengthPrefixedFraming.ReadFrameAsync(stream, CancellationToken.None), "Clean end of stream.");
    }

    [TestMethod]
    public async Task Framing_RejectsInvalidLengthAndTruncatedFrames()
    {
        var hugeHeader = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(hugeHeader, LengthPrefixedFraming.MaxPayloadBytes + 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LengthPrefixedFraming.ReadFrameAsync(new MemoryStream(hugeHeader), CancellationToken.None));

        var negativeHeader = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(negativeHeader, -5);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LengthPrefixedFraming.ReadFrameAsync(new MemoryStream(negativeHeader), CancellationToken.None));

        var truncated = (await FramesAsync(new byte[100]))[..50];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LengthPrefixedFraming.ReadFrameAsync(new MemoryStream(truncated), CancellationToken.None));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LengthPrefixedFraming.ReadFrameAsync(new MemoryStream(new byte[] { 0, 0 }), CancellationToken.None));
    }

    [TestMethod]
    public void Protocol_RoundTripsCompleteSiInput_WithEnumsAsText()
    {
        var input = Input(Model(speed: 7.5, authorized: 12.0, authority: 345.6, passengerEmergency: true, beacon: Beacon(250.0, "WHITED", TrainControl.Contracts.Enums.PlatformSide.Both)),
            Automatic(), tick: 42, trainId: "TRAIN-004");
        var envelope = new HardwareEnvelope { Type = HardwareMessageType.ControllerRequest, RequestId = 9, TrainId = "TRAIN-004", TickId = 42, Input = input };

        var bytes = HardwareProtocol.Serialize(envelope);
        var json = Encoding.UTF8.GetString(bytes);
        StringAssert.Contains(json, "\"type\":\"ControllerRequest\"");
        StringAssert.Contains(json, "\"actualSpeedMetersPerSecond\":7.5");
        StringAssert.Contains(json, "\"platformSide\":\"Both\"");

        var back = HardwareProtocol.Deserialize(bytes);
        Assert.AreEqual(HardwareProtocol.Version, back.ProtocolVersion);
        Assert.AreEqual(9L, back.RequestId);
        Assert.AreEqual(input, back.Input, "Every input field survives the link (records compare by value).");
    }

    [TestMethod]
    public void Protocol_RefusesNaNAndMalformedJson()
    {
        var nanOutput = new HardwareEnvelope
        {
            Type = HardwareMessageType.ControllerResponse,
            Output = new TrainControllerOutput { Commands = new TrainModelCommand { PowerCommandWatts = double.NaN } },
        };
        Assert.ThrowsExactly<HardwareProtocolException>(() => HardwareProtocol.Serialize(nanOutput));

        Assert.ThrowsExactly<HardwareProtocolException>(() => HardwareProtocol.Deserialize(Encoding.UTF8.GetBytes("not json")));
        Assert.ThrowsExactly<HardwareProtocolException>(() => HardwareProtocol.Deserialize(Encoding.UTF8.GetBytes("null")));
        Assert.ThrowsExactly<HardwareProtocolException>(() => HardwareProtocol.Deserialize(
            Encoding.UTF8.GetBytes("{\"type\":\"ControllerResponse\",\"output\":{\"commands\":{\"powerCommandWatts\":\"NaN\"}}}")));
    }
}
