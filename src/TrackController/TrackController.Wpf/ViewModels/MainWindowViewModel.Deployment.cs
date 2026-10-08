using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using TrackController.Contracts.Messages;
using TrackController.Runtime;
using TrackController.Wpf.Services;

namespace TrackController.Wpf.ViewModels;

/// <summary>Organizes the window commands and their input state.</summary>
public sealed partial class MainWindowViewModel
{
    private PreparedDeployment? _plan;
    private string _programName = "wayside.plc";
    private readonly ObservableCollection<ExecutionRow> _executionRows = [];

    private const string SavedSetupChoice = "Saved setup file…";
    private string _selectedSetup = SampleLines[0].Name;

    /// <summary>Built-in lines loaded with their PLC; other setups can be opened from a deployment file.</summary>
    public static IReadOnlyList<SampleLine> SampleLines { get; } =
    [
        new("Green Line", "all-pc.deployment.json"),
        new("Blue Line", "blue-line-all-pc.deployment.json")
    ];

    public static IReadOnlyList<string> SetupChoices { get; } = [.. SampleLines.Select(line => line.Name), SavedSetupChoice];

    public string SelectedSetup
    {
        get => _selectedSetup;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedSetup, value);
            }
        }
    }

    private PreparedDeployment? ActivePlan => _plan;

    public ObservableCollection<ExecutionRow> ExecutionRows => _executionRows;

    public string LayoutDescription => ActivePlan?.LayoutDescription ?? "No line setup loaded. Choose a line above and select Load setup.";

    public string DeploymentFile => ActivePlan?.SourcePath ?? "";

    public bool HasExecutionChanges => ActivePlan is { } plan && ExecutionRows.Any(row =>
        {
            var applied = plan.Territories.Single(territory => territory.Id == row.Id);
            return row.Target != (applied.Execution == "Pi" ? "Pi" : "PC") || row.Host != applied.Host || row.Port != applied.Port.ToString();
        });

    public bool CanEditExecution => IsRoutedDeployment && Snapshot.IsPaused;

    public bool CanApplyExecution => CanEditExecution && HasExecutionChanges;

    public bool CanSaveExecution => IsRoutedDeployment && !HasExecutionChanges;

    public string ExecutionDraftStatus => !IsRoutedDeployment ? "Load a setup to choose where each territory runs." : HasExecutionChanges ? "Unapplied choices · pause, then Apply execution. Controllers still use the applied targets." : "Applied choices · Save applied choices writes placement to the loaded setup file.";

    private Task LoadSelectedSetupAsync()
    {
        if (SelectedSetup == SavedSetupChoice)
        {
            return ChooseDeploymentAsync();
        }

        var line = SampleLines.SingleOrDefault(line => line.Name == SelectedSetup) ?? throw new ArgumentException("Choose a built-in line or a saved setup file.");
        return ReplaceDeploymentAsync(Path.Combine(AppContext.BaseDirectory, "Samples", line.DeploymentFile));
    }

    private async Task ChooseDeploymentAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open controller setup: track, territories, PC/Pi assignments and PLC",
            Filter = "Wayside deployments (*.deployment.json)|*.deployment.json|JSON files (*.json)|*.json",
            InitialDirectory = Path.Combine(AppContext.BaseDirectory, "Samples")
        };
        if (dialog.ShowDialog() == true)
        {
            await ReplaceDeploymentAsync(dialog.FileName);
        }
    }

    /// <summary>Loads a deployment through the same serialized operation path used by the UI command.</summary>
    public Task LoadDeploymentAsync(string path) => RunAsync(() => ReplaceDeploymentAsync(path));

    private Task ReplaceDeploymentAsync(string? path) => UseDeploymentAsync(path is null ? null : WaysideDeployment.Read(path));

    private void ExecutionEdited(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs) => NotifyExecutionEditor();

    private void NotifyExecutionEditor()
    {
        foreach (string property in new[]
        {
            nameof(ExecutionRows),
            nameof(LayoutDescription),
            nameof(DeploymentFile),
            nameof(HasExecutionChanges),
            nameof(CanEditExecution),
            nameof(CanApplyExecution),
            nameof(CanSaveExecution),
            nameof(ExecutionDraftStatus)
        }

        )
        {
            OnPropertyChanged(property);
        }
    }

    private async Task ApplyExecutionAsync()
    {
        var plan = ActivePlan ?? throw new InvalidOperationException("Load a deployment first.");
        var replacement = plan.WithExecution(ExecutionRows.Select(row => row.ToAssignment())).WithProgram(_programName, PlcSource);
        await UseDeploymentAsync(replacement);
    }

    private Task SaveExecutionAsync()
    {
        if (ActivePlan is null || HasExecutionChanges)
        {
            throw new InvalidOperationException("Apply execution choices before saving them.");
        }

        ActivePlan.SaveExecution();
        Status = $"Execution choices saved to {ActivePlan.SourcePath}. Shared map, ownership, and PLC files are unchanged.";
        return Task.CompletedTask;
    }

    private Task RestoreLocalDemoAsync() => UseDeploymentAsync(null);

    private void ObserveHost()
    {
        _host.StatusChanged += text => Post(() => Connection = text);
        _host.InputReceived += text => Post(() => Status = text);
        _host.AcknowledgementReceived += receipt => Post(() =>
        {
            if (receipt.MessageType == nameof(WaysideManualCommandMessage))
            {
                ManualCommandResult = $"{(receipt.IsAccepted ? "Accepted" : "Rejected")}: {receipt.Detail}";
                OnPropertyChanged(nameof(ManualCommandResult));
            }
        });
    }

    private async Task UseDeploymentAsync(PreparedDeployment? plan)
    {
        var current = await _host.PollForSetupAsync(_lifetime.Token);
        if (!current.IsPaused)
        {
            throw new InvalidOperationException("Pause the controller before changing its deployment or execution.");
        }
        var routed = plan?.CreateRuntime();
        try
        {
            await _host.DisposeAsync();
            _controller.ResetTestState();
            _host = routed is null ? new WaysidePipeHost(_controller, _endpoints) : new WaysidePipeHost(routed, _endpoints);
            _plan = plan;
            _deploymentPath = plan?.SourcePath;
            _deploymentDescription = routed?.Description ?? "Standalone controller · local PC";
            ObserveHost();
            _host.Start();
        }
        catch
        {
            if (routed is not null)
            {
                await routed.DisposeAsync();
            }
            throw;
        }
        PlcSource = plan?.ProgramSource ?? "Select a .plc file to view its Boolean program.";
        _programSource = PlcSource;
        _programName = plan?.ProgramName ?? "wayside.plc";
        foreach (var row in ExecutionRows)
        {
            row.PropertyChanged -= ExecutionEdited;
        }
        ExecutionRows.Clear();
        if (plan is not null)
        {
            foreach (var territory in plan.Territories)
            {
                var row = new ExecutionRow(territory);
                row.PropertyChanged += ExecutionEdited;
                ExecutionRows.Add(row);
            }
        }
        OnPropertyChanged(nameof(IsRoutedDeployment));
        OnPropertyChanged(nameof(CanChooseLogic));
        OnPropertyChanged(nameof(DeploymentDescription));
        NotifyExecutionEditor();
        Status = plan is null ? "Standalone controller selected and paused. Check controller logic and send fresh feedback before resuming." : $"{plan.Name} loaded with {plan.ProgramName}. Paused; send fresh feedback before resuming.";
    }

    private async Task ResetControllerAsync()
    {
        var plan = _plan?.WithProgram(_programName, _programSource);
        await SendAsync(new WaysideSimulationMessage { IsPaused = true, SpeedMultiplier = Snapshot.SpeedMultiplier });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(3000);
        while (!(await _host.PollForSetupAsync(deadline.Token)).IsPaused)
        {
            await Task.Delay(15, deadline.Token);
        }
        await UseDeploymentAsync(plan);
    }

}
