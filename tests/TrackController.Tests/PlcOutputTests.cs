using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Models;
using TrackController.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Tests;

/// <summary>Verifies Boolean PLC outputs drive commands and cannot bypass protection.</summary>
[TestClass]
public sealed class PlcOutputTests
{
    private static TrackControllerService Create(string source, string occupied = "5")
    {
        var controller = new TrackControllerService(WaysideConfiguration.CreateDemo());
        controller.LoadPlc("showcase.plc", source);
        controller.ApplyFeedback(PiRuntimeTests.Clear(controller.GetConfiguration(), occupied));
        controller.SetSimulation(false, 1);
        return controller;
    }

    [TestMethod]
    [DataRow("allow_route")]
    [DataRow("speed_enable")]
    [DataRow("authority_enable")]
    [DataRow("signal_clear")]
    public void Movement_OutputFalse_WithholdsBothNumericChannels(string output)
    {
        var controller = Create(PiRuntimeTests.ProgramSource.Replace($"{output} = movement_enabled", $"{output} = false"));
        controller.SubmitMovement(PiRuntimeTests.Movement());
        var authorization = controller.GetSnapshot().Authorizations.Single();
        Assert.AreEqual(0d, authorization.AuthorizedSpeedMetersPerSecond);
        Assert.AreEqual(0d, authorization.AuthorizedAuthorityMeters);
        Assert.AreEqual("Withheld", authorization.MovementStatus);
    }

    [TestMethod]
    public void Signal_YellowOutput_CommandsCautionAndCapsSpeed()
    {
        var controller = Create(PiRuntimeTests.ProgramSource.Replace("signal_yellow = signal_clear and signal_in and (authority_short or yellow_in)", "signal_yellow = true"));
        controller.SubmitMovement(PiRuntimeTests.Movement());
        var snapshot = controller.GetSnapshot();
        Assert.AreEqual(SignalState.Yellow, snapshot.Blocks.Single(block => block.BlockId == "6").CommandedSignal);
        Assert.AreEqual(5d, snapshot.Authorizations.Single().AuthorizedSpeedMetersPerSecond);
        Assert.IsGreaterThan(0d, snapshot.Authorizations.Single().AuthorizedAuthorityMeters);
    }

    [TestMethod]
    public void Switch_MoveOutputFalse_RejectsOtherwiseSafeSwitchRequest()
    {
        var controller = Create(PiRuntimeTests.ProgramSource.Replace("switch_move = switch_request_known and zone_ok and not movement_locked and (running or maintenance)", "switch_move = false"), "none");
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => controller.RequestSwitch(new SwitchPositionRequestMessage { BlockId = "6", RequestedPosition = SwitchPosition.Reverse }));
        StringAssert.Contains(exception.Message, "PLC withholds switch movement");
        Assert.AreEqual(SwitchPosition.Normal, controller.GetSnapshot().Blocks.Single(block => block.BlockId == "6").CommandedSwitch);
    }

    [TestMethod]
    public void Crossing_OutputControlsClearZone_ButCannotOpenOccupiedApproach()
    {
        var closed = Create(PiRuntimeTests.ProgramSource.Replace("crossing_close = crossing_needed or active_route or manual_crossing_close or not running or maintenance", "crossing_close = true"), "none");
        Assert.AreEqual(CrossingState.Closed, closed.GetSnapshot().Blocks.Single(block => block.BlockId == "8").CommandedCrossing);
        string openProgram = PiRuntimeTests.ProgramSource.Replace("crossing_close = crossing_needed or active_route or manual_crossing_close or not running or maintenance", "crossing_close = false");
        var open = Create(openProgram, "none");
        Assert.AreEqual(CrossingState.Open, open.GetSnapshot().Blocks.Single(block => block.BlockId == "8").CommandedCrossing);
        var protectedCrossing = Create(openProgram, "7");
        Assert.AreEqual(CrossingState.Closed, protectedCrossing.GetSnapshot().Blocks.Single(block => block.BlockId == "8").CommandedCrossing);
    }

    [TestMethod]
    public void Compile_FloatingPointOutput_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => VitalPlcProgram.Compile(PiRuntimeTests.ProgramSource.Replace("speed_enable = movement_enabled", "speed_enable = 2.5")));
    }
}
