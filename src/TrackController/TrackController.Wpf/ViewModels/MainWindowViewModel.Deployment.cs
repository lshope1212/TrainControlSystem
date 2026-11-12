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
    private bool _hasLoadedDeployment;
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
                OnPropertyChanged(nameof(PlcSummary));
            }
        }
    }

    public string SelectedPlcName => _plan?.Territories.SingleOrDefault(item => item.Id == SelectedPlcTerritory?.Id)?.ProgramName ?? _plan?.ProgramName ?? _programName;

    private string _programName = "wayside.plc";
    private const string SavedSetupChoice = "Saved setup file…";
    private string _selectedSetup = SetupFiles.Keys.First();

    /// <summary>Built-in setups by the line name their deployment reports. The first is selected by default.</summary>
    private static readonly Dictionary<string, string> SetupFiles = new()
    {
        ["Course network · 16 waysides"] = "course-territories-all-pc.deployment.json",
        ["Blue Line"] = "blue-line-all-pc.deployment.json",
        ["Red Line"] = "red-line-all-pc.deployment.json",
        ["Green Line"] = "green-line-all-pc.deployment.json"
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

    private const string AllSetupLines = "All lines";
    private string _selectedSetupLine = AllSetupLines;

    public IReadOnlyList<string> SetupLines => [AllSetupLines, .. ExecutionRows.Select(row => row.Line).Distinct()];
    public IReadOnlyList<ExecutionRow> FilteredExecutionRows => ExecutionRows
        .Where(row => SelectedSetupLine == AllSetupLines || row.Line == SelectedSetupLine).ToArray();

    public string SelectedSetupLine
    {
        get => _selectedSetupLine;
        set
        {
            if (value is not null && SetupLines.Contains(value) && SetProperty(ref _selectedSetupLine, value))
                RefreshSetupTerritories(SelectedPlcTerritory?.Id);
        }
    }

    private void RefreshSetupTerritories(string? preferredId)
    {
        var rows = FilteredExecutionRows;
        OnPropertyChanged(nameof(FilteredExecutionRows));
        SelectedPlcTerritory = rows.FirstOrDefault(row => row.Id == preferredId) ?? rows.FirstOrDefault();
    }

    public sealed record MapTerritory(string Id, string Name, string Line, IReadOnlySet<string> Blocks);
    private IReadOnlyList<MapTerritory> _mapTerritories = [];
    private string _selectedMapLine = "";
    private MapTerritory? _selectedMapTerritory;
    public IReadOnlyList<string> MapLines => _mapTerritories.Select(item => item.Line).Distinct().ToArray();
    public IReadOnlyList<MapTerritory> MapTerritories => _mapTerritories.Where(item => item.Line == SelectedMapLine).ToArray();
    public string SelectedMapLine
    {
        get => _selectedMapLine;
        set
        {
            if (SetProperty(ref _selectedMapLine, value))
            {
                OnPropertyChanged(nameof(MapTerritories));
                SelectedMapTerritory = MapTerritories.FirstOrDefault();
                OnPropertyChanged(nameof(LineAccentBrush));
            }
        }
    }
    public MapTerritory? SelectedMapTerritory
    {
        get => _selectedMapTerritory;
        set
        {
            if (SetProperty(ref _selectedMapTerritory, value))
            {
                NotifyMap();
                if (value is not null && !value.Blocks.Contains(ManualTarget))
                    ManualTarget = MapBlocks.FirstOrDefault(block => block.HasSwitch)?.BlockId ?? value.Blocks.FirstOrDefault() ?? "";
                if (value is not null && !value.Blocks.Contains(SelectedBlockId))
                    SelectedBlockId = value.Blocks.FirstOrDefault() ?? "";
            }
        }
    }
    public IReadOnlyList<WaysideBlockStatus> MapBlocks => Blocks.Where(block => SelectedMapTerritory?.Blocks.Contains(block.BlockId) == true).ToArray();
    public IReadOnlyList<MovementRow> TerritoryMovements => Movements.Where(row => SelectedMapTerritory?.Blocks.Contains(row.Target) == true).ToArray();
    public IReadOnlyList<EquipmentRow> TerritoryEquipment => Equipment.Where(row => SelectedMapTerritory?.Blocks.Contains(row.Block) == true).ToArray();
    public string[] MapBlockIds => MapBlocks.Select(block => block.BlockId).ToArray();
    public string MapDescription => SelectedMapTerritory is { } territory
        ? $"{territory.Blocks.Count} owned blocks · {MapBlocks.Sum(block => block.LengthMeters) / 1000:0.0} km · {MapBlocks.Count(block => block.HasSwitch)} switches · {MapBlocks.Count(block => block.HasCrossing)} crossings"
        : "Load a controller setup.";
    public string MapHandoffs
    {
        get
        {
            if (SelectedMapTerritory is not { } territory) return "";
            var blocks = Blocks.ToDictionary(block => block.BlockId);
            var links = new List<string>();
            var seen = new HashSet<string>();
            foreach (var from in Blocks)
            foreach (string to in from.NextBlockIds)
            {
                bool outgoing = territory.Blocks.Contains(from.BlockId);
                if (outgoing == territory.Blocks.Contains(to)) continue;
                string inside = outgoing ? from.BlockId : to, outside = outgoing ? to : from.BlockId;
                if (!seen.Add(inside + ":" + outside)) continue;
                string peer = _mapTerritories.FirstOrDefault(item => item.Blocks.Contains(outside))?.Name.Split('·')[0].Trim() ?? "Unassigned";
                string arrow = blocks.TryGetValue(to, out var other) && other.NextBlockIds.Contains(from.BlockId) ? "↔" : outgoing ? "→" : "←";
                links.Add($"{inside.Split('-')[^1]} {arrow} {outside.Split('-')[^1]} ({peer})");
            }
            return links.Count == 0 ? "No territory handoffs." : "Handoffs: " + string.Join("   ·   ", links);
        }
    }
    private void NotifyMap()
    {
        foreach (string property in new[] { nameof(MapBlocks), nameof(MapBlockIds), nameof(MapDescription), nameof(MapHandoffs), nameof(TerritoryMovements), nameof(TerritoryEquipment) })
            OnPropertyChanged(property);
    }
    private void IndexMapLines(PreparedDeployment? plan)
    {
        _mapTerritories = plan is null
            ? [new(Snapshot.WaysideId, "Standalone territory", Snapshot.LineName, Blocks.Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal))]
            : plan.Territories.Select(territory => new MapTerritory(territory.Id, territory.Name, territory.Configuration.LineName,
                TrackController.Core.Models.WaysideConfiguration.GetOwnedIds(territory.Configuration))).ToArray();
        var preferred = _mapTerritories.FirstOrDefault(item => item.Blocks.Contains(SelectedBlockId)) ?? _mapTerritories[0];
        _selectedMapLine = preferred.Line;
        OnPropertyChanged(nameof(MapLines));
        OnPropertyChanged(nameof(SelectedMapLine));
        OnPropertyChanged(nameof(MapTerritories));
        SelectedMapTerritory = preferred;
    }
    public string LayoutDescription => _plan is { } plan ? $"{plan.Name} · {plan.Territories.Count} waysides · {plan.Territories.Sum(item => item.Configuration.OwnedBlockIds?.Count ?? item.Configuration.Blocks.Count)} blocks" : "Standalone test controller. Load the course network for integration.";

    public string DeploymentFile => _plan?.SourcePath ?? "";

    public bool HasExecutionChanges => _plan is { } plan && ExecutionRows.Any(row =>
        {
            var applied = plan.Territories.Single(territory => territory.Id == row.Id);
            return row.Target != (applied.Execution == "Pi" ? "Pi" : "PC") || row.Host != applied.Host || row.Port != applied.Port.ToString();
        });

    public bool CanEditSetup => !_hasLoadedDeployment || Snapshot.IsMaintenanceMode;

    public bool CanEditExecution => IsRoutedDeployment && Snapshot.IsMaintenanceMode;

    public bool CanApplyExecution => CanEditExecution && HasExecutionChanges;

    public bool CanSaveExecution => IsRoutedDeployment && !HasExecutionChanges;

    public string ExecutionDraftStatus
    {
        get
        {
            if (!IsRoutedDeployment) return "Load a layout to assign territories.";
            int count = ExecutionRows.Count(row => row.HasChanges);
            return count > 0 ? $"{count} pending assignment{(count == 1 ? "" : "s")} · not applied" : "All assignments applied";
        }
    }

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
            nameof(CanEditSetup),
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
        if (plan is not null && plan.Territories.GroupBy(territory => territory.Configuration.LineName).Any(line => line.Count() < 2))
            throw new ArgumentException("Each line needs at least two wayside territories for this assignment. Load a split deployment.");
        var current = await _host.PollForSetupAsync(_lifetime.Token);
        if (_hasLoadedDeployment && !current.IsMaintenanceMode)
        {
            throw new InvalidOperationException("Enter maintenance mode before changing its deployment or execution.");
        }
        var routed = plan?.CreateRuntime(automaticOperation: true, maintenanceMode: current.IsMaintenanceMode);
        try
        {
            await _host.DisposeAsync();
            _controller.ResetTestState();
            _controller.SetMaintenanceMode(current.IsMaintenanceMode);
            _host = routed is null ? new WaysidePipeHost(_controller, _pipeNames) : new WaysidePipeHost(routed, _pipeNames);
            _plan = plan;
            _hasLoadedDeployment = true;
            _snapshot = _host.LatestSnapshot;
            IndexMapLines(plan);
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
        if (!SetupLines.Contains(_selectedSetupLine))
            _selectedSetupLine = AllSetupLines;
        OnPropertyChanged(nameof(SetupLines));
        OnPropertyChanged(nameof(SelectedSetupLine));
        RefreshSetupTerritories(selectedPlcId);
        OnPropertyChanged(nameof(SelectedPlcName));
        OnPropertyChanged(nameof(IsRoutedDeployment));
        OnPropertyChanged(nameof(CanChooseLogic));
        NotifyExecutionEditor();
        Status = plan is null ? "Standalone controller loaded. Valid logic and fresh feedback are required." : $"{plan.Name} loaded with {string.Join(" / ", plan.Territories.Select(territory => territory.ProgramName ?? plan.ProgramName).Distinct())}. Send fresh feedback. Maintenance remains enabled if selected.";
    }

    private async Task ResetControllerAsync()
    {
        if (!(await _host.PollForSetupAsync(_lifetime.Token)).IsMaintenanceMode)
            throw new InvalidOperationException("Enter maintenance mode before resetting controllers.");
        await UseDeploymentAsync(_plan);
    }

}
