# Live integration: Track Model, Track Controller and CTC

How the real modules exchange track information for the whole course network (the Blue, Red and
Green Lines, 241 blocks) without their TestUIs, how to demonstrate it, and what is still
outstanding. Train Model physics and Train Controller normal mode are not involved.

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
| The yard is not a block. Yard switches: Red 9 ("SWITCH TO/FROM YARD (75-yard)"; the "75" contradicts its row), Green 58 (57-yard) and 62 (yard-63) | Not modelled; those blocks are plain main-line blocks. Trains are placed in their start block, as on Blue. |
| Signals ("Light") and transponders on Red and Green | None modelled. The Track Controller therefore has no signal to clear on these lines. |
| Green block 16 is a station with no name | Shown as "Green 16 (unnamed station)". |
| Station side (Left/Right), "UNDERGROUND" | Not part of the Track Model's block model or of the shared contracts. |
| Heaters, ambient temperature, waiting passengers | The same documented simulation defaults as the Blue Line. |

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

**Train IDs.** The canonical train ID on the wire is CTC's three-digit dispatch ID (`000` to `999`),
assigned when the schedule is built. Module labels are presentation only and are translated at
that module's own boundary with `TrainControl.Common.Identity.TrainIdConvention`. For example, the
Train Controller fleet label `TRAIN-007` is canonical `007` (same number), and CTC's
`Train 007` is `007`. Anything else, such as the Track Model demonstration's `01`, is not
canonical and is rejected rather than guessed.

## Track Controller deployment

`src/TrackController/TrackController.Wpf/Samples/course-all-pc.deployment.json` runs all three lines
in one Track Controller process on this PC, one territory per line (`wayside-blue`, `wayside-red`,
`wayside-green`), with the bundled `wayside.plc`. Every one of the 241 blocks has exactly one owner.
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
- A movement request is evaluated by its target block's territory. One territory per line means no
  movement on a line ever crosses a territory boundary. A path onto another line is a different
  territory and is rejected.
- In the operator window, the territory overview's **Map** selector draws one line at a time
  (display only).

## Dispatch semantics (current)

- **CTC decides** when a train is released and proposes its route, speed and authority.
- **The Track Controller validates** every request against occupancy, switches, signals,
  crossings, maintenance, direction of travel and territory ownership. It is the final authority.
- **Placing a train in service** is the Train Model / Track Model integration's job.

At its departure time a train whose start block is reported Clear and open is released:
it leaves the queue with *Release = Awaiting placement*. Nothing is sent. The release intent
(`MovementRequestMessage`) has no receiver, and the Track Controller rejects it, so CTC does
not send it and never claims a physical release. When the start block reports occupied, CTC
attributes it to the train (*Detected on track (inferred)*) and sends its first complete
movement request: target = current block, entry = previous block, path = the route blocks the
authority covers, and authority measured from the target block's entry. Speeds come from the
line's own civil speed limits.

On each block change the old request is cancelled and a new one (new ID) re-expresses the same
fixed authority end from the new block. The end is set only when a movement starts (release,
departure from a stop, dispatcher re-plan). Renewals every 3 s of wall time keep a still-valid
request alive under the Track Controller's 10 s watchdog, with the same ID and limits. They never
add distance, continue while the simulation is paused, and stop with CTC, so the Track
Controller then withdraws the authority. A block ahead that becomes occupied, Unknown, stale or
closed shortens the request; clearing restores it only up to the original end.

## Demonstration: Blue, Red and Green together

Close any TestUIs first: the CTC TestUI hosts the Track Controller pipe and the Track Model TestUI
hosts the Track Controller, Train Model and CTC pipes.

1. **Build.** In Visual Studio, open `TrainControlSystem.sln` and build the solution (or
   `dotnet build TrainControlSystem.sln`).
2. **Launcher.** Start `TrainControlSystem.Launcher.Wpf`. From it, launch **Track Controller**,
   **CTC Office** and **Track Model**, in any order.
