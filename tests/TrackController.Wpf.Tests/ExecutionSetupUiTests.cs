using System.Windows.Controls;
using System.Windows.Data;
using TrackController.Runtime;
using TrackController.Tests;
using static TrackController.Wpf.Tests.UiTestSession;

namespace TrackController.Wpf.Tests;

/// <summary>Exercises operator deployment changes while a separate simulator supplies inputs.</summary>
[TestClass]
public sealed class ExecutionSetupUiTests
{
    /// <summary>Checks paused placement changes, saving, and resetting an applied PC/Pi deployment.</summary>
    [TestMethod]
    public async Task ApplyExecution_RunningController_RequiresPauseAndRetainsPlacement()
    {
        using var files = new DeploymentFiles();
        await using var server = new PiRuntimeServer(0);
        server.Start();
        await RunAsync(async session =>
        {
            var controller = session.Controller;
            var inputs = session.Inputs;
            await controller.LoadDeploymentAsync(files.PathFor("all-pc.deployment.json"));
            await UntilAsync(() => inputs.Snapshot.WaysideId == "Shared track demonstration" && inputs.Blocks.Count == 12);
            Assert.HasCount(2, controller.ExecutionRows);
            var junction = controller.ExecutionRows.Single(row => row.Id == "wayside-b");
            var choices = new ComboBox { DataContext = junction };
            choices.SetBinding(ComboBox.ItemsSourceProperty, new Binding(nameof(junction.Targets)));
            choices.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(junction.Target)) { Mode = BindingMode.TwoWay });
            choices.SelectedItem = "Pi";
            junction.Port = server.Port.ToString();
            Assert.IsTrue(controller.HasExecutionChanges);
            Assert.AreEqual("PC", junction.AppliedTarget);
            Assert.IsTrue(files.Read().Territories.All(territory => territory.Execution == "Local"));
            await ExecuteAsync(controller.ApplyExecutionCommand);
            Assert.IsFalse(controller.HasExecutionChanges);
            StringAssert.Contains(controller.SelectedExecutionTarget, "Pi 5");
            await ExecuteAsync(controller.SaveExecutionCommand);
            Assert.AreEqual("Pi", files.Read().Territories[1].Execution);
            await ExecuteAsync(inputs.LoadScenarioCommand);
            await ExecuteAsync(inputs.ApplyFeedbackCommand);
            await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh));
            await ExecuteAsync(controller.PauseResumeCommand);
            await UntilAsync(() => !inputs.Snapshot.IsPaused);
            await ExecuteAsync(inputs.SendMovementCommand);
            await UntilAsync(() => inputs.Snapshot.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0));
            controller.ExecutionRows.Single(row => row.Id == "wayside-b").Target = "PC";
            await ExecuteAsync(controller.ApplyExecutionCommand);
            StringAssert.Contains(controller.Status, "Pause the controller");
            Assert.AreEqual("Pi", controller.ExecutionRows.Single(row => row.Id == "wayside-b").AppliedTarget);
            await ExecuteAsync(inputs.ClearInputsCommand);
            await ExecuteAsync(controller.ResetControllerCommand);
            await UntilAsync(() => inputs.Blocks.Count == 12 && inputs.Snapshot.IsPaused && inputs.Snapshot.Authorizations.Count == 0 && inputs.Blocks.All(block => !block.IsFresh));
            Assert.AreEqual("Pi", controller.ExecutionRows.Single(row => row.Id == "wayside-b").AppliedTarget);
            Assert.IsFalse(controller.HasExecutionChanges);
            CollectionAssert.Contains(inputs.Blocks.Single(block => block.BlockId == "3").NextBlockIds.ToArray(), "4");
        });
    }
}
