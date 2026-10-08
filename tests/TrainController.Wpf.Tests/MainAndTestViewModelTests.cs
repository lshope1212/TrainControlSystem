using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Integration;
using TrainController.Integration.Presentation;
using TrainController.Wpf.ViewModels;

namespace TrainController.Wpf.Tests;

[TestClass]
public class MainAndTestViewModelTests
{
    private static (TrainControllerSubsystem Subsystem, MainWindowViewModel Main, TestWindowViewModel Test) Build()
    {
        var subsystem = TrainControllerSubsystem.Create();
        return (subsystem, new MainWindowViewModel(subsystem), new TestWindowViewModel(subsystem));
    }

    private static TrainChoice Choice(IReadOnlyList<TrainChoice> trains, string id) => trains.Single(t => t.TrainId == id);

    [TestMethod]
    public void MainDropdown_ListsTenTrains_WithFixedControllerType()
    {
        var (_, main, _) = Build();

        CollectionAssert.AreEqual(TrainFleet.AllTrainIds.ToArray(), main.Trains.Select(t => t.TrainId).ToArray());
        foreach (var train in main.Trains)
        {
            Assert.AreEqual(TrainFleet.GetControllerType(train.TrainId), train.ControllerType);
        }

        Assert.AreEqual("TRAIN-002  ·  Hardware", Choice(main.Trains, "TRAIN-002").Display);
    }

    [TestMethod]
    public void MainUi_ShowsControllerSource_AndHardwareStatusForHardwareTrains()
    {
        var (_, main, _) = Build();

        main.SelectedTrain = Choice(main.Trains, "TRAIN-001");
        Assert.AreEqual("SOFTWARE CONTROLLED", main.Status.ControllerSource);
        Assert.AreEqual("Not applicable", main.Status.HardwareLink.Text);

        main.SelectedTrain = Choice(main.Trains, "TRAIN-010");
        Assert.AreEqual("HARDWARE CONTROLLED", main.Status.ControllerSource);
        Assert.AreEqual("Disconnected", main.Status.HardwareLink.Text);
    }

    [TestMethod]
    public void MainAndTestSelections_AreIndependent()
    {
        var (_, main, test) = Build();

        main.SelectedTrain = Choice(main.Trains, "TRAIN-001");
        test.SelectedTrain = Choice(test.Trains, "TRAIN-004");

        Assert.AreEqual("TRAIN-001", main.SelectedTrainId);
        Assert.AreEqual("TRAIN-004", test.SelectedTrainId);

        test.SelectedTrain = Choice(test.Trains, "TRAIN-007");
        Assert.AreEqual("TRAIN-001", main.SelectedTrainId);
    }

    [TestMethod]
    public void MainUi_DriverEdits_GoToSelectedTrainOnly_InSi()
    {
        var (subsystem, main, _) = Build();

        main.SelectedTrain = Choice(main.Trains, "TRAIN-003");
        main.IsManual = true;
        main.RequestedSpeedText = "20";
        main.ApplyRequestedSpeed();
        main.ServiceBrakeRequested = true;
        main.EmergencyBrakeCommand.Execute(null);

        var t3 = subsystem.Registry.Get("TRAIN-003").Driver;
        Assert.AreEqual(8.9408, t3.RequestedSpeedMetersPerSecond, 1e-4, "20 mph stored as m/s.");
        Assert.IsTrue(t3.ServiceBrakeRequested);
        Assert.IsTrue(t3.EmergencyBrakePressPending);

        var t1 = subsystem.Registry.Get("TRAIN-001").Driver;
        Assert.AreEqual(0.0, t1.RequestedSpeedMetersPerSecond);
        Assert.IsFalse(t1.ServiceBrakeRequested);
        Assert.IsFalse(t1.EmergencyBrakePressPending);

        main.IsAutomatic = true;
        Assert.AreEqual(OperatingMode.Automatic, t3.Mode);
        Assert.AreEqual(OperatingMode.Manual, t1.Mode);
    }

    [TestMethod]
    public void MainUi_EngineerGains_ArePerTrain_AndValidated()
    {
        var (subsystem, main, _) = Build();
        main.SelectedTrain = Choice(main.Trains, "TRAIN-005");

        main.KpText = "-1";
        main.KiText = "0";
        main.ApplyGains();
        Assert.AreEqual(1.0, subsystem.Registry.Get("TRAIN-005").Engineer.Kp, "Invalid gains rejected.");
        Assert.AreNotEqual(string.Empty, main.GainsMessage);

        main.KpText = "12000";
        main.KiText = "250";
        main.ApplyGains();
        Assert.AreEqual(12_000.0, subsystem.Registry.Get("TRAIN-005").Engineer.Kp);
        Assert.AreEqual(250.0, subsystem.Registry.Get("TRAIN-005").Engineer.Ki);
        Assert.AreEqual(1.0, subsystem.Registry.Get("TRAIN-006").Engineer.Kp);

        main.SelectedTrain = Choice(main.Trains, "TRAIN-006");
        Assert.AreEqual("1.000", main.KpText, "Editor reloads the newly selected train's gains.");
    }

