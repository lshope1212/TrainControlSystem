using System.Windows.Threading;
using TrackController.Core.Services;
using TrackController.Wpf.Commands;
using TrackController.Wpf.Services;
using TrackController.Wpf.ViewModels;

namespace TrackController.Wpf.Tests;

/// <summary>Exercises TrackControllerTestUi behavior without changing live controller state.</summary>
[TestClass]
public sealed class TrackControllerTestUiTests
{
    /// <summary>Verifies that actual test ui commands drive controller and observe published outputs.</summary>
    [TestMethod]
    public async Task SendMovementCommand_TestMode_ObservesPublishedControllerOutput()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    // Only addresses are injected. This runs the actual application's command/view-model code.
                    model = new MainWindowViewModel(new TrackControllerService(), new TrackControllerService(), WaysideEndpoints.CreateIsolated());
                    Assert.IsTrue(model.IsLive);
                    Assert.IsFalse(model.ApplyFeedbackCommand.CanExecute(null));
                    await ((AsyncRelayCommand)model.UseTestUiCommand).ExecuteAsync();
                    Assert.IsTrue(model.IsTest);
                    Assert.AreEqual(3, model.TabIndex);
                    model.IsHardwareMode = false;
                    await UntilAsync(() => !model.Snapshot.IsHardwareMode);
                    model.Scenario = "Clear route";
                    await ((AsyncRelayCommand)model.LoadScenarioCommand).ExecuteAsync();
                    await Task.Delay(650);
                    Assert.IsTrue(model.HasUnsentFeedback);
                    Assert.IsTrue(model.Blocks.All(blockStatus => !blockStatus.IsFresh), "Loading a preset must not send feedback.");
                    await ((AsyncRelayCommand)model.ApplyFeedbackCommand).ExecuteAsync();
                    await UntilAsync(() => model.Blocks.All(blockStatus => blockStatus.IsFresh));
                    Assert.IsFalse(model.HasUnsentFeedback);
                    await ((AsyncRelayCommand)model.PauseResumeCommand).ExecuteAsync();
                    await UntilAsync(() => !model.Snapshot.IsPaused);
                    await ((AsyncRelayCommand)model.SendMovementCommand).ExecuteAsync();
                    await UntilAsync(() => model.Snapshot.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0));
                    Assert.AreEqual(365.76, model.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);
                    StringAssert.Contains(model.Connection, "Test pipe transport");
                    model.Scenario = "Switch mismatch";
                    await ((AsyncRelayCommand)model.LoadScenarioCommand).ExecuteAsync();
                    await Task.Delay(1250); // A repeat must use the sent values, never the unsent preset.
                    Assert.IsTrue(model.HasUnsentFeedback);
                    Assert.IsGreaterThan(0, model.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters);
                    await ((AsyncRelayCommand)model.ApplyFeedbackCommand).ExecuteAsync();
                    await UntilAsync(() => model.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters == 0);
                    StringAssert.Contains(model.LastResult, "Withheld");
                    model.FeedbackRows[0].IsFaulted = true;
                    Assert.IsTrue(model.HasUnsentFeedback, "Custom edits must be marked as unsent too.");
                    await ((AsyncRelayCommand)model.UseLiveFeedbackCommand).ExecuteAsync();
                    Assert.IsTrue(model.IsLive);
                    Assert.AreEqual(0, model.TabIndex);
                    Assert.IsFalse(model.SendMovementCommand.CanExecute(null));
                    Assert.IsEmpty(model.Snapshot.Authorizations, "Test requests must not appear in live mode.");
                    await ((AsyncRelayCommand)model.UseTestUiCommand).ExecuteAsync();
                    await UntilAsync(() => model.Snapshot.IsPaused);
                    Assert.AreEqual(3, model.TabIndex);
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
                        completed.TrySetResult();
                    else
                        completed.TrySetException(failure);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Track controller test UI dispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate())
            await Task.Delay(15, deadline.Token);
    }
}
