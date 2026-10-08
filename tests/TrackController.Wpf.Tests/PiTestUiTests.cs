using System.Windows.Controls;
using System.Windows.Data;
using TrackController.Runtime;
using TrackController.Tests;
using static TrackController.Wpf.Tests.UiTestSession;

namespace TrackController.Wpf.Tests;

/// <summary>Verifies external test commands cross the real pipe and Pi network receiver.</summary>
[TestClass]
public sealed class PiTestUiTests
{
    /// <summary>Checks Pi execution, selection bindings, and restricted movement feedback.</summary>
    [TestMethod]
    public async Task SendMovementCommand_PiTerritory_UsesPipesAndNetworkReceiver()
    {
        using var files = new DeploymentFiles();
        await using var server = new PiRuntimeServer(0);
        server.Start();
        string planPath = files.PathFor("pc-and-pi.deployment.json");
        File.WriteAllText(planPath, File.ReadAllText(planPath).Replace("11405", server.Port.ToString()));
        await RunAsync(async session =>
        {
            var inputs = session.Inputs;
            var controller = session.Controller;
            var target = new ComboBox { DataContext = inputs };
            target.SetBinding(ComboBox.ItemsSourceProperty, new Binding(nameof(inputs.BlockIds)));
            target.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(inputs.TargetBlock)) { Mode = BindingMode.TwoWay });
            await controller.LoadDeploymentAsync(planPath);
            await UntilAsync(() => inputs.Snapshot.WaysideId == "PC + Pi demonstration");
            Assert.AreEqual("5", inputs.TargetBlock);
            Assert.AreEqual("5", target.SelectedItem);
            Assert.IsTrue(controller.IsRoutedDeployment);
            Assert.IsFalse(controller.CanChooseLogic);
            await ExecuteAsync(inputs.LoadScenarioCommand);
            await ExecuteAsync(inputs.ApplyFeedbackCommand);
            await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh));
            await ExecuteAsync(controller.PauseResumeCommand);
            await UntilAsync(() => !inputs.Snapshot.IsPaused);
            await ExecuteAsync(inputs.SendMovementCommand);
            await UntilAsync(() => inputs.Snapshot.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0));
            Assert.AreEqual(300, inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);
            Assert.AreEqual("5", target.SelectedItem);
            StringAssert.Contains(controller.SelectedExecutionTarget, "Pi 5");
            controller.SelectedBlockId = "1";
            StringAssert.Contains(controller.SelectedExecutionTarget, "Local PC");
            inputs.Scenario = "Switch mismatch";
            await ExecuteAsync(inputs.LoadScenarioCommand);
            await ExecuteAsync(inputs.ApplyFeedbackCommand);
            await UntilAsync(() => inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters == 0);
        });
    }
}
