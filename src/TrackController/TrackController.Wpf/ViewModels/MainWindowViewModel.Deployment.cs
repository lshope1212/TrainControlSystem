using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using TrackController.Contracts.Messages;
using TrackController.Runtime;
using TrackController.Wpf.Services;

namespace TrackController.Wpf.ViewModels;

/// <summary>Binds operator controls and observed outputs to the live and isolated test hosts.</summary>
public sealed partial class MainWindowViewModel
{
    private PreparedDeployment? _livePlan, _testPlan;
    private string _liveProgramName = "wayside.plc", _testProgramName = "wayside.plc";
    private readonly ObservableCollection<ExecutionRow> _liveExecutionRows = [], _testExecutionRows = [];
    private PreparedDeployment? ActivePlan => IsTest ? _testPlan : _livePlan;
    public ObservableCollection<ExecutionRow> ExecutionRows => IsTest ? _testExecutionRows : _liveExecutionRows;
    public string LayoutDescription => ActivePlan?.LayoutDescription ?? "Load a deployment to separate track layout, territory ownership, and execution.";
    public string DeploymentFile => ActivePlan?.SourcePath ?? "";
    public bool HasExecutionChanges => ActivePlan is { } plan && ExecutionRows.Any(row =>
    {
        var applied = plan.Territories.Single(territory => territory.Id == row.Id);
        return row.Target != (applied.Execution == "Pi" ? "Pi" : "PC") || row.Host != applied.Host || row.Port != applied.Port.ToString();
    });
    public bool CanEditExecution => IsRoutedDeployment && Snapshot.IsPaused;
    public bool CanApplyExecution => CanEditExecution && HasExecutionChanges;
    public bool CanSaveExecution => IsRoutedDeployment && !HasExecutionChanges;
    public string ExecutionDraftStatus => !IsRoutedDeployment ? "Load sample layout to start with both territories on the PC." : HasExecutionChanges ? "Unapplied choices · pause, then Apply execution. Controllers still use the applied targets." : "Applied choices · Save applied choices writes placement to the loaded deployment file.";

    private void ObserveLiveHost()
    {
        _host.StatusChanged += text => Post(() => Connection = text);
        _host.InputReceived += text => Post(() =>
        {
            if (!IsTest)
                Status = text;
        });
    }

