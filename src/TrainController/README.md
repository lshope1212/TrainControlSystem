# Train Controller module

Controls ten trains. The Software/Hardware assignment is **fixed** (defined only in
`TrainController.Abstractions/Fleet/TrainFleet.cs`; no runtime switching, no fallback):

| Controller | Trains                                              | Runs on                   |
| ---------- | --------------------------------------------------- | ------------------------- |
| Software   | TRAIN-001, TRAIN-003, TRAIN-005, TRAIN-007, TRAIN-009 | Windows host (in-process) |
| Hardware   | TRAIN-002, TRAIN-004, TRAIN-006, TRAIN-008, TRAIN-010 | one Raspberry Pi (TCP/IP) |

## Projects

| Project                          | Target           | Responsibility |
| -------------------------------- | ---------------- | -------------- |
| `TrainController.Abstractions`   | `net10.0`        | Internal data model: fleet + fixed assignment, `TrainControllerInput`/`Output`, vehicle specification, provisional policy values, input/output validation. No control law. |
| `TrainController.Core`           | `net10.0`        | **Software** Train Controller control law (one independent controller state per train). |
| `TrainController.Integration`    | `net10.0`        | Routing, per-train state registry, Test Mode / Test Simulation control, backends (Software adapter, Hardware TCP client), framing, Named Pipe adapters, output routing. No control law, no WPF. |
| `TrainController.Hardware.Pi`    | `net10.0` console | **Hardware** Train Controller: independent process on the Raspberry Pi (TCP server). Must not reference `TrainController.Core` (enforced by `TrainControllerArchitectureTests`). |
| `TrainController.Wpf`            | `net10.0-windows` | Main UI (Driver/Engineer) and Test UI (Train Model stand-in). Views and ViewModels only. |

Dependency direction:

```
TrainController.Wpf ──► TrainController.Integration ──► TrainController.Core ──► TrainController.Abstractions ──► Contracts / Common
                                                    └──────────────────────────► TrainController.Abstractions
TrainController.Hardware.Pi ─────────────────────────────────────────────────► TrainController.Abstractions ──► Contracts / Common
```

## Publishing the Raspberry Pi controller

For 64-bit Raspberry Pi OS, from the repository root:

```
dotnet publish src/TrainController/TrainController.Hardware.Pi -c Release -r linux-arm64 --self-contained true -p:PublishReadyToRun=true -o publish/pi
```

(Full Pi deployment instructions are added with the Pi server phase.)

## Conventions specific to this module

- **Two peer-level windows.** Unlike the other modules, `TrainController.Wpf` has a Main UI
  window and a Test UI window. Neither is a child of the other. This is an intentional,
  documented exception to the repository's one-window convention, because the Test UI
  must control the Train Controller's own test simulation (Run/Stop/Step/Reset/Speed).
- **Test Mode is global.** Either every train takes its model input from the real Train
  Model, or every train takes it from its Test UI test state. Never mixed, never both.
