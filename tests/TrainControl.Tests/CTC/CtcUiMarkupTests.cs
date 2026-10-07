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
}
