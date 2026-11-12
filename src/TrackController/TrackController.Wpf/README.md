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
**Load setup**. The Green Line sample starts two territories on the PC;
the Blue Line sample starts one. Both load the sample PLC and start paused. In the
Test UI load **Clear route** and select **Send feedback**, then select **Resume**
in this window and **Send movement request** in the Test UI.

Choose **Saved setup file…** in the same selector and select **Load setup** to
open a `.deployment.json` file. A setup includes its track, territory ownership,
PC/Pi assignments, and PLC program. Loading requires pause and fresh feedback
afterward. Choosing an item alone does not replace the active setup.

The collapsed **Advanced** section contains **Replace selected PLC…** to change only the
selected territory’s program, preserving the track and other PLC assignments. The built-in setup already loads
its PLC, so no additional program-loading step is needed for a demonstration.

**Advanced → Use standalone controller** returns to the
original PC controller for software-logic or single-territory tests. It does not
load the selected line or a PLC automatically. Check the logic/program status;
use **Change territory…**, the logic selector, and **Replace selected PLC…** for this advanced workflow.

The footer shows CTC and Track Model connection states together and changes only
when a receiver becomes available or unavailable. Routine feedback and output
publication continue without replacing operator messages; rejected inputs remain visible.

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

Manual command acceptance or rejection stays visible beside the manual controls
in **Wayside equipment**. Switch rejection identifies the occupied, unavailable,
faulted, reserved, or authority-protected block. A paused CTC switch request
requires Resume or maintenance mode; PLC mode also requires a valid program.
For a sample switch demonstration, send the Test UI's **All blocks clear** preset
first. A successful command changes **Command**; **Reported** changes only when
Track Model or the Test UI sends the actual position.

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

- A request identifies its occupied target block, ordered path of **subsequent** blocks, speed, route limit, and request ID. It does not require a train position within the block. The optional train label comes only from CTC; occupancy does not identify a train. If the target itself is a switch, also supply `entryBlockId` to establish the incoming leg.
- Request and output `authorityReference` is `TargetBlockEntry`: the numeric limit is measured from the target block's entry along the ordered route. It defines a fixed endpoint, **not remaining distance from the train**. For a 100 m target block followed by another 100 m block, a 150 m limit ends 50 m into the second block regardless of where the train is. The receiver that knows train position must calculate remaining distance to that endpoint; do not restart the allowance on repeated output or assume an unknown position is zero. Use a new request ID for a new target block or route. Once the target clears, its old request cannot authorize another occupant. Old requests carrying `frontOffsetMeters` are rejected rather than silently reinterpreted.
- Directed topology, ownership, speed limits, occupancy, faults, maintenance, fresh feedback, and switch alignment constrain permission. For bidirectional track, list each allowed direction in `nextBlockIds`; switch definitions identify the stem, normal branch, and reverse branch. Reverse travel must use the aligned branch-to-stem connection; branch-to-branch travel is denied. Authority never extends beyond the supplied route/territory. Overlapping active requests both stop; this is local movement protection, not inferred CTC reservation ownership.
- For an unchanged CTC movement request, polls and identical renewals keep the issued nonzero authority fixed. Improving track conditions cannot extend it; conditions that would shorten it produce **zero** instead. This guard applies after both software and PLC evaluation.
- At a block crossing, the prepared successor takes over and the predecessor is retired atomically. The new authority is the actual previously granted authority minus the previous block's full length, capped by the successor's request. A restriction clearing at the crossing must not extend that endpoint. Retired requests do not block the new movement while CTC sends its cleanup cancellation.
- CTC owns departure and replanning. Its existing cancel/reissue flow, replacement request IDs, explicit authority-limit updates and release from zero commanded speed start a new movement evaluation, including within the current block. Cancellation still withdraws authority immediately; the replacement must pass every route, occupancy, fault, freshness and PLC check. This compatibility behavior permits CTC-directed mid-block reissues; deferring every replan to the next boundary requires a coordinated CTC interface change.
- Station dwell may use both speed and authority zero. The departure request includes the **full current block length plus the onward blocks**, with no inferred position offset. Track Model does not currently populate optional actual-speed feedback, so a measured zero speed is not required to accept CTC's departure or replacement request.
- Missing/unknown/stale feedback on the evaluated route withholds authority. Fresh safe feedback can restore the existing CTC permission, up to its issued limit; there is no separate stopped-speed latch. Maintenance, faults, pause and request expiry remain vetoes. Out-of-order/replayed feedback cannot refresh the watchdog.
- Before the first grant, an occupied or closed block ahead can restrict authority to end before that block. After a grant, a new restriction that shortens it withdraws authority to zero. Closing an occupied or unknown block enters **Pending closure**: new entry is denied; an existing occupant may leave. Fresh clear feedback completes closure. Only an explicit reopen clears it.
- Switch movement requires its entire configured protection zone to be fresh, clear, fault-free, explicitly unreserved, and outside active positive authority. Movement through the switch waits for matching reported alignment. `IsReservedByCtc = null` is unknown and blocks switch movement.
- Crossings close for occupied, unknown, stale, or faulted protection blocks, an active request across the crossing, or a protective manual command. They open only after all those conditions clear. The zone must include approaches in the supplied configuration. Crossing closed feedback is required before movement across it, and signals stay red while waiting.
- Signals default red. A protected route requests a proceed aspect; red/unknown reported signals withhold movement. Super Green is available as reported feedback and adds no signal speed cap, as with Green. Track speed limits, CTC requested speed, granted authority, and all protection checks still apply. A Yellow command or feedback caps speed at 5 m/s. That is a documented prototype default, not a braking-distance model.
- Manual commands require maintenance mode, which withholds automatic movement. Switch protection still applies. A manual signal may be set to Red; a proceed aspect requires automatic route protection. A crossing cannot be manually opened over an active protection condition.
- A separate guard rechecks every positive authorization against the resulting block/equipment snapshot and topology. This is a simulation safety implementation, not a certified vital controller, redundant hardware platform, or formal safety proof.
- Watchdogs use wall time, independent of pause and the 1×–10× simulation clock. Defaults: feedback 5 s, request 10 s. Configuration accepts feedback 1–60 s and request 1–120 s.

