using System.Diagnostics;
using System.IO;
using TrainControlSystem.Launcher.Wpf.Models;

namespace TrainControlSystem.Launcher.Wpf.Services;

/// <summary>
/// Starts subsystem applications as separate operating-system processes.
/// </summary>
/// <remarks>
/// <para>
/// This service is the whole of the launcher's "orchestration" responsibility. It
/// uses <see cref="Process"/> only — it never instantiates a subsystem
/// <c>Window</c>, and the launcher project holds no reference to any subsystem
/// WPF project.
/// </para>
/// <para>
/// <b>Executable path resolution (development-time approach).</b> Visual Studio puts
/// every project in its own <c>bin\{Configuration}\{TargetFramework}</c> folder, so
/// the subsystem executables are not siblings of the launcher executable. Rather
/// than hard-coding "Debug" or a framework moniker, this service:
/// </para>
/// <list type="number">
///   <item>walks up from the launcher's own output folder until it finds the
///         directory containing <c>TrainControlSystem.sln</c> (the repository root);</item>
///   <item>works out the build-output suffix the launcher itself was built into
///         (for example <c>bin\Debug\net10.0-windows</c>) by taking the launcher's
///         output folder relative to the launcher's project folder;</item>
///   <item>applies that same suffix under each module's project directory.</item>
/// </list>
/// <para>
/// This keeps Debug/Release and target-framework changes working with no
/// configuration file. It only works from a build tree — that is fine, because this
/// is a development-time launcher and no deployment tooling exists yet. If the
/// repository root cannot be found, the service falls back to looking for the
/// executable next to the launcher, which is what a future single-output publish
/// layout would produce.
/// </para>
/// </remarks>
public class ModuleLauncherService
{
    private const string SolutionFileName = "TrainControlSystem.sln";
    private const string LauncherProjectDirectory = "src/Launcher/TrainControlSystem.Launcher.Wpf";

    private readonly Dictionary<string, Process> _processes = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// Raised when a process started by this launcher exits. Raised on a background
    /// thread — subscribers that touch UI state must marshal to the UI thread.
    /// </summary>
    public event EventHandler<ModuleInfo>? ModuleExited;

    /// <summary>
    /// True if this launcher started the module and that process is still alive.
    /// Applications started manually outside the launcher are not detected.
    /// </summary>
    public bool IsModuleRunning(ModuleInfo module)
    {
        lock (_gate)
        {
            return _processes.TryGetValue(module.Name, out var process) && !process.HasExited;
        }
    }

    /// <summary>
    /// Starts the module's executable. Returns false and sets <paramref name="error"/>
    /// if the module is already running or its executable could not be found.
    /// </summary>
    public bool LaunchModule(ModuleInfo module, out string? error)
    {
        if (IsModuleRunning(module))
        {
            error = $"{module.Name} is already running.";
            return false;
        }

        var executablePath = ResolveExecutablePath(module);
        if (executablePath is null)
        {
            error = $"Could not find {module.ExecutableName}. Build the whole solution first.";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false
        };

        Process process;
        try
        {
            var started = Process.Start(startInfo);
            if (started is null)
            {
                error = $"{module.Name} could not be started.";
                return false;
            }

            process = started;
        }
        catch (Exception ex)
        {
            error = $"{module.Name} could not be started: {ex.Message}";
            return false;
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            lock (_gate)
            {
                _processes.Remove(module.Name);
            }

            ModuleExited?.Invoke(this, module);
        };

        lock (_gate)
        {
            _processes[module.Name] = process;
        }

        module.IsRunning = true;
        error = null;
        return true;
    }

    /// <summary>
    /// Starts every module that is not already running.
    /// Returns a message for each module that failed to start.
    /// </summary>
    public IReadOnlyList<string> LaunchAll(IEnumerable<ModuleInfo> modules)
    {
        var errors = new List<string>();

        foreach (var module in modules)
        {
            if (IsModuleRunning(module))
            {
                continue;
            }

            if (!LaunchModule(module, out var error) && error is not null)
            {
                errors.Add(error);
            }
        }

        return errors;
    }

    /// <summary>
    /// Resolves the full path of a module's executable, or null if it does not exist.
    /// See the remarks on <see cref="ModuleLauncherService"/> for the strategy.
    /// </summary>
    public string? ResolveExecutablePath(ModuleInfo module)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var repositoryRoot = FindRepositoryRoot(baseDirectory);

        if (repositoryRoot is not null)
        {
            var launcherProject = Path.Combine(repositoryRoot, LauncherProjectDirectory);
            var outputSuffix = Path.GetRelativePath(launcherProject, baseDirectory);

            // Only trust the suffix if the launcher really is running from inside its
            // own project folder (otherwise GetRelativePath walks back out with "..").
            if (!outputSuffix.StartsWith("..", StringComparison.Ordinal))
            {
                var candidate = Path.GetFullPath(Path.Combine(
                    repositoryRoot, module.ProjectDirectory, outputSuffix, module.ExecutableName));

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // Fallback: a flat layout where everything sits next to the launcher.
        var sibling = Path.Combine(baseDirectory, module.ExecutableName);
        return File.Exists(sibling) ? sibling : null;
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
