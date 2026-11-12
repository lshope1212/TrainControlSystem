# CTC.Core

Domain model and service for the Centralized Traffic Control office. No WPF and no
transport code.

```
Track Controller / Track Model
        |  shared contract messages (TrainControl.Contracts)
        v
    CTCService  -- maps into -->  CtcSystemState (Models/)
        |
        v  builds outgoing messages
  MaintenanceRequest / SwitchPositionRequest / MovementRequest
        |
        v  CloseBlockAsync sends via
  IMessageSender  (Interfaces/; implemented in CTC.Wpf by NamedPipeMessageSender)
```

CTC.Core decides *what* to send; the injected `IMessageSender` decides *how*.
Serialization, pipe names and framing live only in the WPF/infrastructure layer.
Inbound messages are received by `CtcNamedPipeReceiver` in CTC.Wpf, which simply
calls the matching `ICTCService` method.

`ICTCService.StateChanged` is raised after every state change (plain .NET event, no
WPF types) so the UI can refresh when messages arrive from other modules.

- `Models/` holds CTC's own state (`CtcSystemState`, `CtcLineState`, `CtcBlockState`,
  `ScheduledTrain`, `ScheduledRouteBlock`, `DispatchQueueEntry`, `DispatchedTrainState`).
  Contract messages are never stored as domain state.
- `Services/CTCService` is the boundary between contracts and the domain model.
- `Dispatching/` holds the routing, speed and authority rules:
  `TrainPerformance` (vehicle max speed), `SpeedPlanner` (schedule feasibility and
  suggested speed), `AuthorityManager` (fixed-block authority), `RouteManager`
  (the shortest legal route, used when re-planning; see `Routing/`),
  `MovementRequestPlanner` (the request a train should hold now) and
  `MovementRequestCoordinator` (the only code that sends, renews or cancels movement
  requests).
- `Routing/` holds the line topology rules shared by every route CTC builds: `LineTopology`
  (direction of travel, switch stem/legs, no reversing), `RoutePlanner` (shortest legal route over
  routing states, so a route may revisit a block from a new approach) and `MovementSegmentation`
  (bounded movement-authority segments for routes that revisit blocks or set a switch both ways).
- `Services/TrackLayoutReconciler` applies a received layout line by line.

A scheduled train is an ordered list of route blocks. The dispatcher times only some of
them (the time the train ENTERS the block); CTC routes the train between timed blocks
through the connected untimed ones, and checks each timed span is reachable within the
speed limits.

