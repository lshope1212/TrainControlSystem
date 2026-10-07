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
  `ScheduledTrain`, `ScheduledBlockTime`, `DispatchQueueEntry`, `DispatchedTrainState`).
  Contract messages are never stored as domain state.
- `Services/CTCService` is the boundary between contracts and the domain model.
- `Dispatching/` holds the speed and authority rules used at dispatch:
  `TrainPerformance` (vehicle max speed), `SpeedPlanner` (schedule feasibility and
  initial suggested speed) and `AuthorityManager` (initial fixed-block authority).
  `RouteManager` is still a placeholder.

A scheduled train is an ordered list of route blocks, each with the time the train
ENTERS it. When its departure time is reached CTC sends a `MovementSuggestionMessage`
(initial suggested speed/authority) and then a `MovementRequestMessage` to the Track
Controller; the train leaves the queue only after both sends succeed.

All quantities are SI (meters, meters/second); the WPF layer converts to mph/feet.
UI-only state such as the selected line or block belongs in the view model.

## Known I/O gaps

1. **No train ID in block occupancy.** `BlockStatusMessage` reports only
   clear/occupied/unknown, so CTC cannot authoritatively determine
   `DispatchedTrainState.CurrentBlockId`. It is left empty rather than guessed.
   For the same reason suggested speed and authority are calculated only once, at
   dispatch, and are not recalculated as the train moves.
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
