using TrackController.Runtime;
using TrackController.Tests;
using TrainControl.Contracts.Enums;
using OperatorModel = TrackController.Wpf.ViewModels.MainWindowViewModel;
using static TrackController.Wpf.Tests.UiTestSession;

namespace TrackController.Wpf.Tests;

/// <summary>Drives the Blue Line through the operator window and the external Test UI over real pipes.</summary>
[TestClass]
[DoNotParallelize]
public sealed class BlueLineUiTests
{
    /// <summary>Combines Blue Line presets with CTC and manual switch commands, preserving separate actual feedback.</summary>
    [TestMethod]
    public Task SendSwitchCommand_BlueLineAllClear_UsesSwitchFiveAndReportsFeedback() => RunAsync(async session =>
    {
        var inputs = session.Inputs;
        var controller = session.Controller;
        controller.SelectedSetup = "Blue Line";
        Assert.AreEqual("Green Line", inputs.Snapshot.LineName, "Choosing a setup must not apply it before Load setup.");
        await ExecuteAsync(controller.LoadSetupCommand);
        await UntilAsync(() => inputs.Snapshot.LineName == "Blue Line" && inputs.Blocks.Count == 15);
        Assert.IsTrue(inputs.Snapshot.IsHardwareMode);
        StringAssert.Contains(inputs.Snapshot.PlcStatus, "Program loaded");
        StringAssert.Contains(controller.Status, "loaded with wayside.plc");
        await ExecuteAsync(inputs.LoadScenarioCommand);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh));
        await ExecuteAsync(controller.PauseResumeCommand);
        await UntilAsync(() => !inputs.Snapshot.IsPaused);
        Assert.IsFalse(controller.LoadSetupCommand.CanExecute(null), "Pause before replacing a running setup.");
        inputs.RequestedSwitch = SwitchPosition.Reverse;
        await ExecuteAsync(inputs.SendSwitchCommand);
        await UntilAsync(() => inputs.SwitchRequestResult.Contains("block 4 is occupied"));