- **Running is owned by the system.** A train executes controller ticks only while its
  model input reports `IsActive` (dispatched via CTC → Track Controller → Train Model; in
  Test Mode, set in that train's Test Model state). The Train Controller has no start/stop
  toggle of its own, and no train runs by default.
- **SI internally, imperial in the UI.** Conversions use `TrainControl.Common.Utilities.UnitConversion`.
- **IPC.** Windows-to-Windows module communication will use the repository's existing
  named-pipe + JSON envelope transport behind adapters. TCP/IP is used **only** between the
  Windows Hardware backend (client) and the Raspberry Pi (server).

## Beacon reception

The requirement is that **every newly received valid beacon recalibrates the distance to the
next station**; between receptions the controller dead-reckons from actual speed × dt.
Internally each tick's `BeaconData.IsNewlyReceived` marks a reception. It is set by the
model-input provider, not imposed on the Train Model contract:

- Test Mode: the Test UI's "transmit beacon" action marks one reception, consumed by that
  train's next tick.
- Normal Mode: the Train Model interface does not yet define how a reception is signalled;
  the Normal-Mode adapter will derive `IsNewlyReceived` once that mechanism is agreed.

## Fail-safe output

Used for Hardware communication failure and any invalid input/output or controller
exception (`FailSafeOutput`): power 0 and emergency brake applied. Door commands are never
carried over; doors stay subject to the door/motion interlock — a door may remain open only
if the last known Train Model state shows the train stopped and that door already open;
otherwise both sides are commanded closed. The fail-safe never opens a door. Exterior lights
and cabin setpoint are retained.

## Runtime structure (Integration)

```
TrainControllerSubsystem (composition root, one per process)
 ├─ TrainStateRegistry ── 10 × TrainRuntimeSlot (Driver, Engineer, TestModel, last output)
 ├─ TestModeController ── GLOBAL switch: NormalModelInputProvider | TestModelInputProvider
 ├─ TrainControllerTickEngine
 │     per tick, for every ACTIVE train:
 │       model input (current provider) + Driver + Engineer + timing -> TrainControllerInput
 │       -> TrainControllerExecutionService (validate, route, fail-safe, log)
 │            -> TrainControllerRouter -> SoftwareTrainControllerBackend | Hardware backend
 │       -> OutputRouter (record for UIs; Normal Mode only: send to Train Model sink)
 └─ TestSimulationController (Run / Stop / Step / Reset / Speed; Test Mode only)
```

- **Fixed simulated dt.** Every tick uses `SimulationTimeStepSeconds`; simulation time is
  TickId × dt. The speed multiplier only shortens the wall-clock interval between ticks
  (dt / multiplier).
- **Stop** lets the current tick finish, then freezes; nothing is lost. **Reset** stops, then
  clears tick counter, simulation time, every controller's runtime (PI integral, E-brake
  latch, station tracking, dwell), previous outputs and pending one-shot events for all ten
  trains. Engineer Kp/Ki, vehicle data, Driver held settings and Test Model input values stay.
- **Normal Mode ticking** is not driven yet: the Test Simulation controls are Test-Mode only,
  and the Normal-Mode tick source (shared system clock / Train Model updates) is a future
  integration point that will call `TrainControllerTickEngine.ExecuteTickAsync`.
- Until the Raspberry Pi backend exists, `NotConnectedHardwareBackend` makes every Hardware
  train fail safe (power 0, emergency brake). There is no Software fallback.

## User interfaces (TrainController.Wpf)

Two PEER windows open at startup on the same `TrainControllerSubsystem`; the app exits when
both are closed. Visual style follows the team's CTC Office / Track Model wireframes (dark
navy header, white panels with gray header bands, label/value status cards, flat buttons,
green/red/orange/blue status colors).

| Window | Contains | Never contains |
| ------ | -------- | -------------- |
| **Main UI** (`MainWindow`) | Train dropdown (TRAIN-001…010 with fixed SW/HW label), controller source badge, Pi link status, input-source badge, station & authority guidance bar (train · brake point · authority end · station), speed readouts incl. effective target and what limits it, Driver controls (mode; requested speed as a numeric **mph** entry — Manual only, applied with Enter/Set, validated; service / emergency brake; E-brake reset; doors; lights; one-shot announcement; cabin °F), Engineer Kp/Ki per train (with untuned-placeholder warning), status cards, fleet table, alerts, event log | Train Model inputs, simulation controls |
| **Test UI** (`TestWindow`) | Own train dropdown (independent of the Main UI), GLOBAL input source (Train Model / Test UI), Run / Stop / Step / Reset / speed, Train Model → Train Controller inputs of the selected train (incl. "active / dispatched" = Train Model state, and "Transmit beacon"), Train Controller → Train Model outputs, fleet table limited to Train Model boundary I/O, edge-value warnings | Driver or Engineer controls, controller type, any non-boundary data (enforced by tests) |

**UI units are imperial everywhere: mph, ft, °F, hp** (480 000 W ≈ 644 hp). Internal,
integration and Pi values stay SI (m/s, m, °C, W, s). Conversion happens only in
`Integration/Presentation` (`DisplayUnits`, `RequestedSpeedEntry`) via
`TrainControl.Common.Utilities.UnitConversion`. Kp/Ki are labelled with their SI meaning
(W per m/s, W per m) because they are gains, not displayed quantities; converting them would
change their meaning for both controllers.

**Window lifetime:** both windows share one subsystem; `ShutdownMode=OnLastWindowClose`, so
closing one window leaves the other (and the running subsystem) untouched. Each window's
selected train is independent and never affects which trains run.

Layering and restyling:

