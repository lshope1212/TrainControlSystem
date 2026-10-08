using System.Net;
using System.Net.Sockets;
using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Hardware;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Core.Services;
using TrainController.Hardware.Pi;
using TrainController.Integration;
using TrainController.Integration.Backends;
using TrainController.Integration.Execution;
using TrainController.Integration.Hardware;
using TrainController.Integration.Routing;
using static TrainControl.Tests.TrainController.ControllerTestInputs;

namespace TrainControl.Tests.TrainController.Hardware;

[TestClass]
public class TcpHardwareBackendTests
{
    /// <summary>Generous timeouts so loaded CI machines do not flake; failure tests override them.</summary>
    private static HardwareLinkOptions Options(int port, int requestTimeoutMs = 2_000) => new HardwareLinkOptions
    {
        Host = "127.0.0.1",
        Port = port,
        RequestTimeout = TimeSpan.FromMilliseconds(requestTimeoutMs),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ReconnectInterval = TimeSpan.Zero,
    };

    private static TrainControllerInput HwInput(string trainId, long tick, double speed = 5.0, bool passenger = false, DriverInput? driver = null) =>
        Input(Model(speed: speed, authorized: 10.0, passengerEmergency: passenger), driver ?? Automatic(), tick: tick, trainId: trainId);

    // ------------------------------------------------------------------ happy path (real Pi server)

    [TestMethod]
    public async Task EndToEnd_OverLoopbackTcp_MatchesSoftwareController()
    {
        await using var server = new HardwareControllerServer(IPAddress.Loopback, 0, new HardwareRequestHandler());
        server.Start();
        await using var backend = new TcpHardwareTrainControllerBackend(Options(server.Port));
        var software = new SoftwareTrainController("TRAIN-002");

        Assert.AreEqual(HardwareConnectionState.Disconnected, backend.ConnectionState);

        for (var tick = 1; tick <= 20; tick++)
        {
            var input = HwInput("TRAIN-002", tick, speed: 0.2 * tick);
            var hw = await backend.EvaluateAsync(input, CancellationToken.None);
            SoftwareHardwareEquivalenceTests.AssertEquivalent(software.Step(input), hw, $"tcp tick {tick}");
        }

        Assert.AreEqual(HardwareConnectionState.Active, backend.ConnectionState);
    }

    [TestMethod]
    public async Task OnePi_FiveTrains_IndependentState_OverOneConnection()
    {
        await using var server = new HardwareControllerServer(IPAddress.Loopback, 0, new HardwareRequestHandler());
        server.Start();
        await using var backend = new TcpHardwareTrainControllerBackend(Options(server.Port));

        foreach (var trainId in TrainFleet.HardwareTrainIds)
        {
            var output = await backend.EvaluateAsync(HwInput(trainId, 1, passenger: trainId == "TRAIN-006"), CancellationToken.None);
            Assert.AreEqual(trainId == "TRAIN-006", output.Commands.EmergencyBrakeCommand, trainId);
        }

        Assert.AreEqual(EmergencyBrakeCause.Passenger, server.Handler.Controller("TRAIN-006").LatchedEmergencyCauses);
        Assert.AreEqual(EmergencyBrakeCause.None, server.Handler.Controller("TRAIN-002").LatchedEmergencyCauses);
        Assert.AreEqual(EmergencyBrakeCause.None, server.Handler.Controller("TRAIN-010").LatchedEmergencyCauses);
    }

    [TestMethod]
    public async Task Reset_ResetsAllPiTrainStates()
    {
        await using var server = new HardwareControllerServer(IPAddress.Loopback, 0, new HardwareRequestHandler());
        server.Start();
        await using var backend = new TcpHardwareTrainControllerBackend(Options(server.Port));
        await backend.EvaluateAsync(HwInput("TRAIN-008", 1, passenger: true), CancellationToken.None);

        await backend.ResetAsync(CancellationToken.None);

        Assert.AreEqual(EmergencyBrakeCause.None, server.Handler.Controller("TRAIN-008").LatchedEmergencyCauses);
    }

