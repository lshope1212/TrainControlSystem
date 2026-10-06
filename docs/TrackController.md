# Track Controller

The Track Controller now has an executable WPF/MVVM implementation: territory monitoring, movement permissions, equipment commands and feedback, external Boolean PLC loading, and an isolated test UI. It targets the solution's existing .NET 10 / Visual Studio setup.

Use **Controller setup â†’ Load sample layout** to start named territories on the PC, or **Load deploymentâ€¦** for a saved plan. The [territory guide](TrackController-Territories.md) explains the shared map, ownership lists, and PC/Pi execution editor. The [Pi 5 guide](TrackController-Pi5.md) covers Ethernet/SSH, runtime deployment, and LEDs. Selecting **PLC program** alone is a local preview; it does not connect hardware.

## Run an independent demonstration

1. Open `TrainControlSystem.sln`, set `TrackController.Wpf` as the startup project, and build/run.
2. Select **Test UI** in the header. This switches modes and opens **Test inputs** in one action. Every tab now shows an isolated instance of the production controller and pipe host. Test peers send and receive real messages; only their pipe addresses differ. Select **Live feedback** in the header to return to CTC and Track Model data.
3. In **Test setup**, choose **PLC program** and **Load sample PLC**, or choose **Software logic** to run without a PLC. Loading any PLC pauses operation until you explicitly resume. **Controller setup** also provides file loading and the read-only PLC source.
4. Under **1. Track feedback**, choose **Clear route** and **Load preset**. This fills the **Blocks** and **Equipment** editor tabs without sending anything. Review or edit the values, then select **Send feedback**. The draft label shows whether changes are unsent. Presets use the bundled 12-block territory. Select **Resume** in the top toolbar.
5. Select **Send movement request** with the initial values: target 5, front offset 0 ft, path `6, 7, 8`, suggested speed 25 mph, requested authority 1,200 ft. The result should be 25 mph and 1,200 ft.
6. Under **3. Controller response**, inspect the returned decision. Load the **Switch mismatch** preset, then **Send feedback**. Authority becomes zero because the switch connects 6â€“10 while the request needs 6â€“7. Loading and sending **Clear route** restores permission if the request is still current.
7. Try **Occupied route**, **Track fault**, and **Crossing awaiting feedback**. The first limits authority before the occupied block; the latter two withhold it.
8. Uncheck **Repeat sent inputs every second** to exercise timeouts. Feedback expires after 5 seconds; requests expire after 10 seconds. Sending valid feedback and a current request recovers operation.
9. Inspect **Movement permissions** and **Wayside equipment**. Click a block on the schematic for its details. Commanded and reported equipment states remain separate.

The test checkbox repeats only previously sent feedback and the last submitted movement request once per second, through the same shared serializer and named-pipe transport on every send. Editing fields or loading a preset does not transmit them or change the repeated values. **Send feedback** is the single action that sends the feedback editor. Switch and block closure requests are expandable sections under **CTC requests**; manual controls are under **Controller response** and on **Wayside equipment**. A controller command and reported equipment state remain separate.

**Reset test** stops the test host, clears inputs and captured outputs, and restarts paused. It retains applied execution and the current valid PLC, discards unapplied execution drafts, and creates fresh Pi sessions. Load a valid PLC before resetting a routed deployment whose replacement program failed. This is test setup/teardown, not a simulated module message. Switching between **Test UI** and **Live feedback** pauses the outgoing controller; resume the selected mode explicitly when ready. The header mode selection applies to all tabs, and test input controls are hidden in live mode. Routine receipts appear under **Connection details** so repeating inputs do not continually replace the action status.

To move the demo switch, first cancel the movement request and report blocks 5, 6, 7, and 10 clear, fault-free, and unreserved. Send the switch request, then separately report its actual position. A command does not simulate an equipment acknowledgement.

## Architecture

