using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using TrainControl.Common.Communication;
using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Hardware;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Integration.Backends;
using TrainController.Integration.Logging;

namespace TrainController.Integration.Hardware;

/// <summary>
/// Hardware backend: TCP client to the single Raspberry Pi that controls all five Hardware
/// trains. Transport, framing, validation and failure handling ONLY — no control law (no PI,
/// braking, authority, station or door logic); that runs on the Pi.
/// </summary>
/// <remarks>
/// <para>
/// One long-lived connection, never reconnected per tick. Requests are serialized through a
/// FIFO lock, so the five trains' requests reach the Pi one at a time in issue order. Each
/// evaluation is synchronous request/response (tick N in, tick N out) implemented with
/// asynchronous I/O; nothing blocks the UI thread.
/// </para>
/// <para>
/// Any communication failure (refused, dropped, timeout, malformed / mismatched / stale
/// response, Pi error) closes the connection and throws <see cref="ControllerCommunicationException"/>;
/// the execution layer turns that into a fail-safe output (power 0, emergency brake). There is
/// never a Software fallback.
/// </para>
/// <para>
/// Safety policy (integration, not control law): a train that suffered a communication fault
/// stays in fail-safe until the link works AND the driver presses E-brake reset. The Pi state
/// of that train is then reset before control resumes, because its authority / station
/// estimates did not advance while the link was down.
/// </para>
/// </remarks>
public sealed class TcpHardwareTrainControllerBackend : ITrainControllerBackend, IHardwareConnectionStatus, IAsyncDisposable
{
    private const string Category = "Hardware";

    private readonly HardwareLinkOptions _options;
    private readonly ITrainControllerEventLog _log;
    private readonly FifoAsyncLock _lock = new FifoAsyncLock();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HashSet<string> _faultLatched = new HashSet<string>(StringComparer.Ordinal);

    private TcpClient? _client;
    private NetworkStream? _stream;
    private long _nextRequestId;
    private TimeSpan? _lastConnectAttempt;
    private bool _fullResetPending;
    private bool _firstRequestOnConnection;
    private volatile HardwareConnectionState _state = HardwareConnectionState.Disconnected;
    private string _detail;

    public TcpHardwareTrainControllerBackend(HardwareLinkOptions options, ITrainControllerEventLog? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _log = log ?? NullTrainControllerEventLog.Instance;
        _detail = $"{_options.Endpoint} — not connected yet";

        // One-time serializer warm-up off the UI thread, so the first real request is not slowed.
        _ = Task.Run(HardwareProtocol.WarmUp);
    }

    public ControllerType ControllerType => ControllerType.Hardware;

    public HardwareConnectionState ConnectionState => _state;

    public string ConnectionDetail => Volatile.Read(ref _detail);

    /// <summary>True while the train is held in fail-safe after a communication fault (driver reset required).</summary>
    public bool IsFaultLatched(string trainId)
    {
        lock (_faultLatched)
        {
            return _faultLatched.Contains(trainId);
        }
    }