- **All colors and styles** are in `TrainController.Wpf/Resources/Theme.xaml`. View models
  expose semantic `DisplayTone`s (Neutral / Muted / Good / Info / Warning / Danger), never colors.
- **Display formatting and imperial units** are produced by
  `TrainController.Integration/Presentation` (`DisplayUnits`, `TrainControllerPresenter`) —
  framework-neutral and unit-tested. View models only bind and forward edits.
- View models refresh by polling (100 ms, display only). Editable fields are reloaded only
  when the selected train changes, so typing is never overwritten. Selecting a train never
  starts, stops or resets any train.
- Reusable view pieces: `Views/Controls/KeyValueRow` (status row) and
  `Views/Controls/GuidanceBar` (distance bar, drawn to scale from controller output).

## Software Train Controller behavior (TrainController.Core)

`SoftwareTrainController` — one instance per Software train — executes per tick:

1. Input validation (invalid → fail-safe).
2. Tracking (station-relative, not absolute position):
   - **Station distance**: each newly received valid beacon recalibrates; otherwise
     distance −= v·dt (signed). Arrival = |distance| ≤ threshold AND stopped. The station
     target is cleared (icon removed, "waiting for next beacon") when the train departs a
     served station, or goes past it by more than the threshold without stopping.
   - **Remaining authority**: received values are trusted ONLY while `TrackSignalValid` is
     true. With a valid signal the estimate is initialized from the first received value and
     recalibrated on each new authority update (`TrainModelInput.AuthorityUpdateReceived`).
     With an invalid signal, received authority values are ignored and the last trusted
     estimate keeps decreasing by v·dt (clamped at 0); restoring the signal does not by itself
     recalibrate — the next valid-signal update does. With no trusted value yet and an invalid
     signal, remaining authority is 0 (no movement permitted). Authority protection, the
     authority braking curve and the display all use this estimate. In the Test UI, changing
     Remaining authority (or "Send authority") is an authority update.

### Signal pickup

Signal pickup (receiving the track-provided authorized speed and authority) is represented by
`TrackSignalValid`, supplied by the Train Model in `TrainModelStatusMessage` and simulated by the
Test UI "Track signal valid" checkbox. There is deliberately no separate `SignalPickupFailure`
input and no timeout / freshness inference: the interface provides signal validity explicitly.
While invalid: target speed 0 (service-brake stop by default, configurable emergency brake via
`TrackSignalLossResponse`) and authority updates are not trusted (see above).
3. Effective target = MINIMUM of: authorized speed, vehicle maximum, the remaining-authority
   braking curve √(2·1.2·(authority − margin)), the driver request (Manual only), and in
   Automatic the station braking curve √(2·1.2·(station distance − margin)) / station stop.
   0 when the track signal is lost (plus a latched emergency brake only if
   `TrackSignalLossResponse.EmergencyBrake` is configured), while the emergency brake is
   applied, or while the driver holds the service brake (`DriverServiceBrake`; the requested
   speed setting is kept and becomes the target again on release). The binding constraint is reported as the strongly typed `TargetLimitedBy`
   (`TargetSpeedConstraint`); presentation converts it to text. The driver request can only
   LOWER the target; Automatic mode never uses it. The station braking curve applies only
   when a valid next-station distance exists (`StationTracker.HasValidDistance`); missing or
   invalid station information never implies a zero target. The authority braking curve is
   a PREVENTIVE target limit; `AuthorityProtection` (step 4) remains the INDEPENDENT safety
   backstop — neither replaces the other.
4. Authority protection: service brake when authority ≤ service stopping distance + authority
   margin + v·dt; emergency brake when authority < emergency stopping distance while moving;
   service-brake hold when stopped with exhausted authority.
5. Emergency latch (driver, passenger, authority). Reset accepted only when no emergency
   condition remains; one source clearing never releases the brake.
6. Station guidance: latest brake point = distance − v·dt − (v²/2·1.2 + station margin).
   Manual: advisory only. Automatic: controller applies the service brake.
7. Arrival = distance ≤ station threshold AND speed ≤ stopped threshold. Automatic: dwell
   with platform-side doors open, then close and depart.
8. Doors: closed and requests refused while moving; traction inhibited while any door is
   open or commanded open.
