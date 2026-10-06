using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrackController.Wpf.Commands;
using TrackController.Wpf.Services;
using TrackController.Wpf.ViewModels;

namespace TrackController.Wpf.Tests;

/// <summary>Exercises PiTestUi behavior without changing live controller state.</summary>
[TestClass]
public sealed class PiTestUiTests
{
    /// <summary>Verifies that test ui uses module pipes then owner router then actual pi network receiver.</summary>
    [TestMethod]
    public async Task SendMovementCommand_PiTerritory_UsesPipesAndNetworkReceiver()
    {
        await using var server = new PiRuntimeServer(0);
        server.Start();
        string folder = Path.Combine(Path.GetTempPath(), "train-pi-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (string source in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "PiSamples")))
            File.Copy(source, Path.Combine(folder, Path.GetFileName(source)));
        string planPath = Path.Combine(folder, "pc-and-pi.deployment.json");
        string plan = File.ReadAllText(planPath).Replace("11405", server.Port.ToString());
        File.WriteAllText(planPath, plan);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                MainWindowViewModel? model = null;
                Exception? failure = null;
                try
                {
                    model = new MainWindowViewModel(new TrackControllerService(), new TrackControllerService(), WaysideEndpoints.CreateIsolated());
                    var target = new ComboBox
                    {
                        DataContext = model
                    };
                    target.SetBinding(ComboBox.ItemsSourceProperty, new Binding(nameof(model.BlockIds)));
                    target.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(model.TargetBlock)) { Mode = BindingMode.TwoWay });
                    await ((AsyncRelayCommand)model.UseTestUiCommand).ExecuteAsync();
                    await model.LoadDeploymentAsync(planPath);
                    await Until(() => model.Snapshot.WaysideId == "PC + Pi demonstration");
                    Assert.AreEqual("5", model.TargetBlock, "Deployment replacement must preserve the bound target selection.");
                    Assert.AreEqual("5", target.SelectedItem);
                    Assert.IsTrue(model.IsRoutedDeployment);
                    Assert.IsFalse(model.CanChooseLogic);
                    model.Scenario = "Clear route";
                    await ((AsyncRelayCommand)model.LoadScenarioCommand).ExecuteAsync();
                    await ((AsyncRelayCommand)model.ApplyFeedbackCommand).ExecuteAsync();
                    await Until(() => model.Blocks.All(blockStatus => blockStatus.IsFresh));
                    await ((AsyncRelayCommand)model.PauseResumeCommand).ExecuteAsync();
                    await Until(() => !model.Snapshot.IsPaused);
                    await ((AsyncRelayCommand)model.SendMovementCommand).ExecuteAsync();
                    await Until(() => model.Snapshot.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0));
                    Assert.AreEqual(365.76, model.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);
                    Assert.AreEqual("5", target.SelectedItem, "Published snapshots must not clear the displayed request target.");
                    StringAssert.Contains(model.SelectedExecutionTarget, "Pi 5");
                    model.SelectedBlockId = "1";
                    StringAssert.Contains(model.SelectedExecutionTarget, "Local PC");
                    StringAssert.Contains(model.Connection, "Test pipe transport");
                    model.Scenario = "Switch mismatch";
                    await ((AsyncRelayCommand)model.LoadScenarioCommand).ExecuteAsync();
                    await ((AsyncRelayCommand)model.ApplyFeedbackCommand).ExecuteAsync();
                    await Until(() => model.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters == 0);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    if (model is not null)
                        try
                        {
                            await model.DisposeAsync();
                        }
                        catch (Exception ex)
                        {
                            failure ??= ex;
                        }

                    if (failure is null)
                        finished.TrySetResult();
                    else
                        finished.TrySetException(failure);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Pi test UI dispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(25));
        }
        finally
        {
            if (finished.Task.IsCompleted)
                Directory.Delete(folder, true);
        }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate())
            await Task.Delay(15, timeout.Token);
    }
}
