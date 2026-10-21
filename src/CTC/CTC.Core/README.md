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
  suggested speed), `AuthorityManager` (fixed-block authority) and `RouteManager`
  (shortest path by block count over connected blocks, used when re-planning).

A scheduled train is an ordered list of route blocks. The dispatcher times only some of
them (the time the train ENTERS the block); CTC routes the train between timed blocks
through the connected untimed ones, and checks each timed span is reachable within the
speed limits. When its departure time is reached CTC sends a `MovementSuggestionMessage`
(initial suggested speed/authority) and then a `MovementRequestMessage` to the Track
Controller; the train leaves the queue only after both sends succeed.

After release the dispatcher can change a train's destination and/or arrival time
(`UpdateDispatchedTrainPlanAsync`). CTC routes it from its inferred `CurrentBlockId` to
the new destination, recalculates speed and authority (the train's own block being
occupied does not stop its authority), and sends only a new `MovementSuggestionMessage`,
never another `MovementRequestMessage`. The train's state, including its active route,
changes only after that send succeeds. An arrival time that cannot be met gives the
maximum permitted speed and a warning; a limit is never exceeded.

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
on the SystemTime update at which its dwell has elapsed CTC sends the suggestion for the
next movement and the train is `EnRoute` again, or `Completed` at its final stop.

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
   Suggested speed and authority are still calculated only at dispatch and when the
   dispatcher changes the plan, not automatically as the train moves.
2. **No maintenance acknowledgement.** There is no Track Controller -> CTC
   maintenance status. `CtcBlockState.RequestedMaintenanceState == Closed` means
   "CTC successfully issued a Close request", not "Track Controller closed the block".
   It is set only after the send succeeds.
3. **Switch terminology.** Requirements use both Left/Right and Normal/Reverse. The
   shared `SwitchPosition` enum stays Normal/Reverse until the teams decide.
4. **System time.** Time is meant to come from a shared simulation clock that is
   not implemented yet. CTC only exposes `SetSystemTime` and has no timer of its own.
5. **Movement request vs. switch request.** The documented "movement request" could
   overlap with switch requests. Here `MovementRequestMessage` means releasing a
   train; switch changes use `SwitchPositionRequestMessage`.