    [TestMethod]
    public async Task SoftwareTrain_IsRejected_NoTcpForSoftware()
    {
        await using var backend = new TcpHardwareTrainControllerBackend(Options(1));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.EvaluateAsync(HwInput("TRAIN-001", 1), CancellationToken.None));
    }

    [TestMethod]
    public async Task Subsystem_WithTcpBackend_RunsHardwareTrainsOnThePi()
    {
        await using var server = new HardwareControllerServer(IPAddress.Loopback, 0, new HardwareRequestHandler());
        server.Start();
        await using var backend = new TcpHardwareTrainControllerBackend(Options(server.Port));
        var subsystem = TrainControllerSubsystem.Create(hardwareBackend: backend);
        subsystem.TestMode.SetTestMode(true);
        foreach (var id in new[] { "TRAIN-001", "TRAIN-002", "TRAIN-004" })
        {
            var model = subsystem.Registry.Get(id).TestModel;
            model.IsActive = true;
            model.AuthorizedSpeedMetersPerSecond = 10.0;
            model.RemainingAuthorityMeters = 5_000.0;
        }

        await subsystem.Simulation.StepAsync();
        await subsystem.Simulation.StepAsync();

        Assert.IsFalse(subsystem.Registry.Get("TRAIN-002").LastOutput!.IsFailSafe);
        Assert.IsFalse(subsystem.Registry.Get("TRAIN-004").LastOutput!.IsFailSafe);
        Assert.AreEqual(2L, subsystem.Registry.Get("TRAIN-004").LastOutput!.TickId);
        Assert.AreEqual(HardwareConnectionState.Active, subsystem.HardwareConnection!.ConnectionState);
    }

    // ------------------------------------------------------------------ framing / queue

    [TestMethod]
    public async Task FragmentedResponses_AreReassembled()
    {
        await using var pi = ScriptedPiServer.Start();
        pi.Script = _ => new PiScript(PiFault.Fragment);
        await using var backend = new TcpHardwareTrainControllerBackend(Options(pi.Port));

        var output = await backend.EvaluateAsync(HwInput("TRAIN-002", 1), CancellationToken.None);

        Assert.AreEqual(1L, output.TickId);
    }

    [TestMethod]
    public async Task RequestQueue_IsFifo_AndDeterministic()
    {
        await using var pi = ScriptedPiServer.Start();
        pi.Script = e => e.Type == HardwareMessageType.ControllerRequest ? new PiScript(PiFault.Delay, DelayMs: 20) : PiScript.Normal;
        await using var backend = new TcpHardwareTrainControllerBackend(Options(pi.Port));
        var order = new[] { "TRAIN-010", "TRAIN-004", "TRAIN-008", "TRAIN-002", "TRAIN-006" };

        var tasks = order.Select(id => backend.EvaluateAsync(HwInput(id, 1), CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks);

        CollectionAssert.AreEqual(order, pi.ControllerRequests().Select(e => e.TrainId).ToArray(), "Pi saw requests in issue order, one at a time.");
    }

    [TestMethod]
    public async Task FifoAsyncLock_GrantsInRequestOrder()
    {
        var gate = new FifoAsyncLock();
        var granted = new List<int>();
        var first = await gate.AcquireAsync(CancellationToken.None);
        var waiters = Enumerable.Range(1, 5).Select(async i =>
        {
            using (await gate.AcquireAsync(CancellationToken.None))
            {
                lock (granted)
                {
                    granted.Add(i);
                }
            }
        }).ToArray();

        first.Dispose();
        await Task.WhenAll(waiters);

        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, granted);
    }

    // ------------------------------------------------------------------ failure handling

    private static async Task AssertCommunicationFailureAsync(ScriptedPiServer pi, Func<HardwareEnvelope, PiScript> script, int requestTimeoutMs = 2_000)
    {
        // Warm up serialization/JIT with a generous timeout so a short test timeout measures only the fault.
        await using (var warmUp = new TcpHardwareTrainControllerBackend(Options(pi.Port)))
        {
            await warmUp.EvaluateAsync(HwInput("TRAIN-010", 1), CancellationToken.None);
        }

        await using var backend = new TcpHardwareTrainControllerBackend(Options(pi.Port, requestTimeoutMs));
        await backend.EvaluateAsync(HwInput("TRAIN-002", 1), CancellationToken.None); // healthy first

        pi.Script = script;
        await Assert.ThrowsExactlyAsync<ControllerCommunicationException>(() => backend.EvaluateAsync(HwInput("TRAIN-002", 2), CancellationToken.None));
        Assert.AreNotEqual(HardwareConnectionState.Active, backend.ConnectionState);
        Assert.IsTrue(backend.IsFaultLatched("TRAIN-002"));
    }

    private static Func<HardwareEnvelope, PiScript> OnStep(PiScript plan) =>
        e => e.Type == HardwareMessageType.ControllerRequest ? plan : PiScript.Normal;

    [TestMethod]
    public async Task Timeout_IsCommunicationFailure()
    {
        await using var pi = ScriptedPiServer.Start();
        await AssertCommunicationFailureAsync(pi, OnStep(new PiScript(PiFault.Delay, DelayMs: 1_000)), requestTimeoutMs: 150);
    }

    [TestMethod]
    public async Task Disconnect_IsCommunicationFailure()
    {
        await using var pi = ScriptedPiServer.Start();
        await AssertCommunicationFailureAsync(pi, OnStep(new PiScript(PiFault.Close)));
    }

    [TestMethod]
    public async Task MalformedJson_IsCommunicationFailure()
    {
        await using var pi = ScriptedPiServer.Start();
        await AssertCommunicationFailureAsync(pi, OnStep(new PiScript(PiFault.Garbage)));
    }

    [TestMethod]
    [DataRow("train")]
    [DataRow("tick")]
    [DataRow("version")]
    [DataRow("stale")]
    [DataRow("type")]
    [DataRow("error")]
    [DataRow("output-train")]
    [DataRow("missing-output")]
    public async Task InvalidResponses_AreCommunicationFailures(string defect)
    {
        Func<HardwareEnvelope, HardwareEnvelope> mutate = defect switch
        {
            "train" => r => r with { TrainId = "TRAIN-004" },
            "tick" => r => r with { TickId = r.TickId + 1 },
            "version" => r => r with { ProtocolVersion = 99 },
            "stale" => r => r with { RequestId = r.RequestId - 1 },
            "type" => r => r with { Type = HardwareMessageType.ResetResponse },
            "error" => r => r with { Type = HardwareMessageType.ErrorResponse, Error = "Pi exploded" },
            "output-train" => r => r with { Output = r.Output! with { TrainId = "TRAIN-004" } },
            _ => r => r with { Output = null },
        };

        await using var pi = ScriptedPiServer.Start();
        await AssertCommunicationFailureAsync(pi, OnStep(new PiScript(PiFault.Mutate, mutate)));
    }

    [TestMethod]
    public async Task ConnectionRefused_IsCommunicationFailure_Disconnected()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var unusedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var backend = new TcpHardwareTrainControllerBackend(Options(unusedPort));

        await Assert.ThrowsExactlyAsync<ControllerCommunicationException>(() => backend.EvaluateAsync(HwInput("TRAIN-002", 1), CancellationToken.None));
        Assert.AreEqual(HardwareConnectionState.Disconnected, backend.ConnectionState);
    }

    [TestMethod]
    public async Task HardwareFailure_ThroughExecution_IsFailSafe_WithNoSoftwareFallback()
    {
        await using var pi = ScriptedPiServer.Start();
        pi.Script = OnStep(new PiScript(PiFault.Close));
        await using var backend = new TcpHardwareTrainControllerBackend(Options(pi.Port));
        var software = new FakeTrainControllerBackend(ControllerType.Software);
        var execution = new TrainControllerExecutionService(new TrainControllerRouter(software, backend));

        var output = await execution.ExecuteAsync(HwInput("TRAIN-002", 1, speed: 8.0), null, CancellationToken.None);

        Assert.IsTrue(output.IsFailSafe);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
        Assert.IsTrue(output.Commands.EmergencyBrakeCommand);
        Assert.IsTrue(output.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.HardwareCommunication));
        Assert.IsEmpty(software.Evaluated, "No Software fallback for a Hardware train.");
    }

    [TestMethod]
    public async Task AfterLinkFault_DriverResetRequired_ThenPiStateResynchronized()
    {
        await using var pi = ScriptedPiServer.Start();
        await using var backend = new TcpHardwareTrainControllerBackend(Options(pi.Port));
        await backend.EvaluateAsync(HwInput("TRAIN-004", 1), CancellationToken.None);

        pi.Script = OnStep(new PiScript(PiFault.Garbage));
        await Assert.ThrowsExactlyAsync<ControllerCommunicationException>(() => backend.EvaluateAsync(HwInput("TRAIN-004", 2), CancellationToken.None));

        pi.Script = _ => PiScript.Normal; // link healthy again
        var stillLatched = await Assert.ThrowsExactlyAsync<ControllerCommunicationException>(() => backend.EvaluateAsync(HwInput("TRAIN-004", 3), CancellationToken.None));
        StringAssert.Contains(stillLatched.Message, "E-brake reset");

        var other = await backend.EvaluateAsync(HwInput("TRAIN-006", 3), CancellationToken.None);
        Assert.IsFalse(other.Commands.EmergencyBrakeCommand, "Other Hardware trains are unaffected.");

        var resumed = await backend.EvaluateAsync(HwInput("TRAIN-004", 4, driver: Automatic() with { EmergencyBrakeResetRequested = true }), CancellationToken.None);
        Assert.AreEqual(4L, resumed.TickId);
        Assert.IsFalse(backend.IsFaultLatched("TRAIN-004"));

        var tail = pi.Received.Reverse().Take(2).Reverse().ToArray();
        Assert.AreEqual(HardwareMessageType.ResetRequest, tail[0].Type);
        Assert.AreEqual("TRAIN-004", tail[0].TrainId, "Only the faulted train is re-synchronized.");
        Assert.AreEqual(HardwareMessageType.ControllerRequest, tail[1].Type);
    }

    // ------------------------------------------------------------------ cold start

    [TestMethod]
    public async Task HandshakeAndFirstRequest_UseConnectTimeout_LaterRequestsAreStrict()
    {
        // Simulates one-time start-up work on the Pi: Hello and the first step are slow (300 ms),
        // longer than the 100 ms request timeout but within the 2 s connect timeout.
        await using var pi = ScriptedPiServer.Start();
        var steps = 0;
        pi.Script = e => e.Type switch
        {
            HardwareMessageType.Hello => new PiScript(PiFault.Delay, DelayMs: 300),
            HardwareMessageType.ControllerRequest when Interlocked.Increment(ref steps) is 1 or 3 => new PiScript(PiFault.Delay, DelayMs: 300),
            _ => PiScript.Normal,
        };
        await using var backend = new TcpHardwareTrainControllerBackend(Options(pi.Port, requestTimeoutMs: 100));

        var first = await backend.EvaluateAsync(HwInput("TRAIN-002", 1), CancellationToken.None);
        Assert.AreEqual(1L, first.TickId, "Cold start tolerated.");

        var second = await backend.EvaluateAsync(HwInput("TRAIN-002", 2), CancellationToken.None);
        Assert.AreEqual(2L, second.TickId);

        await Assert.ThrowsExactlyAsync<ControllerCommunicationException>(
            () => backend.EvaluateAsync(HwInput("TRAIN-002", 3), CancellationToken.None));
    }

    [TestMethod]
    public void PiWarmUp_RunsWithoutTouchingRealState()
    {
        var elapsed = HardwareRequestHandler.WarmUp();
        NumericAssert.Positive(elapsed.TotalMilliseconds);

        HardwareProtocol.WarmUp(); // idempotent, never throws

        var fresh = new HardwareRequestHandler();
        Assert.IsNull(fresh.Handle(new HardwareEnvelope { Type = HardwareMessageType.Hello }).Error);
        Assert.AreEqual(0.0, fresh.Controller("TRAIN-002").IntegralMeters);
    }

    // ------------------------------------------------------------------ configuration

    [TestMethod]
    public void LinkOptions_LoadFromJson_AndPerRunOverrides()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tc-appsettings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              // comments allowed
              "HardwareLink": { "Host": "192.168.50.2", "Port": 5050, "RequestTimeoutMs": 250 }
            }
            """);
        try
        {
            var loaded = HardwareLinkOptions.Load(path);
            Assert.AreEqual("192.168.50.2", loaded.Host);
            Assert.AreEqual(5050, loaded.Port);
            Assert.AreEqual(TimeSpan.FromMilliseconds(250), loaded.RequestTimeout);
            Assert.AreEqual(HardwareLinkOptions.Default.ConnectTimeout, loaded.ConnectTimeout, "Missing values keep defaults.");

            var local = loaded.WithCommandLineOverrides(new[] { "--pi-host", "127.0.0.1", "--pi-port", "6060" });
            Assert.AreEqual("127.0.0.1", local.Host);
            Assert.AreEqual(6060, local.Port);
            Assert.AreEqual("192.168.50.2", loaded.Host, "Overrides never modify the loaded settings.");

            Assert.AreSame(loaded, loaded.WithCommandLineOverrides(Array.Empty<string>()));
            Assert.ThrowsExactly<ArgumentException>(() => loaded.WithCommandLineOverrides(new[] { "--pi-host" }));
            Assert.ThrowsExactly<ArgumentException>(() => loaded.WithCommandLineOverrides(new[] { "--pi-port", "99999" }));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.AreSame(HardwareLinkOptions.Default, HardwareLinkOptions.Load(path), "No file: defaults.");
    }
}
