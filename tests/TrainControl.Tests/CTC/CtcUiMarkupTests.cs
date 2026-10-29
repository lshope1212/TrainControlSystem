namespace TrainControl.Tests.CTC;

/// <summary>
/// The WPF projects are not referenced by this test project, so these checks read their
/// source files to guard the dispatcher-facing wording and the removed TestUI input.
/// </summary>
[TestClass]
public class CtcUiMarkupTests
{
    private static string RepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TrainControlSystem.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate the repository root.");
        return Path.Combine([directory.FullName, .. parts]);
    }

    [TestMethod]
    public void TestUi_HasNoTrainAuthorizationInput()
    {
        string testUi = RepoPath("src", "CTC", "CTC.TestUI.Wpf");

        Assert.DoesNotContain("TrainAuthorization", File.ReadAllText(Path.Combine(testUi, "MainWindow.xaml")));
        Assert.DoesNotContain("TrainAuthorization", File.ReadAllText(Path.Combine(testUi, "ViewModels", "MainWindowViewModel.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(testUi, "ViewModels", "TrainAuthorizationInputViewModel.cs")));
    }

    [TestMethod]
    public void DispatchedTrainsGrid_ShowsSuggestedNotAuthorizedValues()
    {
        string xaml = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "MainWindow.xaml"));

        Assert.Contains("Header=\"Suggested Speed\"", xaml);
        Assert.Contains("Header=\"Suggested Authority\"", xaml);
        Assert.DoesNotContain("Authorized Speed", xaml);
    }

    [TestMethod]
    public void DispatchedTrainsGrid_ShowsInferredCurrentBlock()
    {
        string xaml = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "MainWindow.xaml"));

        Assert.Contains("Header=\"Current Block\"", xaml);
        Assert.Contains("{Binding CurrentBlock}", xaml);
        Assert.Contains("inferred from sequential track occupancy", xaml);
        Assert.DoesNotContain("Last Known Block", xaml);
        Assert.DoesNotContain("not updated yet", xaml);
    }

    [TestMethod]
    public void TestUi_OccupancySimulator_SendsNoTrainIdentity()
    {
        string testUi = RepoPath("src", "CTC", "CTC.TestUI.Wpf");
        string simulator = File.ReadAllText(Path.Combine(testUi, "ViewModels", "OccupancySequenceSimulatorViewModel.cs"));

        // It must imitate anonymous track circuits only, so CTC's inference is what is exercised.
        Assert.Contains("new BlockStatusMessage", simulator);
        Assert.DoesNotContain("TrainId", simulator);
        Assert.Contains("OccupancySequenceSimulator.NextStepCommand", File.ReadAllText(Path.Combine(testUi, "MainWindow.xaml")));
    }

    [TestMethod]
    public void DispatchedTrains_EditOnlyDestinationAndArrivalTime()
    {
        string xaml = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "MainWindow.xaml"));

        Assert.Contains("SelectedItem=\"{Binding SelectedDispatchedTrain}\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding EditedDestinationBlockId}\"", xaml);
        Assert.Contains("{Binding EditedArrivalTime", xaml);
        Assert.Contains("Command=\"{Binding ApplyTrainPlanCommand}\"", xaml);
        Assert.Contains("Header=\"Destination\"", xaml);
        Assert.Contains("Header=\"Arrival Time\"", xaml);

        // Speed and authority are calculated by CTC, never typed in.
        Assert.DoesNotContain("EditedSuggestedSpeed", xaml);
        Assert.DoesNotContain("EditedSuggestedAuthority", xaml);
    }

    [TestMethod]
    public void TrainGrids_ShowTrainDisplayName()
    {
        string xaml = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "MainWindow.xaml"));

        Assert.DoesNotContain("{Binding TrainId}", xaml);
        Assert.Contains("{Binding TrainDisplayName}", xaml);
    }

    [TestMethod]
    public void TrackSpeedLimits_AreShownInMph()
    {
        string xaml = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "MainWindow.xaml"));

        Assert.Contains("SelectedBlock.SpeedLimitMilesPerHour", xaml);
        Assert.Contains("Header=\"Speed Limit (mph)\"", xaml);
        Assert.DoesNotContain("km/h", xaml);
        Assert.DoesNotContain("SpeedLimitKilometersPerHour", xaml);
    }

    [TestMethod]
    public void TrackLengths_AreShownInFeet()
    {
        string xaml = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "MainWindow.xaml"));

        Assert.Contains("Header=\"Length (ft)\"", xaml);
        Assert.Contains("{Binding LengthFeet", xaml);
        Assert.DoesNotContain("Length (m)", xaml);
        Assert.DoesNotContain("LengthMeters", xaml);
    }

    [TestMethod]
    public void ScheduleBuilder_TrainColumnHeadersSayArrivalTime()
    {
        string behavior = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "Behaviors", "DataGridTrainColumns.cs"));

        Assert.Contains("$\"{TrainIds.DisplayName(trainId)} Arrival Time\"", behavior);
    }

    [TestMethod]
    public void ScheduleBuilder_HasDwellColumnPerTrain()
    {
        string behavior = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "Behaviors", "DataGridTrainColumns.cs"));

        Assert.Contains("$\"{TrainIds.DisplayName(trainId)} Dwell (sec)\"", behavior);
        Assert.Contains("TrainTimes[{column}].DwellText", behavior);
    }

    [TestMethod]
    public void ActiveMovementPlan_ShowsAndEditsDwellTime()
    {
        string xaml = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "MainWindow.xaml"));

        Assert.Contains("{Binding EditedDwellTime", xaml);
        Assert.Contains("ItemsSource=\"{Binding SelectedDispatchedTrain.Stops}\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedStop}\"", xaml);
        Assert.Contains("Header=\"Dwell (sec)\"", xaml);
        Assert.Contains("Header=\"Departure Time\"", xaml);
    }

    [TestMethod]
    public void ScheduleBuilder_HasClearTemplateButton()
    {
        string xaml = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "MainWindow.xaml"));

        Assert.Contains("Command=\"{Binding ClearTemplateCommand}\"", xaml);
    }
}