- `TrackController.Core`: validated topology, controller state, route evaluation, a separate authorization guard, maintenance and crossing protection, and the bounded Boolean PLC interpreter. No WPF, file I/O, or transport dependencies.
- `TrackController.Wpf`: views, view models, unit formatting, file dialogs, independent test inputs, message routing, and named-pipe transport. The application creates the controller instances and owns shutdown; window code-behind only initializes the view and assigns its data context; the custom map is presentation code. Asynchronous commands keep transport work off the UI thread.
- `TrackController.Runtime`: the shared application-message receiver, local/Pi runtime boundary, explicit territory-owner router, and Pi connection protocol. The same receiver/controller/program runs on both execution targets.
- `TrackController.Pi`: Linux ARM64 console host for the assigned hardware territory, with an optional Python GPIO Zero indication display. It has no WPF dependency.
- `TrackController.Contracts/Messages/WaysideMessages.cs`: private data definitions shared by the controller core, PC/Pi runtime, and built-in test peers, with SI values. They are not an agreed interface for other team modules; those modules continue to depend on `TrainControl.Contracts`. Boolean C# properties use `Is...` names; explicit JSON names retain the existing wire fields. All wire framing and I/O use the team's `TrainControl.Common.Communication.MessageSerializer` and `NamedPipeTransport`.
- `tests/TrackController.Tests`: controller-owned domain, malformed-input, network, and WPF integration tests. Only this project targets `net10.0-windows`; the teamâ€™s `TrainControl.Tests` project retains its original `net10.0` target and references.

The default territory is an illustrative Green Line wayside with directed blocks 1â€“9 and a branch 6â€“10â€“11â€“12. Blocks are 100 m long, the main route limit is 15 m/s, and the branch limit is 8 m/s. It is not an imported Pittsburgh track layout. Load a JSON territory using **Load territoryâ€¦**; a complete example is bundled beside the PLC. A layout replacement clears feedback and the PLC and pauses execution. Active requests or protected/occupied track prevent replacing a used layout.

## What the Test UI actually tests

Every movement request, switch request, maintenance request, feedback batch, repeating input, and manual command follows this path:

```text
Test UI form / repeating input
  -> existing team message or controller extension
  -> Common.MessageSerializer + Common.NamedPipeTransport.SendAsync
  -> actual named pipe
  -> Common.NamedPipeTransport.ListenAsync
  -> WaysideMessageRouter
  -> production TrackControllerService
  -> production publisher
  -> output named pipes
  -> fake CTC / Track Model receivers
  -> observed outputs in the UI
```

PLC loading, controller mode, maintenance mode, simulation control, and territory loading also have controller-local message contracts and use that same receiver. The test connection cannot call the controller, read its private state, or silently bypass a failed connection. The read-only Test UI output comes from deserialized `WaysideSnapshotMessage` packets on the Track Model output pipe. It also captures CTC's actual `BlockStatusMessage` and `TrainAuthorizationStatusMessage` outputs. The footer reports sent messages and received outputs for both peers. An expired/missing output lease displays unavailable commands instead of a stale approval.

Live and isolated hosts run exactly the same receiver, router, controller class, and output publisher. The isolated host uses a per-run prefix such as `TrainControl.TrackController.Test.<id>` for its input, CTC output, and Track Model output pipes. This allows genuine transport testing without interfering with running live modules. The test does not claim to exercise another teammate's complete application; it impersonates that application's documented interface. The bundled 12-block scenario is still illustrative data.

A completed send only confirms transport delivery, just as in CTC's shared sender. Controller acceptance/rejection is a separate `WaysideAcknowledgementMessage` on its normal Track Model output stream; authorized movement and equipment feedback remain separate from that receipt.

## Movement and protection rules