    public async Task<TrainControllerOutput> EvaluateAsync(TrainControllerInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (TrainFleet.GetControllerType(input.TrainId) != ControllerType.Hardware)
        {
            throw new InvalidOperationException($"'{input.TrainId}' is not a Hardware-controlled train.");
        }

        using (await _lock.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

                if (IsFaultLatched(input.TrainId))
                {
                    if (!input.Driver.EmergencyBrakeResetRequested)
                    {
                        throw new ControllerCommunicationException(
                            $"Hardware link fault latched for {input.TrainId}: link is up again — press E-brake reset to resume Hardware control.");
                    }

                    await SendResetAsync(input.TrainId, cancellationToken).ConfigureAwait(false);
                    SetLatched(input.TrainId, false);
                    _log.Log(TrainControllerLogLevel.Info, Category, input.TrainId, "Driver reset after link fault: Pi state for this train re-synchronized; Hardware control resumed.");
                }

                // The first controller exchange on a fresh connection may still include one-time
                // work on either side; it gets the (longer) connect timeout, later ones the strict one.
                var timeout = _firstRequestOnConnection ? Max(_options.RequestTimeout, _options.ConnectTimeout) : _options.RequestTimeout;
                var reply = await ExchangeAsync(new HardwareEnvelope
                {
                    Type = HardwareMessageType.ControllerRequest,
                    TrainId = input.TrainId,
                    TickId = input.TickId,
                    Input = input,
                }, HardwareMessageType.ControllerResponse, cancellationToken, timeout).ConfigureAwait(false);
                _firstRequestOnConnection = false;

                var output = reply.Output ?? throw new HardwareProtocolException("ControllerResponse without output.");
                if (output.TrainId != input.TrainId || output.TickId != input.TickId)
                {
                    throw new HardwareProtocolException(
                        $"Output identity {output.TrainId}/{output.TickId} does not match request {input.TrainId}/{input.TickId}.");
                }

                SetState(HardwareConnectionState.Active, $"{_options.Endpoint} — active");
                return output;
            }
            catch (ControllerCommunicationException)
            {
                SetLatched(input.TrainId, true);
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancelled mid-exchange: the stream position is unknown, so drop the link.
                Disconnect(HardwareConnectionState.Faulted, "request cancelled");
                SetLatched(input.TrainId, true);
                throw;
            }
            catch (Exception ex) when (IsCommunicationFailure(ex))
            {
                var reason = Describe(ex);
                Disconnect(HardwareConnectionState.Faulted, reason);
                SetLatched(input.TrainId, true);
                _log.Log(TrainControllerLogLevel.Error, Category, input.TrainId, $"Hardware communication failure: {reason}");
                throw new ControllerCommunicationException(reason, ex);
            }
        }
    }

    /// <summary>
    /// Test Simulation Reset: clears link-fault latches and resets ALL Pi train states. If the Pi
    /// is unreachable the reset is remembered and sent as soon as the next connection is made.
    /// Never throws for communication problems.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        using (await _lock.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_faultLatched)
            {
                _faultLatched.Clear();
            }

            _fullResetPending = true;
            try
            {
                await EnsureConnectedAsync(cancellationToken, ignoreThrottle: true).ConfigureAwait(false);
                if (_fullResetPending)
                {
                    await SendResetAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                    _fullResetPending = false;
                }

                _log.Log(TrainControllerLogLevel.Info, Category, null, "Pi controller states reset (all Hardware trains).");
            }
            catch (Exception ex) when (ex is ControllerCommunicationException || IsCommunicationFailure(ex))
            {
                Disconnect(HardwareConnectionState.Faulted, Describe(ex));
                _log.Log(TrainControllerLogLevel.Warning, Category, null, $"Pi unreachable during reset; reset will be sent on reconnect. ({Describe(ex)})");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (await _lock.AcquireAsync(CancellationToken.None).ConfigureAwait(false))
        {
            Disconnect(HardwareConnectionState.Disconnected, "closed");
        }
    }

    // ------------------------------------------------------------------ connection

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken, bool ignoreThrottle = false)
    {
        if (_stream is not null && _client is { Connected: true })
        {
            return;
        }

        var now = _clock.Elapsed;
        if (!ignoreThrottle && _lastConnectAttempt is TimeSpan last && now - last < _options.ReconnectInterval)
        {
            throw new ControllerCommunicationException($"Raspberry Pi not connected ({ConnectionDetail}); next attempt shortly.");
        }

        _lastConnectAttempt = now;
        SetState(HardwareConnectionState.Connecting, $"{_options.Endpoint} — connecting");

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);
            try
            {
                await client.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"connect to {_options.Endpoint} timed out after {_options.ConnectTimeout.TotalMilliseconds:F0} ms");
            }

            _client = client;
            _stream = client.GetStream();

            // Handshake is part of connecting: it uses the connect timeout.
            var hello = await ExchangeAsync(new HardwareEnvelope { Type = HardwareMessageType.Hello }, HardwareMessageType.HelloResponse, cancellationToken, _options.ConnectTimeout).ConfigureAwait(false);
            var served = hello.ServedTrainIds ?? Array.Empty<string>();
            if (!served.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(TrainFleet.HardwareTrainIds))
            {
                throw new HardwareProtocolException($"Pi serves [{string.Join(", ", served)}], expected [{string.Join(", ", TrainFleet.HardwareTrainIds)}].");
            }

            if (_fullResetPending)
            {
                await SendResetAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                _fullResetPending = false;
            }

            _firstRequestOnConnection = true;
            SetState(HardwareConnectionState.Ready, $"{_options.Endpoint} — connected (protocol v{HardwareProtocol.Version})");
            _log.Log(TrainControllerLogLevel.Info, Category, null, $"Raspberry Pi connected at {_options.Endpoint}.");
        }
        catch (Exception ex) when (IsCommunicationFailure(ex))
        {
            if (!ReferenceEquals(_client, client))
            {
                client.Dispose();
            }

            Disconnect(HardwareConnectionState.Disconnected, Describe(ex));
            throw new ControllerCommunicationException($"Raspberry Pi unreachable at {_options.Endpoint}: {Describe(ex)}", ex);
        }
    }

    private Task SendResetAsync(string trainId, CancellationToken cancellationToken) =>
        ExchangeAsync(new HardwareEnvelope { Type = HardwareMessageType.ResetRequest, TrainId = trainId }, HardwareMessageType.ResetResponse, cancellationToken);

    /// <summary>One framed request, one framed reply, with timeout and full echo validation.</summary>
    private async Task<HardwareEnvelope> ExchangeAsync(
        HardwareEnvelope request,
        HardwareMessageType expectedType,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null)
    {
        var stream = _stream ?? throw new IOException("Not connected.");
        var sent = request with { ProtocolVersion = HardwareProtocol.Version, RequestId = ++_nextRequestId };
        var limit = timeoutOverride ?? _options.RequestTimeout;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);

        HardwareEnvelope reply;
        try
        {
            await LengthPrefixedFraming.WriteFrameAsync(stream, HardwareProtocol.Serialize(sent), timeout.Token).ConfigureAwait(false);
            var frame = await LengthPrefixedFraming.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false)
                ?? throw new IOException("Connection closed by the Raspberry Pi.");
            reply = HardwareProtocol.Deserialize(frame);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"no {expectedType} within {limit.TotalMilliseconds:F0} ms");
        }

        if (reply.ProtocolVersion != HardwareProtocol.Version)
        {
            throw new HardwareProtocolException($"unsupported protocol version {reply.ProtocolVersion} (expected {HardwareProtocol.Version})");
        }

        if (reply.RequestId != sent.RequestId)
        {
            throw new HardwareProtocolException($"stale or duplicate response (request id {reply.RequestId}, expected {sent.RequestId})");
        }

        if (reply.Type == HardwareMessageType.ErrorResponse)
        {
            throw new HardwareProtocolException($"Pi reported an error: {reply.Error}");
        }

        if (reply.Type != expectedType)
        {
            throw new HardwareProtocolException($"unexpected {reply.Type} (expected {expectedType})");
        }

        if (reply.TrainId != sent.TrainId || reply.TickId != sent.TickId)
        {
            throw new HardwareProtocolException(
                $"response for {reply.TrainId}/tick {reply.TickId} does not match request {sent.TrainId}/tick {sent.TickId}");
        }

        return reply;
    }

    private void Disconnect(HardwareConnectionState state, string reason)
    {
        try
        {
            _stream?.Dispose();
            _client?.Dispose();
        }
        catch (Exception)
        {
            // Closing a broken socket must never escalate.
        }

        var wasConnected = _stream is not null;
        _stream = null;
        _client = null;
        SetState(state, $"{_options.Endpoint} — {reason}");

        if (wasConnected)
        {
            _log.Log(TrainControllerLogLevel.Warning, Category, null, $"Raspberry Pi disconnected: {reason}");
        }
    }

    private void SetState(HardwareConnectionState state, string detail)
    {
        _state = state;
        Volatile.Write(ref _detail, detail);
    }

    private void SetLatched(string trainId, bool latched)
    {
        lock (_faultLatched)
        {
            if (latched)
            {
                _faultLatched.Add(trainId);
            }
            else
            {
                _faultLatched.Remove(trainId);
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static bool IsCommunicationFailure(Exception ex) =>
        ex is IOException or SocketException or TimeoutException or InvalidDataException
            or HardwareProtocolException or JsonException or ObjectDisposedException;

    private static string Describe(Exception ex) => ex switch
    {
        SocketException socket => $"socket error ({socket.SocketErrorCode}): {socket.Message}",
        TimeoutException timeout => $"timeout: {timeout.Message}",
        HardwareProtocolException protocol => $"invalid response: {protocol.Message}",
        InvalidDataException framing => $"invalid framing: {framing.Message}",
        ControllerCommunicationException comm => comm.Message,
        _ => ex.Message,
    };
}