## Boolean PLC files

Load the updated `Samples/wayside.plc` through the selected territory's PLC control. The source viewer is read-only. The program uses individual Boolean track facts and bit-vector circuits to calculate its commands; see [Vital Boolean PLC control](#vital-boolean-plc-control) and the linked language contract. Legacy permission-only files are no longer accepted.

## Module integration

The live input pipe is `TrainControl.TrackController`. It accepts one UTF-8, newline-terminated JSON envelope per connection using the framing already sent by `CTC.Wpf.Services.NamedPipeMessageSender`:

```json
{"messageType":"MaintenanceRequestMessage","payload":{"blockId":"12","requestedState":"Closed"}}
```

Shared inputs are `MovementSuggestionMessage`, `TrackModelBlockStateMessage`, `MaintenanceRequestMessage`, and `SwitchPositionRequestMessage`. Legacy `BlockStatusMessage` input is also accepted. Movement suggestions include request ID, target block, entry block, route, and fixed authority measured from the target block entry. CTC now supplies this context in its shared suggestion. Feedback uses the shared Track Model state directly, including individual failures and closure, measurement time, optional speed, and optional reservation status.

Use `MessageSerializer.Serialize` for shared contracts. Small UI controls use `MessageSerializer.CreateEnvelope(operation, payload)`; they need no separate message classes:

| Operation | Payload fields |
| --- | --- |
| `SetSimulation` | `paused`, `speedMultiplier` |
| `SetControllerMode` | `hardwareMode` |
| `SetMaintenanceMode` | `enabled` |
| `LoadPlc` | `name`, `source`, optional `territoryId` |
| `ApplyManual` | `blockId`, optional `switchPosition`, `signal`, `crossing` |
| `GetSnapshot` | empty object |
| `TrackModelBlockStates` | `blocks`: array of shared `TrackModelBlockStateMessage` values |

A batch validates all measurements before changing the local controller. Single Track Model updates and batches use the same controller method. The test UI sends batches; the actual Track Model sends one batch per line. The controller publishes its outputs in batches too: one `BlockStatusBatchMessage` to CTC and one `TrackModelCommandBatchMessage` to the Track Model per 500 ms cycle, with acknowledgements to CTC sent ahead of them. The Pi client translates measurement timestamps to the Pi clock. Reservation `null` remains unknown and cannot unlock a protected switch.

```json
{"messageType":"MovementSuggestionMessage","payload":{"requestId":"example-1","trainId":"Train 101","targetBlockId":"5","authorityReference":"TargetBlockEntry","pathBlockIds":["6","7","8"],"suggestedSpeedMetersPerSecond":11.176,"suggestedAuthorityMeters":365.76,"active":true}}
```

```json
{"messageType":"TrackModelBlockStateMessage","payload":{"blockId":"5","observedAtUtc":"2026-10-22T12:00:00Z","occupancy":"Occupied","signal":"Green","switch":"Normal","crossing":"Closed","brokenRail":false,"isReservedByCtc":false}}
```