9. Overspeed (> limit): service brake.
10. PI power (Kp·e + Ki·∫e dt, SI, clamped to 0 … 480 kW, anti-windup, integral cleared while
    traction is inhibited) only when no brake is applied and doors are closed.

### Station announcements

`TrainModelCommand.StationAnnouncement` is an EVENT: non-empty on exactly one tick.

- Automatic: once when a newly received valid beacon names the next station
  ("Next station: X. Doors will open on the left/right/both sides."), and once on arrival
  ("Arrived at X."). If both happen on the same tick, the arrival is announced.
- Driver (Main UI, Manual or Automatic): one-shot text sent on the train's next tick; it takes
  precedence over an automatic announcement on that tick.
- Because the command lasts one tick, the Windows side keeps the last announcement sent
  (`TrainRuntimeSlot.LastAnnouncement`, display only) so both UIs can show it.

Wording of the automatic announcements is provisional.

### Diagnostics shown to the driver

- `TractionState` (shown as **Power status** in the Main UI) explains a zero power command (at/above target, holding brake while
  stopped, service brake, door interlock, emergency brake, fail-safe).
- `EmergencyBrakeResetBlockedReason` says whether a driver reset is possible right now and,
  if not, why (passenger request active, cannot stop within authority, signal lost).
- `StationEvent` reports Arrived / Departed / PassedWithoutStopping.

**Testing without a Train Model:** the Test UI has no physics, so Actual speed only changes
when the tester edits it. With authority now counted down by v·dt, a train left running at
constant speed WILL eventually reach authority protection and then the emergency brake —
this is correct controller behavior. Lower Actual speed (simulating the train braking) or
send a new authority.

## Hardware Train Controller (Raspberry Pi)

```
Windows: TrainControllerTickEngine
   -> TrainControllerExecutionService -> TrainControllerRouter
   -> TcpHardwareTrainControllerBackend  (transport + validation only, no control law)
        FIFO request lock -> one long-lived TCP connection -> [4-byte length][JSON]
Pi:  HardwareControllerServer -> HardwareRequestHandler
        -> HardwareTrainController[TRAIN-002 / 004 / 006 / 008 / 010]  (independent state each)
```

- **Protocol** (`Abstractions/Hardware/HardwareProtocol.cs`, version 1): JSON
  `HardwareEnvelope` per length-prefixed frame (`Common/Communication/LengthPrefixedFraming.cs`).
  Hello / HelloResponse on connect (version + served trains), ControllerRequest (complete SI
  `TrainControllerInput`) / ControllerResponse (`TrainControllerOutput`), ResetRequest /
  ResetResponse, ErrorResponse. Every reply echoes ProtocolVersion, RequestId, TrainId, TickId;
  NaN / Infinity cannot be encoded.
- **Synchronous semantics, async I/O**: tick N in → tick N out, using async socket I/O; the UI
  thread never blocks. Requests from the five trains are serialized FIFO (deterministic).
- **Cold start**: both ends warm up serialization (the Pi also its handler/controller) at
  start-up; the handshake and the first controller request on each connection use the connect
  timeout, later requests the strict request timeout. The Pi is published ReadyToRun.
- **Failures** — refused, dropped, timeout, invalid framing, malformed JSON, wrong version /
  train / tick / request id (stale or duplicate), wrong message type, Pi error, invalid numbers:
  the connection is closed, the train gets a fail-safe output (power 0, emergency brake), the
  Main UI shows Faulted. No Software fallback, ever.
- **Link-fault latch** (integration safety policy, provisional): after a fault the train stays
  in fail-safe until the link works AND the driver presses E-brake reset; the Pi state of that
  train is then reset (its estimates did not advance during the outage, so station distance is
  re-learned from the next beacon). Other Hardware trains are unaffected.
- **Test Simulation Reset** also resets all Pi train states (queued until reconnect if the Pi is
  unreachable).
- **Equivalence**: `SoftwareHardwareEquivalenceTests` feeds identical input vectors to both
  controllers (in-process and over loopback TCP) and compares every command and display field.
- Configuration: `TrainController.Wpf/appsettings.json` (`HardwareLink`), Pi command line.
  Deployment: `TrainController.Hardware.Pi/README.md`.

## Provisional configuration defaults

The values below are **not** defined by any project source. They are placeholders so the
system can run, kept in named configuration, and must be replaced when defined. They are not
requirements. (Source-backed vehicle data lives separately in `VehicleSpecification`.)

