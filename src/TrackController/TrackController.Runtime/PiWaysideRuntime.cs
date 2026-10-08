using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Models;
using TrackController.Core.Services;
using TrainControl.Common.Communication;

namespace TrackController.Runtime;

/// <summary>No local decision fallback. A broken or expired Pi connection produces stop/unknown outputs.</summary>
public sealed class PiWaysideRuntime : IWaysideRuntime
{
    private readonly string _host;
    private readonly int _port;
    private readonly WaysideConfigurationMessage _configuration;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private string _programName, _programSource;
    private TcpClient? _socket;
    private PiProtocol? _protocol;
    private TimeSpan _clockOffset;
    private DateTimeOffset _retryAfter;
    private bool _disposed;

    public WaysideSnapshotMessage LatestSnapshot { get; private set; }

    /// <summary>Configures the loopback SSH-tunnel endpoint and the territory used for a new Pi session.</summary>
    public PiWaysideRuntime(string host, int port, WaysideConfigurationMessage configuration, string programName, string programSource)
    {
        // The device link is authenticated/encrypted by SSH, not an unauthenticated campus-network socket.
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address) || port is < 1 or > 65535)
        {
            throw new ArgumentException("Pi endpoints must be a loopback IP and port reached through the SSH tunnel.");
        }

        _host = host;
        _port = port;
        _configuration = WaysideConfiguration.ValidateAndCopy(configuration);
        _programName = programName;
        _programSource = programSource;
        LatestSnapshot = UnavailableOutput.CreateStopSnapshot(new TrackControllerService(_configuration).GetSnapshot(), "Pi not connected. Start the Pi runtime and SSH tunnel.");
    }

    /// <summary>Handles an application message through the selected runtime and returns its result.</summary>
    public async Task<object> ReceiveAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
    {
        await _io.WaitAsync(cancellationToken);
        try
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PiWaysideRuntime));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            timeout.CancelAfter(1500);
            if (message.MessageType == nameof(WaysidePlcProgramMessage))
            {
                // Preserve the requested replacement even if receipt is lost or compilation fails.
                // Reconnection must never silently restore an older, permissive program.
                var replacement = MessageSerializer.DeserializePayload<WaysidePlcProgramMessage>(message);
                _programName = replacement.Name;
                _programSource = replacement.Source;
            }

            await ConnectAsync(timeout.Token);
            if (message.MessageType == nameof(WaysideFeedbackMessage))
            {
                var feedback = MessageSerializer.DeserializePayload<WaysideFeedbackMessage>(message);
                foreach (var block in feedback.Blocks)
                {
                    block.ObservedAtUtc += _clockOffset;
                }

                message = RuntimeMessages.CreateEnvelope(feedback);
            }

            var sentAt = DateTimeOffset.UtcNow;
            await _protocol!.WriteEnvelopeAsync(message, timeout.Token);
            var envelope = await _protocol.ReadAsync(timeout.Token);
            if (envelope.MessageType == nameof(WaysideSnapshotMessage))
            {
                var snapshot = MessageSerializer.DeserializePayload<WaysideSnapshotMessage>(envelope);
                if (snapshot.Blocks is null || snapshot.Authorizations is null || snapshot.Blocks.Any(blockStatus => blockStatus is null) || snapshot.Authorizations.Any(authorization => authorization is null) || snapshot.WaysideId != _configuration.WaysideId || !snapshot.Blocks.Select(blockStatus => blockStatus.BlockId).Order().SequenceEqual(WaysideConfiguration.GetOwnedIds(_configuration).Order()) || snapshot.NeighborObservations is null || snapshot.NeighborObservations.Any(neighbor => neighbor is null) || !snapshot.NeighborObservations.Select(neighbor => neighbor.BlockId).Order().SequenceEqual(WaysideConfiguration.GetNeighborIds(_configuration).Order()))
                {
                    throw new InvalidDataException("Pi returned a different territory.");
                }

                var evaluated = snapshot.EvaluatedAtUtc - _clockOffset;
                var validUntil = snapshot.ValidUntilUtc - _clockOffset;
                // Never extend a lease by network/queue delay, even if host clocks differ.
                var conservativeLimit = sentAt + (snapshot.ValidUntilUtc - snapshot.EvaluatedAtUtc);
                if (validUntil > conservativeLimit)
                {
                    validUntil = conservativeLimit;
                }

                if (validUntil <= DateTimeOffset.UtcNow || validUntil > sentAt.AddSeconds(1.5))
                {
                    throw new InvalidDataException("Pi output expired before receipt.");
                }

                LatestSnapshot = snapshot with
                {
                    EvaluatedAtUtc = evaluated,
                    ValidUntilUtc = validUntil
                };
                return LatestSnapshot;
            }

            if (envelope.MessageType != nameof(WaysideAcknowledgementMessage))
            {
                throw new InvalidDataException("Unexpected Pi response.");
            }

            var result = MessageSerializer.DeserializePayload<WaysideAcknowledgementMessage>(envelope);
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or JsonException or OperationCanceledException or ArgumentException)
        {
            DropConnection();
            if (DateTimeOffset.UtcNow >= _retryAfter)
            {
                _retryAfter = DateTimeOffset.UtcNow.AddSeconds(1);
            }

            LatestSnapshot = UnavailableOutput.CreateStopSnapshot(LatestSnapshot, $"Pi unavailable: {ex.Message}");
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return RuntimeMessages.CreateRejection(message.MessageType, LatestSnapshot.PlcStatus);
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>Refreshes runtime outputs so timeouts apply even when no new input arrives.</summary>
    public async Task<WaysideSnapshotMessage> PollAsync(CancellationToken cancellationToken = default)
    {
        await ReceiveAsync(RuntimeMessages.CreateEnvelope(new WaysideSnapshotRequest()), cancellationToken);
        return LatestSnapshot;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_protocol is not null)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < _retryAfter)
        {
            throw new IOException("Waiting to reconnect to the Pi.");
        }

        _socket = new TcpClient
        {
            NoDelay = true
        };
        var before = DateTimeOffset.UtcNow;
        await _socket.ConnectAsync(_host, _port, cancellationToken);
        _protocol = new(_socket.GetStream());
        var helloEnvelope = await _protocol.ReadAsync(cancellationToken);
        if (helloEnvelope.MessageType == nameof(WaysideAcknowledgementMessage))
        {
            throw new IOException(MessageSerializer.DeserializePayload<WaysideAcknowledgementMessage>(helloEnvelope).Detail);
        }

        if (helloEnvelope.MessageType != nameof(PiHello))
        {
            throw new InvalidDataException("Not a Track Controller Pi endpoint.");
        }

        var hello = MessageSerializer.DeserializePayload<PiHello>(helloEnvelope);
        if (hello.Version != PiProtocol.Version)
        {
            throw new InvalidDataException("Pi protocol version mismatch. Deploy the matching runtime.");
        }

        _clockOffset = hello.ServerTimeUtc - (before + (DateTimeOffset.UtcNow - before) / 2);
        await _protocol.WriteAsync(new PiSessionStart(_configuration, _programName, _programSource), cancellationToken);
        var response = await _protocol.ReadAsync(cancellationToken);
        if (response.MessageType != nameof(WaysideAcknowledgementMessage))
        {
            throw new InvalidDataException("Missing Pi initialization response.");
        }

        var receipt = MessageSerializer.DeserializePayload<WaysideAcknowledgementMessage>(response);
        if (!receipt.IsAccepted)
        {
            throw new IOException(receipt.Detail);
        }
    }

    private void DropConnection()
    {
        _socket?.Dispose();
        _socket = null;
        _protocol?.Dispose();
        _protocol = null;
    }

    /// <summary>Stops owned background operations and releases connections before returning.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stop.CancelAsync();
        await _io.WaitAsync();
        try
        {
            DropConnection();
        }
        finally
        {
            _io.Release();
        }
    }
}