- A request explicitly identifies its target block, front offset in meters, ordered path of **subsequent** blocks, speed, authority, and request ID. The target must be occupied; occupancy does not identify a train. The optional train label comes only from CTC. If the target itself is a switch, also supply `entryBlockId` to establish the incoming leg.
- Authority is measured from the supplied front offset along the target block and path. A repeated output is the **same anchored limit**, not a fresh distance allowance. Track Model/Train Controller must subtract traveled distance. Use a new request ID for a new origin or route. Once the target clears, its old request can never authorize another occupant.
- Directed topology, ownership, speed limits, occupancy, faults, maintenance, fresh feedback, and switch alignment constrain permission. For bidirectional track, list each allowed direction in `nextBlockIds`; switch definitions identify the stem, normal branch, and reverse branch. Reverse travel must use the aligned branch-to-stem connection; branch-to-branch travel is denied. Front offset is measured along the request's travel direction. Authority never extends beyond the supplied route/territory. Overlapping active requests both stop; this is local movement protection, not inferred CTC reservation ownership.
- Missing/unknown/stale feedback on the evaluated route withholds authority. Valid feedback restores eligibility without erasing maintenance or reported faults. Out-of-order/replayed feedback cannot refresh the watchdog.
- An occupied or closed block ahead ends authority before that block. Closing an occupied or unknown block enters **Pending closure**: new entry is denied; an existing occupant may leave. Fresh clear feedback completes closure. Only an explicit reopen clears it.
- Switch movement requires its entire configured protection zone to be fresh, clear, fault-free, explicitly unreserved, and outside active positive authority. Movement through the switch waits for matching reported alignment. `IsReservedByCtc = null` is unknown and blocks switch movement.
- Crossings close for occupied, unknown, stale, or faulted protection blocks, an active request across the crossing, or a protective manual command. They open only after all those conditions clear. The zone must include approaches in the supplied configuration. Crossing closed feedback is required before movement across it, and signals stay red while waiting.
- Signals default red. A protected route requests a proceed aspect; red/unknown reported signals withhold movement. A Yellow command or feedback caps speed at 5 m/s. That is a documented prototype default, not a braking-distance model.
- Manual commands require maintenance mode, which withholds automatic movement. Switch protection still applies. A manual signal may be set to Red; a proceed aspect requires automatic route protection. A crossing cannot be manually opened over an active protection condition.
- A separate guard rechecks every positive authorization against the resulting block/equipment snapshot and topology. This is a simulation safety implementation, not a certified vital controller, redundant hardware platform, or formal safety proof.
- Watchdogs use wall time, independent of pause and the 1Ã—â€“10Ã— simulation clock. Defaults: feedback 5 s, request 10 s. Configuration accepts feedback 1â€“60 s and request 1â€“120 s.

## Boolean PLC files

`Samples/wayside.plc` is a working external program. Load it from disk; there is intentionally no embedded editor. The source viewer is read-only.

```text
allow_route = route_safe and running
switch_normal = requested_normal
signal_clear = route_safe and running
crossing_close = crossing_required
```

Inputs are `route_safe`, `switch_safe`, `requested_normal`, `crossing_required`, and `running`. Required outputs are `allow_route`, `switch_normal`, `signal_clear`, and `crossing_close`. Temporary Boolean names may be assigned once and used by later lines. Names are case-sensitive. Operators: `not`/`!`, `and`/`&&`, `or`/`||`, parentheses, and `true`/`false`; precedence is NOT, AND, OR. `#` begins a comment; trailing semicolons are optional. Arithmetic, loops, host-language code, unknown names, duplicate assignments, and missing outputs are rejected. Limits: 32,768 source characters, 128 assignments, 256 tokens per expression, and nesting depth 32.

The controller supplies route inputs per movement request and equipment inputs per equipment evaluation. PLC outputs are always subject to the independent protection rules: a program returning `true` cannot grant authority into an occupied block or open a protected crossing. An invalid replacement unloads the previous program and remains paused. Software mode runs its own route/equipment rules without requiring a PLC.

## Module integration

The live input pipe is `TrainControl.TrackController`. It accepts one UTF-8, newline-terminated JSON envelope per connection using the framing already sent by `CTC.Wpf.Services.NamedPipeMessageSender`:

```json
{"messageType":"MaintenanceRequestMessage","payload":{"blockId":"12","requestedState":"Closed"}}
```

Supported inputs (identical in live and test hosts): `WaysideConfigurationMessage`, `WaysideFeedbackMessage`, `WaysideMovementRequestMessage`, `WaysideSimulationMessage`, `WaysideManualCommandMessage`, `WaysideMaintenanceModeMessage`, `WaysideControllerModeMessage`, `WaysidePlcProgramMessage`, `WaysideSnapshotRequest`, and the existing `MaintenanceRequestMessage`, `SwitchPositionRequestMessage`, `BlockStatusMessage`, and `TrackStateMessage`. Live transport never exposes test reset.

The `Wayside*` inputs and outputs are controller extensions used by the built-in test peers and Pi runtime. They are exposed identically on live and isolated test hosts, but are not yet agreed inter-module contracts. Adopting them for CTC or Track Model requires a team-approved shared contract or an adapter inside Track Controller; other modules should not reference `TrackController.Contracts`.

