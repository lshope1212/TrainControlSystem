# Live integration: Track Model, Track Controller and CTC

How the real modules exchange track information without their TestUIs, how to demonstrate
it, and what is still outstanding. Train Model physics and Train Controller normal mode are
not involved.

## Message flow

```text
                 TrackLayoutMessage (start-up, layout change, snapshot request; retried until delivered)
   Track Model ─────────────────────────────────────────────────────────────────────────────┐
     │  ▲                                                                                     │
     │  │ TrackModelCommandMessage (every 500 ms: speed, authority, switch, signal, crossing)  │
     │  │                                                                                     ▼
     │  └──────────────── Track Controller ── BlockStatusMessage (every 500 ms) ──────────►  CTC
     │                        ▲   ▲               + reportedAtUtc, + movement decision       │  │
     │ TrackModelBlockState-  │   └── RequestAcknowledgementMessage (per CTC request) ─────► │  │
     │ Message (on change,    │                                                              │  │
     │ and a 1 s heartbeat)   │◄──── MovementSuggestionMessage / Maintenance / Switch ───────┘  │
     └────────────────────────┘                                                                 │
     ▲                                                                                          │
     └──────────────────── TrackModelSnapshotRequestMessage (until CTC has a layout) ──────────┘

   Launcher (shared clock) ── SystemTimeMessage ──► CTC          CTC ── SimulationTimeControlMessage ──► Launcher
```

1. **Layout.** The Track Model publishes its layout to CTC when it starts, when its layout
   changes, and whenever a `TrackModelSnapshotRequestMessage` arrives. If CTC is offline, the
   Track Model retries on every heartbeat until CTC has that revision; a CTC that has no layout
   also asks for one every 3 s. The layout is not resent with occupancy updates or clock ticks.
   CTC reconciles it per line: a repeated equivalent layout changes nothing; a topology change
   rebuilds a line only if no train is released on it, otherwise that line keeps its topology
   and CTC reports why.
2. **Measured state.** The Track Model sends a `TrackModelBlockStateMessage` per block on every
   state change and, when nothing changed for 1 s, a heartbeat with a fresh observation time
   (wall clock, independent of simulation speed/pause). The Track Controller's 5 s watchdog
   therefore never marks a healthy track stale, and does mark it stale within 5 s of the Track
   Model stopping.
3. **Wayside status.** Every 500 ms the Track Controller sends CTC one `BlockStatusMessage` per
   block with the measured occupancy, signal, switch, crossing and confirmed maintenance, its
   evaluation time (`reportedAtUtc`) and, on a movement request's target block, the decision it
   actually commanded (`movement`). CTC ignores a report older than one it already applied, and
   treats a block whose reports stop for 3 s as Unknown (stale).
4. **Requests and answers.** CTC's movement, maintenance and switch requests carry a request ID.
   The Track Controller answers each with a `RequestAcknowledgementMessage`:
   `AcceptedForEvaluation` or `Rejected` with the reason. Transport delivery, acceptance, and the
   actual authorization (or confirmed closure / reported switch position) are four separate
   facts in CTC and in its UI.

## Identifiers

**Block IDs.** The Track Model layout is the authority. A block ID is an opaque string used
verbatim by every module: the Track Model, the Track Controller track maps (`Samples/*-track-map.json`)
and CTC. It must be unique across every line loaded at the same time, because block-level
messages carry no line ID. Section letters and block numbers are separate display fields
(`TrackBlockDefinition.Section` / `BlockNumber`), never parsed out of the ID, so the CTC
territory overview keeps working whatever the ID format is.

- Blue Line (current production data): IDs `1` to `15`, line ID `Blue`, in the Track Model and
  in the Track Controller's Blue Line deployment. CTC uses them as they are.
- When more than one line is loaded and numbers collide, the IDs are **qualified at their
  source**, line ID and number, for example `GREEN-12` or `RED-3`. The Track Model layout and
  the Track Controller track map for that line both use the qualified IDs. No module remaps
  another module's IDs. CTC rejects a layout whose block IDs collide (within the message or
  with a line it keeps), with an explanation, rather than guessing.
