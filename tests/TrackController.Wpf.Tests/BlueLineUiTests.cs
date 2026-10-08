using TrackController.Runtime;
using TrackController.Tests;
using OperatorModel = TrackController.Wpf.ViewModels.MainWindowViewModel;
using static TrackController.Wpf.Tests.UiTestSession;

namespace TrackController.Wpf.Tests;

/// <summary>Drives the Blue Line through the operator window and the external Test UI over real pipes.</summary>
[TestClass]
public sealed class BlueLineUiTests
{
    /// <summary>Ensures every sample offered by Load sample layout is bundled and loads.</summary>
    [TestMethod]
    public void SampleLines_EachEntry_ReferencesLoadableDeployment()
    {
        Assert.IsTrue(OperatorModel.SampleLines.Any(line => line.Name.StartsWith("Green Line", StringComparison.Ordinal)));
        Assert.IsTrue(OperatorModel.SampleLines.Any(line => line.Name.StartsWith("Blue Line", StringComparison.Ordinal)));
        using var files = new DeploymentFiles();
        foreach (var line in OperatorModel.SampleLines)
        {
            Assert.IsNotEmpty(files.Read(line.DeploymentFile).Territories, line.Name);
        }
    }

    /// <summary>Checks the Blue Line drafts, a granted switch route, and a withheld mismatched switch.</summary>
    [TestMethod]
    public Task SendMovementCommand_BlueLine_UsesBlueDraftsAndSwitchRoute() => RunAsync(async session =>
    {
        using var files = new DeploymentFiles();
        var inputs = session.Inputs;
        var controller = session.Controller;
        controller.IsHardwareMode = false;
        await UntilAsync(() => !inputs.Snapshot.IsHardwareMode);
        await controller.LoadDeploymentAsync(files.PathFor("blue-line-all-pc.deployment.json"));
        await UntilAsync(() => inputs.Snapshot.LineName == "Blue Line" && inputs.Blocks.Count == 15);
        Assert.HasCount(15, controller.Blocks);
        Assert.AreEqual("5", controller.ManualTarget);
        CollectionAssert.AreEqual(new[] { "5" }, inputs.SwitchIds);
        CollectionAssert.AreEqual(new[] { "6", "11" }, inputs.SignalIds);
        CollectionAssert.AreEqual(new[] { "3" }, inputs.CrossingIds);
        Assert.AreEqual("4", inputs.TargetBlock);
        Assert.AreEqual("5, 6, 7", inputs.RequestedPath);
        Assert.AreEqual("5", inputs.SwitchTarget);
        Assert.AreEqual("15", inputs.MaintenanceTarget);
        StringAssert.StartsWith(inputs.ScenarioDescription, "Block 4 occupied; route 5");
        CollectionAssert.AreEquivalent(new[] { "Switch 5", "Signal 6", "Signal 11", "Crossing 3" }, inputs.Equipment.Select(row => row.Equipment).ToArray());

        await ExecuteAsync(inputs.LoadScenarioCommand);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh));
        await ExecuteAsync(controller.PauseResumeCommand);
        await UntilAsync(() => !inputs.Snapshot.IsPaused);
        await ExecuteAsync(inputs.SendMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0));
        Assert.AreEqual(200, inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);

        inputs.Scenario = "Switch mismatch";
        await ExecuteAsync(inputs.LoadScenarioCommand);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters == 0);
        StringAssert.Contains(inputs.LastResult, "Withheld");
    });
}