**Release.** When its departure time is reached and its start block is reported Clear and
open, the train is released: it leaves the queue for `DispatchedTrains` with
`ReleaseStatus = AwaitingPlacement`. Nothing is sent then. The release intent
(`MovementRequestMessage`) has no receiver yet (placing a train in service is the Train
Model's job, and the Track Controller rejects that message), so CTC never claims a
physical release. When the start block reports occupied, CTC attributes it to the train
(`DetectedOnTrack`, inferred) and issues its first movement request.

**Movement requests** (`MovementRequestCoordinator`). Every request is a complete
`MovementSuggestionMessage`: request ID, train, target block = the train's current block,
entry block = the block it came from, the route blocks ahead that the authority covers,
the suggested speed and a fixed authority measured from the target block's entry. When
the train enters its next block, the old request is cancelled (`active: false`, same ID)
and a new one with a new ID re-expresses the SAME authority end from the new block; the
original allowance is never restarted. The authority end is set only when a movement
starts (release, departure from a stop, a dispatcher re-plan); a block ahead becoming
occupied, Unknown, stale or closed shortens the request (new ID), and clearing restores
it only up to that end. Still-valid requests are renewed on wall time (same ID, same
limits, every 3 s) so the Track Controller's 10 s watchdog keeps them alive; renewals
never add distance, and stop when CTC stops. The Track Controller's answers are kept
apart: transport delivery, `RequestAcknowledgementMessage` (accepted for evaluation or
rejected, correlated by request ID; stale answers are ignored) and the actual
authorization it reports on the target block's `BlockStatusMessage.Movement`.

After release the dispatcher can change a train's destination and/or arrival time
(`UpdateDispatchedTrainPlanAsync`). CTC routes it from its inferred `CurrentBlockId` to
the new destination, recalculates speed and authority (the train's own block being
occupied does not stop its authority), and, for a train on the track, replaces its
movement request. The train's state, including its active route, changes only after that
send succeeds. An arrival time that cannot be met gives the maximum permitted speed and a
warning; a limit is never exceeded.

**Conservative safety.** A block not reported Clear (Occupied, Unknown, never reported,
or a live status not refreshed for 3 s) is unsafe for authority, as is a block closed or
requested closed. The Track Controller remains the final authority.

**Track layout.** `ApplyTrackLayout` reconciles per line: an equivalent repeated layout
changes nothing (status, schedules, queue and train tracking survive); attribute changes
apply in place; a topology change rebuilds the line only when it has no released trains,
and is otherwise refused for that line. Block IDs are used verbatim and must be unique
across all lines (see `docs/integration/LIVE_INTEGRATION_GUIDE.md`).

### Dwell times

Every timed stop has a dwell time (`ScheduledRouteBlock.DwellTime`,
`MovementPlanStop.DwellTime`). `Scheduling/DwellTimePolicy` is the only place the default
is decided: 45 s when the track layout gives the block a station name, otherwise 0 s.
The Schedule Builder fills the default when a block is given a time for a train (and keeps
manual edits); the CSV import accepts an optional `DwellTimeSeconds` column (missing or
blank = default). Dwell never moves a stop's arrival: `Departure = Arrival + Dwell`, and the
speed of the next movement is planned over `NextArrival - Departure`. A dwell that leaves
no time for the next movement is rejected.

After release, `DispatchedTrainState.Stops` is the Active Movement Plan
(`Dispatching/MovementPlanner`). `UpdateMovementPlanStopAsync` edits any remaining stop's
destination, arrival or dwell; a dwell change only recalculates the planned timing. At
run time a train entering its current stop becomes `Dwelling` (or `Arrived` with no dwell);
on the SystemTime update at which its dwell has elapsed CTC issues the movement request for
the next movement and the train is `EnRoute` again, or `Completed` at its final stop (its
request is then cancelled).

### Train location: sequential occupancy

`BlockStatusMessage` reports only that something occupies a block, never which train.
`Tracking/TrainLocationTracker` infers identity the way a real CTC follows trains
through track circuits:

1. At release, `DispatchedTrainState.CurrentBlockId` is the route start block and
   `ActiveRouteBlockIds` is the scheduled route (`ActiveRouteIndex` 0).
2. The start block being reported occupied establishes the chain
   (`CurrentBlockOccupancyObserved`).
3. When a block goes from not-occupied to Occupied and it is the NEXT active-route block
   of exactly one dispatched train on that line, that train advances into it
   (`TrainLocationUpdated`). The old block may still be occupied (the train spans both)
   or may already have cleared.
4. A block clearing never moves a train; repeated Occupied snapshots, Unknown, blocks
   further ahead (no skipping) and unexplained occupancy move nothing. If more than one
   train could explain the occupancy, CTC does not guess (`TrainLocationAmbiguous`).
5. A successful dispatcher re-plan makes the new route the active route.

Schedule times are not used to identify trains.

All quantities are SI (meters, meters/second); the WPF layer converts to mph/feet.
UI-only state such as the selected line or block belongs in the view model.

## Known I/O gaps

1. **No train ID in block occupancy (by design).** `BlockStatusMessage` reports only
   clear/occupied/unknown. CTC infers each train's current block from sequential
   occupancy along its active route (see above); no train-position message is expected.
   The suggested speed and the authority END are calculated only when a movement starts
   (release, departure from a stop, dispatcher re-plan); moving block to block only
   re-expresses that end from the new block.
2. **Maintenance acknowledgement vs. confirmation.** `CtcBlockState.RequestedMaintenanceState == Closed`
   means "CTC successfully issued a Close request". The Track Controller's
   `RequestAcknowledgementMessage` says whether it accepted the request (a rejected request
   is withdrawn); only `BlockStatusMessage.Maintenance` confirms the block's actual state.
6. **Physical release.** No module confirms placing a train in service yet. A released
   train stays `AwaitingPlacement` until its start block reports occupied; see the
   integration guide for the outstanding Train Model work.
3. **Switch terminology.** Requirements use both Left/Right and Normal/Reverse. The
   shared `SwitchPosition` enum stays Normal/Reverse until the teams decide.
4. **System time.** Time comes from the shared simulation clock hosted by the
   Launcher (root README, *Simulation timing*). CTC.Core only exposes
   `SetSystemTimeAsync` and has no timer of its own. The CTC window's clock controls
   (in CTC.Wpf, not here) send `SimulationTimeControlMessage` requests to that clock,
   and its authoritative `SystemTimeMessage` comes back through the receiver. Only
   `SystemTime` reaches CTC.Core; speed and paused state are display concerns.
5. **Movement request vs. switch request.** The documented "movement request" could
   overlap with switch requests. Here `MovementRequestMessage` means releasing a
   train; switch changes use `SwitchPositionRequestMessage`.
