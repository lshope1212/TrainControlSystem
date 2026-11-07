using CTC.Core.Exceptions;
using CTC.Wpf.Commands;
using CTC.Wpf.Services;
using CTC.Wpf.ViewModels;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.Wpf.Tests;

/// <summary>
/// CTC's clock controls only SEND requests; what they display always comes from the shared
/// clock's authoritative state.
/// </summary>
[TestClass]
public class SimulationClockViewModelTests
{
    private sealed class RecordingClockSender : ISimulationClockControlSender
    {
        public List<SimulationTimeControlMessage> Requests { get; } = new List<SimulationTimeControlMessage>();

        public Exception? ExceptionToThrow { get; set; }

        public Task SendAsync(SimulationTimeControlMessage request, CancellationToken cancellationToken = default)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private static Task ExecuteAsync(System.Windows.Input.ICommand command, object? parameter = null) =>
        ((AsyncRelayCommand)command).ExecuteAsync(parameter);

    private static SystemTimeMessage State(double speed, bool paused) =>
        new SystemTimeMessage { SystemTime = new TimeSpan(12, 15, 42), SpeedMultiplier = speed, IsPaused = paused, Sequence = 1 };

    [TestMethod]
    public void BeforeAnyClockState_ShowsNotConnected()
    {
        var clock = new SimulationClockViewModel(new RecordingClockSender());

        Assert.IsFalse(clock.IsClockConnected);
        Assert.IsTrue(clock.IsSimulationPaused);
        Assert.AreEqual("Clock not connected", clock.ClockStatusDisplay);
        Assert.AreEqual("--", clock.SimulationSpeedDisplay);
    }

    [TestMethod]
    public async Task SpeedButton_SendsSetSpeed_ButDisplayWaitsForTheClock()
    {
        var sender = new RecordingClockSender();
        var clock = new SimulationClockViewModel(sender);
        clock.ApplyClockState(State(speed: 1.0, paused: false));

        await ExecuteAsync(clock.SetSimulationSpeedCommand, "5");

        var request = sender.Requests.Single();
        Assert.AreEqual(SimulationClockCommand.SetSpeed, request.Command);
        Assert.AreEqual(5.0, request.SpeedMultiplier);
        Assert.AreEqual("1x", clock.SimulationSpeedDisplay, "CTC must not pretend the request already succeeded.");

        clock.ApplyClockState(State(speed: 5.0, paused: false));
        Assert.AreEqual("5x", clock.SimulationSpeedDisplay);
        Assert.AreEqual("Running", clock.ClockStatusDisplay);
    }

    [TestMethod]
    public async Task PauseAndRun_SendRequests_AndStateFollowsTheClock()
    {
        var sender = new RecordingClockSender();
        var clock = new SimulationClockViewModel(sender);
        clock.ApplyClockState(State(speed: 2.0, paused: false));

        Assert.IsTrue(clock.PauseSimulationCommand.CanExecute(null));
        Assert.IsFalse(clock.ResumeSimulationCommand.CanExecute(null));
        await ExecuteAsync(clock.PauseSimulationCommand);
        Assert.IsFalse(clock.IsSimulationPaused);

        clock.ApplyClockState(State(speed: 2.0, paused: true));
        Assert.AreEqual("Paused", clock.ClockStatusDisplay);
        Assert.IsTrue(clock.ResumeSimulationCommand.CanExecute(null));
        await ExecuteAsync(clock.ResumeSimulationCommand);

        CollectionAssert.AreEqual(
            new[] { SimulationClockCommand.Pause, SimulationClockCommand.Run },
            sender.Requests.Select(request => request.Command).ToArray());
    }

    [TestMethod]
    public async Task SetTime_SendsTheTypedTime_AndRejectsBadInputWithoutSending()
    {
        var sender = new RecordingClockSender();
        var clock = new SimulationClockViewModel(sender);

        clock.EditedTime = "25:99";
        await ExecuteAsync(clock.SetSimulationTimeCommand);
        Assert.IsEmpty(sender.Requests);
        StringAssert.Contains(clock.ControlStatus, "HH:mm:ss");

        clock.EditedTime = "06:30:00";
        await ExecuteAsync(clock.SetSimulationTimeCommand);
        await ExecuteAsync(clock.ResetSimulationTimeCommand);

        Assert.AreEqual(SimulationClockCommand.SetTime, sender.Requests[0].Command);
        Assert.AreEqual(new TimeSpan(6, 30, 0), sender.Requests[0].SystemTime);
        Assert.AreEqual(SimulationClockCommand.Reset, sender.Requests[1].Command);
    }

    [TestMethod]
    public async Task UnreachableClock_IsReported_AndNothingChanges()
    {
        var sender = new RecordingClockSender { ExceptionToThrow = new MessageSendException("The simulation clock is not running.") };
        var clock = new SimulationClockViewModel(sender);

        await ExecuteAsync(clock.SetSimulationSpeedCommand, 10.0);

        StringAssert.Contains(clock.ControlStatus, "The simulation clock is not running.");
        Assert.AreEqual("Clock not connected", clock.ClockStatusDisplay);
    }
}