    [TestMethod]
    public async Task ChangingSelections_DoesNotStopOtherTrains()
    {
        var (subsystem, main, test) = Build();
        test.IsTestMode = true;

        foreach (var id in new[] { "TRAIN-001", "TRAIN-002" })
        {
            test.SelectedTrain = Choice(test.Trains, id);
            test.IsActive = true;
            test.AuthorizedSpeedMphText = "20";
            test.RemainingAuthorityFeetText = "5000";
        }

        main.SelectedTrain = Choice(main.Trains, "TRAIN-009");
        test.SelectedTrain = Choice(test.Trains, "TRAIN-007");
        await test.StepAsync();

        Assert.AreEqual(1L, subsystem.Registry.Get("TRAIN-001").LastOutput!.TickId);
        Assert.AreEqual(1L, subsystem.Registry.Get("TRAIN-002").LastOutput!.TickId);
        Assert.IsNull(subsystem.Registry.Get("TRAIN-009").LastOutput, "Selected but not dispatched: not run.");
        Assert.IsNull(subsystem.Registry.Get("TRAIN-007").LastOutput);
    }

    [TestMethod]
    public void TestUi_ExposesOnlyTrainModelIo_NoDriverOrEngineerControls()
    {
        string[] forbidden =
        {
            "KpText", "KiText", "ApplyGainsCommand", "RequestedSpeedText", "ApplyRequestedSpeedCommand", "IsManual", "IsAutomatic",
            "ServiceBrakeRequested", "EmergencyBrakeCommand", "EmergencyBrakeResetCommand",
            "LeftDoorsOpenRequested", "RightDoorsOpenRequested", "ExteriorLightsRequested", "CabinSetpointText",
            "AnnouncementText", "AnnounceCommand",
        };

        var properties = typeof(TestWindowViewModel).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, properties, $"Test UI must not expose Driver/Engineer control '{name}'.");
        }

        // Main UI owns those controls.
        var mainProperties = typeof(MainWindowViewModel).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var name in new[] { "KpText", "KiText", "RequestedSpeedText", "ApplyRequestedSpeedCommand", "EmergencyBrakeCommand", "EmergencyBrakeResetCommand", "AnnouncementText", "AnnounceCommand" })
        {
            Assert.Contains(name, mainProperties, $"Main UI must expose '{name}'.");
        }
    }

    [TestMethod]
    public void TestMode_IsGlobal_AndVisibleInMainUi()
    {
        var (subsystem, main, test) = Build();

        test.IsTestMode = true;
        main.Refresh();

        Assert.IsTrue(subsystem.TestMode.IsTestMode);
        StringAssert.Contains(main.InputSourceText, "TEST");

        test.IsNormalMode = true;
        main.Refresh();
        Assert.IsFalse(subsystem.TestMode.IsTestMode);
        StringAssert.Contains(main.InputSourceText, "TRAIN MODEL");
    }

    [TestMethod]
    public void TestUi_InputsAreImperial_StoredInSi_InvalidTextRejected()
    {
        var (subsystem, _, test) = Build();
        test.SelectedTrain = Choice(test.Trains, "TRAIN-004");

        test.ActualSpeedMphText = "22";
        test.RemainingAuthorityFeetText = "1200";
        test.CabinTemperatureFahrenheitText = "68";

        var model = subsystem.Registry.Get("TRAIN-004").TestModel;
        Assert.AreEqual(9.83488, model.ActualSpeedMetersPerSecond, 1e-4);
        Assert.AreEqual(365.76, model.RemainingAuthorityMeters, 1e-6);
        Assert.AreEqual(20.0, model.CabinTemperatureCelsius, 1e-9);

        test.ActualSpeedMphText = "fast";
        Assert.AreNotEqual(string.Empty, test.InputError);
        Assert.AreEqual(9.83488, model.ActualSpeedMetersPerSecond, 1e-4, "Invalid text leaves the value unchanged.");
    }

    [TestMethod]
    public void TestUi_RunAndStep_RequireTestMode()
    {
        var (_, _, test) = Build();

        Assert.IsFalse(test.Run());
        Assert.IsFalse(test.RunCommand.CanExecute(null));
        Assert.IsFalse(test.StepCommand.CanExecute(null));

        test.IsTestMode = true;
        Assert.IsTrue(test.StepCommand.CanExecute(null));
    }

    [TestMethod]
    public void TestUi_SpeedMultiplierChoices()
    {
        var (subsystem, _, test) = Build();

        CollectionAssert.AreEqual(new[] { 1, 2, 5, 10 }, test.SpeedMultipliers.ToArray());
        test.SelectedSpeedMultiplier = 5;
        Assert.AreEqual(5, subsystem.Simulation.SpeedMultiplier);
        Assert.AreEqual(subsystem.Options.SimulationTimeStepSeconds, subsystem.Simulation.DeltaTimeSeconds);
    }

    [TestMethod]
    public void RequestedSpeed_IsNumericMphInput_StoredAsMetersPerSecond_InvalidRejected()
    {
        var (subsystem, main, _) = Build();
        main.SelectedTrain = Choice(main.Trains, "TRAIN-001");
        main.IsManual = true;
        var driver = subsystem.Registry.Get("TRAIN-001").Driver;

        main.RequestedSpeedText = "30.0";
        main.ApplyRequestedSpeed();
        Assert.AreEqual(13.4112, driver.RequestedSpeedMetersPerSecond, 1e-9);

        foreach (var bad in new[] { "abc", "-5", "" })
        {
            main.RequestedSpeedText = bad;
            main.ApplyRequestedSpeed();
            Assert.AreEqual(13.4112, driver.RequestedSpeedMetersPerSecond, 1e-9, $"'{bad}' must not change the stored value.");
            Assert.AreEqual(DisplayTone.Danger, main.RequestedSpeedMessage.Tone);
        }

        main.RequestedSpeedText = "50";
        main.ApplyRequestedSpeed();
        Assert.AreEqual(DisplayTone.Warning, main.RequestedSpeedMessage.Tone, "Above nominal max: warned.");
    }

    [TestMethod]
    public void RequestedSpeed_IsDisabledInAutomaticMode()
    {
        var (subsystem, main, _) = Build();
        main.SelectedTrain = Choice(main.Trains, "TRAIN-001");
        main.IsAutomatic = true;

        Assert.IsFalse(main.ApplyRequestedSpeedCommand.CanExecute(null));

        main.RequestedSpeedText = "25";
        main.ApplyRequestedSpeed();
        Assert.AreEqual(0.0, subsystem.Registry.Get("TRAIN-001").Driver.RequestedSpeedMetersPerSecond);

        main.IsManual = true;
        Assert.IsTrue(main.ApplyRequestedSpeedCommand.CanExecute(null));
    }

    [TestMethod]
    public void RequestedSpeed_SwitchingTrainsLoadsEachTrainsOwnValue()
    {
        var (_, main, _) = Build();

        main.SelectedTrain = Choice(main.Trains, "TRAIN-003");
        main.IsManual = true;
        main.RequestedSpeedText = "25";
        main.ApplyRequestedSpeed();

        main.SelectedTrain = Choice(main.Trains, "TRAIN-005");
        Assert.AreEqual("0.0", main.RequestedSpeedText);

        main.SelectedTrain = Choice(main.Trains, "TRAIN-003");
        Assert.AreEqual("25.0", main.RequestedSpeedText);
    }

    [TestMethod]
    public void RequestedSpeed_PeriodicRefresh_DoesNotOverwriteTyping()
    {
        var (_, main, _) = Build();
        main.SelectedTrain = Choice(main.Trains, "TRAIN-001");
        main.IsManual = true;

        main.RequestedSpeedText = "3";   // user is part-way through typing "33"
        main.Refresh();
        main.Refresh();

        Assert.AreEqual("3", main.RequestedSpeedText);
    }

    [TestMethod]
    public void DriverAnnouncement_GoesToSelectedTrainOnly_AndClearsTheBox()
    {
        var (subsystem, main, _) = Build();
        main.SelectedTrain = Choice(main.Trains, "TRAIN-003");
        main.IsAutomatic = true; // available in Automatic too

        main.AnnouncementText = "Next stop is the last stop.";
        Assert.IsTrue(main.AnnounceCommand.CanExecute(null));
        main.Announce();

        Assert.AreEqual("Next stop is the last stop.", subsystem.Registry.Get("TRAIN-003").Driver.AnnouncementPending);
        Assert.AreEqual(string.Empty, subsystem.Registry.Get("TRAIN-001").Driver.AnnouncementPending);
        Assert.AreEqual(string.Empty, main.AnnouncementText);
        StringAssert.Contains(main.Status.PendingAnnouncement, "Next stop is the last stop.");
        Assert.IsFalse(main.AnnounceCommand.CanExecute(null), "Nothing to send once cleared.");
    }
}
