using TrainControl.Contracts.Enums;
using static TrackController.Wpf.Tests.UiTestSession;

namespace TrackController.Wpf.Tests;

/// <summary>Verifies the external simulator observes the production controller through module pipes.</summary>
[TestClass]
public sealed class TrackControllerTestUiTests
{
    /// <summary>Cancels the last sent movement even after its draft target changes, preserving other requests.</summary>
    [TestMethod]
    public Task CancelMovementCommand_ChangedDraftTarget_CancelsOnlyLastSentRequest() => RunAsync(async session =>
    {
        var inputs = session.Inputs;
        Assert.IsFalse(inputs.CancelMovementCommand.CanExecute(null));
        await ExecuteAsync(inputs.SendMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1);
        string firstRequest = inputs.Snapshot.Authorizations.Single().RequestId;
        inputs.TargetBlock = "12";
        inputs.RequestedPath = "";
        await ExecuteAsync(inputs.SendMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 2);
        inputs.TargetBlock = "1";
        StringAssert.Contains(inputs.CancelMovementLabel, "12");
        await ExecuteAsync(inputs.CancelMovementCommand);
        await Task.Delay(1250);
        Assert.HasCount(1, inputs.Snapshot.Authorizations);
        Assert.AreEqual(firstRequest, inputs.Snapshot.Authorizations.Single().RequestId);
        Assert.IsFalse(inputs.CancelMovementCommand.CanExecute(null));
    });

    /// <summary>Shows a persistent lock reason, then commands a safe switch without fabricating feedback.</summary>
    [TestMethod]
    public Task SendSwitchCommand_ProtectedThenClear_ReportsRejectionAndSeparateFeedback() => RunAsync(async session =>
    {
        var inputs = session.Inputs;
        var controller = session.Controller;
        await ExecuteAsync(controller.LoadSetupCommand);
        await UntilAsync(() => inputs.Snapshot.PlcStatus.Contains("Program loaded"));
        await ExecuteAsync(inputs.LoadScenarioCommand);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh));
        await ExecuteAsync(controller.PauseResumeCommand);
        await UntilAsync(() => !inputs.Snapshot.IsPaused);
        inputs.RequestedSwitch = SwitchPosition.Reverse;
        await ExecuteAsync(inputs.SendSwitchCommand);
        await UntilAsync(() => inputs.SwitchRequestResult.Contains("block 5 is occupied"));
        await Task.Delay(1250);
        StringAssert.Contains(inputs.SwitchRequestResult, "Rejected");
        StringAssert.Contains(controller.Status, "block 5 is occupied", "Repeated feedback must not erase a rejection.");