- The CTC TestUI's sample layout (`BLUE`/`A1`...`C15`, `RED-*`, `GREEN-*`) remains a standalone
  test fixture. If the real Track Model starts later, its layout replaces the sample lines,
  as long as no train is released on them.

**Train IDs.** The canonical train ID on the wire is CTC's three-digit dispatch ID (`000` to `999`),
assigned when the schedule is built. Module labels are presentation only and are translated at
that module's own boundary with `TrainControl.Common.Identity.TrainIdConvention`. For example, the
Train Controller fleet label `TRAIN-007` is canonical `007` (same number), and CTC's
`Train 007` is `007`. Anything else, such as the Track Model demonstration's `01`, is not
canonical and is rejected rather than guessed. The Train Controller itself was not changed;
it should use `TrainIdConvention` when it starts consuming system messages.

## Dispatch semantics (current)

- **CTC decides** when a train is released and proposes its route, speed and authority.
- **The Track Controller validates** every request against occupancy, switches, signals,
  crossings, maintenance and territory ownership. It is the final authority.
- **Placing a train in service** is the Train Model / Track Model integration's job.

At its departure time a train whose start block is reported Clear and open is released:
it leaves the queue with *Release = Awaiting placement*. Nothing is sent. The release intent
(`MovementRequestMessage`) has no receiver, and the Track Controller rejects it, so CTC does
not send it and never claims a physical release. When the start block reports occupied, CTC
attributes it to the train (*Detected on track (inferred)*) and sends its first complete
movement request: target = current block, entry = previous block, path = the route blocks the
authority covers, and authority measured from the target block's entry.

On each block change the old request is cancelled and a new one (new ID) re-expresses the same
fixed authority end from the new block. The end is set only when a movement starts (release,
departure from a stop, dispatcher re-plan). Renewals every 3 s of wall time keep a still-valid
request alive under the Track Controller's 10 s watchdog, with the same ID and limits. They never
add distance, continue while the simulation is paused, and stop with CTC, so the Track
Controller then withdraws the authority. A block ahead that becomes occupied, Unknown, stale or
closed shortens the request; clearing restores it only up to the original end.

## Demonstration (Blue Line, single territory)

Close any TestUIs first: the CTC TestUI hosts the Track Controller pipe and the Track Model
TestUI hosts the Track Controller, Train Model and CTC pipes.

1. `dotnet build TrainControlSystem.sln`
2. Start `TrainControlSystem.Launcher.Wpf`. From it, launch **Track Controller**, **CTC Office**
   and **Track Model**, in any order.
3. **Track Controller:** under *Controller setup*, choose **Blue Line** and click **Load setup**.
   This is the single-territory Blue Line deployment (`blue-line-all-pc.deployment.json`, PLC
   `wayside.plc`). Until it is loaded the controller runs its built-in 12-block demonstration
   territory, which rejects Track Model feedback for blocks 13 to 15. Leave it paused for the
   block-status part.
4. **CTC:** within a few seconds the territory overview shows the Blue Line (sections A, B, C;
   switch at 5; stations at 10 and 15). No *Send Sample Layout* is needed.
   *Inbound status* says `Received Track layout. Track layout: added Blue.`
5. **Track Model status:** the dashboard's delivery line reads `Track Controller: delivered … CTC:
   delivered`. The demonstration places train `01` in block 1, so CTC shows block 1 occupied
   and the rest clear. These are the Track Model's measurements relayed by the Track Controller.
6. **Change occupancy** with the development harness. It is send-only and hosts no pipe:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\Send-TrackModelTrainUpdate.ps1 -TrainId 01 -Block 2
   ```

   CTC shows block 2 occupied and block 1 clear within about a second. Repeat with other blocks
   (the Track Model rejects a move into an occupied or closed block). `-TrainId 01 -Remove` takes
   the train off the track.
7. **Freshness:** leave everything idle for more than 5 s. The Track Controller's blocks stay
   *Current* (heartbeat). Close the Track Model: within about 5 s the Track Controller shows
   *Feedback unavailable* and CTC shows every block Unknown. Reopen the Track Model and the
   statuses return.