| Setting | Location | Provisional default |
| ------- | -------- | ------------------- |
| Stopped-speed threshold | `ControllerPolicy.StoppedSpeedThresholdMetersPerSecond` | 0.1 m/s |
| Station arrival distance threshold | `ControllerPolicy.StationDistanceThresholdMeters` | 2.0 m |
| Station braking margin | `ControllerPolicy.StationBrakingMarginMeters` | 0 m (none invented) |
| Authority braking margin | `ControllerPolicy.AuthorityBrakingMarginMeters` | 0 m (none invented) |
| Station dwell time | `ControllerPolicy.StationDwellTimeSeconds` | 30 s |
| Initial Manual/Automatic mode | `ControllerStartupDefaults.InitialOperatingMode` | Manual |
| Initial driver cabin setpoint | `ControllerStartupDefaults.InitialCabinTemperatureSetpointCelsius` | 21 °C (≈ 70 °F) |
| Initial Test Model cabin temperature | `ControllerStartupDefaults.InitialTestCabinTemperatureCelsius` | 21 °C (≈ 70 °F) |
| Initial Test Model `IsActive` | `TestModelState` | false (initial state only) |
| Simulation timestep (dt) | `TrainControllerRuntimeOptions.SimulationTimeStepSeconds` | 0.1 s |
| Speed multipliers | `TrainControllerRuntimeOptions.SupportedSpeedMultipliers` | 1×, 2×, 5×, 10× |
| Hardware request timeout | `HardwareLinkOptions.RequestTimeout` / appsettings `RequestTimeoutMs` | 200 ms |
| Hardware connect timeout | `HardwareLinkOptions.ConnectTimeout` / `ConnectTimeoutMs` | 500 ms |
| Hardware reconnect interval | `HardwareLinkOptions.ReconnectInterval` / `ReconnectIntervalMs` | 2 s |
| Pi endpoint | `HardwareLinkOptions.Host` / `Port` (appsettings.json; `--pi-host` / `--pi-port` per run) | appsettings: 192.168.50.2 : 5050 (team network); code default 127.0.0.1 : 5050 |

### Provisional design decisions (Phase 2)

Not specified by a project source. Rows marked **accepted** were reviewed and accepted as the
current provisional behavior; all remain changeable in one place.

| Behavior | Status | Where |
| -------- | ------ | ----- |
| Track signal lost → service-brake stop (target 0, held once stopped). Configurable to a latched emergency brake. The requirement only mandates braking. | accepted | `ControllerPolicy.TrackSignalLossResponse` (default `ServiceBrakeStop`) |
| Stopped and not ready to depart (target ≈ 0, or doors open / commanded open) → service brake held, preventing unintended motion | accepted | `SoftwareTrainController` step 8 |
| Automatic mode: driver door requests do not control the doors; station logic opens the platform side when stopped at the station. Manual mode: driver door commands, subject to the interlock. | accepted | `DoorController` |
| Driver E-brake reset is allowed only when the passenger request is no longer active and no other emergency condition remains; no stopped-train requirement | accepted | `BrakeController` |
| Track signal invalid before any trusted authority → remaining authority 0 (held if stopped; authority emergency brake if moving) | provisional | `AuthorityTracker`, Pi `HardwareTrainController` |
| Manual mode: arrival ends station braking guidance; doors are the driver's | provisional | `SoftwareTrainController` step 7 |
| Look-ahead of one control period (v·dt) in station/authority braking | provisional | discretization compensation, not a safety margin |
| Overspeed has no tolerance band (none defined) | provisional | `OverspeedProtection` |
| Announcement events and wording ("Next station: X. …" on new beacon, "Arrived at X." on arrival; driver text takes precedence) | provisional | `SoftwareTrainController` |

**Kp = 1.0 / Ki = 0.0 are an untuned, safe startup placeholder — not usable operating gains.**
They are specified by the project brief only as startup values (`EngineerSettings`). With SI
gains (W per m/s) Kp = 1 produces only a few watts, so trains barely move until the Engineer
tunes Kp/Ki for each train. `EngineerSettings.IsUntunedStartupPlaceholder` lets the UI warn
about it; controllers never special-case it, and no test relies on it as a realistic value
(behavior tests use explicitly named test-only gains).
