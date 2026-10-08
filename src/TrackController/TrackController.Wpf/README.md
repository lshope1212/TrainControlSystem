# TrackController.Wpf

Operator application for the Track Controller. It owns one controller host and
receives CTC and Track Model inputs through the team's named-pipe transport.
The [separate Test UI](../TrackController.TestUI.Wpf/README.md) simulates those peers.

## Running it

Set **TrackController.Wpf** as the startup project in Visual Studio, or run:

```powershell
dotnet run --project src/TrackController/TrackController.Wpf
```

For a demonstration, also start **TrackController.TestUI.Wpf**. In the operator
window choose **Controller setup**, pick a line in the selector, and select
**Load sample layout**. The Green Line sample starts two territories on the PC;
the Blue Line sample starts one. Both load the sample PLC and start paused. In the
Test UI load **Clear route** and select **Send feedback**, then select **Resume**
in this window and **Send movement request** in the Test UI.

## Blue Line prototype

The Iteration 2 course track is bundled as `Samples/blue-line-*.json`. It follows
the Blue Line sheet of `Track Layout & Vehicle Data vF5.xlsx` and the Track
Model's `blue-line.json`: fifteen 50 m blocks at 50 km/h, with the same block IDs
(`1`–`15`) and line name (`Blue Line`) the Track Model publishes.

| Feature | Blocks |
| --- | --- |
| Yard side, then straight run | 1 → 2 → 3 → 4 → 5 |
| Railway crossing | 3 (protection zone 2, 3, 4) |
| Switch | 5 (stem 4, **Normal → 6**, **Reverse → 11**; zone 4, 5, 6, 11) |
| Signals ("lights") | 6 and 11, one per branch |
| Upper branch, ends at Station B | 6 → 7 → 8 → 9 → 10 |
| Lower branch, ends at Station C | 11 → 12 → 13 → 14 → 15 |

| Deployment file | Territories |
| --- | --- |
| `blue-line-all-pc.deployment.json` | One territory, all 15 blocks, on the PC. Any route to either station is possible. |
| `blue-line-pc-and-pi.deployment.json` | `wayside-a` (blocks 12–15, PC) and `wayside-b` (blocks 1–11, Pi). |

The switch zone (4, 5, 6, 11) and crossing zone (2, 3, 4) overlap at block 4, so
blocks 2–6 and 11 must share an owner. The split keeps the yard, junction and
Station B on one territory; a route to Station C ends at the territory boundary
because requests spanning owners are rejected (see the
[territory guide](../TrackController.Runtime/README.md#change-territory-boundaries)).

The map is outbound only (block 1 toward the stations), because the workbook has no
direction column; the Track Model lists the same blocks as bidirectional. The
controller, shared PLC and Test UI contain no Blue-specific code: the Test UI
derives its presets from the switch and its approach block, so **Clear route** is
a train on block 4 requesting `5, 6, 7`. For Station C, the switch must move
while its zone is clear: with blocks 4, 5, 6 and 11 clear, send the switch
request for block 5 as Reverse and report the switch Reverse, then occupy block 4
and request `5, 11, 12`.

PLC loading, controller logic, maintenance mode, manual equipment commands,
Pause/Resume, and PC/Pi placement belong to the operator window. The Test UI
contains simulated measurements, CTC requests, and captured controller outputs.
There is no Test/Live switch: either the real modules or their test replacements
connect to the same controller interface.

**Reset controller** pauses and clears feedback and movement requests. It retains
the applied deployment and PLC. Stop Test UI repeats before resetting; otherwise
the external simulator will continue sending measurements. Send fresh feedback
and resume explicitly afterward.

## Project organization

- `TrackController.Core`: domain state, topology validation, route/equipment
  protection, authorization guard, and Boolean PLC interpreter. No WPF or I/O.
- `TrackController.Wpf`: views, view models, operator commands, file dialogs,
  named-pipe host, and application startup/shutdown.
- `TrackController.Runtime`: local/Pi execution and territory routing. See its
  [territory guide](../TrackController.Runtime/README.md).
- `TrackController.Pi`: console host and optional indication LEDs. Its
  [README](../TrackController.Pi/README.md) and `scripts` directory cover deployment.
- `TrackController.Contracts`: private PC/Pi and prototype message definitions.
  Other team modules continue to reference `TrainControl.Contracts`.
- `TrackController.TestUI.Wpf`: external-module simulator. It has no reference to
  the controller core, runtime, or operator application.

Automated tests remain in the existing repository `tests` directory:
`TrackController.Tests` targets `net10.0` for core/runtime tests;
`TrackController.Wpf.Tests` targets `net10.0-windows` for UI/transport tests.

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
- Watchdogs use wall time, independent of pause and the 1×–10× simulation clock. Defaults: feedback 5 s, request 10 s. Configuration accepts feedback 1–60 s and request 1–120 s.

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

Supported inputs (identical in the production host): `WaysideConfigurationMessage`, `WaysideFeedbackMessage`, `WaysideMovementRequestMessage`, `WaysideSimulationMessage`, `WaysideManualCommandMessage`, `WaysideMaintenanceModeMessage`, `WaysideControllerModeMessage`, `WaysidePlcProgramMessage`, `WaysideSnapshotRequest`, and the existing `MaintenanceRequestMessage`, `SwitchPositionRequestMessage`, `BlockStatusMessage`, and `TrackStateMessage`. Live transport never exposes test reset.

The `Wayside*` inputs and outputs are controller extensions used by the external Test UI peers and Pi runtime. They are exposed identically on the production host, but are not yet agreed inter-module contracts. Adopting them for CTC or Track Model requires a team-approved shared contract or an adapter inside Track Controller; other modules should not reference `TrackController.Contracts`.

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