Use the Common serializer rather than building JSON by hand. Existing shared feedback is timestamped on arrival; it cannot provide reservation confirmation or all richer fault data. The test/Pi extension `WaysideFeedbackMessage` carries explicit measurement timestamps, optional actual speed, reservation status, and equipment feedback. Mode/program requests are operator/simulation controls; they still pass through the same validation boundary in the test UI.

```json
{"messageType":"WaysideMovementRequestMessage","payload":{"requestId":"example-1","trainId":"Train 101","targetBlockId":"5","frontOffsetMeters":0,"pathBlockIds":["6","7","8"],"suggestedSpeedMetersPerSecond":11.176,"suggestedAuthorityMeters":365.76,"active":true}}
```

```json
{"messageType":"WaysideFeedbackMessage","payload":{"blocks":[{"blockId":"5","observedAtUtc":"2026-10-02T12:00:00Z","occupancy":"Occupied","signal":"Unknown","switch":"Unknown","crossing":"Unknown","faulted":false,"reservedByCtc":false}]}}
```

Replace the example timestamp with the actual current observation time. Update all relevant blocks before requesting movement. A cancellation uses the current request ID and target with `active:false`.

The transport is the team's one-way, one-envelope-per-connection protocol. There is no duplex reply on the input connection. The controller publishes `WaysideAcknowledgementMessage` on the Track Model output pipe after handling an input. An acknowledgement means the input was accepted, **not** that movement was granted or equipment changed. Send a `WaysideSnapshotRequest` with an empty payload object to request a snapshot on that same output pipe:

```json
{"messageType":"WaysideSnapshotRequest","payload":{}}
```

The CTC/input endpoints use the unchanged shared `NamedPipeNames`. The Track Model output name is declared locally in `WaysideEndpoints.TrackModelInput`:

- `TrainControl.CTC`: `BlockStatusMessage` (measured occupancy/equipment, never commanded equipment) and `TrainAuthorizationStatusMessage`, matching the CTC receiver added on current `main`. Cancellation publishes a zero authorization for the previous train label. Multiple active records with the same label produce a conservative zero indication.
- `TrainControl.TrackModel`: full `WaysideSnapshotMessage` packets plus application result messages. Snapshots contain block statuses, commanded/reported equipment, and movement decisions. Only snapshots are actuator/state data; a receipt is not an actuator command.

Each destination publishes independently about every 0.5 seconds when connected; a missing CTC receiver does not block Track Model publication. The shared transport's completed send means delivery only. Failed connections are reported in the live footer and retried. No positive outputs are queued for later delivery. Each snapshot carries `EvaluatedAtUtc` and `ValidUntilUtc` (at most 1.5 seconds, shortened to relevant feedback/request expiry). The receiver must reject old/expired snapshots, stop when its lease expires, and preserve the anchored-authority semantics. CTC's legacy authorization message is for its displayed authorization state; the richer snapshot remains the movement/actuator contract.

**Remaining integration work:** CTC's old outbound movement messages still contain only a train label and numeric suggestions, so they cannot safely identify a block or route. They are explicitly rejected. Integration needs an agreed shared message with the missing route/origin fields and a controller-side mapping to its internal request; no CTC changes are included here. CTC now has receivers for its standard status messages; its loaded block IDs must agree with this controller's configured territory. Track Model integration still needs agreed actuator messages and an adapter; the controller-specific snapshot is currently exercised by the test peer. The Pi runtime, SSH link, ownership routing, and optional indication LEDs are implemented; device testing and final territory allocation remain. Requests spanning owners and inter-territory handoff are not implemented. Whole-system operation still needs those adapters and team-agreed track data, approach distances, braking policy, and timing thresholds.

## Verification

```powershell
dotnet build TrainControlSystem.sln
dotnet test TrainControlSystem.sln
```

Tests cover positive and restricted movement, software/PLC modes, unsafe PLC output clamping, malformed programs, stale/replayed feedback, fault recovery, switch interlocks, crossing protection, pending maintenance, direction/topology validation, conflicts, old-origin retirement, defensive copying, SI limits, simulation timing, malformed JSON, legacy send-only framing, snapshot publication, isolated peer receivers, send-only framing, and independent test-state isolation. Integration tests run the actual Test UI commands on a WPF dispatcher, observe results exclusively over the output pipes, and verify that stopped receivers and missing output listeners cannot be concealed by direct in-process calls.
