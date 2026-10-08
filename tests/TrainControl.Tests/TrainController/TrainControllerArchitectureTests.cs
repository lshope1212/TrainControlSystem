using System.Xml.Linq;

namespace TrainControl.Tests.TrainController;

/// <summary>
/// Guards the Train Controller project graph. In particular the Raspberry Pi Hardware
/// controller must implement the requirements independently and therefore must never
/// (directly or transitively) reference TrainController.Core.
/// </summary>
[TestClass]
public class TrainControllerArchitectureTests
{
    private const string Abstractions = "TrainController.Abstractions";
    private const string Core = "TrainController.Core";
    private const string Integration = "TrainController.Integration";
    private const string Pi = "TrainController.Hardware.Pi";
    private const string Wpf = "TrainController.Wpf";

    [TestMethod]
    public void HardwarePi_DoesNotReferenceSoftwareCore_EvenTransitively()
    {
        var closure = ReferenceClosure(Pi);

        CollectionAssert.DoesNotContain(closure, Core);
        CollectionAssert.DoesNotContain(closure, Integration);
        CollectionAssert.DoesNotContain(closure, Wpf);
        CollectionAssert.Contains(closure, Abstractions);
    }

    [TestMethod]
    public void Abstractions_HasNoControlOrInfrastructureDependencies()
    {
        var closure = ReferenceClosure(Abstractions);

        CollectionAssert.DoesNotContain(closure, Core);
        CollectionAssert.DoesNotContain(closure, Integration);
        CollectionAssert.DoesNotContain(closure, Wpf);
        CollectionAssert.DoesNotContain(closure, Pi);
    }

    [TestMethod]
    public void Core_DoesNotDependOnIntegrationOrUi()
    {
        var closure = ReferenceClosure(Core);

        CollectionAssert.DoesNotContain(closure, Integration);
        CollectionAssert.DoesNotContain(closure, Wpf);
        CollectionAssert.DoesNotContain(closure, Pi);
    }

    [TestMethod]
    public void Integration_DoesNotDependOnUiOrPi()
    {
        var closure = ReferenceClosure(Integration);

        CollectionAssert.DoesNotContain(closure, Wpf);
        CollectionAssert.DoesNotContain(closure, Pi);
    }

    private static List<string> ReferenceClosure(string projectName)
    {
        var root = FindRepositoryRoot();
        var start = Path.Combine(root, "src", "TrainController", projectName, projectName + ".csproj");
        Assert.IsTrue(File.Exists(start), $"Project file not found: {start}");

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(start));

        while (pending.Count > 0)
        {
            var path = pending.Pop();
            var directory = Path.GetDirectoryName(path)!;

            foreach (var include in XDocument.Load(path).Descendants("ProjectReference").Select(e => (string?)e.Attribute("Include")))
            {
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                var referenced = Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));
                if (visited.Add(referenced))
                {
                    pending.Push(referenced);
                }
            }
        }

        return visited.Select(path => Path.GetFileNameWithoutExtension(path)).ToList();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TrainControlSystem.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate TrainControlSystem.sln above the test output folder.");
        return directory.FullName;
    }
}