8. **Close / reopen a block through CTC:** CTC → **Maintenance Mode: ON** → select a clear block
   (e.g. 12) → **Close Block**. The communication line shows the Track Controller's
   acknowledgement, e.g. `Track Controller accepted the request to close Block 12 (Block 12:
   Closed.)`. The block turns Closed only when the Track Controller's status confirms it. A
   closure of an occupied block is accepted but stays *Pending closure* until the block clears.
   A rejected request (for example, a block the deployment does not own) is withdrawn and its
   reason shown. **Reopen Block** works the same way.
9. *(Optional) movement:* resume the Launcher clock from CTC, queue a Blue Line schedule starting
   at block 1, and remove train 01 (`-TrainId 01 -Remove`). At the departure time CTC shows
   *Awaiting placement at 1*. Place the train with `-TrainId 000 -Block 1`. CTC then shows
   *Detected on track (inferred)*, and the *Track Controller* column shows the acknowledgement and
   the decision. While the Track Controller is paused: *Withheld: Track Controller is paused.*
   Resume it to see its real evaluation. Move the train with the harness (`-TrainId 000 -Block 2`, ...)
   to see the request re-targeted at each block.

Other Track Model inputs can be sent the same way as single-line envelopes, for example a track
circuit failure:
`{"messageType":"TrackModelFailureCommandMessage","payload":{"blockId":"7","brokenRail":false,"trackCircuitFailure":true,"powerFailure":false}}`
(CTC then shows block 7 Unknown).

## Automated coverage

`tests/Integration.Tests` (Windows; about 70 s) runs the production `TrackModelConnection`,
`WaysidePipeHost` with the Blue Line deployment runtime, and the CTC receiver, service and link
supervisor against each other over isolated pipe names. It covers: layout load and late-CTC
synchronization, occupancy and clearing end to end, measured (not requested) equipment and
faults, heartbeat freshness, Track Model and Track Controller disconnects, maintenance
acknowledgement and confirmation, the CTC TestUI workflow without real modules, complete movement
requests evaluated by the Track Controller, end-to-end authorization equal to what reached the
track, cross-territory rejection, re-targeted and re-routed requests, renewal without extension
and expiry when CTC stops, and no duplicate requests from repeated reports.

## Outstanding work and limitations

| Owner | Item |
| ----- | ---- |
| Train Model | **Physical release.** Receive CTC's release intent (`MovementRequestMessage`) and acknowledge it with a correlated response, then place the train. Until then CTC infers the train from its start block becoming occupied and never reports a confirmed release. |
| Train Model | **Real occupancy.** Send `TrackModelTrainUpdateMessage` to the Track Model as trains move (today only the harness does). Use canonical train IDs. |
| Track Controller | **Cross-territory handoff.** A request whose route leaves its owner's territory is rejected ("Movement cannot cross a territory boundary…"); CTC shows it as rejected. The single-territory Blue deployment avoids this. |
| Track Controller / Track Model | **CTC reservation feedback.** Switch protection requires `TrackModelBlockStateMessage.IsReservedByCtc`, which the Track Model does not report. With the real Track Model, switch requests are refused ("missing CTC reservation feedback"), and CTC shows that refusal. Decide which module owns reservations. |
| Track Controller | **Signalled routes.** With the bundled `wayside.plc`, a request through block 6 or 11 is withheld ("Waiting for proceed signal feedback at block 6"): the PLC needs a proceed aspect before granting, but signals are commanded only for blocks a granted authority covers. Routes that avoid signals are authorized end to end. |
| Track Controller | Starts in its 12-block demonstration territory; the Blue Line setup must be loaded by hand. Its track map is directional (1→15), so reverse-direction routes are rejected. |
| All | Red and Green production data: qualify block IDs at the source as described above before loading several lines together. |
| Train Controller | Translate `TRAIN-nnn` labels with `TrainIdConvention` when consuming system messages. |
| CTC | Authority is not extended automatically when an obstruction ahead clears beyond a movement's original end; the next departure or a dispatcher re-plan recalculates it (pre-existing design). A request the Track Controller rejected is not renewed; it is replaced when the train moves or the dispatcher re-plans. |
| Performance | One pipe connection per block status (Track Controller → CTC every 500 ms, Track Model heartbeat every 1 s) is fine for the Blue Line; for Green (150 blocks), batch them. The Track Controller already accepts a `TrackModelBlockStates` batch envelope. |

Full system integration is not complete until physical train release and movement exist.