        inputs.Scenario = "All blocks clear";
        await ExecuteAsync(inputs.LoadScenarioCommand);
        Assert.IsTrue(inputs.FeedbackRows.All(row => row.Occupancy == OccupancyState.Clear && row.IsReserved == false));
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh && block.Occupancy == OccupancyState.Clear));
        await ExecuteAsync(inputs.SendSwitchCommand);
        await UntilAsync(() => inputs.Blocks.Single(block => block.BlockId == "5").CommandedSwitch == SwitchPosition.Reverse);
        Assert.AreEqual(SwitchPosition.Normal, inputs.Blocks.Single(block => block.BlockId == "5").ReportedSwitch);
        inputs.ReportedSwitch = SwitchPosition.Reverse;
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.Single(block => block.BlockId == "5").ReportedSwitch == SwitchPosition.Reverse);

        controller.IsMaintenanceMode = true;
        await UntilAsync(() => inputs.Snapshot.IsMaintenanceMode);
        Assert.AreEqual("5", controller.ManualTarget);
        controller.ManualAction = "Switch normal";
        await ExecuteAsync(controller.SendManualCommand);
        await UntilAsync(() => inputs.Blocks.Single(block => block.BlockId == "5").CommandedSwitch == SwitchPosition.Normal);
        Assert.AreEqual(SwitchPosition.Reverse, inputs.Blocks.Single(block => block.BlockId == "5").ReportedSwitch);
        StringAssert.Contains(controller.ManualCommandResult, "Accepted");
    });

    /// <summary>Preserves cancellation's original target and clears that target when a new line replaces the layout.</summary>
    [TestMethod]
    public Task CancelMovementCommand_BlueLineThenGreen_PreservesTargetAndResetsWithLayout() => RunAsync(async session =>
    {
        var inputs = session.Inputs;
        var controller = session.Controller;
        controller.SelectedSetup = "Blue Line";
        await ExecuteAsync(controller.LoadSetupCommand);
        await UntilAsync(() => inputs.Snapshot.LineName == "Blue Line" && inputs.Blocks.Count == 15);
        await ExecuteAsync(inputs.SendMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1);
        inputs.TargetBlock = "15";
        StringAssert.Contains(inputs.CancelMovementLabel, "block 4");
        await ExecuteAsync(inputs.CancelMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 0);
        Assert.IsFalse(inputs.CancelMovementCommand.CanExecute(null));

        inputs.TargetBlock = "4";
        await ExecuteAsync(inputs.SendMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1);
        bool descriptionUpdated = false;
        inputs.PropertyChanged += (_, change) => descriptionUpdated |= change.PropertyName == nameof(inputs.ScenarioDescription);
        controller.SelectedSetup = "Green Line";
        await ExecuteAsync(controller.LoadSetupCommand);
        await UntilAsync(() => inputs.Snapshot.LineName == "Green Line" && inputs.Blocks.Count == 12);
        Assert.IsTrue(descriptionUpdated);
        Assert.IsFalse(inputs.CancelMovementCommand.CanExecute(null));
        Assert.AreEqual("5", inputs.TargetBlock);
        Assert.AreEqual("6, 7, 8", inputs.RequestedPath);
        Assert.AreEqual("6", controller.ManualTarget);
        StringAssert.StartsWith(inputs.ScenarioDescription, "Block 5 occupied");
    });

    /// <summary>Ensures every built-in line is bundled with a loadable setup.</summary>
    [TestMethod]
    public Task SetupChoices_EachBuiltInLine_LoadsThroughCommand() => RunAsync(async session =>
    {
        CollectionAssert.Contains(OperatorModel.SetupChoices.ToArray(), "Green Line");
        CollectionAssert.Contains(OperatorModel.SetupChoices.ToArray(), "Blue Line");
        Assert.AreEqual("Saved setup file…", OperatorModel.SetupChoices.Last());
        foreach (string line in OperatorModel.SetupChoices.SkipLast(1))
        {
            session.Controller.SelectedSetup = line;
            await ExecuteAsync(session.Controller.LoadSetupCommand);
            Assert.IsTrue(session.Controller.IsRoutedDeployment);
            Assert.IsNotEmpty(session.Controller.ExecutionRows);
            Assert.AreEqual(line, session.Controller.Snapshot.LineName);
            Assert.IsTrue(session.Controller.Snapshot.IsPaused);
        }
    });

    /// <summary>Ensures each known line gets its own header color and unknown lines stay neutral.</summary>
    [TestMethod]
    public void For_LineName_ReturnsDistinctColorPerLine()
    {
        var blue = (System.Windows.Media.SolidColorBrush)TrackController.Wpf.ViewModels.LineAccent.For("Blue Line");
        var green = (System.Windows.Media.SolidColorBrush)TrackController.Wpf.ViewModels.LineAccent.For("Green Line");
        var other = (System.Windows.Media.SolidColorBrush)TrackController.Wpf.ViewModels.LineAccent.For("Violet Line");
        Assert.AreNotEqual(blue.Color, green.Color);
        Assert.AreNotEqual(blue.Color, other.Color);
        Assert.AreEqual(other.Color, ((System.Windows.Media.SolidColorBrush)TrackController.Wpf.ViewModels.LineAccent.For("Preferred Line")).Color);
        Assert.AreEqual(other.Color,((System.Windows.Media.SolidColorBrush)TrackController.Wpf.ViewModels.LineAccent.For(null)).Color);
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
        Assert.AreEqual(150, inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);

        inputs.FeedbackSignal = "6";
        inputs.ReportedSignal = SignalState.SuperGreen;
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.Single(block => block.BlockId == "6").ReportedSignal == SignalState.SuperGreen);
        await UntilAsync(() => controller.Equipment.Single(row => row.Equipment == "Signal 6").Feedback == "Super Green");
        Assert.IsGreaterThan(0, inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters);
        Assert.AreEqual("TargetBlockEntry", inputs.Snapshot.Authorizations.Single().Request!.AuthorityReference);

        inputs.Scenario = "Switch mismatch";
        await ExecuteAsync(inputs.LoadScenarioCommand);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters == 0);
        StringAssert.Contains(inputs.LastResult, "Withheld");
    });
}
