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
    public void DispatchedTrainsGrid_ShowsLastKnownNotCurrentBlock()
    {
        string xaml = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "MainWindow.xaml"));

        Assert.Contains("Header=\"Last Known Block\"", xaml);
        Assert.DoesNotContain("Current Block", xaml);
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
    public void ScheduleBuilder_HasClearTemplateButton()
    {
        string xaml = File.ReadAllText(RepoPath("src", "CTC", "CTC.Wpf", "MainWindow.xaml"));

        Assert.Contains("Command=\"{Binding ClearTemplateCommand}\"", xaml);
    }
}
