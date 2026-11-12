using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CTC.Core.Interfaces;
using CTC.Core.Services;
using CTC.Wpf.Services;
using TrackController.Contracts.Messages;
using TrackController.Runtime;
using TrackController.Wpf.Services;
using TrackModel.Core.Services;
using TrackModel.Wpf.Services;
using TrainControl.Common.Communication;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The real Track Model, Track Controller and CTC communication components wired together over
/// isolated named pipes, each "process" on its own thread. Nothing here fakes a module: every
/// message crosses a real pipe through the production receivers and publishers.
/// </summary>
internal sealed class LiveSystem : IAsyncDisposable
{
    private readonly string _prefix = $"TrainControl.IntegrationTest.{Guid.NewGuid():N}";
    private readonly List<string> _temporaryFolders = [];

    private DispatcherThread? _trackModelThread;
    private CancellationTokenSource? _trackModelStop;
    private Task? _trackModelRun;

    private DispatcherThread? _ctcThread;
    private CancellationTokenSource? _ctcStop;
    private Task[] _ctcRun = [];

    public string TrackModelPipe => _prefix + ".TrackModel";

    public string TrackControllerPipe => _prefix + ".TrackController";

    public string CtcPipe => _prefix + ".CTC";

    /// <summary>Where the Track Model sends train environments (the Train Model's inbound pipe).</summary>
    public string TrainModelPipe => _prefix + ".TrainModel";

    /// <summary>The Train Controller's inbound pipe: the Track Model's lights and the Train Model's statuses go here.</summary>
    public string TrainControllerPipe => TrackModelPipe + ".TrainController";

    public TrackService? Track { get; private set; }

    public WaysidePipeHost? Wayside { get; private set; }

    public CTCService? Ctc { get; private set; }

    /// <summary>Everything CTC actually delivered to the Track Controller, in order.</summary>
    public RecordingSender? CtcSent { get; private set; }

    /// <summary>Inbound and link status lines CTC reported (what its UI would show).</summary>
    public ConcurrentQueue<string> CtcStatuses { get; } = new();

    public static string SamplesFolder => Path.Combine(AppContext.BaseDirectory, "WaysideSamples");

    /// <summary>The Track Controller deployment for the production three-line network: two waysides per line, on this PC.</summary>
    public static string CourseDeployment => Path.Combine(SamplesFolder, "course-territories-all-pc.deployment.json");

    /// <summary>Starts the Track Controller with a deployment (two-wayside Blue Line by default). It starts paused, as in production.</summary>
    public Task StartTrackControllerAsync(string? deploymentPath = null)
    {
        var plan = WaysideDeployment.Read(deploymentPath ?? Path.Combine(SamplesFolder, "blue-line-all-pc.deployment.json"));
        Wayside = new WaysidePipeHost(plan.CreateRuntime(), [TrackControllerPipe, CtcPipe, TrackModelPipe, TrackControllerPipe + ".Monitor"]);
        Wayside.AcknowledgementReceived += WaysideReceipts.Enqueue;
        Wayside.Start();
        return Task.CompletedTask;
    }

    /// <summary>The Track Controller runtime's answer to every input it handled (what its operator window sees).</summary>
    public ConcurrentQueue<WaysideAcknowledgementMessage> WaysideReceipts { get; } = new();

    /// <summary>A message to the Track Controller's input pipe, as any module would send it.</summary>
    public Task SendToTrackControllerAsync(object message) => NamedPipeTransport.SendAsync(TrackControllerPipe, message);

    public async Task ResumeTrackControllerAsync() =>
        await NamedPipeTransport.SendAsync(TrackControllerPipe, MessageSerializer.CreateEnvelope("SetSimulation", new { paused = false, speedMultiplier = 1 }));

    public async Task StopTrackControllerAsync()
    {
        if (Wayside is not null)
        {
            await Wayside.DisposeAsync();
            Wayside = null;
        }
    }

    /// <summary>
    /// Starts the production Track Model connection. With <paramref name="allLines"/> it loads
    /// exactly what TrackModel.Wpf's App loads (the Blue, Red and Green course lines, no trains);
    /// otherwise the Blue Line on its own, the single-line setup the Blue Line tests were written for.
    /// </summary>
    /// <param name="demonstrationTrain">
    /// Also seed the standalone demonstration's stopped train 001 in Blue block 1, for tests about
    /// live occupancy. Off by default, as in production, where CTC allocates train 001 itself.
    /// </param>
    /// <param name="prepare">Adjusts the loaded Track Model before it starts communicating (its start-up state).</param>
    public async Task StartTrackModelAsync(bool allLines = false, bool demonstrationTrain = false, Action<TrackService>? prepare = null)
    {
        _trackModelThread = new DispatcherThread("Track Model UI");
        _trackModelStop = new CancellationTokenSource();
        var endpoints = new TrackModelEndpoints(TrackModelPipe, TrackControllerPipe, _prefix + ".TrainModel", CtcPipe, _prefix + ".TrackModel.TestUI");
        Track = await _trackModelThread.GetAsync(() =>
        {
            var track = new TrackService();
            switch (allLines, demonstrationTrain)
            {
                case (true, false): CourseTrackLayout.Load(track); break;
                case (true, true): CourseTrackLayout.LoadDemonstration(track); break;
                case (false, false): BlueLineTrackLayout.Load(track); break;
                case (false, true): BlueLineTrackLayout.LoadDemonstration(track); break;
            }

            prepare?.Invoke(track);
            return track;
        });
        var connection = await _trackModelThread.GetAsync(() => new TrackModelConnection(Track, _trackModelThread.Dispatcher, endpoints));
        var stop = _trackModelStop.Token;
        _trackModelRun = _trackModelThread.StartAsync(() => connection.RunAsync(stop));
    }

