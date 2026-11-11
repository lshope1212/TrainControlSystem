using System.Collections.Concurrent;
using TrainControl.Common.Communication;
using TrainControl.Common.Identity;
using TrainControl.Contracts.Messages;
using TrainController.Abstractions.Configuration;
using TrainController.Integration;
using TrainController.Integration.Communication;
using TrainController.Integration.Output;
using TrainController.Integration.Simulation;
using TrainModel.Core.Models;
using TrainModel.Core.Services;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The trains' side of the live system, built from production pieces: the Train Model's track-circuit
/// pickup (<see cref="TrackCircuitReceiver"/>, as TrainModel.Wpf's receiver uses it) listening on the
/// Track Model's Train Model pipe, and the production Train Controller (subsystem and pipe receiver)
/// fed the <see cref="TrainModelStatusMessage"/> the Train Model publishes. The Train Model's physics
/// is stood in for by the Train Controller's simple point-mass plant model.
/// </summary>
internal sealed class OnboardTrains : IAsyncDisposable
{
    private readonly TrackCircuitReceiver _pickup = new();
    private readonly Dictionary<string, Train> _trains = new(StringComparer.Ordinal);
    private readonly RecordingSink _sink = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listen;
    private readonly TrainControllerSubsystem _controller;
    private readonly TrainControllerPipeReceiver _receiver;

    /// <param name="trainIds">Canonical dispatch IDs (e.g. "001"); each must be a Software-controlled fleet train.</param>
    public OnboardTrains(string trainModelPipe, params string[] trainIds)
    {
        foreach (string id in trainIds)
        {
            _trains[id] = new Train(id);
        }

        _controller = TrainControllerSubsystem.Create(trainModelSink: _sink);
        _receiver = new TrainControllerPipeReceiver(_controller);
        _listen = NamedPipeTransport.ListenAsync(trainModelPipe, HandleAsync, _ => { }, _stop.Token);
    }

    public Train this[string trainId] => _trains[trainId];

    private Task HandleAsync(MessageEnvelope envelope)
    {
        if (envelope.MessageType == nameof(TrackModelTrainEnvironmentMessage))
        {
            var environment = MessageSerializer.DeserializePayload<TrackModelTrainEnvironmentMessage>(envelope);
            if (_trains.TryGetValue(environment.TrainId, out var train))
            {
                lock (train)
                {
                    if (_pickup.TryApply(train.TrainId, environment, train.Inputs))
                    {
                        train.Received.Enqueue(new Pickup(DateTime.UtcNow, environment.AuthorityMeters, environment.CommandedSpeedMetersPerSecond, environment.MovementRequestId, environment.BlockId));
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// One control period for <paramref name="trainId"/>: the Train Model status from its current
    /// track-circuit inputs and speed goes to the production Train Controller, whose commands then
    /// move the stand-in plant one period. Returns the commands.
    /// </summary>
    public async Task<TrainControllerCommandMessage> TickAsync(string trainId)
    {
        var train = _trains[trainId];
        TrainModelStatusMessage status;
        lock (train)
        {
            status = new TrainModelStatusMessage
            {
                TrainId = train.ControllerLabel,
                TickId = ++train.TickId,
                IsActive = true,
                ActualSpeedMetersPerSecond = train.SpeedMetersPerSecond,
                CommandedSpeedMetersPerSecond = train.Inputs.CommandedSpeedMetersPerSecond,
                CommandedAuthorityMeters = train.Inputs.CommandedAuthorityMeters,
                TrackSignalValid = true,
            };
        }

        await _receiver.HandleStatusAsync(status, CancellationToken.None);
        var command = _sink.Last(train.ControllerLabel)!;
        var output = _controller.Registry.Get(train.ControllerLabel).LastOutput!;
        lock (train)
        {
            train.SpeedMetersPerSecond = SimplePlantModel.NextSpeed(train.SpeedMetersPerSecond, output.Commands, VehicleSpecification.Flexity2Blackpool, _controller.Engine.DeltaTimeSeconds);
            train.Ticks.Add(new ControlTick(DateTime.UtcNow, status.CommandedAuthorityMeters, command.EmergencyBrakeCommand, command.ServiceBrakeCommand, output.Display.Alerts.ToArray()));
        }

        return command;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _listen.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        _stop.Dispose();
    }

    internal sealed record Pickup(DateTime At, double Authority, double Speed, string RequestId, string BlockId);

    internal sealed record ControlTick(DateTime At, double Authority, bool EmergencyBrake, bool ServiceBrake, string[] Alerts);

    internal sealed class Train(string trainId)
    {
        public string TrainId { get; } = trainId;

        public string ControllerLabel { get; } = TrainIdConvention.ToTrainControllerLabel(trainId);

        public TrainInputs Inputs { get; } = new();

        /// <summary>Every track-circuit update the Train Model applied for this train, in order.</summary>
        public ConcurrentQueue<Pickup> Received { get; } = new();

        public List<ControlTick> Ticks { get; } = [];

        public long TickId { get; set; }

        public double SpeedMetersPerSecond { get; set; }

        public double Authority
        {
            get
            {
                lock (this)
                {
                    return Inputs.CommandedAuthorityMeters;
                }
            }
        }
    }

    private sealed class RecordingSink : ITrainModelCommandSink
    {
        private readonly ConcurrentDictionary<string, TrainControllerCommandMessage> _last = new(StringComparer.Ordinal);

        public TrainControllerCommandMessage? Last(string trainId) => _last.GetValueOrDefault(trainId);

        public Task SendAsync(TrainControllerCommandMessage message, CancellationToken cancellationToken)
        {
            _last[message.TrainId] = message;
            return Task.CompletedTask;
        }
    }
}
