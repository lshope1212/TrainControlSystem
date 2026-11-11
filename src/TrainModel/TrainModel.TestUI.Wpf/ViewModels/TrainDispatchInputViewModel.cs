using TrainControl.Contracts.Messages;
using TrainModel.TestUI.Wpf.Commands;
using TrainModel.TestUI.Wpf.Services;

namespace TrainModel.TestUI.Wpf.ViewModels;

/// <summary>
/// Stands in for the Track Model's train placement (the planned dispatch flow: CTC dispatches,
/// the Track Model places the train on its start block). Sends a
/// <see cref="TrackModelTrainUpdateMessage"/>: a non-empty CurrentBlockId dispatches the
/// target train (active), an empty one removes it (inactive). "Load demo trains" dispatches
/// several trains at rest and gives each a commanded speed and authority, for multi-train tests.
/// </summary>
public sealed class TrainDispatchInputViewModel : TrainModelInputViewModelBase
{
    private string _startBlockId = "1";
    private int _demoCount = 3;

    public TrainDispatchInputViewModel(ITrainModelMessageSender sender, Func<string> trainIdProvider)
        : base(sender, trainIdProvider)
    {
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
        LoadDemoCommand = new AsyncRelayCommand(LoadDemoAsync);
    }

    public string StartBlockId { get => _startBlockId; set => SetProperty(ref _startBlockId, value); }

    public IReadOnlyList<int> DemoCounts { get; } = new[] { 3, 5, 10 };

    public int DemoCount { get => _demoCount; set => SetProperty(ref _demoCount, value); }

    public AsyncRelayCommand RemoveCommand { get; }
    public AsyncRelayCommand LoadDemoCommand { get; }

    /// <summary>Dispatch: place the target train on the start block.</summary>
    protected override Task SendAsync() =>
        SendMessageAsync(new TrackModelTrainUpdateMessage
        {
            TrainId = TargetTrainId,
            CurrentBlockId = string.IsNullOrWhiteSpace(StartBlockId) ? "1" : StartBlockId.Trim(),
        }, $"Dispatched {TargetTrainId}");

    private Task RemoveAsync() =>
        SendMessageAsync(new TrackModelTrainUpdateMessage
        {
            TrainId = TargetTrainId,
            CurrentBlockId = string.Empty,
        }, $"Removed {TargetTrainId}");

    private async Task LoadDemoAsync()
    {
        for (int i = 1; i <= DemoCount; i++)
        {
            string id = i.ToString("D3");
            await Sender.SendToTrainModelAsync(new TrackModelTrainUpdateMessage { TrainId = id, CurrentBlockId = StartBlockId });
            await Sender.SendToTrainModelAsync(new SpeedCommandMessage { TrainId = id, CommandedSpeedMetersPerSecond = 19.44 });
            await Sender.SendToTrainModelAsync(new AuthorityMessage { TrainId = id, AuthorityMeters = 381.0 });
        }

        Result = $"Dispatched demo trains 001-{DemoCount:D3} at rest (70 km/h commanded, 381 m authority) @ {DateTime.Now:HH:mm:ss}";
    }
}
