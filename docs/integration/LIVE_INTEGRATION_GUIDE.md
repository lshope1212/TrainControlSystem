# Live integration: Track Model, Track Controller and CTC

How the real modules exchange track information for the whole course network (the Blue, Red and
Green Lines, 241 blocks) without their TestUIs, how to demonstrate it, and what is still
outstanding. How CTC physically dispatches a train and how the Train Model's position becomes block
occupancy is described in [Physical dispatch and train position](#physical-dispatch-and-train-position);
how movement authority reaches the Train Model and the Train Controller across block boundaries in
[Movement Authority Continuity and Emergency
Revocation](#movement-authority-continuity-and-emergency-revocation).

## Message flow

```text
                 TrackLayoutMessage: all three lines in one message (start-up, layout change,
                 snapshot request; retried until delivered)
   Track Model ─────────────────────────────────────────────────────────────────────────────┐
     │  ▲                                                                                     │
     │  │ TrackModelCommandBatchMessage (every 500 ms: speed, authority, switch, signal,      │
     │  │ crossing for every block)                                                           ▼
     │  └──────────────── Track Controller ── BlockStatusBatchMessage (every 500 ms) ─────►  CTC
     │                        ▲   ▲               all blocks, + reportedAtUtc, + decisions   │  │
     │ TrackModelBlockStates  │   └── RequestAcknowledgementMessage (per CTC request,  ───► │  │
     │ (one batch per line,   │       sent ahead of the status batch)                       │  │
     │ on change and on a     │◄──── MovementSuggestionMessage / Maintenance / Switch ───────┘  │
     │ 1 s heartbeat)         │                                                                 │
     └────────────────────────┘                                                                 │
     ▲                                                                                          │
     └──────────────────── TrackModelSnapshotRequestMessage (until CTC has a layout) ──────────┘

   Launcher (shared clock) ── SystemTimeMessage ──► CTC          CTC ── SimulationTimeControlMessage ──► Launcher
```

1. **Layout.** The Track Model publishes its layout, all three lines in one `TrackLayoutMessage`, when
   it starts, when its layout changes, and whenever a `TrackModelSnapshotRequestMessage` arrives. If
   CTC is offline, the Track Model retries on every heartbeat until CTC has that revision; a CTC that
   has no layout also asks for one every 3 s. The layout is not resent with occupancy updates or
   clock ticks. CTC reconciles it per line: a repeated equivalent layout changes nothing; a topology
   change rebuilds a line only if no train is released on it, otherwise that line keeps its topology
   and CTC reports why.
2. **Measured state.** The Track Model sends its block states to the Track Controller as one
   `TrackModelBlockStates` batch per line (the controller's existing batch input) on every state
   change, and, when nothing changed for 1 s, as a heartbeat with a fresh observation time (wall
   clock, independent of simulation speed/pause). The Track Controller's 5 s watchdog therefore
   never marks a healthy track stale, and does mark it stale within 5 s of the Track Model stopping.
   A controller that does not own a line rejects that line's batch only.
3. **Wayside status.** Every 500 ms the Track Controller sends CTC one `BlockStatusBatchMessage`:
   every owned block's status with the measured occupancy, signal, switch, crossing and confirmed
   maintenance, its evaluation time (`reportedAtUtc`) and, on a movement request's target block, the
   decision it actually commanded (`movement`). CTC applies the entries one by one exactly as it
   applies a single `BlockStatusMessage` (still accepted, e.g. from the CTC TestUI): an entry for an
   unknown block is reported and skipped, never remapped. CTC ignores a report older than one it
   already applied, and treats a block whose reports stop for 3 s as Unknown (stale).
4. **Requests and answers.** CTC's movement, maintenance and switch requests carry a request ID.
   The Track Controller answers each with a `RequestAcknowledgementMessage`:
   `AcceptedForEvaluation` or `Rejected` with the reason. Transport delivery, acceptance, and the
   actual authorization (or confirmed closure / reported switch position) are four separate
   facts in CTC and in its UI.

## The course network

The production Track Model loads the three course lines together (`CourseTrackLayout`, bundled
`src/TrackModel/SampleLayouts/course-lines.json`). That file is the course workbook
*Track Layout & Vehicle Data vF5* imported by the Track Model's own XLSX importer and exported as
JSON, so it can be regenerated the same way (Track Model → **Import layout** → the workbook →
**Export layout**). Section, number, length, grade, elevation, speed limit, station, crossing and
switch data come from the workbook's Blue, Red and Green worksheets.

| Line | Blocks | Block IDs | Switches (switch block: Normal / Reverse) | Crossings | Signals |
| --- | ---: | --- | --- | --- | --- |
| Blue | 15 | `1`–`15` | 5: 6 / 11 | 3 | 6, 11 |
| Red | 76 | `RED-1`–`RED-76` | 16: 15/1 · 27: 28/76 · 33: 32/72 · 38: 39/71 · 44: 43/67 · 52: 53/66 | 11, 47 | none in the workbook |
| Green | 150 | `GREEN-1`–`GREEN-150` | 13: 12/1 · 28: 29/150 · 77: 76/101 · 85: 86/100 | 19, 108 | none in the workbook |

**How the Red and Green worksheets were converted.** The worksheets list blocks and their
equipment but not connections, so the importer applies documented rules and checks the result
against the vF5 switch layout. A workbook that does not match is rejected.

- `SWITCH (a-b; c-d)`: the switch is on the block common to both pairs; Normal goes to the other end
  of the first pair, Reverse to the other end of the second pair (the same convention as Blue's
  "(5 to 6) or (5 to 11)"). The workbook writes some switches on the neighbouring row (Red 15/16,
  32/33, 43/44; Green 12/13, 76/77). The model puts them on the block where both legs meet.
- Consecutive block numbers are connected, except between two switch-branch ends (Red 66-67 and
  71-72, Green 100-101), which join their switches instead.
- Direction of travel (`travelDirection`) is not a worksheet column; it is taken from the
  workbook's Track Layout drawing. Red is bidirectional throughout. On Green, A–C (1–12) runs toward
  block 1 and leaves to D13. D–F (13–28) and N (77–85) are bidirectional. Everything else runs in
  increasing block order (28→29…→76→77, 85→86…→100→85, 77→101…→150→28).

The result agrees, block for block, with the CTC TestUI fixture (`course-vf5-layout.json`), which
was generated from the same workbook independently: same sections, lengths, grades, elevations,
speed limits, stations, crossings and connections. The automated test
`TrackModelCourseLayout_AgreesWithTheIndependentCtcFixture` checks this.

**Data the workbook does not contain** (identified, not invented):

| Item | How it is handled |
| --- | --- |
| The yard is not a block. Yard switches: Red 9 ("SWITCH TO/FROM YARD (75-yard)"; the "75" contradicts its row), Green 58 (57-yard) and 62 (yard-63) | Not modelled as track; those blocks are plain main-line blocks. Trains are placed in their start block, as on Blue. The main-line blocks a train enters from the yard are marked `IsYardEntry` (see *Yard entries*). |
| Signals ("Light") and transponders on Red and Green | None modelled. The Track Controller therefore has no signal to clear on these lines. |
| Green block 16 is a station with no name | Shown as "Green 16 (unnamed station)". |
| Station side (Left/Right), "UNDERGROUND" | Not part of the Track Model's block model or of the shared contracts. |
| Heaters, ambient temperature, waiting passengers | The same documented simulation defaults as the Blue Line. |

## Yard entries

A train enters service only from a **yard entry**: a main-line block the Track Model's layout marks
`IsYardEntry` (`TrackBlock.IsYardEntry`, published as `TrackBlockDefinition.IsYardEntry` in the
existing `TrackLayoutMessage`). CTC derives each line's permitted starting blocks from that flag
alone (`CTC.Core.Scheduling.YardEntries`) and never infers one from the lowest block number, a
switch or a station. The bundled course data marks:

| Line | Yard entry | Evidence |
| --- | --- | --- |
| Blue | `1` (A1) | The worksheet drawing runs Yard → 1 → 2 …; Blue has no yard-switch row. |
| Red | `RED-9` (C9) | Row 9 is the only yard switch, "SWITCH TO/FROM YARD (75-yard)". **Uncertain:** its leg text names 75, which contradicts the row (block 75 is nowhere near 9), so the entry rests on the switch row alone (and on both schematics joining the yard at 9). If the corrected data names 8 or 10, change the flag there. |
| Green | `GREEN-63` (K63) | Row 62, "SWITCH FROM YARD (Yard-63)": the yard joins block 63. Row 58, "SWITCH TO YARD (57-yard)", is the way *into* the yard from 57, so `GREEN-57` is an exit, not an entry; the switch rows 58 and 62 are not entries; `GREEN-1` and `GREEN-14` have no yard evidence. |

The XLSX importer derives these from the yard text and rejects a workbook whose yard text does not
state them; a JSON layout without the field, or a CSV without an `IsYardEntry` column, has no yard
entries, and CTC then refuses to schedule on that line with an explanation. A layout revision that
removes a yard entry drops any not-yet-released schedule starting there (reported in the layout
summary); released trains are unaffected.

In the Schedule Builder each train has its own starting-block selector offering only the line's
yard entries (preselected when there is just one). That train's time in its starting block is its
departure time; the converter builds the route from that block, so `ScheduledTrain.Route[0]`,
`ScheduledTrain.StartBlockId` and `TrainDispatchRequestMessage.StartBlockId` are the selected block.
`CTCService.QueueSchedule` enforces the same rule for every caller.

**Routing limitation.** CTC routes by connectivity only (fewest blocks, no block twice). It does
not apply the Track Model's direction of travel or switch geometry, which the Track Controller does.
The Schedule Builder therefore reports, after queueing, any step against the direction of travel or
any pass through a switch from one leg to the other (`RouteGeometry`); it does not change or block
the route. For example, from `GREEN-63` to Edgebrook (`GREEN-9`) the shortest connected route runs
63 → 62 → … → 29 against Green's one-way direction and is refused by the Track Controller ("No
permitted connection from GREEN-63 to GREEN-62"); forcing it forward (timing e.g. `GREEN-120`)
gives 76 → 77 → 101, two legs of switch 77, which the Track Controller cannot align. The legal route
(… 76, 77, 78 … 85, 86 … 100, 85, 84 … 77, 101 … 150, 28 … 13, 12 … 9) passes 77–85 twice, which
CTC's planner cannot produce.

## Identifiers

**Block IDs.** The Track Model layout is the authority. A block ID is an opaque string used verbatim
by every module: the Track Model, the Track Controller track maps and CTC. It is unique across all
three lines, because block-level messages carry no line ID. Section letters and block numbers are
separate fields (`TrackBlockDefinition.Section` / `BlockNumber`), never parsed out of the ID. CTC
displays them as its block labels (e.g. `H40` for `RED-40`, `K65` for `GREEN-65`).

- Blue keeps its production IDs `1` to `15`.
- Red and Green IDs are qualified at their source: `RED-n`, `GREEN-n` (n = workbook block number).
  The Track Model, the Track Controller maps and ownership files, and CTC all use these exact IDs.
  No module converts IDs.
- Duplicates are rejected everywhere: by the Track Model's layout validator, by the Track
  Controller's deployment loader ("Block ID … is on both …"), and by CTC's layout reconciler.
- The CTC TestUI's sample layout (`BLUE`/`A1`…, `RED-A1`…, `GREEN-A1`…) remains a standalone test
  fixture with its own IDs. If the real Track Model starts later, its layout replaces the sample
  lines, as long as no train is released on them.

**Train IDs.** The canonical train ID on the wire is CTC's three-digit dispatch ID, assigned when
the schedule is built. Only the ten fleet trains `001` to `010` can be scheduled and dispatched
(`TrainIdConvention.IsDispatchable`): they are the Train Controller's `TRAIN-001` to `TRAIN-010`
(odd Software, even Hardware). IDs are unique across all three lines. CTC gives a new schedule the
lowest IDs no scheduled, queued or dispatched train uses (a fresh system starts at `001`), and
refuses a schedule (generated, imported from CSV or passed to `QueueSchedule` directly) with an
unsupported ID such as `000` or `011`, an ID another line already uses, or more trains than free
IDs. Module labels are presentation only and are translated at that module's own boundary with
`TrainControl.Common.Identity.TrainIdConvention`. For example, the Train Controller fleet label
`TRAIN-007` is canonical `007` (same number), and CTC's `Train 007` is `007`. Anything else, such
as a two-digit `01`, is not canonical and is rejected rather than guessed.

## Track Controller deployment

`src/TrackController/TrackController.Wpf/Samples/course-territories-all-pc.deployment.json` is the
default: 16 waysides (two Blue, six Red, eight Green), running locally with `wayside.plc`.
Every one of the 241 blocks has exactly one owner. Whole-line single-wayside setups have been removed.
The deployment lists one map and one ownership file per line (`lines: [...]`, see the
[Runtime README](../../src/TrackController/TrackController.Runtime/README.md#several-lines-in-one-deployment)).
The single-line deployments are unchanged.

- The Red and Green maps (`red-line-track-map.json`, `green-line-track-map.json`) are derived from
  the Track Model layout: same IDs, lengths, speed limits, stations, switches and crossings. Each
  switch's protection zone is its stem and both legs; each crossing's is its approaches. The exits
  follow the direction of travel (Green's one-way sections are enforced). The test
  `TrackControllerRedAndGreenMaps_AreWhatTheTrackModelLayoutDerives` keeps them identical to the
  layout; `tests/Integration.Tests/CourseTrackMaps.cs` documents the derivation and can regenerate
  the files.
- CTC sends every request to the one Track Controller pipe with the shared contracts and block IDs.
  The controller's router delivers it to the owning territory. CTC.Core has no knowledge of
  territories.
- On split lines, movement and handoff require current approval from every wayside on that line.
  Equipment commands still come only from the block owner. Cross-line routes are rejected.
- The operator selects **Line** and named **Territory** above the tabs. **Territory map**,
  movement rows and equipment rows show only that territory's blocks.

## Dispatch semantics

- **CTC decides** when a train is released and proposes its route, speed and authority.
- **The Track Controller validates** every request against occupancy, switches, signals,
  crossings, maintenance, direction of travel and territory ownership. It is the final authority.
- **The Track Model places the train**, through the Track Controller, and has the Train Model
  activate it (see [Physical dispatch and train position](#physical-dispatch-and-train-position)).

At its departure time a train whose start block is reported Clear and open is released: it leaves
the queue with *Release = Awaiting placement* and CTC sends its physical dispatch
(`TrainDispatchRequestMessage`) to the Track Controller, which forwards it to the Track Model. The
legacy label-only `MovementRequestMessage` is never sent. A delivered or accepted dispatch is not a
placement: CTC shows each stage (*forwarded*, *activation requested*, *placed*, *refused … retrying*),
but only when the start block reports occupied does it attribute the occupancy to the train
(*Detected on track (inferred)*) and send its first complete movement request: target = current
block, entry = previous block, path = the route blocks the authority covers, and authority measured
from the target block's entry. Speeds come from the line's own civil speed limits.

On each block change the train's request passes to a new request (new ID) that re-expresses the
same fixed authority end from the new block. When the Track Controller grants the current request
into the next block, that successor is prepared in advance and takes over at the boundary without
an interruption (see [Movement Authority Continuity and Emergency
Revocation](#movement-authority-continuity-and-emergency-revocation)); otherwise the old request is
cancelled and a new one issued. The end is set only when a movement starts (release, departure from
a stop, dispatcher re-plan). Renewals every 3 s of wall time keep a still-valid
request alive under the Track Controller's 10 s watchdog, with the same ID and limits. They never
add distance, continue while the simulation is paused, and stop with CTC, so the Track
Controller then withdraws the authority. A block ahead that becomes occupied, Unknown, stale or
closed shortens the request; clearing restores it only up to the original end.

## Physical dispatch and train position

```text
 CTC ── TrainDispatchRequestMessage ──► Track Controller ── (forward) ──► Track Model ── TrainActivationMessage ──► Train Model
  ▲     (train, line, start block,          │  relay, no PLC,                 │  validate route & start block,          │ create/activate at
  │      ordered route; retried with        │  no authority                   │  register route context                 │ position 0, publish
  │      the same request ID)               │                                 │                                         │ initial report at once
  │                                         │                                 ◄──────── TrainStateMessage ─────────────┘
  │◄── TrainDispatchStatusMessage ──────────┤◄── TrainDispatchStatusMessage ──┤  (every 0.1 s tick: cumulative front
  │    (forwarded / activation requested /  │    (relayed unchanged)          │   position, speed, length, sequence)
  │     placed / rejected [retryable])      │                                 │
  │                                         │◄── TrackModelBlockStates ───────┤  footprint → occupancy, one observation
  │◄── BlockStatusBatchMessage (anonymous) ─┤    (each boundary its own       │  per boundary crossed, in order
       → TrainLocationTracker: Detected      │     observation)                │
         → MovementSuggestionMessage ───────►│── TrackModelCommandBatch ──────►│── TrackModelTrainEnvironment (head block) ──► Train Model ⇄ Train Controller
```

CTC never sends anything to the Train Model or the Train Controller, and the Track Model sends CTC
only its layout and ticket sales: every dispatch outcome reaches CTC through the Track Controller.

### Contracts

| Message | Direction | Fields |
| --- | --- | --- |
| `TrainDispatchRequestMessage` (new) | CTC → Track Controller → Track Model | `RequestId` (unique per placement; retries reuse it), `TrainId` (canonical), `LineId`, `StartBlockId`, `RouteBlockIds` (ordered, starting with the start block) |
| `TrainRouteUpdateMessage` (new) | CTC → Track Controller → Track Model | `RequestId`, `TrainId`, `LineId`, `RouteBlockIds` (from the block CTC places the train in) |
| `TrainDispatchStatusMessage` (new) | Track Model → Track Controller → CTC (relayed), Track Controller → CTC | `RequestId`, `RequestType`, `TrainId`, `LineId`, `StartBlockId`, `Stage` (`TrainDispatchStage`), `IsRetryable`, `Detail`, `ReportedBy`, `ReportedAtUtc` |
| `TrainDispatchStage` (new enum) | | `ForwardedToTrackModel`, `ActivationRequested`, `Placed`, `Rejected`, `RouteUpdated`, `TrackingFault`, `Retired` — none of them is a movement authorization |
| `TrainActivationMessage` (new) | Track Model → Train Model | `DispatchRequestId`, `TrainId`, `LineId`, `StartBlockId` (diagnostic only), `Activate` (false retires), `Reason` |
| `TrainStateMessage` (extended) | Train Model → Track Model | `TrainId`, `PositionMeters`, `SpeedMetersPerSecond`, `AccelerationMetersPerSecondSquared` (existing) + `TrainLengthMeters`, `Sequence` (physics tick count), `DispatchRequestId`, `PassengerCount`. No block ID. |
| `MovementRequestMessage` | — | Unchanged; documented as the superseded label-only release intent. Never sent; still rejected by the Track Controller. |

`TrackModelTrainUpdateMessage` keeps its meaning for the development harness and the TestUIs
(placement by block). For a position-tracked train the Track Model refuses block moves; an empty
block retires it.

### Dispatch: CTC → Track Controller → Track Model → Train Model

1. **CTC** (`CTCService.SetSystemTimeAsync` → `ReleaseTrain`, then `TrainDispatchCoordinator`): the
   existing release checks are unchanged (the start block must be Clear, known and open). The train
   becomes `AwaitingPlacement` and its dispatch is sent at once to the Track Controller pipe. It is
   resent with the same request ID every 3 s of wall time (the movement renewal interval) until it
   settles (placed, or refused for good); a send failure is recorded and retried, never thrown, and
   the train never returns to the queue, so repeated clock ticks cannot create a second train. A
   re-plan before placement sends a new dispatch (new ID) for the new route.
2. **Track Controller** (`WaysidePipeHost` → `TrainDispatchRelay`, the integration layer, not the PLC):
   checks the request's shape and that this deployment owns the start block and reports it fresh,
   clear and not closed for maintenance; the dispatcher never needs to know which territory owns it.
   It forwards the request to the Track Model on its own sender (an absent Track Model never stalls
   the input pipe) and reports `ForwardedToTrackModel`, or `Rejected` with `IsRetryable` (the Track
   Model is unreachable, the block is occupied/closed/unowned) to CTC on its ordered CTC publisher. It
   grants nothing: authority still comes only from a movement request it evaluates.
3. **Track Model** (`TrackService.RequestTrainPlacement`): validates the train ID, the line, that the
   route starts at the start block and is a physical path (every block on the line, consecutive blocks
   connected, no reversal, never leg-to-leg through a switch), and that the start block is clear, open,
   unfaulted and not reserved by another pending placement. It registers the train's route context
   (pending; nothing is occupied) and sends `TrainActivationMessage` to the Train Model. Delivered:
   `ActivationRequested`. Undeliverable (Train Model down): the placement is withdrawn and CTC is told
   `Rejected`, retryable. A retry with the same ID is idempotent (`AlreadyPending` re-sends the
   activation; `AlreadyPlaced` reports `Placed`); a different dispatch of a train already on the track
   is refused rather than resetting it.
4. **Train Model** (`MainWindowViewModel.ApplyTrainActivation`): creates the train if needed, sets its
   cumulative position and speed to 0 (`ITrainSimulationService.PlaceAtDispatchOrigin`), keeps its
   `TrainSpecs` (32.2 m), assigns its line for the roster, clears any stale speed/authority/power, marks
   it active and publishes its initial `TrainStateMessage` immediately, so placement works with the
   clock paused. Repeating the same activation only re-publishes the current position; a different
   dispatch is adopted only while the train is still at its origin; `Activate = false` retires it.

### Occupancy feedback: Train Model → Track Model → Track Controller → CTC

1. After every 0.1 s physics step the Train Model publishes each active train's `TrainStateMessage`
   (sequence = physics tick count) to the Track Model pipe through a latest-per-train queue: bounded,
   ordered, never blocking the simulation. When the Track Model is slower, intermediate reports are
   coalesced; the position is cumulative, so nothing is lost.
2. The Track Model applies each report (`TrackService.ApplyTrainPosition`). The first report places
   the train in its start block (`Placed`, relayed to CTC). Every report is validated as a whole before
   anything changes, and each boundary crossed becomes one atomic step that raises an occupancy
   transition.
3. `TrackModelConnection` sends each step to the Track Controller as its own `TrackModelBlockStates`
   observation of that line, through one ordered sender, with strictly increasing observation times;
   full snapshots and heartbeats coalesce among themselves but never replace or merge a step.
4. The Track Controller applies each observation (its handoff sees the front's Clear → Occupied
   transition) and reports anonymous occupancy to CTC in its 500 ms `BlockStatusBatchMessage`.
5. CTC's `TrainLocationTracker` attributes the start block's occupancy to the awaiting train
   (*Detected on track*), then follows it block by block along its route from Clear → Occupied
   transitions. A block that was last definitely Occupied and only went Unknown in between (stale
   reports, a circuit fault) is not a new entry; repeated Occupied snapshots never move a train.

### From cumulative position to blocks

The Track Model keeps, per train: train/line/start block/dispatch ID, the planned route, the traversed
path with each block's cumulative entry position (real layout lengths, in route order, never block
numbers), the latest front position, length, speed and sequence, the head block, the occupied blocks
and its status. With the front at `f` and the rear at `f − length`:

- the **head** is the path block with `start ≤ f < end` (so at `f = 0` it is the start block);
- a **trailing** block stays occupied while the rear is still in it (`rear < end`);
- the rear below 0 is in the yard: no block before the start block is ever occupied.

Example (A 100 m, B 75 m, C 120 m; train 32.2 m, dispatched at A's entry): `f = 115` puts the front 15 m
into B and the rear at 82.8 m in A, so A and B are occupied with the head in B; A clears when the rear
reaches 100 m (`f = 132.2`). A train longer than a block occupies every block it touches.

The path grows only where the track physically leads: from the head block the next block is the one
the connections and the switch's CURRENT position lead to (a train from a switch's stem takes the leg
the points are set to; from a leg it always goes to the stem). That is checked against the route. If
the switch sends the train somewhere its route does not go, or a trailing switch is set against it,
the train is placed where the track took it (so that block is protected) and faulted. A collision
(another train's block), a closed block, a position beyond the route's end (more than 1 m), an
impossible jump (faster than 30 m/s between ticks), a backward jump, a length change, a non-finite or
negative value, or a stale or conflicting sequence is rejected; the discontinuities fault the train.
A faulted train's footprint is held (nothing is cleared) and no further telemetry is applied until it
is retired; the fault is reported to CTC as `TrackingFault`. Modest backward motion (rolling back on a
grade) is followed exactly: the rear re-occupies the block behind.

A dispatcher re-plan of a train already on the track sends a `TrainRouteUpdateMessage`: the Track
Model keeps everything traversed (cumulative boundaries never change) and replaces only the route
ahead of the front, after checking it continues from where the train physically is. Multiple trains
are independent; each block has one owner, and no train ever clears another's block.

### Multi-block occupancy and authority

- Every occupied block (head and trailing) is reported occupied to the Track Controller and CTC, so
  track circuits, switch locking (an occupied switch cannot be thrown) and maintenance closures
  protect the whole train.
- Exactly one block per train is its head (`TrackBlock.IsTrainHead`). Only the head names the train in
  its `TrackModelTrainEnvironmentMessage`, so the Train Model receives one environment (code, signal,
  beacon, grade) per train; `CaptureTrainCodes` has one entry per train from its head (it previously
  threw on a train in two blocks); the Train Controller's light is the head block's
  (`TrainControllerSignalPublisher` no longer treats a multi-block train as conflicting aliases).
- The head entering the next block sets that block's `TrainEntryBlockId` to the block it came from,
  so the prepared continuation code applies immediately. The Track Controller sees that block go
  Clear → Occupied while the previous block is still occupied and promotes the prepared successor (a
  transition "in two observations", which it already supported); the trailing block stays occupied,
  correctly, and clears later with no request on it. Nothing reports a block clear early, and no
  authority comes from the front entering a block: the successor is evaluated by the PLC and every
  existing check as before.
- CTC still receives anonymous occupancy only; no train identity was added to block statuses.

### Failure handling

| Failure | What happens |
| --- | --- |
| Track Controller unreachable from CTC | The dispatch send fails, is recorded (*not delivered; retrying*) and resent every 3 s with the same ID; the train stays awaiting placement. |
| Track Model unreachable from the Track Controller | `Rejected`, retryable ("The Track Model is not reachable"); CTC retries; nothing placed. |
| Train Model unreachable from the Track Model | The activation fails; the placement is withdrawn and reported `Rejected`, retryable; CTC retries until the Train Model runs. |
| Start block occupied, closed or faulted | Refused (Track Controller or Track Model), retryable. Re-checked when the initial report arrives. |
| Invalid route, line or train ID | Refused, not retryable; the dispatcher sees the reason. |
| Lost or duplicated messages | Same request ID everywhere; the Track Model and Train Model are idempotent; CTC ignores stages of requests it no longer holds. |
| Track Model restarted | Its tracked trains are lost (the train's last occupancy is gone). Not recovered automatically; see the outstanding work. |

## Movement Authority Continuity and Emergency Revocation

A train moving from block A into block B under a valid movement keeps its authority across the
boundary. A genuine withdrawal of authority reaches it immediately. The two cases are kept apart
**at the source, not by filtering**: the wayside never produces a zero for a valid handoff, so every
zero a train receives is authoritative and is acted on at once. No module delays, debounces, holds
or ignores a zero.

### Why a moving train used to receive a false zero

Before this change every block transition produced roughly 0.5–1.5 s of zero authority, even for a
train with a valid, protected route:

1. **Track Controller → Track Model.** Speed and authority were coded only onto the request's
   target block (the train's block); every other block, including the next one, was coded 0. When
   the train entered B, the code it picked up was already 0.
2. **Track Controller.** When A cleared, its request was retired at once. B had no request: a
   request is only granted from an occupied target, and two overlapping requests withhold each
   other, so the next request could not exist in advance.
3. **CTC.** CTC learned of the move from the next 500 ms status batch, cancelled the old request,
   sent a new one, and the Track Controller's grant reached the Track Model on its next 500 ms
   publication.
4. **Train Model.** It did not consume `TrackModelTrainEnvironmentMessage` at all, so no live
   authority reached it (only the TestUI's placeholder `AuthorityMessage`).
5. **Train Controller.** A changed authority value recalibrates the remaining authority; a moving
   train recalibrated to 0 is below its emergency stopping distance, so `AuthorityProtection`
   applies the emergency brake (correctly, for the zero it was given).

Re-ordering the cancellation and the new request would not have helped: the new request could not
be granted before the train was in B, and while both existed they would have withheld each other.

### Lifecycle: prepare → transfer → retire

```text
 CTC                      Track Controller                 Track Model          Train Model → Train Controller
  │ P: target A, path B…    │                                 │                       │
  │────────────────────────►│ evaluate P: Approved 300 m      │                       │
  │◄──── status: P granted ─│── A: 300 m (P) ────────────────►│ train in A: 300 m ───►│ remaining 300 m
  │ S: target B, entry A,   │                                 │                       │
  │    predecessor P, 250 m │ validate + STAGE S (no grant,   │                       │
  │────────────────────────►│ no conflict check)              │                       │
  │◄ ack PreparedAsSuccessor│── B: 250 m (P, from A) ────────►│ B: continuation code  │
  │                         │                                 │                       │
  │                         │       train moves A → B ────────│ train in B: 250 m ───►│ recalibrated 250 m
  │                         │◄── B occupied (fresh) ──────────│ (code applies: it     │ (no zero, no brake)
  │                         │ ONE STEP: promote S, retire P,  │  entered from A)      │
  │                         │ evaluate S: Approved 250 m      │                       │
  │                         │── B: 250 m (S) ── at once ─────►│ train in B: 250 m ───►│ same value: counts on
  │◄──── status: S granted, │                                 │                       │
  │      superseded P ──────│                                 │                       │
  │ adopt S; cancel P       │ (P already retired: no-op)      │                       │
  │ (idempotent clean-up)   │                                 │                       │
  │ prepare S2 for C …      │                                 │                       │
```

**Prepare (Phase A).** When CTC sees its active request P granted (Approved or Restricted) with
authority reaching past its own block, it sends a successor S for the next route block:
`MovementSuggestionMessage` with a new `RequestId`, `PredecessorRequestId = P`, target B, entry A,
path = P's path after B, authority at most P's authority less A's length (the same fixed end point),
speed at most P's. The Track Controller validates ownership, topology and all of these limits
(anything that would extend the end point, leave P's route, raise the speed, change the train label
or name an inactive predecessor is rejected) and stages S. A staged successor:

- is never evaluated, takes part in no route-conflict check, and is never reported as a grant;
- is acknowledged `PreparedAsSuccessor`, which CTC shows as "handoff to B prepared", never as an
  authorization;
- is renewed by CTC like any request and expires under the same 10 s request watchdog;
- is replaced (and the old ID retired) if CTC prepares a different successor, e.g. after a re-route,
  and is discarded when P is cancelled, retired or expires.

While S is staged and P's grant actually covers B, the Track Controller codes B with a
**continuation**: P's own granted authority measured from B's entry (P's granted authority less A's
length, never more than S asks for), at P's speed, tagged with P's request ID and with
`AuthorityEntryBlockId = A`. It is P's authority, not a second grant: it exists only while P is
commanded and disappears in the same evaluation that withholds P.

**Transfer (Phase B).** In the same call that applies a fresh Track Model observation, before any
other request is retired or evaluated, the Track Controller promotes S and retires P **in one
locked state transition** when all of the following hold:

- B went from fresh, unfaulted Clear to fresh Occupied in this observation (a stale, Unknown or
  already-occupied block shows nothing about who entered it);
- P is still the live, unexpired request of A, and **P's grant in force just before this observation
  covered B** (so B was clear and protected for this train and no other movement could hold it);
- A's feedback is fresh and definite (Occupied or Clear, not Unknown or faulted); B is not faulted or
  closed;
- no other request holds B or any block of S's route;
- the occupancy cannot be another train: if any other block a train is permitted to enter B from was
  not definitely clear, the transfer also requires that A cleared in the same observation (a train
  cannot vanish). CTC's train labels are never used to decide this;
- exactly one staged successor targets B.

S is then evaluated exactly like any other request (PLC and the independent checks: switches,
signals, crossings, freshness, maintenance, conflicts). Its result is published under S's request ID
and reported to CTC with `SupersededRequestId = P`. If any condition fails, nothing is transferred:
the train in B has no request and receives zero. Transfers never force a grant.

The transition may arrive in one observation (A clear and B occupied together, which is what the
Track Model sends) or in two (B occupied first while the train spans the boundary, then A clear). A
clear of A *before* B is occupied is a loss of detection: P is retired as before and the train
receives zero.

**Retire (Phase C).** P is retired by the transfer itself; no cancellation is needed. A late renewal
of P is rejected ("target block cleared"). A late, repeated or clean-up cancellation of a retired ID
is accepted as a no-op. A replayed staging message for S after promotion is only a renewal of S. A
promoted request cannot be extended: a renewal with more authority or speed than it inherited is
rejected. CTC adopts S as the train's active request when it tracks the train into B and S is
exactly the plan it needs (no new request ID, no cancel-and-reissue), then sends P's cancellation
as idempotent clean-up (retried until delivered). A dispatcher re-plan never adopts a prepared
successor: it cancels it and starts a new movement.

### Exactly when authority changes hands

- **At the train:** the moment the Track Model moves the train from A into B, the train receives B's
  continuation code, which is P's own end point measured from B's entry. Before that it holds P's
  code in A. There is no instant at which it holds neither.
- **At the Track Controller:** inside the feedback batch that first shows B occupied. Before it, P is
  the only request; after it, S is the only request.
- **At CTC:** on its next synchronization after the status batch that moves the train into B.

### Downstream propagation

| Hop | What ensures continuity | What ensures a zero is never hidden or delayed |
| --- | --- | --- |
| Track Controller → Track Model | Continuation code on B for the train entering from A. Each code carries its `MovementRequestId`. | A change of any movement output (grant, decision, request, continuation, pause, maintenance mode) wakes the publisher at once instead of waiting for the 500 ms cycle (measured: 14 ms from a pause to the Train Model). `TrackModelCommandBatchMessage.Sequence` (seeded from the wall clock) lets the Track Model ignore, per block, any older or replayed batch. `AuthorityLeaseSeconds` (the controller's own output validity, ≤ 1.5 s) makes a silent controller's codes lapse to zero. |
| Track Model | A continuation code applies to the occupant only if it entered from the code's entry block (the Track Model knows the physical previous block); any other occupant, or a train placed there, gets zero. | Train moves, wayside batches that change any train's code, lease expiry and failure inputs publish immediately (the 100 ms debounce is skipped). Train environments have their own ordered sender, so a slow CTC/TestUI pipe never delays them; occupied blocks go first. |
| Track Model → Train Model | Each `TrackModelTrainEnvironmentMessage` carries the code for its train and `Sequence`. | `TrackCircuitReceiver` (TrainModel.Core) applies only the newest snapshot for each train; a zero is applied like any value. Occupied blocks are re-sent with the 1 s heartbeat, so a restarted Train Model resynchronizes to the current code (never an older one). |
| Train Model → Train Controller | `CommandedAuthorityMeters` relayed unchanged. A same-value hand-over (continuation → successor) is not a new update, so the estimate keeps counting down (it never jumps back up). | A changed value is an update: the remaining authority is recalibrated on that tick; moving with zero authority engages the emergency brake on that control period. |

### Genuine zero authority

Withdrawal is the wayside's ordinary decision and is never weakened:

- **Unsafe route or equipment** (occupied/Unknown block, fault, closure, stale feedback, switch not
  confirmed, no proceed aspect, crossing not closed, PLC withholding, pause, maintenance mode,
  request expiry): the evaluation withholds P (or restricts it). Its continuation is removed in the
  same evaluation, so a prepared successor can never override it, and the change is published at
  once.
- **During a transfer:** the successor is evaluated after promotion with the same checks; an unsafe
  condition reported in the same observation as the transition withholds it.
- **CTC stops:** renewals stop; the 10 s watchdog withdraws the request and its staged successor.
- **Track Controller stops:** the Track Model's lease lapses (≤ 1.5 s) and every code becomes zero.
- **Train Model unavailable:** nothing upstream waits for it; when it returns it receives the current
  code. A train with no track data has zero authority.
- **Final destination:** CTC terminates the movement when the plan completes (existing policy,
  unchanged): the request and its successor are cancelled and the train receives zero.

**Train Controller review.** `AuthorityTracker` and `AuthorityProtection` are unchanged, and so are
the emergency-brake thresholds. With a valid track signal every new value, zero included, is applied
on the tick it arrives. With an invalid track signal received values are not trusted and the train is
already stopped with the service brake; when the signal returns, the adapter
(`TrainControllerPipeReceiver`) treats the first valid value as an update even if it did not change,
so a zero sent during the loss is applied on the first valid period. The only Train Controller change
is the adapter reading the renamed `TrainModelStatusMessage.CommandedAuthorityMeters` (the build was
broken by that rename).

### Contract changes

| Message | Field | Meaning |
| --- | --- | --- |
| `MovementSuggestionMessage` | `PredecessorRequestId` | Successor to stage behind that active request; null for every existing message. |
| `RequestAcknowledgementOutcome` | `PreparedAsSuccessor` | Staged; not a grant, not accepted for evaluation. |
| `MovementAuthorizationReport` | `SupersededRequestId` | The request this one replaced at a block transition (diagnostic). |
| `TrackModelCommandMessage` | `MovementRequestId`, `AuthorityEntryBlockId` | Which movement the code belongs to; for a continuation, the block the train must enter from. |
| `TrackModelCommandBatchMessage` | `Sequence`, `AuthorityLeaseSeconds` | Ordering (older batches ignored per block) and code validity. 0 = legacy sender, applied as before. |
| `TrackModelTrainEnvironmentMessage` | `MovementRequestId`, `Sequence` | Code ownership and snapshot order. |
| `WaysideBlockStatus` (Track Controller) | `Continuation` | The prepared handoff on the successor's target block. |
| `WaysideAcknowledgementMessage` (Track Controller) | `IsStagedSuccessor` | Carries "staged" through the Pi runtime and router to the CTC acknowledgement. |

### Remaining limitations

- **Live occupancy** now comes from the Train Model's position telemetry (see
  [Physical dispatch and train position](#physical-dispatch-and-train-position)). The authority
  continuity tests below still move their train with the harness message so each boundary is
  exact; `LivePhysicsEndToEndTests` runs the same handoffs on real physics.
- **A train must spend long enough in a block for its successor to be prepared** (one CTC status
  cycle, about 0.5 s, after the grant). A train that reaches the next block first receives zero and
  brakes. Continuations are coded one block ahead only.
- **Obstruction changes still interrupt.** When a block ahead becomes unsafe or clears, CTC still
  cancels and re-issues the request for the train's current block. The wayside restricts the old
  request first, so this is never less safe, but a train may see a brief zero before the shorter
  request is granted.
- **Mid-block restrictions.** Authority is measured from the block entry and the Train Controller
  recalibrates as if the train were at the entry, so a restriction received mid-block is optimistic by
  the distance already travelled in the block (pre-existing contract semantics). A zero is exact.
- **Arrival at the final destination** while moving still ends in an emergency stop (existing CTC
  termination policy).
- **CTC tracking ambiguity.** If CTC cannot attribute the occupancy of B to its train (two trains
  could explain it), it does not adopt the successor even if the Track Controller transferred
  authority; its renewal of the retired predecessor is then rejected and it withdraws the
  successor, so the train brakes. This is the conservative outcome, not a continuity guarantee.
- **Cross-territory handoff** now uses joint wayside approval; the 16-wayside project setup exercises it. Time-based expiries (request watchdog, stale feedback) are still detected on the 500 ms cycle.
- The Hardware Train Controller (Pi) receives the same relayed values but was not exercised.

## Signalled routes: signal clearance before authority

Blue Line blocks 6 (toward Station B) and 11 (toward Station C) carry signals. Red and Green have no
signals in the course data, so nothing in this section changes them.

### Root cause of "Waiting for proceed signal feedback at block 6"

A request from block 1 to block 10 was withheld forever, although the route was clear. Three pieces
of the Track Controller depended on each other in a circle:

1. **The PLC needed a reported proceed aspect.** In `wayside.plc`,
   `signal_ok = not has_signal or (signal_proceed and not manual_red)`, where `signal_proceed` is the
   Track Model's *reported* aspect. With block 6 reporting Red, the scan stopped at block 6 with
   `route_fault`, so `movement_enabled` was false and the whole authority was zero, not only the part
   beyond the signal.
2. **Signals were commanded only for granted authority.** `EvaluateVital` set an aspect only for
   the blocks covered by its own *granted* output. With zero authority it covered nothing.
3. **Everything else was forced Red.** At the end of `Reevaluate`, every signal outside a granted
   authority was set back to Red. The software comparison mode had the same circle: it commanded
   Green first, then withheld the grant for lack of feedback, and then this step reset the signal to
   Red.

So the signal stayed Red because there was no authority, and there was no authority because the
signal was Red. Block 11 had the same circle (and, on top of that, needs switch 5 Reverse).

### The fix: a route-preparation step

`TrackControllerService.Reevaluate` now runs in three steps on every evaluation:

1. **Prepare (`PrepareSignals`).** For each active request, the PLC runs a separate
   **clearance scan**. It is the normal route scan with the new PLC input `signal_clearance` set,
   and `wayside.plc` now reads
   `signal_ok = not has_signal or ((signal_proceed or signal_clearance) and not manual_red)`.
   Only a signal's *own reported aspect* stops counting. Every other input still applies: session
   (running, maintenance, request lease and retirement, origin occupied, route conflicts), freshness,
   Unknown occupancy, faults, closures, occupancy ahead, switch position and direction, crossing
   closed, and manual red. If that scan protects a route through the signal, and the independent C#
   check (`IsAuthorizationSafe(..., requireSignalProof: false)`) passes as well, the signals the route
   covers are commanded to the PLC's aspect (`signal_clear` and `signal_yellow`). The independent
   check also refuses to clear a signal the Track Model reports as Unknown (for example after a power
   failure). **Nothing is granted in this step.**
2. **Authorize (unchanged).** The normal PLC scan, with `signal_clearance` false, needs the Track
   Model's *reported* proceed aspect. The independent check then needs that aspect both commanded
   **and** reported, and caps Yellow at 5 m/s. Authority is granted only here.
3. **Restrict.** Any signal that is neither covered by a granted authority nor prepared in this
   evaluation is commanded Red.

Prepared aspects are not stored. They are rebuilt from Red on every evaluation (every feedback batch,
request and command, and at least every 500 ms). So a request that is cancelled, expires, is
superseded or retired, or becomes unsafe leaves its signals Red on the next evaluation. Pausing or
maintenance mode makes `session_ok` false, so both scans fail and every signal goes Red.

`WaysidePipeHost` now also includes commanded signal aspects in its movement-output signature. A
newly prepared aspect, or a signal dropping to Red, therefore reaches the Track Model at once instead
of on the next 500 ms publication.

### The sequence for Train 001, block 1 → 10

| Cycle | Track Controller | Track Model reports | Authority |
| --- | --- | --- | --- |
| Request arrives | Crossing 3 commanded Closed; signal 6 Red (the crossing is not yet proven) | crossing Open, signal 6 Red | 0, *Waiting for crossing 3 to report closed.* |
| Next feedback | Clearance scan passes: **signal 6 commanded Green** | crossing Closed, signal 6 Red | 0, *Waiting for proceed signal feedback at block 6 (commanded Green; Track Model reports Red).* |
| Next feedback | Authorization scan passes on the reported aspect | signal 6 **Green** | 450 m, granted |

The live test logs this exact sequence. Each wait lasts one feedback cycle, typically well under a
second. The grant then reaches the Train Model and Train Controller as usual, and the train runs
through switch 5 and block 6 with normal block-to-block handoffs.

### Requested versus reported aspects

The Track Controller keeps the two apart, and the operator window shows both.

- **Commanded:** `WaysideBlockStatus.CommandedSignal`, which the controller sends in
  `TrackModelCommandMessage.Signal`.
- **Reported:** `TrackModelBlockStateMessage.Signal`, the Track Model's `EffectiveSignal` (Unknown on
  power failure). It appears as `WaysideBlockStatus.ReportedSignal`, and it is what CTC displays.

Authority uses only the reported aspect (PLC input `signal_proceed`), plus the requirement that the
aspect is also commanded. The withheld reason says which side is missing: *(commanded Green; Track
Model reports Red)* means the report is pending; *(held Red: the route is not safe to clear)* means
the route itself is not safe; *(held Red manually)* means a maintainer set it Red.

### Unsafe conditions still prevent authorization

Only the signal's own report is relaxed in the clearance scan, and only for commanding the signal.
These conditions still apply:

- **Occupied, Unknown or stale blocks, broken rail, track-circuit or power failures, maintenance
  closures, or a switch not reported in the needed position:** the clearance scan fails, so the
  signal stays Red and there is no authority. An occupied or closed block ahead ends the authority
  before that block, exactly as on unsignalled lines (an occupied block 6 gives blocks 1–5 only, and
  signal 6 stays Red). An occupied block beyond the signal clears it to Caution (Yellow, 5 m/s) for
  the shortened route.
- **Conflicting requests over the same blocks:** neither request is prepared or granted.
- **Pause, maintenance mode, an expired or retired request:** the session vetoes apply to both
  scans.
- **After a grant:** any of the above, or signal 6's reported aspect turning non-proceed, withdraws
  the authority on the next evaluation. A shortened route is zero, never a shortened nonzero value
  inside the block (`HoldBlockAuthority`). The signal is re-evaluated (Red, or Caution if a shorter
  route through it is still safe), the zero is published at once, and the Train Controller brakes on
  its own protections. Genuine zeros are never delayed.
- **A PLC that does not use `signal_clearance`** (a custom or older program) behaves as before:
  it withholds and never forces a grant. A Raspberry Pi territory needs the rebuilt
  `TrackController.Core`, because an older Pi build rejects the new `wayside.plc` and therefore
  withholds everything (also fail-safe).

### Verify it in the production applications

1. Launch **Track Controller**, **CTC Office**, **Track Model**, **Train Model** and
   **Train Controller** from the Launcher.
2. In the Track Controller, load **Blue Line / Red Line / Green Line** and resume it.
3. In the Train Controller, set Train 001's Kp/Ki (for example the test gains Kp = 20000, Ki = 0).
4. In CTC, queue Train 001 on Blue from `A1` (block 1) to `B10` (block 10) and run the clock.
5. Train 001 appears in block 1. Under **Dispatched Trains**, the Track Controller column may show
   *Waiting for crossing 3…* and then *Waiting for proceed signal feedback at block 6 (commanded
   Green; Track Model reports Red)* for about a second. It then shows the grant.
6. On the Track Controller's equipment list, signal 6 shows commanded Green and reported Green, and
   signal 11 stays Red. The Track Model's block 6 light is Green.
7. The Train Controller shows positive commanded speed and authority, releases the service brake and
   accelerates. Occupancy moves 1 → 2 → … → 7 in the Track Model, Track Controller and CTC.

For Station C (block 15), request switch 5 Reverse from CTC while its protection zone is clear and no active movement locks it. Wait for the reported Reverse position; signal 11 then clears the same way.

## Demonstration: Blue, Red and Green together

Close any TestUIs first: the CTC TestUI hosts the Track Controller pipe and the Track Model TestUI
hosts the Track Controller, Train Model and CTC pipes.

1. **Build.** In Visual Studio, open `TrainControlSystem.sln` and build the solution (or
   `dotnet build TrainControlSystem.sln`).
2. **Launcher.** Start `TrainControlSystem.Launcher.Wpf`. From it, launch **Track Controller**,
   **CTC Office** and **Track Model**, in any order.
3. **Track Controller.** It opens the 16-wayside course network automatically, with 241 blocks
   and the bundled PLC. Leave movement held for the status and maintenance steps. To reload,
   choose **Course network · 16 waysides** in **Controller setup** and select **Load setup**.
4. **CTC.** Within a few seconds the line selector lists **Blue Line**, **Red Line** and
   **Green Line**, with no *Send Sample Layout*. *Inbound status* reads
   `Received Track layout. Track layout: added Blue, Red, Green.` Select each line: its territory
   overview draws the course schematic, and every block shows a live status (every block is
   clear: the production Track Model starts with no trains). Use **Fit**, **+** and **-** or
   Ctrl+wheel to zoom the larger lines.
5. **Track Model.** Its line selector shows the three lines. The delivery line reads
   `Track Controller: delivered … CTC: delivered`.
6. **Change occupancy on Red or Green** with the development harness. It is send-only and hosts no pipe:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\Send-TrackModelTrainUpdate.ps1 -TrainId 007 -Block RED-30
   ```

   - **Track Model:** select Red Line; on **All blocks**, `RED-30` is occupied. The *Schematic*
     tab scales the whole line to fit, so it is small for Red and Green.
   - **Track Controller:** select **Red Line → Central & south loop**, then **Territory map**. `RED-30` turns
     occupied (red) within about a second.
   - **CTC:** select Red Line. `H30` is occupied; nothing changed on Blue or Green.

   Move the train (`-Block RED-31`) or remove it (`-TrainId 007 -Remove`) and watch it clear. The
   same works on Green, e.g. `-TrainId 008 -Block GREEN-65` (CTC label `K65`, Glenbury). The Track
   Model refuses a move into an occupied, closed or unknown block.
7. **Close and reopen a block on each line through CTC.** CTC → **Maintenance Mode: ON**. Select
   the line and a clear block, then **Close Block**: for example Blue `C12` (block `12`), Red `H40`
   (`RED-40`), Green `Q100` (`GREEN-100`).
   - The communication line shows the Track Controller's acknowledgement, e.g.
     `Track Controller accepted the request to close Block RED-40 (Block RED-40: Closed.)`.
   - The block turns Closed only when the controller's status confirms it. The Track Controller's
     equipment and map show the same closure.
   - Closing an occupied block (e.g. `RED-30` while train 007 is there) is accepted but stays
     *Pending closure* until the block clears.
   - **Reopen Block** works the same way. Closures on one line never change another line.
8. **Freshness.** Close the Track Model: within about 5 s the Track Controller shows
   *Feedback unavailable* and CTC shows every block of every line Unknown. Reopen the Track Model
   (from the Launcher) and all three lines return without restarting anything else.
9. *(Optional) movement on Red or Green with the harness, without the Train Model.*
   1. Resume the Launcher clock from CTC and queue a schedule on Red (e.g. a route through
      `RED-20`…`RED-25`) or on Green in its direction of travel (e.g. `GREEN-63`…`GREEN-68`).
   2. Remove any train left on the schedule's start block by step 6.
   3. At the departure time CTC shows *Awaiting placement* and sends the dispatch; with no Train
      Model running it shows the retryable refusal. Place the train with the harness instead,
      using its dispatch ID: on a fresh system the first scheduled train is `001`, so
      `-TrainId 001 -Block RED-20` (a manual placement takes over the pending dispatch).
   4. CTC shows *Detected on track (inferred)*. The *Track Controller* column shows the
      acknowledgement and the current vital movement decision; maintenance mode withholds movement.
   5. Leave Track Controller maintenance mode to see its automatic evaluation (Approved/Restricted with the granted
      authority). Move the train with the harness to see the request re-targeted.
   6. A Green route against its arrows is rejected (`No permitted connection from GREEN-68 to GREEN-67`).

10. *(Live dispatch and movement with the real Train Model and Train Controller.)* Also launch
    **Train Model** and **Train Controller** from the Launcher. In the Train Controller, set the
    train's Kp/Ki first (the startup Kp = 1 W per m/s is an untuned placeholder that barely moves the
    train). Queue a Green schedule in its direction of travel (e.g. `GREEN-63` … `GREEN-68`), resume
    the Track Controller, and run the clock from CTC. At the departure time the train appears in the
    Train Model's roster on the Green Line at position 0, `GREEN-63` turns occupied in the Track Model,
    Track Controller and CTC without any harness message, and CTC shows the dispatch stages, then
    *Detected on track (inferred)* and the Track Controller's grant. The train then runs on its own
    physics; occupancy moves along the route and CTC follows it. Do not use the harness for a
    dispatched train (the Track Model refuses block moves for it).

Other Track Model inputs can be sent the same way as single-line envelopes, for example a track
circuit failure on Green:
`{"messageType":"TrackModelFailureCommandMessage","payload":{"blockId":"GREEN-50","brokenRail":false,"trackCircuitFailure":true,"powerFailure":false}}`
(CTC then shows `GREEN-50` (`I50`) Unknown, and only that block).

**Blue Line alone.** The Blue-only Track Controller setup (**Blue Line**) still works with the
three-line Track Model. Blue behaves exactly as before. The controller refuses the Red and Green
feedback it does not own, so CTC shows those lines Unknown, never clear.

## Performance with 241 blocks

| Path | Before | Now |
| --- | --- | --- |
| Track Model → Track Controller | one pipe connection per block per snapshot and per 1 s heartbeat (241/s at idle) | one batch per line: 3 connections per heartbeat |
| Track Controller → CTC | one connection per block every 500 ms (482/s), each followed by a movement-request sync | one `BlockStatusBatchMessage` every 500 ms (46 KB for 241 blocks); one sync per batch; about 6 ms of CTC UI-thread work per batch, measured |
| Track Controller → Track Model | one connection per block every 500 ms | one `TrackModelCommandBatchMessage`; the Track Model notifies its UI once, and only when something changed |
| Track Model input loop | each input waited about 110 ms (measured) trying to answer the absent TestUI, so at most about 9 inputs/s | the TestUI answer is sent separately; inputs are applied back to back |

Acknowledgements are still sent ahead of the status batch and wake the CTC publisher immediately.
Every status keeps its own evaluation or observation time, and receivers still drop older reports.
Watchdogs, freshness limits and conservative fallbacks are unchanged. Measured in the automated
test with all three lines live:

- every block's status at CTC is under 1.5 s old;
- an occupancy change reaches CTC within 2 s on each line;
- three simultaneous maintenance requests are acknowledged within 1.5 s;
- with CTC offline, the Track Model → Track Controller feedback stays fresh and commands still reach the Track Model.

**Occupancy steps** of a moving train are sent to the Track Controller as one batch of that line per
boundary crossed (about two per block per train), on the same ordered sender as snapshots and
heartbeats. Train Model position reports (10 per simulated second per train, latest-per-train when the
Track Model is slower) cost the Track Model one UI-thread call each and republish the network only
when a footprint changes.

**Not batched:** the Track Model still sends the Train Model one `TrackModelTrainEnvironmentMessage`
per block per snapshot, occupied blocks first, on its own sender: a Train Model that is not running
costs that sender one 250 ms timeout per snapshot and delays nothing else.

## Automated coverage

`tests/Integration.Tests` (Windows; about 2 minutes) runs the production `TrackModelConnection`,
`WaysidePipeHost` with the deployment runtimes, and the CTC receiver, service and link supervisor
against each other over isolated pipe names.

- **Three lines** (`ThreeLineIntegrationTests`, `BatchedPublicationTests`, `CourseNetworkDataTests`):
  - Layout: all three lines (15/76/150, 241 unique IDs) reach CTC automatically. A repeated layout
    preserves live state, and a restarted CTC resynchronizes.
  - Occupancy: occupancy and clearing on each line, isolated to that line (including same-number
    blocks on different lines).
  - Equipment: measured crossings and switches; faults Unknown only where they occur; unknown
    block IDs rejected by CTC, the Track Controller and the Track Model.
  - Failures: Track Model and Track Controller loss handled conservatively, with recovery without
    restarts.
  - Maintenance: close/reopen on each line with acknowledgement and confirmation, no cross-talk,
    pending closure on occupied Red and Green blocks.
  - Movement: on Red and Green, evaluated by that line's territory with the commanded authority
    reported. Wrong-direction Green routes, unknown blocks, gaps, paths onto another line, stale
    feedback are rejected or withheld. A split Green accepts cross-territory routes with joint approval.
  - Performance and compatibility: status age, end-to-end latency and acknowledgement time;
    one batch per line on the wire; inputs not delayed by the absent TestUI; an offline CTC not
    affecting the other two; a Blue-only controller with the three-line Track Model.
  - Data: Track Model data equals the independent CTC fixture; the Track Controller maps equal what
    the layout derives; the combined deployment owns every block once.
- **Authority continuity** (`AuthorityContinuityIntegrationTests`, with the production Train Model
  track-circuit pickup and Train Controller): a moving train crosses a boundary with no zero and no
  emergency brake and keeps its end point; a Track Controller pause reaches the Train Model in
  milliseconds and the Train Controller brakes on its next period; a fault ahead during preparation
  withdraws the grant and the successor never takes effect; Blue, Red and Green hand over
  independently; CTC stopping, the Track Controller stopping and an absent Train Model all fail safe.
- **Physical dispatch and live physics** (`PhysicalDispatchIntegrationTests`, `LivePhysicsEndToEndTests`,
  `TrainModelActivationTests`, `OccupancyTransitionPublicationTests`, with the production TrainModel.Wpf
  view model and, for physics, the production Train Controller): CTC's dispatch places Train 001 on
  Green through the Track Controller and Track Model with the clock paused, the Train Model activates it
  at 0 on the Green Line, the start block becomes occupied without any harness message and CTC starts
  movement authorization; retries never duplicate or reset a train; Train Model and Track Model outages
  are refused as retryable and complete when they return; three trains on three lines dispatch
  independently; Train 001 then runs on real physics under the real Train Controller while CTC tracks it
  `GREEN-63 → 64 → 65 → 66` from the occupancy its position produces, with two-block footprints at each
  boundary, the successor handoff while the trailing block is still occupied, and no emergency brake; a
  coalesced report reaches the Track Controller as every intermediate step in order.
- **Signalled routes** (`SignalledRouteIntegrationTests`, course deployment and bundled `wayside.plc`, with the
  production Train Model and Train Controller): Train 001 dispatched to block 10 has signal 6 commanded,
  applied by the Track Model and reported Green before the grant; positive speed and authority reach the
  Train Model and Train Controller; and the train runs 1 → … → 7 past the signal with no emergency brake.
  The same is checked to block 15 through signal 11 (switch 5 Reverse). A broken rail beyond signal 6
  after the grant withdraws authority, signal 6 goes Red at the Track Model, the Train Model receives
  zero and the Train Controller brakes. Unit tests (`SignalClearanceTests`, real PLC and software mode,
  with a Track Model stand-in that reports only commanded equipment) cover the preparation order and
  the unsafe and revocation cases: Unknown, stale, occupied and faulted blocks, closures, the wrong
  switch position, pause, conflicts, manual red, signal power failure, cancelled and expired requests,
  and a PLC without the new input.
- **Blue Line** (unchanged tests, Blue-only Track Model and controller): layout load and late-CTC
  synchronization, occupancy, equipment and faults, heartbeat freshness, disconnects, maintenance,
  the CTC TestUI workflow, complete movement requests, end-to-end authorization, cross-territory
  rejection, re-targeted and re-routed requests, renewal and expiry, no duplicate requests.

Unit tests cover the position-to-block mapping (`TrainPositionTrackingTests`: start block at 0, the
worked example, trailing blocks, three short blocks, coalesced reports, one environment and code per
train, two trains, invalid/stale/impossible telemetry, route end, switches facing/trailing/locked,
roll-back, route updates, retirement, harness interplay), CTC's dispatch (`CtcPhysicalDispatchTests`),
the Track Controller relay (`TrainDispatchRelayTests`) and the head-block light (`TrainControllerSignalTests`).
Unit tests cover the handoff rules in the Track Controller (`AuthorityHandoffTests`, software and
PLC: staging, atomic transfer in one or two observations, fixed end point, restricted grants, missing,
rejected, stale, ambiguous and expired successors, revocation before, during and after the transfer,
switch/signal/crossing protection, replayed and out-of-order messages, two trains), CTC
(`CtcAuthorityHandoffTests`), the Track Model's codes (`TrackAuthorityCodeTests`), the Train Model
pickup (`TrackCircuitReceiverTests`) and the Train Controller adapter
(`AuthorityContinuityAdapterTests`), as well as the XLSX importer reproducing the bundled layout from vF5-format worksheets,
batch command application, and multi-line deployment validation and routing.

## Outstanding work and limitations

| Owner | Item |
| --- | --- |
| Track Model | **Restart recovery.** A restarted Track Model forgets tracked trains while the Train Model keeps simulating them (their telemetry is then rejected as unplaced). Recovery is manual: retire the train and dispatch it again. |
| Track Model / CTC | **End of service.** Nothing retires a train automatically when its plan completes; it stays occupying its last block (the harness `-Remove` or a layout reload retires it). |
| Track Model | **Yard.** The rear of a just-dispatched train is "in the yard" (outside the modelled route), so for its first 32.2 m only the start block is occupied. Yard blocks are not modelled. |
| Track Model | **Direction of travel** is not checked at dispatch (the Track Controller enforces it for authority): a route against the arrows is placed, then gets no authority. |
| Train Model | `PassengerExchangeMessage` now goes to the Track Model once per exchange, which logs it; station counts still change only through `TrackModelTrainUpdateMessage` exchanges. |
| Train Model / Track Model | **Environment volume.** Track Model → Train Model is still one message per block per snapshot (241, occupied blocks first, on their own sender). The Train Model applies only its own trains' blocks; batching would reduce the connection count. |
| Track Controller | **Cross-territory handoff implemented.** The default `course-territories-all-pc.deployment.json` uses 16 waysides (Blue 2, Red 6, Green 8). Current joint approvals preserve the endpoint at owner boundaries; lost peers withdraw authority. See the [runtime guide](../../src/TrackController/TrackController.Runtime/README.md#project-territory-split-and-cross-wayside-movement) and `ProjectSplit_CrossesWaysidesWithoutFalseZero_ThroughAllProductionModules`. Physical Pi testing is still required. |
| Track Controller | **CTC switch commands: resolved for course deployments.** The course maps use wayside-owned movement locks (`requireCtcReservationFeedback: false`) because CTC has no separate reservation service. PLC and independent checks still require fresh clear protection-zone occupancy, no faults or closures, and no active movement through the zone. Explicit CTC reservations still veto movement; legacy configurations continue requiring reservation feedback. |
| Track Controller | **Signalled routes (Blue): resolved.** Routes through signals 6 and 11 are now prepared, cleared on a safe route and granted on the Track Model's reported aspect (see [Signalled routes](#signalled-routes-signal-clearance-before-authority)). A custom PLC must use the `signal_clearance` input to benefit; one that does not keeps withholding (fail-safe). Station C additionally needs switch 5 reported Reverse, subject to the switch protection checks above. Red and Green have no signals in the course data. |
| Track Controller | Starts in the 16-wayside course setup; the 12-block demonstration is available only as a saved regression fixture. The Blue map is directional (1→15), so reverse-direction Blue routes are rejected. A switch block lists both legs as exits (the controller's switch model requires it), so on Green a route can end on a one-way leg entered from the wrong side, but can never continue. |
| Course data | Yard connections, Red/Green signals and transponders, station sides and the Green 16 station name are not in the workbook (see *Data the workbook does not contain*). |
| CTC | Route planning (schedule builder, re-plan) follows connections without direction of travel. A Green schedule against the arrows is planned, then refused by the Track Controller, and CTC shows the refusal. Authority is not extended automatically when an obstruction ahead clears beyond a movement's original end; the next departure or a dispatcher re-plan recalculates it (pre-existing design). A request the Track Controller rejected is not renewed; it is replaced when the train moves or the dispatcher re-plans. |
| Train Controller | Translate `TRAIN-nnn` labels with `TrainIdConvention` when consuming system messages (the Train Model now does so for track environments). |
| CTC / Track Controller | Remaining authority-continuity limitations: see [Remaining limitations](#remaining-limitations). |

Physical dispatch and position-driven occupancy are implemented and exercised end to end by the
automated tests with every production communication component and the real Train Model physics and
Train Controller; the WPF windows themselves are not part of those tests (see the demonstration).