    /// <summary>Stops the Track Model process: its receiver and publisher go away, as when the app closes.</summary>
    public async Task StopTrackModelAsync()
    {
        if (_trackModelStop is null)
        {
            return;
        }

        await _trackModelStop.CancelAsync();
        await AwaitQuietly(_trackModelRun);
        _trackModelThread!.Dispose();
        _trackModelStop.Dispose();
        _trackModelStop = null;
        _trackModelThread = null;
    }

    /// <summary>Reads Track Model state on its own thread.</summary>
    public Task<T> OnTrackModel<T>(Func<TrackService, T> read) => _trackModelThread!.GetAsync(() => read(Track!));

    /// <summary>Starts CTC exactly as CTC.Wpf's App does (receiver plus link supervisor), minus the window.</summary>
    public async Task StartCtcAsync(TimeSpan? requestRenewalInterval = null)
    {
        _ctcThread = new DispatcherThread("CTC UI");
        _ctcStop = new CancellationTokenSource();
        CtcSent = new RecordingSender(new NamedPipeMessageSender(TrackControllerPipe, connectTimeoutMilliseconds: 1000));
        Ctc = await _ctcThread.GetAsync(() => new CTCService(CtcSent, null, requestRenewalInterval));
        var receiver = new CtcNamedPipeReceiver(Ctc, _ctcThread.Dispatcher, CtcPipe);
        var links = new CtcLinkSupervisor(Ctc, _ctcThread.Dispatcher, TrackModelPipe);
        receiver.StatusReported += (_, status) => CtcStatuses.Enqueue(status);
        links.StatusReported += (_, status) => CtcStatuses.Enqueue(status);
        var stop = _ctcStop.Token;
        _ctcRun = [_ctcThread.StartAsync(() => receiver.RunAsync(stop)), _ctcThread.StartAsync(() => links.RunAsync(stop))];
    }

    public async Task StopCtcAsync()
    {
        if (_ctcStop is null)
        {
            return;
        }

        await _ctcStop.CancelAsync();
        foreach (var task in _ctcRun)
        {
            await AwaitQuietly(task);
        }

        _ctcThread!.Dispose();
        _ctcStop.Dispose();
        _ctcStop = null;
        _ctcThread = null;
    }

    /// <summary>Reads or acts on CTC state on CTC's own thread.</summary>
    public Task<T> OnCtc<T>(Func<CTCService, T> read) => _ctcThread!.GetAsync(() => read(Ctc!));

    public Task OnCtcAsync(Func<CTCService, Task> action) => _ctcThread!.StartAsync(() => action(Ctc!));

    /// <summary>An external input to the production Track Model (e.g. what the Train Model will send).</summary>
    public Task SendToTrackModelAsync(object message) => NamedPipeTransport.SendAsync(TrackModelPipe, message);

    /// <summary>A message to CTC's inbound pipe, e.g. the shared clock's SystemTimeMessage.</summary>
    public Task SendToCtcAsync(object message) => NamedPipeTransport.SendAsync(CtcPipe, message);

    public WaysideBlockStatus WaysideBlock(string blockId) => Wayside!.LatestSnapshot.Blocks.Single(block => block.BlockId == blockId);

    /// <summary>A private copy of the deployment samples the test may edit.</summary>
    public string CopySamples(Action<string>? edit = null)
    {
        string folder = Path.Combine(Path.GetTempPath(), "train-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (string file in Directory.GetFiles(SamplesFolder))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }

        _temporaryFolders.Add(folder);
        edit?.Invoke(folder);
        return folder;
    }

    public static void EditJson(string path, Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(json);
        File.WriteAllText(path, json.ToJsonString());
    }

    /// <summary>Polls <paramref name="condition"/> until it holds; fails with <paramref name="because"/> after <paramref name="timeout"/>.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(12));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out: {because}");
            }

            await Task.Delay(50);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopCtcAsync();
        await StopTrackModelAsync();
        await StopTrackControllerAsync();
        foreach (string folder in _temporaryFolders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task AwaitQuietly(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The production CTC sender, recording what it actually delivered.</summary>
    internal sealed class RecordingSender(IMessageSender inner) : IMessageSender
    {
        public ConcurrentQueue<object> Delivered { get; } = new();

        public async Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
            where TMessage : class
        {
            await inner.SendAsync(message, cancellationToken);
            Delivered.Enqueue(message);
        }
    }
}
