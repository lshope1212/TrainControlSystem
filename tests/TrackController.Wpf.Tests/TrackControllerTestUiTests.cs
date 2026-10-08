using static TrackController.Wpf.Tests.UiTestSession;

namespace TrackController.Wpf.Tests;

/// <summary>Verifies the external simulator observes the production controller through module pipes.</summary>
[TestClass]
public sealed class TrackControllerTestUiTests
{
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
        Assert.AreEqual(365.76, inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);
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