    private async Task ChooseDeploymentAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load track, territories, and execution",
            Filter = "Wayside deployments (*.deployment.json)|*.deployment.json|JSON files (*.json)|*.json",
            InitialDirectory = Path.Combine(AppContext.BaseDirectory, "Samples")
        };
        if (dialog.ShowDialog() == true)
            await ReplaceDeploymentAsync(dialog.FileName);
    }

    /// <summary>Loads a deployment through the same serialized operation path used by the UI command.</summary>
    public Task LoadDeploymentAsync(string path) => RunAsync(() => ReplaceDeploymentAsync(path));
    private Task ReplaceDeploymentAsync(string? path) => UseDeploymentAsync(path is null ? null : WaysideDeployment.Read(path));
    private async Task UseDeploymentAsync(PreparedDeployment? plan)
    {
        // Check current runtime state, not a potentially delayed UI snapshot after Resume.
        var current = await (IsTest ? _testHost : _host).PollForSetupAsync(_lifetime.Token);
        if (!current.IsPaused)
            throw new InvalidOperationException("Pause the selected mode before changing its deployment or execution.");
        var routed = plan?.CreateRuntime();
        try
        {
            if (IsTest)
            {
                await _testHost.DisposeAsync();
                _test.ResetTestState();
                _testConnection.ClearObservations();
                _testHost = routed is null ? new WaysidePipeHost(_test, _testConnection.Endpoints) : new WaysidePipeHost(routed, _testConnection.Endpoints);
                _testPlan = plan;
                _testDeploymentPath = plan?.SourcePath;
                _testDeploymentDescription = routed?.Description ?? "Local PC preview · all blocks";
                _testHost.Start();
            }
            else
            {
                await _host.DisposeAsync();
                _live.ResetTestState();
                _host = routed is null ? new WaysidePipeHost(_live, _liveEndpoints) : new WaysidePipeHost(routed, _liveEndpoints);
                _livePlan = plan;
                _liveDeploymentPath = plan?.SourcePath;
                _liveDeploymentDescription = routed?.Description ?? "Local PC preview · all blocks";
                ObserveLiveHost();
                _host.Start();
            }
        }
        catch
        {
            if (routed is not null)
                await routed.DisposeAsync();
            throw;
        }

        _appliedFeedback = null;
        _appliedRequest = null;
        _hasSentFeedback = false;
        MarkFeedbackDirty();
        LastReceipt = "Deployment replaced. Awaiting controller receipts.";
        PlcSource = plan?.ProgramSource ?? "Select a .plc file to view its Boolean program.";
        if (IsTest)
        {
            _testPlcSource = PlcSource;
            _testProgramName = plan?.ProgramName ?? "wayside.plc";
        }
        else
        {
            _livePlcSource = PlcSource;
            _liveProgramName = plan?.ProgramName ?? "wayside.plc";
        }

        foreach (var row in ExecutionRows)
            row.PropertyChanged -= ExecutionEdited;
        ExecutionRows.Clear();
        if (plan is not null)
            foreach (var territory in plan.Territories)
            {
                var row = new ExecutionRow(territory);
                row.PropertyChanged += ExecutionEdited;
                ExecutionRows.Add(row);
            }

        foreach (string property in new[]
        {
            nameof(IsRoutedDeployment),
            nameof(CanChooseLogic),
            nameof(DeploymentDescription),
            nameof(LastReceipt)
        }

        )
            OnPropertyChanged(property);
        NotifyExecutionEditor();
        Status = "Deployment applied and paused. Send fresh feedback before resuming. Territory ownership is independent of PC/Pi execution.";
    }

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
            OnPropertyChanged(property);
    }

    private async Task ApplyExecutionAsync()
    {
        var plan = ActivePlan ?? throw new InvalidOperationException("Load a deployment first.");
        var replacement = plan.WithExecution(ExecutionRows.Select(row => row.ToAssignment())).WithProgram(IsTest ? _testProgramName : _liveProgramName, PlcSource);
        await UseDeploymentAsync(replacement);
    }

    private Task SaveExecutionAsync()
    {
        if (ActivePlan is null || HasExecutionChanges)
            throw new InvalidOperationException("Apply execution choices before saving them.");
        ActivePlan.SaveExecution();
        Status = $"Execution choices saved to {ActivePlan.SourcePath}. Shared map, ownership, and PLC files are unchanged.";
        return Task.CompletedTask;
    }

    private Task RestoreLocalDemoAsync() => UseDeploymentAsync(null);
    private async Task ResetTestAsync()
    {
        var resetPlan = _testPlan?.WithProgram(_testProgramName, _testPlcSource);
        await SendAsync(new WaysideSimulationMessage { IsPaused = true, SpeedMultiplier = Snapshot.SpeedMultiplier });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(3000);
        while (!(await _testHost.PollForSetupAsync(deadline.Token)).IsPaused)
            await Task.Delay(15, deadline.Token);
        if (resetPlan is not null)
            await UseDeploymentAsync(resetPlan);
        else
        {
            await _testHost.DisposeAsync();
            _test.ResetTestState();
            _testConnection.ClearObservations();
            _testHost = new WaysidePipeHost(_test, _testConnection.Endpoints);
            _testHost.Start();
            _appliedFeedback = null;
            _appliedRequest = null;
            _hasSentFeedback = false;
        }

        LastReceipt = "Test reset. Awaiting controller receipts.";
        OnPropertyChanged(nameof(LastReceipt));
        CreateFeedbackRows();
        Status = "Test reset and paused. Applied execution and PLC choices retained; send fresh feedback before resuming.";
    }
}