Replace the example timestamp with the actual measurement time. Update relevant blocks before requesting movement. A cancellation uses the current request ID and target with `active:false`.

`WaysideConfigurationMessage` configures the standalone runtime. Routed territory ownership is configured through deployment loading. Live transport does not expose test reset.

The input connection remains one-way. Outputs use separate pipes:

- `TrainControl.CTC`: shared `BlockStatusMessage` containing measured block/equipment status every 500 ms, stamped with `reportedAtUtc` (the evaluation time) so CTC can drop late reports and detect that reports stopped. The status of a movement request's target block also carries `movement`: the request ID and the authorization actually commanded to the Track Model (Approved/Restricted with speed and authority, or Withheld with the reason). Each CTC request (`MovementSuggestionMessage`, `MaintenanceRequestMessage`, `SwitchPositionRequestMessage`) is answered with a shared `RequestAcknowledgementMessage` carrying its request ID and whether it was accepted for evaluation or rejected (with the reason). Acknowledgements are queued by the input receiver and sent by the single CTC publisher, so a busy CTC never stalls the controller's input pipe.
- `TrainControl.TrackModel`: shared `TrackModelCommandMessage`, combining commanded equipment with authorized movement for the occupied target block.
- `TrainControl.TrackController.Monitor`: `WaysideSnapshotMessage` and `WaysideAcknowledgementMessage` for UI/testing. An acknowledgement means an input was accepted; inspect the resulting authorization and measured equipment to confirm its effect. `GetSnapshot` publishes here.

Both module destinations share `PublishAsync`, their output projection, and `TrySendAsync`. The monitor is separate. Snapshot validity is bounded to 1.5 seconds and shortened by feedback/request expiry; the Pi client stops outputs on expiry or communication loss.

Remaining integration limits: route handoff between owners is not implemented, so a request whose route leaves its owner's territory is rejected. CTC now sends a complete movement request for the train's current block on every block change and renews it within the request watchdog; it no longer sends its separate release intent. The Track Model does not report `IsReservedByCtc`, so with the real Track Model switch protection reports "missing CTC reservation feedback" and switch requests are refused. A route through the signalled blocks (6, 11) is first prepared: the PLC's clearance scan (`signal_clearance`) commands the signal when the route is otherwise safe, and authority is granted only once the Track Model reports the proceed aspect (see *Signalled routes* in the integration guide). Deployment territories and track IDs must agree with the Track Model layout. See `docs/integration/LIVE_INTEGRATION_GUIDE.md`.

## Verification

```powershell
dotnet build TrainControlSystem.sln
dotnet test TrainControlSystem.sln
```

Tests cover positive and restricted movement, software/PLC modes, unsafe PLC output rejection, malformed programs, stale/replayed feedback, fault recovery, switch interlocks, crossing protection, pending maintenance, direction/topology validation, conflicts, old-origin retirement, defensive copying, SI limits, simulation timing, malformed JSON, legacy send-only framing, snapshot publication, isolated peer receivers, send-only framing, and independent test-state isolation. Integration tests run the actual Test UI commands on a WPF dispatcher, observe results exclusively over the output pipes, and verify that stopped receivers and missing output listeners cannot be concealed by direct in-process calls.

## Per-territory PLC assignment

Pause, then open Controller setup and select a **PLC territory** under Controller logic. The source viewer shows that assignment. Under Advanced, choose **Replace selected PLC…**. The replacement is validated before sending and targets only that territory on PC or Pi; inspect the reported PLC status before explicitly resuming. Reset and Apply execution retain the individual assignments. **Save applied choices** saves portable PLC copies and assignment references next to the loaded deployment file.

The deployment-level `plcFile` remains the default. Each entry in `territories` may override it with its own relative `plcFile`. Every referenced PLC is validated before runtimes are created. Paths must remain inside the deployment folder. Existing deployments need no changes. A PLC message with `territoryId` targets that owner; omitting it retains the legacy broadcast behavior.

## Vital Boolean PLC control

The assigned PLC now owns route interlocking, numeric speed/authority selection using explicit Boolean gates, signals, switch interlocking, crossings, and closure completion. C# supplies individual facts and encoded input words, decodes the PLC output words, and independently vetoes unsafe results. It no longer supplies precomputed route permission or normal numeric movement limits to the PLC. See the [language and execution contract](../TrackController.Core/Control/README.md).

Use the updated `Samples/wayside.plc`, beginning with `plc boolean_v1`. Older permission-only programs are rejected. Rebuild/redeploy the Pi package for protocol version 8. Source and programs are the same on PC/Pi; territory assignment selects the file used by each controller.
