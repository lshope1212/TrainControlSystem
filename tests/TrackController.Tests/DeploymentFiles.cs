using System.Text.Json.Nodes;
using TrackController.Runtime;

namespace TrackController.Tests;

internal sealed class DeploymentFiles : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "train-territories-" + Guid.NewGuid().ToString("N"));

    /// <summary>Copies deployment samples into a temporary directory for safe test edits.</summary>
    public DeploymentFiles()
    {
        Directory.CreateDirectory(Folder);
        foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "PiSamples")))
        {
            File.Copy(path, Path.Combine(Folder, Path.GetFileName(path)));
        }
    }

    /// <summary>Resolves a fixture filename inside this test’s temporary directory.</summary>
    public string PathFor(string name) => Path.Combine(Folder, name);

    /// <summary>Loads and validates deployment files before any controller connection is opened.</summary>
    public PreparedDeployment Read(string name = "all-pc.deployment.json") => WaysideDeployment.Read(PathFor(name));

    /// <summary>Changes a copied deployment fixture while leaving the repository samples untouched.</summary>
    public void Edit(string name, Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(File.ReadAllText(PathFor(name)))!.AsObject();
        edit(json);
        File.WriteAllText(PathFor(name), json.ToJsonString());
    }

    /// <summary>Removes only the temporary fixture directory created by this test.</summary>
    public void Dispose() => Directory.Delete(Folder, true);
}
