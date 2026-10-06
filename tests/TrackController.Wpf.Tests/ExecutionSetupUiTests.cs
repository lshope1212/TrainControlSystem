using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrackController.Tests;
using TrackController.Wpf.Commands;
using TrackController.Wpf.Services;
using TrackController.Wpf.ViewModels;

namespace TrackController.Wpf.Tests;

/// <summary>Exercises ExecutionSetupUi behavior without changing live controller state.</summary>
[TestClass]
public sealed class ExecutionSetupUiTests
{
    /// <summary>Verifies that apply moves execution only after pause and keeps test live deployments separate.</summary>
    [TestMethod]
    public async Task ApplyExecution_RunningOrTestMode_RequiresPauseAndKeepsLiveStateSeparate()
    {
        using var files = new DeploymentFiles();
        await using var server = new PiRuntimeServer(0);
        server.Start();
        await OnDispatcher(async model =>
        {
            await Execute(model.UseTestUiCommand);
            await model.LoadDeploymentAsync(files.PathFor("all-pc.deployment.json"));
            await Until(() => model.Snapshot.WaysideId == "Shared track demonstration" && model.Blocks.Count == 12);
            Assert.HasCount(2, model.ExecutionRows);
            Assert.IsTrue(model.ExecutionRows.All(row => row.AppliedTarget == "PC"));
            var junction = model.ExecutionRows.Single(row => row.Id == "wayside-b");
            var choices = new ComboBox
            {
                DataContext = junction
            };
            choices.SetBinding(ComboBox.ItemsSourceProperty, new Binding(nameof(junction.Targets)));
            choices.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(junction.Target)) { Mode = BindingMode.TwoWay });
            choices.SelectedItem = "Pi";
            junction.Port = server.Port.ToString();
            Assert.IsTrue(model.HasExecutionChanges);
            Assert.AreEqual("PC", junction.AppliedTarget);
            StringAssert.Contains(model.SelectedExecutionTarget, "Local PC");
            Assert.IsTrue(files.Read().Territories.All(territory => territory.Execution == "Local"));
            await Execute(model.ApplyExecutionCommand);
            await Until(() => model.Blocks.Count == 12 && model.Snapshot.IsPaused);
            Assert.IsFalse(model.HasExecutionChanges);
            StringAssert.Contains(model.SelectedExecutionTarget, "Pi 5");
            Assert.AreEqual("Pi", model.ExecutionRows.Single(row => row.Id == "wayside-b").AppliedTarget);
            await Execute(model.SaveExecutionCommand);
            Assert.AreEqual("Pi", files.Read().Territories[1].Execution);
            await Execute(model.LoadScenarioCommand);
            await Execute(model.ApplyFeedbackCommand);
            await Until(() => model.Blocks.All(blockStatus => blockStatus.IsFresh));
            await Execute(model.PauseResumeCommand);
            await Until(() => !model.Snapshot.IsPaused);
            await Execute(model.SendMovementCommand);
            await Until(() => model.Snapshot.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0));
            junction = model.ExecutionRows.Single(row => row.Id == "wayside-b");
            junction.Target = "PC";
            await Execute(model.ApplyExecutionCommand);
            StringAssert.Contains(model.Status, "Pause the selected mode");
            StringAssert.Contains(model.SelectedExecutionTarget, "Pi 5");
            Assert.AreEqual("Pi", model.ExecutionRows.Single(row => row.Id == "wayside-b").AppliedTarget);
            await Execute(model.UseLiveFeedbackCommand);
            Assert.IsFalse(model.IsRoutedDeployment);
            Assert.IsEmpty(model.ExecutionRows);
            await Execute(model.UseTestUiCommand);
            Assert.IsTrue(model.IsRoutedDeployment);
            Assert.IsTrue(model.HasExecutionChanges);
            await Execute(model.ResetTestCommand);
            await Until(() => model.Blocks.Count == 12 && model.Snapshot.IsPaused && model.Snapshot.Authorizations.Count == 0);
            Assert.AreEqual("Pi", model.ExecutionRows.Single(row => row.Id == "wayside-b").AppliedTarget, "Reset keeps applied execution, not an unapplied draft.");
            Assert.IsFalse(model.HasExecutionChanges);
            CollectionAssert.Contains(model.Blocks.Single(blockStatus => blockStatus.BlockId == "3").NextBlockIds.ToArray(), "4");
        });
    }

    private static Task Execute(System.Windows.Input.ICommand command) => ((AsyncRelayCommand)command).ExecuteAsync();
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition())
            await Task.Delay(15, timeout.Token);
    }

    private static async Task OnDispatcher(Func<MainWindowViewModel, Task> action)
    {
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
                    await action(model);
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
            Name = "Execution setup test"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
}