3. **Track Controller (the one manual setup step).** On **Controller setup**, choose
   **Blue Line / Red Line / Green Line** in *Setup* and click **Load setup**. This loads
   `course-all-pc.deployment.json`: three territories, 241 blocks, PLC `wayside.plc`. It starts
   paused; leave it paused for the status and maintenance steps. (Until a setup is loaded the
   controller runs its 12-block demonstration territory, which owns none of the course blocks.)
4. **CTC.** Within a few seconds the line selector lists **Blue Line**, **Red Line** and
   **Green Line**, with no *Send Sample Layout*. *Inbound status* reads
   `Received Track layout. Track layout: added Blue, Red, Green.` Select each line: its territory
   overview draws the course schematic, and every block shows a live status (only Blue block 1 is
   occupied, by the Track Model's demonstration train 01). Use **Fit**, **+** and **-** or
   Ctrl+wheel to zoom the larger lines.
5. **Track Model.** Its line selector shows the three lines. The delivery line reads
   `Track Controller: delivered … CTC: delivered`.
6. **Change occupancy on Red or Green** with the development harness. It is send-only and hosts no pipe:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\Send-TrackModelTrainUpdate.ps1 -TrainId 07 -Block RED-30
   ```

   - **Track Model:** select Red Line; on **All blocks**, `RED-30` is occupied. The *Schematic*
     tab scales the whole line to fit, so it is small for Red and Green.
   - **Track Controller:** on the territory overview, set *Map* to **Red Line**. `RED-30` turns
     occupied (red) within about a second.
   - **CTC:** select Red Line. `H30` is occupied; nothing changed on Blue or Green.

   Move the train (`-Block RED-31`) or remove it (`-TrainId 07 -Remove`) and watch it clear. The
   same works on Green, e.g. `-TrainId 08 -Block GREEN-65` (CTC label `K65`, Glenbury). The Track
   Model refuses a move into an occupied, closed or unknown block.
7. **Close and reopen a block on each line through CTC.** CTC → **Maintenance Mode: ON**. Select
   the line and a clear block, then **Close Block**: for example Blue `C12` (block `12`), Red `H40`
   (`RED-40`), Green `Q100` (`GREEN-100`).
   - The communication line shows the Track Controller's acknowledgement, e.g.
     `Track Controller accepted the request to close Block RED-40 (Block RED-40: Closed.)`.
   - The block turns Closed only when the controller's status confirms it. The Track Controller's
     equipment and map show the same closure.
   - Closing an occupied block (e.g. `RED-30` while train 07 is there) is accepted but stays
     *Pending closure* until the block clears.
   - **Reopen Block** works the same way. Closures on one line never change another line.
8. **Freshness.** Close the Track Model: within about 5 s the Track Controller shows
   *Feedback unavailable* and CTC shows every block of every line Unknown. Reopen the Track Model
   (from the Launcher) and all three lines return without restarting anything else.
9. *(Optional) movement on Red or Green.*
   1. Resume the Launcher clock from CTC and queue a schedule on Red (e.g. a route through
      `RED-20`…`RED-25`) or on Green in its direction of travel (e.g. `GREEN-63`…`GREEN-68`).
   2. Remove train 01 if the schedule is on Blue.
   3. At the departure time CTC shows *Awaiting placement*. Place the train with the harness,
      using its dispatch ID: `-TrainId 000 -Block RED-20`.
   4. CTC shows *Detected on track (inferred)*. The *Track Controller* column shows the
      acknowledgement and, while the controller is paused, *Withheld: Track Controller is paused.*
   5. Resume the Track Controller to see its real evaluation (Approved/Restricted with the granted
      authority). Move the train with the harness to see the request re-targeted.
   6. A Green route against its arrows is rejected (`No permitted connection from GREEN-68 to GREEN-67`).

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

**Not batched:** the Track Model still sends the Train Model one `TrackModelTrainEnvironmentMessage`
per block per snapshot (the Train Model receiver is outside this milestone). It costs one 250 ms
timeout per snapshot while the Train Model is not running, and does not delay the Track Controller
path.

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
    feedback, and a territory boundary inside a split Green are all rejected.
  - Performance and compatibility: status age, end-to-end latency and acknowledgement time;
    one batch per line on the wire; inputs not delayed by the absent TestUI; an offline CTC not
    affecting the other two; a Blue-only controller with the three-line Track Model.
  - Data: Track Model data equals the independent CTC fixture; the Track Controller maps equal what
    the layout derives; the combined deployment owns every block once.
- **Blue Line** (unchanged tests, Blue-only Track Model and controller): layout load and late-CTC
  synchronization, occupancy, equipment and faults, heartbeat freshness, disconnects, maintenance,
  the CTC TestUI workflow, complete movement requests, end-to-end authorization, cross-territory
  rejection, re-targeted and re-routed requests, renewal and expiry, no duplicate requests.

Unit tests cover the XLSX importer reproducing the bundled layout from vF5-format worksheets,
batch command application, and multi-line deployment validation and routing.

## Outstanding work and limitations

| Owner | Item |
| --- | --- |
| Train Model | **Physical release.** Receive CTC's release intent (`MovementRequestMessage`), acknowledge it with a correlated response, then place the train. Until then CTC infers the train from its start block becoming occupied and never reports a confirmed release. |
| Train Model | **Real occupancy.** Send `TrackModelTrainUpdateMessage` to the Track Model as trains move (today only the harness does). Use canonical train IDs and the Track Model's block IDs (`RED-n`, `GREEN-n`). |
| Train Model / Track Model | **Environment volume.** Track Model → Train Model is still one message per block per snapshot (241); batch it, or send only occupied blocks, when the Train Model receiver is integrated. |
| Track Controller | **Cross-territory handoff.** A request whose route leaves its owner's territory is rejected ("Movement cannot cross a territory boundary…"). The production deployment uses one territory per line, so no movement on the course lines crosses a boundary today. Splitting a line (for example to run part of it on a Raspberry Pi) needs a handoff protocol first: the owner grants authority up to its boundary; the next owner, observing the shared boundary block, is asked for the continuation; both sides' grants are combined and reported against one request ID; and a lost neighbour withdraws the combined authority. The existing neighbour observations provide the shared-block feedback this needs. |
| Track Controller / Track Model | **CTC reservation feedback.** Switch protection requires `TrackModelBlockStateMessage.IsReservedByCtc`, which the Track Model does not report. With the real Track Model, switch requests are refused ("missing CTC reservation feedback") on every line, and CTC shows that refusal. Trains can still move through a switch in its current position. Decide which module owns reservations. |
| Track Controller | **Signalled routes (Blue).** With the bundled `wayside.plc`, a request through block 6 or 11 is withheld ("Waiting for proceed signal feedback at block 6"). The PLC needs a proceed aspect before granting, but signals are commanded only for blocks a granted authority covers. Red and Green have no signals in the course data. |
| Track Controller | Starts in its 12-block demonstration territory; the three-line setup is one selection plus **Load setup**. The Blue map is directional (1→15), so reverse-direction Blue routes are rejected. A switch block lists both legs as exits (the controller's switch model requires it), so on Green a route can end on a one-way leg entered from the wrong side, but can never continue. |
| Course data | Yard connections, Red/Green signals and transponders, station sides and the Green 16 station name are not in the workbook (see *Data the workbook does not contain*). |
| CTC | Route planning (schedule builder, re-plan) follows connections without direction of travel. A Green schedule against the arrows is planned, then refused by the Track Controller, and CTC shows the refusal. Authority is not extended automatically when an obstruction ahead clears beyond a movement's original end; the next departure or a dispatcher re-plan recalculates it (pre-existing design). A request the Track Controller rejected is not renewed; it is replaced when the train moves or the dispatcher re-plans. |
| Train Controller | Translate `TRAIN-nnn` labels with `TrainIdConvention` when consuming system messages. |

Full system integration is not complete until physical train release and movement exist.
