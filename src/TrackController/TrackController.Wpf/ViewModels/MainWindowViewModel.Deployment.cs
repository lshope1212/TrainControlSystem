using TrainControl.Common.Communication;
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
    private ExecutionRow? _selectedPlcTerritory;

    public ExecutionRow? SelectedPlcTerritory
    {
        get => _selectedPlcTerritory;
        set
        {
            if (SetProperty(ref _selectedPlcTerritory, value))
            {
                var territory = _plan?.Territories.SingleOrDefault(item => item.Id == value?.Id);
                PlcSource = territory?.ProgramSource ?? _plan?.ProgramSource ?? PlcSource;
                OnPropertyChanged(nameof(SelectedPlcName));
            }
        }
    }

    public string SelectedPlcName => _plan?.Territories.SingleOrDefault(item => item.Id == SelectedPlcTerritory?.Id)?.ProgramName ?? _plan?.ProgramName ?? _programName;

    private string _programName = "wayside.plc";
    private const string SavedSetupChoice = "Saved setup file…";
    private string _selectedSetup = SetupFiles.Keys.First();

    private static readonly Dictionary<string, string> SetupFiles = new()
    {
        ["Green Line"] = "all-pc.deployment.json",
        ["Blue Line"] = "blue-line-all-pc.deployment.json"
    };

    public static IReadOnlyList<string> SetupChoices { get; } = [.. SetupFiles.Keys, SavedSetupChoice];

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

    public ObservableCollection<ExecutionRow> ExecutionRows { get; } = [];

    public string LayoutDescription => _plan?.LayoutDescription ?? "No line setup loaded. Choose a line above and select Load setup.";

    public string DeploymentFile => _plan?.SourcePath ?? "";

    public bool HasExecutionChanges => _plan is { } plan && ExecutionRows.Any(row =>
        {
            var applied = plan.Territories.Single(territory => territory.Id == row.Id);
            return row.Target != (applied.Execution == "Pi" ? "Pi" : "PC") || row.Host != applied.Host || row.Port != applied.Port.ToString();
        });

    public bool CanEditExecution => IsRoutedDeployment && Snapshot.IsPaused;

    public bool CanApplyExecution => CanEditExecution && HasExecutionChanges;

    public bool CanSaveExecution => IsRoutedDeployment && !HasExecutionChanges;

    public string ExecutionDraftStatus => !IsRoutedDeployment ? "Load a setup to choose where each territory runs." : HasExecutionChanges ? "Unapplied choices · pause, then Apply execution. Controllers still use the applied targets." : "Applied choices · Save applied choices writes placement and PLC assignments to the loaded setup file.";

    private async Task LoadSelectedSetupAsync()
    {
        string path;
        if (SelectedSetup == SavedSetupChoice)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open controller setup: track, territories, PC/Pi assignments and PLC",
                Filter = "Wayside deployments (*.deployment.json)|*.deployment.json|JSON files (*.json)|*.json",
                InitialDirectory = Path.Combine(AppContext.BaseDirectory, "Samples")
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            path = dialog.FileName;
        }
        else
        {
            if (!SetupFiles.TryGetValue(SelectedSetup, out var file))
            {
                throw new ArgumentException("Choose a built-in line or a saved setup file.");
            }
            path = Path.Combine(AppContext.BaseDirectory, "Samples", file);
        }
        await UseDeploymentAsync(WaysideDeployment.Read(path));
    }

    /// <summary>Loads a deployment through the same serialized operation path used by the UI command.</summary>
    public Task LoadDeploymentAsync(string path) => RunAsync(() => UseDeploymentAsync(path is null ? null : WaysideDeployment.Read(path)));

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
        var plan = _plan ?? throw new InvalidOperationException("Load a deployment first.");
        var replacement = plan.WithExecution(ExecutionRows.Select(row => row.ToAssignment()));
        await UseDeploymentAsync(replacement);
    }

    private Task SaveExecutionAsync()
    {
        if (_plan is null || HasExecutionChanges)
        {
            throw new InvalidOperationException("Apply execution choices before saving them.");
        }

        _plan.SaveExecution();
        Status = $"Placement and PLC assignments saved to {_plan.SourcePath}. Original PLC files, map, and ownership are unchanged.";
        return Task.CompletedTask;
    }

    private void ObserveHost()
    {
        _host.StatusChanged += text => Post(() => Connection = text);
        _host.InputReceived += text => Post(() => Status = text);
        _host.AcknowledgementReceived += receipt => Post(() =>
        {
            if (receipt.MessageType == "ApplyManual")
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
            _host = routed is null ? new WaysidePipeHost(_controller, _pipeNames) : new WaysidePipeHost(routed, _pipeNames);
            _plan = plan;
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
        _programName = plan?.ProgramName ?? "wayside.plc";
        string? selectedPlcId = SelectedPlcTerritory?.Id;
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
        SelectedPlcTerritory = ExecutionRows.FirstOrDefault(row => row.Id == selectedPlcId) ?? ExecutionRows.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedPlcName));
        OnPropertyChanged(nameof(IsRoutedDeployment));
        OnPropertyChanged(nameof(CanChooseLogic));
        NotifyExecutionEditor();
        Status = plan is null ? "Standalone controller selected and paused. Check controller logic and send fresh feedback before resuming." : $"{plan.Name} loaded with {string.Join(" / ", plan.Territories.Select(territory => territory.ProgramName ?? plan.ProgramName).Distinct())}. Paused; send fresh feedback before resuming.";
    }

    private async Task ResetControllerAsync()
    {
        var plan = _plan;
        await SendAsync(MessageSerializer.CreateEnvelope("SetSimulation", new { paused = true, speedMultiplier = Snapshot.SpeedMultiplier }));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(3000);
        while (!(await _host.PollForSetupAsync(deadline.Token)).IsPaused)
        {
            await Task.Delay(15, deadline.Token);
        }
        await UseDeploymentAsync(plan);
    }

}