        inputs.Scenario = "All blocks clear";
        await ExecuteAsync(inputs.LoadScenarioCommand);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh && block.Occupancy == OccupancyState.Clear));
        await ExecuteAsync(inputs.SendSwitchCommand);
        await UntilAsync(() => inputs.Blocks.Single(block => block.BlockId == "6").CommandedSwitch == SwitchPosition.Reverse);
        Assert.AreEqual(SwitchPosition.Normal, inputs.Blocks.Single(block => block.BlockId == "6").ReportedSwitch);
        StringAssert.Contains(inputs.SwitchRequestResult, "Accepted");
        StringAssert.Contains(inputs.SwitchFeedbackStatus, "Waiting for matching");
        inputs.ReportedSwitch = SwitchPosition.Reverse;
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.Single(block => block.BlockId == "6").ReportedSwitch == SwitchPosition.Reverse);
        Assert.DoesNotContain("Waiting for matching", inputs.SwitchFeedbackStatus);
        inputs.RequestedSwitch = SwitchPosition.Normal;
        await ExecuteAsync(inputs.SendSwitchCommand);
        await UntilAsync(() => inputs.Blocks.Single(block => block.BlockId == "6").CommandedSwitch == SwitchPosition.Normal);
        Assert.AreEqual(SwitchPosition.Reverse, inputs.Blocks.Single(block => block.BlockId == "6").ReportedSwitch);
    });

    /// <summary>Exercises maintenance commands through the operator UI and keeps rejection details visible.</summary>
    [TestMethod]
    public Task SendManualCommand_ProtectedThenClear_ChangesSwitchWhilePausedInMaintenance() => RunAsync(async session =>
    {
        var inputs = session.Inputs;
        var controller = session.Controller;
        await ExecuteAsync(controller.LoadSetupCommand);
        await UntilAsync(() => inputs.Snapshot.PlcStatus.Contains("Program loaded"));
        await ExecuteAsync(inputs.LoadScenarioCommand);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh));
        controller.IsMaintenanceMode = true;
        await UntilAsync(() => inputs.Snapshot.IsMaintenanceMode);
        controller.ManualTarget = "6";
        controller.ManualAction = "Switch reverse";
        await ExecuteAsync(controller.SendManualCommand);
        await UntilAsync(() => controller.ManualCommandResult.Contains("block 5 is occupied"));
        await Task.Delay(1250);
        StringAssert.Contains(controller.ManualCommandResult, "Rejected");

        inputs.Scenario = "All blocks clear";
        await ExecuteAsync(inputs.LoadScenarioCommand);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => controller.Blocks.All(block => block.IsFresh && block.Occupancy == OccupancyState.Clear));
        await ExecuteAsync(controller.SendManualCommand);
        await UntilAsync(() => inputs.Blocks.Single(block => block.BlockId == "6").CommandedSwitch == SwitchPosition.Reverse);
        Assert.IsTrue(inputs.Snapshot.IsPaused);
        Assert.AreEqual(SwitchPosition.Normal, inputs.Blocks.Single(block => block.BlockId == "6").ReportedSwitch);
        StringAssert.Contains(controller.ManualCommandResult, "Accepted");
        await UntilAsync(() => controller.Equipment.Single(equipment => equipment.Equipment == "Switch 6").Status.Contains("Waiting for Reverse feedback"));
    });

    /// <summary>Checks requests, unsent drafts, repeated inputs, and feedback restrictions.</summary>
    [TestMethod]
    public Task SendMovementCommand_ExternalTestUi_ObservesProductionOutput() => RunAsync(async session =>
    {
        var inputs = session.Inputs;
        var controller = session.Controller;
        controller.IsHardwareMode = false;
        await UntilAsync(() => !inputs.Snapshot.IsHardwareMode);
        await ExecuteAsync(inputs.LoadScenarioCommand);
        await Task.Delay(650);
        Assert.IsTrue(inputs.HasUnsentFeedback);
        Assert.IsTrue(inputs.Blocks.All(block => !block.IsFresh), "A preset edits drafts only.");
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh));
        Assert.IsFalse(inputs.HasUnsentFeedback);
        await ExecuteAsync(controller.PauseResumeCommand);
        await UntilAsync(() => !inputs.Snapshot.IsPaused);
        await ExecuteAsync(inputs.SendMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0));
        Assert.AreEqual(300, inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);
        await UntilAsync(() => controller.Snapshot.Authorizations.Count == 1);
        Assert.AreEqual(inputs.Snapshot.Authorizations.Single().RequestId, controller.Snapshot.Authorizations.Single().RequestId);
        inputs.Scenario = "Switch mismatch";
        await ExecuteAsync(inputs.LoadScenarioCommand);
        await Task.Delay(1250);
        Assert.IsTrue(inputs.HasUnsentFeedback);
        Assert.IsGreaterThan(0, inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters == 0);
        StringAssert.Contains(inputs.LastResult, "Withheld");
        inputs.FeedbackRows[0].IsFaulted = true;
        Assert.IsTrue(inputs.HasUnsentFeedback);
        await ExecuteAsync(inputs.ClearInputsCommand);
        Assert.IsFalse(inputs.ShouldRepeatInputs);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 0);
        Assert.IsFalse(controller.Snapshot.IsPaused, "Clearing drafts must not reset the controller.");
    });

    /// <summary>Ensures the Test UI cannot host controller logic or reuse the operator application.</summary>
    [TestMethod]
    public void TestUiAssembly_References_ExcludeControllerImplementations()
    {
        var references = typeof(TestUI.Wpf.ViewModels.MainWindowViewModel).Assembly.GetReferencedAssemblies();
        Assert.IsFalse(references.Any(reference => reference.Name is "TrackController.Core" or "TrackController.Runtime" or "TrackController.Wpf"));
    }

    /// <summary>Ensures disconnected outputs expire and sends have no in-process fallback.</summary>
    [TestMethod]
    public Task SendFeedback_ControllerStopped_ReportsFailureAndExpiresOutput() => RunAsync(async session =>
    {
        session.Inputs.ShouldRepeatInputs = false;
        await session.StopControllerAsync();
        await UntilAsync(() => session.Inputs.LastResult.Contains("No current controller output"));
        await ExecuteAsync(session.Inputs.ApplyFeedbackCommand);
        Assert.IsTrue(session.Inputs.HasUnsentFeedback);
        Assert.DoesNotStartWith("Track feedback sent", session.Inputs.Status);
        Assert.IsTrue(session.Inputs.Blocks.All(block => !block.IsFresh));
    });
}
